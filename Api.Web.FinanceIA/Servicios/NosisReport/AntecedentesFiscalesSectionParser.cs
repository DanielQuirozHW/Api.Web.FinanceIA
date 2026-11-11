// Project : Api.Web.FinanceIA
// Created : 2026-11-10

using System.Text.RegularExpressions;
using Api.Web.FinanceIA.Models.Nosis;

namespace Api.Web.FinanceIA.Servicios.NosisReport;

/// <summary>
/// <c>antecedentesFiscales</c>: ANTECEDENTES FISCALES — per regime its organism (AGIP, ARBA, …), type,
/// rate as a NUMBER ("5,00 %" → 5.00) and validity dates. The real template prints one block per
/// organism with label lines (one entry per rate); a table or a single row per regime are read too. A CUIT or name column is located only so its
/// words are dropped. Without a header, a row is an organism word in capitals followed by its dates.
/// </summary>
internal static class AntecedentesFiscalesSectionParser
{
    private const string Org  = "org";
    private const string Tipo = "tipo";
    private const string Ali  = "ali";
    private const string Desde = "desde";
    private const string Hasta = "hasta";
    private const string Drop = "drop";

    private static readonly Dictionary<string, string> HeaderPhrases = new(StringComparer.Ordinal)
    {
        ["Organismo"] = Org, ["Fuente"] = Org, ["Jurisdicción"] = Org, ["Ente"] = Org,
        ["Tipo"] = Tipo, ["Régimen"] = Tipo, ["Regimen"] = Tipo, ["Padrón"] = Tipo, ["Padron"] = Tipo, ["Concepto"] = Tipo, ["Impuesto"] = Tipo,
        ["Alícuota"] = Ali, ["Alicuota"] = Ali, ["Tasa"] = Ali,
        ["Desde"] = Desde, ["Vigencia desde"] = Desde, ["Fecha desde"] = Desde, ["Vigente desde"] = Desde,
        ["Hasta"] = Hasta, ["Vigencia hasta"] = Hasta, ["Fecha hasta"] = Hasta, ["Vigente hasta"] = Hasta,
        ["CUIT"] = Drop, ["Contribuyente"] = Drop, ["Razón social"] = Drop, ["Denominación"] = Drop,
    };

