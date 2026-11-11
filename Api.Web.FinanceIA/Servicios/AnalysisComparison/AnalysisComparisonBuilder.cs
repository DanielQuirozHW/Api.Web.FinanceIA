// Project : Api.Web.FinanceIA
// Created : 2026-11-06

using System.Globalization;
using System.Text.Json;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.ScoreReliability;
using Codes = Api.Web.FinanceIA.Models.Financia.AnalysisComparisonCodes;

namespace Api.Web.FinanceIA.Servicios.AnalysisComparison;

/// <summary>
/// Builds "Cambios respecto al análisis anterior" from two company analyses (<see cref="Build"/>) or two
/// persona humana analyses (<c>BuildPersona</c>, in the Persona partial). Pure: no I/O, no logging, never
/// throws for a blob — a group whose source does not read on either side is left out and its key
/// reported in <see cref="Outcome.UnreadableKeys"/> for the caller to log (keys only, never values).
/// </summary>
/// <remarks>
/// <para>
/// Every source is the one the analysis detail renders: the score and level stored on the result
/// (<see cref="ScoreScale.ToNosis"/>), the persisted ratios (<c>RatiosJson</c>, which is the
/// deterministic recompute the scoring stage wrote), the flat <c>BalanceJson</c> (pesos), the BCRA
/// scalars <c>JSON_VALUE</c> extracted (read with <see cref="BcraSituacionRule"/>), and the breakdown
/// the score was built from.
/// </para>
/// <para>
/// Amounts are NOMINAL pesos. When the fiscal years differ a pesos row keeps its figures and deltas but
/// gets trend "n/a" — inflation alone moves them — and the <c>nominal_amounts</c> note is added.
/// </para>
/// </remarks>
internal static partial class AnalysisComparisonBuilder
{
    /// <summary>What was built, and the sources that could not be read (as "group:side").</summary>
    internal sealed record Outcome(AnalysisComparisonResponse Response, IReadOnlyList<string> UnreadableKeys);

    private const string SideCurrent  = "current";
    private const string SidePrevious = "previous";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // Least to most severe: the order the trend of the level row reads.
    private static readonly IReadOnlyList<string> LevelSeverity = ["Low", "Medium", "High", "Critical"];

    private sealed record RowSpec(string Key, string Label, string Unit, string Direction, bool HasPercentChange = true);

    // The ratios in the detail's order and wording (PdfExportService.RatioMetaMap).
    private static readonly IReadOnlyList<(RowSpec Spec, Func<RatiosData, double?> Read)> RatioRows =
    [
        (new("current_ratio",     "Liquidez corriente",     Codes.Units.Ratio,   Codes.Directions.UpIsBetter),   r => r.CurrentRatio),
        (new("acid_test",         "Prueba ácida",           Codes.Units.Ratio,   Codes.Directions.UpIsBetter),   r => r.AcidTest),
        (new("debt_ratio",        "Endeudamiento",          Codes.Units.Ratio,   Codes.Directions.DownIsBetter), r => r.DebtRatio),
        (new("leverage",          "Apalancamiento",         Codes.Units.Ratio,   Codes.Directions.DownIsBetter), r => r.Leverage),
        (new("roe",               "ROE",                    Codes.Units.Percent, Codes.Directions.UpIsBetter),   r => r.Roe),
        (new("roa",               "ROA",                    Codes.Units.Percent, Codes.Directions.UpIsBetter),   r => r.Roa),
        (new("net_margin",        "Margen neto",            Codes.Units.Percent, Codes.Directions.UpIsBetter),   r => r.NetMargin),
        (new("interest_coverage", "Cobertura de intereses", Codes.Units.Ratio,   Codes.Directions.UpIsBetter),   r => r.InterestCoverage),
    ];

