// Project : Api.Web.FinanceIA
// Created : 2026-08-31

using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace Api.Web.FinanceIA.Clases.Filters;

/// <summary>
/// The filter behind <see cref="BillingQuotaAttribute"/>. Reads the tenant's usage snapshot and
/// short-circuits the action with 429 when the relevant monthly limit is already reached.
///
/// <para>
/// FAIL-OPEN IS THE POLICY HERE, and it is deliberate. If the plan or the usage counts cannot be
/// read, this filter lets the request through. Phase 1 shipped the opposite reflex — a failed
/// config read quietly resolved to the smallest plan — and flagged it for exactly this PR:
/// enforcing that fallback would turn one blipped SELECT into a paying 200-plan customer being
/// told they are out of quota at 50. Over-serving for the length of an outage is recoverable;
/// refusing work someone paid for, at the moment they need it, is not.
/// </para>
/// </summary>
public sealed class BillingQuotaFilter : IAsyncActionFilter
{
    private const string ErrorTypeBase = "https://serviciosfinancieros.api/errors/";

    private readonly BillingQuotaKind             _kind;
    private readonly ITenantContextAccessor       _tenantContextAccessor;
    private readonly IBillingUsageService         _billingUsageService;
    private readonly IOptions<BillingOptions>     _billingOptions;
    private readonly ILogger<BillingQuotaFilter>  _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="BillingQuotaFilter"/>. <paramref name="kind"/> is
    /// supplied by the attribute; the rest come from DI.
    /// </summary>
    public BillingQuotaFilter(
        BillingQuotaKind            kind,
        ITenantContextAccessor      tenantContextAccessor,
        IBillingUsageService        billingUsageService,
        IOptions<BillingOptions>    billingOptions,
        ILogger<BillingQuotaFilter> logger)
    {
        _kind                  = kind;
        _tenantContextAccessor = tenantContextAccessor;
        _billingUsageService   = billingUsageService;
        _billingOptions        = billingOptions;
        _logger                = logger;
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        // Reading the accessor here rather than passing it down is the sanctioned use: this is a
        // request-pipeline component, the same position a controller occupies. The tenant id then
        // travels to the service as a parameter, never as ambient state.
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            // Not this filter's error to report. The action's own ResolveTenant() owns the 500,
            // and duplicating it here would give the same fault two different shapes.
            await next().ConfigureAwait(false);
            return;
        }

        var tenantId = tenantContext.TenantId;

        BillingUsageSnapshot snapshot;

