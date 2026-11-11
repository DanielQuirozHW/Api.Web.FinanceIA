// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-31

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Covers <see cref="BillingController"/>: the projection from the internal snapshot onto the API
/// shape the Portal consumes.
///
/// <para>
/// The projection is worth testing rather than trusting because two of its fields exist to stop a
/// specific misreading: both enums must go out as STRINGS (no JsonStringEnumConverter is
/// registered on this API, so serializing the snapshot directly would send the Portal bare
/// integers), and a degraded snapshot must still return 200 with the numbers plus a marker,
/// rather than blanking the widget.
/// </para>
/// </summary>
public sealed class BillingControllerTests
{
    private const string Tenant = "tenant-1";

    // ── Stubs ───────────────────────────────────────────────────────────────────

    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; }
    }

    private sealed class StubBillingUsageService : IBillingUsageService
    {
        public BillingUsageSnapshot? SnapshotToReturn { get; set; }
        public string? TenantIdSeen { get; private set; }

        public Task<BillingUsageSnapshot> GetSnapshotAsync(string tenantId, CancellationToken ct)
        {
            TenantIdSeen = tenantId;
            return Task.FromResult(SnapshotToReturn!);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static BillingUsageSnapshot Snapshot(
        int analysesUsed = 40,
        int analysesLimit = 100,
        long chatTokensUsed = 1_000_000L,
        long chatTokensLimit = 4_000_000L,
        int chatContextBudgetTokens = 60_000,
        IReadOnlyList<string>? chatEnabledModels = null,
        BillingPlanSource planSource = BillingPlanSource.Configured,
        ChatTokenSource chatTokensSource = ChatTokenSource.ApiCallLogs,
        bool analysesReadFailed = false,
        bool chatTokensReadFailed = false) => new()
    {
        PlanName             = "100 análisis",
        PeriodStart          = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
        PeriodEnd            = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        PlanSource           = planSource,
        ChatTokensSource     = chatTokensSource,
        AnalysesReadFailed   = analysesReadFailed,
        ChatTokensReadFailed = chatTokensReadFailed,
        AnalysesUsed         = analysesUsed,
        AnalysesLimit        = analysesLimit,
        ChatTokensUsed       = chatTokensUsed,
        ChatTokensLimit      = chatTokensLimit,
        ChatContextBudgetTokens = chatContextBudgetTokens,
        ChatEnabledModels       = chatEnabledModels ?? ChatModelCatalog.AllIds
    };

    /// <summary>
    /// TenantContext declares four required members, not just TenantId. Mirrors the helper in
    /// AnalysisControllerTriggerGuardTests and eight other test files.
    /// </summary>
    private static TenantContext BuildTenant() => new()
    {
        TenantId               = Tenant,
        FirebaseProjectId      = "proj",
        FirebaseCredentialJson = "{}",
        RecaptchaSecretKey     = "secret"
    };

    /// <summary>
    /// Records what the endpoint handed the alert emitter. Never throws: the real service owns its
    /// own try/catch and promises the controller nothing to catch, so a throwing stub would be
    /// asserting a contract that does not exist.
    /// </summary>
    private sealed class StubBillingAlertService : IBillingAlertService
    {
        public int CallCount { get; private set; }
        public string? LastTenantId { get; private set; }
        public BillingUsageSnapshot? LastSnapshot { get; private set; }

        public Task EvaluateAsync(string tenantId, BillingUsageSnapshot snapshot, CancellationToken ct)
        {
            CallCount++;
            LastTenantId = tenantId;
            LastSnapshot = snapshot;
            return Task.CompletedTask;
        }
    }

    private sealed class StubBillingConfigService : ITenantBillingConfigService
    {
        public Task<BillingPlanResolution> GetEffectiveAsync(string tenantId, CancellationToken ct)
            => Task.FromResult(new BillingPlanResolution(BillingPlans.Minimum, BillingPlanSource.NoConfiguration));

        public Task<TenantBillingConfig> UpdateAsync(
            string tenantId, BillingPlan plan, bool isActive, string updatedBy, CancellationToken ct)
            => throw new NotSupportedException("GET usage must not write billing configuration.");
    }

    private sealed class StubAuditService : IAuditService
    {
        public Task LogAsync(
            string tenantId, Guid? userId, string action, string severity = "Info",
            string? entityType = null, string? entityId = null, string? ipAddress = null,
            string? requestId = null, string? oldValueJson = null, string? newValueJson = null,
            CancellationToken ct = default)
            => Task.CompletedTask;

        public Task LogSessionAsync(
            Guid tenantId, Guid? userId, string email, string eventType,
            string? ipAddress, string? userAgent, string? failureReason = null,
            CancellationToken ct = default)
            => Task.CompletedTask;
    }

    /// <summary>
    /// Stands in for the scoring service's level-cutoff read. The engine's own resolution is pinned
    /// against a real config table in <c>ScoringServiceLevelCutoffsTests</c>; here it only has to
    /// return something, or fail on demand.
    /// </summary>
    private sealed class StubScoringService : Api.Web.FinanceIA.Servicios.IScoringService
    {
        public ScoreLevelCutoffs CutoffsToReturn { get; set; } = new(35m, 60m, 80m);
        public Exception? Throws { get; set; }

        public Task<ScoringResult> CalculateAsync(
            string tenantId, string? entitySector, RatiosData ratios, BcraData? bcraData,
            BcraMacroData? macroData, List<Api.Web.FinanceIA.Models.Lufe.LufeVentaCompraMes>? ventasMensuales,
            List<CasfogGarantiaSgr>? garantiasPorSgr, CancellationToken ct)
            => throw new NotSupportedException("GET usage must not score anything.");

        public Task<ScoreLevelCutoffs> GetLevelCutoffsAsync(string tenantId, CancellationToken ct)
            => Throws is not null ? Task.FromException<ScoreLevelCutoffs>(Throws) : Task.FromResult(CutoffsToReturn);
    }

    private static BillingController BuildSut(
        StubBillingUsageService usage,
        bool enforceChatTokens = false,
        bool withTenant = true,
        StubBillingAlertService? alerts = null,
        StubScoringService? scoring = null)
    {
        var sut = new BillingController(
            new StubTenantContextAccessor
            {
                TenantContext = withTenant ? BuildTenant() : null
            },
            usage,
            alerts ?? new StubBillingAlertService(),
            // The config write path is not exercised here; see BillingControllerConfigTests.
            new StubBillingConfigService(),
            new StubAuditService(),
            Options.Create(new BillingOptions { EnforceChatTokens = enforceChatTokens }),
            scoring ?? new StubScoringService(),
            NullLogger<BillingController>.Instance);

        sut.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return sut;
    }

    private static async Task<BillingUsageResponse> GetAsync(BillingController sut)
    {
        var result = await sut.GetUsageAsync(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<BillingUsageResponse>(ok.Value);
    }

    // ── Projection ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetUsageAsync_ReturnsBothCountersWithUsedLimitRemainingAndPercent()
    {
        var usage = new StubBillingUsageService { SnapshotToReturn = Snapshot() };

        var body = await GetAsync(BuildSut(usage));

        Assert.Equal("100 análisis", body.PlanName);
        Assert.Equal(40, body.AnalysesUsed);
        Assert.Equal(100, body.AnalysesLimit);
        Assert.Equal(60, body.AnalysesRemaining);
        Assert.Equal(40m, body.AnalysesPercent);
        Assert.Equal(1_000_000L, body.ChatTokensUsed);
        Assert.Equal(4_000_000L, body.ChatTokensLimit);
        Assert.Equal(3_000_000L, body.ChatTokensRemaining);
        Assert.Equal(25m, body.ChatTokensPercent);
    }

    /// <summary>
    /// The per-conversation context budget surfaces here so any authenticated tenant user can read
    /// the denominator without the SuperAdmin write endpoint. It arrives alone, with no used /
    /// remaining / percent companions: it is a ceiling on one prompt, not a pool spent over the
    /// month, so there is nothing for this endpoint to count against it.
    /// </summary>
    [Theory]
    [InlineData(40_000)]
    [InlineData(60_000)]
    [InlineData(80_000)]
    public async Task GetUsageAsync_ReturnsTheChatContextBudget(int budget)
    {
        var usage = new StubBillingUsageService
        {
            SnapshotToReturn = Snapshot(chatContextBudgetTokens: budget)
        };

        var body = await GetAsync(BuildSut(usage));

        Assert.Equal(budget, body.ChatContextBudgetTokens);
    }

    // ── Chat model allow-list ───────────────────────────────────────────────────
    //
    // This endpoint is how the Portal learns which models to offer in its per-conversation selector.
    // It rides here rather than behind the SuperAdmin write endpoint deliberately: a client already
    // polls this for the quota banner, and reading your own entitlement should not require the role
    // that changes it.

    [Fact]
    public async Task GetUsageAsync_ReturnsTheTenantsEnabledChatModels()
    {
        var usage = new StubBillingUsageService
        {
            SnapshotToReturn = Snapshot(chatEnabledModels: ["claude-haiku-4-5", "claude-sonnet-4-6"])
        };

        var body = await GetAsync(BuildSut(usage));

        Assert.Equal(new[] { "claude-haiku-4-5", "claude-sonnet-4-6" }, body.ChatEnabledModels);
    }

    [Fact]
    public async Task GetUsageAsync_ReturnsTheWholeCatalogueForAnUnconfiguredTenant()
    {
        // A tenant with no TenantBillingConfig row resolves to BillingPlans.Minimum, whose model list
        // is the full catalogue — unlike its ANALYSIS limit, which is deliberately the smallest. The
        // asymmetry is the point: a capability the tenant already had when the model was hardcoded
        // must not be taken away by the mere absence of a row.
        var usage = new StubBillingUsageService
        {
            SnapshotToReturn = Snapshot(planSource: BillingPlanSource.NoConfiguration)
        };

        var body = await GetAsync(BuildSut(usage));

        Assert.Equal(ChatModelCatalog.AllIds, body.ChatEnabledModels);
    }

    [Fact]
    public async Task GetUsageAsync_StillReportsModelsWhenThePlanReadFailed()
    {
        // Fail-open, and the response is still a 200 so the Portal does not blank its widget. The
        // numbers are flagged degraded; the model list is not degraded in any useful sense, because
        // the fallback plan carries every model — a database blip must not cost the analyst their
        // model picker.
        var usage = new StubBillingUsageService
        {
            SnapshotToReturn = Snapshot(planSource: BillingPlanSource.ReadFailed)
        };

        var body = await GetAsync(BuildSut(usage));

        Assert.True(body.IsDegraded);
        Assert.Equal(ChatModelCatalog.AllIds, body.ChatEnabledModels);
    }

    [Fact]
    public async Task GetUsageAsync_ReportsTheModelsInTheCataloguesOrder()
    {
        // The FIRST id is the model a chat turn falls back to when it names none, so this endpoint
        // must report the same order the server resolves against. It is NOT what a client renders
        // pre-selected — that is DefaultChatModel, covered below.
        var usage = new StubBillingUsageService { SnapshotToReturn = Snapshot() };

        var body = await GetAsync(BuildSut(usage));

        Assert.Equal("claude-haiku-4-5", body.ChatEnabledModels[0]);
        Assert.NotEmpty(body.ChatEnabledModels);
    }

    [Fact]
    public async Task GetUsageAsync_ReturnsTheProductDefaultAsThePreselectedModel()
    {
        // The regression guard for the reported bug: the Portal used to infer the pre-selection from
        // array position and land on Haiku, because the catalogue is ordered cheapest-first. The
        // endpoint now states the product default, which is deliberately NOT ChatEnabledModels[0].
        var usage = new StubBillingUsageService { SnapshotToReturn = Snapshot() };

        var body = await GetAsync(BuildSut(usage));

        Assert.Equal(ChatModelCatalog.Default.Id, body.DefaultChatModel);
        Assert.NotEqual(body.ChatEnabledModels[0], body.DefaultChatModel);
    }

    [Fact]
    public async Task GetUsageAsync_FallsBackToTheFirstEnabledModelWhenTheDefaultIsNotEntitled()
    {
        // A tenant whose plan excludes the product default must not be handed it: the selector could
        // not offer it, and a turn naming it would be silently replaced anyway.
        var usage = new StubBillingUsageService
        {
            SnapshotToReturn = Snapshot(chatEnabledModels: ["claude-haiku-4-5"])
        };

        var body = await GetAsync(BuildSut(usage));

        Assert.Equal("claude-haiku-4-5", body.DefaultChatModel);
    }

    [Theory]
    [InlineData(BillingPlanSource.Configured)]
    [InlineData(BillingPlanSource.NoConfiguration)]
    [InlineData(BillingPlanSource.ReadFailed)]
    public async Task GetUsageAsync_DefaultChatModelIsAlwaysOneOfTheEnabledModels(BillingPlanSource planSource)
    {
        // The invariant the Portal relies on to pre-select without validating first — it must hold on
        // the degraded path too, where the model list comes from the fallback plan.
        var usage = new StubBillingUsageService
        {
            SnapshotToReturn = Snapshot(planSource: planSource)
        };

        var body = await GetAsync(BuildSut(usage));

        Assert.Contains(body.DefaultChatModel, body.ChatEnabledModels);
    }

    [Fact]
    public async Task GetUsageAsync_DefaultChatModelIsOneOfTheEnabledModelsForANarrowedPlan()
    {
        var usage = new StubBillingUsageService
        {
            SnapshotToReturn = Snapshot(chatEnabledModels: ["claude-sonnet-5", "claude-haiku-4-5"])
        };

        var body = await GetAsync(BuildSut(usage));

        Assert.Contains(body.DefaultChatModel, body.ChatEnabledModels);
        Assert.Equal("claude-sonnet-5", body.DefaultChatModel);
    }

    [Fact]
    public async Task GetUsageAsync_ReturnsTheHalfOpenUtcPeriod()
    {
        var usage = new StubBillingUsageService { SnapshotToReturn = Snapshot() };

        var body = await GetAsync(BuildSut(usage));

        Assert.Equal(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), body.PeriodStart);
        Assert.Equal(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), body.PeriodEnd);
    }

    /// <summary>
    /// A bare integer here would reach the Portal as <c>chatTokensSource: 1</c>, which is exactly
    /// the kind of casing/shape surprise this API has burned the Portal with before.
    /// </summary>
    [Theory]
    [InlineData(ChatTokenSource.ApiCallLogs, "ApiCallLogs")]
    [InlineData(ChatTokenSource.ChatMessages, "ChatMessages")]
    public async Task GetUsageAsync_RendersTheChatTokenSourceAsAString(ChatTokenSource source, string expected)
    {
        var usage = new StubBillingUsageService { SnapshotToReturn = Snapshot(chatTokensSource: source) };

        var body = await GetAsync(BuildSut(usage));

        Assert.Equal(expected, body.ChatTokensSource);
    }

    // ── Enforcement flag passthrough ────────────────────────────────────────────

    /// <summary>
    /// The Portal must not warn an analyst about a wall that is not there. While chat enforcement
    /// is off, the numbers are reported but the limit does not block.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetUsageAsync_ReportsWhetherTheChatLimitActuallyBlocks(bool enforce)
    {
        var usage = new StubBillingUsageService { SnapshotToReturn = Snapshot() };

        var body = await GetAsync(BuildSut(usage, enforceChatTokens: enforce));

        Assert.Equal(enforce, body.ChatTokensEnforced);
    }

    // ── Degradation ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetUsageAsync_WhenNothingFailed_IsNotDegraded()
    {
        var usage = new StubBillingUsageService { SnapshotToReturn = Snapshot() };

        var body = await GetAsync(BuildSut(usage));

        Assert.False(body.IsDegraded);
        Assert.Null(body.DegradedReason);
    }

    /// <summary>
    /// 200 with a marker, not a 500. A billing widget going blank during a database blip is worse
    /// than partial numbers the client can caveat.
    /// </summary>
    [Theory]
    [InlineData(BillingPlanSource.ReadFailed, false, false, "plan_read_failed")]
    [InlineData(BillingPlanSource.Configured, true, false, "analyses_read_failed")]
    [InlineData(BillingPlanSource.Configured, false, true, "chat_tokens_read_failed")]
    [InlineData(BillingPlanSource.Configured, true, true, "analyses_and_chat_tokens_read_failed")]
    public async Task GetUsageAsync_WhenAReadFailed_Returns200WithAStableReason(
        BillingPlanSource planSource, bool analysesFailed, bool chatFailed, string expectedReason)
    {
        var usage = new StubBillingUsageService
        {
            SnapshotToReturn = Snapshot(
                planSource: planSource,
                analysesReadFailed: analysesFailed,
                chatTokensReadFailed: chatFailed)
        };

        var body = await GetAsync(BuildSut(usage));

        Assert.True(body.IsDegraded);
        Assert.Equal(expectedReason, body.DegradedReason);
    }

    // ── Tenant scope ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetUsageAsync_ScopesToTheResolvedTenant()
    {
        var usage = new StubBillingUsageService { SnapshotToReturn = Snapshot() };

        _ = await GetAsync(BuildSut(usage));

        Assert.Equal(Tenant, usage.TenantIdSeen);
    }

    [Fact]
    public async Task GetUsageAsync_WhenTenantContextIsMissing_Returns500AndNeverReadsUsage()
    {
        var usage = new StubBillingUsageService();

        var result = await BuildSut(usage, withTenant: false).GetUsageAsync(CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, problem.StatusCode);
        Assert.Null(usage.TenantIdSeen);
    }

    // ── Billing quota alert trigger ─────────────────────────────────────────────

    /// <summary>
    /// This endpoint is the trigger for the 80% / 100% plan alerts, because the Portal polls it and
    /// therefore reaches a tenant that has stopped creating analyses. The alert emitter must see the
    /// SAME snapshot the response is built from — recomputing a percentage anywhere else is how the
    /// banner and the alert start disagreeing.
    /// </summary>
    [Fact]
    public async Task GetUsageAsync_EvaluatesBillingAlertsWithTheSameSnapshotAndTenant()
    {
        var snapshot = Snapshot();
        var usage    = new StubBillingUsageService { SnapshotToReturn = snapshot };
        var alerts   = new StubBillingAlertService();

        _ = await GetAsync(BuildSut(usage, alerts: alerts));

        Assert.Equal(1, alerts.CallCount);
        Assert.Equal(Tenant, alerts.LastTenantId);
        Assert.Same(snapshot, alerts.LastSnapshot);
    }

    /// <summary>
    /// Emission is a side effect, never a precondition: an unresolved tenant short-circuits before
    /// anything is measured, so there is nothing to raise an alert about.
    /// </summary>
    [Fact]
    public async Task GetUsageAsync_WhenTenantContextIsMissing_NeverEvaluatesBillingAlerts()
    {
        var alerts = new StubBillingAlertService();

        _ = await BuildSut(new StubBillingUsageService(), withTenant: false, alerts: alerts)
            .GetUsageAsync(CancellationToken.None);

        Assert.Equal(0, alerts.CallCount);
    }

    /// <summary>
    /// The response contract for GET /usage is unchanged by the alert work — same 200, same body.
    /// Pinned because the Portal parses this shape and the emission sits directly in its path.
    /// </summary>
    [Fact]
    public async Task GetUsageAsync_ResponseIsUnaffectedByAlertEmission()
    {
        var snapshot = Snapshot();
        var usage    = new StubBillingUsageService { SnapshotToReturn = snapshot };

        var response = await GetAsync(BuildSut(usage, alerts: new StubBillingAlertService()));

        Assert.Equal(snapshot.PlanName, response.PlanName);
        Assert.Equal(snapshot.AnalysesUsed, response.AnalysesUsed);
        Assert.Equal(snapshot.AnalysesPercent, response.AnalysesPercent);
    }

    // ── Level cutoffs (score level contract) ────────────────────────────────────
    //
    // The tenant's CURRENT level cutoffs ride this endpoint because it is the one tenant read every
    // role can make. They are a fallback for rows scored before ScoreBreakdownJson carried its own.

    [Fact]
    public async Task GetUsageAsync_ReturnsTheTenantsCurrentLevelCutoffs()
    {
        var scoring = new StubScoringService { CutoffsToReturn = new ScoreLevelCutoffs(30m, 55m, 75m) };

        var body = await GetAsync(BuildSut(new StubBillingUsageService { SnapshotToReturn = Snapshot() }, scoring: scoring));

        Assert.Equal(new ScoreLevelCutoffs(30m, 55m, 75m), body.LevelCutoffs);
    }

    /// <summary>
    /// A failed cutoff read is null — never the defaults, which would mislabel a tenant with custom
    /// cutoffs — and it touches nothing else: same 200, same figures, not degraded.
    /// </summary>
    [Fact]
    public async Task GetUsageAsync_WhenTheCutoffReadFails_ReturnsNullCutoffsAndTheUsageUntouched()
    {
        var scoring = new StubScoringService { Throws = new InvalidOperationException("db down") };

        var body = await GetAsync(BuildSut(new StubBillingUsageService { SnapshotToReturn = Snapshot() }, scoring: scoring));

        Assert.Null(body.LevelCutoffs);
        Assert.False(body.IsDegraded);
        Assert.Null(body.DegradedReason);
        Assert.Equal(40, body.AnalysesUsed);
        Assert.Equal(1_000_000L, body.ChatTokensUsed);
    }

    [Fact]
    public async Task GetUsageAsync_LevelCutoffsSerialiseAsLowMediumHigh()
    {
        var body = await GetAsync(BuildSut(new StubBillingUsageService { SnapshotToReturn = Snapshot() }));

        using var doc = System.Text.Json.JsonDocument.Parse(
            System.Text.Json.JsonSerializer.Serialize(body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        var cutoffs = doc.RootElement.GetProperty("levelCutoffs");

        Assert.Equal(35m, cutoffs.GetProperty("low").GetDecimal());
        Assert.Equal(60m, cutoffs.GetProperty("medium").GetDecimal());
        Assert.Equal(80m, cutoffs.GetProperty("high").GetDecimal());
    }
}
