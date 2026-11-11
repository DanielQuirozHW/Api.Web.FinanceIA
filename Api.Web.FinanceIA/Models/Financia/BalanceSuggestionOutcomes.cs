// Project : Api.Web.FinanceIA
// Created : 2026-11-03

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// <c>outcome</c> of POST /api/analyses/{id}/balance-corrections/suggestions. Part of the Portal contract.
/// The checks a suggestion targets are the structure checks and net_profit_reconciliation.
/// </summary>
public static class BalanceSuggestionOutcomes
{
    /// <summary>With the suggested figures every previously failed targeted check passes and none newly fails.</summary>
    public const string Fixes = "fixes";

    /// <summary>Fewer targeted checks fail and none newly fails.</summary>
    public const string Reduces = "reduces";

    /// <summary>The model read every involved figure as stored and the checks still fail: the document itself does not add up.</summary>
    public const string DocumentError = "document_error";

    /// <summary>No verified suggestion.</summary>
    public const string NoSuggestion = "no_suggestion";
}
