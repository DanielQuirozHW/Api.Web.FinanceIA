// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-08

using Api.Web.FinanceIA.Servicios.Pdf;
using iText.Kernel.Colors;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios.PdfBranding;

/// <summary>
/// The co-branded slot's geometry (logo fit, name fit, the limit before the title block) and the client-accent
/// palette — the pure parts, without rendering.
/// </summary>
public sealed class ClientPdfBrandGeometryTests
{
    // A rough width model: every glyph is 0.6 em.
    private static float Measure(string text, float size) => text.Length * size * 0.6f;

    private const float Left   = 48.2f;
    private const float Middle = 790f;
    private const float Limit  = 410f;

    [Theory]
    [InlineData(2000, 100)]    // very wide (20:1)
    [InlineData(500, 500)]     // square
    [InlineData(100, 400)]     // tall
    [InlineData(480, 120)]     // the recommended 4:1
    [InlineData(16, 16)]       // tiny: scaled up to the box, still undistorted
    public void Logo_FitsTheBox_AndKeepsItsRatio(int width, int height)
    {
        foreach (bool onPlate in new[] { false, true })
        {
            ClientPdfBrand.SlotLayout layout = ClientPdfBrand.Layout(width, height, null, Left, Middle, Limit, onPlate, Measure);

            float w = layout.Logo.GetWidth();
            float h = layout.Logo.GetHeight();

            // On the dark plate the plate (logo + padding) is what must fit the 150 pt.
            float maxWidth = ClientPdfBrand.LogoBoxMaxWidth - (onPlate ? 2f * ClientPdfBrand.PlatePadding : 0f);

            Assert.True(h <= ClientPdfBrand.LogoBoxHeight + 0.01f, $"height {h}");
            Assert.True(w <= maxWidth + 0.01f, $"width {w}");
            Assert.InRange(w / h, (width / (float)height) * 0.995f, (width / (float)height) * 1.005f);
            Assert.True(w >= maxWidth - 0.01f || h >= ClientPdfBrand.LogoBoxHeight - 0.01f,
                "the logo touches the box on one side at least");
            Assert.InRange(layout.Logo.GetY() + (h / 2f), Middle - 0.01f, Middle + 0.01f);   // vertically centred
        }
    }

    [Fact]
    public void WideLogo_TakesTheFullWidth_SquareLogo_TheFullHeight()
    {
        ClientPdfBrand.SlotLayout wide   = ClientPdfBrand.Layout(2000, 100, null, Left, Middle, Limit, false, Measure);
        ClientPdfBrand.SlotLayout square = ClientPdfBrand.Layout(500, 500, null, Left, Middle, Limit, false, Measure);

        Assert.Equal(150f, wide.Logo.GetWidth(), 2);
        Assert.Equal(7.5f, wide.Logo.GetHeight(), 2);
        Assert.Equal(28f, square.Logo.GetWidth(), 2);
        Assert.Equal(28f, square.Logo.GetHeight(), 2);
        Assert.Equal(Left, wide.Logo.GetX(), 2);
    }

