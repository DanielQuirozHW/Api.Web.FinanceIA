// Project : Api.Web.FinanceIA
// Created : 2026-09-04

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Resolves the EFFECTIVE tenant profile — entity type plus which optional integrations
/// (LUFE, CASFOG, NOSIS) the tenant may use — from the tenant's <see cref="TenantSettings"/>
/// rows overlaid on the preset for its entity type (<see cref="TenantProfilePresets"/>).
///
/// <para>
/// The two methods take OPPOSITE failure contracts, exactly as
/// <see cref="ITenantBillingConfigService"/> does: <see cref="GetEffectiveAsync"/> never throws
/// because the profile feeds authentication and analysis flows that must not fall over on a
/// settings blip; <see cref="SetEntityTypeAsync"/> must throw, because an operator told a profile
/// was saved must not be told that by a write that failed.
/// </para>
///
/// <para>
/// Pass <c>tenantId</c> as a parameter — never inject the tenant accessor.
/// </para>
/// </summary>
public interface ITenantProfileService
{
    /// <summary>
    /// Returns the profile in force for <paramref name="tenantId"/>, together with how it was
    /// obtained.
    ///
    /// <para>
    /// NEVER returns null and NEVER throws: a tenant with no rows, a tenant with malformed rows,
    /// and a failed read all resolve to a non-null profile. They are NOT interchangeable though —
    /// a failed read comes back as <see cref="TenantProfileSource.ReadFailed"/>, and a consumer
    /// that would switch an integration off on the answer must treat that as "assume the
    /// preset". See <see cref="TenantProfileResolution.IsDegraded"/>.
    /// </para>
    /// </summary>
    Task<TenantProfileResolution> GetEffectiveAsync(string tenantId, CancellationToken ct);

    /// <summary>
    /// Sets the tenant's entity type — <see cref="TenantSettingKeys.EntityTypeSgr"/> or
    /// <see cref="TenantSettingKeys.EntityTypeNonSgr"/> — and returns the profile that results.
    ///
    /// <para>
    /// THROWS, unlike <see cref="GetEffectiveAsync"/>. <see cref="ArgumentException"/> for a value
    /// outside the known entity types, and whatever the store raises for a failed write. Reporting
    /// a profile change that did not happen is the worse outcome here.
    /// </para>
    ///
    /// <para>
    /// Writes the <c>entity.type</c> row and nothing else. The three
    /// <c>integration.*.enabled</c> keys are left alone, so a tenant that has overridden one keeps
    /// that override and the rest continue to follow the new preset.
    /// </para>
    /// </summary>
    Task<TenantProfileResolution> SetEntityTypeAsync(string tenantId, string entityType, CancellationToken ct);
}
