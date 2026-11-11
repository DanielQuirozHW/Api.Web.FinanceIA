// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-06

using System.Security.Claims;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Endpoint-level tests for <see cref="NotificationsController"/>: the real
/// <see cref="NotificationRepository"/> on the EF in-memory provider, a stub tenant accessor and a stub
/// user repository resolving two users of one tenant. Called directly, so the authorization middleware
/// is out of scope here (SuperAdminReadOnlyAuthorizationTests covers the declared metadata).
/// </summary>
public sealed class NotificationsControllerTests
{
    private const string Tenant = "tenant-1";
    private const string UidA   = "uid-a";
    private const string UidB   = "uid-b";

    private static readonly Guid UserA = Guid.NewGuid();
    private static readonly Guid UserB = Guid.NewGuid();

    private static readonly DateTime Start = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

    // ── GET /api/notifications ────────────────────────────────────────────────

    /// <summary>
    /// The Portal in production calls with no parameters: it must get the 50 newest (not every row),
    /// the TRUE unread count — including unread rows beyond the page — a cursor and the total.
    /// </summary>
    [Fact]
    public async Task List_NoParams_ReturnsFiftyNewest_AndTheTrueUnreadCount()
    {
        await using var db = NewContext();
        // 98 notifications; the 10 OLDEST are the unread ones, so none of them is on the first page.
        var rows = Enumerable.Range(0, 98)
            .Select(i => NewNotification(UserA, Start.AddMinutes(i), isRead: i >= 10))
            .ToArray();
        await SeedAsync(db, rows);

        var body = await ListAsync(db);

        Assert.Equal(50, body.Notifications.Count);
        Assert.Equal(Start.AddMinutes(97), body.Notifications[0].CreatedAt);
        Assert.Equal(Start.AddMinutes(48), body.Notifications[^1].CreatedAt);
        Assert.All(body.Notifications, n => Assert.True(n.IsRead));
        Assert.Equal(10, body.UnreadCount);
        Assert.Equal(98, body.TotalCount);
        Assert.NotNull(body.NextCursor);
    }

    [Fact]
    public async Task List_FollowsNextCursor_ToALastPageWithNullCursor()
    {
        await using var db = NewContext();
        await SeedAsync(db, Enumerable.Range(0, 7)
            .Select(i => NewNotification(UserA, Start.AddMinutes(i)))
            .ToArray());

        var first  = await ListAsync(db, c => c.GetNotificationsAsync(3, null, null, null, null, null, null, null, null, default));
        var second = await ListAsync(db, c => c.GetNotificationsAsync(3, first.NextCursor, null, null, null, null, null, null, null, default));
        var third  = await ListAsync(db, c => c.GetNotificationsAsync(3, second.NextCursor, null, null, null, null, null, null, null, default));

        Assert.Equal(3, first.Notifications.Count);
        Assert.Equal(3, second.Notifications.Count);
        Assert.Single(third.Notifications);
        Assert.Null(third.NextCursor);
        Assert.Equal(7, third.TotalCount);

        var all = first.Notifications.Concat(second.Notifications).Concat(third.Notifications)
            .Select(n => n.NotificationId).ToList();
        Assert.Equal(7, all.Distinct().Count());
    }

    /// <summary>
    /// The badge number is the user's whole unread count whatever the filter or page: an unread-only,
    /// typed, one-row page reports the same unreadCount as the default listing.
    /// </summary>
    [Fact]
    public async Task List_UnreadCount_IsIndependentOfFiltersAndPage()
    {
        await using var db = NewContext();
        await SeedAsync(db,
            NewNotification(UserA, Start.AddMinutes(1)),
            NewNotification(UserA, Start.AddMinutes(2), type: AnalysisDecisionNotifier.ConfirmedType),
            NewNotification(UserA, Start.AddMinutes(3)),
            NewNotification(UserA, Start.AddMinutes(4), isRead: true),
            NewNotification(UserB, Start.AddMinutes(5)));   // another user's unread never counts

        var unfiltered = await ListAsync(db);
        var filtered = await ListAsync(db, c => c.GetNotificationsAsync(
            1, null, true, AnalysisDecisionNotifier.ConfirmedType, null, null, null, null, null, default));

        Assert.Equal(3, unfiltered.UnreadCount);
        Assert.Equal(3, filtered.UnreadCount);
        Assert.Single(filtered.Notifications);
        Assert.Equal(1, filtered.TotalCount);
        Assert.Equal(4, unfiltered.TotalCount);
    }

    [Fact]
    public async Task List_FromTo_AreAppliedInUtc()
    {
        await using var db = NewContext();
        await SeedAsync(db,
            NewNotification(UserA, Start.AddDays(-1)),
            NewNotification(UserA, Start),
            NewNotification(UserA, Start.AddDays(1)));

        // An unspecified-kind bound is taken as UTC.
        var from = DateTime.SpecifyKind(Start, DateTimeKind.Unspecified);
        var to   = DateTime.SpecifyKind(Start.AddDays(1), DateTimeKind.Unspecified);
        var body = await ListAsync(db, c => c.GetNotificationsAsync(
            null, null, null, null, null, null, null, from, to, default));

        Assert.Equal(Start, Assert.Single(body.Notifications).CreatedAt);
    }

