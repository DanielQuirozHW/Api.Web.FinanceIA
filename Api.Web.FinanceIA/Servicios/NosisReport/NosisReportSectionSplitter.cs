// Project : Api.Web.FinanceIA
// Created : 2026-11-10

using System.Text.RegularExpressions;

namespace Api.Web.FinanceIA.Servicios.NosisReport;

/// <summary>
/// Cuts the report's body lines into sections at its upper-case headings.
///
/// <para>
/// A heading CANDIDATE is a line written in capitals with no digit. Its text, accents stripped and a
/// trailing "(…)" dropped, is matched against the known headings — alone, doubled ("ANTECEDENTES
/// FISCALES ANTECEDENTES FISCALES"), or as the last known part of "GROUP / SUBHEADING".
/// </para>
///
/// <para>
/// The STYLE decides, once it can be trusted. The known headings found in the document give their
/// printed style (size, weight, colour). When at least <see cref="MinKnownForStyle"/> of them are found
/// and some are printed unlike the body text:
/// </para>
/// <list type="bullet">
/// <item>a known-text line printed like the body is body text, not a heading (a "CONSULTAS" label);</item>
/// <item>an UNKNOWN caps line printed in a style that at least two known headings share is a heading
/// too — it opens a <see cref="NosisReportHeadingKind.Skip"/> section, so it closes the section before
/// it and its own lines are dropped. Its words must read as one phrase (no column-wide gaps), so a
/// table's column header row is never mistaken for one.</item>
/// </list>
/// <para>
/// Otherwise (a document too small to calibrate, or headings printed like the body) the known texts
/// alone decide, as before.
/// </para>
///
/// <para>
/// Lines under a Skip heading, and lines before the first heading (kept apart as the preamble, see
/// <see cref="NosisReportSplit"/>), never reach a section parser. That is how NÓMINA DE EMPLEADOS,
/// RELACIONES / ÁRBOL, FIRMANTES DE CHEQUES, INFORMACIÓN DE CONTACTO, SOCIEDADES, HOMÓNIMOS and any
/// unknown section stay out of the model.
/// </para>
/// </summary>
internal static class NosisReportSectionSplitter
{
    /// <summary>Known headings needed before their style is trusted.</summary>
    internal const int MinKnownForStyle = 3;

    /// <summary>A gap between two words wider than this many font sizes splits columns, not a phrase.</summary>
    private const double PhraseGapShare = 3d;

    /// <summary>Headings that only group others; the "miles de $" note under one applies to its members.</summary>
    private const string BancariaGroup = "INFORMACION BANCARIA";

