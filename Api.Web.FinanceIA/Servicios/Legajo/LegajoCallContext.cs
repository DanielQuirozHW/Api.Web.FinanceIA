// Project : Api.Web.FinanceIA
// Created : 2026-09-28

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// Whose call a model request is, for its ApiCallLogs row: the tenant and the document. Carries no
/// identity of the person — nothing here reaches the request body.
/// </summary>
/// <param name="TenantId">Tenant slug.</param>
/// <param name="DocumentId">The legajo document read.</param>
public sealed record LegajoCallContext(string TenantId, Guid DocumentId);
