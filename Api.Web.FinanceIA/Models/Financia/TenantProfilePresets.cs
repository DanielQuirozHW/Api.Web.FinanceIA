// Project : Api.Web.FinanceIA
// Created : 2026-09-04

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The tenant profile in force for one tenant: what kind of entity it is and which OPTIONAL
/// integrations it may use. Returned by <c>ITenantProfileService.GetEffectiveAsync</c>, built
/// from the tenant's <see cref="TenantSettings"/> rows overlaid on the preset for its entity
/// type, or from <see cref="TenantProfilePresets.Default"/> when it has no rows at all.
///
/// <para>
/// ARCA and BCRA are deliberately absent. They are base integrations, always on and never
/// parametrised, so the profile has nothing to say about them.
/// </para>
///
/// <para>
/// Every flag here is an ENTITLEMENT — whether the tenant may use the vendor — not a statement
/// that the installation can reach it. Whether a call can actually be made is still decided by
/// the installation's own configuration (<c>Nosis:Enabled</c>, the CASFOG credentials, the
/// per-tenant LUFE key); this record does not fold that in. <c>IIntegrationAccessService</c> holds
/// both halves.
/// </para>
/// </summary>
/// <param name="EntityType">
/// <see cref="TenantSettingKeys.EntityTypeSgr"/> or <see cref="TenantSettingKeys.EntityTypeNonSgr"/>.
/// </param>
/// <param name="LufeEnabled">Whether the LUFE company/partner registry may be queried.</param>
/// <param name="CasfogEnabled">Whether the CASFOG guarantee-fund registry may be queried.</param>
/// <param name="NosisEnabled">Whether the NOSIS credit bureau may be queried.</param>
public sealed record TenantProfile(
    string EntityType,
    bool LufeEnabled,
    bool CasfogEnabled,
    bool NosisEnabled);

/// <summary>
/// Where the values in a <see cref="TenantProfileResolution"/> came from.
///
/// <para>
/// The distinction exists for the same reason <see cref="BillingPlanSource"/> has it: a tenant
/// that genuinely has no rows and a tenant whose rows could not be read both resolve to the
/// default preset, but only one of those is a fact about the tenant.
/// </para>
/// </summary>
public enum TenantProfileSource
{
    /// <summary>At least one <see cref="TenantSettings"/> profile row was read and applied.</summary>
    Configured,

    /// <summary>
    /// The tenant has no profile rows, so <see cref="TenantProfilePresets.Default"/> applies in
    /// full. This is the expected state for an SGR tenant — the default IS the SGR preset — and
    /// it is a real answer, not a degraded one.
    /// </summary>
    NoConfiguration,

    /// <summary>
    /// The rows could not be read — a transient database fault, not a fact about the tenant.
    /// The profile is a fallback and any consumer that would switch an integration OFF on it
    /// must not. See <see cref="TenantProfileResolution.IsDegraded"/>.
    /// </summary>
    ReadFailed
}

/// <summary>
/// The effective profile for one tenant, together with how it was arrived at.
/// </summary>
/// <param name="Profile">The profile in force, never null.</param>
/// <param name="Source">How <paramref name="Profile"/> was obtained.</param>
public sealed record TenantProfileResolution(TenantProfile Profile, TenantProfileSource Source)
{
    /// <summary>
    /// True when the stored rows could not be read, so <see cref="Profile"/> is a fallback
    /// rather than the tenant's real configuration.
    ///
    /// <para>
    /// CONSUMERS MUST FAIL OPEN ON THIS: a degraded resolution means "assume the preset", never
    /// "assume everything is off". Dropping an integration from an analysis because a SELECT
    /// blipped would silently remove enrichment data from a score.
    /// </para>
    /// </summary>
    public bool IsDegraded => Source == TenantProfileSource.ReadFailed;
}

/// <summary>
/// Single source of truth for the tenant-profile presets and for the fallback a tenant gets
/// when it has no profile rows.
///
/// <para>
/// The presets live here rather than in a catalogue table, for the same reason
/// <see cref="TenantSettingKeys"/> holds the settings defaults and <see cref="BillingPlans"/>
/// holds the commercial presets: a tenant with no row must resolve to something sensible and
/// must never fail, and adding or changing a preset must not require inserting a row for every
/// existing tenant.
/// </para>
///
/// <para>
/// Unlike <see cref="BillingPlans"/>, these presets ARE consulted to interpret stored rows: the
/// tenant's <c>entity.type</c> row selects a preset, and that preset supplies the default for
/// every <c>integration.*.enabled</c> key the tenant has not overridden with a row of its own.
/// Applying the SGR preset at onboarding therefore writes no rows at all, and applying the
/// non-SGR preset writes exactly one (<c>entity.type = non_sgr</c>). See
/// docs/guides/tenant-profile.md.
/// </para>
/// </summary>
public static class TenantProfilePresets
{
    // ── Presets ─────────────────────────────────────────────────────────────────
    //
    // LUFE is the public company/partner registry and is useful to any lender. CASFOG is the
    // SGR guarantee-fund registry (its base URL is sgr.casfog.com.ar) and has no meaning for a
    // lender that is not an SGR. NOSIS is a general credit bureau.
    //
    // NOSIS is OFF by default for a non-SGR tenant. It is billed per query, so the default that
    // costs nothing is the safe one to ship: a tenant that wants it turns it on with one
    // integration.nosis.enabled row. This resolves the TODO(profile) that stood here — product
    // confirmed the billing concern outweighs the convenience of it being on.

    /// <summary>An SGR: every optional integration on. Also the fallback — see <see cref="Default"/>.</summary>
    public static readonly TenantProfile Sgr =
        new(TenantSettingKeys.EntityTypeSgr, LufeEnabled: true, CasfogEnabled: true, NosisEnabled: true);

    /// <summary>A non-SGR lender: LUFE on, CASFOG and NOSIS off.</summary>
    public static readonly TenantProfile NonSgr =
        new(TenantSettingKeys.EntityTypeNonSgr, LufeEnabled: true, CasfogEnabled: false, NosisEnabled: false);

    /// <summary>
    /// The profile of a tenant with no rows, and the base every unknown entity type falls back
    /// to. It is <see cref="Sgr"/> because every tenant that existed before the profile did is
    /// an SGR, and <see cref="TenantSettingKeys.EntityTypeDefault"/> says the same thing.
    /// </summary>
    public static readonly TenantProfile Default = Sgr;

    // ── Lookup ──────────────────────────────────────────────────────────────────

    /// <summary>All presets.</summary>
    public static readonly IReadOnlyList<TenantProfile> All = [Sgr, NonSgr];

    /// <summary>
    /// Finds the preset for an entity type, case-insensitively and ignoring surrounding
    /// whitespace, or null when no preset carries that type.
    /// </summary>
    public static TenantProfile? Find(string? entityType)
    {
        if (string.IsNullOrWhiteSpace(entityType))
            return null;

        var trimmed = entityType.Trim();

        foreach (var preset in All)
        {
            if (string.Equals(preset.EntityType, trimmed, StringComparison.OrdinalIgnoreCase))
                return preset;
        }

        return null;
    }

    /// <summary>
    /// The preset for an entity type, or <see cref="Default"/> when the type is null, blank or
    /// unknown. This is the read-path lookup: a typo in a stored <c>entity.type</c> row must
    /// resolve to something rather than fail.
    /// </summary>
    public static TenantProfile ForEntityType(string? entityType) => Find(entityType) ?? Default;
}
