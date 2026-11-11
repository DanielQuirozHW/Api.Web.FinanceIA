// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-09

using Api.Web.FinanceIA.Servicios.Pdf;
using iText.IO.Font;
using iText.Kernel.Geom;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Page 1's Financia lockup, measured from the drawn paths and text of both reports in both themes, against the
/// Portal's FLogo rule (Estratos v1.3): the symbol's ink is centred on the wordmark's cap-height midline, half a cap
/// height from the wordmark's first stroke, its left edge on the content margin, and that midline is on the centre
/// of the right-hand title/date block. The ring is a closed path (no seam at 3 o'clock), and the title and date did
/// not move.
/// </summary>
public sealed class PdfHeaderLockupTests
{
    private const float SideMargin = 17f * 72f / 25.4f;
    private const float BandHeight = 20f * 72f / 25.4f;

    // Where the title and date baselines sat before this change (pt from the page top) — they must not move.
    private const float TitleBaselineFromTop = 26.49f;
    private const float DateBaselineFromTop  = 39.37f;

    private static readonly FontProgram SoraBold = PdfPageGeometry.BrandFont("Sora-Bold.ttf");

    private sealed record Lockup(
        Rectangle Ink, IReadOnlyList<PdfPageGeometry.PathGeo> SymbolPaths,
        PdfPageGeometry.Run Wordmark, PdfPageGeometry.Run Title, PdfPageGeometry.Run Date, Rectangle Page);

    private static async Task<Lockup> ReadAsync(string report, string themeName)
    {
        byte[] bytes = await PdfFooterTests.RenderAsync(report, Enum.Parse<PdfTheme>(themeName));
        PdfPageGeometry.Page page = PdfPageGeometry.Read(bytes, 1);

        float top       = page.Size.GetTop();
        float bandFloor = top - BandHeight;
        float middle    = page.Size.GetWidth() / 2f;

        // The symbol: every shape inside the band (not the full-bleed accent rule or hairline) in its left half.
        var symbol = page.Paths
            .Where(p => p.Ink.GetBottom() > bandFloor && p.Ink.GetTop() < top - 6f
                        && p.Ink.GetLeft() > 0f && p.Ink.GetRight() < middle)
            .ToList();
        Assert.NotEmpty(symbol);

        var band = page.Runs.Where(r => r.Baseline > bandFloor).ToList();
        PdfPageGeometry.Run wordmark = Assert.Single(band, r => r.Left < middle);
        var right = band.Where(r => r.Left >= middle).OrderByDescending(r => r.Baseline).ToList();
        Assert.Equal(2, right.Count);

        return new Lockup(PdfPageGeometry.Union(symbol), symbol, wordmark, right[0], right[1], page.Size);
    }

    [Theory]
    [MemberData(nameof(PdfFooterTests.Reports), MemberType = typeof(PdfFooterTests))]
    public async Task SymbolInk_IsCentredOnTheWordmarksCapMidline(string report, string themeName)
    {
        Lockup l = await ReadAsync(report, themeName);
        Assert.Equal("Financia", l.Wordmark.Text);

        float capMidline = l.Wordmark.Baseline + (PdfPageGeometry.CapHeight(SoraBold, l.Wordmark.FontSize) / 2f);
        float inkCentre  = (l.Ink.GetBottom() + l.Ink.GetTop()) / 2f;

        Assert.InRange(inkCentre, capMidline - 0.25f, capMidline + 0.25f);
    }

    [Theory]
    [MemberData(nameof(PdfFooterTests.Reports), MemberType = typeof(PdfFooterTests))]
    public async Task VisibleGap_IsHalfACapHeight(string report, string themeName)
    {
        Lockup l = await ReadAsync(report, themeName);

        float cap       = PdfPageGeometry.CapHeight(SoraBold, l.Wordmark.FontSize);
        float firstInk  = l.Wordmark.Left + PdfPageGeometry.LeftBearing(SoraBold, l.Wordmark.Text[0], l.Wordmark.FontSize);
        float gap       = firstInk - l.Ink.GetRight();

        Assert.InRange(gap, (0.5f * cap) - 0.3f, (0.5f * cap) + 0.3f);
    }

    [Theory]
    [MemberData(nameof(PdfFooterTests.Reports), MemberType = typeof(PdfFooterTests))]
    public async Task SymbolInk_StartsOnTheContentMargin(string report, string themeName)
    {
        Lockup l = await ReadAsync(report, themeName);

        Assert.InRange(l.Ink.GetLeft(), SideMargin - 0.25f, SideMargin + 0.25f);
    }

    [Theory]
    [MemberData(nameof(PdfFooterTests.Reports), MemberType = typeof(PdfFooterTests))]
    public async Task Lockup_IsCentredOnTheTitleAndDateBlock_WhichDidNotMove(string report, string themeName)
    {
        Lockup l = await ReadAsync(report, themeName);
        float top = l.Page.GetTop();

        Assert.Equal(top - TitleBaselineFromTop, l.Title.Baseline, 0.05f);
        Assert.Equal(top - DateBaselineFromTop, l.Date.Baseline, 0.05f);
        Assert.Equal(report == "persona" ? "Evaluación crediticia" : "Informe crediticio", l.Title.Text);
        Assert.Contains(" de 2026 · ", l.Date.Text);

        // Title cap top → date baseline.
        float blockCentre = (l.Title.Baseline + PdfPageGeometry.CapHeight(SoraBold, l.Title.FontSize) + l.Date.Baseline) / 2f;
        float capMidline  = l.Wordmark.Baseline + (PdfPageGeometry.CapHeight(SoraBold, l.Wordmark.FontSize) / 2f);

        Assert.InRange(capMidline, blockCentre - 1f, blockCentre + 1f);
    }

    [Theory]
    [MemberData(nameof(PdfFooterTests.Reports), MemberType = typeof(PdfFooterTests))]
    public async Task Ring_IsAClosedPath(string report, string themeName)
    {
        Lockup l = await ReadAsync(report, themeName);

        PdfPageGeometry.PathGeo ring = Assert.Single(l.SymbolPaths, p => p.Strokes);
        Assert.True(ring.IsClosed, "the ring is stroked as an open path: its butt caps leave a notch at 3 o'clock");
    }

    // ── The geometry itself (no PDF) ──────────────────────────────────────────

    [Fact]
    public void Layout_PutsTheInkOnTheMarginAndMidline_HalfACapFromTheFirstStroke()
    {
        Rectangle ink = FinanciaPdfBrand.Instance.SymbolInk(26f);
        const float Left = 48f, Midline = 800f, Cap = 11.68f, Bearing = 1.312f;

        HeaderLockup.Placement p = HeaderLockup.Layout(Left, Midline, ink, Cap, Bearing);

        Assert.Equal(Left, p.SymbolX + ink.GetLeft(), 3);                                     // ink left on the margin
        Assert.Equal(Midline, p.SymbolY + ink.GetBottom() + (ink.GetHeight() / 2f), 3);       // ink centre on the midline
        Assert.Equal(Midline, p.WordmarkBaseline + (Cap / 2f), 3);                            // cap midline on the midline
        Assert.Equal(0.5f * Cap, (p.WordmarkX + Bearing) - (p.SymbolX + ink.GetRight()), 3); // the visible gap
    }

    [Fact]
    public void BlockCentre_IsHalfwayFromTheFirstCapTopToTheLastBaseline() =>
        Assert.Equal(815f, HeaderLockup.BlockCentre(firstBaseline: 820f, firstCapHeight: 5f, lastBaseline: 805f), 3);
}
