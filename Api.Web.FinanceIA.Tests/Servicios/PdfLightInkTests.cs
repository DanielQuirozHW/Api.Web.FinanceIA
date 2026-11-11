// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-08

using Api.Web.FinanceIA.Servicios.Pdf;
using Xunit;
using static Api.Web.FinanceIA.Tests.Servicios.PdfThemeInspector;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// The light theme sets the document's default text colour to its Ink, as the dark theme does: a paragraph that
/// names no colour prints in #0B1222, never in iText's default black.
/// </summary>
public sealed class PdfLightInkTests
{
    private static readonly Rgb Black = new(0, 0, 0);

    [Theory]
    [InlineData("company")]
    [InlineData("persona")]
    public async Task NoLightTextIsDrawnInDefaultBlack(string report)
    {
        byte[] bytes = report == "company"
            ? await PdfLightFingerprintTests.RenderCompanyAsync()
            : await PdfLightFingerprintTests.RenderPersonaAsync();

        Report pdf = Inspect(bytes, Rgb.From(PdfPalette.Light.Paper));

        Assert.DoesNotContain(pdf.Text, run => run.Color == Black);
        Assert.Contains(pdf.Text, run => run.Color == Rgb.From(PdfPalette.Light.Ink));
    }
}
