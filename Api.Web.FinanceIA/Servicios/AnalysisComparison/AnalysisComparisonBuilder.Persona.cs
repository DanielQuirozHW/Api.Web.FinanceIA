// Project : Api.Web.FinanceIA
// Created : 2026-11-08

using System.Globalization;
using System.Text.Json;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.PersonEvaluation;
using Codes = Api.Web.FinanceIA.Models.Financia.AnalysisComparisonCodes;

namespace Api.Web.FinanceIA.Servicios.AnalysisComparison;

/// <summary>
/// The persona humana half of <see cref="AnalysisComparisonBuilder"/>: two frozen solicitante evaluations
/// (<see cref="PersonComparisonFacts"/>) side by side. No score — the evaluation draws none — so the groups are
/// the flag, the pre-filters, the headline indicators, the BCRA situation, the documentary coverage and the
/// terms. Every column is read with <see cref="PersonEvaluationJson"/>, the reader the detail, the PDF and the
/// re-score summary use, so a figure here is the figure the analyst saw.
/// </summary>
/// <remarks>
/// <para>
/// Unreadable sources are reported per COLUMN and side (<c>filters</c>, <c>indicators</c>, <c>coverage</c>,
/// <c>parameters</c>), never as a value. FiltersJson feeds three groups (suggestion, filters, bcra): a side
/// that does not read drops all three. A blank column, the literal <c>null</c> and an empty <c>{}</c>/<c>[]</c>
/// are absent, not unreadable: that side simply reads null.
/// </para>
/// <para>
/// Amounts: solvencia_pn is nominal pesos and the two evaluations are always taken at different dates, so its
/// trend is never judged (<c>nominal_amounts</c>). The terms are in their own currency (unit <c>amount</c>, the
/// side header carries it); when the currencies differ the amount rows keep both figures but no delta,
/// deltaPct or trend (<c>currency_changed</c>).
/// </para>
/// </remarks>
internal static partial class AnalysisComparisonBuilder
{
    private const string SourceFilters    = "filters";
    private const string SourceIndicators = "indicators";
    private const string SourceCoverage   = "coverage";
    private const string SourceParameters = "parameters";

    private const string BcraSituacionFilterId = "bcra_situacion";
    private const string BcraSinHistorial      = "sin_historial";

    /// <summary>The periodicity a row written before DDL 078 means: a monthly cuota.</summary>
    private const int DefaultCuotasPorAnio = 12;

    // Least to most severe: the order the suggestion row's trend reads.
    private static readonly IReadOnlyList<string> SuggestionSeverity =
        [PersonSuggestions.ParaDecisionDelAnalista, PersonSuggestions.RechazoSugerido];

    private static readonly RowSpec SuggestionRow     = new("suggestion",       "Sugerencia",          Codes.Units.Text,  Codes.Directions.DownIsBetter, HasPercentChange: false);
    private static readonly RowSpec BlockingFilterRow = new("blocking_filters", "Filtros bloqueantes", Codes.Units.Count, Codes.Directions.DownIsBetter, HasPercentChange: false);
    private static readonly RowSpec WarningRow        = new("warnings",         "Advertencias",        Codes.Units.Count, Codes.Directions.DownIsBetter, HasPercentChange: false);

    /// <summary>How an indicator's stored value becomes a row value.</summary>
    private enum IndicatorScale
    {
        /// <summary>A plain ratio (times), 2 decimals.</summary>
        Ratio,

        /// <summary>A fraction shown as a percentage: ×100, 2 decimals.</summary>
        Percent,

        /// <summary>Pesos, no decimals.</summary>
        Pesos,
    }

    // The solicitante's headline indicators, as the evaluation defines them. cuota_ingreso_estres is not here:
    // only a fiador's evaluation computes it.
    private static readonly IReadOnlyList<(RowSpec Spec, string IndicatorId, IndicatorScale Scale)> PersonIndicatorRows =
    [
        (new("cobertura_servicio_deuda", "Cobertura del servicio de deuda",    Codes.Units.Ratio,   Codes.Directions.UpIsBetter),   "cobertura_servicio_deuda", IndicatorScale.Ratio),
        (new("cuota_ingreso",            "Cuota / ingreso",                    Codes.Units.Percent, Codes.Directions.DownIsBetter), "cuota_ingreso",            IndicatorScale.Percent),
        (new("solvencia_pn",             "Patrimonio neto (valor de mercado)", Codes.Units.Pesos,   Codes.Directions.UpIsBetter),   "solvencia_pn",             IndicatorScale.Pesos),
        (new("patrimonio_operativo",     "Patrimonio operativo",               Codes.Units.Percent, Codes.Directions.DownIsBetter), "patrimonio_operativo",     IndicatorScale.Percent),
    ];

