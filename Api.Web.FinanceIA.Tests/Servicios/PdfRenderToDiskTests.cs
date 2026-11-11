// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-08

using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.Pdf;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Writes the synthetic exports to disk so their pages can be rendered to PNG for review (the PR
/// screenshots). Does nothing unless FINANCIA_PDF_RENDER_DIR is set, so CI never writes files.
/// </summary>
public sealed class PdfRenderToDiskTests
{
    // The CUIT every screenshot prints: a placeholder that can never be a real client's.
    internal const string PlaceholderCuit = "30000000000";

    [Fact]
    public async Task SyntheticCompanyExport_WrittenToDisk_WhenRequested()
    {
        string? dir = Environment.GetEnvironmentVariable("FINANCIA_PDF_RENDER_DIR");
        if (string.IsNullOrWhiteSpace(dir)) return;

        PdfExportService service = PdfExportServiceRenderTests.NewService();
        byte[] bytes = await service.GenerateAsync(
            PdfSyntheticReport.Result(), PdfSyntheticReport.Analysis(), "tenant-synthetic",
            Array.Empty<string>(), CancellationToken.None, PdfSyntheticReport.Report() with { EntityCuit = PlaceholderCuit });

        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, "informe-sintetico.pdf"), bytes);
    }

    [Fact]
    public async Task SyntheticPersonaExport_WrittenToDisk_WhenRequested()
    {
        string? dir = Environment.GetEnvironmentVariable("FINANCIA_PDF_RENDER_DIR");
        if (string.IsNullOrWhiteSpace(dir)) return;

        var evaluation = PersonPdfFixtures.Evaluate(PersonPdfFixtures.ResponsableInscripto());
        byte[] bytes = await PdfExportServiceRenderTests.NewService().GeneratePersonAsync(
            PersonPdfFixtures.Report(PersonPdfFixtures.Approved(), evaluation) with { EntityCuit = PlaceholderCuit },
            [], CancellationToken.None);

        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, "persona-sintetica.pdf"), bytes);
    }

    [Fact]
    public async Task SyntheticDarkExports_WrittenToDisk_WhenRequested()
    {
        string? dir = Environment.GetEnvironmentVariable("FINANCIA_PDF_RENDER_DIR");
        if (string.IsNullOrWhiteSpace(dir)) return;

        PdfExportService service = PdfExportServiceRenderTests.NewService();
        byte[] company = await service.GenerateAsync(
            PdfSyntheticReport.Result(), PdfSyntheticReport.Analysis(), "tenant-synthetic",
            Array.Empty<string>(), CancellationToken.None,
            PdfSyntheticReport.Report() with { EntityCuit = PlaceholderCuit }, PdfTheme.Dark);

        var evaluation = PersonPdfFixtures.Evaluate(PersonPdfFixtures.ResponsableInscripto());
        byte[] persona = await service.GeneratePersonAsync(
            PersonPdfFixtures.Report(PersonPdfFixtures.Approved(), evaluation) with { EntityCuit = PlaceholderCuit },
            [], CancellationToken.None, PdfTheme.Dark);

        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, "informe-sintetico-dark.pdf"), company);
        await File.WriteAllBytesAsync(Path.Combine(dir, "persona-sintetica-dark.pdf"), persona);
    }

    [Fact]
    public async Task SyntheticCoBrandedExports_WrittenToDisk_WhenRequested()
    {
        string? dir = Environment.GetEnvironmentVariable("FINANCIA_PDF_RENDER_DIR");
        if (string.IsNullOrWhiteSpace(dir)) return;

        // A synthetic client ("SGR Ejemplo"), a generated placeholder logo and a placeholder CUIT: nothing real.
        TenantPdfBranding branding = PdfBranding.BrandingFixtures.CoBranded("SGR Ejemplo");
        PdfExportService service = PdfExportServiceRenderTests.NewService();
        Directory.CreateDirectory(dir);

        foreach (PdfTheme theme in new[] { PdfTheme.Light, PdfTheme.Dark })
        {
            string suffix = theme == PdfTheme.Dark ? "dark" : "light";

            byte[] company = await service.GenerateAsync(
                PdfSyntheticReport.Result(), PdfSyntheticReport.Analysis(), "tenant-synthetic",
                Array.Empty<string>(), CancellationToken.None,
                PdfSyntheticReport.Report() with { EntityCuit = PlaceholderCuit, TenantDisplayName = "SGR Ejemplo", Branding = branding },
                theme);

            var evaluation = PersonPdfFixtures.Evaluate(PersonPdfFixtures.ResponsableInscripto());
            byte[] persona = await service.GeneratePersonAsync(
                PersonPdfFixtures.Report(PersonPdfFixtures.Approved(), evaluation)
                    with { EntityCuit = PlaceholderCuit, TenantDisplayName = "SGR Ejemplo", Branding = branding },
                [], CancellationToken.None, theme);

            await File.WriteAllBytesAsync(Path.Combine(dir, $"informe-cobrand-{suffix}.pdf"), company);
            await File.WriteAllBytesAsync(Path.Combine(dir, $"persona-cobrand-{suffix}.pdf"), persona);
        }
    }

    // QA Q48 shared case 1 (Portal PR #452), in millions: the placements were printed under Financing.
    [Fact]
    public async Task SyntheticCashFlowCase1Export_WrittenToDisk_WhenRequested()
    {
        string? dir = Environment.GetEnvironmentVariable("FINANCIA_PDF_RENDER_DIR");
        if (string.IsNullOrWhiteSpace(dir)) return;

        AnalysisResult result = PdfSyntheticReport.Result();
        JsonObject balance = JsonNode.Parse(result.BalanceJson!)!.AsObject();
        balance["cash_flow"] = JsonNode.Parse("""
            {
              "present": true,
              "cash_beginning": 400000000, "cash_end": 400000000, "net_change": 0,
              "operating": 1300000000, "investing": -1000000000, "financing": -300000000,
              "components": { "capex": -1000000000, "short_term_debt_raised": 200000000, "financial_placements": -500000000 }
            }
            """);
        result.BalanceJson = balance.ToJsonString();

        byte[] bytes = await PdfExportServiceRenderTests.NewService().GenerateAsync(
            result, PdfSyntheticReport.Analysis(), "tenant-synthetic", ["cash_flow"], CancellationToken.None,
            PdfSyntheticReport.Report() with { EntityCuit = PlaceholderCuit });

        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, "flujo-caso-1.pdf"), bytes);
    }

    // The LUFE page break of the last real export: five annual balances (2025–2021) under a 28-line executive summary
    // put four rows at the foot of a page and 2021 alone on the next. Both themes.
    [Fact]
    public async Task SyntheticLufeBreakCase_WrittenToDisk_WhenRequested()
    {
        string? dir = Environment.GetEnvironmentVariable("FINANCIA_PDF_RENDER_DIR");
        if (string.IsNullOrWhiteSpace(dir)) return;

        AnalysisResult result = PdfSyntheticReport.Result();
        result.ExecutiveSummary = string.Join("\n", Enumerable.Range(1, 28).Select(i => $"Línea de resumen {i}."));
        PdfReport.PdfLufePaginationTests.AnnualRows(result, 5);

        PdfExportService service = PdfExportServiceRenderTests.NewService();
        Directory.CreateDirectory(dir);

        foreach (PdfTheme theme in new[] { PdfTheme.Light, PdfTheme.Dark })
        {
            byte[] bytes = await service.GenerateAsync(
                result, PdfSyntheticReport.Analysis(), "tenant-synthetic", ["executive_summary", "lufe"], CancellationToken.None,
                PdfSyntheticReport.Report() with { EntityCuit = PlaceholderCuit }, theme);

            await File.WriteAllBytesAsync(Path.Combine(dir, $"lufe-corte-{(theme == PdfTheme.Dark ? "dark" : "light")}.pdf"), bytes);
        }
    }
}
