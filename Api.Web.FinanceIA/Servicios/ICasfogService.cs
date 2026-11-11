// Project : Api.Web.FinanceIA
// Created : 2026-07-29
// Modified: 2026-07-29

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// Provides access to the CASFOG (SGR central de deudores / garantías) API.
/// The vendor requires a login (email + password → bearer token) before any data
/// call; the token is cached and refreshed on a 401. All methods are fault-isolated
/// and never throw: <see cref="GetCentralDeudoresAsync"/> always returns a non-null
/// <see cref="CasfogData"/> whose <see cref="CasfogData.Estado"/> discriminates
/// DataFound / NoDataReported / LookupFailed, with non-null (empty) collections on the
/// negative paths. Successful results (DataFound / NoDataReported) are cached for 24 h
/// per CUIT under <c>casfog:{cuit}</c>; transient failures (LookupFailed) are never cached.
/// </summary>
public interface ICasfogService
{
    /// <summary>
    /// Queries the CASFOG central de deudores for the given CUIT.
    /// Always returns a non-null result; inspect <see cref="CasfogData.Estado"/> to
    /// distinguish <c>DataFound</c>, <c>NoDataReported</c> (a valid "nothing on file"
    /// answer), and <c>LookupFailed</c> (transient). Cache key: <c>casfog:{cuit}</c>,
    /// TTL 24 h (transient failures are not cached).
    /// </summary>
    Task<CasfogData> GetCentralDeudoresAsync(long cuit, CancellationToken ct);
}
