// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-31

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// Query-level tests for <see cref="NotificationRepository"/>, run against the EF Core in-memory
/// provider (a database provider, not a mocking library) so the real WHERE clauses are exercised.
///
/// <para>
/// ISOLATION IS PROVEN HERE, not in <c>BillingAlertServiceTests</c>, which stubs this repository
/// away. Two predicates carry it and both are tested on every read and write below. The TENANT
/// predicate is the standing rule for FinanciaDbContext — there is no EF global query filter, so
/// it is written by hand or not at all. The USER predicate is what makes notifications per-user at
/// all: without it this would be a second, redundant copy of the tenant-wide alerts inbox, and one
/// analyst could read or clear another's notifications.
/// </para>
/// </summary>
public sealed class NotificationRepositoryTests
{
    private const string Tenant = "tenant-1";
    private const string Other  = "tenant-2";
    private const string Type   = "BILLING_ANALYSIS_QUOTA_80";

    private static readonly Guid UserA = Guid.NewGuid();
    private static readonly Guid UserB = Guid.NewGuid();

    private static readonly DateTime PeriodStart = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PeriodEnd   = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static FinanciaDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"notifications-{Guid.NewGuid()}")
            .Options;
        return new FinanciaDbContext(options);
    }

    private static NotificationRepository NewRepository(FinanciaDbContext ctx)
        => new(ctx, NullLogger<NotificationRepository>.Instance);

    private static Notification BillingNotification(
        string    tenantId,
        Guid      userId,
        DateTime  createdAt,
        string    type   = Type,
        bool      isRead = false) => new()
    {
        NotificationId = Guid.NewGuid(),
        TenantId       = tenantId,
        UserId         = userId,
        Type           = type,
        Title          = "Consumo del plan al 80%",
        Body           = "Usaste el 82% de los análisis de tu plan (100 análisis) este mes.",
        SourceType     = "Alert",
        SourceId       = Guid.NewGuid(),
        IsRead         = isRead,
        CreatedAt      = createdAt
    };

    private static async Task SeedAsync(FinanciaDbContext ctx, params Notification[] notifications)
    {
        ctx.Notifications.AddRange(notifications);
        ctx.ChangeTracker.DetectChanges();
        await ctx.SaveChangesAsync(CancellationToken.None);
        ctx.ChangeTracker.Clear();
    }

    // ── GetPageForUserAsync / CountForUserAsync ─────────────────────────────────

    private static NotificationQuery Query(int limit = 50) => new() { Limit = limit };

    /// <summary>
    /// A page holds only the caller's own notifications: not another user's within the same tenant,
    /// and not the same user's id under a different tenant. The count is scoped the same way.
    /// </summary>
    [Fact]
    public async Task GetPageForUserAsync_ReturnsOnlyThatUsersNotifications()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx,
            BillingNotification(Tenant, UserA, PeriodStart.AddDays(1)),
            BillingNotification(Tenant, UserB, PeriodStart.AddDays(2)),
            BillingNotification(Other,  UserA, PeriodStart.AddDays(3)));

        var repo = NewRepository(ctx);
        var page = await repo.GetPageForUserAsync(Tenant, UserA, Query(), CancellationToken.None);
        var count = await repo.CountForUserAsync(Tenant, UserA, Query(), CancellationToken.None);

        var only = Assert.Single(page.Items);
        Assert.Equal(UserA,  only.UserId);
        Assert.Equal(Tenant, only.TenantId);
        Assert.False(page.HasMore);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task GetPageForUserAsync_OrdersByCreatedAtDescending()
    {
        await using var ctx = NewContext();
        var oldest = PeriodStart.AddDays(1);
        var middle = PeriodStart.AddDays(2);
        var newest = PeriodStart.AddDays(3);

        await SeedAsync(ctx,
            BillingNotification(Tenant, UserA, middle),
            BillingNotification(Tenant, UserA, oldest),
            BillingNotification(Tenant, UserA, newest));

        var page = await NewRepository(ctx).GetPageForUserAsync(Tenant, UserA, Query(), CancellationToken.None);

        Assert.Equal([newest, middle, oldest], page.Items.Select(n => n.CreatedAt));
    }

    [Fact]
    public async Task GetPageForUserAsync_HonoursLimitAndReportsMore()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, Enumerable.Range(0, 5)
            .Select(i => BillingNotification(Tenant, UserA, PeriodStart.AddHours(i)))
            .ToArray());

        var repo  = NewRepository(ctx);
        var page  = await repo.GetPageForUserAsync(Tenant, UserA, Query(limit: 3), CancellationToken.None);
        var exact = await repo.GetPageForUserAsync(Tenant, UserA, Query(limit: 5), CancellationToken.None);

        Assert.Equal(3, page.Items.Count);
        Assert.True(page.HasMore);
        Assert.Equal(5, exact.Items.Count);
        Assert.False(exact.HasMore);
    }

    /// <summary>
    /// Walking every page with the cursor of the previous one visits each notification exactly once,
    /// in order — including a block of rows that share ONE CreatedAt (a fan-out writes one "now") and
    /// straddle a page edge. The tie-break on NotificationId is what keeps those from being skipped
    /// or repeated.
    /// </summary>
    [Fact]
    public async Task GetPageForUserAsync_CursorRoundTrip_VisitsEveryRowOnce_EvenWithEqualCreatedAt()
    {
        await using var ctx = NewContext();
        var shared = PeriodStart.AddDays(5);
        var rows = Enumerable.Range(0, 4).Select(i => BillingNotification(Tenant, UserA, PeriodStart.AddDays(i)))
            .Concat(Enumerable.Range(0, 5).Select(_ => BillingNotification(Tenant, UserA, shared)))
            .Concat(Enumerable.Range(0, 2).Select(i => BillingNotification(Tenant, UserA, PeriodStart.AddDays(10 + i))))
            .ToArray();
        await SeedAsync(ctx, rows);

        var repo = NewRepository(ctx);
        var seen = new List<Notification>();
        NotificationQuery query = Query(limit: 4);
        var pages = 0;

        while (true)
        {
            var page = await repo.GetPageForUserAsync(Tenant, UserA, query, CancellationToken.None);
            seen.AddRange(page.Items);
            pages++;
            if (!page.HasMore) break;

            // Through the real wire form, exactly as the controller hands it to the Portal and back.
            var last = page.Items[^1];
            var cursor = NotificationCursor.Encode(last.CreatedAt, last.NotificationId);
            Assert.True(NotificationCursor.TryDecode(cursor, out var c, out var id));
            query = query with { AfterCreatedAt = c, AfterNotificationId = id };
        }

        Assert.Equal(3, pages);
        Assert.Equal(rows.Length, seen.Count);
        Assert.Equal(rows.Length, seen.Select(n => n.NotificationId).Distinct().Count());

        var expected = rows
            .OrderByDescending(n => n.CreatedAt)
            .ThenByDescending(n => n.NotificationId)
            .Select(n => n.NotificationId);
        Assert.Equal(expected, seen.Select(n => n.NotificationId));
    }

    [Fact]
    public async Task GetPageForUserAsync_LastPage_HasNoMore()
    {
        await using var ctx = NewContext();
        var rows = Enumerable.Range(0, 3)
            .Select(i => BillingNotification(Tenant, UserA, PeriodStart.AddHours(i)))
            .ToArray();
        await SeedAsync(ctx, rows);

        var second = rows.OrderByDescending(n => n.CreatedAt).ElementAt(1);
        var page = await NewRepository(ctx).GetPageForUserAsync(
            Tenant, UserA,
            Query(limit: 5) with { AfterCreatedAt = second.CreatedAt, AfterNotificationId = second.NotificationId },
            CancellationToken.None);

        var only = Assert.Single(page.Items);
        Assert.Equal(PeriodStart, only.CreatedAt);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task Filter_UnreadOnly()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx,
            BillingNotification(Tenant, UserA, PeriodStart.AddDays(1), isRead: true),
            BillingNotification(Tenant, UserA, PeriodStart.AddDays(2)),
            BillingNotification(Tenant, UserA, PeriodStart.AddDays(3)));

        var (page, count) = await PageAndCountAsync(ctx, Query() with { UnreadOnly = true });

        Assert.All(page.Items, n => Assert.False(n.IsRead));
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Filter_Type()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx,
            BillingNotification(Tenant, UserA, PeriodStart.AddDays(1)),
            BillingNotification(Tenant, UserA, PeriodStart.AddDays(2), type: "BILLING_CHAT_QUOTA_100"));

        var (page, count) = await PageAndCountAsync(ctx, Query() with { Type = "BILLING_CHAT_QUOTA_100" });

        Assert.Equal("BILLING_CHAT_QUOTA_100", Assert.Single(page.Items).Type);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Filter_SourceType_AndSourceId()
    {
        await using var ctx = NewContext();
        var alert    = BillingNotification(Tenant, UserA, PeriodStart.AddDays(1));
        var other    = BillingNotification(Tenant, UserA, PeriodStart.AddDays(2));
        var analysis = AnalysisNotification(Tenant, UserA, PeriodStart.AddDays(3), Guid.NewGuid());
        await SeedAsync(ctx, alert, other, analysis);

        var (byType, typeCount) = await PageAndCountAsync(ctx, Query() with { SourceType = "Alert" });
        var (byId, idCount) = await PageAndCountAsync(
            ctx, Query() with { SourceType = "Alert", SourceId = alert.SourceId });

        Assert.Equal(2, byType.Items.Count);
        Assert.Equal(2, typeCount);
        Assert.Equal(alert.NotificationId, Assert.Single(byId.Items).NotificationId);
        Assert.Equal(1, idCount);
    }

    [Fact]
    public async Task Filter_FromTo_IsHalfOpen()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx,
            BillingNotification(Tenant, UserA, PeriodStart.AddDays(-1)),
            BillingNotification(Tenant, UserA, PeriodStart),                  // == from → included
            BillingNotification(Tenant, UserA, PeriodStart.AddDays(10)),
            BillingNotification(Tenant, UserA, PeriodEnd));                   // == to   → excluded

        var (page, count) = await PageAndCountAsync(ctx, Query() with { FromUtc = PeriodStart, ToUtc = PeriodEnd });

        Assert.Equal([PeriodStart.AddDays(10), PeriodStart], page.Items.Select(n => n.CreatedAt));
        Assert.Equal(2, count);
    }

    /// <summary>
    /// entityId reaches the PyME through the source: an "Analysis" row via its ResultId → Analyses,
    /// an "AnalysisRun" row via its DocumentId. Another PyME's rows, billing ("Alert") rows, rows with
    /// no source, and a same-EntityId analysis under ANOTHER tenant never match.
    /// </summary>
    [Fact]
    public async Task Filter_EntityId_ResolvesAnalysisAndRunSources_AndNothingElse()
    {
        await using var ctx = NewContext();
        var entity      = Guid.NewGuid();
        var otherEntity = Guid.NewGuid();

        var (ourResult, _)       = SeedAnalysis(ctx, Tenant, entity);
        var (otherResult, _)     = SeedAnalysis(ctx, Tenant, otherEntity);
        var (foreignResult, _)   = SeedAnalysis(ctx, Other, entity);   // same EntityId, other tenant
        var ourDocument          = SeedDocument(ctx, Tenant, entity);
        var otherDocument        = SeedDocument(ctx, Tenant, otherEntity);

        var completed   = AnalysisNotification(Tenant, UserA, PeriodStart.AddDays(1), ourResult);
        var failedRun   = RunNotification(Tenant, UserA, PeriodStart.AddDays(2), ourDocument);
        var otherPyme   = AnalysisNotification(Tenant, UserA, PeriodStart.AddDays(3), otherResult);
        var otherRun    = RunNotification(Tenant, UserA, PeriodStart.AddDays(4), otherDocument);
        var foreign     = AnalysisNotification(Tenant, UserA, PeriodStart.AddDays(5), foreignResult);
        var billing     = BillingNotification(Tenant, UserA, PeriodStart.AddDays(6));
        var noSource    = BillingNotification(Tenant, UserA, PeriodStart.AddDays(7));
        noSource.SourceType = null;
        noSource.SourceId   = null;
        await SeedAsync(ctx, completed, failedRun, otherPyme, otherRun, foreign, billing, noSource);

        var (page, count) = await PageAndCountAsync(ctx, Query() with { EntityId = entity });

        Assert.Equal(
            [failedRun.NotificationId, completed.NotificationId],
            page.Items.Select(n => n.NotificationId));
        Assert.Equal(2, count);
    }

    /// <summary>totalCount describes the whole filtered set: the cursor moves the page, not the count.</summary>
    [Fact]
    public async Task CountForUserAsync_IgnoresCursorAndLimit()
    {
        await using var ctx = NewContext();
        var rows = Enumerable.Range(0, 6)
            .Select(i => BillingNotification(Tenant, UserA, PeriodStart.AddHours(i)))
            .ToArray();
        await SeedAsync(ctx, rows);

        var newest = rows[^1];
        var count = await NewRepository(ctx).CountForUserAsync(
            Tenant, UserA,
            Query(limit: 2) with { AfterCreatedAt = newest.CreatedAt, AfterNotificationId = newest.NotificationId },
            CancellationToken.None);

        Assert.Equal(6, count);
    }

    /// <summary>
    /// The repository's OWN page query, rendered for SQL Server (ToQueryString builds the SQL without a
    /// connection). Pins that every part runs server-side: the (TenantId, UserId) scope, the keyset on
    /// SQL Server's uniqueidentifier ordering (the same one the ORDER BY uses), TOP (limit + 1), and the
    /// entityId filter as EXISTS semi-joins — each repeating the TenantId — rather than a JOIN or a
    /// client-side evaluation that would throw at runtime.
    /// </summary>
    [Fact]
    public void BuildPageQuery_TranslatesToSqlServer_KeysetAndEntityExists()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseSqlServer("Server=unused;Database=unused;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;
        using var ctx = new FinanciaDbContext(options);

        var sql = NewRepository(ctx).BuildPageQuery(Tenant, UserA, Query(limit: 50) with
        {
            AfterCreatedAt      = new DateTime(2026, 11, 1, 12, 0, 0),
            AfterNotificationId = Guid.NewGuid(),
            UnreadOnly          = true,
            EntityId            = Guid.NewGuid(),
        }).ToQueryString();

        Assert.Contains("TOP(@", sql);
        Assert.Contains("[n].[TenantId] = @", sql);
        Assert.Contains("[n].[UserId] = @", sql);
        Assert.Contains("[n].[NotificationId] < @", sql);
        Assert.Contains("ORDER BY [n].[CreatedAt] DESC, [n].[NotificationId] DESC", sql);
        Assert.Contains("EXISTS", sql);
        Assert.Contains("[AnalysisResults]", sql);
        Assert.Contains("[Analyses]", sql);
        Assert.Contains("[Documents]", sql);
        Assert.DoesNotContain(" JOIN ", sql);
    }

    private static async Task<(NotificationPage Page, int Count)> PageAndCountAsync(
        FinanciaDbContext ctx, NotificationQuery query)
    {
        var repo = NewRepository(ctx);
        var page = await repo.GetPageForUserAsync(Tenant, UserA, query, CancellationToken.None);
        var count = await repo.CountForUserAsync(Tenant, UserA, query, CancellationToken.None);
        return (page, count);
    }

    private static Notification AnalysisNotification(string tenantId, Guid userId, DateTime createdAt, Guid resultId)
    {
        var n = BillingNotification(tenantId, userId, createdAt, type: "ANALYSIS_COMPLETED");
        n.SourceType = "Analysis";
        n.SourceId   = resultId;
        n.Title      = "Análisis listo";
        return n;
    }

    private static Notification RunNotification(string tenantId, Guid userId, DateTime createdAt, Guid documentId)
    {
        var n = BillingNotification(tenantId, userId, createdAt, type: "ANALYSIS_FAILED");
        n.SourceType = "AnalysisRun";
        n.SourceId   = documentId;
        n.Title      = "No se pudo completar el análisis";
        return n;
    }

    /// <summary>Adds an Analysis + its AnalysisResult (saved by the next SeedAsync); returns the ResultId.</summary>
    private static (Guid ResultId, Guid AnalysisId) SeedAnalysis(FinanciaDbContext ctx, string tenantId, Guid entityId)
    {
        var analysisId = Guid.NewGuid();
        var resultId   = Guid.NewGuid();

        ctx.Analyses.Add(new Analysis
        {
            AnalysisId = analysisId,
            TenantId   = tenantId,
            EntityId   = entityId,
            DocumentId = Guid.NewGuid(),
            Status     = "Approved",
            FiscalYear = 2025
        });
        ctx.AnalysisResults.Add(new AnalysisResult
        {
            ResultId         = resultId,
            AnalysisId       = analysisId,
            TenantId         = tenantId,
            AiInputHash      = new string('a', 64),
            AiOutputHash     = new string('b', 64),
            ValidationStatus = "Passed",
            EnrichmentStatus = "Complete"
        });

        return (resultId, analysisId);
    }

    /// <summary>Adds a Document of <paramref name="entityId"/> (saved by the next SeedAsync); returns its id.</summary>
    private static Guid SeedDocument(FinanciaDbContext ctx, string tenantId, Guid entityId)
    {
        var documentId = Guid.NewGuid();
        ctx.Documents.Add(new Document
        {
            DocumentId       = documentId,
            TenantId         = tenantId,
            EntityId         = entityId,
            UploadedBy       = Guid.NewGuid(),
            OriginalFileName = "balance.pdf",
            StoredFileName   = "stored.pdf",
            StoragePath      = "/tmp/stored.pdf",
            MimeType         = "application/pdf",
            FileHashSHA256   = new string('c', 64),
            DocumentType     = "Balance",
            ProcessingStatus = "Failed",
            UploadedAt       = PeriodStart
        });
        return documentId;
    }

    // ── GetUnreadCountForUserAsync ──────────────────────────────────────────────

    [Fact]
    public async Task GetUnreadCountForUserAsync_CountsOnlyThatUsersUnread()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx,
            BillingNotification(Tenant, UserA, PeriodStart.AddDays(1)),
            BillingNotification(Tenant, UserA, PeriodStart.AddDays(2), isRead: true),
            BillingNotification(Tenant, UserB, PeriodStart.AddDays(3)),
            BillingNotification(Other,  UserA, PeriodStart.AddDays(4)));

        var count = await NewRepository(ctx)
            .GetUnreadCountForUserAsync(Tenant, UserA, CancellationToken.None);

        Assert.Equal(1, count);
    }

    // ── MarkAsReadAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task MarkAsReadAsync_SetsIsReadAndReadAt()
    {
        await using var ctx = NewContext();
        var notification = BillingNotification(Tenant, UserA, PeriodStart.AddDays(1));
        await SeedAsync(ctx, notification);

        var success = await NewRepository(ctx)
            .MarkAsReadAsync(notification.NotificationId, Tenant, UserA, CancellationToken.None);

        Assert.True(success);

        var stored = await ctx.Notifications.AsNoTracking()
            .SingleAsync(n => n.NotificationId == notification.NotificationId);
        Assert.True(stored.IsRead);
        Assert.NotNull(stored.ReadAt);
    }

    /// <summary>
    /// THE per-user guarantee. Another user of the SAME tenant cannot mark this notification read:
    /// the row is not found, so it returns false (which the controller reports as 404) and the
    /// notification is left untouched.
    /// </summary>
    [Fact]
    public async Task MarkAsReadAsync_ForAnotherUsersNotification_DoesNothing()
    {
        await using var ctx = NewContext();
        var notification = BillingNotification(Tenant, UserA, PeriodStart.AddDays(1));
        await SeedAsync(ctx, notification);

        var success = await NewRepository(ctx)
            .MarkAsReadAsync(notification.NotificationId, Tenant, UserB, CancellationToken.None);

        Assert.False(success);

        var stored = await ctx.Notifications.AsNoTracking()
            .SingleAsync(n => n.NotificationId == notification.NotificationId);
        Assert.False(stored.IsRead);
        Assert.Null(stored.ReadAt);
    }

    [Fact]
    public async Task MarkAsReadAsync_UnderAnotherTenant_DoesNothing()
    {
        await using var ctx = NewContext();
        var notification = BillingNotification(Tenant, UserA, PeriodStart.AddDays(1));
        await SeedAsync(ctx, notification);

        var success = await NewRepository(ctx)
            .MarkAsReadAsync(notification.NotificationId, Other, UserA, CancellationToken.None);

        Assert.False(success);
        Assert.False((await ctx.Notifications.AsNoTracking()
            .SingleAsync(n => n.NotificationId == notification.NotificationId)).IsRead);
    }

    // ── MarkAsUnreadAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task MarkAsUnreadAsync_ClearsReadState_OfOwnNotification()
    {
        await using var ctx = NewContext();
        var n = BillingNotification(Tenant, UserA, PeriodStart.AddDays(1), isRead: true);
        n.ReadAt = PeriodStart.AddDays(2);
        await SeedAsync(ctx, n);

        var ok = await NewRepository(ctx).MarkAsUnreadAsync(n.NotificationId, Tenant, UserA, CancellationToken.None);

        Assert.True(ok);
        var stored = await ctx.Notifications.AsNoTracking().SingleAsync(x => x.NotificationId == n.NotificationId);
        Assert.False(stored.IsRead);
        Assert.Null(stored.ReadAt);
    }

    [Fact]
    public async Task MarkAsUnreadAsync_IsIdempotent()
    {
        await using var ctx = NewContext();
        var n = BillingNotification(Tenant, UserA, PeriodStart.AddDays(1));
        await SeedAsync(ctx, n);

        Assert.True(await NewRepository(ctx).MarkAsUnreadAsync(n.NotificationId, Tenant, UserA, CancellationToken.None));
    }

    /// <summary>Another user's id, or the same id under another tenant, is "not found" and untouched.</summary>
    [Fact]
    public async Task MarkAsUnreadAsync_OtherUserOrTenant_NotFound_AndUnchanged()
    {
        await using var ctx = NewContext();
        var theirs = BillingNotification(Tenant, UserB, PeriodStart.AddDays(1), isRead: true);
        theirs.ReadAt = PeriodStart.AddDays(2);
        await SeedAsync(ctx, theirs);

        var repo = NewRepository(ctx);
        Assert.False(await repo.MarkAsUnreadAsync(theirs.NotificationId, Tenant, UserA, CancellationToken.None));
        Assert.False(await repo.MarkAsUnreadAsync(theirs.NotificationId, Other,  UserB, CancellationToken.None));

        var stored = await ctx.Notifications.AsNoTracking().SingleAsync(x => x.NotificationId == theirs.NotificationId);
        Assert.True(stored.IsRead);
        Assert.Equal(PeriodStart.AddDays(2), stored.ReadAt);
    }

    // ── MarkManyAsReadAsync ─────────────────────────────────────────────────────

    /// <summary>
    /// Only the caller's own UNREAD rows among the ids flip and are counted: another user's id is not
    /// updated, an already-read row keeps its ReadAt, an unknown id and a duplicate count nothing.
    /// </summary>
    [Fact]
    public async Task MarkManyAsReadAsync_FlipsOnlyOwnUnread()
    {
        await using var ctx = NewContext();
        var mine1   = BillingNotification(Tenant, UserA, PeriodStart.AddDays(1));
        var mine2   = BillingNotification(Tenant, UserA, PeriodStart.AddDays(2));
        var already = BillingNotification(Tenant, UserA, PeriodStart.AddDays(3), isRead: true);
        already.ReadAt = PeriodStart.AddDays(4);
        var theirs  = BillingNotification(Tenant, UserB, PeriodStart.AddDays(5));
        var foreign = BillingNotification(Other,  UserA, PeriodStart.AddDays(6));
        var untouched = BillingNotification(Tenant, UserA, PeriodStart.AddDays(7));
        await SeedAsync(ctx, mine1, mine2, already, theirs, foreign, untouched);

        var updated = await NewRepository(ctx).MarkManyAsReadAsync(
            Tenant, UserA,
            [mine1.NotificationId, mine2.NotificationId, mine2.NotificationId, already.NotificationId,
             theirs.NotificationId, foreign.NotificationId, Guid.NewGuid()],
            CancellationToken.None);

        Assert.Equal(2, updated);

        var stored = await ctx.Notifications.AsNoTracking().ToDictionaryAsync(x => x.NotificationId);
        Assert.True(stored[mine1.NotificationId].IsRead);
        Assert.NotNull(stored[mine1.NotificationId].ReadAt);
        Assert.True(stored[mine2.NotificationId].IsRead);
        Assert.Equal(PeriodStart.AddDays(4), stored[already.NotificationId].ReadAt);
        Assert.False(stored[theirs.NotificationId].IsRead);
        Assert.False(stored[foreign.NotificationId].IsRead);
        Assert.False(stored[untouched.NotificationId].IsRead);
    }

    [Fact]
    public async Task MarkManyAsReadAsync_EmptyList_UpdatesNothing()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, BillingNotification(Tenant, UserA, PeriodStart.AddDays(1)));

        Assert.Equal(0, await NewRepository(ctx).MarkManyAsReadAsync(Tenant, UserA, [], CancellationToken.None));
    }

    // ── MarkAllAsReadForUserAsync ───────────────────────────────────────────────

    /// <summary>
    /// "Mark all read" means all of MINE. A sweep by one analyst must not clear their colleague's
    /// badge — the clearest behavioural difference from the tenant-wide alerts inbox.
    /// </summary>
    [Fact]
    public async Task MarkAllAsReadForUserAsync_LeavesOtherUsersUntouched()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx,
            BillingNotification(Tenant, UserA, PeriodStart.AddDays(1)),
            BillingNotification(Tenant, UserA, PeriodStart.AddDays(2)),
            BillingNotification(Tenant, UserB, PeriodStart.AddDays(3)),
            BillingNotification(Other,  UserA, PeriodStart.AddDays(4)));

        var updated = await NewRepository(ctx)
            .MarkAllAsReadForUserAsync(Tenant, UserA, CancellationToken.None);

        Assert.Equal(2, updated);

        var untouched = await ctx.Notifications.AsNoTracking()
            .Where(n => n.UserId == UserB || n.TenantId == Other)
            .ToListAsync();
        Assert.All(untouched, n => Assert.False(n.IsRead));
    }

    [Fact]
    public async Task MarkAllAsReadForUserAsync_WithNothingUnread_ReturnsZero()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, BillingNotification(Tenant, UserA, PeriodStart.AddDays(1), isRead: true));

        var updated = await NewRepository(ctx)
            .MarkAllAsReadForUserAsync(Tenant, UserA, CancellationToken.None);

        Assert.Equal(0, updated);
    }

    // ── ExistsByTypeForUserInPeriodAsync ────────────────────────────────────────

    [Fact]
    public async Task ExistsByTypeForUserInPeriodAsync_WhenPresent_ReturnsTrue()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, BillingNotification(Tenant, UserA, PeriodStart.AddDays(5)));

        Assert.True(await NewRepository(ctx).ExistsByTypeForUserInPeriodAsync(
            Tenant, UserA, Type, PeriodStart, PeriodEnd, CancellationToken.None));
    }

    /// <summary>
    /// The dedup question is per USER. A notification already delivered to a colleague must not
    /// make this user look "already notified", or a tenant's second analyst would never be told.
    /// </summary>
    [Fact]
    public async Task ExistsByTypeForUserInPeriodAsync_ForAnotherUser_ReturnsFalse()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, BillingNotification(Tenant, UserA, PeriodStart.AddDays(5)));

        Assert.False(await NewRepository(ctx).ExistsByTypeForUserInPeriodAsync(
            Tenant, UserB, Type, PeriodStart, PeriodEnd, CancellationToken.None));
    }

    [Fact]
    public async Task ExistsByTypeForUserInPeriodAsync_ForAnotherTenant_ReturnsFalse()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, BillingNotification(Tenant, UserA, PeriodStart.AddDays(5)));

        Assert.False(await NewRepository(ctx).ExistsByTypeForUserInPeriodAsync(
            Other, UserA, Type, PeriodStart, PeriodEnd, CancellationToken.None));
    }

    /// <summary>
    /// The 80% and 100% tiers are DISTINCT types, and that is what lets both reach the user in one
    /// month. An existing 80% notification must never answer the 100% question.
    /// </summary>
    [Fact]
    public async Task ExistsByTypeForUserInPeriodAsync_ForAnotherType_ReturnsFalse()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, BillingNotification(Tenant, UserA, PeriodStart.AddDays(5)));

        Assert.False(await NewRepository(ctx).ExistsByTypeForUserInPeriodAsync(
            Tenant, UserA, "BILLING_ANALYSIS_QUOTA_100", PeriodStart, PeriodEnd,
            CancellationToken.None));
    }

    /// <summary>
    /// The window is half-open, [PeriodStart, PeriodEnd). PeriodEnd is the 1st of the NEXT month,
    /// so a notification written in its first instant belongs to that month and not to this one —
    /// a closed interval would let one boundary row satisfy two periods.
    /// </summary>
    [Fact]
    public async Task ExistsByTypeForUserInPeriodAsync_AtTheExclusiveEnd_ReturnsFalse()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, BillingNotification(Tenant, UserA, PeriodEnd));

        Assert.False(await NewRepository(ctx).ExistsByTypeForUserInPeriodAsync(
            Tenant, UserA, Type, PeriodStart, PeriodEnd, CancellationToken.None));
    }

    [Fact]
    public async Task ExistsByTypeForUserInPeriodAsync_AtTheInclusiveStart_ReturnsTrue()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, BillingNotification(Tenant, UserA, PeriodStart));

        Assert.True(await NewRepository(ctx).ExistsByTypeForUserInPeriodAsync(
            Tenant, UserA, Type, PeriodStart, PeriodEnd, CancellationToken.None));
    }

    // ── CreateManyAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateManyAsync_PersistsTheBatch()
    {
        await using var ctx = NewContext();

        await NewRepository(ctx).CreateManyAsync(
            [
                BillingNotification(Tenant, UserA, PeriodStart.AddDays(1)),
                BillingNotification(Tenant, UserB, PeriodStart.AddDays(1))
            ],
            CancellationToken.None);

        Assert.Equal(2, await ctx.Notifications.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task CreateManyAsync_WithAnEmptyBatch_IsANoOp()
    {
        await using var ctx = NewContext();

        await NewRepository(ctx).CreateManyAsync([], CancellationToken.None);

        Assert.Empty(await ctx.Notifications.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// NotificationId is mapped ValueGeneratedNever, so the Guid the application supplies is the one
    /// that must survive the round-trip. If EF were told the database generates it, the id would be
    /// silently replaced and SourceId-based navigation from the emitter would break.
    /// </summary>
    [Fact]
    public async Task CreateManyAsync_KeepsTheApplicationSuppliedId()
    {
        await using var ctx = NewContext();
        var notification = BillingNotification(Tenant, UserA, PeriodStart.AddDays(1));

        await NewRepository(ctx).CreateManyAsync([notification], CancellationToken.None);

        var stored = Assert.Single(await ctx.Notifications.AsNoTracking().ToListAsync());
        Assert.Equal(notification.NotificationId, stored.NotificationId);
        Assert.Equal(notification.SourceId,       stored.SourceId);
        Assert.Equal("Alert",                     stored.SourceType);
    }
}
