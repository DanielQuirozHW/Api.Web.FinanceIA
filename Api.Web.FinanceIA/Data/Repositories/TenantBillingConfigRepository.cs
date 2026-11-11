// Project : Api.Web.FinanceIA
// Created : 2026-08-31

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="ITenantBillingConfigRepository"/>.
/// Reads use AsNoTracking; the write loads tracked and calls ChangeTracker.DetectChanges()
/// manually because AutoDetectChangesEnabled is disabled on <see cref="FinanciaDbContext"/>.
/// Tenant-isolated by TenantId: there is no EF global query filter in this project, so the
/// TenantId predicates below are the only thing keeping one tenant's billing configuration out
/// of another's reads — and, on the write, the only thing stopping one tenant's plan from
/// overwriting another's.
/// </summary>
public sealed class TenantBillingConfigRepository : ITenantBillingConfigRepository
{
    private readonly FinanciaDbContext                      _dbContext;
    private readonly ILogger<TenantBillingConfigRepository> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="TenantBillingConfigRepository"/>.
    /// </summary>
    public TenantBillingConfigRepository(
        FinanciaDbContext dbContext,
        ILogger<TenantBillingConfigRepository> logger)
    {
        _dbContext = dbContext;
        _logger    = logger;
    }

    /// <inheritdoc />
    public async Task<TenantBillingConfig?> GetActiveByTenantAsync(string tenantId, CancellationToken ct)
    {
        // The IsActive predicate is applied here rather than in the service so an inactive row
        // is indistinguishable from an absent one to every caller — both mean "no configuration",
        // and both resolve to the in-code minimum plan.
        var config = await _dbContext.TenantBillingConfigs
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.IsActive)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (config is null)
        {
            // Debug, not Warning: an unconfigured tenant is the expected default state while
            // billing rolls out, not an anomaly. It is logged at all because "why is this tenant
            // on the minimum plan?" is the first question anyone will ask of this feature.
            _logger.LogDebug(
                "No active billing configuration for tenant {TenantId}; the caller will apply the in-code minimum plan.",
                tenantId);
        }

        return config;
    }

    /// <inheritdoc />
    public async Task<TenantBillingConfig> UpsertAsync(TenantBillingConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);

        // Tracked, and WITHOUT the IsActive filter that GetActiveByTenantAsync applies. A switched-off
        // row is still the tenant's one row under UQ_TenantBillingConfig_Tenant; reusing the
        // active-only read here would miss it and INSERT a duplicate that the constraint rejects.
        var existing = await _dbContext.TenantBillingConfigs
            .FirstOrDefaultAsync(x => x.TenantId == config.TenantId, ct)
            .ConfigureAwait(false);

        var isInsert = existing is null;

        if (existing is null)
        {
            // BillingConfigId is left unset: the property is ValueGeneratedOnAdd, so EF omits the
            // column and the store's DF_TenantBillingConfig_BillingConfigId (NEWSEQUENTIALID())
            // assigns it. IsActive, by contrast, MUST be set by the caller — the column default is
            // deliberately unmapped, so bool's CLR default would send 0 and never fall back to it.
            _dbContext.TenantBillingConfigs.Add(config);
            existing = config;
        }
        else
        {
            // Every mapped column is copied here by hand. A column added to the entity and forgotten
            // in this block is not a compile error — it is an UPDATE that silently keeps the old
            // value — so a new column belongs in this list at the same time as it reaches the entity.
            existing.PlanName                = config.PlanName;
            existing.AnalysisMonthlyLimit    = config.AnalysisMonthlyLimit;
            existing.ChatTokensMonthlyLimit  = config.ChatTokensMonthlyLimit;
            existing.ChatContextBudgetTokens = config.ChatContextBudgetTokens;
            existing.ChatEnabledModelsRaw    = config.ChatEnabledModelsRaw;
            existing.IsActive                = config.IsActive;
            existing.UpdatedAt               = config.UpdatedAt;
            existing.UpdatedBy               = config.UpdatedBy;
        }

        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Billing configuration {Operation} for tenant {TenantId}: plan {PlanName}, " +
            "{AnalysisLimit} analyses/month, {ChatTokenLimit} chat tokens/month, " +
            "{ChatContextBudget} tokens per chat context, chat models [{ChatEnabledModels}], " +
            "active={IsActive}.",
            isInsert ? "inserted" : "updated",
            existing.TenantId,
            existing.PlanName,
            existing.AnalysisMonthlyLimit,
            existing.ChatTokensMonthlyLimit,
            existing.ChatContextBudgetTokens,
            existing.ChatEnabledModelsRaw,
            existing.IsActive);

        return existing;
    }
}