    private static readonly Regex TrailingParenthesis = new(
        @"\s*\([^)]*\)\s*$", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

    /// <summary>Known headings (normalised) → kind and the "miles de $" group they belong to, if any.</summary>
    private static readonly Dictionary<string, (NosisReportHeadingKind Kind, string? Group)> Headings = new(StringComparer.Ordinal)
    {
        ["PERFIL DEL CONSULTADO"]                     = (NosisReportHeadingKind.Profile, null),
        ["INFORMACION GENERAL"]                       = (NosisReportHeadingKind.Profile, null),
        ["ARCA"]                                      = (NosisReportHeadingKind.Arca, null),
        ["MI PYME"]                                   = (NosisReportHeadingKind.MiPyme, null),
        ["RESUMEN CONFIGURABLE"]                      = (NosisReportHeadingKind.Resumen, null),
        ["APORTES PATRONALES"]                        = (NosisReportHeadingKind.AportesPatronales, null),
        ["NOMINA DE EMPLEADOS"]                       = (NosisReportHeadingKind.Skip, null),
        ["RELACIONES"]                                = (NosisReportHeadingKind.Skip, null),
        ["ARBOL"]                                     = (NosisReportHeadingKind.Skip, null),
        ["ARBOL DE RELACIONES"]                       = (NosisReportHeadingKind.Skip, null),
        ["FIRMANTES DE CHEQUES"]                      = (NosisReportHeadingKind.Skip, null),
        ["INFORMACION DE CONTACTO"]                   = (NosisReportHeadingKind.Skip, null),
        ["BALANCES"]                                  = (NosisReportHeadingKind.Skip, null),
        ["ULTIMOS BALANCES REDUCIDOS"]                = (NosisReportHeadingKind.BalancesReducidos, null),
        ["RATIOS ECONOMICOFINANCIEROS"]               = (NosisReportHeadingKind.Skip, null),
        ["RATIOS ECONOMICO FINANCIEROS"]              = (NosisReportHeadingKind.Skip, null),
        ["RATIOS ECONOMICO-FINANCIEROS"]              = (NosisReportHeadingKind.Skip, null),
        ["RATIOS"]                                    = (NosisReportHeadingKind.Skip, null),
        ["EVOLUCION ANTECEDENTES"]                    = (NosisReportHeadingKind.Evolucion, null),
        ["EVOLUCION DE ANTECEDENTES"]                 = (NosisReportHeadingKind.Evolucion, null),
        ["CRITERIOS DE ACEPTACION"]                   = (NosisReportHeadingKind.CriteriosAceptacion, null),
        [BancariaGroup]                               = (NosisReportHeadingKind.Skip, BancariaGroup),
        ["BUREAU DE CREDITO"]                         = (NosisReportHeadingKind.BureauCredito, BancariaGroup),
        ["ULTIMO INFORME"]                            = (NosisReportHeadingKind.Skip, BancariaGroup),
        ["EVOLUCION DE ATRASOS"]                      = (NosisReportHeadingKind.Atrasos, BancariaGroup),
        ["LINEAS DE CREDITO"]                         = (NosisReportHeadingKind.Skip, BancariaGroup),
        ["COMPROMISOS MENSUALES"]                     = (NosisReportHeadingKind.Skip, BancariaGroup),
        ["CHEQUES RECHAZADOS DEL BCRA"]               = (NosisReportHeadingKind.ChequesRechazados, null),
        ["CHEQUES RECHAZADOS"]                        = (NosisReportHeadingKind.ChequesRechazados, null),
        ["CHEQUES POSITIVOS"]                         = (NosisReportHeadingKind.Skip, null),
        ["REFERENCIAS BANCARIAS"]                     = (NosisReportHeadingKind.Skip, null),
        ["REFERENCIAS COMERCIALES"]                   = (NosisReportHeadingKind.ReferenciasComerciales, null),
        ["OTROS ANTECEDENTES"]                        = (NosisReportHeadingKind.Srt, null),
        ["SRT"]                                       = (NosisReportHeadingKind.Srt, null),
        ["SISA"]                                      = (NosisReportHeadingKind.Sisa, null),
        ["COMUNICADOS DEL BCRA Y DE FUENTES DIRECTAS"] = (NosisReportHeadingKind.ComunicadosBcra, null),
        ["COMUNICADOS DEL BCRA"]                      = (NosisReportHeadingKind.ComunicadosBcra, null),
        ["MARCAS REGISTRADAS"]                        = (NosisReportHeadingKind.Marcas, null),
        ["MERCADO ARGENTINO DE VALORES"]              = (NosisReportHeadingKind.Mav, null),
        ["MAV"]                                       = (NosisReportHeadingKind.Mav, null),
        ["ANTECEDENTES FISCALES"]                     = (NosisReportHeadingKind.AntecedentesFiscales, null),
        ["COMERCIO EXTERIOR"]                         = (NosisReportHeadingKind.ComercioExterior, null),
        ["IMPORTACIONES"]                             = (NosisReportHeadingKind.Importaciones, null),
        ["EXPORTACIONES"]                             = (NosisReportHeadingKind.Exportaciones, null),
        ["SOCIEDADES"]                                = (NosisReportHeadingKind.Skip, null),
        ["HOMONIMOS"]                                 = (NosisReportHeadingKind.Skip, null),
        ["TRANSFERENCIAS FONDOS DE COMERCIO"]         = (NosisReportHeadingKind.Skip, null),
        ["TRANSFERENCIAS DE FONDOS DE COMERCIO"]      = (NosisReportHeadingKind.Skip, null),
        ["MIS SEGUIMIENTOS"]                          = (NosisReportHeadingKind.Skip, null),
        ["CONSULTAS"]                                 = (NosisReportHeadingKind.Skip, null),
        ["CONSULTAS Y SEGUIMIENTOS"]                  = (NosisReportHeadingKind.Skip, null),
        ["FICHA PROFESIONAL"]                         = (NosisReportHeadingKind.Skip, null),
        ["SCORES"]                                    = (NosisReportHeadingKind.Skip, null),
        ["RIESGO SECTORIAL"]                          = (NosisReportHeadingKind.Skip, null),
        ["VALIDACION DE IDENTIDAD"]                   = (NosisReportHeadingKind.Skip, null),
        ["INFORMACION POR RAZON SOCIAL"]              = (NosisReportHeadingKind.Skip, null),
        ["JUICIOS COMO DEMANDADO"]                    = (NosisReportHeadingKind.Juicios, null),
    };

    /// <summary>
    /// The data sections, in report order, plus the preamble and the headings acted on. A kind met
    /// twice (a "(continuación)" heading, a page break, a doubled heading) is one section: its lines
    /// are appended.
    /// </summary>
    public static NosisReportSplit Split(IReadOnlyList<NosisReportLine> lines)
    {
        var (calibrated, headingStyles, bodyStyle) = Calibrate(lines);

        var buckets = new Dictionary<NosisReportHeadingKind, (List<NosisReportLine> Lines, bool Thousands, string? Group)>();
        var groupThousands = new HashSet<string>(StringComparer.Ordinal);
        var preamble = new List<NosisReportLine>();
        var headings = new List<NosisReportHeading>();

        NosisReportHeadingKind? current = null;
        string? currentGroup = null;

        foreach (var line in lines)
        {
            if (IsHeading(line, calibrated, headingStyles, bodyStyle, out var kind, out var group, out var known))
            {
                headings.Add(new NosisReportHeading(kind, known, line));
                current = kind;
                currentGroup = group;
                if (kind != NosisReportHeadingKind.Skip && !buckets.ContainsKey(kind))
                    buckets[kind] = ([], false, group);
                continue;
            }

            if (current is null)
            {
                preamble.Add(line);   // the subject's header block: only the header-row reader looks here
                continue;
            }

            var thousands = line.Normalised.Contains("MILES DE $", StringComparison.Ordinal)
                            || line.Normalised.Contains("MILES DE PESOS", StringComparison.Ordinal);
            if (thousands && currentGroup is not null)
                groupThousands.Add(currentGroup);

            if (current == NosisReportHeadingKind.Skip)
                continue;   // a skipped section: its lines are dropped here, unread

            var bucket = buckets[current.Value];
            bucket.Lines.Add(line);
            if (thousands)
                buckets[current.Value] = (bucket.Lines, true, bucket.Group);
        }

        var sections = buckets.ToDictionary(
            b => b.Key,
            b => new NosisReportSection(
                b.Key,
                b.Value.Lines,
                b.Value.Thousands || (b.Value.Group is not null && groupThousands.Contains(b.Value.Group))));

        return new NosisReportSplit(sections, preamble, headings, calibrated);
    }

    /// <summary>The kind a heading TEXT names, by text alone (no style). For tests and the harness.</summary>
    internal static bool TryKnownText(NosisReportLine line, out NosisReportHeadingKind kind, out string? group)
    {
        kind = NosisReportHeadingKind.Skip;
        group = null;

        if (!IsCandidate(line))
            return false;

        var key = Key(line.Normalised);
        if (Headings.TryGetValue(key, out var direct))
        {
            (kind, group) = direct;
            return true;
        }

        // "GROUP / SUBHEADING": the most specific (last) known part decides.
        var parts = key.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            return false;

        for (var i = parts.Length - 1; i >= 0; i--)
        {
            if (Headings.TryGetValue(Key(parts[i]), out var found))
            {
                (kind, group) = found;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Learns the headings' look from the known ones. Calibrated when at least
    /// <see cref="MinKnownForStyle"/> known headings are found and at least one is printed unlike the
    /// body. <c>headingStyles</c>: the styles shared by two or more known headings, the body's excluded.
    /// </summary>
    private static (bool Calibrated, HashSet<NosisReportLineStyle> HeadingStyles, NosisReportLineStyle? Body) Calibrate(
        IReadOnlyList<NosisReportLine> lines)
    {
        var body = lines
            .Where(l => l.Style != NosisReportLineStyle.Unknown)
            .GroupBy(l => l.Style)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();

        var known = lines.Where(l => TryKnownText(l, out _, out _)).Select(l => l.Style).ToList();
        var headingStyles = known
            .Where(s => s != NosisReportLineStyle.Unknown && s != body)
            .GroupBy(s => s)
            .Where(g => g.Count() >= 2)
            .Select(g => g.Key)
            .ToHashSet();

        var calibrated = body is not null
                         && known.Count >= MinKnownForStyle
                         && known.Any(s => s != NosisReportLineStyle.Unknown && s != body);

        return (calibrated, headingStyles, body);
    }

    private static bool IsHeading(
        NosisReportLine line, bool calibrated, HashSet<NosisReportLineStyle> headingStyles, NosisReportLineStyle? body,
        out NosisReportHeadingKind kind, out string? group, out bool known)
    {
        known = TryKnownText(line, out kind, out group);

        if (!calibrated)
            return known;

        if (known)
            return line.Style != body;   // a known text printed like the body is a label, not a heading

        if (IsCandidate(line) && headingStyles.Contains(line.Style) && IsPhrase(line))
        {
            kind = NosisReportHeadingKind.Skip;   // a heading we do not know: close the section, drop its lines
            group = null;
            return true;
        }

        return false;
    }

    /// <summary>Capitals, no digit, at least one letter.</summary>
    private static bool IsCandidate(NosisReportLine line)
    {
        var hasLetter = false;
        foreach (var c in line.Text)
        {
            if (char.IsDigit(c))
                return false;
            if (char.IsLetter(c))
            {
                hasLetter = true;
                if (char.IsLower(c))
                    return false;
            }
        }

        return hasLetter;
    }

    /// <summary>Words that read as one phrase: no gap wider than a few font sizes between them.</summary>
    private static bool IsPhrase(NosisReportLine line)
    {
        var size = line.Style.Size > 0 ? line.Style.Size : 8d;
        for (var i = 1; i < line.Words.Count; i++)
        {
            if (line.Words[i].Left - line.Words[i - 1].Right > PhraseGapShare * size)
                return false;
        }

        return true;
    }

    /// <summary>Trailing "(…)" dropped; a doubled text ("X X") read once.</summary>
    private static string Key(string normalised)
    {
        var key = TrailingParenthesis.Replace(normalised, string.Empty).Trim();
        var words = key.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 2 && words.Length % 2 == 0)
        {
            var half = words.Length / 2;
            if (words.Take(half).SequenceEqual(words.Skip(half), StringComparer.Ordinal))
                return string.Join(' ', words.Take(half));
        }

        return key;
    }
}
