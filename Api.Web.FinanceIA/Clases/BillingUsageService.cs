// Project : Api.Web.FinanceIA
// Created : 2026-08-31

using Api.Web.FinanceIA.Clases.Time;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Default implementation of <see cref="IBillingUsageService"/>.
///
/// <para>
/// Registered Scoped. It holds no cache of its own: <see cref="ITenantBillingConfigService"/>
/// already memoizes the plan for the request, and the two usage counts are the numbers that
/// change, so caching them is exactly what a quota gate must not do.
/// </para>
/// </summary>
public sealed class BillingUsageService : IBillingUsageService
{
    private readonly ITenantBillingConfigService _billingConfigService;
    private readonly IAnalysisRepository         _analysisRepository;
    private readonly IUsageRepository            _usageRepository;
    private readonly ILogger<BillingUsageService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="BillingUsageService"/>.
    /// </summary>
    public BillingUsageService(
        ITenantBillingConfigService  billingConfigService,
        IAnalysisRepository          analysisRepository,
        IUsageRepository             usageRepository,
        ILogger<BillingUsageService> logger)
    {
        _billingConfigService = billingConfigService;
        _analysisRepository   = analysisRepository;
        _usageRepository      = usageRepository;
        _logger               = logger;
    }

    /// <inheritdoc />
    public async Task<BillingUsageSnapshot> GetSnapshotAsync(string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        // One "now" for the whole snapshot, resolved into one period that every read below shares.
        // Reading the clock per query could straddle a month boundary — Year read at 23:59:59.999
        // on 31 December and Month read at 00:00:00 would meter January of the wrong year — and
        // would let the analysis count and the token count describe different periods.
        //
        // The period is the ARGENTINE calendar month, not the UTC one. ArgentineTime converts
        // 00:00 ART on the 1st into the UTC instants the queries compare against: the stored
        // timestamps stay UTC, only the boundary moves. Metering on the UTC month would reset a
        // tenant's counter at 21:00 ART on the last day of the month.
        var now    = DateTime.UtcNow;
        var period = ArgentineTime.CurrentPeriod(now);

        // Never throws; its Source says whether the limits are trustworthy.
        var resolution = await _billingConfigService
            .GetEffectiveAsync(tenantId, ct)
            .ConfigureAwait(false);

        // The two counts get their OWN try blocks, and the failures are reported separately
        // rather than thrown. A chat-token read that fails must not switch off analysis
        // enforcement — and that is not hypothetical: until script 043 runs, ApiCallLogs.TenantId
        // is UNIQUEIDENTIFIER while every caller passes a Firebase slug, so the chat read can
        // fail conversion on every single request. One shared catch would have quietly disabled
        // the analysis quota for every tenant, which is the whole feature.
        //
        // Sequential, not Task.WhenAll: both reads go through the same request-scoped
        // FinanciaDbContext, which is not thread-safe. Two short reads do not justify a second
        // context.
        var analysesUsed       = 0;
        var analysesReadFailed = false;

        try
        {
            analysesUsed = await _analysisRepository
                .CountByTenantInPeriodAsync(tenantId, period.StartUtc, period.EndUtc, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to count analyses for tenant {TenantId} in {Year}-{Month:D2} (ART); the analysis quota will fail open for this request.",
                tenantId, period.Year, period.Month);

            analysesReadFailed = true;
        }

        var chatTokens       = ChatTokenUsage.Empty;
        var chatSource       = ChatTokenSource.ApiCallLogs;
        var chatReadFailed   = false;

        try
        {
            (chatTokens, chatSource) = await ResolveChatTokensAsync(tenantId, period, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to read chat token usage for tenant {TenantId} in {Year}-{Month:D2} (ART); the chat quota will fail open for this request.",
                tenantId, period.Year, period.Month);

            chatReadFailed = true;
        }

        return new BillingUsageSnapshot
        {
            PlanName             = resolution.Plan.PlanName,
            PeriodStart          = period.StartUtc,
            PeriodEnd            = period.EndUtc,
            PlanSource           = resolution.Source,
            ChatTokensSource     = chatSource,
            AnalysesReadFailed   = analysesReadFailed,
            ChatTokensReadFailed = chatReadFailed,
            AnalysesUsed         = analysesUsed,
            AnalysesLimit        = resolution.Plan.AnalysisMonthlyLimit,
            ChatTokensUsed       = chatTokens.TotalTokens,
            ChatTokensLimit      = resolution.Plan.ChatTokensMonthlyLimit,

            // Carried straight off the resolved plan, with no read of its own: it is a ceiling on
            // one conversation's prompt, not something consumed over the period, so there is no
            // usage query behind it and nothing here can fail reading it.
            ChatContextBudgetTokens = resolution.Plan.ChatContextBudgetTokens,
            ChatEnabledModels       = resolution.Plan.ChatEnabledModels
        };
    }

    /// <summary>
    /// Reads chat tokens from the api-call ledger, falling back to the chat transcript when the
    /// ledger holds no rows at all for the period.
    ///
    /// <para>
    /// The fallback is not belt-and-braces, it is load-bearing. Writes to [dbo].[ApiCallLogs] are
    /// fire-and-forget and ApiCallLogService swallows its own failures, and script 043 — which
    /// repairs the TenantId type that rejects every one of those writes — is still unexecuted per
    /// docs/database/README.md. Metering a quota off a table that may be silently empty would
    /// report every tenant at zero forever.
    /// </para>
    ///
    /// <para>
    /// The switch is on the ROW COUNT, never on the token total. Zero rows means nothing was
    /// recorded; zero tokens across real rows means the tenant genuinely did not chat, and that
    /// answer is correct and must not be second-guessed by re-reading another table.
    /// </para>
    /// </summary>
    private async Task<(ChatTokenUsage Usage, ChatTokenSource Source)> ResolveChatTokensAsync(
        string tenantId, BillingPeriod period, CancellationToken ct)
    {
        var fromLedger = await _usageRepository
            .GetChatTokensInPeriodAsync(tenantId, period.StartUtc, period.EndUtc, ct)
            .ConfigureAwait(false);

        if (fromLedger.Calls > 0)
            return (fromLedger, ChatTokenSource.ApiCallLogs);

        var fromMessages = await _usageRepository
            .GetChatTokensFromMessagesInPeriodAsync(tenantId, period.StartUtc, period.EndUtc, ct)
            .ConfigureAwait(false);

        if (fromMessages.Calls > 0)
        {
            _logger.LogWarning(
                "ApiCallLogs holds no ClaudeChat rows for tenant {TenantId} in {Year}-{Month:D2} (ART) but ChatMessages holds {Turns} assistant turns; metering chat tokens from ChatMessages. This is what an unexecuted script 043 looks like.",
                tenantId, period.Year, period.Month, fromMessages.Calls);

            return (fromMessages, ChatTokenSource.ChatMessages);
        }

        // Neither source has anything: a tenant that has not chatted this month. Report it as the
        // ledger's answer, which is the source of record when there is nothing to disagree about.
        return (ChatTokenUsage.Empty, ChatTokenSource.ApiCallLogs);
    }
}
