// Project : Api.Web.FinanceIA
// Created : 2026-11-02

using System.Globalization;
using Api.Web.FinanceIA.Clases.Time;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.ScoreReliability;

/// <summary>
/// The es-AR text of every score-reliability reason — the ONLY place it lives. Each message names what is
/// known (ratios, dates, periods, the sector text, month counts) and never a code, a snake_case key or an
/// English word. Ratio names come from <see cref="ClaudeService.RatioDisplayLabel"/>, the labels the
/// Portal and the PDF show.
/// </summary>
internal static class ScoreReliabilityMessages
{
    // ── none ────────────────────────────────────────────────────────────────

    public const string ScoreUnavailable =
        "Este análisis no tiene un score calculado.";

    // ── provisional ─────────────────────────────────────────────────────────

    public const string BcraUnavailableLookupFailed =
        "No se pudo consultar la Central de Deudores del BCRA: el score se calculó sin el componente BCRA.";

    public const string BcraUnavailableNoDeudaBlock =
        "La respuesta del BCRA no incluyó la Central de Deudores: el score se calculó sin el componente BCRA.";

    public const string BcraUnavailableNoData =
        "No se obtuvo información del BCRA: el score se calculó sin el componente BCRA.";

    public static string IntegrityFailed(int failedChecks) => failedChecks switch
    {
        <= 0 => "La extracción del balance no superó los controles de consistencia: los importes leídos pueden contener errores.",
        1    => "La extracción del balance no superó 1 control de consistencia: los importes leídos pueden contener errores.",
        _    => $"La extracción del balance no superó {failedChecks} controles de consistencia: los importes leídos pueden contener errores.",
    };

    /// <summary>
    /// Policy 6: <see cref="IntegrityFailed"/> when the failed net_profit_reconciliation is a column mix — the result
    /// before tax and the income tax were read from the comparative column. Names the cause; no figure.
    /// </summary>
    public static string IntegrityFailedColumnMix(int failedChecks) =>
        IntegrityFailed(failedChecks) + " " + IntegrityColumnMixSentence;

    /// <summary>The sentence <see cref="IntegrityFailedColumnMix"/> appends.</summary>
    public const string IntegrityColumnMixSentence =
        "El resultado antes de impuestos y el impuesto parecen tomados de la columna del ejercicio anterior; revisá el estado de resultados.";

    /// <summary>
    /// Score credibility 1b-5: the critical floor of <paramref name="ratioKeys"/> fixed the score, and those
    /// ratios were computed from balance figures that failed a consistency check. <paramref name="scoreBeforeFloor"/>
    /// is on the displayed 0–1000 scale; null drops that sentence.
    /// </summary>
    public static string CriticalFloorUnverified(IReadOnlyList<string> ratioKeys, int? scoreBeforeFloor)
    {
        var calculated = ratioKeys.Count > 1 ? "calculados" : "calculado";
        var without    = scoreBeforeFloor is { } score
            ? $" Sin ese piso sería {score.ToString(CultureInfo.InvariantCulture)}/1000."
            : string.Empty;

        return $"El score está fijado por el piso crítico de {Labels(ratioKeys)}, {calculated} con cifras del balance que no cierran.{without} " +
               "Corregí las cifras o confirmalas contra el documento antes de decidir.";
    }

    /// <summary>
    /// Phase 10-F3a: figures of the balance were corrected by hand after the automatic reading. Count and
    /// date only — never which figure, its value or the analyst's reason.
    /// </summary>
    public static string ManualCorrection(int count, DateTime? lastAtUtc)
    {
        var figures = count == 1 ? "1 cifra" : $"{count.ToString(CultureInfo.InvariantCulture)} cifras";
        return lastAtUtc is { } at
            ? $"Se corrigieron manualmente {figures} del balance después de la lectura automática (última corrección: {Date(at)})."
            : $"Se corrigieron manualmente {figures} del balance después de la lectura automática.";
    }

