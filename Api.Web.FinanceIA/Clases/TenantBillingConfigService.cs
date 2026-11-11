// Project : Api.Web.FinanceIA
// Created : 2026-08-31

using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Default implementation of <see cref="ITenantBillingConfigService"/>.
/// Registered Scoped, so a single instance lives for the duration of one request; the private
/// dictionary therefore acts as a per-request read cache with no extra infrastructure, matching
/// <see cref="TenantSettingsService"/>. Cross-request caching is deliberately avoided: a plan can
/// change at any moment — from the SuperAdmin endpoint or from the DBA's SQL — and a longer-lived
/// cache would keep serving the old numbers to other instances, which nothing could invalidate.
/// Within the request, <see cref="UpdateAsync"/> drops its own tenant's entry, so a write followed
/// by a read cannot return the plan the write just replaced.
/// </summary>
public sealed class TenantBillingConfigService : ITenantBillingConfigService
{
    private readonly ITenantBillingConfigRepository      _repository;
    private readonly ILogger<TenantBillingConfigService> _logger;

    // Per-request memoization keyed by tenant id. The resolution is cached even when it is the
    // minimum default or a degraded read, so an unconfigured tenant is not re-queried within a
    // request and a failing one does not retry the same broken read on every gate.
    private readonly Dictionary<string, BillingPlanResolution> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of <see cref="TenantBillingConfigService"/>.
    /// </summary>
    public TenantBillingConfigService(
        ITenantBillingConfigRepository repository,
        ILogger<TenantBillingConfigService> logger)
    {
        _repository = repository;
        _logger     = logger;
    }

    /// <inheritdoc />
    public async Task<BillingPlanResolution> GetEffectiveAsync(string tenantId, CancellationToken ct)
    {
        if (_cache.TryGetValue(tenantId, out var cached))
            return cached;

        TenantBillingConfig? config;

        try
        {
            config = await _repository.GetActiveByTenantAsync(tenantId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Resolving billing config must never take down the caller, so a failed read still
            // yields a usable plan rather than an exception.
            //
            // What it does NOT do any more is pretend the answer is a fact. Phase 1 returned the
            // minimum plan here indistinguishably from an unconfigured tenant, and left a warning
            // that enforcement would have to revisit it: once limits gate requests, silently
            // downgrading a 200-plan tenant to 50 during a transient database fault denies
            // service to a paying customer.
            //
            // The resolution therefore carries ReadFailed, and BillingQuotaFilter FAILS OPEN on
            // it — over-serving for the length of a database blip beats refusing work someone
            // paid for. Anything that gates a request on these numbers must do the same; see
            // BillingPlanResolution.IsDegraded.
            _logger.LogWarning(
                ex,
                "Failed to read billing configuration for tenant {TenantId}; returning the in-code minimum plan {PlanName} marked as degraded (enforcement will fail open).",
                tenantId,
                BillingPlans.Minimum.PlanName);

            var degraded = new BillingPlanResolution(BillingPlans.Minimum, BillingPlanSource.ReadFailed);
            _cache[tenantId] = degraded;
            return degraded;
        }

        // The row's own columns are authoritative. PlanName rides along as a display label and is
        // deliberately NOT used to look a preset up, so a tenant priced off-preset keeps its
        // negotiated numbers.
        var resolution = config is null
            ? new BillingPlanResolution(BillingPlans.Minimum, BillingPlanSource.NoConfiguration)
            : new BillingPlanResolution(
                new BillingPlan(
                    config.PlanName,
                    config.AnalysisMonthlyLimit,
                    config.ChatTokensMonthlyLimit,
                    config.ChatContextBudgetTokens,
                    // Already sanitised by the entity: filtered to the catalogue, de-duplicated,
                    // ordered, and never empty even if the column is blank or full of unknown ids.
                    config.ChatEnabledModels),
                BillingPlanSource.Configured);

        _cache[tenantId] = resolution;
        return resolution;
    }

    /// <inheritdoc />
    public async Task<TenantBillingConfig> UpdateAsync(
        string tenantId,
        BillingPlan plan,
        bool isActive,
        string updatedBy,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(plan);

        var config = new TenantBillingConfig
        {
            TenantId                = tenantId,
            PlanName                = plan.PlanName,
            AnalysisMonthlyLimit    = plan.AnalysisMonthlyLimit,
            ChatTokensMonthlyLimit  = plan.ChatTokensMonthlyLimit,
            ChatContextBudgetTokens = plan.ChatContextBudgetTokens,
            ChatEnabledModelsRaw    = ChatModelCatalog.ToCsv(plan.ChatEnabledModels),
            IsActive                = isActive,
            UpdatedAt               = DateTime.UtcNow,
            UpdatedBy               = updatedBy
        };

        // No try/catch here on purpose. GetEffectiveAsync above swallows read failures because
        // enforcement must fail open; a write must do the opposite and let the caller turn the
        // failure into a 500, rather than report a plan change that did not happen.
        var saved = await _repository.UpsertAsync(config, ct).ConfigureAwait(false);

        // Drop the memoized resolution: this instance lives for the whole request, so a
        // GetEffectiveAsync later in the same request would otherwise return the pre-write plan.
        _cache.Remove(tenantId);

        _logger.LogInformation(
            "Billing configuration updated for tenant {TenantId} to plan {PlanName} by {UpdatedBy}.",
            tenantId,
            saved.PlanName,
            updatedBy);

        return saved;
    }
}