    private static readonly IReadOnlyList<(RowSpec Spec, Func<BalanceAccounts, double?> Read)> BalanceRows =
    [
        (new("assets",              "Activo total",        Codes.Units.Pesos, Codes.Directions.Neutral),    b => b.TotalAssets),
        (new("current_assets",      "Activo corriente",    Codes.Units.Pesos, Codes.Directions.Neutral),    b => b.CurrentAssets),
        (new("liabilities",         "Pasivo total",        Codes.Units.Pesos, Codes.Directions.Neutral),    b => b.TotalLiabilities),
        (new("current_liabilities", "Pasivo corriente",    Codes.Units.Pesos, Codes.Directions.Neutral),    b => b.CurrentLiabilities),
        (new("equity",              "Patrimonio neto",     Codes.Units.Pesos, Codes.Directions.UpIsBetter), b => b.Equity),
        (new("net_revenue",         "Ventas netas",        Codes.Units.Pesos, Codes.Directions.UpIsBetter), b => b.NetRevenue),
        (new("operating_result",    "Resultado operativo", Codes.Units.Pesos, Codes.Directions.UpIsBetter), b => b.OperatingResult),
        (new("net_profit",          "Resultado neto",      Codes.Units.Pesos, Codes.Directions.UpIsBetter), b => b.NetProfit),
        (new("inventory",           "Bienes de cambio",    Codes.Units.Pesos, Codes.Directions.Neutral),    b => b.Inventory),
    ];

    private static readonly RowSpec ScoreRow       = new("score",        "Score",                     Codes.Units.Score, Codes.Directions.UpIsBetter);
    private static readonly RowSpec LevelRow       = new("level",        "Nivel de riesgo",           Codes.Units.Text,  Codes.Directions.DownIsBetter);
    private static readonly RowSpec ScoreRatiosRow = new("score_ratios", "Score de ratios",           Codes.Units.Score, Codes.Directions.UpIsBetter);
    private static readonly RowSpec ScoreBcraRow   = new("score_bcra",   "Score BCRA",                Codes.Units.Score, Codes.Directions.UpIsBetter);
    private static readonly RowSpec ScoreMacroRow  = new("score_macro",  "Score de contexto macro",   Codes.Units.Score, Codes.Directions.UpIsBetter);

    private static readonly RowSpec WorstSituationRow  = new("worst_situation",         "Peor situación BCRA",         Codes.Units.Count, Codes.Directions.DownIsBetter, HasPercentChange: false);
    private static readonly RowSpec TotalDebtRow       = new("total_debt",              "Deuda en el sistema financiero", Codes.Units.Pesos, Codes.Directions.DownIsBetter);
    private static readonly RowSpec ChequesCountRow    = new("rejected_cheques_count",  "Cheques rechazados",          Codes.Units.Count, Codes.Directions.DownIsBetter);
    private static readonly RowSpec ChequesAmountRow   = new("rejected_cheques_amount", "Monto de cheques rechazados", Codes.Units.Pesos, Codes.Directions.DownIsBetter);

    private static readonly RowSpec SalesVariationRow = new("sales_variation", "Variación de ventas declaradas (LUFE)", Codes.Units.Percent, Codes.Directions.UpIsBetter);

    /// <summary>Compares <paramref name="current"/> with <paramref name="previous"/>.</summary>
    public static Outcome Build(AnalysisComparisonSnapshot current, AnalysisComparisonSnapshot previous)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(previous);

        var sameFiscalYear = current.FiscalYear == previous.FiscalYear;
        var context        = new Context(sameFiscalYear);
        var unreadable     = new List<string>();

        var (currentBreakdown, currentBreakdownState)   = ComparisonScoreBreakdown.Parse(current.ScoreBreakdownJson);
        var (previousBreakdown, previousBreakdownState) = ComparisonScoreBreakdown.Parse(previous.ScoreBreakdownJson);
        var breakdownsReadable = currentBreakdownState != ScoreReliabilityBreakdownState.Malformed
                              && previousBreakdownState != ScoreReliabilityBreakdownState.Malformed;
        if (currentBreakdownState == ScoreReliabilityBreakdownState.Malformed)
            unreadable.Add($"{Codes.Groups.Score}:{SideCurrent}");
        if (previousBreakdownState == ScoreReliabilityBreakdownState.Malformed)
            unreadable.Add($"{Codes.Groups.Score}:{SidePrevious}");

        var groups = new List<AnalysisComparisonGroup>();

        AddGroup(groups, Codes.Groups.Score, "Score",
            BuildScoreRows(current, previous, currentBreakdown, previousBreakdown, breakdownsReadable, context));

