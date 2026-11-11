// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-02

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// The row <see cref="AnalysisCompletionNotifier"/> writes for a finished analysis, and its fault
/// isolation.
///
/// <para>
/// What a compiler cannot check here is the POINTER: a completed row must carry SourceType "Analysis"
/// and the ResultId, or the Portal's deep link 404s; a failed row has no result to open, so it points at
/// the RUN instead — SourceType "AnalysisRun" and the document id, what GET /api/analyses/runs lists. And the copy names the company, so it must never reach a log line.
/// </para>
/// </summary>
public sealed class AnalysisCompletionNotifierTests
{
    private const string Tenant = "SgrOneClickSandboxFB2";
    private const string Company = "Metalúrgica Del Sur S.A.";

    // ── Completed ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Completed_WritesOneRow_PointingAtTheResult()
    {
        var notifications = new StubNotificationRepository();
        var sut           = BuildSut(notifications);
        var userId        = Guid.NewGuid();
        var analysisId    = Guid.NewGuid();
        var resultId      = Guid.NewGuid();

        await sut.NotifyCompletedAsync(Tenant, userId, analysisId, resultId, Company, 2024, 42.5m, CancellationToken.None);

        var row = Assert.Single(notifications.Created);
        Assert.Equal(Tenant, row.TenantId);
        Assert.Equal(userId, row.UserId);
        Assert.Equal("ANALYSIS_COMPLETED", row.Type);
        Assert.Equal(AnalysisCompletionNotifier.CompletedType, row.Type);
        Assert.Equal($"Análisis listo: {Company}", row.Title);
        // 42.5 internal (0 = healthy) → 570 on the NOSIS scale the Portal's gauge shows.
        Assert.Equal("El análisis del balance 2024 terminó. Score: 570 sobre 1000.", row.Body);
        Assert.Equal("Analysis", row.SourceType);
        Assert.Equal(resultId, row.SourceId);
        Assert.NotEqual(Guid.Empty, row.NotificationId);
        Assert.False(row.IsRead);
    }

    [Fact]
    public async Task Completed_WithoutScoreOrName_DropsBoth()
    {
        var notifications = new StubNotificationRepository();
        var sut           = BuildSut(notifications);

        await sut.NotifyCompletedAsync(Tenant, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "  ", 2023, null, CancellationToken.None);

        var row = Assert.Single(notifications.Created);
        Assert.Equal("Análisis listo", row.Title);
        Assert.Equal("El análisis del balance 2023 terminó.", row.Body);
    }

    [Fact]
    public async Task Completed_WithoutFiscalYear_OmitsTheYear()
    {
        var notifications = new StubNotificationRepository();
        var sut           = BuildSut(notifications);

        await sut.NotifyCompletedAsync(Tenant, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Company, 0, 71m, CancellationToken.None);

        Assert.Equal("El análisis del balance terminó. Score: 290 sobre 1000.", Assert.Single(notifications.Created).Body);
    }

    /// <summary>
    /// The body must show the score the analyst will SEE: the internal scale is inverted (0 =
    /// healthy), so a healthy company must read high, never low.
    /// </summary>
    [Theory]
    [InlineData(0,     "1000")]
    [InlineData(13.8,  "860")]
    [InlineData(100,   "0")]
    public async Task Completed_ScoreIsOnTheNosisScale(double internalScore, string shown)
    {
        var notifications = new StubNotificationRepository();
        var sut           = BuildSut(notifications);

        await sut.NotifyCompletedAsync(Tenant, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Company, 2024, (decimal)internalScore, CancellationToken.None);

        Assert.EndsWith($"Score: {shown} sobre 1000.", Assert.Single(notifications.Created).Body);
    }

