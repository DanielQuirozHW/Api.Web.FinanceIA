// Project : Api.Web.FinanceIA
// Created : 2026-09-09

namespace Api.Web.FinanceIA.Models.Settings;

/// <summary>
/// The tenant profile in force: what kind of entity the tenant is, and which OPTIONAL
/// integrations that leaves it entitled to use.
///
/// <para>
/// The three flags are ENTITLEMENTS, not availability. A flag being true says the tenant may use
/// the vendor, not that this installation can reach it — the per-tenant LUFE key, the NOSIS
/// configuration and the CASFOG credentials still decide that, and they are not folded in here.
/// </para>
///
/// <para>
/// ARCA and BCRA are absent on purpose: they are base integrations, always on and never
/// parametrised.
/// </para>
/// </summary>
public sealed record TenantProfileDto
{
    /// <summary>The tenant's entity type: <c>sgr</c> or <c>non_sgr</c>.</summary>
    public required string EntityType { get; init; }

    /// <summary>
    /// How the profile was arrived at: <c>Configured</c> (at least one stored row was applied),
    /// <c>NoConfiguration</c> (no rows — the SGR preset applies in full, a real answer and not a
    /// degraded one) or <c>ReadFailed</c> (the rows could not be read, so this is a fallback).
    /// A client that would present the profile as the tenant's settled state should say so
    /// differently on <c>ReadFailed</c>.
    /// </summary>
    public required string Source { get; init; }

    /// <summary>Whether the LUFE company/partner registry may be queried.</summary>
    public required bool LufeEnabled { get; init; }

    /// <summary>Whether the CASFOG guarantee-fund registry may be queried.</summary>
    public required bool CasfogEnabled { get; init; }

    /// <summary>Whether the NOSIS credit bureau may be queried.</summary>
    public required bool NosisEnabled { get; init; }
}

/// <summary>
/// Request body for setting the tenant's entity type.
/// </summary>
public sealed record UpdateTenantProfileRequest
{
    /// <summary>
    /// The entity type to store: <c>sgr</c> or <c>non_sgr</c>. Matched case-insensitively and
    /// trimmed; anything else is rejected with a 400.
    /// </summary>
    public required string EntityType { get; init; }
}
