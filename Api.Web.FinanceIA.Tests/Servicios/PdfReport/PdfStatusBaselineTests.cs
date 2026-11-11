// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-09

using System.Text.Json;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Lufe;
using Xunit;
using Xunit.Abstractions;

namespace Api.Web.FinanceIA.Tests.Servicios.PdfReport;

/// <summary>
/// A status label reads on the line of its row's figures: in the ratios table, the score-component table, CASFOG's
/// "Estado" and LUFE's "Sin declaración", the label's baseline is the baseline of the figures (JetBrains Mono) of the
/// same row, within ±0.5 pt — read from the text geometry of the rendered synthetic export.
/// </summary>
public sealed class PdfStatusBaselineTests(ITestOutputHelper output)
{
    private const float Tolerance = 0.5f;

    // Same row: the label's baseline within this distance of a figure's (a row is ~21 pt tall).
    private const float SameRow = 6f;

    private static readonly string[] RatioStatuses = ["Saludable", "Atención", "Crítico", "N/D", "Liquidez máxima", "No disponible"];
    private static readonly string[] CasfogStatuses = ["al día", "en mora"];
    private const string NoDeclaration = "Sin declaración";

    private static async Task<byte[]> RenderAsync()
    {
        AnalysisResult result = PdfSyntheticReport.Result();

        // One month of the LUFE window without declared purchases, so the monthly table prints "Sin declaración".
        LufeData lufe = JsonSerializer.Deserialize<LufeData>(result.LufeDataJson!)!;
        lufe.VentasComprasMensual![^3].Compras = null;
        result.LufeDataJson = JsonSerializer.Serialize(lufe);

        return await PdfExportServiceRenderTests.NewService().GenerateAsync(
            result, PdfSyntheticReport.Analysis(), "tenant-synthetic", Array.Empty<string>(),
            CancellationToken.None, PdfSyntheticReport.Report());
    }

    [Fact]
    public async Task EveryStatusLabel_SitsOnTheBaseline_OfItsRowsFigures()
    {
        byte[] pdf = await RenderAsync();

        var found = new List<(string Label, int Page, float Delta)>();

        // Page 1 is the fact sheet; the tables live from page 2 on.
        for (int page = 2; page <= PdfPageGeometry.PageCount(pdf); page++)
        {
            IReadOnlyList<PdfPageGeometry.Run> runs = PdfPageGeometry.Read(pdf, page).Runs;
            List<PdfPageGeometry.Run> figures = runs.Where(r => r.Font.Contains("JetBrainsMono", StringComparison.Ordinal)).ToList();

            foreach (PdfPageGeometry.Run label in runs)
            {
                string text = label.Text.Trim();
                if (!RatioStatuses.Contains(text) && !CasfogStatuses.Contains(text) && text != NoDeclaration) continue;

                List<float> deltas = figures
                    .Select(f => f.Baseline - label.Baseline)
                    .Where(d => Math.Abs(d) < SameRow)
                    .ToList();
                if (deltas.Count == 0) continue;   // not in a row of figures

                float delta = deltas.MinBy(Math.Abs);
                found.Add((text, page, delta));
                output.WriteLine($"p{page} '{text}' Δbaseline={delta:F2} pt");
            }
        }

        // Every table this fix covers was actually measured.
        Assert.Contains(found, f => f.Label is "Saludable" or "Atención" or "Crítico");
        Assert.Contains(found, f => CasfogStatuses.Contains(f.Label));
        Assert.Contains(found, f => f.Label == NoDeclaration);
        Assert.True(found.Count(f => RatioStatuses.Contains(f.Label)) >= 10, "Both the ratios and the component tables were measured.");

        Assert.All(found, f => Assert.True(Math.Abs(f.Delta) <= Tolerance,
            $"p{f.Page} '{f.Label}' sits {f.Delta:F2} pt off its row's figures."));
    }
}
