// Project : Api.Web.FinanceIA
// Created : 2026-11-02

using System.Globalization;
using System.Text.RegularExpressions;

namespace Api.Web.FinanceIA.Servicios.BalanceCorrections;

/// <summary>
/// A path of the persisted BalanceJson an analyst may correct — the CLOSED whitelist of
/// POST /api/analyses/{resultId}/balance-corrections. The rule: every CURRENT-YEAR figure an integrity
/// check reads is correctable, so no failed check is beyond the analyst's reach. That is one of the 15
/// top-level figures — the 13 the ratios read (<see cref="Models.Financia.BalanceAccounts"/>) plus
/// <c>result_before_tax</c> and <c>income_tax</c>, which only net_profit_reconciliation reads — or the
/// amount of one rubro line of the three arrays the items checks sum. Labels, the prior-year column (which
/// column_swap only compares AGAINST), amounts_scale (a quoting unit, not a figure), the cash flow and every
/// other key are not editable in this phase.
/// </summary>
/// <param name="Raw">The path exactly as validated, e.g. <c>current_assets_items[2].amount</c>.</param>
/// <param name="Key">The top-level key: the figure itself, or the array holding the line.</param>
/// <param name="Index">The line index for an item path; null for a top-level figure.</param>
public sealed partial record BalanceCorrectionPath(string Raw, string Key, int? Index)
{
    /// <summary>
    /// The 15 top-level figures: the 13 of BalanceAccounts, in its order, then the two income-statement
    /// inputs of net_profit_reconciliation. A new integrity-check input joins this list.
    /// </summary>
    public static readonly IReadOnlyList<string> TopLevelKeys =
    [
        "current_assets", "non_current_assets", "assets",
        "current_liabilities", "non_current_liabilities", "liabilities",
        "equity",
        "net_revenue", "net_profit", "operating_result", "financial_expenses",
        "inventory", "prepaid_expenses",
        "result_before_tax", "income_tax",
    ];

    /// <summary>The three rubro arrays whose <c>amount</c> may be corrected.</summary>
    public static readonly IReadOnlyList<string> ItemArrays =
    [
        "current_assets_items", "current_liabilities_items", "non_current_liabilities_items",
    ];

    /// <summary>True for a rubro-line path.</summary>
    public bool IsItem => Index is not null;

    /// <summary>The path when it is in the whitelist, otherwise null. Case-sensitive, no whitespace.</summary>
    public static BalanceCorrectionPath? TryParse(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        if (TopLevelKeys.Contains(path, StringComparer.Ordinal))
            return new BalanceCorrectionPath(path, path, null);

        var match = ItemPathRegex().Match(path);
        if (!match.Success)
            return null;

        return new BalanceCorrectionPath(
            path, match.Groups["array"].Value, int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture));
    }

    // Up to three digits, no leading zero: a printed balance never has a thousand rubros under one subtotal.
    [GeneratedRegex(
        @"^(?<array>current_assets_items|current_liabilities_items|non_current_liabilities_items)\[(?<index>0|[1-9][0-9]{0,2})\]\.amount$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ItemPathRegex();
}
