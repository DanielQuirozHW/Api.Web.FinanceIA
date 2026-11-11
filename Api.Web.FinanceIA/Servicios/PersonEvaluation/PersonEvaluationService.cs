// Project : Api.Web.FinanceIA
// Created : 2026-10-20

using System.Globalization;
using Api.Web.FinanceIA.Clases.Time;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.PersonEvaluation;

/// <summary>
/// The persona humana evaluation of the applicant (M10 Phase 4a "v0"): pre-filters, indicators and the
/// documentary coverage index, computed from a legajo that is already loaded. PURE and deterministic —
/// no I/O, no clock (the instant arrives in the input), no model call — so the same input always yields
/// the same output and a test can pin every branch.
/// </summary>
/// <remarks>
/// <para>
/// NO SCORE AND NO LEVEL. The only conclusion is a flag (<see cref="PersonSuggestions"/>): a suggested
/// rejection when a pre-filter says so, otherwise the analyst decides. Every cut is a <c>pf_</c> tenant
/// parameter with a PROVISIONAL default (<see cref="PersonEvaluationDefaults"/>), awaiting the SGR's answers.
/// </para>
/// <para>
/// A MISSING SOURCE IS NEVER OK. A filter whose source is absent or failed reports
/// <see cref="PersonFilterOutcomes.SinDatos"/>, and an indicator with a missing input reports
/// <see cref="PersonIndicatorStatuses.SinDatos"/> with the reason. The ScoringService of the PyME path is
/// not touched.
/// </para>
/// </remarks>
public static partial class PersonEvaluationService
{
    /// <summary>
    /// The engine version, frozen on every AnalysisParticipants row (EvaluationVersion) and used as the
    /// analysis's PromptVersion suffix. Bump it whenever a rule, a formula or a default changes.
    /// </summary>
    /// <remarks>
    /// 0.10: the coverage index measures against the tenant's expected-documents matrix (per regime, with
    /// per-line recency cuts) and freezes it in the sources. Versions are dotted numbers — "0.10" comes AFTER
    /// "0.9" — so anything that orders them must use <see cref="EngineVersion.Comparer"/>, never text order.
    /// </remarks>
    public const string Version = "0.10";

    /// <summary>Evaluates the applicant.</summary>
    public static PersonEvaluationResult Evaluate(PersonEvaluationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var asOf  = DateOnly.FromDateTime(ArgentineTime.ToLocal(input.AsOfUtc));
        var facts = IncomeFacts.From(input);

        var filters = new List<PersonFilterResult>
        {
            CertificadoMipyme(input, asOf),
            BcraSituacion(input),
            BcraEventosGraves(input),
            BcraChequesImpagos(input, asOf),
            ArcaSucesion(input),
            ArcaCuitActivo(input),
            AntiguedadActividad(input, asOf)
        };

        if (input.FiscalRegime == FiscalRegimes.Monotributo)
            filters.Add(MonotributoTope(input, facts));
        else
            filters.Add(ConsumoDeclarado(input, facts));

        var acreedorVsFisco = VentasAcreedorVsFisco(input, facts);
        filters.Add(AcreedorVsFiscoFilter(acreedorVsFisco));

        var flujo = FlujoActividad(input, facts);
        var indicators = new List<PersonIndicatorResult>
        {
            VentasNivel(facts),
            VentasTendencia(facts),
            flujo,
            CuotaIngreso(input, facts),
            CoberturaServicioDeuda(input, flujo),
            SolvenciaPn(input),
            Endeudamiento(input),
            PatrimonioOperativo(input),
            VentasCoherencia(input),
            acreedorVsFisco
        };

        var coverage = Coverage(input, facts, asOf, filters);

        return new PersonEvaluationResult
        {
            Version    = Version,
            Suggestion = Suggest(filters),
            Filters    = filters,
            Indicators = indicators,
            Coverage   = coverage
        };
    }

