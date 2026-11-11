// Project : Api.Web.FinanceIA
// Created : 2026-11-08

using iText.Kernel.Colors;

namespace Api.Web.FinanceIA.Servicios.Pdf;

/// <summary>
/// Every colour the exported reports use, as semantic tokens. The reports read colours ONLY through one
/// of these records, so a theme (or a client's branding) is a new instance, not a change to layout code.
/// Two ship: <see cref="Light"/> (the default, the paper document) and <see cref="Dark"/>, chosen per export
/// with the <c>theme</c> query parameter and made current for that render by <see cref="PdfRenderScope"/>.
/// </summary>
/// <remarks>
/// The values are the Financia brand manual's (Estratos v1.3) light and dark variants. Good, caution and bad each
/// carry a hue: "caution" states (Atención and the caution tone of the component and ratio bars, Media, Con notas,
/// BCRA situación 2–3, the persona report's amber states) read in amber — a dark amber on light, a light amber on
/// dark — on the surface tint. The dark theme's <see cref="ScoreFigure"/> keeps its own amber, visibly apart from
/// <see cref="Caution"/> (a lighter, yellower tone). Every text-bearing token reaches WCAG 4.5:1 against
/// <see cref="Paper"/> and <see cref="Surface"/> (PdfPaletteContrastTests).
/// </remarks>
public sealed record PdfPalette
{
    /// <summary>Body text.</summary>
    public required DeviceRgb Ink { get; init; }

    /// <summary>Secondary text: labels, the caution state.</summary>
    public required DeviceRgb InkSecondary { get; init; }

    /// <summary>Muted text: captions, footers, units.</summary>
    public required DeviceRgb InkMuted { get; init; }

    /// <summary>Hairlines, bar tracks, table rules.</summary>
    public required DeviceRgb Hairline { get; init; }

    /// <summary>Cards, zebra rows, chips.</summary>
    public required DeviceRgb Surface { get; init; }

    /// <summary>The page itself.</summary>
    public required DeviceRgb Paper { get; init; }

    /// <summary>Brand accent (cobalto 600 on light).</summary>
    public required DeviceRgb Accent { get; init; }

    /// <summary>Flat accent tint for neutral callouts.</summary>
    public required DeviceRgb AccentSoft { get; init; }

    /// <summary>Good state text and fills.</summary>
    public required DeviceRgb Success { get; init; }

    /// <summary>Good state background.</summary>
    public required DeviceRgb SuccessSoft { get; init; }

    /// <summary>Caution text and fills: amber — dark on light, light on dark.</summary>
    public required DeviceRgb Caution { get; init; }

    /// <summary>Caution background.</summary>
    public required DeviceRgb CautionSoft { get; init; }

    /// <summary>Bad state text and fills.</summary>
    public required DeviceRgb Danger { get; init; }

    /// <summary>Bad state background.</summary>
    public required DeviceRgb DangerSoft { get; init; }

    /// <summary>A positive variation (figures).</summary>
    public required DeviceRgb Gain { get; init; }

    /// <summary>A negative variation (figures).</summary>
    public required DeviceRgb Loss { get; init; }

    /// <summary>Cash outflow in the waterfall: not "bad", so not the danger red.</summary>
    public required DeviceRgb CashOutflow { get; init; }

    /// <summary>Equity segment of the composition bars (its own hue family).</summary>
    public required DeviceRgb Equity { get; init; }

    /// <summary>Liability segments of the composition bars (neutral slate, no valence).</summary>
    public required DeviceRgb Liability { get; init; }

    /// <summary>The persona report's "alerta fuerte" / Naranja band.</summary>
    public required DeviceRgb Orange { get; init; }

    /// <summary>The big score numeral of the fact-sheet gauge — and nothing else. Amber on the dark theme.</summary>
    public required DeviceRgb ScoreFigure { get; init; }

    /// <summary>The palette of <paramref name="theme"/>; an undefined value reads as <see cref="Light"/>.</summary>
    public static PdfPalette For(PdfTheme theme) => theme == PdfTheme.Dark ? Dark : Light;

    /// <summary>
    /// The palette of <paramref name="theme"/> with a client's <paramref name="accent"/> (branding.accent_color,
    /// already contrast-checked by the provider). Null returns <see cref="For(PdfTheme)"/> itself — the same
    /// instance, so an export without a client accent is exactly what it was. Otherwise Accent is replaced and
    /// AccentSoft re-derived the way the shipped tints are built: the accent at 10 % over white on light (EAF1FC
    /// from the manual's 2A6FDB, and still within one 8-bit step of the shipped 296EDB) and at 14 % over navy-950 on dark
    /// (121E37 from 4F8EF7).
    /// </summary>
    public static PdfPalette For(PdfTheme theme, DeviceRgb? accent)
    {
        PdfPalette palette = For(theme);
        if (accent is null)
            return palette;

        double softAlpha = theme == PdfTheme.Dark ? DarkAccentSoftAlpha : LightAccentSoftAlpha;
        return palette with { Accent = accent, AccentSoft = PdfColorMath.Blend(accent, palette.Paper, softAlpha) };
    }

    internal const double LightAccentSoftAlpha = 0.10;
    internal const double DarkAccentSoftAlpha  = 0.14;

