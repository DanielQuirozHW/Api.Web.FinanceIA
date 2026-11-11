// Project : Api.Web.FinanceIA
// Created : 2026-10-20

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The only conclusion a Phase 4a persona humana evaluation draws: a flag, never a score or a level. The
/// analyst decides either way.
/// </summary>
public static class PersonSuggestions
{
    /// <summary>At least one pre-filter came out <see cref="PersonFilterOutcomes.Rechazo"/>.</summary>
    public const string RechazoSugerido = "RechazoSugerido";

    /// <summary>No pre-filter suggests rejection.</summary>
    public const string ParaDecisionDelAnalista = "ParaDecisionDelAnalista";
}