    private static readonly RowSpec CoverageIndexRow   = new("coverage_index",   "Cobertura documental",            Codes.Units.Percent, Codes.Directions.UpIsBetter);
    private static readonly RowSpec RequiredPresentRow = new("required_present", "Documentos requeridos presentes", Codes.Units.Count,   Codes.Directions.UpIsBetter, HasPercentChange: false);
    private static readonly RowSpec RequiredTotalRow   = new("required_total",   "Documentos requeridos",           Codes.Units.Count,   Codes.Directions.Neutral,    HasPercentChange: false);

    private static readonly RowSpec RequestedAmountRow = new("requested_amount", "Monto solicitado", Codes.Units.Amount, Codes.Directions.Neutral);
    private static readonly RowSpec InstallmentRow     = new("installment",      "Cuota",            Codes.Units.Amount, Codes.Directions.Neutral);
    private static readonly RowSpec FiadoresRow        = new("fiadores",         "Fiadores",         Codes.Units.Count,  Codes.Directions.Neutral, HasPercentChange: false);

    /// <summary>One side's evaluation, read once. A null member is absent or unreadable (see the flags).</summary>
    private sealed record PersonSide(
        PersonComparisonFacts Facts,
        IReadOnlyList<PersonFilterResult>? Filters,
        bool FiltersOk,
        IReadOnlyList<PersonIndicatorResult>? Indicators,
        bool IndicatorsOk,
        PersonCoverageResult? Coverage,
        bool CoverageOk,
        PersonEvaluationSources? Sources,
        bool SourcesOk);

    /// <summary>Compares two persona humana analyses; both must carry <see cref="AnalysisComparisonSnapshot.Person"/>.</summary>
    public static Outcome BuildPersona(AnalysisComparisonSnapshot current, AnalysisComparisonSnapshot previous)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(previous);
        if (current.Person is null || previous.Person is null)
            throw new ArgumentException("Both sides of a persona humana comparison need their evaluation facts.");

        var unreadable   = new List<string>();
        var currentSide  = ReadPersonSide(current.Person, SideCurrent, unreadable);
        var previousSide = ReadPersonSide(previous.Person, SidePrevious, unreadable);

        // Two evaluations are always taken at different dates: nominal pesos are never judged.
        var context = new Context(SameFiscalYear: false);
        var sameCurrency = string.Equals(current.Person.Currency, previous.Person.Currency, StringComparison.Ordinal);

        var groups = new List<AnalysisComparisonGroup>();
        var filtersReadable = currentSide.FiltersOk && previousSide.FiltersOk;

        if (filtersReadable)
        {
            AddGroup(groups, Codes.Groups.Suggestion, "Sugerencia",
                [SuggestionRowFor(SuggestionOf(currentSide), SuggestionOf(previousSide))]);

            var currentCounts  = currentSide.Filters is { } cf ? PersonFilterCounts.Of(cf) : null;
            var previousCounts = previousSide.Filters is { } pf ? PersonFilterCounts.Of(pf) : null;
            AddGroup(groups, Codes.Groups.Filters, "Filtros",
            [
                Row(BlockingFilterRow, currentCounts?.Rechazo, previousCounts?.Rechazo, context),
                Row(WarningRow,        currentCounts?.Alerta,  previousCounts?.Alerta,  context),
            ]);
        }

        if (currentSide.IndicatorsOk && previousSide.IndicatorsOk)
        {
            AddGroup(groups, Codes.Groups.Indicators, "Indicadores",
                PersonIndicatorRows.Select(spec => Row(spec.Spec,
                    IndicatorValue(currentSide.Indicators, spec.IndicatorId, spec.Scale),
                    IndicatorValue(previousSide.Indicators, spec.IndicatorId, spec.Scale),
                    context)));
        }

        if (filtersReadable)
        {
            AddGroup(groups, Codes.Groups.Bcra, "BCRA",
                [Row(WorstSituationRow, WorstBcraSituation(currentSide.Filters), WorstBcraSituation(previousSide.Filters), context)]);
        }

        if (currentSide.CoverageOk && previousSide.CoverageOk)
        {
            AddGroup(groups, Codes.Groups.Coverage, "Cobertura documental",
            [
                Row(CoverageIndexRow,   Percent(currentSide.Coverage?.Index),   Percent(previousSide.Coverage?.Index),   context),
                Row(RequiredPresentRow, currentSide.Coverage?.RequiredPresent,  previousSide.Coverage?.RequiredPresent,  context),
                Row(RequiredTotalRow,   currentSide.Coverage?.RequiredTotal,    previousSide.Coverage?.RequiredTotal,    context),
            ]);
        }

