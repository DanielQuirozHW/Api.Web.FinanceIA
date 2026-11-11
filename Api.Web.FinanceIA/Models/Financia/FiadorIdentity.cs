// Project : Api.Web.FinanceIA
// Created : 2026-10-30

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Who a fiador of a persona humana analysis is, read live from the Fiadores row (never frozen):
/// Fiadores.Denominacion and Fiadores.CUIT. Either is null when the row has none.
/// </summary>
public sealed record FiadorIdentity(string? Name, string? Cuit);
