// Project : Api.Web.FinanceIA
// Created : 2026-08-16

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IAlertRuleCatalogueRepository"/>.
/// Reads use AsNoTracking; tenant isolation is explicit in the WHERE clause — this context
/// declares no global query filter, so every query carries its own tenant predicate.
/// </summary>
public sealed class AlertRuleCatalogueRepository : IAlertRuleCatalogueRepository
{
    private readonly FinanciaDbContext                      _dbContext;
    private readonly ILogger<AlertRuleCatalogueRepository>  _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="AlertRuleCatalogueRepository"/>.
    /// </summary>
    public AlertRuleCatalogueRepository(
        FinanciaDbContext dbContext, ILogger<AlertRuleCatalogueRepository> logger)
    {
        _dbContext = dbContext;
        _logger    = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AlertRuleDefinition>> GetEnabledAsync(
        string tenantId, CancellationToken ct)
    {
        return await _dbContext.AlertRuleCatalogue
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.Enabled)
            .OrderBy(r => r.Code)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AlertRuleDefinition>> GetAllAsync(
        string tenantId, CancellationToken ct)
    {
        var rules = await _dbContext.AlertRuleCatalogue
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Ordered in memory rather than in SQL: the database's collation ordering is not
        // necessarily ordinal, and the screen's row order must not depend on the query plan.
        // Same reasoning as AlertRuleEngine.Evaluate's OrderBy.
        return rules.OrderBy(r => r.Code, StringComparer.Ordinal).ToList();
    }

    /// <inheritdoc />
    public async Task<AlertRuleDefinition?> GetByIdAsync(
        Guid ruleId, string tenantId, CancellationToken ct)
    {
        // The tenant predicate is part of the lookup, not a check applied afterwards: another
        // tenant's rule is simply not found.
        return await _dbContext.AlertRuleCatalogue
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.RuleId == ruleId && r.TenantId == tenantId, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AlertRuleDefinition?> UpdateAsync(
        Guid ruleId,
        string tenantId,
        bool enabled,
        string severity,
        decimal? thresholdValue,
        Guid updatedBy,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // Single UPDATE naming exactly the five writable columns, mirroring
        // CreditLineRepository.RaiseAsync. Listing the columns explicitly — rather than loading the
        // entity and calling SaveChanges — is what makes "Code and the templates are immutable
        // through this path" a property of the SQL rather than of the calling code's discipline.
        // TenantId is in the WHERE, so a foreign ruleId matches 0 rows and writes nothing.
        var affected = await _dbContext.AlertRuleCatalogue
            .Where(r => r.RuleId == ruleId && r.TenantId == tenantId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.Enabled,        enabled)
                .SetProperty(r => r.Severity,       severity)
                .SetProperty(r => r.ThresholdValue, thresholdValue)
                .SetProperty(r => r.UpdatedAt,      now)
                .SetProperty(r => r.UpdatedBy,      (Guid?)updatedBy),
                ct)
            .ConfigureAwait(false);

        if (affected == 0)
            return null;

        _logger.LogInformation(
            "Alert rule {RuleId} updated (enabled={Enabled}, severity={Severity}, threshold={Threshold}) " +
            "by {UpdatedBy} tenant {TenantId}",
            ruleId, enabled, severity, thresholdValue, updatedBy, tenantId);

        // Return the authoritative persisted snapshot for the response mapping.
        return await _dbContext.AlertRuleCatalogue
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.RuleId == ruleId && r.TenantId == tenantId, ct)
            .ConfigureAwait(false);
    }
}
