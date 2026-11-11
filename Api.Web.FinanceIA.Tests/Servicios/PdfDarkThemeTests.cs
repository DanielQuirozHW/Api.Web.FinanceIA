// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-08

using Api.Web.FinanceIA.Servicios.Pdf;
using Xunit;
using static Api.Web.FinanceIA.Tests.Servicios.PdfThemeInspector;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Both reports rendered dark, every section: navy paper under every page, no text a reader cannot see (each run
/// measured against the fill really behind it), nothing in black, and the score amber on the score figure alone (the caution amber is another, lighter tone).
/// </summary>
public sealed class PdfDarkThemeTests
{
    private const double MinimumTextContrast = 4.5;

    private static readonly Rgb Paper = Rgb.From(PdfPalette.Dark.Paper);
    private static readonly Rgb Amber = Rgb.From(PdfPalette.Dark.ScoreFigure);   // the score amber, not Caution
    private static readonly Rgb Black = new(0, 0, 0);

    internal static async Task<byte[]> RenderCompanyAsync() =>
        await PdfContentFingerprint.FixedClockService().GenerateAsync(
            PdfSyntheticReport.Result(), PdfSyntheticReport.Analysis(), "tenant-synthetic",
            Array.Empty<string>(), CancellationToken.None, PdfSyntheticReport.Report(), PdfTheme.Dark);

    internal static async Task<byte[]> RenderPersonaAsync() =>
        await PdfContentFingerprint.FixedClockService().GeneratePersonAsync(
            PersonPdfFixtures.Report(PersonPdfFixtures.Approved(), PersonPdfFixtures.Evaluate(PersonPdfFixtures.ResponsableInscripto())),
            [], CancellationToken.None, PdfTheme.Dark);

    public static TheoryData<string> Reports() => new() { "company", "persona" };

    private static async Task<Report> InspectAsync(string report) =>
        Inspect(report == "company" ? await RenderCompanyAsync() : await RenderPersonaAsync(), Paper);

    [Theory]
    [MemberData(nameof(Reports))]
    public async Task EveryPage_IsPaintedInNavyPaper_BeforeAnythingElse(string report)
    {
        Report pdf = await InspectAsync(report);

        Assert.True(pdf.Pages > 1);
        Assert.Equal(pdf.Pages, pdf.FirstFills.Count);
        foreach (Fill fill in pdf.FirstFills)
        {
            Assert.Equal(Paper, fill.Color);
            Assert.Equal(pdf.PageSize.GetWidth(), fill.Bounds.GetWidth(), 0.5);
            Assert.Equal(pdf.PageSize.GetHeight(), fill.Bounds.GetHeight(), 0.5);
        }
    }

    [Theory]
    [MemberData(nameof(Reports))]
    public async Task NoText_IsDrawnBelowFourAndAHalfToOne_AgainstWhatIsBehindIt(string report)
    {
        Report pdf = await InspectAsync(report);
        Assert.NotEmpty(pdf.Text);

        // The walker does see what text sits on: cards, table headers and tints, not only the page.
        Assert.Contains(pdf.Text, run => run.Background != Paper);

        List<string> failures = pdf.Text
            .Where(run => run.Color is null || Contrast(run.Color, run.Background) < MinimumTextContrast)
            .Select(run => $"p{run.Page} '{run.Text}' {run.Color?.ToString() ?? "(no colour)"} on {run.Background} = " +
                           (run.Color is null ? "?" : Contrast(run.Color, run.Background).ToString("0.00")))
            .Distinct()
            .ToList();

        Assert.True(failures.Count == 0, "Low-contrast text:\n" + string.Join("\n", failures.Take(40)));
    }

    [Theory]
    [MemberData(nameof(Reports))]
    public async Task NothingIsDrawnInBlack(string report)
    {
        Report pdf = await InspectAsync(report);

        Assert.DoesNotContain(pdf.Text, run => run.Color == Black);
        Assert.DoesNotContain(pdf.Paths, path => (path.Fills && path.Fill == Black) || (path.Strokes && path.Stroke == Black));
    }

    [Fact]
    public async Task TheScoreAmber_IsTheScoreFigure_AndNothingElse()
    {
        Report company = await InspectAsync("company");

        List<TextRun> amber = company.Text.Where(run => run.Color == Amber).ToList();
        Assert.Equal("820", string.Concat(amber.Select(run => run.Text)));
        Assert.All(amber, run => Assert.Equal(1, run.Page));

        Assert.DoesNotContain(company.Paths, path => path.Fill == Amber || path.Stroke == Amber);
        Assert.DoesNotContain((await InspectAsync("persona")).Text, run => run.Color == Amber);
    }

    [Theory]
    [MemberData(nameof(Reports))]
    public async Task Dark_IsNotTheLightDocument(string report)
    {
        byte[] bytes = report == "company" ? await RenderCompanyAsync() : await RenderPersonaAsync();
        string light = report == "company" ? PdfLightFingerprintTests.CompanyLight : PdfLightFingerprintTests.PersonaLight;

        Assert.NotEqual(light, PdfContentFingerprint.Of(bytes));
    }
}
