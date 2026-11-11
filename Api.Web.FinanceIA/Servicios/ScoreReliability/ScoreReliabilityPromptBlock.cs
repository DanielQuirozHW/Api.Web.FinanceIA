// Project : Api.Web.FinanceIA
// Created : 2026-11-02

using System.Globalization;
using Api.Web.FinanceIA.Models.Financia;
using Codes = Api.Web.FinanceIA.Servicios.ScoreReliability.ScoreReliabilityCodes;
using Messages = Api.Web.FinanceIA.Servicios.ScoreReliability.ScoreReliabilityMessages;

namespace Api.Web.FinanceIA.Servicios.ScoreReliability;

/// <summary>
/// The "CALIDAD DE LOS DATOS" block the scoring narrative and the analyst chat put in front of Claude
/// (score credibility 1b-3): the score-reliability verdict, restated as FIXED es-AR phrases keyed by code.
/// <para>
/// PII rule: a #301 message is NOT prompt-safe — <c>sector_fallback</c> quotes the entity's free-text sector
/// — so nothing here ever reads <see cref="ScoreReliabilityReasonResponse.Message"/>. The only variable parts
/// come from <see cref="ScoreReliabilityReasonResponse.Args"/>, which the evaluator fills with numbers, dates,
/// YYYY-MM periods and ratio keys only: ratio display labels, month counts, Argentine calendar days, periods,
/// fiscal years and counts. An unknown code renders a generic phrase, never its message.
/// </para>
/// </summary>
internal static class ScoreReliabilityPromptBlock
{
    /// <summary>The block's first line.</summary>
    public const string Header = "CALIDAD DE LOS DATOS:";

    /// <summary>The phrase for a code this map does not know.</summary>
    public const string GenericPhrase = "Hay otra observación sobre la calidad de los datos de este score.";

    private const string Bullet = "  • ";

    /// <summary>
    /// The block, lines joined by "\n" with no trailing newline; null when there is no verdict or it is
    /// <c>complete</c> with no reasons — the caller then emits nothing at all.
    /// </summary>
    public static string? Build(ScoreReliabilityResponse? reliability)
    {
        if (reliability is null || reliability.Reasons.Count == 0)
            return null;

        var lines = new List<string>(reliability.Reasons.Count + 2)
        {
            Header,
            $"Confiabilidad del score: {LevelLabel(reliability.Level)}",
        };
        lines.AddRange(reliability.Reasons.Select(r => Bullet + Phrase(r)));

        return string.Join("\n", lines);
    }

    /// <summary>The level in lower-case es-AR, as the prompt states it.</summary>
    public static string LevelLabel(string? level) => level switch
    {
        ScoreReliabilityLevels.Complete    => "completa",
        ScoreReliabilityLevels.WithCaveats => "con observaciones",
        ScoreReliabilityLevels.Provisional => "provisoria",
        ScoreReliabilityLevels.None        => "sin score",
        _                                  => "con observaciones",
    };

    /// <summary>True when <paramref name="reliability"/> raises the reason <paramref name="code"/>.</summary>
    public static bool Has(ScoreReliabilityResponse? reliability, string code)
        => reliability?.Reasons.Any(r => string.Equals(r.Code, code, StringComparison.Ordinal)) == true;