    /// <summary>
    /// The duplicate guard is keyed on the value actually stored in SourceId — the ResultId — so a
    /// second call for the same result writes nothing.
    /// </summary>
    [Fact]
    public async Task Completed_WhenAlreadyNotifiedForThisResult_WritesNothing()
    {
        var userId        = Guid.NewGuid();
        var resultId      = Guid.NewGuid();
        var notifications = new StubNotificationRepository();
        notifications.Existing.Add((userId, AnalysisCompletionNotifier.CompletedType, resultId));
        var sut = BuildSut(notifications);

        await sut.NotifyCompletedAsync(Tenant, userId, Guid.NewGuid(), resultId, Company, 2024, 50m, CancellationToken.None);

        Assert.Empty(notifications.Created);
        Assert.Equal(0, notifications.CreateCallCount);
    }

    [Fact]
    public async Task Completed_CalledTwiceForTheSameResult_LeavesOneRow()
    {
        var userId        = Guid.NewGuid();
        var resultId      = Guid.NewGuid();
        var notifications = new StubNotificationRepository();
        var sut           = BuildSut(notifications);

        await sut.NotifyCompletedAsync(Tenant, userId, Guid.NewGuid(), resultId, Company, 2024, null, CancellationToken.None);
        await sut.NotifyCompletedAsync(Tenant, userId, Guid.NewGuid(), resultId, Company, 2024, null, CancellationToken.None);

        Assert.Single(notifications.Created);
    }

    [Fact]
    public async Task Completed_ALongBusinessName_IsClampedToTheTitleColumn()
    {
        var notifications = new StubNotificationRepository();
        var sut           = BuildSut(notifications);

        await sut.NotifyCompletedAsync(Tenant, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new string('A', 400), 2024, null, CancellationToken.None);

        var title = Assert.Single(notifications.Created).Title;
        Assert.Equal(300, title.Length);
        Assert.StartsWith("Análisis listo: AAA", title);
        Assert.EndsWith("…", title);
    }

    // ── Failed ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Failed_WritesOneRow_PointingAtTheRun()
    {
        var entityId      = Guid.NewGuid();
        var userId        = Guid.NewGuid();
        var documentId    = Guid.NewGuid();
        var notifications = new StubNotificationRepository();
        var entities      = new StubEntityRepository();
        entities.Names[entityId] = Company;
        var sut = BuildSut(notifications, entities);

        await sut.NotifyFailedAsync(Tenant, userId, documentId, entityId, CancellationToken.None);

        var row = Assert.Single(notifications.Created);
        Assert.Equal(Tenant, row.TenantId);
        Assert.Equal(userId, row.UserId);
        Assert.Equal("ANALYSIS_FAILED", row.Type);
        Assert.Equal(AnalysisCompletionNotifier.FailedType, row.Type);
        Assert.Equal($"No pudimos completar el análisis de {Company}", row.Title);
        Assert.Equal("Volvé a intentarlo desde Nuevo análisis. El documento quedó cargado.", row.Body);
        Assert.Equal("AnalysisRun", row.SourceType);
        Assert.Equal(AnalysisCompletionNotifier.AnalysisRunSourceType, row.SourceType);
        Assert.Equal(documentId, row.SourceId);
        Assert.False(row.IsRead);
        Assert.Equal(Tenant, entities.LastTenantId);
    }