    /// <summary>
    /// Policy 5: the only failed check is net_profit_reconciliation, neither a sign flip nor a column mix; the balance
    /// figures add up. It names the POSSIBLE causes and asserts none: the check cannot tell a presentation (RECPAM
    /// after the tax) from a figure taken from the comparative column that it could not check.
    /// </summary>
    public const string IntegrityPresentation =
        "El resultado neto no concilia con el resultado antes de impuestos y el impuesto leídos: puede deberse a cómo " +
        "está presentado el estado de resultados (por ejemplo, el RECPAM después del impuesto) o a una cifra tomada de " +
        "la columna del ejercicio anterior. Las cifras del balance cierran.";

    /// <summary>
    /// The balance was read off a trial balance (sumas y saldos) printed from the accounting system. A caveat:
    /// whether it should weigh more is pending a decision with the SGR.
    /// </summary>
    public const string TrialBalance =
        "El documento es un balance de sumas y saldos del sistema contable, no estados contables certificados.";

    /// <summary>
    /// The extraction is not verified: one clause per reason it records (<c>ExtractionIntegrityReader</c>'s
    /// <c>notVerifiedReasons</c>, carried by the facts since they exist), e.g. "No se pudo verificar la estructura
    /// del balance extraído: no se pudo comprobar que el pasivo total coincida con la suma del pasivo corriente y el
    /// no corriente." No reason, or any reason this build does not know, gives <see cref="IntegrityUnverifiedGeneric"/>:
    /// a code never reaches the text.
    /// </summary>
    public static string IntegrityUnverified(IReadOnlyList<string>? notVerifiedReasons)
    {
        if (notVerifiedReasons is not { Count: > 0 })
            return IntegrityUnverifiedGeneric;

        var clauses = new List<string>(notVerifiedReasons.Count);
        foreach (var reason in notVerifiedReasons)
        {
            if (reason is null || !NotVerifiedReasonClause.TryGetValue(reason, out var clause))
                return IntegrityUnverifiedGeneric;
            if (!clauses.Contains(clause, StringComparer.Ordinal))
                clauses.Add(clause);
        }

        return $"{IntegrityUnverifiedLead}: {string.Join("; ", clauses)}.";
    }

    /// <summary>The <see cref="IntegrityUnverified"/> text when the reasons are unknown or absent.</summary>
    public const string IntegrityUnverifiedGeneric =
        IntegrityUnverifiedLead + ": los controles de totales no llegaron a ejecutarse.";

    private const string IntegrityUnverifiedLead = "No se pudo verificar la estructura del balance extraído";