    private static readonly Regex RatePattern = new(
        @"ALICUOTA\s+(?<kind>[A-Z]+(?:\s+[A-Z]+)?)\s*:\s*(?<rate>[\d.,]+)\s*%", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

    private static readonly Regex FromPattern = new(
        @"VIGENCIA\s+DESDE\s*:\s*(?<date>\d{1,2}[/\-.]\d{1,2}[/\-.]\d{4})", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

    private static readonly Regex ToPattern = new(
        @"VIGENCIA\s+HASTA\s*:\s*(?<date>\d{1,2}[/\-.]\d{1,2}[/\-.]\d{4})", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

    public static IReadOnlyList<NosisReportAntecedenteFiscal> Parse(NosisReportSection section)
    {
        var lines = section.Lines;
        if (lines.Count == 0)
            return [];

        // The real template: one block per organism — "AGIP - <description>", then its rates and
        // validity on label lines.
        if (Blocks(section) is { Count: > 0 } blocks)
            return blocks;

        var items = new List<NosisReportAntecedenteFiscal>();
        var named = NosisReportNamedColumns.Find(lines, HeaderPhrases, 3);

        if (named is not null && named.Columns.Any(c => c.Key == Org))
        {
            for (var i = named.HeaderIndex + 1; i < lines.Count; i++)
            {
                var line = lines[i];
                if (NosisReportTableLines.IsNote(line) || named.IsRepeatedHeader(line, HeaderPhrases))
                    continue;

                var cells = named.Read(line);
                var organismo = cells.GetValueOrDefault(Org);
                if (string.IsNullOrWhiteSpace(organismo))
                {
                    // A wrapped type continues the row above.
                    if (items.Count > 0 && cells.GetValueOrDefault(Tipo) is { Length: > 0 } more)
                        items[^1] = items[^1] with { Tipo = $"{items[^1].Tipo} {more}".Trim() };
                    continue;
                }

                items.Add(new NosisReportAntecedenteFiscal
                {
                    Organismo     = organismo.Trim(),
                    Tipo          = cells.GetValueOrDefault(Tipo)?.Trim(),
                    Alicuota      = NosisReportNumbers.FirstAmount(cells.GetValueOrDefault(Ali), section.NumberStyle),
                    VigenciaDesde = NosisReportText.FirstDate(cells.GetValueOrDefault(Desde)),
                    VigenciaHasta = NosisReportText.FirstDate(cells.GetValueOrDefault(Hasta)),
                });
            }
        }
        else
        {
            foreach (var line in lines)
            {
                if (NosisReportTableLines.IsNote(line) || line.Words.Count < 2)
                    continue;

                var first = line.Words[0].Text;
                var dates = NosisReportText.Dates(line.Text);
                if (dates.Count == 0 || first.Length < 2 || !first.All(c => char.IsLetter(c) && char.IsUpper(c)))
                    continue;

                // The type: the words between the organism and the first figure, date or identifier.
                var tipo = line.Words.Skip(1)
                    .TakeWhile(w => !w.Text.Any(char.IsDigit) && w.Text != "%")
                    .Select(w => w.Text);
                var rate = line.Words.Select((w, k) => (w, k))
                    .FirstOrDefault(x => x.w.Text.EndsWith('%') || (x.k + 1 < line.Words.Count && line.Words[x.k + 1].Text == "%"));

                items.Add(new NosisReportAntecedenteFiscal
                {
                    Organismo     = first,
                    Tipo          = string.Join(' ', tipo) is { Length: > 0 } t ? t : null,
                    Alicuota      = rate.w is null ? null : NosisReportNumbers.FirstAmount(rate.w.Text, section.NumberStyle),
                    VigenciaDesde = dates[0],
                    VigenciaHasta = dates.Count > 1 ? dates[1] : null,
                });
            }
        }

        if (items.Count == 0 && !NosisReportText.SaysNone(lines))
            throw new NosisReportFormatException("No fiscal regime row.");

        return items;
    }

    /// <summary>
    /// "AGIP - Regímenes … (C.A.B.A.)" / "Alícuota percepción: 5,00 %  Alícuota retención: 2,50 %" /
    /// "Vigencia desde: dd.mm.yyyy  Vigencia hasta: dd.mm.yyyy": ONE entry per rate (tipo = the rate's
    /// kind, "Percepción" / "Retención"), each with the block's dates; a block without a rate gives one
    /// entry whose tipo is the description. Empty when the section has no such block.
    /// </summary>
    private static List<NosisReportAntecedenteFiscal> Blocks(NosisReportSection section)
    {
        var items = new List<NosisReportAntecedenteFiscal>();
        string? organismo = null, description = null;
        var rates = new List<(string Kind, decimal? Rate)>();
        DateOnly? desde = null, hasta = null;

        void Flush()
        {
            if (organismo is null)
                return;

            if (rates.Count == 0)
            {
                items.Add(new NosisReportAntecedenteFiscal { Organismo = organismo, Tipo = description, VigenciaDesde = desde, VigenciaHasta = hasta });
            }
            else
            {
                foreach (var (kind, rate) in rates)
                {
                    items.Add(new NosisReportAntecedenteFiscal
                    {
                        Organismo = organismo, Tipo = kind, Alicuota = rate, VigenciaDesde = desde, VigenciaHasta = hasta,
                    });
                }
            }

            organismo = description = null;
            rates.Clear();
            desde = hasta = null;
        }

        foreach (var line in section.Lines)
        {
            var words = line.Words;
            if (words.Count >= 3 && words[1].Text is "-" or "–" && IsOrganism(words[0].Text))
            {
                Flush();
                organismo = words[0].Text;
                description = string.Join(' ', words.Skip(2).Select(w => w.Text));
                continue;
            }

            if (organismo is null)
                continue;

            foreach (Match m in RatePattern.Matches(line.Normalised))
            {
                var kind = m.Groups["kind"].Value.Trim();
                rates.Add((char.ToUpperInvariant(kind[0]) + RestoreAccents(kind[1..].ToLowerInvariant()),
                           NosisReportNumbers.FirstAmount(m.Groups["rate"].Value, section.NumberStyle)));
            }

            if (FromPattern.Match(line.Normalised) is { Success: true } from)
                desde ??= NosisReportText.FirstDate(from.Groups["date"].Value);
            if (ToPattern.Match(line.Normalised) is { Success: true } to)
                hasta ??= NosisReportText.FirstDate(to.Groups["date"].Value);
        }

        Flush();
        return items;
    }

    /// <summary>"AGIP", "ARBA", "API", "DGR", "ATER": an organism's acronym in capitals.</summary>
    private static bool IsOrganism(string word)
        => word.Length >= 2 && word.All(c => char.IsUpper(c) || c == '.');

    /// <summary>The normalised kind back in Spanish spelling ("PERCEPCION" → "percepción").</summary>
    private static string RestoreAccents(string kind)
        => kind.EndsWith("cion", StringComparison.Ordinal) ? kind[..^4] + "ción" : kind;
}
