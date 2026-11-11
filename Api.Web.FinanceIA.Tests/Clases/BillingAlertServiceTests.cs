// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-31

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// Covers <see cref="BillingAlertService"/>: which tier fires, that it fires exactly once per
/// calendar month, that a degraded figure fires nothing, and that the value it stores is the
/// percentage rather than the raw count.
///
/// <para>
/// Hand-rolled stubs per the repo's no-mock-library convention. Tenant isolation is deliberately
/// NOT proven here — the stub returns whatever it is told regardless of the tenant it is handed,
/// so only <see cref="Api.Web.FinanceIA.Tests.Data.AlertRepositoryExistsByTypeTests"/>, which runs
/// against a real DbContext, can show the dedup query actually honours its TenantId predicate.
/// What IS proven here is that the service passes the right tenant and the right window down.
/// </para>
/// </summary>
public sealed class BillingAlertServiceTests
{
    private const string Tenant = "tenant-1";

    private static readonly DateTime PeriodStart = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PeriodEnd   = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    // ── Stubs ───────────────────────────────────────────────────────────────────

    private sealed class StubAlertRepository : IAlertRepository
    {
        /// <summary>Alert types already on record for the period.</summary>
        public HashSet<string> ExistingTypes { get; } = new(StringComparer.Ordinal);

        public List<Alert> Created { get; } = new();

        public int ExistsCallCount { get; private set; }
        public int CreateCallCount { get; private set; }

        public string? LastExistsTenantId { get; private set; }
        public DateTime? LastExistsPeriodStart { get; private set; }
        public DateTime? LastExistsPeriodEnd { get; private set; }

        public Exception? ThrowOnExists { get; set; }
        public Exception? ThrowOnCreate { get; set; }

        public Task<bool> ExistsByTypeInPeriodAsync(
            string tenantId, string alertType, DateTime periodStartUtc, DateTime periodEndUtc, CancellationToken ct)
        {
            ExistsCallCount++;
            LastExistsTenantId    = tenantId;
            LastExistsPeriodStart = periodStartUtc;
            LastExistsPeriodEnd   = periodEndUtc;

            if (ThrowOnExists is not null) return Task.FromException<bool>(ThrowOnExists);

            return Task.FromResult(ExistingTypes.Contains(alertType));
        }

