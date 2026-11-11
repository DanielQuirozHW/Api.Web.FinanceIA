// Project : Api.Web.FinanceIA
// Created : 2026-05-12

using Api.Web.FinanceIA.Models.Arca;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// Handles WSAA (Web Service de Autenticacion y Autorizacion) authentication
/// for ARCA web services. Manages TRA generation, certificate signing,
/// and token caching.
/// </summary>
public interface IArcaWsaaService
{
    /// <summary>
    /// Returns a valid WSAA ticket for the specified service.
    /// Tickets are cached and auto-refreshed before expiration.
    /// </summary>
    /// <param name="serviceId">ARCA service identifier (e.g. "ws_sr_padron_a13").</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    Task<WsaaTicket> GetTicketAsync(string serviceId, CancellationToken cancellationToken = default);
}
