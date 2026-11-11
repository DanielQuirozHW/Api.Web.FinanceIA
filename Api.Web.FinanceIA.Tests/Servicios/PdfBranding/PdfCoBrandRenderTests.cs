// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-08

using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.Pdf;
using iText.Kernel.Colors;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios.PdfBranding;

/// <summary>
/// Both reports, both themes, with and without a client logo. Without one, the export draws exactly what the
/// Financia header draws (the light pins of <see cref="PdfLightFingerprintTests"/> and the dark pins below). With one,
/// page 1's brand slot carries the logo and the name, inside the band, centred on the band's middle and clear of the
/// title/date block, while the footer keeps "Elaborado con Financia" (text only, as without a logo).
/// </summary>
public sealed class PdfCoBrandRenderTests
{
    // Synthetic company / persona report, every section, dark theme, clock at PdfContentFingerprint.FixedUtc.
    // First taken on 6a55959, before the brand slot existed: a logo-less export must never move them because of
    // branding. Re-pinned by the PDF polish: the company pin through the attention points, the BCRA trend line and
    // the footer mark; the persona pin through the footer mark alone (the dark palette did not change). The company
    // pin moved again with the catalogue findings ("Hallazgos de catálogo") and the fixture's catalogue alerts. Both
    // moved with the header lockup fix: the FLogo lockup on page 1, the closed ring, the footer without the mark.
    // Re-pinned by the November PDF review: the company pin through Q47 (catalogue figure on page 1), Q48 (cash-flow
    // composition), Q49 (balance age), Q52 (identity faces, strip widths), Q53+Q56 (one ratios table, section order,
    // anatomy block), Q54 (headings, minus sign, CASFOG columns), Q57 (composition / verdict blocks), Q55 (page count)
    // and the fixture's placeholder CUIT; the persona pin through Q55 alone (the shared footer). Re-pinned both by the
    // final PDF touches: the company pin through the cash-flow chart as its own block, the table headers aligned like
    // their columns and the amber caution colour; the persona pin through the headers and the amber. The company pin
    // moved with the pagination polish (LUFE summary box kept whole, LUFE row breaks, status labels on their row's
    // baseline); the persona pin did not.
    internal const string CompanyDark = "971D76E8F11269131C0F254EF596D6AFC4F55C940F8AD07CF7193B23738BD2D8";
    internal const string PersonaDark = "8D2D59699F6141A7EA982166D564C6D0C002C70FC0A853EC63DE6E1227FA471A";

    private const float BandHeight = 20f * 72f / 25.4f;
    private const float SideMargin = 17f * 72f / 25.4f;
    private const string ClientName = "SGR Ejemplo";

    // ── Rendering helpers ─────────────────────────────────────────────────────

    internal static Task<byte[]> RenderCompanyAsync(PdfTheme theme, TenantPdfBranding branding) =>
        PdfContentFingerprint.FixedClockService().GenerateAsync(
            PdfSyntheticReport.Result(), PdfSyntheticReport.Analysis(), "tenant-synthetic",
            Array.Empty<string>(), CancellationToken.None,
            PdfSyntheticReport.Report() with { Branding = branding }, theme);

    internal static Task<byte[]> RenderPersonaAsync(PdfTheme theme, TenantPdfBranding branding) =>
        PdfContentFingerprint.FixedClockService().GeneratePersonAsync(
            PersonPdfFixtures.Report(PersonPdfFixtures.Approved(), PersonPdfFixtures.Evaluate(PersonPdfFixtures.ResponsableInscripto()))
                with { Branding = branding },
            [], CancellationToken.None, theme);

    private static Task<byte[]> RenderAsync(bool persona, PdfTheme theme, TenantPdfBranding branding) =>
        persona ? RenderPersonaAsync(theme, branding) : RenderCompanyAsync(theme, branding);

    // ── Without a logo: unchanged ─────────────────────────────────────────────

