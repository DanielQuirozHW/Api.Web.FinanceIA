// Project : Api.Web.FinanceIA
// Created : 2026-11-03

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Api.Web.FinanceIA.Servicios.BalanceCorrections;

/// <summary>
/// What a correction suggestion asks, read off the STORED balance: the failed targeted checks of its
/// integrity report (<see cref="BalanceSuggestionPlanner.TargetedChecks"/>), the figures they involve and the question for the model. Values the model compares
/// against are in printed units; <see cref="StoredByPath"/> keeps them in pesos, as stored.
/// </summary>
/// <param name="BalanceJson">The stored BalanceJson, as read.</param>
/// <param name="Scale">amounts_scale (1 when absent): printed × scale = pesos.</param>
/// <param name="FailedChecks">The failed targeted checks of the stored report, distinct, in report order.</param>
/// <param name="Question">The question for the model.</param>
/// <param name="StoredByPath">Every asked path (top-level and item) with its stored value in pesos.</param>
/// <param name="NetProfitColumnMix">
/// True when the stored failed net_profit_reconciliation RECORDED a column mix (<c>columnMix: true</c>): result
/// before tax and income tax were read from the prior-year column. Never re-judged here — a blob without the
/// record reads false, as everywhere else.
/// </param>
public sealed record BalanceSuggestionPlan(
    string BalanceJson,
    double Scale,
    IReadOnlyList<string> FailedChecks,
    BalanceSuggestionQuestion Question,
    IReadOnlyDictionary<string, double?> StoredByPath,
    bool NetProfitColumnMix = false);

/// <summary>
/// Builds the <see cref="BalanceSuggestionPlan"/>: pure and static, never throws. The figures asked are
/// the current-year lines every failed targeted check involves (its <c>lines</c>, when correctable; for
/// net_profit_reconciliation, net_profit, income_tax and result_before_tax — the check's own <c>lines</c>
/// name only net_profit) plus every line of the rubro list of a failed <c>*_items</c> check. Only paths of the correction whitelist
/// (<see cref="BalanceCorrectionPath"/>) are asked, so every answer maps to a correction the analyst can apply.
/// </summary>
public static class BalanceSuggestionPlanner
{
    /// <summary>
    /// The checks a suggestion answers: every structure check, and net_profit_reconciliation — which a
    /// document's presentation can fail on its own (a pre-tax subtotal that omits a line, no pre-tax line at
    /// all) and whose missing figure can be derived. The other soft checks (the signs) are not targeted: a
    /// balance failing only those is not applicable.
    /// </summary>
    public static readonly IReadOnlySet<string> TargetedChecks = new HashSet<string>(
        BalanceIntegrityService.StructureChecks.Append(BalanceIntegrityService.NetProfitReconciliationCheck),
        StringComparer.Ordinal);

    // What net_profit_reconciliation asks, in this order: the two figures a derivation is made from, then
    // the figure it would correct.
    private static readonly string[] NetProfitLines = ["net_profit", "income_tax", "result_before_tax"];

    /// <summary>The plan, or null when the stored integrity has no failed targeted check (or nothing correctable).</summary>
    public static BalanceSuggestionPlan? Build(string? balanceJson, int? fiscalYear)
    {
        if (string.IsNullOrWhiteSpace(balanceJson))
            return null;

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(balanceJson) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }

        if (root?["integrity"] is not JsonObject integrity || integrity["checks"] is not JsonArray checks)
            return null;

        var failed    = new List<string>();
        var lines     = new List<string>();
        var columnMix = false;
        foreach (var node in checks)
        {
            if (node is not JsonObject check
                || String(check["name"]) is not { } name
                || !TargetedChecks.Contains(name)
                || check["passed"] is not JsonValue passed || passed.GetValueKind() != JsonValueKind.False)
            {
                continue;
            }

            if (!failed.Contains(name, StringComparer.Ordinal))
                failed.Add(name);

            if (name == BalanceIntegrityService.NetProfitReconciliationCheck
                && check["columnMix"] is JsonValue mix && mix.GetValueKind() == JsonValueKind.True)
            {
                columnMix = true;
            }

            IEnumerable<string?> checkKeys = name == BalanceIntegrityService.NetProfitReconciliationCheck
                ? NetProfitLines
                : check["lines"] is JsonArray checkLines ? checkLines.Select(String) : [];
            foreach (var key in checkKeys)
            {
                if (key is not null && !lines.Contains(key, StringComparer.Ordinal))
                    lines.Add(key);
            }
        }

        if (failed.Count == 0)
            return null;

        var scale = root["amounts_scale"] is JsonValue s && s.GetValueKind() == JsonValueKind.Number
                    && s.TryGetValue<double>(out var factor) && factor > 0d
            ? factor
            : 1d;

        var stored = new Dictionary<string, double?>(StringComparer.Ordinal);
        var fields = new List<BalanceSuggestionField>();
        foreach (var key in lines)
        {
            if (BalanceCorrectionPath.TryParse(key) is not { IsItem: false } path
                || !BalanceCorrectionValidator.TryReadStored(root, path, out var value))
            {
                continue;
            }

            stored[path.Raw] = value;
            fields.Add(new BalanceSuggestionField(
                path.Raw,
                BalanceSuggestionLabels.FieldLabels.TryGetValue(path.Raw, out var hint) ? hint : path.Raw,
                Printed(value, scale)));
        }

        var groups = new List<BalanceSuggestionItemGroup>();
        foreach (var array in failed.Where(n => BalanceCorrectionPath.ItemArrays.Contains(n, StringComparer.Ordinal)))
        {
            if (root[array] is not JsonArray items || items.Count == 0)
                continue;

            var printed = new List<double?>();
            for (var i = 0; i < items.Count; i++)
            {
                var itemPath = BalanceCorrectionPath.TryParse(
                    string.Create(CultureInfo.InvariantCulture, $"{array}[{i}].amount"));
                if (itemPath is null || !BalanceCorrectionValidator.TryReadStored(root, itemPath, out var amount))
                    break;   // past the whitelist's index range, or a line that is not a correctable figure

                stored[itemPath.Raw] = amount;
                printed.Add(Printed(amount, scale));
            }

            if (printed.Count > 0)
            {
                groups.Add(new BalanceSuggestionItemGroup(
                    array,
                    BalanceSuggestionLabels.ItemGroupLabels.TryGetValue(array, out var hint) ? hint : array,
                    printed));
            }
        }

        if (fields.Count == 0 && groups.Count == 0)
            return null;

        var asksNetProfit = failed.Contains(BalanceIntegrityService.NetProfitReconciliationCheck, StringComparer.Ordinal);
        return new BalanceSuggestionPlan(
            balanceJson, scale, failed,
            new BalanceSuggestionQuestion(fiscalYear, fields, groups, asksNetProfit, columnMix && asksNetProfit),
            stored,
            columnMix && asksNetProfit);
    }

    private static double? Printed(double? pesos, double scale) => pesos is { } p ? p / scale : null;

    private static string? String(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
}