    /// <summary>
    /// The Financia brand manual's light variant. Four text tokens sat just under WCAG 4.5:1 on Paper or Surface
    /// (Accent, CashOutflow, Liability, Orange); each was darkened in HLS lightness only — same hue, same
    /// saturation — to the least step that clears 4.5:1 on both. The manual's originals are noted beside them.
    /// </summary>
    public static PdfPalette Light { get; } = new()
    {
        Ink          = new DeviceRgb(0x0B, 0x12, 0x22),
        InkSecondary = new DeviceRgb(0x3D, 0x46, 0x57),
        InkMuted     = new DeviceRgb(0x5B, 0x64, 0x76),
        Hairline     = new DeviceRgb(0xE3, 0xE8, 0xF0),
        Surface      = new DeviceRgb(0xF5, 0xF7, 0xFB),
        Paper        = new DeviceRgb(0xFF, 0xFF, 0xFF),
        Accent       = new DeviceRgb(0x29, 0x6E, 0xDB),   // cobalto 600 (2A6FDB), one step darker: 4.5:1 on Surface
        AccentSoft   = new DeviceRgb(0xEA, 0xF1, 0xFC),
        Success      = new DeviceRgb(0x14, 0x6B, 0x46),
        SuccessSoft  = new DeviceRgb(0xE3, 0xF4, 0xEC),
        Caution      = new DeviceRgb(0x8F, 0x5B, 0x00),   // dark amber: 5.7:1 on Paper, 5.3:1 on Surface
        CautionSoft  = new DeviceRgb(0xF5, 0xF7, 0xFB),
        Danger       = new DeviceRgb(0xB4, 0x23, 0x18),
        DangerSoft   = new DeviceRgb(0xFC, 0xEB, 0xE9),
        Gain         = new DeviceRgb(0x14, 0x6B, 0x46),
        Loss         = new DeviceRgb(0xB4, 0x23, 0x18),
        CashOutflow  = new DeviceRgb(0xB9, 0x52, 0x37),   // terracotta C2563A, darkened to 4.5:1
        Equity       = new DeviceRgb(0x7B, 0x2F, 0xBE),
        Liability    = new DeviceRgb(0x68, 0x72, 0x86),   // slate 6B768A, darkened to 4.5:1
        Orange       = new DeviceRgb(0xB2, 0x59, 0x19),   // orange D96C1F, darkened to 4.5:1
        ScoreFigure  = new DeviceRgb(0x0B, 0x12, 0x22),   // = Ink: the colour the numeral has always used
    };

    /// <summary>
    /// The Financia brand manual's dark variant. DeviceRgb has no alpha, so every translucent token of the manual
    /// is pre-blended over <see cref="Paper"/> (navy-950 #080C18) — exact, since it is only ever drawn there or on
    /// a surface a few steps away. Equity, Liability, CashOutflow and Orange keep the light theme's hue families,
    /// lifted so a navy label on them and they on navy both clear 4.5:1.
    /// </summary>
    public static PdfPalette Dark { get; } = new()
    {
        Ink          = new DeviceRgb(0xFF, 0xFF, 0xFF),
        InkSecondary = new DeviceRgb(0xBA, 0xBB, 0xBE),   // white 72 % over navy-950
        InkMuted     = new DeviceRgb(0x88, 0x8A, 0x90),   // white 52 % over navy-950
        Hairline     = new DeviceRgb(0x21, 0x24, 0x2F),   // white 10 % over navy-950
        Surface      = new DeviceRgb(0x0D, 0x15, 0x25),   // navy-900
        Paper        = new DeviceRgb(0x08, 0x0C, 0x18),   // navy-950
        Accent       = new DeviceRgb(0x4F, 0x8E, 0xF7),   // cobalto-500
        AccentSoft   = new DeviceRgb(0x12, 0x1E, 0x37),   // cobalto-500 14 % over navy-950
        Success      = new DeviceRgb(0x38, 0xC7, 0x89),   // gain-500
        SuccessSoft  = new DeviceRgb(0x0F, 0x26, 0x28),   // gain-500 14 % over navy-950
        Caution      = new DeviceRgb(0xFC, 0xD3, 0x4D),   // light amber (ambar-300): apart from ScoreFigure's ambar-500
        CautionSoft  = new DeviceRgb(0x0D, 0x15, 0x25),   // = Surface, as on light
        Danger       = new DeviceRgb(0xE5, 0x63, 0x6A),   // loss-500
        DangerSoft   = new DeviceRgb(0x27, 0x18, 0x23),   // loss-500 14 % over navy-950
        Gain         = new DeviceRgb(0x38, 0xC7, 0x89),   // gain-500
        Loss         = new DeviceRgb(0xE5, 0x63, 0x6A),   // loss-500
        CashOutflow  = new DeviceRgb(0xE8, 0x89, 0x6B),   // terracotta, lifted
        Equity       = new DeviceRgb(0xA9, 0x8B, 0xF0),   // violet, lifted
        Liability    = new DeviceRgb(0x8E, 0x99, 0xAD),   // slate, lifted
        Orange       = new DeviceRgb(0xF2, 0x9A, 0x52),   // orange, lifted
        ScoreFigure  = new DeviceRgb(0xEB, 0xA9, 0x41),   // ambar-500 — the score figure only
    };
}
