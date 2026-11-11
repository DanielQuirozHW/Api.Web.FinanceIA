// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-09

using Api.Web.FinanceIA.Models.Financia;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using Xunit;
using Xunit.Abstractions;

namespace Api.Web.FinanceIA.Tests.Servicios.PdfReport;

/// <summary>
/// QA Q57: a label line never ends a page while the table (or text) it introduces starts the next. An executive
/// summary one line longer at a time walks each pair down page 2 and over the break; at every step the label and the
/// first thing under it must share a page.
/// </summary>
public sealed class PdfOrphanHeadingTests(ITestOutputHelper output)
{
    private const int MaxLines = 70;

    // (label, the first text that must share its page, found after the label).
    private static readonly (string Label, string Follower)[] CashFlowPairs =
    [
        ("Flujo de efectivo", "De dónde salió"),
        ("Composición de cada sección", "Operativo:"),
        ("Operativo:", "esta lectura no identifica"),
        ("Inversión:", "Concepto"),
        ("Financiación:", "Concepto"),
        ("Lectura del flujo", "•"),
    ];

    private static readonly (string Label, string Follower)[] LufePairs =
    [
        ("Ventas y compras mensuales", "Período"),
        ("Indicadores anuales", "Período"),
    ];

    private static readonly (string Label, string Follower)[] RiskPairs =
    [
        ("Perfil de scoring", "Componente"),
        ("Componente", "Score compuesto"),
        ("Justificación del scoring", "Liquidez"),
    ];

    private static async Task<string[]> RenderPagesAsync(int summaryLines, string section)
    {
        AnalysisResult result = PdfSyntheticReport.Result();
        result.ExecutiveSummary = string.Join("\n", Enumerable.Range(1, summaryLines + 1).Select(i => $"Línea de resumen {i}."));
        if (section == "risk")
            result.ScoringNarrative = "Liquidez y solvencia dentro de los rangos del perfil; el endeudamiento es moderado.";

        byte[] pdf = await PdfExportServiceRenderTests.NewService().GenerateAsync(
            result, PdfSyntheticReport.Analysis(), "tenant-synthetic", ["executive_summary", section],
            CancellationToken.None, PdfSyntheticReport.Report());

        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        return Enumerable.Range(1, doc.GetNumberOfPages()).Select(i => PdfTextExtractor.GetTextFromPage(doc.GetPage(i))).ToArray();
    }

    // The page (0-based) of the first occurrence of `text` at or after (page, index); null when absent.
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

    [Theory]
    [InlineData("cash_flow")]
    [InlineData("lufe")]
    [InlineData("risk")]
    public async Task EveryLabel_SharesItsPage_WithWhatItIntroduces(string section)
    {
        (string Label, string Follower)[] pairs = section switch
        {
            "cash_flow" => CashFlowPairs,
            "lufe"      => LufePairs,
            _           => RiskPairs,
        };

        int crossings = 0;
        for (int lines = 0; lines <= MaxLines; lines++)
        {
            string[] pages = await RenderPagesAsync(lines, section);

            foreach ((string label, string follower) in pairs)
            {
                (int Page, int Index)? at = Find(pages, label, 1, 0);
                Assert.True(at is not null, $"lines={lines}: '{label}' is missing.");

                (int Page, int Index)? next = Find(pages, follower, at.Value.Page, at.Value.Index + label.Length);
                Assert.True(next is not null, $"lines={lines}: nothing '{follower}' after '{label}'.");
                Assert.True(at.Value.Page == next.Value.Page,
                    $"lines={lines}: '{label}' on page {at.Value.Page + 1}, '{follower}' on page {next.Value.Page + 1}.");

                if (at.Value.Page >= 2)
                    crossings++;
            }
        }

        // The sweep must actually push the pairs past page 2, or it proved nothing.
        output.WriteLine($"{section}: {crossings} label placements beyond page 2.");
        Assert.True(crossings > 0, "The sweep never moved a label onto page 3.");
    }
}