    [Theory]
    [InlineData(false, PdfTheme.Light, PdfLightFingerprintTests.CompanyLight)]
    [InlineData(true, PdfTheme.Light, PdfLightFingerprintTests.PersonaLight)]
    [InlineData(false, PdfTheme.Dark, CompanyDark)]
    [InlineData(true, PdfTheme.Dark, PersonaDark)]
    public async Task NoLogo_DrawsExactlyWhatItDrewBefore(bool persona, PdfTheme theme, string pinned)
    {
        // The synthetic reports already carry a TenantDisplayName ("SGR Sintética"): a company name alone is not
        // a co-brand, so this is also the "company_name set, no logo" case.
        string fingerprint = PdfContentFingerprint.Of(await RenderAsync(persona, theme, TenantPdfBranding.None));

        Assert.Equal(pinned, fingerprint);
    }

    [Theory]
    [InlineData(false, PdfTheme.Light)]
    [InlineData(true, PdfTheme.Light)]
    [InlineData(false, PdfTheme.Dark)]
    [InlineData(true, PdfTheme.Dark)]
    public async Task NoLogo_HasNoImage_AndTheFinanciaWordmarkInTheBand(bool persona, PdfTheme theme)
    {
        Page1 page = Page1.Read(await RenderAsync(persona, theme, TenantPdfBranding.None));

        Assert.Empty(page.Images);
        Assert.Contains("Financia", page.BandText(left: true), StringComparison.Ordinal);
    }

    // ── With a logo ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false, PdfTheme.Light)]
    [InlineData(true, PdfTheme.Light)]
    [InlineData(false, PdfTheme.Dark)]
    [InlineData(true, PdfTheme.Dark)]
    public async Task Logo_TakesTheBrandSlot_InsideTheBand_ClearOfTheTitleAndDate(bool persona, PdfTheme theme)
    {
        byte[] pdf = await RenderAsync(persona, theme, BrandingFixtures.CoBranded(ClientName));
        Page1 page = Page1.Read(pdf);

        Rectangle image = Assert.Single(page.Images);
        float bandBottom = page.Size.GetTop() - BandHeight;

        // Inside the band, under the 6 pt accent rule, starting at the side margin (plus the plate's padding on dark).
        Assert.True(image.GetBottom() > bandBottom, $"image bottom {image.GetBottom()} under the band {bandBottom}");
        Assert.True(image.GetTop() < page.Size.GetTop() - 6f, "image over the accent rule");
        float expectedLeft = SideMargin + (theme == PdfTheme.Dark ? ClientPdfBrand.PlatePadding : 0f);
        Assert.Equal(expectedLeft, image.GetLeft(), 1);

        // Still centred on the band's middle: the Financia lockup's realignment does not move the co-brand slot.
        float middle = page.Size.GetTop() - 6f - ((BandHeight - 6f) / 2f);
        Assert.Equal(middle, (image.GetBottom() + image.GetTop()) / 2f, 1);

        // The 480×120 placeholder keeps its 4:1 ratio.
        Assert.InRange(image.GetWidth() / image.GetHeight(), 3.98f, 4.02f);

        // The name sits beside the logo, and the whole slot ends before the right-aligned title/date block starts.
        string brandText = page.BandText(left: true);
        Assert.Contains(ClientName.Replace(" ", ""), brandText.Replace(" ", ""), StringComparison.Ordinal);
        Assert.DoesNotContain("Financia", brandText, StringComparison.Ordinal);

        float titleStart = page.BandRuns(left: false).Min(r => r.Left);
        float slotEnd    = Math.Max(image.GetRight(), page.BandRuns(left: true).Max(r => r.Right));
        Assert.True(slotEnd < titleStart, $"brand slot ends at {slotEnd}, title block starts at {titleStart}");
        Assert.Contains(persona ? "crediticia" : "crediticio", page.BandText(left: false), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, PdfTheme.Light)]
    [InlineData(true, PdfTheme.Dark)]
    public async Task Logo_LeavesTheFooter_ElaboradoConFinancia_TextOnly(bool persona, PdfTheme theme)
    {
        byte[] pdf = await RenderAsync(persona, theme, BrandingFixtures.CoBranded(ClientName, accent: Accent(theme)));
        PdfPalette palette = PdfPalette.For(theme);

        Page1 page = Page1.Read(pdf);
        Run footer = Assert.Single(page.Runs, r => r.Baseline < 40f && r.Left < page.Size.GetWidth() / 2f);
        Assert.StartsWith("Elaborado con Financia · Confidencial · ID ", footer.Text, StringComparison.Ordinal);

        // No shape at all in the footer band — no Financia symbol, and nothing in the client accent.
        PdfThemeInspector.Report report = PdfThemeInspector.Inspect(pdf, PdfThemeInspector.Rgb.From(palette.Paper));
        Assert.DoesNotContain(report.Paths, p => p.Page == 1 && p.Bounds.GetTop() < 40f);
    }

