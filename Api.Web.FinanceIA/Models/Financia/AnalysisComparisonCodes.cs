// Project : Api.Web.FinanceIA
// Created : 2026-11-06

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The vocabulary of GET /api/analyses/{id}/previous-comparison — every string the Portal branches on,
/// in one place. Codes only: the Portal writes the copy for reasons and notes. Additive: a value is never
/// renamed or removed, so the Portal must ignore one it does not know.
/// </summary>
public static class AnalysisComparisonCodes
{
    /// <summary>Why <c>available</c> is false.</summary>
    public static class Reasons
    {
        /// <summary>
        /// There is no earlier decided analysis of the same entity and kind to compare against: for a company,
        /// none with a score and a balance that was read; for a persona humana, none with a stored evaluation.
        /// </summary>
        public const string NoPrevious = "no_previous";

        /// <summary>The analysis is neither a company (Balance) nor a persona humana analysis.</summary>
        public const string UnsupportedKind = "unsupported_kind";

        /// <summary>
        /// The analysis itself has nothing to compare: a company analysis with no score (a failed or unscored
        /// run), or a persona humana analysis with no stored evaluation (no solicitante row).
        /// </summary>
        public const string NoResult = "no_result";

        public static readonly IReadOnlyList<string> All = [NoPrevious, UnsupportedKind, NoResult];
    }

    /// <summary>Caveats on the comparison as a whole.</summary>
    public static class Notes
    {
        /// <summary>Both analyses cover the same fiscal year (a redo).</summary>
        public const string SameFiscalYear = "same_fiscal_year";

        /// <summary>
        /// Amounts are nominal pesos of different moments — a company's fiscal years differ, or a persona humana's
        /// two evaluations (always taken at different dates) — so their trend is not judged.
        /// </summary>
        public const string NominalAmounts = "nominal_amounts";

        /// <summary>
        /// The two analyses were computed by different engine versions: the scoring algorithm version for a
        /// company, the evaluation engine version (<c>EvaluationVersion</c>) for a persona humana.
        /// </summary>
        public const string EngineChanged = "engine_changed";

        /// <summary>The current analysis's extraction failed an integrity check.</summary>
        public const string IntegrityFailedCurrent = "integrity_failed_current";

        /// <summary>The previous analysis's extraction failed an integrity check.</summary>
        public const string IntegrityFailedPrevious = "integrity_failed_previous";

        /// <summary>
        /// Persona humana: the terms of the operation differ — requested amount, installment, its periodicity
        /// (cuotas a year) or the set of fiadores.
        /// </summary>
        public const string TermsChanged = "terms_changed";

        /// <summary>
        /// Persona humana: the currency of the terms differs. The amount rows keep both figures but carry no
        /// delta, deltaPct or trend.
        /// </summary>
        public const string CurrencyChanged = "currency_changed";

        /// <summary>
        /// Persona humana: the evaluation parameters (the tenant's pf_ cuts frozen with each evaluation) differ.
        /// A different engine version is <see cref="EngineChanged"/>.
        /// </summary>
        public const string ParametersChanged = "parameters_changed";

        public static readonly IReadOnlyList<string> All =
        [
            SameFiscalYear, NominalAmounts, EngineChanged, IntegrityFailedCurrent, IntegrityFailedPrevious,
            TermsChanged, CurrencyChanged, ParametersChanged,
        ];
    }

    /// <summary>What a row's values measure.</summary>
    public static class Units
    {
        /// <summary>A score on the displayed 0–1000 scale (<c>ScoreScale.ToNosis</c>).</summary>
        public const string Score = "score";

        /// <summary>A plain ratio (1.25 = 1,25x).</summary>
        public const string Ratio = "ratio";

        /// <summary>A percentage, already ×100 (12.5 = 12,5 %).</summary>
        public const string Percent = "percent";

        /// <summary>Nominal Argentine pesos.</summary>
        public const string Pesos = "pesos";

        /// <summary>An integer count or category (the BCRA situación 1–6).</summary>
        public const string Count = "count";

        /// <summary>A string value; delta and deltaPct are always null.</summary>
        public const string Text = "text";

        /// <summary>
        /// An amount in the currency of its side (<c>current.currency</c> / <c>previous.currency</c>, ISO 4217).
        /// Persona humana terms only.
        /// </summary>
        public const string Amount = "amount";

        public static readonly IReadOnlyList<string> All = [Score, Ratio, Percent, Pesos, Count, Text, Amount];
    }

    /// <summary>Which way a row improves.</summary>
    public static class Directions
    {
        public const string UpIsBetter   = "up_is_better";
        public const string DownIsBetter = "down_is_better";
        public const string Neutral      = "neutral";

        public static readonly IReadOnlyList<string> All = [UpIsBetter, DownIsBetter, Neutral];
    }

    /// <summary>The BFF's verdict on a row, so the Portal never computes one.</summary>
    public static class Trends
    {
        public const string Better       = "better";
        public const string Worse        = "worse";
        public const string Same         = "same";
        public const string NotAvailable = "n/a";

        public static readonly IReadOnlyList<string> All = [Better, Worse, Same, NotAvailable];
    }

    /// <summary>
    /// Group keys. A company response uses <see cref="Company"/>, in that order; a persona humana response
    /// uses <see cref="Persona"/>, in that order. <see cref="Bcra"/> is shared.
    /// </summary>
    public static class Groups
    {
        public const string Score   = "score";
        public const string Ratios  = "ratios";
        public const string Balance = "balance";
        public const string Bcra    = "bcra";
        public const string Lufe    = "lufe";

        public const string Suggestion = "suggestion";
        public const string Filters    = "filters";
        public const string Indicators = "indicators";
        public const string Coverage   = "coverage";
        public const string Terms      = "terms";

        /// <summary>A company (Balance) response, in order.</summary>
        public static readonly IReadOnlyList<string> Company = [Score, Ratios, Balance, Bcra, Lufe];

        /// <summary>A persona humana response, in order.</summary>
        public static readonly IReadOnlyList<string> Persona = [Suggestion, Filters, Indicators, Bcra, Coverage, Terms];

        /// <summary>Every key.</summary>
        public static readonly IReadOnlyList<string> All =
            [Score, Ratios, Balance, Bcra, Lufe, Suggestion, Filters, Indicators, Coverage, Terms];
    }
}
