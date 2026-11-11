// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-08

using Api.Web.FinanceIA.Servicios.Pdf;
using iText.Kernel.Colors;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Every token the reports print TEXT in reaches WCAG 4.5:1 against the page and the card surface, on BOTH themes —
/// no exceptions. (The light theme carried four pinned shortfalls until its Accent, CashOutflow, Liability and
/// Orange were darkened to clear it.)
/// </summary>
public sealed class PdfPaletteContrastTests
{
    private const double MinimumTextContrast = 4.5;

    // The tokens that carry text somewhere in either report (labels, figures, states, legends, the score).
    private static readonly (string Name, Func<PdfPalette, DeviceRgb> Token)[] TextTokens =
    [
        ("Ink",          p => p.Ink),
        ("InkSecondary", p => p.InkSecondary),
        ("InkMuted",     p => p.InkMuted),
        ("Accent",       p => p.Accent),
        ("Success",      p => p.Success),
        ("Caution",      p => p.Caution),
        ("Danger",       p => p.Danger),
        ("Gain",         p => p.Gain),
        ("Loss",         p => p.Loss),
        ("CashOutflow",  p => p.CashOutflow),
        ("Equity",       p => p.Equity),
        ("Liability",    p => p.Liability),
        ("Orange",       p => p.Orange),
        ("ScoreFigure",  p => p.ScoreFigure),
    ];

    public static TheoryData<string, string> ThemesAndTokens()
    {
        var data = new TheoryData<string, string>();
        foreach (PdfTheme theme in new[] { PdfTheme.Light, PdfTheme.Dark })
        {
            foreach ((string name, _) in TextTokens)
                data.Add(theme.ToString(), name);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(ThemesAndTokens))]
    public void TextToken_ReachesFourAndAHalfToOne_OnPaperAndSurface(string theme, string token)
    {
        PdfPalette palette = PdfPalette.For(Enum.Parse<PdfTheme>(theme));
        DeviceRgb  colour  = TextTokens.Single(t => t.Name == token).Token(palette);

        double onPaper   = PdfThemeInspector.Contrast(colour, palette.Paper);
        double onSurface = PdfThemeInspector.Contrast(colour, palette.Surface);

        Assert.True(onPaper >= MinimumTextContrast, $"{theme}.{token} on Paper is {onPaper:0.000}:1");
        Assert.True(onSurface >= MinimumTextContrast, $"{theme}.{token} on Surface is {onSurface:0.000}:1");
    }

    // The fix is hue-preserving: each darkened light token keeps the manual colour's hue (and saturation, within
    // the rounding of 8-bit channels) — it only lost lightness.
    [Theory]
    [InlineData("Accent",      0x2A, 0x6F, 0xDB)]
    [InlineData("CashOutflow", 0xC2, 0x56, 0x3A)]
    [InlineData("Liability",   0x6B, 0x76, 0x8A)]
    [InlineData("Orange",      0xD9, 0x6C, 0x1F)]
    public void DarkenedLightTokens_KeepTheManualHue(string token, int r, int g, int b)
    {
        DeviceRgb shipped = TextTokens.Single(t => t.Name == token).Token(PdfPalette.Light);
        (double hManual, double sManual, double lManual) = Hsl(r, g, b);
        float[] v = shipped.GetColorValue();
        (double h, double s, double l) = Hsl((int)Math.Round(v[0] * 255), (int)Math.Round(v[1] * 255), (int)Math.Round(v[2] * 255));

        Assert.InRange(Math.Abs(h - hManual), 0, 1.5);   // degrees
        Assert.InRange(Math.Abs(s - sManual), 0, 0.02);
        Assert.True(l < lManual, $"{token} should be darker than the manual's value");
    }

    private static (double H, double S, double L) Hsl(int r, int g, int b)
    {
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
        double max = Math.Max(rf, Math.Max(gf, bf)), min = Math.Min(rf, Math.Min(gf, bf));
        double l = (max + min) / 2, d = max - min;
        if (d == 0) return (0, 0, l);
        double s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h = max == rf ? ((gf - bf) / d) + (gf < bf ? 6 : 0) : max == gf ? ((bf - rf) / d) + 2 : ((rf - gf) / d) + 4;
        return (h * 60, s, l);
    }