        AddGroup(groups, Codes.Groups.Ratios, "Ratios financieros",
            BuildFromBlob<RatiosData>(current.RatiosJson, previous.RatiosJson, RatioRows,
                Codes.Groups.Ratios, unreadable, context));

        AddGroup(groups, Codes.Groups.Balance, "Balance",
            BuildFromBlob<BalanceAccounts>(current.BalanceJson, previous.BalanceJson, BalanceRows,
                Codes.Groups.Balance, unreadable, context));

        AddGroup(groups, Codes.Groups.Bcra, "BCRA", BuildBcraRows(current, previous, unreadable, context));

        // The variation the score USED, read off the breakdown — never recomputed from LUFE here.
        if (breakdownsReadable)
        {
            AddGroup(groups, Codes.Groups.Lufe, "LUFE",
            [
                Row(SalesVariationRow,
                    Percent(currentBreakdown?.VentasVariacion), Percent(previousBreakdown?.VentasVariacion), context),
            ]);
        }

        var notes = BuildNotes(current, previous, currentBreakdown, previousBreakdown, sameFiscalYear);

        var response = new AnalysisComparisonResponse
        {
            Available      = true,
            Current        = Side(current),
            Previous       = Side(previous),
            SameFiscalYear = sameFiscalYear,
            Notes          = notes,
            Groups         = groups,
        };

