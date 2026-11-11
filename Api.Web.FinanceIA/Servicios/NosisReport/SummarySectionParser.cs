// Project : Api.Web.FinanceIA
// Created : 2026-11-10

using Api.Web.FinanceIA.Models.Nosis;

namespace Api.Web.FinanceIA.Servicios.NosisReport;

/// <summary>
/// <c>summary</c>: whitelisted labels from PERFIL / INFORMACIÓN GENERAL (where the headline figures
/// print: total endeudamiento and compromisos of the financial system, cheques, consultas, and a label
/// row with antigüedad and empleados), ARCA, MI PYME and RESUMEN CONFIGURABLE (the fallback). Only the labels below are read; everything else in those sections (addresses, phones,
/// e-mails, the business name) is never looked at. A label the report does not print stays null.
/// </summary>
internal static class SummarySectionParser
{
    /// <summary>
    /// The summary and its status: "ok" when at least one field was read, "unreadable" when one of its
    /// headings is there but no field was, "absent" when none of its headings is.
    /// </summary>
    public static (NosisReportSummary? Summary, string Status) Parse(NosisReportSplit split, DateTime reportDate)
    {
        var sections = split.Sections;
        var profile = Lines(sections, NosisReportHeadingKind.Profile);
        var arca    = Lines(sections, NosisReportHeadingKind.Arca);
        var mipyme  = Lines(sections, NosisReportHeadingKind.MiPyme);
        var resumen = Lines(sections, NosisReportHeadingKind.Resumen);

        // The header block's label row first (score and situación as printed there), then the labels.
        var header = NosisReportHeaderRow.Read(new[] { split.Preamble, profile ?? [], resumen ?? [] });

        if (profile is null && arca is null && mipyme is null && resumen is null && header.Score is null && header.Situacion is null)
            return (null, NosisReportSectionKeys.StatusAbsent);

        // The headline figures print in INFORMACIÓN GENERAL ("Total de endeudamiento sistema financiero:
        // $ …"); RESUMEN CONFIGURABLE is the fallback. Each source keeps its own number style and "miles".
        var sources = new List<(IReadOnlyList<NosisReportLine> Lines, NosisNumberStyle Style, bool Thousands)>();
        foreach (var kind in new[] { NosisReportHeadingKind.Profile, NosisReportHeadingKind.Resumen })
        {
            if (sections.TryGetValue(kind, out var section))
                sources.Add((section.Lines, section.NumberStyle, section.InThousands));
        }

        var profileStyle = sections.TryGetValue(NosisReportHeadingKind.Profile, out var p) ? p.NumberStyle : NosisNumberStyle.Unknown;
        var prof = profile ?? [];
        var (consultas3m, consultas12m) = Consultas(sources);

        var summary = new NosisReportSummary
        {
            Score                          = header.Score ?? Integer(sources, "Score"),
            SituacionBcra                  = header.Situacion ?? Integer(sources, "Peor situación BCRA", "Situación BCRA", "Situación"),
            EndeudamientoTotal             = Amount(sources, "Total de endeudamiento sistema financiero", "Total de endeudamiento",
                                                    "Endeudamiento total", "Endeudamiento", "Deuda total"),
            CompromisosMensuales           = Amount(sources, "Total de compromisos mensuales sistema financiero",
                                                    "Total de compromisos mensuales", "Compromisos mensuales"),
            TieneChequesRechazados         = FirstFlag(sources, "Cheques rechazados"),
            ReferenciasComercialesCantidad = Integer(sources, "Referencias comerciales"),
            AportesImpagos12m              = FirstFlag(sources, "Aportes impagos últimos 12 meses", "Aportes impagos 12 meses", "Aportes impagos"),
            Consultas3m                    = consultas3m ?? Integer(sources, "Consultas últimos 3 meses", "Consultas 3 meses"),
            Consultas12m                   = consultas12m ?? Integer(sources, "Consultas últimos 12 meses", "Consultas 12 meses"),
            AntiguedadAnios                = NosisReportNumbers.FirstInteger(Value(prof, "Antigüedad"), profileStyle)
                                             ?? NosisReportNumbers.FirstInteger(NosisReportText.ValueBelow(prof, "Antigüedad"), profileStyle),
            EmpleadosRango                 = NosisReportText.ValueOf(prof, "Rango de empleados", "Cantidad de empleados", "Empleados")
                                             ?? NosisReportText.ValueBelow(prof, "Cantidad de empleados"),
            ActividadPrincipal             = NosisReportText.ValueOf(prof, "Actividad principal", "Actividad"),
            CertificadoMiPyme              = mipyme is null ? null : Certificado(mipyme, reportDate),
            Arca                           = arca is null ? null : Arca(arca),
        };

        var (facturacionMin, facturacionMax) = FacturacionOf(sources);
        summary = summary with { FacturacionEstimadaMin = facturacionMin, FacturacionEstimadaMax = facturacionMax };

        var anyField = summary.Score is not null || summary.SituacionBcra is not null
                       || summary.EndeudamientoTotal is not null || summary.CompromisosMensuales is not null
                       || summary.FacturacionEstimadaMin is not null || summary.FacturacionEstimadaMax is not null
                       || summary.TieneChequesRechazados is not null || summary.ReferenciasComercialesCantidad is not null
                       || summary.AportesImpagos12m is not null || summary.Consultas3m is not null
                       || summary.Consultas12m is not null || summary.AntiguedadAnios is not null
                       || summary.EmpleadosRango is not null || summary.ActividadPrincipal is not null
                       || summary.CertificadoMiPyme is not null || summary.Arca is not null;

        return anyField
            ? (summary, NosisReportSectionKeys.StatusOk)
            : (null, NosisReportSectionKeys.StatusUnreadable);
    }

