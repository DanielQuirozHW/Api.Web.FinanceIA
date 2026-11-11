// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-08

using System.Text.RegularExpressions;
using Api.Web.FinanceIA.Servicios;
using Xunit;
using PigDocument = UglyToad.PdfPig.PdfDocument;
using PigPage = UglyToad.PdfPig.Content.Page;
using PigWord = UglyToad.PdfPig.Content.Word;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// The redesigned company report, read back with PdfPig from a complete SYNTHETIC analysis
/// (<see cref="PdfSyntheticReport"/>): the first-page fact sheet, the furniture on every page, the document
/// information and the outline — and iText's Producer entry left exactly as iText writes it.
/// </summary>
public sealed class PdfReportGoldenTests
{
    // Every section heading the company report can print.
    private static readonly string[] SectionHeadings =
    [
        "Resumen ejecutivo", "Análisis de riesgo", "Estado patrimonial", "Ratios financieros", "Flujo de efectivo",
        "Datos BCRA", "Ventas declaradas (LUFE)", "Datos CASFOG", "Anomalías detectadas", "Cierre del informe",
    ];

    private static async Task<byte[]> RenderAsync() =>
        await PdfExportServiceRenderTests.NewService().GenerateAsync(
            PdfSyntheticReport.Result(), PdfSyntheticReport.Analysis(), "tenant-synthetic",
            Array.Empty<string>(), CancellationToken.None, PdfSyntheticReport.Report());

    private static string PageText(PigPage page) => string.Join(" ", page.GetWords().Select(word => word.Text));

    [Fact]
    public async Task PageOne_IsTheFactSheet_WithTheCompanyTheScoreAndTheLevel()
    {
        using PigDocument pdf = PigDocument.Open(await RenderAsync());
        string first = PageText(pdf.GetPage(1));

        Assert.Contains("Metalúrgica Ejemplo S.A.", first);
        Assert.Contains("30-00000000-0", first);
        Assert.Contains("820", first);
        Assert.Contains("Riesgo Bajo", first);
        Assert.Contains("Cómo se compone el score", first);
        Assert.Contains("Puntos de atención", first);
        Assert.Contains("Decisión", first);
        Assert.Contains("Informe crediticio", first);

        // The LUFE penalty is the engine's −100, never with the rounding folded in.
        Assert.Contains("−100", first);
        Assert.DoesNotContain("−105", first);

        // The executive summary opens page 2, not page 1.
        Assert.DoesNotContain("Resumen ejecutivo", first);
        Assert.Contains("Resumen ejecutivo", PageText(pdf.GetPage(2)));
    }

    [Fact]
    public async Task NoPage_EndsWithASectionHeading()
    {
        using PigDocument pdf = PigDocument.Open(await RenderAsync());

        foreach (PigPage page in pdf.GetPages())
        {
            // The body's last line: the lowest words above the running-footer band.
            List<PigWord> body = page.GetWords().Where(word => word.BoundingBox.Bottom > 54).ToList();
            if (body.Count == 0) continue;

            double lowest = body.Min(word => word.BoundingBox.Bottom);
            string lastLine = string.Join(" ", body
                .Where(word => Math.Abs(word.BoundingBox.Bottom - lowest) < 3)
                .OrderBy(word => word.BoundingBox.Left)
                .Select(word => word.Text));

            Assert.DoesNotContain(lastLine, SectionHeadings);
        }
    }

    [Fact]
    public async Task EveryPage_SaysPaginaXdeY_WithSingleSpaces()
    {
        using PigDocument pdf = PigDocument.Open(await RenderAsync());
        int total = pdf.NumberOfPages;

        foreach (PigPage page in pdf.GetPages())
        {
            // The footer's own words, in reading order on its baseline.
            List<PigWord> footer = page.GetWords()
                .Where(word => word.BoundingBox.Bottom < 45)
                .OrderBy(word => word.BoundingBox.Left)
                .ToList();
            string text = string.Join(" ", footer.Select(word => word.Text));

            Assert.Contains($"Página {page.Number} de {total}", text);
            Assert.DoesNotMatch(new Regex(@"de\s{2,}\d"), text);

            // No gap wider than a word space between "de" and the total.
            PigWord de    = footer.Last(word => word.Text == "de");
            PigWord count = footer.Single(word => word.Text == total.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                                  && word.BoundingBox.Left > de.BoundingBox.Right);
            double gap = count.BoundingBox.Left - de.BoundingBox.Right;
            Assert.InRange(gap, 0.5, 3.5);

            Assert.Contains("Financia · Confidencial · ID 5C0C72EB", text);
        }
    }

    [Fact]
    public async Task TheHeader_IsNotDuplicated()
    {
        using PigDocument pdf = PigDocument.Open(await RenderAsync());

        // "Confidencial" lives in the footer only (it used to be in the header too); "Solo uso interno" is gone.
        foreach (PigPage page in pdf.GetPages())
        {
            string text = PageText(page);
            Assert.Single(Regex.Matches(text, "Confidencial"));
            Assert.DoesNotContain("Solo uso interno", text);
        }
    }

    [Fact]
    public async Task DocumentInformation_IsSet_AndTheProducerIsIText()
    {
        using PigDocument pdf = PigDocument.Open(await RenderAsync());

        Assert.Equal("Informe crediticio — Metalúrgica Ejemplo S.A. — Ejercicio 2025", pdf.Information.Title);
        Assert.Equal("SGR Sintética", pdf.Information.Author);
        Assert.Equal("Informe crediticio", pdf.Information.Subject);
        Assert.Equal("Financia", pdf.Information.Creator);

        // Untouched: exactly what iText writes for a document whose Producer nobody set.
        Assert.Equal(UntouchedProducer(), pdf.Information.Producer);
        Assert.Contains("iText", pdf.Information.Producer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheOutline_HasOneEntryPerRenderedSection()
    {
        using PigDocument pdf = PigDocument.Open(await RenderAsync());

        Assert.True(pdf.TryGetBookmarks(out var bookmarks));
        string[] titles = bookmarks.GetNodes().Select(node => node.Title).ToArray();

        Assert.Equal("Ficha del análisis", titles[0]);
        foreach (string heading in SectionHeadings)
            Assert.Contains(heading, titles);
        Assert.Equal(SectionHeadings.Length + 1, titles.Length);
    }

    // The Producer iText writes when nothing overrides it, from a bare document of the same library.
    private static string UntouchedProducer()
    {
        using var stream = new MemoryStream();
        using (var writer = new iText.Kernel.Pdf.PdfWriter(stream))
        using (var document = new iText.Kernel.Pdf.PdfDocument(writer))
        {
            document.AddNewPage();
        }

        using PigDocument bare = PigDocument.Open(stream.ToArray());
        return bare.Information.Producer ?? string.Empty;
    }
}
