// Project : Api.Web.FinanceIA
// Created : 2026-10-20

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>A conclusion the legajo does not support, stated explicitly (M10 Phase 4a).</summary>
/// <param name="Code">Stable code, e.g. <c>flujo_solo_declarado</c>.</param>
/// <param name="Text">The statement, in es-AR.</param>
public sealed record PersonCannotConclude(string Code, string Text);
