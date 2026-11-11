// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-09

using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.Pdf;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios.PdfReport;

/// <summary>
/// K20: after a manual balance correction the re-score rewrites the justification, never the executive summary, so
/// the report says — as the Portal does under the summary — that the summary was written from the original reading.
/// The condition is the detail's own: <c>ManualCorrectionsReader.Read(BalanceJson)</c> is not null.
/// </summary>
public sealed class PdfExecutiveSummaryCorrectionNoteTests
{
    private const string Note = "Este resumen se generó con la lectura original del balance.";

    // A synthetic correction entry, in the trail shape BalanceCorrectionApplier writes.
    private static string Corrected(string balanceJson)
    {
        JsonObject root = JsonNode.Parse(balanceJson)!.AsObject();
        root["manual_corrections"] = new JsonArray(new JsonObject
        {
            ["path"]   = "current_assets",
            ["old"]    = "100",
            ["new"]    = "120",
            ["reason"] = "Corrección sintética.",
            ["at"]     = "2026-11-02T12:00:00Z",
            ["by"]     = "00000000-0000-0000-0000-000000000001",
            ["origin"] = "manual",
        });
        return root.ToJsonString();
    }

    private static async Task<string[]> RenderPagesAsync(Action<AnalysisResult> tweak)
    {
        AnalysisResult result = PdfSyntheticReport.Result();
        tweak(result);

        byte[] pdf = await PdfExportServiceRenderTests.NewService().GenerateAsync(
            result, PdfSyntheticReport.Analysis(), "tenant-synthetic", ["executive_summary", "risk"],
            CancellationToken.None, PdfSyntheticReport.Report());

        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        return Enumerable.Range(1, doc.GetNumberOfPages()).Select(i => PdfTextExtractor.GetTextFromPage(doc.GetPage(i))).ToArray();
    }

    // Pages 2.. as one text (page 1 is the fact sheet), so positions can be compared across a break.
    private static string Body(string[] pages) => string.Join("\n", pages.Skip(1));

    private static int Count(string text, string value) =>
        (text.Length - text.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;

    [Fact]
    public void TheNote_IsThePortalsSentence() => Assert.Equal(Note, PdfLabels.ExecutiveSummaryOriginalReadingNote);

    [Fact]
    public async Task WithCorrections_TheNoteFollowsTheSummary_BeforeTheNextSection()
    {
        string[] pages = await RenderPagesAsync(r =>
        {
            r.ExecutiveSummary = "Primera línea del resumen.\nÚltima línea del resumen.";
            r.BalanceJson      = Corrected(r.BalanceJson!);
        });
        string body = Body(pages);

        Assert.Equal(1, Count(body, Note));
        int heading = body.IndexOf("Resumen ejecutivo", StringComparison.Ordinal);
        int last    = body.IndexOf("Última línea del resumen.", StringComparison.Ordinal);
        int note    = body.IndexOf(Note, StringComparison.Ordinal);
        int next    = body.IndexOf("Análisis de riesgo", StringComparison.Ordinal);

        Assert.True(heading >= 0 && heading < last && last < note && note < next,
            $"heading {heading}, last summary line {last}, note {note}, next section {next}.");
    }

    [Fact]
    public async Task WithoutCorrections_NoNote()
    {
        string[] pages = await RenderPagesAsync(r => r.ExecutiveSummary = "Resumen sin correcciones.");

        Assert.Contains("Resumen sin correcciones.", Body(pages));
        Assert.DoesNotContain(Note, string.Join("\n", pages));
    }

    [Fact]
    public async Task WithCorrections_ButNoSummary_NoNote()
    {
        string[] pages = await RenderPagesAsync(r =>
        {
            r.ExecutiveSummary = null;
            r.BalanceJson      = Corrected(r.BalanceJson!);
        });

        Assert.Contains("Datos no disponibles.", Body(pages));
        Assert.DoesNotContain(Note, string.Join("\n", pages));
    }

    [Fact]
    public async Task AnEmptyTrail_IsNoCorrection()
    {
        string[] pages = await RenderPagesAsync(r =>
        {
            r.ExecutiveSummary = "Resumen con un rastro vacío.";
            JsonObject root = JsonNode.Parse(r.BalanceJson!)!.AsObject();
            root["manual_corrections"] = new JsonArray();
            r.BalanceJson = root.ToJsonString();
        });

        Assert.DoesNotContain(Note, string.Join("\n", pages));
    }

    [Fact]
    public async Task OverAPageBreak_TheNoteSharesThePage_OfTheSummarysLastLine()
    {
        int crossings = 0;
        for (int lines = 0; lines <= 70; lines++)
        {
            string[] pages = await RenderPagesAsync(r =>
            {
                r.ExecutiveSummary = string.Join("\n", Enumerable.Range(1, lines + 1).Select(i => $"Línea de resumen {i}."))
                                     + "\nFin del resumen.";
                r.BalanceJson = Corrected(r.BalanceJson!);
            });

            int lastPage = Array.FindIndex(pages, 1, p => p.Contains("Fin del resumen.", StringComparison.Ordinal));
            int notePage = Array.FindIndex(pages, 1, p => p.Contains(Note, StringComparison.Ordinal));
            Assert.True(lastPage > 0 && lastPage == notePage, $"lines={lines}: summary ends on p{lastPage + 1}, note on p{notePage + 1}.");

            // The summary itself still starts where it always did: under its heading, on page 2.
            Assert.Contains("Línea de resumen 1.", pages[1]);

            if (lastPage > 1) crossings++;
        }

        Assert.True(crossings > 0, "The sweep never carried the summary over a page break.");
    }
}