    /// <summary>
    /// The flag: a suggested rejection when any pre-filter is a Rechazo, otherwise the analyst's call.
    /// Public so a stored evaluation is read back with the same rule it was written with.
    /// </summary>
    public static string Suggest(IReadOnlyList<PersonFilterResult> filters)
    {
        ArgumentNullException.ThrowIfNull(filters);

        return filters.Any(f => f.Outcome == PersonFilterOutcomes.Rechazo)
            ? PersonSuggestions.RechazoSugerido
            : PersonSuggestions.ParaDecisionDelAnalista;
    }

    // ── Shared helpers ───────────────────────────────────────────────────────────

    /// <summary>Invariant, trailing-zero-free decimal.</summary>
    private static string F(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>Invariant decimal, null-aware.</summary>
    private static string? F(decimal? value) => value is { } v ? F(v) : null;

    /// <summary>Invariant integer.</summary>
    private static string F(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>ISO date.</summary>
    private static string F(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>es-AR pesos for the evidence text.</summary>
    private static string Pesos(decimal value)
        => "$ " + value.ToString("#,##0", CultureInfo.GetCultureInfo("es-AR"));

    /// <summary>es-AR percentage for the evidence text.</summary>
    private static string Pct(decimal fraction)
        => (fraction * 100m).ToString("0.#", CultureInfo.GetCultureInfo("es-AR")) + " %";

    /// <summary>
    /// Whole months from <paramref name="from"/> to <paramref name="to"/>, counting only completed ones. A
    /// month-end date counts the month as complete at the end of a shorter month (31 Mar → 30 Sep is 6).
    /// </summary>
    internal static int MonthsBetween(DateOnly from, DateOnly to)
    {
        var months = (to.Year - from.Year) * 12 + to.Month - from.Month;
        var anniversaryDay = Math.Min(from.Day, DateTime.DaysInMonth(to.Year, to.Month));
        if (to.Day < anniversaryDay)
            months--;
        return months;
    }

    /// <summary>A month as a single integer (year × 12 + month − 1), for window arithmetic.</summary>
    internal static int MonthIndex(DateOnly date) => date.Year * 12 + date.Month - 1;

    /// <summary>Parses a YYYYMM period (BCRA, ARCA) into its first day; null when malformed.</summary>
    internal static DateOnly? ParseYearMonth(string? period)
    {
        if (string.IsNullOrWhiteSpace(period))
            return null;

        var digits = period.Trim();
        if (digits.Length < 6
            || !int.TryParse(digits[..4], NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            || !int.TryParse(digits[4..6], NumberStyles.None, CultureInfo.InvariantCulture, out var month)
            || year < 1900 || month is < 1 or > 12)
        {
            return null;
        }

        return new DateOnly(year, month, 1);
    }

    private static PersonFilterResult Filter(
        string id, string outcome, string? severity, string evidence, string source,
        Dictionary<string, string?>? inputs = null, string evidenceKind = PersonEvidenceKinds.Verificado)
        => new()
        {
            Id           = id,
            Outcome      = outcome,
            Severity     = outcome == PersonFilterOutcomes.Alerta ? severity ?? PersonSeverities.Normal : null,
            EvidenceKind = evidenceKind,
            Evidence     = evidence,
            Source       = source,
            Inputs       = inputs ?? []
        };

    private static PersonIndicatorResult NoData(
        string id, string unit, string evidence, Dictionary<string, string?>? inputs = null,
        IReadOnlyList<Guid>? documents = null, string evidenceKind = PersonEvidenceKinds.Declarado)
        => new()
        {
            Id                = id,
            Status            = PersonIndicatorStatuses.SinDatos,
            Value             = null,
            Unit              = unit,
            EvidenceKind      = evidenceKind,
            Evidence          = evidence,
            Inputs            = inputs ?? [],
            SourceDocumentIds = documents ?? []
        };
}
