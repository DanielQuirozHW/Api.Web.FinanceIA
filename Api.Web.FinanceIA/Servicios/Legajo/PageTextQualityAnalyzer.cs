// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using System.Text;
using UglyToad.PdfPig;

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// Measures, page by page, whether a PDF carries a usable text layer. Local and deterministic: it
/// runs PdfPig over the bytes and makes NO model call. The verdicts decide how the classification
/// reads a document (one document block when every page is Good, rendered images otherwise) and
/// derive Documents.PdfType.
///
/// <para>
/// <see cref="AnalyzeText"/> is pure and carries all the judgement, so the thresholds are unit-tested
/// on text samples; <see cref="Analyze"/> only extracts each page's words and delegates.
/// </para>
/// </summary>
public static class PageTextQualityAnalyzer
{
    /// <summary>Below this many non-whitespace characters a page is <see cref="PageTextVerdicts.NoText"/>.</summary>
    public const int MinCharsForText = 20;

    /// <summary>Above this share of suspicious characters a page is <see cref="PageTextVerdicts.Suspect"/>.</summary>
    public const decimal MaxSuspiciousRatio = 0.05m;

    /// <summary>
    /// Below this share of letters-or-digits among the characters a page is Suspect: the text layer is
    /// mostly symbols, the signature of a font with a broken ToUnicode map.
    /// </summary>
    public const decimal MinAlphanumericRatio = 0.50m;

    /// <summary>
    /// Above this mean token length a page is Suspect: words glued together with no spaces between
    /// them. Generous, because amounts such as "1.234.567.890,12" are long tokens.
    /// </summary>
    public const decimal MaxAverageWordLength = 20m;

    /// <summary>
    /// Below this mean token length a page is Suspect: letter-spaced text ("G a n a n c i a s").
    /// </summary>
    public const decimal MinAverageWordLength = 1.8m;

    private const string CidMarker = "(cid:";

    /// <summary>
    /// Measures every page of <paramref name="pdfBytes"/>. Never throws: a PDF PdfPig cannot open
    /// yields <see cref="PageTextQualityReport.Unreadable"/>, and a page whose text cannot be read is
    /// <see cref="PageTextVerdicts.Unknown"/>. <paramref name="onUnreadable"/> is told which case
    /// happened (exception type only) so the caller can log it — never with the document's content.
    /// </summary>
    public static PageTextQualityReport Analyze(byte[] pdfBytes, Action<Exception, int?>? onUnreadable = null)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);

        PdfDocument document;
        try
        {
            document = PdfDocument.Open(pdfBytes);
        }
        catch (Exception ex)
        {
            onUnreadable?.Invoke(ex, null);
            return PageTextQualityReport.Unreadable;
        }

        using (document)
        {
            int pageCount;
            try
            {
                pageCount = document.NumberOfPages;
            }
            catch (Exception ex)
            {
                onUnreadable?.Invoke(ex, null);
                return PageTextQualityReport.Unreadable;
            }

            var pages = new List<PageTextQuality>(pageCount);
            for (var number = 1; number <= pageCount; number++)
            {
                try
                {
                    var page = document.GetPage(number);

                    // Words, not page.Text: PdfPig's Text concatenates glyphs in stream order and only
                    // contains a space where the PDF drew one, so a page whose words are positioned
                    // apart would read as one glued token.
                    var text = string.Join(' ', page.GetWords().Select(w => w.Text));
                    pages.Add(AnalyzeText(number, text));
                }
                catch (Exception ex)
                {
                    onUnreadable?.Invoke(ex, number);
                    pages.Add(new PageTextQuality(number, 0, 0, 0, 0m, 0m, PageTextVerdicts.Unknown));
                }
            }

            return new PageTextQualityReport(true, pages);
        }
    }

    /// <summary>Measures one page's extracted text and returns its verdict. Pure.</summary>
    public static PageTextQuality AnalyzeText(int page, string? text)
    {
        text ??= string.Empty;

        var chars      = 0;
        var letters    = 0;
        var digits     = 0;
        var suspicious = 0;

        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
                continue;

            chars++;

            if (Rune.IsLetter(rune))
                letters++;
            else if (Rune.IsDigit(rune))
                digits++;

            if (IsSuspicious(rune))
                suspicious++;
        }

        // "(cid:NN)" is what an extractor emits for a glyph with no Unicode mapping. Every character
        // of the marker counts: the digits inside it would otherwise look like real figures.
        suspicious += CountCidCharacters(text);
        if (suspicious > chars)
            suspicious = chars;

        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var averageWordLength = tokens.Length == 0
            ? 0m
            : Math.Round((decimal)tokens.Sum(t => t.Length) / tokens.Length, 2);

        var suspiciousRatio = chars == 0 ? 0m : Math.Round((decimal)suspicious / chars, 4);

        return new PageTextQuality(
            page, chars, letters, digits, suspiciousRatio, averageWordLength,
            Judge(chars, letters, digits, suspiciousRatio, averageWordLength));
    }

    private static string Judge(int chars, int letters, int digits, decimal suspiciousRatio, decimal averageWordLength)
    {
        if (chars < MinCharsForText)
            return PageTextVerdicts.NoText;

        var alphanumericRatio = (decimal)(letters + digits) / chars;

        if (suspiciousRatio > MaxSuspiciousRatio
            || alphanumericRatio < MinAlphanumericRatio
            || averageWordLength > MaxAverageWordLength
            || averageWordLength < MinAverageWordLength)
        {
            return PageTextVerdicts.Suspect;
        }

        return PageTextVerdicts.Good;
    }

    private static bool IsSuspicious(Rune rune)
    {
        if (rune.Value == 0xFFFD)
            return true;

        if (Rune.IsControl(rune))
            return true;

        // Private-use areas: the BMP block and supplementary planes 15 and 16.
        return rune.Value is (>= 0xE000 and <= 0xF8FF)
                          or (>= 0xF0000 and <= 0xFFFFD)
                          or (>= 0x100000 and <= 0x10FFFD);
    }

    private static int CountCidCharacters(string text)
    {
        var count = 0;
        var index = text.IndexOf(CidMarker, StringComparison.Ordinal);
        while (index >= 0)
        {
            var close = text.IndexOf(')', index);
            var end   = close < 0 ? index + CidMarker.Length : close + 1;
            count += end - index;
            index = text.IndexOf(CidMarker, end, StringComparison.Ordinal);
        }

        return count;
    }
}
