// Project : Api.Web.FinanceIA
// Created : 2026-09-27

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// What <see cref="ArcaConstanciaService.ResolveFiscalRegime"/> derived from a getPersona_v2 answer.
/// </summary>
/// <param name="FiscalRegime">One of <see cref="FiscalRegimes.Known"/>.</param>
/// <param name="MonotributoCategoria">
/// A single letter A–K; null unless the regime is Monotributo and its category parsed.
/// </param>
/// <param name="CategoriaUnparsable">
/// True when a non-blank descripcionCategoria yielded no category A–K — the parser's cue to log a
/// Warning, which carries the description's length and never its text.
/// </param>
internal sealed record ArcaFiscalRegimeResult(
    string  FiscalRegime,
    string? MonotributoCategoria,
    bool    CategoriaUnparsable);