        public Task CreateManyAsync(IEnumerable<Alert> alerts, CancellationToken ct)
        {
            CreateCallCount++;
            if (ThrowOnCreate is not null) return Task.FromException(ThrowOnCreate);

            Created.AddRange(alerts);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Alert>> GetAllByTenantAsync(string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Alert>> GetByEntityIdAsync(Guid entityId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<Alert?> GetByIdAsync(Guid alertId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> MarkAsReadAsync(Guid alertId, string tenantId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> MarkAsResolvedAsync(Guid alertId, string tenantId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> SupersedePreviousEntityAlertsAsync(Guid entityId, string tenantId, Guid? excludeAnalysisId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> MarkAllAsReadAsync(string tenantId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Alert>> GetOpenByTypesForUpdateAsync(string tenantId, IReadOnlyCollection<string> alertTypes, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Alert>> GetOpenByTypesCreatedBeforeForUpdateAsync(string tenantId, IReadOnlyCollection<string> alertTypes, DateTime createdBeforeUtc, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<(string AlertType, string MetricKey), Alert>> GetLatestByMetricKeysAsync(string tenantId, IReadOnlyCollection<string> alertTypes, IReadOnlyCollection<string> metricKeys, CancellationToken ct) => throw new NotImplementedException();
        public Task SaveReconciliationAsync(string tenantId, IReadOnlyCollection<Alert> toCreate, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<PreviousAlertRow>> GetPreviousByKeysAsync(string tenantId, IReadOnlyCollection<string> metricKeys, CancellationToken ct) => throw new NotImplementedException();
        public Task<AlertUnresolveOutcome> UnresolveAsync(Guid alertId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class StubNotificationRepository : INotificationRepository
    {
        /// <summary>(userId, type) pairs already on record for the period.</summary>
        public HashSet<(Guid UserId, string Type)> ExistingForUser { get; } = new();

        public List<Notification> Created { get; } = new();

        public int ExistsCallCount { get; private set; }
        public int CreateCallCount { get; private set; }

        public string? LastExistsTenantId { get; private set; }
        public DateTime? LastExistsPeriodStart { get; private set; }
        public DateTime? LastExistsPeriodEnd { get; private set; }

        public Exception? ThrowOnExists { get; set; }
        public Exception? ThrowOnCreate { get; set; }

        public Task<bool> ExistsByTypeForUserInPeriodAsync(
            string tenantId, Guid userId, string type,
            DateTime periodStartUtc, DateTime periodEndUtc, CancellationToken ct)
        {
            ExistsCallCount++;
            LastExistsTenantId    = tenantId;
            LastExistsPeriodStart = periodStartUtc;
            LastExistsPeriodEnd   = periodEndUtc;

            if (ThrowOnExists is not null) return Task.FromException<bool>(ThrowOnExists);

            return Task.FromResult(ExistingForUser.Contains((userId, type)));
        }

        public Task CreateManyAsync(IEnumerable<Notification> notifications, CancellationToken ct)
        {
            CreateCallCount++;
            if (ThrowOnCreate is not null) return Task.FromException(ThrowOnCreate);

            Created.AddRange(notifications);
            return Task.CompletedTask;
        }

        public Task<NotificationPage> GetPageForUserAsync(string tenantId, Guid userId, NotificationQuery query, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> CountForUserAsync(string tenantId, Guid userId, NotificationQuery query, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> MarkAsUnreadAsync(Guid notificationId, string tenantId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> MarkManyAsReadAsync(string tenantId, Guid userId, IReadOnlyCollection<Guid> notificationIds, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> GetUnreadCountForUserAsync(string tenantId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> MarkAsReadAsync(Guid notificationId, string tenantId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> MarkAllAsReadForUserAsync(string tenantId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        // The source-keyed sibling. Unused by the billing emitter, which is period-keyed.
        public Task<bool> ExistsByTypeForUserAndSourceAsync(string tenantId, Guid userId, string type, Guid sourceId, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class StubUserRepository : IUserRepository
    {
        public List<User> ActiveUsers { get; } = new();

        public string? LastTenantId { get; private set; }

        public Exception? ThrowOnGetActive { get; set; }

        public Task<IReadOnlyList<User>> GetActiveByTenantAsync(string tenantId, CancellationToken ct)
        {
            LastTenantId = tenantId;

            if (ThrowOnGetActive is not null)
                return Task.FromException<IReadOnlyList<User>>(ThrowOnGetActive);

            return Task.FromResult<IReadOnlyList<User>>(ActiveUsers);
        }

        public Task<User?> GetByFirebaseUidAsync(string firebaseUid, CancellationToken ct) => throw new NotImplementedException();
        public Task<User?> GetByUserIdAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<User?> GetByEmailAsync(string email, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> ExistsByFirebaseUidAsync(string firebaseUid, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<User>> GetByIdsAsync(IEnumerable<Guid> userIds, CancellationToken ct) => throw new NotImplementedException();
        public Task<User> CreateAsync(User user, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateLastLoginAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateOnboardingFlagsAsync(Guid userId, string? flagsJson, CancellationToken ct) => throw new NotImplementedException();
        // Team assignment (DDL 063) — unused by this suite.
        public Task UpdateTeamAsync(Guid userId, Guid? teamId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> ClearTeamAsync(Guid teamId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static BillingUsageSnapshot Snapshot(
        int  analysesUsed         = 10,
        int  analysesLimit        = 100,
        long chatTokensUsed       = 400_000L,
        long chatTokensLimit      = 4_000_000L,
        BillingPlanSource planSource = BillingPlanSource.Configured,
        bool analysesReadFailed   = false,
        bool chatTokensReadFailed = false) => new()
    {
        PlanName             = "100 análisis",
        PeriodStart          = PeriodStart,
        PeriodEnd            = PeriodEnd,
        PlanSource           = planSource,
        ChatTokensSource     = ChatTokenSource.ApiCallLogs,
        AnalysesReadFailed   = analysesReadFailed,
        ChatTokensReadFailed = chatTokensReadFailed,
        AnalysesUsed         = analysesUsed,
        AnalysesLimit        = analysesLimit,
        ChatTokensUsed       = chatTokensUsed,
        ChatTokensLimit      = chatTokensLimit,

        // Both required on the record but irrelevant here: nothing in this file enforces or reports
        // the per-conversation context budget, and nothing meters the model allow-list at all — it is
        // a capability set, not an allowance. Fixed rather than parameterised so neither can drift
        // into looking like an input to what these tests actually assert.
        ChatContextBudgetTokens = 60_000,
        ChatEnabledModels       = ChatModelCatalog.AllIds
    };

    /// <summary>
    /// Builds the service under test. The notification and user stubs default to empty, which
    /// leaves the fan-out a no-op (no users ⇒ it returns before touching anything), so every
    /// alert-focused test below is unaffected by the notification feature existing.
    /// </summary>
    private static BillingAlertService BuildSut(
        StubAlertRepository alerts,
        bool enforceChatTokens = false,
        StubNotificationRepository? notifications = null,
        StubUserRepository? users = null)
        => new(
            alerts,
            notifications ?? new StubNotificationRepository(),
            users ?? new StubUserRepository(),
            Options.Create(new BillingOptions { EnforceChatTokens = enforceChatTokens }),
            NullLogger<BillingAlertService>.Instance);

    private static User ActiveUser(Guid userId)
        => new()
        {
            UserId      = userId,
            TenantId    = Tenant,
            FirebaseUid = $"fb-{userId}",
            Email       = $"{userId}@example.test",
            DisplayName = "Analyst",
            Role        = "Analyst",
            IsActive    = true
        };

    // ── Tiers ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Below the warning tier nothing is emitted AND the database is never asked. That second half
    /// is the point: this runs on an endpoint the Portal polls, so the common case has to be free.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(79)]   // the boundary — 79% is still quiet
    public async Task EvaluateAsync_BelowEightyPercent_EmitsNothingAndNeverQueries(int used)
    {
        var repo = new StubAlertRepository();

        await BuildSut(repo).EvaluateAsync(Tenant, Snapshot(analysesUsed: used), CancellationToken.None);

        Assert.Empty(repo.Created);
        Assert.Equal(0, repo.ExistsCallCount);
        Assert.Equal(0, repo.CreateCallCount);
    }

    [Theory]
    [InlineData(80)]   // exactly at the threshold — inclusive
    [InlineData(82)]
    [InlineData(99)]
    public async Task EvaluateAsync_BetweenEightyAndOneHundred_EmitsTheEightyAlert(int used)
    {
        var repo = new StubAlertRepository();

        await BuildSut(repo).EvaluateAsync(Tenant, Snapshot(analysesUsed: used), CancellationToken.None);

        var alert = Assert.Single(repo.Created);
        Assert.Equal(BillingAlertService.AnalysisQuota80Type, alert.AlertType);
        Assert.Equal("Medium", alert.Severity);
        Assert.Equal(80m, alert.ThresholdValue);
    }

    [Theory]
    [InlineData(100)]  // exactly at the limit
    [InlineData(140)]  // percent is uncapped above 100 and must still map to the limit tier
    public async Task EvaluateAsync_AtOrAboveOneHundredPercent_EmitsTheLimitAlert(int used)
    {
        var repo = new StubAlertRepository();

        await BuildSut(repo).EvaluateAsync(Tenant, Snapshot(analysesUsed: used), CancellationToken.None);

        var alert = Assert.Single(repo.Created);
        Assert.Equal(BillingAlertService.AnalysisQuota100Type, alert.AlertType);
        Assert.Equal("High", alert.Severity);
        Assert.Equal(100m, alert.ThresholdValue);
    }

    /// <summary>
    /// Only the highest due tier fires. At 100% the 80% alert is not back-filled — an earlier poll
    /// already emitted it when it was the tier in force.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_AtOneHundredPercent_DoesNotAlsoEmitTheEightyAlert()
    {
        var repo = new StubAlertRepository();

        await BuildSut(repo).EvaluateAsync(Tenant, Snapshot(analysesUsed: 100), CancellationToken.None);

        Assert.DoesNotContain(repo.Created, a => a.AlertType == BillingAlertService.AnalysisQuota80Type);
    }

    // ── Idempotency ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The whole design in one test: the Portal polls this endpoint, so a second evaluation in the
    /// same month must be silent or the inbox floods.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_CalledTwiceInTheSamePeriod_EmitsOnlyOnce()
    {
        var repo = new StubAlertRepository();
        var sut  = BuildSut(repo);
        var snapshot = Snapshot(analysesUsed: 82);

        await sut.EvaluateAsync(Tenant, snapshot, CancellationToken.None);

        // What the first call wrote is what the database would now report on the second.
        foreach (var created in repo.Created) repo.ExistingTypes.Add(created.AlertType);

        await sut.EvaluateAsync(Tenant, snapshot, CancellationToken.None);

        Assert.Single(repo.Created);
        Assert.Equal(1, repo.CreateCallCount);
    }

    /// <summary>
    /// _80 and _100 are DISTINCT types, so an already-emitted 80% warning must not suppress the
    /// 100% one when the tenant crosses the limit later in the same month. Emitting both, in
    /// sequence, is the correct month.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_WhenEightyAlreadyFiredAndUsageReachesOneHundred_EmitsTheLimitAlert()
    {
        var repo = new StubAlertRepository();
        repo.ExistingTypes.Add(BillingAlertService.AnalysisQuota80Type);

        await BuildSut(repo).EvaluateAsync(Tenant, Snapshot(analysesUsed: 100), CancellationToken.None);

        var alert = Assert.Single(repo.Created);
        Assert.Equal(BillingAlertService.AnalysisQuota100Type, alert.AlertType);
    }

    /// <summary>
    /// The dedup lookup must be scoped to the tenant and bounded by the snapshot's own period —
    /// never a rolling window, or an alert from late July would silence August.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_QueriesWithTheTenantAndTheSnapshotPeriod()
    {
        var repo = new StubAlertRepository();

        await BuildSut(repo).EvaluateAsync(Tenant, Snapshot(analysesUsed: 82), CancellationToken.None);

        Assert.Equal(Tenant, repo.LastExistsTenantId);
        Assert.Equal(PeriodStart, repo.LastExistsPeriodStart);
        Assert.Equal(PeriodEnd, repo.LastExistsPeriodEnd);
    }

    // ── Degraded snapshots ──────────────────────────────────────────────────────

    /// <summary>
    /// A fallback number is not a reason to tell someone they are near a limit. Both routes into
    /// "unreliable" are covered: a plan that could not be read, and a count that could not be.
    /// </summary>
    [Theory]
    [InlineData(true,  false)]  // the plan read failed
    [InlineData(false, true)]   // the analysis count read failed
    public async Task EvaluateAsync_WhenTheAnalysisQuotaIsUnreliable_EmitsNothing(
        bool planReadFailed, bool analysesReadFailed)
    {
        var repo = new StubAlertRepository();

        var snapshot = Snapshot(
            analysesUsed: 95,
            planSource: planReadFailed ? BillingPlanSource.ReadFailed : BillingPlanSource.Configured,
            analysesReadFailed: analysesReadFailed);

        await BuildSut(repo).EvaluateAsync(Tenant, snapshot, CancellationToken.None);

        Assert.Empty(repo.Created);
        Assert.Equal(0, repo.ExistsCallCount);
    }

    /// <summary>
    /// The two sides degrade independently, exactly as the two enforcement gates do: a broken
    /// analysis count must not silence a chat warning that is still measurable.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_WhenOnlyTheAnalysisSideIsDegraded_StillEmitsTheChatAlert()
    {
        var repo = new StubAlertRepository();

        var snapshot = Snapshot(
            analysesUsed: 95,
            chatTokensUsed: 3_400_000L,   // 85%
            analysesReadFailed: true);

        await BuildSut(repo, enforceChatTokens: true).EvaluateAsync(Tenant, snapshot, CancellationToken.None);

        var alert = Assert.Single(repo.Created);
        Assert.Equal(BillingAlertService.ChatQuota80Type, alert.AlertType);
    }

    // ── The chat flag ───────────────────────────────────────────────────────────

    /// <summary>
    /// Mirrors BillingQuotaFilter: while Billing:EnforceChatTokens is false the per-plan token
    /// figures are unagreed placeholders, so the quota is measured but never announced.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_WhenChatTokensAreNotEnforced_EmitsNoChatAlert()
    {
        var repo = new StubAlertRepository();

        await BuildSut(repo, enforceChatTokens: false)
            .EvaluateAsync(Tenant, Snapshot(chatTokensUsed: 4_000_000L), CancellationToken.None);

        Assert.Empty(repo.Created);
    }

    [Fact]
    public async Task EvaluateAsync_WhenChatTokensAreEnforced_EmitsTheChatAlert()
    {
        var repo = new StubAlertRepository();

        await BuildSut(repo, enforceChatTokens: true)
            .EvaluateAsync(Tenant, Snapshot(chatTokensUsed: 4_000_000L), CancellationToken.None);

        var alert = Assert.Single(repo.Created);
        Assert.Equal(BillingAlertService.ChatQuota100Type, alert.AlertType);
    }

    [Fact]
    public async Task EvaluateAsync_WhenTheChatQuotaIsUnreliable_EmitsNoChatAlertEvenWhenEnforced()
    {
        var repo = new StubAlertRepository();

        await BuildSut(repo, enforceChatTokens: true).EvaluateAsync(
            Tenant, Snapshot(chatTokensUsed: 4_000_000L, chatTokensReadFailed: true), CancellationToken.None);

        Assert.Empty(repo.Created);
    }

    // ── The stored row ──────────────────────────────────────────────────────────

    /// <summary>
    /// MetricValue carries the PERCENTAGE, never the underlying count. dbo.Alerts.MetricValue is
    /// decimal(10,4) — it tops out just under a million — so storing 3,400,000 chat tokens there
    /// would overflow the column and fail the insert. This test is the guard on that.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_StoresThePercentInMetricValue_NotTheTokenCount()
    {
        var repo = new StubAlertRepository();

        await BuildSut(repo, enforceChatTokens: true).EvaluateAsync(
            Tenant, Snapshot(chatTokensUsed: 3_400_000L, chatTokensLimit: 4_000_000L), CancellationToken.None);

        var alert = Assert.Single(repo.Created);
        Assert.Equal(85m, alert.MetricValue);
        Assert.Equal("chat_tokens_percent", alert.MetricKey);
        Assert.True(alert.MetricValue < 1_000_000m, "MetricValue must fit decimal(10,4).");
    }

    /// <summary>
    /// System-level shape: no entity, no analysis. AlertsController renders that as "Sistema", and
    /// dbo.Alerts only accepts the null EntityId once script 048 has run.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_EmitsASystemLevelAlertWithNoEntityAndNoAnalysis()
    {
        var repo = new StubAlertRepository();

        await BuildSut(repo).EvaluateAsync(Tenant, Snapshot(analysesUsed: 82), CancellationToken.None);

        var alert = Assert.Single(repo.Created);
        Assert.Null(alert.EntityId);
        Assert.Null(alert.AnalysisId);
        Assert.Equal(Tenant, alert.TenantId);
        Assert.NotEqual(Guid.Empty, alert.AlertId);
        Assert.False(alert.IsRead);
        Assert.False(alert.IsResolved);
        Assert.Equal(DateTimeKind.Utc, alert.CreatedAt.Kind);
    }

    /// <summary>
    /// The copy is es-AR, names the plan, and rounds the percentage — an analyst should read
    /// "82%", not "82,35%".
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_WritesRoundedEsArCopyNamingThePlan()
    {
        var repo = new StubAlertRepository();

        await BuildSut(repo).EvaluateAsync(
            Tenant, Snapshot(analysesUsed: 82, analysesLimit: 100), CancellationToken.None);

        var alert = Assert.Single(repo.Created);
        Assert.Equal("Consumo del plan al 80%", alert.Title);
        Assert.Contains("82%", alert.Description);
        Assert.Contains("100 análisis", alert.Description);
        Assert.DoesNotContain("82,00", alert.Description);
    }

    /// <summary>
    /// Both sides due at once still costs a single round-trip to the writer.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_WhenBothQuotasAreDue_WritesThemInOneCall()
    {
        var repo = new StubAlertRepository();

        await BuildSut(repo, enforceChatTokens: true).EvaluateAsync(
            Tenant, Snapshot(analysesUsed: 85, chatTokensUsed: 4_000_000L), CancellationToken.None);

        Assert.Equal(2, repo.Created.Count);
        Assert.Equal(1, repo.CreateCallCount);
        Assert.Contains(repo.Created, a => a.AlertType == BillingAlertService.AnalysisQuota80Type);
        Assert.Contains(repo.Created, a => a.AlertType == BillingAlertService.ChatQuota100Type);
    }

    // ── Fault isolation ─────────────────────────────────────────────────────────

    /// <summary>
    /// The caller is a GET whose response is already computed. Nothing this service does may reach
    /// it — including the "script 048 has not been run" case, which surfaces as a NOT NULL
    /// violation on the insert and is the single likeliest failure in the first days of this
    /// feature's life.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_WhenTheInsertFails_DoesNotPropagate()
    {
        var repo = new StubAlertRepository
        {
            ThrowOnCreate = new InvalidOperationException(
                "Cannot insert the value NULL into column 'EntityId'")
        };

        await BuildSut(repo).EvaluateAsync(Tenant, Snapshot(analysesUsed: 82), CancellationToken.None);

        Assert.Equal(1, repo.CreateCallCount);
    }

    [Fact]
    public async Task EvaluateAsync_WhenTheDedupReadFails_DoesNotPropagate()
    {
        var repo = new StubAlertRepository { ThrowOnExists = new TimeoutException("db down") };

        await BuildSut(repo).EvaluateAsync(Tenant, Snapshot(analysesUsed: 82), CancellationToken.None);

        Assert.Empty(repo.Created);
    }

    [Fact]
    public async Task EvaluateAsync_WithoutATenant_EmitsNothing()
    {
        var repo = new StubAlertRepository();

        await BuildSut(repo).EvaluateAsync("  ", Snapshot(analysesUsed: 82), CancellationToken.None);

        Assert.Empty(repo.Created);
        Assert.Equal(0, repo.ExistsCallCount);
    }

    // ── Per-user notification fan-out ───────────────────────────────────────────

    /// <summary>
    /// The core of the feature: ONE alert becomes one notification PER USER. This is the whole
    /// difference from alerts — three analysts get three unread badges and three independent read
    /// states, not one shared row.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_FansOneNotificationOutPerUser()
    {
        var alerts        = new StubAlertRepository();
        var notifications = new StubNotificationRepository();
        var users         = new StubUserRepository();

        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var userC = Guid.NewGuid();
        users.ActiveUsers.AddRange([ActiveUser(userA), ActiveUser(userB), ActiveUser(userC)]);

        await BuildSut(alerts, notifications: notifications, users: users)
            .EvaluateAsync(Tenant, Snapshot(analysesUsed: 82), CancellationToken.None);

        Assert.Single(alerts.Created);
        Assert.Equal(3, notifications.Created.Count);
        Assert.Equal(
            new[] { userA, userB, userC }.OrderBy(x => x),
            notifications.Created.Select(n => n.UserId).OrderBy(x => x));
        Assert.Equal(Tenant, users.LastTenantId);
    }

    /// <summary>
    /// Type carries the alert's own PER-TIER AlertType, not a shared 'BILLING_QUOTA' constant, and
    /// SourceType/SourceId point back at the alert that caused it. The per-tier value is what makes
    /// the dedup check below able to match at all, since that query filters on the stored Type.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_NotificationCarriesThePerTierTypeAndPointsAtItsAlert()
    {
        var alerts        = new StubAlertRepository();
        var notifications = new StubNotificationRepository();
        var users         = new StubUserRepository();
        users.ActiveUsers.Add(ActiveUser(Guid.NewGuid()));

        await BuildSut(alerts, notifications: notifications, users: users)
            .EvaluateAsync(Tenant, Snapshot(analysesUsed: 100), CancellationToken.None);

        var alert        = Assert.Single(alerts.Created);
        var notification = Assert.Single(notifications.Created);

        Assert.Equal(BillingAlertService.AnalysisQuota100Type, notification.Type);
        Assert.Equal(alert.AlertType,   notification.Type);
        Assert.Equal(alert.Title,       notification.Title);
        Assert.Equal(alert.Description, notification.Body);
        Assert.Equal("Alert",           notification.SourceType);
        Assert.Equal(alert.AlertId,     notification.SourceId);
        Assert.Equal(Tenant,            notification.TenantId);
        Assert.False(notification.IsRead);
        Assert.NotEqual(Guid.Empty, notification.NotificationId);
    }

    /// <summary>
    /// The per-user dedup guard, for the case the outer one cannot cover: the notification write
    /// failed after the alerts were committed, and a later poll emits a different tier. The user
    /// who already has the 80% notification must not receive a second one.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_SkipsUsersAlreadyNotifiedForThatTypeInThePeriod()
    {
        var alerts        = new StubAlertRepository();
        var notifications = new StubNotificationRepository();
        var users         = new StubUserRepository();

        var alreadyNotified = Guid.NewGuid();
        var freshUser       = Guid.NewGuid();
        users.ActiveUsers.AddRange([ActiveUser(alreadyNotified), ActiveUser(freshUser)]);
        notifications.ExistingForUser.Add((alreadyNotified, BillingAlertService.AnalysisQuota80Type));

        await BuildSut(alerts, notifications: notifications, users: users)
            .EvaluateAsync(Tenant, Snapshot(analysesUsed: 82), CancellationToken.None);

        var notification = Assert.Single(notifications.Created);
        Assert.Equal(freshUser, notification.UserId);

        // The window handed to the dedup query is the snapshot's own month, half-open.
        Assert.Equal(Tenant,      notifications.LastExistsTenantId);
        Assert.Equal(PeriodStart, notifications.LastExistsPeriodStart);
        Assert.Equal(PeriodEnd,   notifications.LastExistsPeriodEnd);
    }

    /// <summary>
    /// When every user has already been notified, nothing is inserted at all — the batch is not
    /// merely empty, CreateManyAsync is never called.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_WhenEveryUserIsAlreadyNotified_InsertsNothing()
    {
        var alerts        = new StubAlertRepository();
        var notifications = new StubNotificationRepository();
        var users         = new StubUserRepository();

        var userId = Guid.NewGuid();
        users.ActiveUsers.Add(ActiveUser(userId));
        notifications.ExistingForUser.Add((userId, BillingAlertService.AnalysisQuota80Type));

        await BuildSut(alerts, notifications: notifications, users: users)
            .EvaluateAsync(Tenant, Snapshot(analysesUsed: 82), CancellationToken.None);

        Assert.Single(alerts.Created);
        Assert.Empty(notifications.Created);
        Assert.Equal(0, notifications.CreateCallCount);
    }

    /// <summary>
    /// A tenant with no active users costs nothing: the fan-out returns before it asks the dedup
    /// question even once.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_WithNoActiveUsers_DoesNotTouchTheNotificationRepository()
    {
        var alerts        = new StubAlertRepository();
        var notifications = new StubNotificationRepository();

        await BuildSut(alerts, notifications: notifications, users: new StubUserRepository())
            .EvaluateAsync(Tenant, Snapshot(analysesUsed: 82), CancellationToken.None);

        Assert.Single(alerts.Created);
        Assert.Equal(0, notifications.ExistsCallCount);
        Assert.Equal(0, notifications.CreateCallCount);
    }

    /// <summary>
    /// THE fault-isolation guarantee for this feature. The likeliest failure in its first days is
    /// script 049 not having been run, which makes the insert throw. When that happens the alerts
    /// must still be written — notifications are secondary to them — and nothing may reach the GET.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_WhenTheNotificationInsertFails_AlertsAreStillEmitted()
    {
        var alerts        = new StubAlertRepository();
        var notifications = new StubNotificationRepository
        {
            ThrowOnCreate = new InvalidOperationException(
                "Invalid object name 'dbo.Notifications'")
        };
        var users = new StubUserRepository();
        users.ActiveUsers.Add(ActiveUser(Guid.NewGuid()));

        await BuildSut(alerts, notifications: notifications, users: users)
            .EvaluateAsync(Tenant, Snapshot(analysesUsed: 82), CancellationToken.None);

        Assert.Single(alerts.Created);
        Assert.Equal(1, alerts.CreateCallCount);
    }

    [Fact]
    public async Task EvaluateAsync_WhenTheNotificationDedupReadFails_AlertsAreStillEmitted()
    {
        var alerts        = new StubAlertRepository();
        var notifications = new StubNotificationRepository
        {
            ThrowOnExists = new TimeoutException("db down")
        };
        var users = new StubUserRepository();
        users.ActiveUsers.Add(ActiveUser(Guid.NewGuid()));

        await BuildSut(alerts, notifications: notifications, users: users)
            .EvaluateAsync(Tenant, Snapshot(analysesUsed: 82), CancellationToken.None);

        Assert.Single(alerts.Created);
        Assert.Empty(notifications.Created);
    }

    [Fact]
    public async Task EvaluateAsync_WhenTheUserLookupFails_AlertsAreStillEmitted()
    {
        var alerts        = new StubAlertRepository();
        var notifications = new StubNotificationRepository();
        var users         = new StubUserRepository
        {
            ThrowOnGetActive = new TimeoutException("db down")
        };

        await BuildSut(alerts, notifications: notifications, users: users)
            .EvaluateAsync(Tenant, Snapshot(analysesUsed: 82), CancellationToken.None);

        Assert.Single(alerts.Created);
        Assert.Equal(0, notifications.CreateCallCount);
    }

    /// <summary>
    /// Below the warning tier no alert is due, so the fan-out is never reached. This is the common
    /// case on an endpoint the Portal polls every 30 seconds, and it must cost nothing.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_BelowTheWarningTier_DoesNotFanOut()
    {
        var alerts        = new StubAlertRepository();
        var notifications = new StubNotificationRepository();
        var users         = new StubUserRepository();
        users.ActiveUsers.Add(ActiveUser(Guid.NewGuid()));

        await BuildSut(alerts, notifications: notifications, users: users)
            .EvaluateAsync(Tenant, Snapshot(analysesUsed: 10), CancellationToken.None);

        Assert.Empty(alerts.Created);
        Assert.Null(users.LastTenantId);
        Assert.Equal(0, notifications.ExistsCallCount);
    }
}