        try
        {
            snapshot = await _billingUsageService
                .GetSnapshotAsync(tenantId, context.HttpContext.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // FAIL OPEN. See the class remarks: a metering read that throws must not become an
            // outage for the endpoint it meters.
            _logger.LogWarning(
                ex,
                "Billing usage could not be read for tenant {TenantId}; allowing the request through without a {QuotaKind} quota check.",
                tenantId, _kind);

            await next().ConfigureAwait(false);
            return;
        }

        // Ask the snapshot rather than re-deriving the predicate here, and ask the one for THIS
        // gate: a chat-token read that failed says nothing about whether the analysis count is
        // trustworthy, and collapsing the two would let one broken table switch off both quotas.
        var unreliable = _kind switch
        {
            BillingQuotaKind.Analysis   => snapshot.AnalysisQuotaIsUnreliable,
            BillingQuotaKind.ChatTokens => snapshot.ChatQuotaIsUnreliable,
            _                           => true
        };

        if (unreliable)
        {
            // FAIL OPEN. These numbers are a fallback, not the tenant's entitlement, so they are
            // not something to refuse a request over.
            _logger.LogWarning(
                "Billing usage for tenant {TenantId} is degraded ({DegradedReason}); allowing the request through without a {QuotaKind} quota check.",
                tenantId, snapshot.DegradedReason, _kind);

            await next().ConfigureAwait(false);
            return;
        }

        var exceeded = _kind switch
        {
            BillingQuotaKind.Analysis   => snapshot.AnalysesUsed   >= snapshot.AnalysesLimit,
            BillingQuotaKind.ChatTokens => snapshot.ChatTokensUsed >= snapshot.ChatTokensLimit,
            _                           => false
        };

        if (!exceeded)
        {
            await next().ConfigureAwait(false);
            return;
        }

        if (_kind == BillingQuotaKind.ChatTokens && !_billingOptions.Value.EnforceChatTokens)
        {
            // Measured, not enforced. This log is the evidence product needs to agree the real
            // per-plan token figures before Billing:EnforceChatTokens is switched on — see
            // BillingOptions.EnforceChatTokens for why it ships off.
            _logger.LogWarning(
                "Chat token quota reached for tenant {TenantId} on plan {PlanName}: {Used}/{Limit} tokens ({Percent}%), measured from {Source}. NOT blocking — Billing:EnforceChatTokens is off.",
                tenantId, snapshot.PlanName, snapshot.ChatTokensUsed, snapshot.ChatTokensLimit,
                snapshot.ChatTokensPercent, snapshot.ChatTokensSource);

            await next().ConfigureAwait(false);
            return;
        }

        context.Result = _kind == BillingQuotaKind.Analysis
            ? BuildQuotaResult(
                context.HttpContext,
                errorCode: "ANALYSIS_QUOTA_EXCEEDED",
                typeSlug:  "analysis_quota_exceeded",
                detail:    "Alcanzaste el límite de análisis de tu plan este mes.",
                used:      snapshot.AnalysesUsed,
                limit:     snapshot.AnalysesLimit,
                snapshot:  snapshot)
            : BuildQuotaResult(
                context.HttpContext,
                errorCode: "CHAT_QUOTA_EXCEEDED",
                typeSlug:  "chat_quota_exceeded",
                detail:    "Alcanzaste el límite de consultas de tu plan este mes.",
                used:      snapshot.ChatTokensUsed,
                limit:     snapshot.ChatTokensLimit,
                snapshot:  snapshot);

        _logger.LogWarning(
            "{QuotaKind} quota exceeded for tenant {TenantId} on plan {PlanName}: {Used}/{Limit}. Request rejected with 429.",
            _kind, tenantId, snapshot.PlanName,
            _kind == BillingQuotaKind.Analysis ? snapshot.AnalysesUsed : snapshot.ChatTokensUsed,
            _kind == BillingQuotaKind.Analysis ? snapshot.AnalysesLimit : snapshot.ChatTokensLimit);
    }

    /// <summary>
    /// Builds the 429 body: a ProblemDetails carrying the same extension keys
    /// RateLimitingMiddleware writes, so the Portal parses one quota shape and not two.
    ///
    /// <para>
    /// No <c>Retry-After</c> header, unlike the rate limiter. The honest value would be the
    /// seconds left in the calendar month, and handing a client a delay measured in weeks invites
    /// a library somewhere to act on it. <c>periodEnd</c> says the same thing without that risk.
    /// </para>
    /// </summary>
    private static ObjectResult BuildQuotaResult(
        HttpContext http,
        string errorCode,
        string typeSlug,
        string detail,
        long used,
        long limit,
        BillingUsageSnapshot snapshot)
    {
        var problem = new ProblemDetails
        {
            Status   = StatusCodes.Status429TooManyRequests,
            Title    = "Too Many Requests",
            Detail   = detail,
            Type     = ErrorTypeBase + typeSlug,
            Instance = http.Request.Path
        };

        problem.Extensions["errorCode"]   = errorCode;
        problem.Extensions["used"]        = used;
        problem.Extensions["limit"]       = limit;
        problem.Extensions["planName"]    = snapshot.PlanName;
        problem.Extensions["periodStart"] = snapshot.PeriodStart;
        problem.Extensions["periodEnd"]   = snapshot.PeriodEnd;

        return new ObjectResult(problem)
        {
            StatusCode   = StatusCodes.Status429TooManyRequests,
            ContentTypes = { "application/problem+json" }
        };
    }
}
