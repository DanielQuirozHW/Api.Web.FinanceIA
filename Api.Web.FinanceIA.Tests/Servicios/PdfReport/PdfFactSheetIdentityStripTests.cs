// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-09

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.Pdf;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios.PdfReport;

/// <summary>QA Q52: page 1's identity line and the four-column strip.</summary>
public sealed class PdfFactSheetIdentityStripTests
{
    private static async Task<PdfPageGeometry.Page> RenderPageOneAsync(string status, string analystName)
    {
        Analysis analysis = PdfSyntheticReport.Analysis();
        analysis.Status = status;

        var names = new Dictionary<Guid, string>(PdfSyntheticReport.Report().UserNames)
        {
            [analysis.RequestedBy] = analystName,
        };

        byte[] pdf = await PdfContentFingerprint.FixedClockService().GenerateAsync(
            PdfSyntheticReport.Result(), analysis, "tenant-synthetic", ["footer"], CancellationToken.None,
            PdfSyntheticReport.Report() with { UserNames = names });

        return PdfPageGeometry.Read(pdf, 1);
    }

    [Fact]
    public async Task TheEjercicio_SetsOnlyItsFiguresInMono()
    {
        PdfPageGeometry.Page page = await RenderPageOneAsync(AnalysisStatuses.Approved, "Analista Uno");

        PdfPageGeometry.Run year    = Assert.Single(page.Runs, r => r.Text == "2025");
        PdfPageGeometry.Run joiner  = Assert.Single(page.Runs, r => r.Text == ", cierre ");
        PdfPageGeometry.Run closing = Assert.Single(page.Runs, r => r.Text == "06/2025");

        Assert.Contains("JetBrains", year.Font, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("JetBrains", closing.Font, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("JetBrains", joiner.Font, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(year.Baseline, joiner.Baseline, 0.01f);
        Assert.Equal(year.Baseline, closing.Baseline, 0.01f);
    }

    [Fact]
    public async Task ThePendingStatus_DoesNotWrap_AndTheAnalystNameIsCollapsed()
    {
        PdfPageGeometry.Page page = await RenderPageOneAsync(AnalysisStatuses.PendingReview, "Analista   Uno ");

        // The strip sits above the decision block, which also says "Pendiente de revisión.": take the top-most one.
        PdfPageGeometry.Run status = page.Runs
            .Where(r => r.Text.StartsWith("Pendiente", StringComparison.Ordinal))
            .OrderByDescending(r => r.Baseline)
            .First();
        Assert.Equal("Pendiente de revisión", status.Text);

        Assert.Contains(page.Runs, r => r.Text == "Analista Uno");
        Assert.DoesNotContain(page.Runs, r => r.Text.Contains("Analista  ", StringComparison.Ordinal));
    }

    [Fact]
    public void StripColumns_FitTheirContents_AndShareTheSlack()
    {
        float[] widths = StripColumns.Fit([100f, 60f, 120f, 80f], 400f, StripColumns.FallbackPercents);

        Assert.Equal([110f, 70f, 130f, 90f], widths);
    }

    [Fact]
    public void StripColumns_ThatFitExactly_KeepTheirMinimums()
    {
        Assert.Equal([100f, 300f], StripColumns.Fit([100f, 300f], 400f, [50f, 50f]));
    }

    [Fact]
    public void StripColumns_ThatDoNotFit_FallBackToTheFixedShares()
    {
        float[] widths = StripColumns.Fit([150f, 150f, 150f, 150f], 500f, StripColumns.FallbackPercents);

        Assert.Equal([110f, 130f, 110f, 150f], widths);
    }

    [Theory]
    [InlineData("Analista  Uno", "Analista Uno")]
    [InlineData("  Analista \t Uno\n", "Analista Uno")]
    [InlineData("Uno", "Uno")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void CollapseWhitespace(string? input, string expected)
    {
        Assert.Equal(expected, PdfText.CollapseWhitespace(input));
    }

    [Theory]
    [InlineData("El resto, -$ 30.000.000, corresponde a conceptos.", "El resto, \u2212$ 30.000.000, corresponde a conceptos.")]
    [InlineData("aportó $ 5.000 y -$ 200 y -$ 1", "aportó $ 5.000 y \u2212$ 200 y \u2212$ 1")]
    [InlineData("Un guión - suelto queda igual.", "Un guión - suelto queda igual.")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void TypographicMinus_ChangesOnlyTheSignOfAPesoAmount(string? input, string expected)
    {
        Assert.Equal(expected, PdfText.TypographicMinus(input));
    }
}