    /// <summary>The fixed phrase for one reason. Internal for tests.</summary>
    internal static string Phrase(ScoreReliabilityReasonResponse reason)
    {
        var args = reason.Args;

        return reason.Code switch
        {
            Codes.ScoreUnavailable =>
                "El análisis no tiene un score calculado.",

            Codes.BcraUnavailable =>
                "No se obtuvo la Central de Deudores del BCRA: el score se calculó sin el componente BCRA.",

            Codes.IntegrityFailed => args?.Count switch
            {
                1        => "La lectura del balance no superó 1 control de consistencia: las cifras extraídas pueden contener errores.",
                > 1      => $"La lectura del balance no superó {Number(args.Count.Value)} controles de consistencia: las cifras extraídas pueden contener errores.",
                _        => "La lectura del balance no superó los controles de consistencia: las cifras extraídas pueden contener errores.",
            },

            // Ratio keys and the before-floor score from Args only (score credibility 1b-5).
            Codes.CriticalFloorUnverified => CriticalFloorUnverified(args),

            // Count and date from Args only: never which figure, its value, the analyst or the reason.
            Codes.ManualCorrection => args switch
            {
                { Count: 1, DateUtc: { } at }      => $"Un analista corrigió manualmente 1 cifra del balance después de la lectura automática (última corrección: {Messages.Date(at)}).",
                { Count: { } n, DateUtc: { } at }  => $"Un analista corrigió manualmente {Number(n)} cifras del balance después de la lectura automática (última corrección: {Messages.Date(at)}).",
                { Count: 1 }                       => "Un analista corrigió manualmente 1 cifra del balance después de la lectura automática.",
                { Count: { } n }                   => $"Un analista corrigió manualmente {Number(n)} cifras del balance después de la lectura automática.",
                _                                  => "Un analista corrigió manualmente cifras del balance después de la lectura automática.",
            },

            Codes.IntegrityPresentation =>
                "El resultado neto no concilia con el resultado antes de impuestos y el impuesto leídos; puede deberse a la " +
                "forma en que está presentado el estado de resultados (por ejemplo, el RECPAM expuesto después del impuesto) " +
                "o a una cifra tomada de la columna del ejercicio anterior. Las cifras del balance cierran.",

            Codes.TrialBalance =>
                "El documento es un balance de sumas y saldos del sistema contable, no estados contables certificados.",

            Codes.IntegrityUnverified =>
                "No se pudo verificar la estructura del balance extraído: los controles de totales no se ejecutaron.",

            Codes.FiscalYearMismatch => args?.FiscalYear is { } requested
                ? $"El ejercicio que figura en el balance no coincide con el ejercicio {Number(requested)} solicitado."
                : "El ejercicio que figura en el balance no coincide con el solicitado.",

            Codes.RatiosLowCoverage => HasRatios(args)
                ? $"Se pudieron calcular muy pocos ratios: {Messages.Labels(args!.RatioKeys)} no se midieron y se puntuaron con un valor neutro."
                : "Se pudieron calcular muy pocos ratios: los que no se midieron se puntuaron con un valor neutro.",

            Codes.ChequesUnavailable =>
                "No se pudo consultar la base de cheques rechazados del BCRA: el score puede no incluir esa penalidad.",

            Codes.RatiosNotMeasured => HasRatios(args)
                ? (args!.RatioKeys.Count == 1 ? "No se pudo medir " : "No se pudieron medir ") +
                  $"{Messages.Labels(args.RatioKeys)} por falta de datos en el balance; el score usó los ratios restantes."
                : "Algunos ratios no se pudieron medir por falta de datos en el balance; el score usó los ratios restantes.",

            Codes.NegativeEquity =>
                "El patrimonio neto es negativo o nulo: Apalancamiento y ROE se excluyeron del score.",

            Codes.RatiosNotMeaningful => HasRatios(args)
                ? (args!.RatioKeys.Count == 1
                    ? $"No se pudo calcular de forma significativa {Messages.Labels(args.RatioKeys)}, que se excluyó del score."
                    : $"No se pudieron calcular de forma significativa {Messages.Labels(args.RatioKeys)}, que se excluyeron del score.")
                : "Algunos ratios no se pudieron calcular de forma significativa y se excluyeron del score.",

            Codes.RatiosFromModel =>
                "Los ratios provienen de la lectura automática del documento, no de un recálculo sobre el balance extraído.",

            Codes.AmountsUnitAssumed =>
                "El balance no indicaba la unidad de los importes y se asumió una.",

            // Never the sector text: the message quotes it, this phrase must not.
            Codes.SectorFallback =>
                "El sector de la empresa no corresponde a ningún perfil conocido: se usaron los umbrales del perfil General.",

            Codes.BalanceStale => args is { FiscalYear: { } year, Months: { } months }
                ? $"El balance del ejercicio {Number(year)} tiene al menos {Messages.Months(months)} de antigüedad a la fecha del score."
                : "El balance tiene una antigüedad considerable a la fecha del score.",

            Codes.LufeCarriedForward => args?.DateUtc is { } asOf
                ? $"La consulta de ventas a LUFE falló y se usó una serie obtenida el {Messages.Date(asOf)}."
                : "La consulta de ventas a LUFE falló y se usó una serie obtenida anteriormente.",

            Codes.LufeUnavailable =>
                "No se pudo consultar la serie de ventas de LUFE: el score no incluye la señal de caída de ventas.",

            Codes.LufeStale => LufeStale(args),

            Codes.CasfogUnavailable =>
                "No se pudo consultar CASFOG: el score no incluye la señal de mora en garantías.",

            Codes.CasfogStale => args is { DateUtc: { } fetchedAt, Months: { } casfogMonths }
                ? $"Los datos de CASFOG se obtuvieron el {Messages.Date(fetchedAt)}, {Messages.Months(casfogMonths)} antes de la fecha del score."
                : "Los datos de CASFOG están desactualizados a la fecha del score.",

            Codes.BcraSituacionUnreadable =>
                "No se pudo interpretar la situación informada por el BCRA: ese componente se puntuó con un valor neutro.",

            Codes.BcraPeriodLag => args is { Periodo: { } periodo, PeriodoMonths: { } lag }
                ? $"La Central de Deudores del BCRA corresponde al período {Messages.Periodo(periodo)}, {Messages.Months(lag)} antes de la fecha del score."
                : "La Central de Deudores del BCRA corresponde a un período atrasado respecto de la fecha del score.",

            Codes.BcraNotApplicable =>
                "No se consultó el BCRA porque la empresa no tiene identificación fiscal registrada: el score no incluye ese componente.",

            Codes.MacroImputed =>
                "No se obtuvo el dato de inflación del BCRA: el componente macroeconómico se puntuó con un valor neutro.",

            Codes.ConfigThresholdFallback => HasRatios(args)
                ? $"Los umbrales configurados para {Messages.Labels(args!.RatioKeys)} estaban desordenados: se usaron los valores por defecto."
                : "Algunos umbrales configurados estaban desordenados: se usaron los valores por defecto.",

            Codes.FactsUnavailable =>
                "No quedó registrada la verificación completa de los datos de este score.",

            _ => GenericPhrase,
        };
    }