    [Fact]
    public async Task Dark_PutsTheLogoOnAWhitePlate_Light_DoesNot()
    {
        foreach (PdfTheme theme in new[] { PdfTheme.Light, PdfTheme.Dark })
        {
            byte[] pdf = await RenderCompanyAsync(theme, BrandingFixtures.CoBranded(ClientName));
            Rectangle image = Assert.Single(Page1.Read(pdf).Images);

            PdfThemeInspector.Report report = PdfThemeInspector.Inspect(pdf, PdfThemeInspector.Rgb.From(PdfPalette.For(theme).Paper));
            bool plate = report.Paths.Any(p => p.Page == 1 && p.Fills
                && p.Fill == new PdfThemeInspector.Rgb(0xFF, 0xFF, 0xFF)
                && p.Bounds.GetLeft() <= image.GetLeft() && p.Bounds.GetRight() >= image.GetRight()
                && p.Bounds.GetBottom() <= image.GetBottom() && p.Bounds.GetTop() >= image.GetTop()
                && p.Bounds.GetWidth() < 200f);

            Assert.Equal(theme == PdfTheme.Dark, plate);
        }
    }

    [Theory]
    [InlineData(2000, 100)]
    [InlineData(500, 500)]
    public async Task WideAndSquareLogos_AreFittedWithoutDistortion(int width, int height)
    {
        byte[] pdf = await RenderCompanyAsync(PdfTheme.Light, BrandingFixtures.CoBrandedSolid(width, height, ClientName));
        Rectangle image = Assert.Single(Page1.Read(pdf).Images);

        (float w, float h) = ClientPdfBrand.FitLogo(width, height, ClientPdfBrand.LogoBoxMaxWidth, ClientPdfBrand.LogoBoxHeight);
        Assert.Equal(w, image.GetWidth(), 1);
        Assert.Equal(h, image.GetHeight(), 1);
        Assert.InRange(image.GetWidth() / image.GetHeight(), (width / (float)height) * 0.995f, (width / (float)height) * 1.005f);
    }

    [Fact]
    public async Task JpegLogo_RendersToo()
    {
        var branding = new TenantPdfBranding(
            new PdfBrandLogo(BrandingFixtures.Jpeg40x20, 40, 20, PdfLogoFormat.Jpeg), ClientName, null);

        Rectangle image = Assert.Single(Page1.Read(await RenderPersonaAsync(PdfTheme.Light, branding)).Images);
        Assert.InRange(image.GetWidth() / image.GetHeight(), 1.99f, 2.01f);
    }

