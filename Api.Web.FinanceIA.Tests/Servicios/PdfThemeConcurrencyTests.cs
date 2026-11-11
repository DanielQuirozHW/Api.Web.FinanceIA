// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-08

using System.Collections.Concurrent;
using Api.Web.FinanceIA.Servicios.Pdf;
using Xunit;
using static Api.Web.FinanceIA.Tests.Servicios.PdfThemeInspector;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// The theme is per render: light and dark exports of both reports running at once never borrow each other's
/// palette. Every light one still draws exactly the pinned light document; every dark one is navy throughout and
/// prints no light ink.
/// </summary>
public sealed class PdfThemeConcurrencyTests
{
    private const int ConcurrentExports = 24;

    [Fact]
    public async Task ParallelLightAndDarkExports_EachGetTheirOwnPalette()
    {
        var results = new ConcurrentBag<(bool IsDark, bool IsCompany, byte[] Bytes)>();

        await Parallel.ForEachAsync(
            Enumerable.Range(0, ConcurrentExports),
            new ParallelOptions { MaxDegreeOfParallelism = ConcurrentExports },
            async (i, _) =>
            {
                bool isDark    = i % 2 == 1;
                bool isCompany = i % 4 < 2;
                byte[] bytes = (isDark, isCompany) switch
                {
                    (false, true)  => await PdfLightFingerprintTests.RenderCompanyAsync(),
                    (false, false) => await PdfLightFingerprintTests.RenderPersonaAsync(),
                    (true, true)   => await PdfDarkThemeTests.RenderCompanyAsync(),
                    (true, false)  => await PdfDarkThemeTests.RenderPersonaAsync(),
                };
                results.Add((isDark, isCompany, bytes));
            });

        Assert.Equal(ConcurrentExports, results.Count);

        Rgb darkPaper = Rgb.From(PdfPalette.Dark.Paper);
        Rgb lightInk  = Rgb.From(PdfPalette.Light.Ink);

        foreach ((bool isDark, bool isCompany, byte[] bytes) in results)
        {
            if (!isDark)
            {
                Assert.Equal(isCompany ? PdfLightFingerprintTests.CompanyLight : PdfLightFingerprintTests.PersonaLight,
                             PdfContentFingerprint.Of(bytes));
                continue;
            }

            Report pdf = Inspect(bytes, darkPaper);
            Assert.Equal(pdf.Pages, pdf.FirstFills.Count);
            Assert.All(pdf.FirstFills, fill => Assert.Equal(darkPaper, fill.Color));
            Assert.DoesNotContain(pdf.Text, run => run.Color == lightInk);
        }

        // Nothing leaked into the test's own flow.
        Assert.Same(PdfPalette.Light, PdfRenderScope.Current);
    }
}