    // One clause per not-verified reason: the Portal's NOT_VERIFIED_REASON_ES sentences
    // (apps/portal/src/lib/extraction-integrity.ts) with the first letter lowercased and the final period dropped,
    // so both sides say the same thing. Keep them in step.
    private static readonly IReadOnlyDictionary<string, string> NotVerifiedReasonClause =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ExtractionIntegrityResponse.IntegrityUnavailableReason] =
                "no se pudo evaluar si las cifras del balance cierran",
            [BalanceIntegrityService.AssetsComponentsCheck + ExtractionIntegrityResponse.SkippedReasonSuffix] =
                "no se pudo comprobar que el activo total coincida con la suma del activo corriente y el no corriente",
            [BalanceIntegrityService.LiabilitiesComponentsCheck + ExtractionIntegrityResponse.SkippedReasonSuffix] =
                "no se pudo comprobar que el pasivo total coincida con la suma del pasivo corriente y el no corriente",
            [BalanceIntegrityService.AccountingEquationCheck + ExtractionIntegrityResponse.SkippedReasonSuffix] =
                "no se pudo comprobar que el activo total coincida con la suma del pasivo total y el patrimonio neto",
        };

    public static string FiscalYearMismatch(int? fiscalYear) => fiscalYear is { } year
        ? $"El ejercicio que figura en el balance no coincide con el ejercicio {year.ToString(CultureInfo.InvariantCulture)} solicitado para el análisis."
        : "El ejercicio que figura en el balance no coincide con el solicitado para el análisis.";

    public static string RatiosLowCoverage(IReadOnlyList<string> ratioKeys) => ratioKeys.Count == 0
        ? "Se pudieron calcular muy pocos ratios: los que no se midieron se puntuaron con un valor neutro."
        : $"Se pudieron calcular muy pocos ratios: {Labels(ratioKeys)} no se midieron y se puntuaron con un valor neutro.";

    // ── caveat ──────────────────────────────────────────────────────────────

    public const string ChequesUnavailable =
        "No se pudo consultar la base de cheques rechazados del BCRA: el score puede no incluir una penalidad por cheques.";

    public static string RatiosNotMeasured(IReadOnlyList<string> ratioKeys) => ratioKeys.Count == 1
        ? $"No se pudo medir {Labels(ratioKeys)} por falta de datos en el balance: el score se calculó con los ratios restantes."
        : $"No se pudieron medir {Labels(ratioKeys)} por falta de datos en el balance: el score se calculó con los ratios restantes.";

    public const string NegativeEquity =
        "El patrimonio neto es negativo o nulo: Apalancamiento y ROE no tienen sentido y se excluyeron del score.";

    public static string RatiosNotMeaningful(IReadOnlyList<string> ratioKeys) => ratioKeys.Count == 1
        ? $"No se pudo calcular {Labels(ratioKeys)} de forma significativa (por ejemplo, por un denominador en cero) y se excluyó del score."
        : $"No se pudieron calcular {Labels(ratioKeys)} de forma significativa (por ejemplo, por un denominador en cero) y se excluyeron del score.";

    public const string RatiosFromModel =
        "Los ratios provienen de la lectura automática del documento y no de un recálculo sobre el balance extraído.";

    public const string AmountsUnitAssumed =
        "El balance no indicaba la unidad de los importes (pesos, miles o millones) y se asumió una.";

    public static string SectorFallback(string? sectorRaw) => string.IsNullOrWhiteSpace(sectorRaw)
        ? "El sector de la empresa no corresponde a ningún perfil conocido: se usaron los umbrales del perfil «General»."
        : $"El sector «{sectorRaw.Trim()}» no corresponde a ningún perfil conocido: se usaron los umbrales del perfil «General».";

    public static string BalanceStale(int months, int fiscalYear) =>
        $"El balance del ejercicio {fiscalYear.ToString(CultureInfo.InvariantCulture)} tiene al menos {Months(months)} de antigüedad a la fecha del score " +
        "(contados desde el 31/12, porque no se registra el mes de cierre).";

    public static string LufeCarriedForward(DateTime? ventasAsOf) => ventasAsOf is { } asOf
        ? $"La consulta de ventas a LUFE falló y se usó la serie obtenida el {Date(asOf)}."
        : "La consulta de ventas a LUFE falló y se usó una serie obtenida anteriormente.";

    public const string LufeUnavailable =
        "No se pudo consultar la serie de ventas de LUFE: el score no incluye la señal de caída de ventas.";

    /// <param name="snapshotAt">The snapshot date, when it is the stale part.</param>
    /// <param name="snapshotMonths">Its age at the score date.</param>
    /// <param name="ultimoPeriodo">The latest sales month (year, month), when it is the stale part.</param>
    /// <param name="salesMonths">Its age at the score date.</param>
    public static string LufeStale(DateTime? snapshotAt, int snapshotMonths, (int Year, int Month)? ultimoPeriodo, int salesMonths)
    {
        var parts = new List<string>(2);
        if (snapshotAt is { } at)
            parts.Add($"los datos se obtuvieron el {Date(at)}, {Months(snapshotMonths)} antes de la fecha del score");
        if (ultimoPeriodo is { } periodo)
            parts.Add($"la última venta informada es de {Periodo(periodo)}, {Months(salesMonths)} antes de la fecha del score");

        return $"Los datos de LUFE están desactualizados: {string.Join("; ", parts)}.";
    }

    public const string CasfogUnavailable =
        "No se pudo consultar CASFOG: el score no incluye la señal de mora en garantías.";

    public static string CasfogStale(DateTime fetchedAt, int months) =>
        $"Los datos de CASFOG se obtuvieron el {Date(fetchedAt)}, {Months(months)} antes de la fecha del score.";

    public const string BcraSituacionUnreadable =
        "No se pudo interpretar la situación informada por la Central de Deudores del BCRA: el componente BCRA se puntuó con un valor neutro.";

    public static string BcraPeriodLag((int Year, int Month) periodo, int months) =>
        $"La Central de Deudores del BCRA corresponde al período {Periodo(periodo)}, {Months(months)} antes de la fecha del score.";

    public const string BcraNotApplicable =
        "La empresa no tiene CUIT registrado, por lo que no se consultó el BCRA: el score no incluye ese componente.";

    public const string MacroImputed =
        "No se obtuvo el dato de inflación del BCRA: el componente macroeconómico se puntuó con un valor neutro.";

    public static string ConfigThresholdFallback(IReadOnlyList<string> ratioKeys) => ratioKeys.Count == 0
        ? "Algunos umbrales configurados estaban desordenados: se usaron los valores por defecto."
        : $"Los umbrales configurados para {Labels(ratioKeys)} estaban desordenados: se usaron los valores por defecto.";

    /// <summary>The row predates the facts (#300): the user-facing text of record.</summary>
    public const string FactsUnavailableBeforeRecording =
        "Analizado antes de que se registrara la verificación completa de los datos; reanalizalo para verificarlo.";

    /// <summary>The facts block was written as null: building it failed when the score was persisted.</summary>
    public const string FactsUnavailableNotRecorded =
        "No se pudo registrar la verificación completa de los datos de este score; reanalizalo para verificarlo.";

    /// <summary>The breakdown (or its facts block) could not be read.</summary>
    public const string FactsUnavailableUnreadable =
        "No se pudo leer el detalle de la verificación de este score; reanalizalo para verificarlo.";

    // ── formatting ──────────────────────────────────────────────────────────

    /// <summary>"Liquidez corriente", "Liquidez corriente y ROE", "A, B y C" — Portal labels, canonical order.</summary>
    internal static string Labels(IReadOnlyList<string> ratioKeys)
    {
        var labels = ratioKeys.Select(Label).ToList();
        return labels.Count switch
        {
            0 => "algunos ratios",
            1 => labels[0],
            _ => $"{string.Join(", ", labels.Take(labels.Count - 1))} y {labels[^1]}",
        };
    }

    /// <summary>The Portal label of a ratio key; an unknown key becomes a neutral phrase rather than leaking the key.</summary>
    internal static string Label(string ratioKey)
        => ClaudeService.RatioDisplayLabel.TryGetValue(ratioKey, out var label) ? label : "otro indicador";

    internal static string Months(int months) => months == 1 ? "1 mes" : $"{months.ToString(CultureInfo.InvariantCulture)} meses";

    /// <summary>
    /// The calendar day an analyst in Argentina reads: the instant is projected to Argentine time first
    /// (<see cref="ArgentineTime.ToLocal"/>, the BFF's one Argentina-time helper), so 01:30Z on the 2nd is
    /// the 1st — formatted from UTC it would print the next day for every instant after 21:00 AR.
    /// </summary>
    internal static string Date(DateTime utc)
        => ArgentineTime.ToLocal(utc).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    internal static string Periodo((int Year, int Month) periodo)
        => $"{periodo.Month.ToString("00", CultureInfo.InvariantCulture)}/{periodo.Year.ToString(CultureInfo.InvariantCulture)}";
}