        AddGroup(groups, Codes.Groups.Terms, "Condiciones de la operación",
        [
            AmountRow(RequestedAmountRow, current.Person.AmountAtCharge,    previous.Person.AmountAtCharge,    sameCurrency, context),
            AmountRow(InstallmentRow,     current.Person.InstallmentAmount, previous.Person.InstallmentAmount, sameCurrency, context),
            Row(FiadoresRow, current.Person.FiadorIds.Count, previous.Person.FiadorIds.Count, context),
        ]);

        var notes = BuildPersonNotes(currentSide, previousSide, groups, sameCurrency);

        var response = new AnalysisComparisonResponse
        {
            Available      = true,
            Current        = PersonHeader(current),
            Previous       = PersonHeader(previous),
            SameFiscalYear = null,
            Notes          = notes,
            Groups         = groups,
        };

        return new Outcome(response, unreadable);
    }

    // ── Reading ───────────────────────────────────────────────────────────────

    private static PersonSide ReadPersonSide(PersonComparisonFacts facts, string side, List<string> unreadable)
    {
        var (filters, filtersOk)       = ReadPersonColumn<List<PersonFilterResult>>(facts.FiltersJson);
        var (indicators, indicatorsOk) = ReadPersonColumn<List<PersonIndicatorResult>>(facts.IndicatorsJson);
        var (coverage, coverageOk)     = ReadPersonColumn<PersonCoverageResult>(facts.CoverageJson);
        var (sources, sourcesOk)       = ReadPersonColumn<PersonEvaluationSources>(facts.DocumentVersionsJson);

        if (!filtersOk)    unreadable.Add($"{SourceFilters}:{side}");
        if (!indicatorsOk) unreadable.Add($"{SourceIndicators}:{side}");
        if (!coverageOk)   unreadable.Add($"{SourceCoverage}:{side}");
        if (!sourcesOk)    unreadable.Add($"{SourceParameters}:{side}");

        return new PersonSide(facts, filters, filtersOk, indicators, indicatorsOk, coverage, coverageOk, sources, sourcesOk);
    }

    /// <summary>
    /// One frozen column through <see cref="PersonEvaluationJson.TryDeserialize{T}"/>. (null, true) when it is
    /// absent — blank, the literal null, or an empty object/array (the columns' defaults); (null, false) when
    /// it is there and does not read. Never throws.
    /// </summary>
    private static (T? Value, bool Ok) ReadPersonColumn<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
            return (null, true);

        var trimmed = json.Trim();
        if (trimmed is "null" or "{}" or "[]")
            return (null, true);

        try
        {
            var value = PersonEvaluationJson.TryDeserialize<T>(json);
            return (value, value is not null);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return (null, false);
        }
    }

    /// <summary>The flag, by the rule it was written with (<see cref="PersonEvaluationService.Suggest"/>); null without filters.</summary>
    private static string? SuggestionOf(PersonSide side)
        => side.Filters is { } filters ? PersonEvaluationService.Suggest(filters) : null;

    private static decimal? IndicatorValue(IReadOnlyList<PersonIndicatorResult>? indicators, string id, IndicatorScale scale)
    {
        // A SinDatos indicator carries no value; a missing one reads the same.
        if (indicators?.FirstOrDefault(i => i.Id == id)?.Value is not { } value)
            return null;

        return scale switch
        {
            IndicatorScale.Percent => Percent(value),
            IndicatorScale.Pesos   => Math.Round(value, 0, MidpointRounding.AwayFromZero),
            _                      => Math.Round(value, 2, MidpointRounding.AwayFromZero),
        };
    }

    /// <summary>
    /// The worst BCRA situación the bcra_situacion pre-filter judged: the current one or the worst within its
    /// window, whichever is higher. "No debt reported" is a real answer, situación 1 — as for a company. A
    /// filter without data (SinDatos), or none at all, is null: an outage is not a clean record.
    /// </summary>
    private static decimal? WorstBcraSituation(IReadOnlyList<PersonFilterResult>? filters)
    {
        var filter = filters?.FirstOrDefault(f => f.Id == BcraSituacionFilterId);
        if (filter is null || filter.Outcome == PersonFilterOutcomes.SinDatos)
            return null;

        if (filter.Inputs.TryGetValue("estado", out var estado) && estado == BcraSinHistorial)
            return 1m;

        int? worst = null;
        foreach (var key in new[] { "situacionActual", "peorSituacionVentana" })
        {
            if (filter.Inputs.TryGetValue(key, out var raw)
                && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var situacion))
            {
                worst = worst is { } w ? Math.Max(w, situacion) : situacion;
            }
        }

        return worst;
    }

    // ── Rows ──────────────────────────────────────────────────────────────────

    /// <summary>The suggestion row: text values, trend by severity (analyst's decision &lt; suggested rejection).</summary>
    private static AnalysisComparisonRow? SuggestionRowFor(string? current, string? previous)
    {
        if (current is null && previous is null)
            return null;

        var currentRank  = RankOf(current);
        var previousRank = RankOf(previous);

        var trend = currentRank is { } c && previousRank is { } p
            ? c == p ? Codes.Trends.Same : c < p ? Codes.Trends.Better : Codes.Trends.Worse
            : Codes.Trends.NotAvailable;

        return new AnalysisComparisonRow
        {
            Key       = SuggestionRow.Key,
            Label     = SuggestionRow.Label,
            Unit      = SuggestionRow.Unit,
            Current   = current,
            Previous  = previous,
            Delta     = null,
            DeltaPct  = null,
            Direction = SuggestionRow.Direction,
            Trend     = trend,
        };

        static int? RankOf(string? suggestion)
        {
            for (var i = 0; i < SuggestionSeverity.Count; i++)
            {
                if (string.Equals(SuggestionSeverity[i], suggestion, StringComparison.Ordinal))
                    return i;
            }

            return null;
        }
    }

    /// <summary>An amount of the terms; across different currencies the figures stay and nothing is computed.</summary>
    private static AnalysisComparisonRow? AmountRow(
        RowSpec spec, decimal? current, decimal? previous, bool sameCurrency, Context context)
    {
        var row = Row(spec, current, previous, context);

        return row is null || sameCurrency
            ? row
            : row with { Delta = null, DeltaPct = null, Trend = Codes.Trends.NotAvailable };
    }

    // ── Header and notes ──────────────────────────────────────────────────────

    private static AnalysisComparisonSide PersonHeader(AnalysisComparisonSnapshot snapshot)
    {
        var currency = snapshot.Person?.Currency;
        return Side(snapshot) with { Currency = string.IsNullOrWhiteSpace(currency) ? null : currency };
    }

    private static List<string> BuildPersonNotes(
        PersonSide current, PersonSide previous, IReadOnlyList<AnalysisComparisonGroup> groups, bool sameCurrency)
    {
        var notes = new List<string>();

        if (groups.Any(g => g.Rows.Any(r => r.Unit == Codes.Units.Pesos)))
            notes.Add(Codes.Notes.NominalAmounts);

        if (!string.Equals(current.Facts.EvaluationVersion, previous.Facts.EvaluationVersion, StringComparison.Ordinal))
            notes.Add(Codes.Notes.EngineChanged);

        // Only when both sides' frozen parameters read: with one missing, whether they changed is unknown.
        if (current.Sources?.Parameters is { } currentParameters
            && previous.Sources?.Parameters is { } previousParameters
            && !string.Equals(ParametersFingerprint(currentParameters), ParametersFingerprint(previousParameters), StringComparison.Ordinal))
        {
            notes.Add(Codes.Notes.ParametersChanged);
        }

        if (!sameCurrency)
            notes.Add(Codes.Notes.CurrencyChanged);

        if (TermsChanged(current, previous))
            notes.Add(Codes.Notes.TermsChanged);

        return notes;
    }

    /// <summary>
    /// The cuts and rules themselves, serialised. <c>TenantOverrides</c> is cleared: it says where a value came
    /// from (a tenant row or the code default), not what it is.
    /// </summary>
    private static string ParametersFingerprint(PersonEvaluationParameters parameters)
        => JsonSerializer.Serialize(parameters with { TenantOverrides = [] }, PersonEvaluationJson.Options);

    /// <summary>Amount, installment, periodicity or the set of fiadores differ.</summary>
    private static bool TermsChanged(PersonSide current, PersonSide previous)
    {
        var c = current.Facts;
        var p = previous.Facts;

        return c.AmountAtCharge != p.AmountAtCharge
            || c.InstallmentAmount != p.InstallmentAmount
            || CuotasPorAnioOf(current) != CuotasPorAnioOf(previous)
            || !c.FiadorIds.ToHashSet().SetEquals(p.FiadorIds);
    }

    /// <summary>The periodicity as the detail resolves it: the column, else the frozen agro mode, else monthly.</summary>
    private static int CuotasPorAnioOf(PersonSide side)
        => side.Facts.CuotasPorAnio ?? side.Sources?.Agro?.CuotasPorAnio ?? DefaultCuotasPorAnio;
}
