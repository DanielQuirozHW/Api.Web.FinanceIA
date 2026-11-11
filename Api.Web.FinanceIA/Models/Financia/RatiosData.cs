// Project : Api.Web.FinanceIA
// Created : 2026-05-08

using System.Text.Json.Serialization;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Typed representation of the financial ratios JSON stored in
/// <c>AnalysisResults.RatiosJson</c>. Keys match Claude's output schema exactly.
/// </summary>
public sealed class RatiosData
{
    [JsonPropertyName("current_ratio")]
    public double? CurrentRatio { get; set; }

    [JsonPropertyName("acid_test")]
    public double? AcidTest { get; set; }

    [JsonPropertyName("debt_ratio")]
    public double? DebtRatio { get; set; }

    [JsonPropertyName("leverage")]
    public double? Leverage { get; set; }

    [JsonPropertyName("roe")]
    public double? Roe { get; set; }

    [JsonPropertyName("roa")]
    public double? Roa { get; set; }

    [JsonPropertyName("net_margin")]
    public double? NetMargin { get; set; }

    [JsonPropertyName("interest_coverage")]
    public double? InterestCoverage { get; set; }

    /// <summary>
    /// Why each null ratio is null, keyed by its SCORING key (<c>liquidez_corriente</c>, …):
    /// <see cref="RatioExclusionReasons.Unread"/>, <see cref="RatioExclusionReasons.NotMeaningful"/> or —
    /// for liquidez corriente and prueba ácida only, since scoring algorithm 10 — the marker
    /// <see cref="RatioExclusionReasons.NoCurrentLiabilities"/>, which the engine scores at the best band.
    /// Filled by <c>RatioCalculationService</c> only; null for ratios that came from the model's own
    /// output (no operands to inspect). Reporting only — never read by the composite — and never
    /// serialised, so <c>AnalysisResults.RatiosJson</c> keeps its bytes.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, string>? ExclusionReasons { get; set; }

    /// <summary>
    /// The balance's <c>operating_result</c> (before every financial result), as read. Read by the
    /// scoring engine's profitability floor only (algorithm 10): ROE, ROA and margen neto floor the score
    /// only when it is not positive. Filled by <c>RatioCalculationService</c>; null for ratios that came
    /// from the model's own output, which keeps the floor as it always was. Never serialised, so
    /// <c>AnalysisResults.RatiosJson</c> keeps its bytes.
    /// </summary>
    [JsonIgnore]
    public double? OperatingResult { get; set; }

    /// <summary>
    /// The balance's <c>financial_expenses</c> (pure interest, when printed on its own line), as read —
    /// sign as extracted; the engine takes its absolute value. Same provenance and the same
    /// never-serialised rule as <see cref="OperatingResult"/>.
    /// </summary>
    [JsonIgnore]
    public double? FinancialExpenses { get; set; }
}
