// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-09

using Api.Web.FinanceIA.Models.Financia;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using Xunit;
using Xunit.Abstractions;

namespace Api.Web.FinanceIA.Tests.Servicios.PdfReport;

/// <summary>
/// The cash-flow opening after the ratios table. The heading, the verdict and the "De dónde salió y a dónde fue" table
/// travel as one block; the bridge chart is its own block right after. When the whole opening does not fit under the
/// ratios table, the heading, verdict and table still start on the ratios page and only the chart moves on — the page
/// is no longer left half blank. An executive summary one line longer at a time walks the opening down the pages.
/// </summary>
public sealed class PdfCashFlowOpeningTests(ITestOutputHelper output)
{
    private const int MaxLines = 70;

    private const string Heading      = "Flujo de efectivo";
    private const string Verdict      = "concilia internamente";
    private const string TableHeading = "De dónde salió";
    private const string LastTableRow = "Efectivo al cierre";
    private const string RatiosHeading = "Ratios financieros";
    private const string LastRatioRow  = "Cobertura de intereses";

    private static async Task<string[]> RenderPagesAsync(int summaryLines)
    {
        AnalysisResult result = PdfSyntheticReport.Result();
        result.ExecutiveSummary = string.Join("\n", Enumerable.Range(1, summaryLines + 1).Select(i => $"Línea de resumen {i}."));

        byte[] pdf = await PdfExportServiceRenderTests.NewService().GenerateAsync(
            result, PdfSyntheticReport.Analysis(), "tenant-synthetic",
            ["executive_summary", "ratios", "ratio_breakdown", "cash_flow"],
            CancellationToken.None, PdfSyntheticReport.Report());

        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        return Enumerable.Range(1, doc.GetNumberOfPages()).Select(i => PdfTextExtractor.GetTextFromPage(doc.GetPage(i))).ToArray();
    }

    // The page (0-based) and index of the first occurrence of `text` at or after (page, index); null when absent.
    private static (int Page, int Index)? Find(string[] pages, string text, int fromPage, int fromIndex)
    {
        for (int page = fromPage; page < pages.Length; page++)
        {
            int at = pages[page].IndexOf(text, page == fromPage ? fromIndex : 0, StringComparison.Ordinal);
            if (at >= 0)
                return (page, at);
        }

        return null;
    }

    private static (int Page, int Index) Require(string[] pages, string text, (int Page, int Index) after, int lines)
    {
        (int Page, int Index)? at = Find(pages, text, after.Page, after.Index);
        Assert.True(at is not null, $"lines={lines}: '{text}' is missing.");
        return at.Value;
    }

    [Fact]
    public async Task TheOpening_StartsUnderTheRatios_AndOnlyTheChartMovesOn()
    {
        int chartMovedAlone = 0;
        for (int lines = 0; lines <= MaxLines; lines++)
        {
            string[] pages = await RenderPagesAsync(lines);

            // Page 1 is the fact sheet: everything is looked for from page 2 on. The ratio row is the table's (the
            // reliability box before it can name the same ratio).
            (int Page, int Index) ratios   = Require(pages, RatiosHeading, (1, 0), lines);
            (int Page, int Index) ratioRow = Require(pages, LastRatioRow, Require(pages, "Indicador", ratios, lines), lines);
            (int Page, int Index) heading  = Require(pages, Heading, ratioRow, lines);
            (int Page, int Index) verdict  = Require(pages, Verdict, heading, lines);
            (int Page, int Index) table    = Require(pages, TableHeading, verdict, lines);
            (int Page, int Index) lastRow  = Require(pages, LastTableRow, table, lines);

            // Heading, verdict and bridge table: one block, never split.
            Assert.True(heading.Page == verdict.Page && verdict.Page == table.Page && table.Page == lastRow.Page,
                $"lines={lines}: heading p{heading.Page + 1}, verdict p{verdict.Page + 1}, table p{table.Page + 1}..p{lastRow.Page + 1}.");

            // The chart (its column labels, after the table's last row): one block, on that page or the next.
            (int Page, int Index) chartFirst = Require(pages, "Inicio", lastRow, lines);
            (int Page, int Index) chartLast  = Require(pages, "Cierre", chartFirst, lines);
            Assert.True(chartFirst.Page == chartLast.Page, $"lines={lines}: the chart split over pages {chartFirst.Page + 1}-{chartLast.Page + 1}.");
            Assert.InRange(chartFirst.Page, heading.Page, heading.Page + 1);

            output.WriteLine($"lines={lines}: ratios p{ratioRow.Page + 1}, heading p{heading.Page + 1}, chart p{chartFirst.Page + 1}, pages {pages.Length}");

            // The case that used to jump: the opening shares the ratios page and the chart follows on the next.
            if (ratioRow.Page == heading.Page && chartFirst.Page == heading.Page + 1)
                chartMovedAlone++;
        }

        output.WriteLine($"{chartMovedAlone} lengths start the opening under the ratios with the chart on the next page.");
        Assert.True(chartMovedAlone > 0, "The sweep never produced the case where only the chart moves on.");
    }
}