    [Theory]
    [InlineData(PdfTheme.Light)]
    [InlineData(PdfTheme.Dark)]
    public async Task ClientAccent_PaintsTheTopRule(PdfTheme theme)
    {
        DeviceRgb accent = Accent(theme);
        byte[] pdf = await RenderCompanyAsync(theme, BrandingFixtures.CoBranded(ClientName, accent));

        PdfThemeInspector.Report report = PdfThemeInspector.Inspect(pdf, PdfThemeInspector.Rgb.From(PdfPalette.For(theme).Paper));
        float top = report.PageSize.GetTop();
        var rule = Assert.Single(report.Paths, p => p.Page == 1 && p.Fills
            && Math.Abs(p.Bounds.GetTop() - top) < 0.5f && Math.Abs(p.Bounds.GetHeight() - 6f) < 0.5f
            && p.Bounds.GetWidth() >= report.PageSize.GetWidth() - 0.5f);

        Assert.Equal(PdfThemeInspector.Rgb.From(accent), rule.Fill);
        Assert.Contains(report.Paths, p => p.Fills && p.Fill == PdfThemeInspector.Rgb.From(PdfPalette.For(theme, accent).Accent));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Logo_KeepsTheDocumentInformation(bool persona)
    {
        byte[] pdf = await RenderAsync(persona, PdfTheme.Light, BrandingFixtures.CoBranded(ClientName));

        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        PdfDocumentInfo info = doc.GetDocumentInfo();

        Assert.Equal("Financia", info.GetCreator());
        Assert.NotEqual(ClientName, info.GetAuthor());   // Author stays the tenant display name the report carries
        Assert.Contains("iText", info.GetProducer() ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Logo_OnlyChangesPageOne()
    {
        // Pages 2+ have no brand slot: their content is the same with and without a logo.
        byte[] plain = await RenderCompanyAsync(PdfTheme.Light, TenantPdfBranding.None);
        byte[] cobrand = await RenderCompanyAsync(PdfTheme.Light, BrandingFixtures.CoBranded(ClientName));

        using var a = new PdfDocument(new PdfReader(new MemoryStream(plain)));
        using var b = new PdfDocument(new PdfReader(new MemoryStream(cobrand)));
        Assert.Equal(a.GetNumberOfPages(), b.GetNumberOfPages());
        for (int i = 2; i <= a.GetNumberOfPages(); i++)
            Assert.Equal(a.GetPage(i).GetContentBytes(), b.GetPage(i).GetContentBytes());
    }

    private static DeviceRgb Accent(PdfTheme theme)
    {
        Assert.True(PdfColorMath.TryParseHex(theme == PdfTheme.Dark ? "#FFD400" : "#1B4F9C", out DeviceRgb accent));
        return accent;
    }

    // ── Page-1 reader: images (from their CTM) and text runs with their extents ──

    private sealed record Run(string Text, float Left, float Right, float Baseline);

    private sealed class Page1 : IEventListener
    {
        private Page1(Rectangle size) => Size = size;

        public Rectangle Size { get; }

        public List<Rectangle> Images { get; } = [];

        public List<Run> Runs { get; } = [];

        public static Page1 Read(byte[] pdf)
        {
            using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
            var page = new Page1(doc.GetPage(1).GetPageSize());
            new PdfCanvasProcessor(page).ProcessPageContent(doc.GetPage(1));
            return page;
        }

        public IEnumerable<Run> BandRuns(bool left) => Runs.Where(r =>
            r.Baseline > Size.GetTop() - BandHeight && (left ? r.Left < Size.GetWidth() / 2f : r.Left >= Size.GetWidth() / 2f));

        public string BandText(bool left) => string.Concat(BandRuns(left).Select(r => r.Text));

        public void EventOccurred(IEventData data, EventType type)
        {
            switch (data)
            {
                case ImageRenderInfo image when type == EventType.RENDER_IMAGE:
                    Matrix m = image.GetImageCtm();
                    Images.Add(new Rectangle(m.Get(Matrix.I31), m.Get(Matrix.I32), m.Get(Matrix.I11), m.Get(Matrix.I22)));
                    break;
                case TextRenderInfo text when type == EventType.RENDER_TEXT:
                    LineSegment baseline = text.GetBaseline();
                    Runs.Add(new Run(
                        text.GetText(),
                        baseline.GetStartPoint().Get(Vector.I1),
                        baseline.GetEndPoint().Get(Vector.I1),
                        baseline.GetStartPoint().Get(Vector.I2)));
                    break;
            }
        }

        public ICollection<EventType>? GetSupportedEvents() => null;
    }
}
