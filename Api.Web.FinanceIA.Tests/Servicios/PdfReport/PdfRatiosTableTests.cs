// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-09

using System.Text;
using Api.Web.FinanceIA.Models.Financia;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios.PdfReport;

/// <summary>
/// QA Q53 + Q56: "ratios" and "ratio_breakdown" print ONE table under "Ratios financieros" — each key its own columns —
/// and the risk section's anatomy line stays on the page of the component table it sums.
/// </summary>
public sealed class PdfRatiosTableTests
{
    private static async Task<string[]> RenderPagesAsync(AnalysisResult result, params string[] sections)
    {
        byte[] pdf = await PdfExportServiceRenderTests.NewService().GenerateAsync(
            result, PdfSyntheticReport.Analysis(), "tenant-synthetic", sections, CancellationToken.None, PdfSyntheticReport.Report());

        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        return Enumerable.Range(1, doc.GetNumberOfPages())
            .Select(i => PdfTextExtractor.GetTextFromPage(doc.GetPage(i)))
            .ToArray();
    }

    private static int Count(string text, string value)
    {
        int count = 0;
        for (int at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    [Theory]
    [InlineData(new[] { "ratios", "ratio_breakdown" }, "Indicador Valor Puntaje Estado")]
    [InlineData(new[] { "ratios" }, "Indicador Valor Estado")]
    [InlineData(new[] { "ratio_breakdown" }, "Indicador Puntaje Estado")]
    public async Task EachKeyPrintsItsColumns_InOneTable_UnderOneHeading(string[] sections, string header)
    {
        // From page 2 on: page 1's score composition has its own "Ratios financieros" row.
        string text = string.Join("\n", (await RenderPagesAsync(PdfSyntheticReport.Result(), sections)).Skip(1));

        Assert.Equal(1, Count(text, "Ratios financieros"));
        Assert.Contains(header, text, StringComparison.Ordinal);
        Assert.DoesNotContain("Desglose", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Puntaje Parcial", text, StringComparison.Ordinal);

        // The same eight indicators, in the canonical order, once each.
        string[] order = ["Liquidez corriente", "Prueba ácida", "Endeudamiento", "Apalancamiento", "ROE", "ROA", "Margen neto", "Cobertura de intereses"];
        int table = text.IndexOf(header, StringComparison.Ordinal);
        int previous = table;
        foreach (string indicator in order)
        {
            int at = text.IndexOf(indicator, previous, StringComparison.Ordinal);
            Assert.True(at > previous, $"'{indicator}' is missing or out of order.");
            previous = at;
        }
    }

    [Fact]
    public async Task TheAnatomyLine_StaysOnThePageOfItsComponentTable_WhereverTheTableFalls()
    {
        // A summary of growing length walks the risk section's component table down page 2 and over the break.
        for (int paragraphs = 0; paragraphs <= 60; paragraphs += 2)
        {
            AnalysisResult result = PdfSyntheticReport.Result();
            result.ExecutiveSummary = string.Join("\n\n", Enumerable.Repeat(
                "La empresa presenta indicadores de liquidez y solvencia acordes con su sector y un flujo operativo positivo.",
                paragraphs + 1));

            string[] pages = await RenderPagesAsync(result, "executive_summary", "risk");

            int tablePage   = Array.FindIndex(pages, 1, p => p.Contains("Componente", StringComparison.Ordinal));
            int anatomyPage = Array.FindIndex(pages, 1, p => p.Contains("→ Score compuesto", StringComparison.Ordinal));
            Assert.True(tablePage >= 1 && anatomyPage >= 1, $"paragraphs={paragraphs}: table or anatomy line missing.");
            Assert.True(tablePage == anatomyPage, $"paragraphs={paragraphs}: table on page {tablePage + 1}, anatomy line on page {anatomyPage + 1}.");
        }
    }
}
