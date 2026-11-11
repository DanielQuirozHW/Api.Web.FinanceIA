// Project : Api.Web.FinanceIA
// Created : 2026-11-03

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>The values of <see cref="BalanceCorrectionSuggestionItem.Basis"/>.</summary>
public static class BalanceSuggestionBases
{
    /// <summary>The figure as printed on the document.</summary>
    public const string Printed = "printed";

    /// <summary>A figure calculated from other printed figures (no printed line reconciles).</summary>
    public const string Derived = "derived";
}
