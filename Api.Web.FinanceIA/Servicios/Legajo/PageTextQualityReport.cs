// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using System.Text.Json;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// The result of measuring every page of one PDF with <see cref="PageTextQualityAnalyzer"/>.
/// </summary>
/// <param name="IsReadable">False when the PDF could not be opened at all.</param>
/// <param name="Pages">One entry per page, in page order. Empty when unreadable.</param>
public sealed record PageTextQualityReport(bool IsReadable, IReadOnlyList<PageTextQuality> Pages)
{
    /// <summary>A report for a PDF that could not be opened: no page count, no verdicts.</summary>
    public static PageTextQualityReport Unreadable { get; } = new(false, []);

    /// <summary>The page count, or null when the PDF could not be opened.</summary>
    public int? PageCount => IsReadable ? Pages.Count : null;

    /// <summary>Pages judged <see cref="PageTextVerdicts.Good"/>.</summary>
    public int GoodPages => Count(PageTextVerdicts.Good);

    /// <summary>Pages judged <see cref="PageTextVerdicts.Suspect"/>.</summary>
    public int SuspectPages => Count(PageTextVerdicts.Suspect);

    /// <summary>Pages judged <see cref="PageTextVerdicts.NoText"/>.</summary>
    public int NoTextPages => Count(PageTextVerdicts.NoText);

    /// <summary>True when the PDF opened, has at least one page, and every page is Good.</summary>
    public bool AllPagesGood => IsReadable && Pages.Count > 0 && GoodPages == Pages.Count;

    /// <summary>
    /// The <see cref="PdfTypes"/> value the pages imply: Native when every page has text (Good or
    /// Suspect), Scanned when none has, Mixed when both occur; null when unreadable, empty, or any
    /// page is Unknown.
    /// </summary>
    public string? PdfType
    {
        get
        {
            if (!IsReadable || Pages.Count == 0 || Pages.Any(p => p.Verdict == PageTextVerdicts.Unknown))
                return null;

            var withText = GoodPages + SuspectPages;
            if (withText == Pages.Count) return PdfTypes.Native;
            if (withText == 0)           return PdfTypes.Scanned;
            return PdfTypes.Mixed;
        }
    }

    /// <summary>
    /// The value stored in Documents.PageQualityJson: <c>[{page, chars, digits, suspiciousRatio, verdict}]</c>,
    /// or null when the PDF could not be opened.
    /// </summary>
    public string? ToJson() => IsReadable ? JsonSerializer.Serialize(Pages) : null;

    private int Count(string verdict) => Pages.Count(p => p.Verdict == verdict);
}
