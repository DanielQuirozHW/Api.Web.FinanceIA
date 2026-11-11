// Project : Api.Web.FinanceIA
// Created : 2026-10-24

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// The single home of BOTH halves of every optional-integration gate (LUFE, CASFOG, NOSIS):
/// ENTITLEMENT — may the tenant use the vendor at all (its <see cref="TenantProfile"/>) — and
/// AVAILABILITY — can this installation reach it (the per-tenant LUFE key, the CASFOG
/// credentials, <c>Nosis:Enabled</c> plus its key and VR). ARCA, BCRA and Claude are base
/// integrations and never come through here.
///
/// <para>
/// <c>EntitiesController</c>, <c>FiadoresController</c> and the operations dashboard all read
/// from this one service, so an integration the dashboard hides is exactly one the enrichment
/// endpoints would refuse — they cannot drift.
/// </para>
///
/// <para>
/// Pass <c>tenantId</c> as a parameter — never inject the tenant accessor.
/// </para>
/// </summary>
public interface IIntegrationAccessService
{
    /// <summary>
    /// Resolves which optional integrations the tenant may use — the ENTITLEMENT half.
    ///
    /// <para>
    /// FAILS OPEN on anything short of a fact: a degraded resolution
    /// (<see cref="TenantProfileResolution.IsDegraded"/>) and an exception out of the resolver both
    /// yield a profile with every optional integration ON, so the availability half alone decides.
    /// Cancellation is not a degraded profile and propagates. A profile that legitimately disables
    /// an integration is returned as-is.
    /// </para>
    /// </summary>
    Task<TenantProfile> ResolveEntitlementsAsync(string tenantId, CancellationToken ct);

    /// <summary>The tenant's LUFE API key (<c>Tenants:{tenantId}:LufeApiKey</c>), or null when unset.</summary>
    string? GetLufeApiKey(string tenantId);

    /// <summary>
    /// True when NOSIS is switched on AND has the credentials it needs (<c>Nosis:Enabled</c>,
    /// <c>Nosis:ApiKey</c>, <c>Nosis:VR</c>). Global, not per-tenant: NOSIS is licensed per
    /// installation.
    /// </summary>
    bool IsNosisConfigured();

    /// <summary>
    /// True when the CASFOG credentials (<c>Casfog:Email</c>, <c>Casfog:Password</c>) are present —
    /// the same test <c>CasfogService</c> makes before it logs in.
    /// </summary>
    bool IsCasfogConfigured();

    /// <summary>
    /// Entitlement AND availability, per optional integration, from ONE profile read. For a
    /// consumer that only needs to know what is usable — the operations dashboard — rather than
    /// why not; the enrichment endpoints check the two halves separately because each failure
    /// answers with its own message.
    /// </summary>
    Task<OptionalIntegrationAccess> ResolveUsableAsync(string tenantId, CancellationToken ct);
}
