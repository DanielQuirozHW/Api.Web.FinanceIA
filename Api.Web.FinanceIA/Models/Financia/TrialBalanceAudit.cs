// Project : Api.Web.FinanceIA
// Created : 2026-11-04

using System.Text.Json.Serialization;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// What <see cref="Api.Web.FinanceIA.Servicios.TrialBalanceNormalizer"/> did to a trial balance, persisted at
/// the ROOT of BalanceJson under <c>trial_balance</c> — beside <c>document_kind</c>, not inside
/// <c>integrity</c>, which a manual correction rebuilds. Written only for a trial balance, so every other
/// BalanceJson is byte-identical to what it was before. Amounts in pesos, in THIS schema's sign convention;
/// no account label (a ledger label can name a person).
/// </summary>
public sealed record TrialBalanceAudit
{
    /// <summary>One of the <c>TrialBalanceNormalizer</c> adjustment codes.</summary>
    [JsonPropertyName("adjustment")]
    public required string Adjustment { get; init; }

    /// <summary>The ledger sign convention the accounting equation confirmed; null when it confirmed none.</summary>
    [JsonPropertyName("credit_balances_negative")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CreditBalancesNegative { get; init; }

    /// <summary>Equity as the equity accounts print it (before any result was added), schema sign.</summary>
    [JsonPropertyName("equity_printed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? EquityPrinted { get; init; }

    /// <summary>The year's result computed from the results accounts (a loss is negative).</summary>
    [JsonPropertyName("year_result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? YearResult { get; init; }

    /// <summary>Each class's contribution to the year's result (a charge is negative).</summary>
    [JsonPropertyName("results_by_class")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, double>? ResultsByClass { get; init; }

    /// <summary>The figures the model reported that were overwritten, by BalanceJson key (null: it reported none).</summary>
    [JsonPropertyName("replaced")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, double?>? Replaced { get; init; }

    /// <summary>BalanceJson keys whose ledger credit sign was turned into this schema's positive magnitude.</summary>
    [JsonPropertyName("signs_normalized")]
    public IReadOnlyList<string> SignsNormalized { get; init; } = [];
}