    private static string LufeStale(ScoreReliabilityReasonArgs? args)
    {
        var parts = new List<string>(2);
        if (args is { DateUtc: { } at, Months: { } snapshotMonths })
            parts.Add($"se obtuvieron el {Messages.Date(at)}, {Messages.Months(snapshotMonths)} antes de la fecha del score");
        if (args is { Periodo: { } periodo, PeriodoMonths: { } salesMonths })
            parts.Add($"la última venta informada es de {Messages.Periodo(periodo)}, {Messages.Months(salesMonths)} antes de la fecha del score");

        return parts.Count == 0
            ? "Los datos de LUFE están desactualizados a la fecha del score."
            : $"Los datos de LUFE están desactualizados: {string.Join("; ", parts)}.";
    }

    private static string CriticalFloorUnverified(ScoreReliabilityReasonArgs? args)
    {
        var ratios = HasRatios(args) ? Messages.Labels(args!.RatioKeys) : "un ratio";
        var phrase = $"El score quedó fijado por el piso crítico de {ratios}, " +
                     (HasRatios(args) && args!.RatioKeys.Count > 1 ? "calculados" : "calculado") +
                     " con cifras del balance que no superaron los controles de consistencia.";

        return args?.Score is { } score
            ? $"{phrase} Sin ese piso el score sería {Number(score)}/1000."
            : phrase;
    }

    private static bool HasRatios(ScoreReliabilityReasonArgs? args) => args is { RatioKeys.Count: > 0 };

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
