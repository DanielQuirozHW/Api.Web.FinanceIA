// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-31

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Clases.Filters;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// Covers <see cref="BillingQuotaFilter"/> — the gate itself.
///
/// <para>
/// This file exists because the controller tests cannot cover it: they invoke
/// <c>TriggerAsync</c> / <c>ChatAsync</c> as plain methods, and a filter never runs on that path.
/// The companion <c>BillingQuotaAttributeTests</c> proves the attribute is actually present on
/// those actions; together the two say what one integration test would.
/// </para>
///
/// <para>
/// The tests that matter most are the fail-open ones. Blocking a paying tenant because a database
/// read blipped is a worse failure than letting one request over the line, and that judgement is
/// only visible as a test.
/// </para>
/// </summary>
public sealed class BillingQuotaFilterTests
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
        public Exception? ThrowOnCall { get; set; }

        public Task<BillingUsageSnapshot> GetSnapshotAsync(string tenantId, CancellationToken ct)
        {
            if (ThrowOnCall is not null) return Task.FromException<BillingUsageSnapshot>(ThrowOnCall);
            return Task.FromResult(SnapshotToReturn!);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static BillingUsageSnapshot Snapshot(
        int analysesUsed = 0,
        int analysesLimit = 100,
        long chatTokensUsed = 0L,
        long chatTokensLimit = 4_000_000L,
        BillingPlanSource planSource = BillingPlanSource.Configured,
        bool analysesReadFailed = false,
        bool chatTokensReadFailed = false) => new()
    {
        PlanName             = "100 análisis",
        PeriodStart          = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
        PeriodEnd            = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
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

    private static BillingQuotaFilter BuildSut(
        BillingQuotaKind kind,
        StubBillingUsageService usage,
        bool enforceChatTokens = false,
        bool withTenant = true)
        => new(
            kind,
            new StubTenantContextAccessor
            {
                TenantContext = withTenant ? BuildTenant() : null
            },
            usage,
            Options.Create(new BillingOptions { EnforceChatTokens = enforceChatTokens }),
            NullLogger<BillingQuotaFilter>.Instance);

    private static ActionExecutingContext NewExecutingContext()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/api/analyses";

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());

        return new ActionExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            controller: new object());
    }

    /// <summary>
    /// Runs the filter and reports whether the action would have executed. A filter that
    /// short-circuits sets <c>context.Result</c> and never calls next.
    /// </summary>
    private static async Task<(bool NextCalled, ActionExecutingContext Context)> RunAsync(BillingQuotaFilter sut)
    {
        var context = NewExecutingContext();
        var nextCalled = false;

        await sut.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(
                context, new List<IFilterMetadata>(), controller: new object()));
        });

        return (nextCalled, context);
    }

    private static ProblemDetails AssertQuotaRejection(ActionExecutingContext context)
    {
        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, result.StatusCode);
        Assert.Contains("application/problem+json", result.ContentTypes);

        var problem = Assert.IsType<ProblemDetails>(result.Value);
        Assert.Equal(StatusCodes.Status429TooManyRequests, problem.Status);
        return problem;
    }

    // ── Under the limit ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Analysis_WhenUnderTheLimit_LetsTheActionRun()
    {
        var usage = new StubBillingUsageService { SnapshotToReturn = Snapshot(analysesUsed: 49, analysesLimit: 50) };

        var (nextCalled, context) = await RunAsync(BuildSut(BillingQuotaKind.Analysis, usage));

        Assert.True(nextCalled);
        Assert.Null(context.Result);
    }

    // ── At and over the limit ───────────────────────────────────────────────────

    /// <summary>
    /// Exactly at the limit BLOCKS. The 50th analysis of a 50 plan is the last one allowed, so by
    /// the time used == limit the allowance is spent — this is the off-by-one that decides whether
    /// a plan sells 50 analyses or 51.
    /// </summary>
    [Fact]
    public async Task Analysis_WhenExactlyAtTheLimit_Rejects()
    {
        var usage = new StubBillingUsageService { SnapshotToReturn = Snapshot(analysesUsed: 50, analysesLimit: 50) };

        var (nextCalled, context) = await RunAsync(BuildSut(BillingQuotaKind.Analysis, usage));

        Assert.False(nextCalled);
        AssertQuotaRejection(context);
    }

    [Fact]
    public async Task Analysis_WhenOverTheLimit_Rejects()
    {
        var usage = new StubBillingUsageService { SnapshotToReturn = Snapshot(analysesUsed: 51, analysesLimit: 50) };

        var (nextCalled, _) = await RunAsync(BuildSut(BillingQuotaKind.Analysis, usage));

        Assert.False(nextCalled);
    }

    [Fact]
    public async Task Analysis_WhenRejected_CarriesTheErrorCodeNumbersAndPeriodForThePortal()
    {
        var usage = new StubBillingUsageService { SnapshotToReturn = Snapshot(analysesUsed: 50, analysesLimit: 50) };

        var (_, context) = await RunAsync(BuildSut(BillingQuotaKind.Analysis, usage));

        var problem = AssertQuotaRejection(context);
        Assert.Equal("ANALYSIS_QUOTA_EXCEEDED", problem.Extensions["errorCode"]);
        Assert.Equal(50L, problem.Extensions["used"]);
        Assert.Equal(50L, problem.Extensions["limit"]);
        Assert.Equal("100 análisis", problem.Extensions["planName"]);
        Assert.Equal(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), problem.Extensions["periodEnd"]);
        Assert.Equal("https://serviciosfinancieros.api/errors/analysis_quota_exceeded", problem.Type);
        Assert.Equal("Alcanzaste el límite de análisis de tu plan este mes.", problem.Detail);
    }

    // ── Fail open ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The case phase 1 flagged for this PR: a failed config read resolves to the smallest plan,
    /// and enforcing that would tell a 200-plan customer they are out of quota at 50.
    /// </summary>
    [Fact]
    public async Task Analysis_WhenThePlanReadFailed_FailsOpenEvenThoughTheFallbackLimitIsExceeded()
    {
        var usage = new StubBillingUsageService
        {
            SnapshotToReturn = Snapshot(analysesUsed: 120, analysesLimit: 50, planSource: BillingPlanSource.ReadFailed)
        };

        var (nextCalled, context) = await RunAsync(BuildSut(BillingQuotaKind.Analysis, usage));

        Assert.True(nextCalled);
        Assert.Null(context.Result);
    }

    [Fact]
    public async Task Analysis_WhenTheAnalysisCountFailed_FailsOpen()
    {
        var usage = new StubBillingUsageService
        {
            SnapshotToReturn = Snapshot(analysesUsed: 0, analysesLimit: 50, analysesReadFailed: true)
        };

        var (nextCalled, _) = await RunAsync(BuildSut(BillingQuotaKind.Analysis, usage));

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Analysis_WhenTheSnapshotReadThrows_FailsOpenAndDoesNotPropagate()
    {
        var usage = new StubBillingUsageService { ThrowOnCall = new TimeoutException("transient") };

        var (nextCalled, context) = await RunAsync(BuildSut(BillingQuotaKind.Analysis, usage));

        Assert.True(nextCalled);
        Assert.Null(context.Result);
    }

    /// <summary>
    /// Degradation is per-source. A broken chat meter — the likely state until script 043 runs —
    /// must not switch off the analysis quota, which is the half of the feature that actually
    /// blocks.
    /// </summary>
    [Fact]
    public async Task Analysis_WhenOnlyTheChatReadFailed_StillEnforces()
    {
        var usage = new StubBillingUsageService
        {
            SnapshotToReturn = Snapshot(analysesUsed: 50, analysesLimit: 50, chatTokensReadFailed: true)
        };

        var (nextCalled, context) = await RunAsync(BuildSut(BillingQuotaKind.Analysis, usage));

        Assert.False(nextCalled);
        AssertQuotaRejection(context);
    }

    /// <summary>
    /// The filter does not invent a 500 for a missing tenant; the action's own ResolveTenant owns
    /// that, and duplicating it would give one fault two shapes.
    /// </summary>
    [Fact]
    public async Task WhenTenantContextIsMissing_LetsTheActionRunAndReportItsOwnError()
    {
        var usage = new StubBillingUsageService { ThrowOnCall = new InvalidOperationException("must not be called") };

        var (nextCalled, context) = await RunAsync(
            BuildSut(BillingQuotaKind.Analysis, usage, withTenant: false));

        Assert.True(nextCalled);
        Assert.Null(context.Result);
    }

    // ── Chat: measured, not enforced ────────────────────────────────────────────

    [Fact]
    public async Task ChatTokens_WhenUnderTheLimit_LetsTheActionRun()
    {
        var usage = new StubBillingUsageService
        {
            SnapshotToReturn = Snapshot(chatTokensUsed: 1_000L, chatTokensLimit: 4_000_000L)
        };

        var (nextCalled, _) = await RunAsync(BuildSut(BillingQuotaKind.ChatTokens, usage));

        Assert.True(nextCalled);
    }

    /// <summary>
    /// Over the limit but the switch is off: measured and logged, never blocked. This is what
    /// makes it safe to ship a gate against token figures product has not agreed.
    /// </summary>
    [Fact]
    public async Task ChatTokens_WhenOverTheLimitAndEnforcementIsOff_LetsTheActionRun()
    {
        var usage = new StubBillingUsageService
        {
            SnapshotToReturn = Snapshot(chatTokensUsed: 9_000_000L, chatTokensLimit: 4_000_000L)
        };

        var (nextCalled, context) = await RunAsync(
            BuildSut(BillingQuotaKind.ChatTokens, usage, enforceChatTokens: false));

        Assert.True(nextCalled);
        Assert.Null(context.Result);
    }

    [Fact]
    public async Task ChatTokens_WhenOverTheLimitAndEnforcementIsOn_Rejects()
    {
        var usage = new StubBillingUsageService
        {
            SnapshotToReturn = Snapshot(chatTokensUsed: 9_000_000L, chatTokensLimit: 4_000_000L)
        };

        var (nextCalled, context) = await RunAsync(
            BuildSut(BillingQuotaKind.ChatTokens, usage, enforceChatTokens: true));

        Assert.False(nextCalled);
        var problem = AssertQuotaRejection(context);
        Assert.Equal("CHAT_QUOTA_EXCEEDED", problem.Extensions["errorCode"]);
        Assert.Equal("Alcanzaste el límite de consultas de tu plan este mes.", problem.Detail);
        Assert.Equal(9_000_000L, problem.Extensions["used"]);
    }

    [Fact]
    public async Task ChatTokens_WhenExactlyAtTheLimitAndEnforcementIsOn_Rejects()
    {
        var usage = new StubBillingUsageService
        {
            SnapshotToReturn = Snapshot(chatTokensUsed: 4_000_000L, chatTokensLimit: 4_000_000L)
        };

        var (nextCalled, _) = await RunAsync(
            BuildSut(BillingQuotaKind.ChatTokens, usage, enforceChatTokens: true));

        Assert.False(nextCalled);
    }

    [Fact]
    public async Task ChatTokens_WhenTheChatReadFailed_FailsOpenEvenWithEnforcementOn()
    {
        var usage = new StubBillingUsageService
        {
            SnapshotToReturn = Snapshot(
                chatTokensUsed: 9_000_000L, chatTokensLimit: 4_000_000L, chatTokensReadFailed: true)
        };

        var (nextCalled, _) = await RunAsync(
            BuildSut(BillingQuotaKind.ChatTokens, usage, enforceChatTokens: true));

        Assert.True(nextCalled);
    }
}
