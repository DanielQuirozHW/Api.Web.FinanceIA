// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-25

using System.Collections.Concurrent;
using System.Text;
using Api.Web.FinanceIA.Servicios;
using iText.IO.Font;
using Xunit;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Api.Web.FinanceIA.Tests.Servicios;

// ── Test class ────────────────────────────────────────────────────────────────

/// <summary>
/// Guards the brand-font concurrency fix in <see cref="PdfExportService"/>. The service used to load each
/// brand TTF through iText's cached path overload, so every export in the process shared ONE file-backed
/// TrueTypeFont per face; subsetting at Document.Close() read glyph data back through it, and two exports
/// closing at once corrupted each other (IndexOutOfRange / ArgumentOutOfRange inside TrueTypeFontSubset).
/// This fires many full exports at once and requires every one to succeed, parse, and still embed the
/// three brand families — so a fix that "passes" by silently falling back to Helvetica does not.
/// </summary>
public sealed class PdfExportConcurrencyTests
{
    private const int ConcurrentExports = 24;

    // The shared-program race only bites while a freshly cached face is subset for the first time:
    // RandomAccessFileOrArray.EnsureByteSourceIsThreadSafe is an unsynchronised check-then-set, so the
    // first concurrent views can each get their own lock over one FileStream. Once it settles the face is
    // safe for the life of the process — which is why every round clears iText's static FontCache first:
    // each round is then a cold start, and the guard does not depend on which test warmed the cache.
    private const int Rounds = 5;

    private static readonly byte[] PdfMagic = Encoding.ASCII.GetBytes("%PDF");

    // Substrings of the PostScript names the TTFs carry (SoraRegular / SoraBold, DMSansRegular /
    // DMSansMedium, JetBrainsMono-Regular); PdfPig reports them behind the 6-letter subset prefix.
    private static readonly string[] BrandFamilies = ["Sora", "DMSans", "JetBrainsMono"];

    [Fact]
    public async Task GenerateAsync_ManyConcurrentExports_AllSucceedAndEmbedBrandFonts()
    {
        var pdfs = new ConcurrentBag<byte[]>();

        // One service per export, as in production (the service is resolved per request). Empty sections
        // → every section, so all five faces are exercised and subset at Close.
        for (int round = 0; round < Rounds; round++)
        {
            FontCache.ClearSavedFonts();

            await Parallel.ForEachAsync(
                Enumerable.Range(0, ConcurrentExports),
                new ParallelOptions { MaxDegreeOfParallelism = ConcurrentExports },
                async (_, ct) =>
                {
                    byte[] bytes = await PdfExportServiceRenderTests.NewService().GenerateAsync(
                        PdfExportServiceRenderTests.SampleResult(225000000),
                        PdfExportServiceRenderTests.SampleAnalysis(),
                        "tenant-x",
                        Array.Empty<string>(),
                        ct);
                    pdfs.Add(bytes);
                });
        }

        Assert.Equal(ConcurrentExports * Rounds, pdfs.Count);

        foreach (byte[] bytes in pdfs)
        {
            Assert.True(bytes.Length > 1000, $"PDF suspiciously small: {bytes.Length} bytes");
            Assert.Equal(PdfMagic, bytes.AsSpan(0, 4).ToArray());

            HashSet<string> fontNames = FontNamesUsed(bytes);
            foreach (string family in BrandFamilies)
            {
                Assert.True(
                    fontNames.Any(n => n.Contains(family, StringComparison.Ordinal)),
                    $"Brand face '{family}' not embedded; fonts used: {string.Join(", ", fontNames)}");
            }
        }
    }

    private static HashSet<string> FontNamesUsed(byte[] pdf)
    {
        using PigDocument doc = PigDocument.Open(pdf);

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (UglyToad.PdfPig.Content.Page page in doc.GetPages())
        {
            foreach (UglyToad.PdfPig.Content.Letter letter in page.Letters)
            {
                if (!string.IsNullOrWhiteSpace(letter.FontName))
                    names.Add(letter.FontName);
            }
        }

        return names;
    }
}