    public static TheoryData<int?, string?, string?, string?, Guid?, string> InvalidQueries() => new()
    {
        { 0,    null,                null,                  null,     null,           "limit" },
        { 101,  null,                null,                  null,     null,           "limit" },
        { null, "not-a-cursor",      null,                  null,     null,           "cursor" },
        { null, null,                "ANALYSIS_EXPLODED",   null,     null,           "type" },
        { null, null,                null,                  "Fiador", null,           "sourceType" },
        { null, null,                null,                  null,     Guid.NewGuid(), "sourceId" },
    };

    [Theory]
    [MemberData(nameof(InvalidQueries))]
    public async Task List_InvalidQuery_Returns400_NamingTheField(
        int? limit, string? cursor, string? type, string? sourceType, Guid? sourceId, string field)
    {
        await using var db = NewContext();

        var result = await NewController(db).GetNotificationsAsync(
            limit, cursor, null, type, sourceType, sourceId, null, null, null, default);

        AssertValidation(result, field);
    }

    [Fact]
    public async Task List_FromNotBeforeTo_Returns400()
    {
        await using var db = NewContext();

        var result = await NewController(db).GetNotificationsAsync(
            null, null, null, null, null, null, null, Start, Start, default);

        AssertValidation(result, "from");
    }

    [Fact]
    public async Task List_UnknownUser_Returns401()
    {
        await using var db = NewContext();

        var result = await NewController(db, uid: "nobody").GetNotificationsAsync(
            null, null, null, null, null, null, null, null, null, default);

        Assert.IsType<UnauthorizedResult>(result);
    }

    // ── GET /api/notifications/unread-count ───────────────────────────────────

    [Fact]
    public async Task UnreadCount_CountsOnlyTheCallersUnread()
    {
        await using var db = NewContext();
        await SeedAsync(db,
            NewNotification(UserA, Start),
            NewNotification(UserA, Start.AddMinutes(1)),
            NewNotification(UserA, Start.AddMinutes(2), isRead: true),
            NewNotification(UserB, Start.AddMinutes(3)));

        var result = await NewController(db).GetUnreadCountAsync(default);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(2, Assert.IsType<UnreadCountResponseDto>(ok.Value).UnreadCount);
    }

    // ── PATCH /api/notifications/{id}/unread ──────────────────────────────────

    [Fact]
    public async Task MarkAsUnread_Own_Returns204_AndClearsReadState()
    {
        await using var db = NewContext();
        var n = NewNotification(UserA, Start, isRead: true);
        n.ReadAt = Start.AddHours(1);
        await SeedAsync(db, n);

        var result = await NewController(db).MarkAsUnreadAsync(n.NotificationId, default);

        Assert.IsType<NoContentResult>(result);
        var stored = await db.Notifications.AsNoTracking().SingleAsync();
        Assert.False(stored.IsRead);
        Assert.Null(stored.ReadAt);
    }

    [Fact]
    public async Task MarkAsUnread_AnotherUsersNotification_Returns404_AndLeavesItRead()
    {
        await using var db = NewContext();
        var theirs = NewNotification(UserB, Start, isRead: true);
        await SeedAsync(db, theirs);

        var result = await NewController(db).MarkAsUnreadAsync(theirs.NotificationId, default);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(404, problem.StatusCode);
        Assert.True((await db.Notifications.AsNoTracking().SingleAsync()).IsRead);
    }

    // ── POST /api/notifications/mark-read ─────────────────────────────────────

    [Fact]
    public async Task MarkRead_UpdatesOnlyTheCallersOwn()
    {
        await using var db = NewContext();
        var mine   = NewNotification(UserA, Start);
        var theirs = NewNotification(UserB, Start.AddMinutes(1));
        await SeedAsync(db, mine, theirs);

        var result = await NewController(db).MarkManyAsReadAsync(
            new MarkNotificationsReadRequest { Ids = [mine.NotificationId, theirs.NotificationId] }, default);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, ok.Value!.GetType().GetProperty("updated")!.GetValue(ok.Value));

