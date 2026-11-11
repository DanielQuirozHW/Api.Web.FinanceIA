// Project : Api.Web.FinanceIA
// Created : 2026-11-10

namespace Api.Web.FinanceIA.Servicios.NosisReport;

/// <summary>
/// One visual line of a report page: its words left to right. <see cref="Text"/> joins them with one
/// space; <see cref="Normalised"/> is the same text upper-cased, without accents and with whitespace
/// collapsed — what headings and labels are matched on.
/// </summary>
internal sealed class NosisReportLine
{
    public NosisReportLine(int page, double baseline, IReadOnlyList<NosisReportWord> words, NosisReportLineStyle? style = null)
    {
        Style      = style ?? NosisReportLineStyle.Unknown;
        Page       = page;
        Baseline   = baseline;
        Words      = words;
        Text       = string.Join(' ', words.Select(w => w.Text));
        Normalised = NosisReportText.Normalise(Text);
    }

    /// <summary>1-based page number.</summary>
    public int Page { get; }

    /// <summary>Baseline in PDF user space (larger = higher on the page).</summary>
    public double Baseline { get; }

    public IReadOnlyList<NosisReportWord> Words { get; }

    /// <summary>How the line is printed (size, weight, colour).</summary>
    public NosisReportLineStyle Style { get; }

    public string Text { get; }

    public string Normalised { get; }
}