    [Fact]
    public async Task Failed_TwoFailuresOfOneDocument_WriteTwoRows_BothPointingAtIt()
    {
        // One document can fail many times; each failed run is its own event, so the source must not
        // turn into a dedupe key.
        var documentId    = Guid.NewGuid();
        var userId        = Guid.NewGuid();
        var notifications = new StubNotificationRepository();
        var sut           = BuildSut(notifications, new StubEntityRepository());

        await sut.NotifyFailedAsync(Tenant, userId, documentId, Guid.NewGuid(), CancellationToken.None);
        await sut.NotifyFailedAsync(Tenant, userId, documentId, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(2, notifications.Created.Count);
        Assert.All(notifications.Created, row =>
        {
            Assert.Equal(AnalysisCompletionNotifier.AnalysisRunSourceType, row.SourceType);
            Assert.Equal(documentId, row.SourceId);
        });
    }

    [Fact]
    public async Task Failed_WhenTheEntityIsNotFound_UsesTheGenericTitle()
    {
        var notifications = new StubNotificationRepository();
        var sut           = BuildSut(notifications, new StubEntityRepository());

        await sut.NotifyFailedAsync(Tenant, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        Assert.Equal("No pudimos completar el análisis", Assert.Single(notifications.Created).Title);
    }

    /// <summary>A failed name lookup degrades the title; it never costs the analyst the notification.</summary>
    [Fact]
    public async Task Failed_WhenTheEntityLookupThrows_StillWritesWithTheGenericTitle()
    {
        var notifications = new StubNotificationRepository();
        var entities      = new StubEntityRepository { ThrowOnGetName = new TimeoutException("sql timeout") };
        var logger        = new RecordingLogger<AnalysisCompletionNotifier>();
        var sut           = BuildSut(notifications, entities, logger);

        await sut.NotifyFailedAsync(Tenant, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        Assert.Equal("No pudimos completar el análisis", Assert.Single(notifications.Created).Title);
        Assert.Single(logger.Warnings);
    }

    // ── Fault isolation ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Completed_AFailingRepository_IsSwallowed_AndLoggedWithIdsOnly()
    {
        var analysisId    = Guid.NewGuid();
        var notifications = new StubNotificationRepository
        {
            ThrowOnCreate = new InvalidOperationException("Invalid object name 'dbo.Notifications'.")
        };
        var logger = new RecordingLogger<AnalysisCompletionNotifier>();
        var sut    = BuildSut(notifications, logger: logger);

        var thrown = await Record.ExceptionAsync(() => sut.NotifyCompletedAsync(
            Tenant, Guid.NewGuid(), analysisId, Guid.NewGuid(), Company, 2024, 42m, CancellationToken.None));

        Assert.Null(thrown);
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains(analysisId.ToString(), warning);
        Assert.Contains(Tenant, warning);
        Assert.DoesNotContain(Company, warning);
    }

    [Fact]
    public async Task Failed_AFailingRepository_IsSwallowed_AndLoggedWithIdsOnly()
    {
        var documentId    = Guid.NewGuid();
        var entityId      = Guid.NewGuid();
        var notifications = new StubNotificationRepository
        {
            ThrowOnCreate = new InvalidOperationException("Invalid object name 'dbo.Notifications'.")
        };
        var entities = new StubEntityRepository();
        entities.Names[entityId] = Company;
        var logger = new RecordingLogger<AnalysisCompletionNotifier>();
        var sut    = BuildSut(notifications, entities, logger);

        var thrown = await Record.ExceptionAsync(() => sut.NotifyFailedAsync(
            Tenant, Guid.NewGuid(), documentId, entityId, CancellationToken.None));

        Assert.Null(thrown);
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains(documentId.ToString(), warning);
        Assert.Contains(Tenant, warning);
        Assert.DoesNotContain(Company, warning);
    }

    [Fact]
    public async Task Completed_AFailingDuplicateCheck_IsSwallowed()
    {
        var notifications = new StubNotificationRepository
        {
            ThrowOnExists = new TimeoutException("sql timeout")
        };
        var logger = new RecordingLogger<AnalysisCompletionNotifier>();
        var sut    = BuildSut(notifications, logger: logger);

        var thrown = await Record.ExceptionAsync(() => sut.NotifyCompletedAsync(
            Tenant, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Company, 2024, null, CancellationToken.None));

        Assert.Null(thrown);
        Assert.Empty(notifications.Created);
        Assert.Single(logger.Warnings);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    private static AnalysisCompletionNotifier BuildSut(
        StubNotificationRepository                     notifications,
        StubEntityRepository?                          entities = null,
        RecordingLogger<AnalysisCompletionNotifier>?   logger   = null)
        => new(
            notifications,
            entities ?? new StubEntityRepository(),
            logger ?? new RecordingLogger<AnalysisCompletionNotifier>());

    // ── Stubs (hand-rolled, no mocking library — the house convention) ───────────

    private sealed class StubNotificationRepository : INotificationRepository
    {
        /// <summary>(userId, type, sourceId) triples already on record.</summary>
        public HashSet<(Guid UserId, string Type, Guid SourceId)> Existing { get; } = new();

        public List<Notification> Created { get; } = new();

        public int CreateCallCount { get; private set; }

        public Exception? ThrowOnCreate { get; set; }

        public Exception? ThrowOnExists { get; set; }

        public Task<bool> ExistsByTypeForUserAndSourceAsync(
            string tenantId, Guid userId, string type, Guid sourceId, CancellationToken ct)
        {
            if (ThrowOnExists is not null) return Task.FromException<bool>(ThrowOnExists);
            return Task.FromResult(Existing.Contains((userId, type, sourceId)));
        }

        public Task CreateManyAsync(IEnumerable<Notification> notifications, CancellationToken ct)
        {
            CreateCallCount++;
            if (ThrowOnCreate is not null) return Task.FromException(ThrowOnCreate);

            foreach (var notification in notifications)
            {
                Created.Add(notification);
                if (notification.SourceId is Guid sourceId)
                    Existing.Add((notification.UserId, notification.Type, sourceId));
            }
            return Task.CompletedTask;
        }

        public Task<bool> ExistsByTypeForUserInPeriodAsync(string tenantId, Guid userId, string type, DateTime periodStartUtc, DateTime periodEndUtc, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationPage> GetPageForUserAsync(string tenantId, Guid userId, NotificationQuery query, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> CountForUserAsync(string tenantId, Guid userId, NotificationQuery query, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> MarkAsUnreadAsync(Guid notificationId, string tenantId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> MarkManyAsReadAsync(string tenantId, Guid userId, IReadOnlyCollection<Guid> notificationIds, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> GetUnreadCountForUserAsync(string tenantId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> MarkAsReadAsync(Guid notificationId, string tenantId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> MarkAllAsReadForUserAsync(string tenantId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
    }

    /// <summary>Only the narrow name read is implemented: the notifier must never load a full entity row.</summary>
    private sealed class StubEntityRepository : IEntityRepository
    {
        public Dictionary<Guid, string> Names { get; } = new();

        public Exception? ThrowOnGetName { get; set; }

        public string? LastTenantId { get; private set; }

        public Task<EntityNameProjection?> GetNameByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
        {
            LastTenantId = tenantId;
            if (ThrowOnGetName is not null) return Task.FromException<EntityNameProjection?>(ThrowOnGetName);

            return Task.FromResult(Names.TryGetValue(entityId, out var name)
                ? new EntityNameProjection { EntityId = entityId, BusinessName = name, CUIT = "30-00000000-0" }
                : null);
        }

        public Task<Entity?> GetByIdAsync(Guid entityId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, EntityNameProjection>> GetNameLookupByIdsAsync(IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<EntityDetailProjection?> GetDetailByIdAsync(Guid entityId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> ExistsByIdAsync(Guid entityId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Entity>> GetAllByTenantAsync(string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<Entity?> GetByCuitAsync(string cuit, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<Entity> CreateAsync(Entity entity, CancellationToken ct) => throw new NotImplementedException();
        public Task<Entity> UpdateAsync(Entity entity, CancellationToken ct) => throw new NotImplementedException();
        public Task<(IReadOnlyList<EntityListProjection> Items, int TotalCount)> GetListPageByTenantAsync(string tenantId, int skip, int take, CancellationToken ct) => throw new NotImplementedException();
        public Task<EntitySnapshotProjection?> GetBcraSnapshotAsync(Guid entityId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<EntitySnapshotProjection?> GetCasfogSnapshotAsync(Guid entityId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<EntitySnapshotProjection?> GetLufeSnapshotAsync(Guid entityId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<EntitySnapshotProjection?> GetNosisSnapshotAsync(Guid entityId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
    }

    /// <summary>Captures Warning-level log calls, rendered, so their CONTENT can be asserted.</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }
}
