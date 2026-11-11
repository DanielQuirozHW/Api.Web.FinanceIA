// Project : Api.Web.FinanceIA
// Created : 2026-09-28

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>One queued job: a document to classify, and the tenant every statement about it is scoped to.</summary>
/// <param name="DocumentId">The document.</param>
/// <param name="TenantId">Its tenant slug.</param>
public sealed record LegajoWorkItem(Guid DocumentId, string TenantId);
