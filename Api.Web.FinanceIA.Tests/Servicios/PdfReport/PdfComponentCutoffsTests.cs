// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-08

using System.Text.Json;
using System.Text.RegularExpressions;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.Pdf;
using Xunit;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Api.Web.FinanceIA.Tests.Servicios.PdfReport;

/// <summary>
/// The score components ("Cómo se compone el score" on page 1, the component table of "Análisis de riesgo") are
/// coloured with the cutoffs the analysis was scored with — the breakdown's levelCutoffs — never fixed 35 / 60.
/// </summary>
public sealed class PdfComponentCutoffsTests
{
    private const string DefaultCutoffs = "\"levelCutoffs\": {\"low\": 35, \"medium\": 60, \"high\": 80}";

    // The synthetic components are ratios 5, BCRA 10, macro 10: all Low under the defaults; under these, ratios is
    // Medium and BCRA / macro are High.
    private const string TightCutoffs = "\"levelCutoffs\": {\"low\": 4, \"medium\": 8, \"high\": 70}";

    [Theory]
    [InlineData(4,   "Good")]
    [InlineData(4.01, "Caution")]
    [InlineData(8,   "Caution")]
    [InlineData(9,   "Bad")]
    [InlineData(71,  "Bad")]   // Critical reads as bad too
    public void ComponentTone_UsesTheGivenCutoffs(double score, string expected)
    {
        var cutoffs = new ScoreLevelCutoffs(4m, 8m, 70m);

        Assert.Equal(Enum.Parse<PdfTone>(expected), PdfLabels.ComponentTone((decimal)score, cutoffs));
    }

    [Theory]
    [InlineData(35, "Good")]
    [InlineData(36, "Caution")]
    [InlineData(60, "Caution")]
    [InlineData(61, "Bad")]
    public void ComponentTone_WithoutCutoffs_FallsBackToTheEngineDefaults(double score, string expected)
    {
        Assert.Equal(Enum.Parse<PdfTone>(expected), PdfLabels.ComponentTone((decimal)score, cutoffs: null));
    }

    [Fact]
    public void LevelCutoffs_ReadBackAsTheScoringStageWritesThem()
    {
        string json = JsonSerializer.Serialize(new { levelCutoffs = new ScoreLevelCutoffs(20m, 45.5m, 70m) });

        ScoreLevelCutoffs? read = JsonSerializer.Deserialize<JsonElement>(json).GetProperty("levelCutoffs").Deserialize<ScoreLevelCutoffs>();

        Assert.Equal(new ScoreLevelCutoffs(20m, 45.5m, 70m), read);
    }

    [Fact]
    public async Task ComponentTable_ReadsTheAnalysisCutoffs()
    {
        string defaults = ComponentTableText(await RenderAsync(cutoffs: DefaultCutoffs));
        string tight    = ComponentTableText(await RenderAsync(cutoffs: TightCutoffs));

        Assert.Equal((3, 0, 0), Statuses(defaults));
        Assert.Equal((0, 1, 2), Statuses(tight));   // ratios Atención; BCRA and macro Crítico
    }

    [Fact]
    public async Task ComponentTable_WithoutCutoffs_UsesTheDefaults()
    {
        Assert.Equal((3, 0, 0), Statuses(ComponentTableText(await RenderAsync(cutoffs: null))));
    }

    [Fact]
    public async Task PageOneBars_ReadTheAnalysisCutoffs()
    {
        PdfPalette light = PdfPalette.Light;
        var paper = PdfThemeInspector.Rgb.From(light.Paper);

        PdfThemeInspector.Report defaults = PdfThemeInspector.Inspect(await RenderAsync(cutoffs: DefaultCutoffs), paper);
        PdfThemeInspector.Report tight    = PdfThemeInspector.Inspect(await RenderAsync(cutoffs: TightCutoffs), paper);

        int Count(PdfThemeInspector.Report report, DeviceRgbToken token) =>
            report.Paths.Count(p => p.Page == 1 && p.Fills && p.Fill == PdfThemeInspector.Rgb.From(token(light)));

        // Three bars leave the good tone: ratios to caution, BCRA and macro to bad.
        Assert.Equal(Count(defaults, p => p.Success) - 3, Count(tight, p => p.Success));
        Assert.Equal(Count(defaults, p => p.Danger) + 2, Count(tight, p => p.Danger));
    }

    private delegate iText.Kernel.Colors.DeviceRgb DeviceRgbToken(PdfPalette palette);

    private static async Task<byte[]> RenderAsync(string? cutoffs)
    {
        AnalysisResult result = PdfSyntheticReport.Result();
        Assert.Contains(DefaultCutoffs, result.ScoreBreakdownJson);

        result.ScoreBreakdownJson = cutoffs is null
            ? Regex.Replace(result.ScoreBreakdownJson!, @",\s*""levelCutoffs"":\s*\{[^}]*\}", string.Empty)
            : result.ScoreBreakdownJson!.Replace(DefaultCutoffs, cutoffs, StringComparison.Ordinal);

        if (cutoffs is null)
            Assert.DoesNotContain("levelCutoffs", result.ScoreBreakdownJson);

        return await PdfExportServiceRenderTests.NewService().GenerateAsync(
            result, PdfSyntheticReport.Analysis(), "tenant-synthetic",
            Array.Empty<string>(), CancellationToken.None, PdfSyntheticReport.Report());
    }

    // (Saludable, Atención, Crítico) in the component table. PdfPig does not keep a vertically centred status
    // cell in its row's order, so the table is checked by its counts.
    private static (int Good, int Caution, int Bad) Statuses(string table)
    {
        string[] words = table.Split(' ');
        return (words.Count(w => w == "Saludable"), words.Count(w => w == "Atención"), words.Count(w => w == "Crítico"));
    }

    // The component table of "Análisis de riesgo": from its header row to the composite line under it.
    private static string ComponentTableText(byte[] pdfBytes)
    {
        using PigDocument pdf = PigDocument.Open(pdfBytes);
        string all = string.Join(" ", pdf.GetPages().Skip(1).SelectMany(page => page.GetWords()).Select(word => word.Text));
        int start = all.IndexOf("Componente Puntaje Peso Estado", StringComparison.Ordinal);
        Assert.True(start >= 0, "the component table was not rendered");
        int end = all.IndexOf("Score compuesto", start, StringComparison.Ordinal);
        Assert.True(end > start, "the composite line was not rendered");
        return all[start..end];
    }
}
