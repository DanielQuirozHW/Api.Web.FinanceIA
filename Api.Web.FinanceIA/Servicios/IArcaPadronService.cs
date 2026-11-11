// Project : Api.Web.FinanceIA
// Created : 2026-05-12

using Api.Web.FinanceIA.Models.Arca;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// Client for ARCA Padron A13 web service.
/// Queries entity data by CUIT or DNI using SOAP protocol.
/// </summary>
public interface IArcaPadronService
{
    /// <summary>
    /// Gets entity data from ARCA by CUIT.
    /// </summary>
    /// <param name="cuit">11-digit CUIT (no hyphens).</param>
    /// <param name="cuitRepresentada">CUIT of the entity making the request (tenant's CUIT).</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>Entity data or null if not found.</returns>
    Task<ArcaPersonaData?> GetPersonaByCuitAsync(
        string cuit,
        string cuitRepresentada,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets CUIT(s) associated with a DNI.
    /// </summary>
    /// <param name="dni">7 or 8 digit DNI.</param>
    /// <param name="cuitRepresentada">CUIT of the entity making the request (tenant's CUIT).</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>List of CUITs (11-digit, no hyphens) or empty list.</returns>
    Task<List<string>> GetCuitsByDniAsync(
        string dni,
        string cuitRepresentada,
        CancellationToken cancellationToken = default);
}
