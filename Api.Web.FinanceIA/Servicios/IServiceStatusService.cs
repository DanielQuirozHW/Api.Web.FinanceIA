// Project : Api.Web.FinanceIA
// Created : 2026-06-07

using Api.Web.FinanceIA.Models.Health;

namespace Api.Web.FinanceIA.Servicios;

public interface IServiceStatusService
{
    Task<ServiceStatusResponse> GetStatusAsync(string tenantId, CancellationToken ct);
}