        return new Outcome(response, unreadable);
    }

    /// <summary>The header of one side.</summary>
    internal static AnalysisComparisonSide Side(AnalysisComparisonSnapshot snapshot) => new()
    {
        AnalysisId    = snapshot.AnalysisId,
        ResultId      = snapshot.ResultId,
        FiscalYear    = snapshot.FiscalYear,
        Status        = snapshot.Status,
        DecidedAt     = snapshot.DecidedAt,
        Score         = snapshot.AiSuggestedScore is { } score ? ScoreScale.ToNosis(score) : null,
        Level         = snapshot.AiSuggestedLevel,
        PromptVersion = snapshot.PromptVersion,
    };

    // ── Groups ────────────────────────────────────────────────────────────────

    private static List<AnalysisComparisonRow?> BuildScoreRows(
        AnalysisComparisonSnapshot current,
        AnalysisComparisonSnapshot previous,
        ComparisonScoreBreakdown? currentBreakdown,
        ComparisonScoreBreakdown? previousBreakdown,
        bool breakdownsReadable,
        Context context)
    {
        List<AnalysisComparisonRow?> rows =
        [
            Row(ScoreRow, Nosis(current.AiSuggestedScore), Nosis(previous.AiSuggestedScore), context),
            LevelRowFor(current.AiSuggestedLevel, previous.AiSuggestedLevel),
        ];

        // The sub-scores come from the breakdown; when either side's does not read they are left out
        // rather than compared against a hole. The score and level above come from columns and stay.
        if (breakdownsReadable)
        {
            rows.Add(Row(ScoreRatiosRow, Nosis(currentBreakdown?.ScoreRatios), Nosis(previousBreakdown?.ScoreRatios), context));
            rows.Add(Row(ScoreBcraRow,   Nosis(BcraScore(currentBreakdown)),   Nosis(BcraScore(previousBreakdown)),   context));
            rows.Add(Row(ScoreMacroRow,  Nosis(currentBreakdown?.ScoreMacro),  Nosis(previousBreakdown?.ScoreMacro),  context));
        }

        return rows;
    }

    /// <summary>
    /// Rows read off one JSON blob per side with a typed reader. A side whose blob does not read drops the
    /// whole group (one half of a comparison is not a comparison) and is reported unreadable. A blank blob
    /// is not unreadable: its side is simply null on every row.
    /// </summary>
    private static List<AnalysisComparisonRow?> BuildFromBlob<TBlob>(
        string? currentJson,
        string? previousJson,
        IReadOnlyList<(RowSpec Spec, Func<TBlob, double?> Read)> specs,
        string groupKey,
        List<string> unreadable,
        Context context)
        where TBlob : class
    {
        var (currentBlob, currentOk)   = TryRead<TBlob>(currentJson);
        var (previousBlob, previousOk) = TryRead<TBlob>(previousJson);

        if (!currentOk)  unreadable.Add($"{groupKey}:{SideCurrent}");
        if (!previousOk) unreadable.Add($"{groupKey}:{SidePrevious}");
        if (!currentOk || !previousOk)
            return [];

        var rows = new List<AnalysisComparisonRow?>(specs.Count);
        foreach (var (spec, read) in specs)
        {
            var decimals = spec.Unit == Codes.Units.Pesos ? 0 : 2;
            rows.Add(Row(spec,
                Rounded(currentBlob is null ? null : read(currentBlob), decimals),
                Rounded(previousBlob is null ? null : read(previousBlob), decimals),
                context));
        }

        return rows;
    }

    /// <summary>
    /// BCRA rows from the scalars JSON_VALUE extracted. A failed lookup, or no block at all, is null — never
    /// zero: an outage is not a clean record. "No debt / no cheques reported" is a real answer: situación 1
    /// (as the engine scores it, <see cref="BcraSituacionRule.Worst"/>) and zero.
    /// </summary>
    private static List<AnalysisComparisonRow?> BuildBcraRows(
        AnalysisComparisonSnapshot current,
        AnalysisComparisonSnapshot previous,
        List<string> unreadable,
        Context context)
    {
        var currentBcra  = ReadBcra(current);
        var previousBcra = ReadBcra(previous);

        if (!currentBcra.Readable)  unreadable.Add($"{Codes.Groups.Bcra}:{SideCurrent}");
        if (!previousBcra.Readable) unreadable.Add($"{Codes.Groups.Bcra}:{SidePrevious}");

        return
        [
            Row(WorstSituationRow, currentBcra.WorstSituation, previousBcra.WorstSituation, context),
            Row(TotalDebtRow,      currentBcra.TotalDebt,      previousBcra.TotalDebt,      context),
            Row(ChequesCountRow,   currentBcra.ChequesCount,   previousBcra.ChequesCount,   context),
            Row(ChequesAmountRow,  currentBcra.ChequesAmount,  previousBcra.ChequesAmount,  context),
        ];
    }

    private sealed record BcraValues(
        decimal? WorstSituation, decimal? TotalDebt, decimal? ChequesCount, decimal? ChequesAmount, bool Readable);

    /// <summary>One side's BCRA figures. A scalar that does not parse leaves that value null and marks the side.</summary>
    private static BcraValues ReadBcra(AnalysisComparisonSnapshot snapshot)
    {
        var readable = true;

        // Deuda — the reading the engine and the detail use.
        var reading   = BcraSituacionRule.Read(snapshot.BcraDeudaEstadoRaw, snapshot.BcraDeudaSituacionMaximaRaw);
        decimal? worst = BcraSituacionRule.Worst(reading) is { } situacion ? situacion : null;
        if (reading.Kind == BcraDeudaReadingKind.Excluded && snapshot.BcraDeudaEstadoRaw is not null
            && reading.ExclusionReason == BcraExclusionReasons.NoDeudaBlock)
        {
            readable = false; // an estado was stored but is not one we know
        }

        decimal? totalDebt = null;
        switch (reading.Kind)
        {
            case BcraDeudaReadingKind.NoDebt:
                totalDebt = 0m;
                break;
            case BcraDeudaReadingKind.Debt when snapshot.BcraDeudaMontoTotalRaw is not null:
                // BCRA reports the Central de Deudores in MILES de pesos; the row is in pesos.
                if (TryParseDecimal(snapshot.BcraDeudaMontoTotalRaw, out var miles))
                    totalDebt = Math.Round(miles * 1000m, 0, MidpointRounding.AwayFromZero);
                else
                    readable = false;
                break;
        }

        // Cheques rechazados — the stored aggregates, by estado.
        decimal? chequesCount  = null;
        decimal? chequesAmount = null;
        if (snapshot.BcraChequesEstadoRaw is not null)
        {
            if (int.TryParse(snapshot.BcraChequesEstadoRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var estado)
                && Enum.IsDefined(typeof(BcraChequesConsultaEstado), estado))
            {
                switch ((BcraChequesConsultaEstado)estado)
                {
                    case BcraChequesConsultaEstado.NoChequesReported:
                        chequesCount  = 0m;
                        chequesAmount = 0m;
                        break;
                    case BcraChequesConsultaEstado.ChequesFound:
                        if (snapshot.BcraChequesTotalRechazadosRaw is not null)
                        {
                            if (TryParseDecimal(snapshot.BcraChequesTotalRechazadosRaw, out var count)) chequesCount = count;
                            else readable = false;
                        }

                        if (snapshot.BcraChequesMontoTotalRechazadoRaw is not null)
                        {
                            if (TryParseDecimal(snapshot.BcraChequesMontoTotalRechazadoRaw, out var amount))
                                chequesAmount = Math.Round(amount, 0, MidpointRounding.AwayFromZero);
                            else readable = false;
                        }

                        break;
                }
            }
            else
            {
                readable = false;
            }
        }

        return new BcraValues(worst, totalDebt, chequesCount, chequesAmount, readable);
    }

    // ── Notes ─────────────────────────────────────────────────────────────────

    private static List<string> BuildNotes(
        AnalysisComparisonSnapshot current,
        AnalysisComparisonSnapshot previous,
        ComparisonScoreBreakdown? currentBreakdown,
        ComparisonScoreBreakdown? previousBreakdown,
        bool sameFiscalYear)
    {
        var notes = new List<string>
        {
            sameFiscalYear ? Codes.Notes.SameFiscalYear : Codes.Notes.NominalAmounts,
        };

        // Only when both breakdowns read: with one missing, which engine produced it is unknown.
        if (currentBreakdown is not null && previousBreakdown is not null
            && !string.Equals(currentBreakdown.ScoringAlgoVersion, previousBreakdown.ScoringAlgoVersion, StringComparison.Ordinal))
        {
            notes.Add(Codes.Notes.EngineChanged);
        }

        if (IntegrityFailed(current))
            notes.Add(Codes.Notes.IntegrityFailedCurrent);
        if (IntegrityFailed(previous))
            notes.Add(Codes.Notes.IntegrityFailedPrevious);

        return notes;
    }

    /// <summary>
    /// The detail's verdict (<see cref="ExtractionIntegrityReader"/>): a check that ran and failed, and not
    /// only a presentation check (an income statement that does not reconcile as it is presented, while the
    /// balance figures add up — a caveat, not a misread). Never throws: the reader does not.
    /// </summary>
    internal static bool IntegrityFailed(AnalysisComparisonSnapshot snapshot)
        => ExtractionIntegrityReader.Read(snapshot.BalanceJson, snapshot.FiscalYear) is { } verdict
           && verdict.FailedChecks.Count > 0
           && !verdict.PresentationOnly;

    // ── Rows ──────────────────────────────────────────────────────────────────

    private sealed record Context(bool SameFiscalYear);

    private static void AddGroup(
        List<AnalysisComparisonGroup> groups, string key, string label, IEnumerable<AnalysisComparisonRow?> rows)
    {
        var kept = rows.OfType<AnalysisComparisonRow>().ToList();
        if (kept.Count > 0)
            groups.Add(new AnalysisComparisonGroup { Key = key, Label = label, Rows = kept });
    }

    /// <summary>A numeric row, or null when both sides are null.</summary>
    private static AnalysisComparisonRow? Row(RowSpec spec, decimal? current, decimal? previous, Context context)
    {
        if (current is null && previous is null)
            return null;

        decimal? delta = current is { } c && previous is { } p ? c - p : null;

        return new AnalysisComparisonRow
        {
            Key       = spec.Key,
            Label     = spec.Label,
            Unit      = spec.Unit,
            Current   = current,
            Previous  = previous,
            Delta     = delta,
            DeltaPct  = spec.HasPercentChange ? PercentChange(current, previous) : null,
            Direction = spec.Direction,
            Trend     = TrendOf(spec, delta, context),
        };
    }

    /// <summary>
    /// (current − previous) / previous × 100, one decimal — only when it means something: null when either side
    /// is null, when previous ≤ 0 (no base to measure a change against), and when the sides have opposite signs
    /// (a figure that crossed zero has no meaningful percentage). With previous &gt; 0 the only opposite sign is a
    /// negative current; a current of zero is a real −100 %. Delta and trend do not depend on it.
    /// </summary>
    internal static decimal? PercentChange(decimal? current, decimal? previous)
    {
        if (current is not { } c || previous is not { } p || p <= 0m || c < 0m)
            return null;

        return Math.Round((c - p) / p * 100m, 1, MidpointRounding.AwayFromZero);
    }

    private static string TrendOf(RowSpec spec, decimal? delta, Context context)
    {
        if (delta is not { } d)
            return Codes.Trends.NotAvailable;

        // Nominal pesos of different years: inflation alone moves them, so no verdict either way.
        if (spec.Unit == Codes.Units.Pesos && !context.SameFiscalYear)
            return Codes.Trends.NotAvailable;

        if (d == 0m)
            return Codes.Trends.Same;

        return spec.Direction switch
        {
            Codes.Directions.UpIsBetter   => d > 0m ? Codes.Trends.Better : Codes.Trends.Worse,
            Codes.Directions.DownIsBetter => d < 0m ? Codes.Trends.Better : Codes.Trends.Worse,
            _                             => Codes.Trends.NotAvailable,
        };
    }

    /// <summary>The level row: text values, trend by severity (Low &lt; Medium &lt; High &lt; Critical).</summary>
    private static AnalysisComparisonRow? LevelRowFor(string? current, string? previous)
    {
        if (string.IsNullOrWhiteSpace(current) && string.IsNullOrWhiteSpace(previous))
            return null;

        var currentRank  = SeverityOf(current);
        var previousRank = SeverityOf(previous);

        var trend = currentRank is { } c && previousRank is { } p
            ? c == p ? Codes.Trends.Same : c < p ? Codes.Trends.Better : Codes.Trends.Worse
            : Codes.Trends.NotAvailable;

        return new AnalysisComparisonRow
        {
            Key       = LevelRow.Key,
            Label     = LevelRow.Label,
            Unit      = LevelRow.Unit,
            Current   = string.IsNullOrWhiteSpace(current) ? null : current,
            Previous  = string.IsNullOrWhiteSpace(previous) ? null : previous,
            Delta     = null,
            DeltaPct  = null,
            Direction = LevelRow.Direction,
            Trend     = trend,
        };
    }

    private static int? SeverityOf(string? level)
    {
        if (string.IsNullOrWhiteSpace(level))
            return null;

        for (var i = 0; i < LevelSeverity.Count; i++)
        {
            if (string.Equals(LevelSeverity[i], level, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return null;
    }

    // ── Readers and conversions ───────────────────────────────────────────────

    /// <summary>
    /// Reads a blob. (null, true) for a blank blob or the JSON literal null; (null, false) for one that does
    /// not read — including a non-object root, which the serializer rejects with a JsonException.
    /// </summary>
    private static (T? Value, bool Ok) TryRead<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
            return (null, true);

        try
        {
            return (JsonSerializer.Deserialize<T>(json, JsonOpts), true);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return (null, false);
        }
    }

    /// <summary>scoreBcra is a placeholder when BCRA was excluded from the score: no figure to compare.</summary>
    private static decimal? BcraScore(ComparisonScoreBreakdown? breakdown)
        => breakdown?.BcraExcluded == true ? null : breakdown?.ScoreBcra;

    private static decimal? Nosis(decimal? internalScore)
        => internalScore is { } score ? ScoreScale.ToNosis(score) : null;

    private static decimal? Percent(decimal? fraction)
        => fraction is { } f ? Math.Round(f * 100m, 2, MidpointRounding.AwayFromZero) : null;

    /// <summary>A double rounded as displayed; null for null, NaN, infinity or a value beyond decimal.</summary>
    private static decimal? Rounded(double? value, int decimals)
    {
        if (value is not { } v || !double.IsFinite(v) || Math.Abs(v) >= 7.9e27)
            return null;

        return Math.Round((decimal)v, decimals, MidpointRounding.AwayFromZero);
    }

    private static bool TryParseDecimal(string raw, out decimal value)
        => decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