        var stored = await db.Notifications.AsNoTracking().ToDictionaryAsync(x => x.NotificationId);
        Assert.True(stored[mine.NotificationId].IsRead);
        Assert.False(stored[theirs.NotificationId].IsRead);
    }

    [Fact]
    public async Task MarkRead_AnotherUsersIdOnly_UpdatesNothing()
    {
        await using var db = NewContext();
        var theirs = NewNotification(UserB, Start);
        await SeedAsync(db, theirs);

        var result = await NewController(db).MarkManyAsReadAsync(
            new MarkNotificationsReadRequest { Ids = [theirs.NotificationId] }, default);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(0, ok.Value!.GetType().GetProperty("updated")!.GetValue(ok.Value));
        Assert.False((await db.Notifications.AsNoTracking().SingleAsync()).IsRead);
    }

    [Fact]
    public async Task MarkRead_NoIds_Returns400()
    {
        await using var db = NewContext();

        AssertValidation(await NewController(db).MarkManyAsReadAsync(new MarkNotificationsReadRequest(), default), "ids");
        AssertValidation(await NewController(db).MarkManyAsReadAsync(new MarkNotificationsReadRequest { Ids = [] }, default), "ids");
        AssertValidation(await NewController(db).MarkManyAsReadAsync(null, default), "ids");
    }

    [Fact]
    public async Task MarkRead_MoreThanOneHundredIds_Returns400()
    {
        await using var db = NewContext();
        var ids = Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToList();

        var result = await NewController(db).MarkManyAsReadAsync(new MarkNotificationsReadRequest { Ids = ids }, default);

        AssertValidation(result, "ids");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static FinanciaDbContext NewContext()
        => new(new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"notifications-controller-{Guid.NewGuid()}")
            .Options);

    private static NotificationsController NewController(FinanciaDbContext db, string uid = UidA)
    {
        var controller = new NotificationsController(
            new StubTenantContextAccessor(),
            new NotificationRepository(db, NullLogger<NotificationRepository>.Instance),
            new StubUserRepository(),
            NullLogger<NotificationsController>.Instance);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("uid", uid)], "Internal"))
            }
        };

        return controller;
    }

    private static Task<NotificationsResponseDto> ListAsync(FinanciaDbContext db)
        => ListAsync(db, c => c.GetNotificationsAsync(null, null, null, null, null, null, null, null, null, default));

    private static async Task<NotificationsResponseDto> ListAsync(
        FinanciaDbContext db, Func<NotificationsController, Task<IActionResult>> call)
    {
        var result = await call(NewController(db));
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<NotificationsResponseDto>(ok.Value);
    }

    private static void AssertValidation(IActionResult result, string field)
    {
        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, obj.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(obj.Value);
        Assert.Equal("validation_error", problem.Extensions["errorCode"]);
        var errors = Assert.IsAssignableFrom<System.Collections.IEnumerable>(problem.Extensions["errors"]);
        var first = errors.Cast<object>().Single();
        Assert.Equal(field, first.GetType().GetProperty("field")!.GetValue(first));
    }

    private static Notification NewNotification(
        Guid userId, DateTime createdAt, bool isRead = false, string type = BillingAlertService.AnalysisQuota80Type) => new()
    {
        NotificationId = Guid.NewGuid(),
        TenantId       = Tenant,
        UserId         = userId,
        Type           = type,
        Title          = "Consumo del plan al 80%",
        Body           = "Usaste el 82% de los análisis de tu plan este mes.",
        SourceType     = "Alert",
        SourceId       = Guid.NewGuid(),
        IsRead         = isRead,
        ReadAt         = isRead ? createdAt.AddMinutes(5) : null,
        CreatedAt      = createdAt
    };

    private static async Task SeedAsync(FinanciaDbContext db, params Notification[] notifications)
    {
        db.Notifications.AddRange(notifications);
        db.ChangeTracker.DetectChanges(); // AutoDetectChanges is disabled on the context
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; } = new()
        {
            TenantId               = Tenant,
            FirebaseProjectId      = "project",
            FirebaseCredentialJson = "{}",
            RecaptchaSecretKey     = "secret"
        };
    }

    /// <summary>Two active users of the tenant, resolved by Firebase uid.</summary>
    private sealed class StubUserRepository : IUserRepository
    {
        private static User NewUser(Guid id, string uid) => new()
        {
            UserId      = id,
            TenantId    = Tenant,
            FirebaseUid = uid,
            Email       = $"{uid}@example.com",
            DisplayName = uid,
            Role        = Roles.Analyst,
            IsActive    = true,
            CreatedAt   = Start,
            UpdatedAt   = Start
        };

        public Task<User?> GetByFirebaseUidAsync(string firebaseUid, CancellationToken ct) => Task.FromResult(firebaseUid switch
        {
            UidA => NewUser(UserA, UidA),
            UidB => NewUser(UserB, UidB),
            _    => (User?)null,
        });

        public Task UpdateLastLoginAsync(Guid userId, CancellationToken ct) => Task.CompletedTask;
        public Task<User?> GetByUserIdAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<User>> GetByIdsAsync(IEnumerable<Guid> userIds, CancellationToken ct) => throw new NotImplementedException();
        public Task<User?> GetByEmailAsync(string email, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> ExistsByFirebaseUidAsync(string firebaseUid, CancellationToken ct) => throw new NotImplementedException();
        public Task<User> CreateAsync(User user, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateOnboardingFlagsAsync(Guid userId, string? flagsJson, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<User>> GetActiveByTenantAsync(string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateTeamAsync(Guid userId, Guid? teamId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> ClearTeamAsync(Guid teamId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
    }
}
