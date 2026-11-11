// Project : Api.Web.FinanceIA
// Created : 2026-08-16

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// Reads the per-tenant deterministic alert rule catalogue ([dbo].[AlertRuleCatalogue]).
/// </summary>
public interface IAlertRuleCatalogueRepository
{
    /// <summary>
    /// Returns the ENABLED rules for the tenant, ordered by <see cref="AlertRuleDefinition.Code"/>
    /// so the engine's input — and therefore its output — is stable regardless of the query plan.
    /// Returns an empty list when the tenant has no catalogue rows at all: absence means "no rule
    /// fires", never a hardcoded fallback rule.
    /// </summary>
    Task<IReadOnlyList<AlertRuleDefinition>> GetEnabledAsync(string tenantId, CancellationToken ct);

    /// <summary>
    /// Returns EVERY rule for the tenant, enabled or not, ordered ordinally by
    /// <see cref="AlertRuleDefinition.Code"/>. The configuration screen exists to enable disabled
    /// rules, so a query that filtered them out would hide the only rows worth acting on.
    /// </summary>
    Task<IReadOnlyList<AlertRuleDefinition>> GetAllAsync(string tenantId, CancellationToken ct);

    /// <summary>
    /// Returns one rule, or null when it does not exist OR belongs to another tenant — the two are
    /// deliberately indistinguishable to the caller, so a rule id from another tenant reads as
    /// "not found" and can never become a cross-tenant write.
    /// </summary>
    Task<AlertRuleDefinition?> GetByIdAsync(Guid ruleId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Updates ONLY <see cref="AlertRuleDefinition.Enabled"/>,
    /// <see cref="AlertRuleDefinition.Severity"/> and <see cref="AlertRuleDefinition.ThresholdValue"/>,
    /// plus the audit columns UpdatedAt/UpdatedBy. Everything else — Code above all — is immutable
    /// through this path.
    /// </summary>
    /// <returns>The persisted row, or null when no row matched (unknown id, or another tenant's).</returns>
    Task<AlertRuleDefinition?> UpdateAsync(
        Guid ruleId,
        string tenantId,
        bool enabled,
        string severity,
        decimal? thresholdValue,
        Guid updatedBy,
        CancellationToken ct);
}
