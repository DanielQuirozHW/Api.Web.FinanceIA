// Project : Api.Web.FinanceIA
// Created : 2026-06-14
// Modified: 2026-06-14

using Api.Web.FinanceIA.Models.Lufe;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// Fetches entity data from the Lufe (Legajo Único de Facturación Electrónica) government API.
/// The API key is tenant-specific and must be provided by the caller.
/// </summary>
public interface ILufeService
{
    /// <summary>
    /// Fetches entity data, authorities, financial indicators, documents, periods and the
    /// post-balance monthly sales/purchases series from Lufe in parallel.
    /// Never throws (bar caller cancellation). The result's Data is null only when the entidad,
    /// autoridades AND indicadores sections all came back empty; the estado then says why:
    /// NoLegajoReported when Lufe answered that the CUIT has no legajo or that the PyME has not
    /// authorized sharing it with this entity (any 4xx except 408/429 on entidades/{cuit}, or a 200
    /// with no entity), LookupFailed when the calls failed at transport level (network error,
    /// timeout, 5xx, 408, 429). Any other section failing just leaves its own member
    /// null — partial results are allowed and reported as LegajoFound.
    /// The API key must be provided by the caller (tenant-specific).
    /// </summary>
    Task<LufeLookupResult> GetLufeDataAsync(string tenantId, long cuit, string apiKey, CancellationToken ct);

    /// <summary>
    /// Fetches only the MiPyME category (CertificadoPyme.Categoria) for a given CUIT.
    /// Makes exactly ONE HTTP call to Lufe (entidades/{cuit}).
    /// Returns the raw categoria string, or null on any failure or if not certified.
    /// </summary>
    Task<string?> GetMipymeCategoriaAsync(string tenantId, long cuit, string apiKey, CancellationToken ct);

    /// <summary>
    /// Downloads a document from Lufe using its absolute URL (from persisted metadata).
    /// Returns null on network/timeout error.
    /// Returns LufeDocumentDownload(Found: false) when Lufe returns 404.
    /// </summary>
    Task<LufeDocumentDownload?> DownloadDocumentAsync(
        string absoluteUrl, string apiKey, CancellationToken ct);
}
