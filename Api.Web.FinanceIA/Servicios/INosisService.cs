// Project : Api.Web.FinanceIA
// Created : 2026-08-24

using Api.Web.FinanceIA.Models.Nosis;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// Provides access to the NOSIS WS.01 variables API (<c>GET rest/variables</c>).
/// Authentication is a single <c>X-API-KEY</c> header — there is no login/token flow and no
/// refresh-on-401 dance. All methods are fault-isolated and never throw:
/// <see cref="GetVariablesAsync"/> always returns a non-null <see cref="NosisData"/> whose
/// <see cref="NosisData.Estado"/> discriminates DataFound / NoDataReported / LookupFailed.
/// Successful results (DataFound / NoDataReported) are cached for 24 h per (VR group, CUIT) under
/// <c>nosis:{vr}:{cuit}</c>; transient failures (LookupFailed) are never cached.
/// The integration is gated on <c>Nosis:Enabled</c> and on the presence of <c>Nosis:ApiKey</c>
/// plus the VR the requested group resolves to; when any of those is missing the call degrades to
/// LookupFailed without touching the network.
/// </summary>
public interface INosisService
{
    /// <summary>
    /// Queries one NOSIS variable group (VR) for the given CUIT.
    /// Always returns a non-null result; inspect <see cref="NosisData.Estado"/> to distinguish
    /// <c>DataFound</c>, <c>NoDataReported</c> (a valid "nothing on file" answer) and
    /// <c>LookupFailed</c> (transient, or the integration is switched off).
    /// </summary>
    /// <param name="tenantId">
    /// Tenant that triggered the lookup — recorded on the api-call log row. Passed as a parameter
    /// rather than resolved from an injected accessor, per the project rule. NOSIS credentials
    /// themselves are per-installation, not per-tenant.
    /// </param>
    /// <param name="cuit">CUIT to query, digits only.</param>
    /// <param name="grupo">
    /// Which report group to ask for. <c>Principal</c> resolves to <c>Nosis:VR</c>;
    /// <c>Fiador</c> resolves to <c>Nosis:VrFiador</c>, falling back to <c>Nosis:VR</c> when that
    /// key is absent or blank. Required rather than defaulted: the vendor answers a wrong group
    /// with the wrong variables instead of an error, so the choice must be made at every call site.
    /// </param>
    /// <param name="ct">Token to observe for cancellation.</param>
    /// <remarks>
    /// Cache key is <c>nosis:{resolvedVr}:{cuit}</c>, TTL 24 h, failures never cached. It is keyed
    /// on the RESOLVED vr rather than on <paramref name="grupo"/> on purpose: once the two groups
    /// differ, a principal and a fiador lookup of the same CUIT can never collide, and while
    /// <c>Nosis:VrFiador</c> is unset both resolve to the same query and legitimately share one
    /// cached answer instead of billing the vendor twice for it.
    /// </remarks>
    Task<NosisData> GetVariablesAsync(
        string tenantId, long cuit, NosisVariableGroup grupo, CancellationToken ct);
}
