// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-09

using System.Globalization;
using System.Text.RegularExpressions;
using Api.Web.FinanceIA.Servicios.Pdf;
using iText.Kernel.Geom;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// The running footer of both reports in both themes: "Elaborado con Financia · Confidencial · ID …" as text only —
/// the Financia symbol is NOT drawn there, since at footer size any version of it sits below the brand manual's
/// minimum — and "Página X de Y" on the right as ONE run with one space before the total, ending on the right margin
/// like the header's title (QA Q55). Checked on the first and the last page, for a report under 10 pages and one of 10
/// or more.
/// </summary>
public sealed partial class PdfFooterTests
{
    // Everything drawn under this line is the footer (its baseline is at 32.17 pt).
    private const float FooterBandTop = 45f;

    [GeneratedRegex("^Elaborado con Financia · Confidencial · ID [0-9A-F]{8}$")]
    private static partial Regex FooterLeft();

    // The right margin, and how far a right-aligned run's measured end may sit from it. iText's extraction ends a run
    // 0.28 pt short of the arithmetic margin for the header's title and date too, so the footer is held to the
    // header's own edge (±0.25) and to the arithmetic margin within half a point.
    private static readonly float RightMargin = PageSize.A4.GetWidth() - (17f * 72f / 25.4f);
    private const float SameEdge = 0.25f;

    public static TheoryData<string, string> Reports() => new()
    {
        { "company", nameof(PdfTheme.Light) },
        { "company", nameof(PdfTheme.Dark) },
        { "company-short", nameof(PdfTheme.Light) },
        { "company-short", nameof(PdfTheme.Dark) },
        { "persona", nameof(PdfTheme.Light) },
        { "persona", nameof(PdfTheme.Dark) },
    };

    internal static async Task<byte[]> RenderAsync(string report, PdfTheme theme) => report switch
    {
        "company-short" => await PdfContentFingerprint.FixedClockService().GenerateAsync(
            PdfSyntheticReport.Result(), PdfSyntheticReport.Analysis(), "tenant-synthetic",
            ["executive_summary", "footer"], CancellationToken.None, PdfSyntheticReport.Report(), theme),
        _ => theme == PdfTheme.Light
            ? report == "company" ? await PdfLightFingerprintTests.RenderCompanyAsync() : await PdfLightFingerprintTests.RenderPersonaAsync()
            : report == "company" ? await PdfDarkThemeTests.RenderCompanyAsync() : await PdfDarkThemeTests.RenderPersonaAsync(),
    };

    [Theory]
    [MemberData(nameof(Reports))]
    public async Task Footer_IsTextOnly_ElaboradoConFinancia_AndThePageCountEndsOnTheMargin(string report, string themeName)
    {
        byte[] bytes = await RenderAsync(report, Enum.Parse<PdfTheme>(themeName));
        int pages = PdfPageGeometry.PageCount(bytes);
        Assert.True(pages > 1, "the synthetic report should span several pages");
        if (report == "company")
            Assert.True(pages >= 10, $"the full synthetic report should reach two-digit totals ({pages} pages)");
        if (report == "company-short")
            Assert.True(pages < 10, $"the short synthetic report should stay under 10 pages ({pages} pages)");

        foreach (int pageNumber in new[] { 1, pages })
        {
            PdfPageGeometry.Page page = PdfPageGeometry.Read(bytes, pageNumber);
            float middle = page.Size.GetWidth() / 2f;

            // No symbol, and no other shape, anywhere in the footer band.
            var footerPaths = page.Paths.Where(p => p.Ink.GetTop() < FooterBandTop).ToList();
            Assert.True(footerPaths.Count == 0,
                $"page {pageNumber}: {footerPaths.Count} path(s) painted in the footer band: "
                + string.Join(" ", footerPaths.Select(p => Describe(p.Ink))));

            // Left: ONE run, single spaces, the name where the mark used to be.
            var left = page.Runs.Where(r => r.Baseline < FooterBandTop && r.Left < middle).ToList();
            PdfPageGeometry.Run lead = Assert.Single(left);
            Assert.Matches(FooterLeft(), lead.Text);

            // Right: ONE run, "Página X de Y" with exactly one space before the total, on the left run's baseline.
            PdfPageGeometry.Run count = Assert.Single(page.Runs, r => r.Baseline < FooterBandTop && r.Left >= middle);
            Assert.Equal(
                "Página " + pageNumber.ToString(CultureInfo.InvariantCulture) + " de " + pages.ToString(CultureInfo.InvariantCulture),
                count.Text);
            Assert.Equal(lead.Baseline, count.Baseline, 2);

            // It ends where the header's right-hand run ends — the report title on every page — and on the margin.
            PdfPageGeometry.Run header = page.Runs
                .Where(r => r.Baseline > page.Size.GetHeight() - 60f && r.Left >= middle)
                .OrderByDescending(r => r.Baseline)
                .First();
            Assert.True(Math.Abs(count.Right - header.Right) <= SameEdge,
                $"page {pageNumber}: the page count ends at {count.Right:F2}, the header at {header.Right:F2}");
            Assert.True(Math.Abs(count.Right - RightMargin) <= 2 * SameEdge,
                $"page {pageNumber}: the page count ends at {count.Right:F2}, the margin is {RightMargin:F2}");
        }
    }

    [Theory]
    [InlineData(26f)]
    [InlineData(48f)]
    public void SymbolInk_IsTheInkedPartOfTheSquare(float size)
    {
        Rectangle ink = FinanciaPdfBrand.Instance.SymbolInk(size);
        float s = size / 24f;

        Assert.Equal(3f * s, ink.GetLeft(), 3);
        Assert.Equal(21f * s, ink.GetRight(), 3);
        Assert.Equal(1f * s, ink.GetBottom(), 3);
        Assert.Equal(19f * s, ink.GetTop(), 3);
    }

    private static string Describe(Rectangle r) =>
        $"[{r.GetLeft():F2}, {r.GetBottom():F2} → {r.GetRight():F2}, {r.GetTop():F2}]";
}
