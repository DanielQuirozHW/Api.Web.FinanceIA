// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-09

using System.Text.Json;
using System.Text.RegularExpressions;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Lufe;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using Xunit;
using Xunit.Abstractions;

namespace Api.Web.FinanceIA.Tests.Servicios.PdfReport;

/// <summary>
/// The LUFE section across a page break. An executive summary one line longer at a time walks the section down the
/// page and over the break (the sweep of <see cref="PdfOrphanHeadingTests"/>); at every step the summary box stays in
/// one piece with its heading, and neither LUFE table leaves fewer than two body rows on either side of a break
/// (kept whole up to six rows).
/// </summary>
public sealed class PdfLufePaginationTests(ITestOutputHelper output)
{
    private const int MaxLines = 70;

    internal static async Task<string[]> RenderPagesAsync(int summaryLines, Action<AnalysisResult>? tweak = null)
    {
        AnalysisResult result = PdfSyntheticReport.Result();
        result.ExecutiveSummary = string.Join("\n", Enumerable.Range(1, summaryLines + 1).Select(i => $"Línea de resumen {i}."));
        tweak?.Invoke(result);

        byte[] pdf = await PdfExportServiceRenderTests.NewService().GenerateAsync(
            result, PdfSyntheticReport.Analysis(), "tenant-synthetic", ["executive_summary", "lufe"],
            CancellationToken.None, PdfSyntheticReport.Report());

        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        return Enumerable.Range(1, doc.GetNumberOfPages()).Select(i => PdfTextExtractor.GetTextFromPage(doc.GetPage(i))).ToArray();
    }

    // The 0-based page of the first occurrence of `text` from page 2 on (page 1 is the fact sheet); -1 when absent.
    internal static int PageOf(string[] pages, string text)
    {
        for (int page = 1; page < pages.Length; page++)
        {
            if (pages[page].Contains(text, StringComparison.Ordinal))
                return page;
        }

        return -1;
    }

    [Fact]
    public async Task TheSummaryBox_NeverSplits_AndKeepsItsHeading()
    {
        var headingPages = new HashSet<int>();
        for (int lines = 0; lines <= MaxLines; lines++)
        {
            string[] pages = await RenderPagesAsync(lines);

            int heading = PageOf(pages, "Ventas declaradas (LUFE)");
            int first   = PageOf(pages, "usada por el score");
            int context = PageOf(pages, "Contexto (no usado por el score)");
            int last    = PageOf(pages, "sin declaración.");

            Assert.True(heading > 0, $"lines={lines}: the LUFE heading is missing.");
            Assert.True(heading == first && first == context && context == last,
                $"lines={lines}: heading p{heading + 1}, first line p{first + 1}, context p{context + 1}, end p{last + 1}.");

            headingPages.Add(heading);
        }

        // The sweep must actually carry the section over a break, or it proved nothing.
        output.WriteLine($"LUFE heading pages seen: {string.Join(", ", headingPages.Order().Select(p => p + 1))}");
        Assert.True(headingPages.Count > 1, "The sweep never moved the LUFE section onto another page.");
    }

    // A row line as the text extractor reads it: the period, then the first figure (or the muted no-declaration).
    private static readonly Regex MonthlyRow = new(@"^\d{2}/\d{4} (\$|Sin declaración)", RegexOptions.Multiline);
    private static readonly Regex AnnualRow  = new(@"^\d{4} (\$|—)", RegexOptions.Multiline);

    // `count` annual balances (YYYY00, printed as the bare year), most recent 2025.
    internal static void AnnualRows(AnalysisResult result, int count)
    {
        LufeData lufe = JsonSerializer.Deserialize<LufeData>(result.LufeDataJson!)!;
        lufe.IndicadoresHistoricos = Enumerable.Range(0, count)
            .Select(i => new LufeIndicador
            {
                Periodo           = (2025 - i) * 100m,
                VentasPb          = 900_000_000m + (i * 10_000_000m),
                ComprasPb         = 0m,
                VtasMensualesProm = 75_000_000m,
            })
            .ToArray();
        result.LufeDataJson = JsonSerializer.Serialize(lufe);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(12)]
    public async Task NeitherLufeTable_LeavesFewerThanTwoRows_OnEitherSideOfABreak(int annualRows)
    {
        int monthlySplits = 0, annualSplits = 0;
        for (int lines = 0; lines <= MaxLines; lines++)
        {
            string[] pages = await RenderPagesAsync(lines, r => AnnualRows(r, annualRows));

            int[] monthly = pages.Select(p => MonthlyRow.Matches(p).Count).ToArray();
            int[] annual  = pages.Select(p => AnnualRow.Matches(p).Count).ToArray();

            Assert.True(monthly.Sum() == 12, $"lines={lines}: {monthly.Sum()} monthly rows, not 12.");
            Assert.True(annual.Sum() == annualRows, $"lines={lines}: {annual.Sum()} annual rows, not {annualRows}.");

            for (int page = 0; page < pages.Length; page++)
            {
                Assert.True(monthly[page] is 0 or >= 2, $"lines={lines}: page {page + 1} holds {monthly[page]} monthly row(s).");
                Assert.True(annual[page] is 0 or >= 2 || annualRows < 2, $"lines={lines}: page {page + 1} holds {annual[page]} annual row(s).");

                // A page carrying rows repeats the header: one "Período" per table with rows on it.
                int tables  = (monthly[page] > 0 ? 1 : 0) + (annual[page] > 0 ? 1 : 0);
                int headers = Regex.Matches(pages[page], "Período").Count;
                Assert.True(headers >= tables, $"lines={lines}: page {page + 1} has {tables} table part(s) but {headers} header(s).");
            }

            if (annualRows <= 6)
                Assert.True(annual.Count(n => n > 0) == 1, $"lines={lines}: the {annualRows}-row annual table split.");

            if (monthly.Count(n => n > 0) > 1) monthlySplits++;
            if (annual.Count(n => n > 0) > 1) annualSplits++;
        }

        // The sweep must actually split the long tables somewhere, or it proved nothing about the rule.
        output.WriteLine($"annual={annualRows}: monthly split {monthlySplits}×, annual split {annualSplits}×.");
        Assert.True(monthlySplits > 0, "The sweep never split the monthly table.");
        if (annualRows > 6)
            Assert.True(annualSplits > 0, "The sweep never split the annual table.");
    }
}