    [Fact]
    public void ScoreFigure_OnLight_IsTheInkTheNumeralHasAlwaysUsed_AndAmberOnDark()
    {
        Assert.Equal(PdfThemeInspector.Rgb.From(PdfPalette.Light.Ink), PdfThemeInspector.Rgb.From(PdfPalette.Light.ScoreFigure));
        Assert.Equal(new PdfThemeInspector.Rgb(0xEB, 0xA9, 0x41), PdfThemeInspector.Rgb.From(PdfPalette.Dark.ScoreFigure));
    }

    // Caution is amber in both themes: a dark amber on light, a light amber on dark.
    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Caution_IsAmber_DarkOnLight_LightOnDark(string theme)
    {
        PdfTheme parsed = Enum.Parse<PdfTheme>(theme);
        float[] v = PdfPalette.For(parsed).Caution.GetColorValue();
        (double h, double s, double l) = Hsl((int)Math.Round(v[0] * 255), (int)Math.Round(v[1] * 255), (int)Math.Round(v[2] * 255));

        Assert.InRange(h, 30, 50);   // degrees: amber, between orange and yellow
        Assert.True(s >= 0.6, $"{theme}.Caution saturation is {s:0.00}");
        if (parsed == PdfTheme.Light)
            Assert.True(l < 0.4, $"Light.Caution lightness is {l:0.00}");
        else
            Assert.True(l > 0.6, $"Dark.Caution lightness is {l:0.00}");
    }

    // The dark theme's score figure keeps its own amber; the caution amber must read as a different colour.
    [Fact]
    public void DarkCaution_IsVisiblyApartFromTheScoreAmber()
    {
        double deltaE = DeltaE76(PdfPalette.Dark.Caution, PdfPalette.Dark.ScoreFigure);
        Assert.True(deltaE >= 15, $"ΔE76 between Dark.Caution and Dark.ScoreFigure is {deltaE:0.0}");
    }

    private static double DeltaE76(DeviceRgb a, DeviceRgb b)
    {
        (double L, double A, double B) la = Lab(a), lb = Lab(b);
        return Math.Sqrt(Math.Pow(la.L - lb.L, 2) + Math.Pow(la.A - lb.A, 2) + Math.Pow(la.B - lb.B, 2));
    }

    // sRGB (D65) → CIE L*a*b*.
    private static (double L, double A, double B) Lab(DeviceRgb colour)
    {
        static double Linear(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : (7.787 * t) + (16.0 / 116.0);

        float[] v = colour.GetColorValue();
        double r = Linear(v[0]), g = Linear(v[1]), b = Linear(v[2]);
        double x = ((0.4124 * r) + (0.3576 * g) + (0.1805 * b)) / 0.95047;
        double y = (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
        double z = ((0.0193 * r) + (0.1192 * g) + (0.9505 * b)) / 1.08883;
        return ((116 * F(y)) - 16, 500 * (F(x) - F(y)), 200 * (F(y) - F(z)));
    }

    [Fact]
    public void For_ReturnsTheSharedInstances()
    {
        Assert.Same(PdfPalette.Light, PdfPalette.For(PdfTheme.Light));
        Assert.Same(PdfPalette.Dark, PdfPalette.For(PdfTheme.Dark));
        Assert.Same(PdfPalette.Light, PdfPalette.For((PdfTheme)42));
    }

    [Fact]
    public void RenderScope_IsLightOutsideARender_AndRestoresWhatWasCurrent()
    {
        Assert.Same(PdfPalette.Light, PdfRenderScope.Current);

        using (PdfRenderScope.Enter(PdfPalette.Dark))
        {
            Assert.Same(PdfPalette.Dark, PdfRenderScope.Current);
            using (PdfRenderScope.Enter(PdfPalette.Light))
                Assert.Same(PdfPalette.Light, PdfRenderScope.Current);
            Assert.Same(PdfPalette.Dark, PdfRenderScope.Current);
        }

        Assert.Same(PdfPalette.Light, PdfRenderScope.Current);
    }
}