    [Fact]
    public void LightTheme_HasNoPlate_DarkTheme_PlateWrapsTheLogo()
    {
        ClientPdfBrand.SlotLayout light = ClientPdfBrand.Layout(480, 120, null, Left, Middle, Limit, false, Measure);
        ClientPdfBrand.SlotLayout dark  = ClientPdfBrand.Layout(480, 120, null, Left, Middle, Limit, true, Measure);

        Assert.Null(light.Plate);
        Assert.NotNull(dark.Plate);
        Assert.Equal(Left, dark.Plate!.GetX(), 2);
        Assert.Equal(light.Logo.GetHeight(), dark.Logo.GetHeight(), 2);   // same logo size in both themes
        Assert.Equal(dark.Logo.GetX() - ClientPdfBrand.PlatePadding, dark.Plate!.GetX(), 2);
        Assert.Equal(dark.Logo.GetWidth() + (2 * ClientPdfBrand.PlatePadding), dark.Plate!.GetWidth(), 2);
        Assert.Equal(dark.Logo.GetHeight() + (2 * ClientPdfBrand.PlatePadding), dark.Plate!.GetHeight(), 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompanyName_StartsAfterTheLogo_AndEndsBeforeTheLimit(bool onPlate)
    {
        ClientPdfBrand.SlotLayout layout = ClientPdfBrand.Layout(480, 120, "SGR Ejemplo", Left, Middle, Limit, onPlate, Measure);

        float logoRight = (layout.Plate ?? layout.Logo).GetRight();
        Assert.Equal("SGR Ejemplo", layout.NameText);
        Assert.Equal(ClientPdfBrand.NameMaxSize, layout.NameSize);
        Assert.Equal(logoRight + ClientPdfBrand.NameGap, layout.NameX, 2);
        Assert.True(layout.NameX + Measure(layout.NameText!, layout.NameSize) <= Limit);
    }

    [Fact]
    public void LongCompanyName_ShrinksThenEllipsises_NeverCrossingTheLimit()
    {
        string name = "Sociedad de Garantía Recíproca del Ejemplo Sintético Muy Largo";

        ClientPdfBrand.SlotLayout layout = ClientPdfBrand.Layout(480, 120, name, Left, Middle, Limit, false, Measure);

        Assert.NotNull(layout.NameText);
        Assert.EndsWith("…", layout.NameText, StringComparison.Ordinal);
        Assert.Equal(ClientPdfBrand.NameMinSize, layout.NameSize);
        Assert.True(layout.NameX + Measure(layout.NameText!, layout.NameSize) <= Limit);
    }

    [Fact]
    public void BlankCompanyName_DrawsNoName()
    {
        Assert.Null(ClientPdfBrand.Layout(480, 120, "   ", Left, Middle, Limit, false, Measure).NameText);
        Assert.Null(ClientPdfBrand.Layout(480, 120, null, Left, Middle, Limit, false, Measure).NameText);
    }

    [Fact]
    public void BrandRightLimit_LeavesTheGap_BeforeTheWiderOfTitleAndDate()
    {
        Assert.Equal(547f - 120f - ClientPdfBrand.TitleGap, ClientPdfBrand.BrandRightLimit(547f, 95f, 120f), 3);
        Assert.Equal(547f - 130f - ClientPdfBrand.TitleGap, ClientPdfBrand.BrandRightLimit(547f, 130f, 110f), 3);
    }

    [Fact]
    public void ClientBrand_WithoutALogo_IsRejected() =>
        Assert.Throws<ArgumentException>(() => new ClientPdfBrand(TenantPdfBranding.None));

    // ── Palette with a client accent ──────────────────────────────────────────

    [Theory]
    [InlineData(PdfTheme.Light)]
    [InlineData(PdfTheme.Dark)]
    public void NoAccent_IsTheThemePaletteItself(PdfTheme theme) =>
        Assert.Same(PdfPalette.For(theme), PdfPalette.For(theme, null));

    [Theory]
    [InlineData(PdfTheme.Light)]
    [InlineData(PdfTheme.Dark)]
    public void AccentSoftAlphas_ReproduceTheShippedTints_WithinOneStep(PdfTheme theme)
    {
        // The shipped tints are the brand manual's rounded values (EAF1FC ≈ its 2A6FDB at 10 % over white, 121E37 =
        // 4F8EF7 at 14 % over navy-950): re-deriving them from the shipped accent (296EDB on light, darkened for
        // 4.5:1) lands within one 8-bit step.
        PdfPalette shipped = PdfPalette.For(theme);
        float[] derived = PdfPalette.For(theme, shipped.Accent).AccentSoft.GetColorValue();
        float[] expected = shipped.AccentSoft.GetColorValue();

        for (int i = 0; i < 3; i++)
            Assert.InRange(Math.Abs((derived[i] - expected[i]) * 255f), 0f, 1.01f);
    }

    [Theory]
    [InlineData(PdfTheme.Light, "#1B4F9C", "#E8EDF5")]
    [InlineData(PdfTheme.Dark, "#FFD400", "#2B2815")]
    public void ClientAccent_ReplacesAccent_AndDerivesAccentSoft_LeavingEveryOtherToken(PdfTheme theme, string accentHex, string expectedSoft)
    {
        Assert.True(PdfColorMath.TryParseHex(accentHex, out DeviceRgb accent));
        PdfPalette baseline = PdfPalette.For(theme);

        PdfPalette palette = PdfPalette.For(theme, accent);

        Assert.Equal(accentHex, PdfColorMath.ToHex(palette.Accent));
        Assert.Equal(expectedSoft, PdfColorMath.ToHex(palette.AccentSoft));
        Assert.Equal(baseline with { Accent = palette.Accent, AccentSoft = palette.AccentSoft }, palette);
    }

    [Fact]
    public void Contrast_MatchesWcag()
    {
        Assert.True(PdfColorMath.TryParseHex("#000000", out DeviceRgb black));
        Assert.True(PdfColorMath.TryParseHex("#FFFFFF", out DeviceRgb white));
        Assert.Equal(21.0, PdfColorMath.Contrast(black, white), 2);
        Assert.Equal(1.0, PdfColorMath.Contrast(white, white), 3);
        Assert.Equal(PdfColorMath.Contrast(black, white), PdfColorMath.Contrast(white, black), 6);
    }
}