    private static IReadOnlyList<NosisReportLine>? Lines(
        IReadOnlyDictionary<NosisReportHeadingKind, NosisReportSection> sections, NosisReportHeadingKind kind)
        => sections.TryGetValue(kind, out var section) ? section.Lines : null;

    /// <summary>The value after a label starting a line, else the word after it anywhere in a line.</summary>
    private static string? Value(IReadOnlyList<NosisReportLine> lines, params string[] labels)
        => NosisReportText.ValueOf(lines, labels) ?? NosisReportText.TokenAfter(lines, labels);

    private static readonly System.Text.RegularExpressions.Regex ConsultasWindow = new(
        @"ULTIMOS\s+(\d+)\s+MESES:?\s*([\d.,]+)", System.Text.RegularExpressions.RegexOptions.CultureInvariant
        | System.Text.RegularExpressions.RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

    private static decimal? Amount(
        List<(IReadOnlyList<NosisReportLine> Lines, NosisNumberStyle Style, bool Thousands)> sources, params string[] labels)
    {
        foreach (var (lines, style, thousands) in sources)
        {
            if (NosisReportNumbers.FirstAmount(Value(lines, labels), style) is { } amount)
                return NosisReportNumbers.ToPesos(amount, thousands);
        }

        return null;
    }

    private static int? Integer(
        List<(IReadOnlyList<NosisReportLine> Lines, NosisNumberStyle Style, bool Thousands)> sources, params string[] labels)
    {
        foreach (var (lines, style, _) in sources)
        {
            if (NosisReportNumbers.FirstInteger(Value(lines, labels), style) is { } value)
                return value;
        }

        return null;
    }

    private static bool? FirstFlag(
        List<(IReadOnlyList<NosisReportLine> Lines, NosisNumberStyle Style, bool Thousands)> sources, params string[] labels)
    {
        foreach (var (lines, style, _) in sources)
        {
            if (Flag(Value(lines, labels), style) is { } flag)
                return flag;
        }

        return null;
    }

    /// <summary>"Total de consultas en Nosis: Últimos 3 meses: 7 | Últimos 12 meses: 25" → (7, 25).</summary>
    private static (int? Three, int? Twelve) Consultas(
        List<(IReadOnlyList<NosisReportLine> Lines, NosisNumberStyle Style, bool Thousands)> sources)
    {
        int? three = null, twelve = null;
        foreach (var (lines, style, _) in sources)
        {
            foreach (var line in lines)
            {
                foreach (System.Text.RegularExpressions.Match m in ConsultasWindow.Matches(line.Normalised))
                {
                    var count = NosisReportNumbers.FirstInteger(m.Groups[2].Value, style);
                    if (m.Groups[1].Value == "3") three ??= count;
                    else if (m.Groups[1].Value == "12") twelve ??= count;
                }
            }
        }

        return (three, twelve);
    }

    private static (decimal? Min, decimal? Max) FacturacionOf(
        List<(IReadOnlyList<NosisReportLine> Lines, NosisNumberStyle Style, bool Thousands)> sources)
    {
        foreach (var (lines, style, thousands) in sources)
        {
            var (min, max) = Facturacion(
                Value(lines, "Facturación estimada anual", "Facturación anual estimada", "Facturación estimada", "Facturación"), style);
            if (min is not null || max is not null)
                return (NosisReportNumbers.ToPesos(min, thousands), NosisReportNumbers.ToPesos(max, thousands));
        }

        return (null, null);
    }

    /// <summary>
    /// Sí/No, "(No) existen / registra / posee …", or a count (more than zero = yes).
    /// </summary>
    private static bool? Flag(string? value, NosisNumberStyle style)
    {
        if (NosisReportText.YesNo(value) is { } yesNo)
            return yesNo;

        var key = NosisReportText.Normalise(value);
        if (key.StartsWith("EXISTE", StringComparison.Ordinal) || key.StartsWith("REGISTRA", StringComparison.Ordinal)
            || key.StartsWith("POSEE", StringComparison.Ordinal) || key.StartsWith("TIENE", StringComparison.Ordinal))
            return true;

        return NosisReportNumbers.FirstInteger(value, style) is { } count ? count > 0 : null;
    }

    /// <summary>"6,000,000 a 9,000,000" → both; "Hasta / Menor a X" → max; "Más de / Mayor a / Desde X" → min; "X" → both.</summary>
    private static (decimal? Min, decimal? Max) Facturacion(string? value, NosisNumberStyle style)
    {
        var amounts = NosisReportNumbers.Amounts(value, style);
        if (amounts.Count == 0)
            return (null, null);
        if (amounts.Count >= 2)
            return (amounts[0], amounts[1]);

        // "anual: Mayor a $ X" — the qualifier may follow a word the label did not take.
        var key = NosisReportText.Normalise(value);
        if (key.Contains("HASTA", StringComparison.Ordinal) || key.Contains("MENOR A", StringComparison.Ordinal))
            return (null, amounts[0]);
        if (key.Contains("MAS DE", StringComparison.Ordinal) || key.Contains("MAYOR A", StringComparison.Ordinal)
            || key.Contains("DESDE", StringComparison.Ordinal))
            return (amounts[0], null);

        return (amounts[0], amounts[0]);
    }

    private static NosisReportArca? Arca(IReadOnlyList<NosisReportLine> lines)
    {
        var arca = new NosisReportArca
        {
            Inscripcion = NosisReportText.FirstDate(NosisReportText.ValueOf(lines, "Fecha de inscripción", "Fecha inscripción", "Inscripción")),
            Ganancias   = NosisReportText.ValueOf(lines, "Impuesto a las ganancias", "Ganancias"),
            Iva         = NosisReportText.ValueOf(lines, "IVA"),
        };

        return arca.Inscripcion is null && arca.Ganancias is null && arca.Iva is null ? null : arca;
    }

    private static NosisReportCertificadoMiPyme? Certificado(IReadOnlyList<NosisReportLine> lines, DateTime reportDate)
    {
        DateOnly? desde = null, hasta = null;

        var vigencia = NosisReportText.Dates(NosisReportText.ValueOf(lines, "Vigencia", "Período de vigencia"));
        if (vigencia.Count >= 2)
            (desde, hasta) = (vigencia[0], vigencia[1]);

        desde ??= NosisReportText.FirstDate(NosisReportText.ValueOf(lines, "Vigencia desde", "Fecha desde", "Fecha de emisión", "Desde", "Emisión"));
        hasta ??= NosisReportText.FirstDate(NosisReportText.ValueOf(lines, "Vigencia hasta", "Fecha hasta", "Fecha de vencimiento", "Vencimiento", "Hasta"));

        bool? vigente = NosisReportText.YesNo(Value(lines, "Vigente", "Certificado vigente"));
        if (vigente is null)
        {
            var estado = NosisReportText.Normalise(NosisReportText.ValueOf(lines, "Estado", "Certificado", "Estado del certificado"));
            if (estado.Contains("NO VIGENTE", StringComparison.Ordinal) || estado.Contains("VENCID", StringComparison.Ordinal)
                || estado.Contains("BAJA", StringComparison.Ordinal))
                vigente = false;
            else if (estado.Contains("VIGENTE", StringComparison.Ordinal))
                vigente = true;
        }

        if (vigente is null && NosisReportText.SaysNone(lines))
            vigente = false;

        if (vigente is null && hasta is not null)
            vigente = hasta.Value >= DateOnly.FromDateTime(reportDate);

        return vigente is null ? null : new NosisReportCertificadoMiPyme { Vigente = vigente.Value, Desde = desde, Hasta = hasta };
    }
}
