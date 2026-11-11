// Project : Api.Web.FinanceIA
// Created : 2026-11-08

using System.Globalization;
using iText.Kernel.Colors;

namespace Api.Web.FinanceIA.Servicios.Pdf;

/// <summary>
/// The colour arithmetic the export needs at run time: parsing a tenant's <c>#RRGGBB</c>, pre-blending a tint
/// over a known solid background (print has no transparency) and the WCAG 2.x contrast ratio that decides
/// whether a client accent is legible on a theme's paper and surface.
/// </summary>
internal static class PdfColorMath
{
    /// <summary>
    /// Parses exactly <c>#RRGGBB</c> (hex digits in either case, surrounding spaces ignored). Anything else — a
    /// missing '#', three digits, an alpha channel, a colour name — is false.
    /// </summary>
    internal static bool TryParseHex(string? raw, out DeviceRgb color)
    {
        color = null!;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        string value = raw.Trim();
        if (value.Length != 7 || value[0] != '#')
            return false;

        if (!int.TryParse(value.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int rgb))
            return false;

        color = new DeviceRgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        return true;
    }

    /// <summary>
    /// <paramref name="foreground"/> at <paramref name="alpha"/> over a solid <paramref name="background"/>, rounded
    /// to whole 8-bit channels so the result is the same colour a designer would write down.
    /// </summary>
    internal static DeviceRgb Blend(DeviceRgb foreground, DeviceRgb background, double alpha)
    {
        ArgumentNullException.ThrowIfNull(foreground);
        ArgumentNullException.ThrowIfNull(background);

        float[] f = foreground.GetColorValue();
        float[] b = background.GetColorValue();

        int Mix(int i) => (int)Math.Round(((f[i] * alpha) + (b[i] * (1.0 - alpha))) * 255.0, MidpointRounding.AwayFromZero);

        return new DeviceRgb(Mix(0), Mix(1), Mix(2));
    }

    /// <summary>WCAG 2.x relative luminance of an sRGB colour, 0 (black) to 1 (white).</summary>
    internal static double RelativeLuminance(DeviceRgb color)
    {
        ArgumentNullException.ThrowIfNull(color);

        float[] v = color.GetColorValue();

        static double Linear(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

        return (0.2126 * Linear(v[0])) + (0.7152 * Linear(v[1])) + (0.0722 * Linear(v[2]));
    }

    /// <summary>WCAG 2.x contrast ratio between two colours, 1 to 21, symmetric.</summary>
    internal static double Contrast(DeviceRgb a, DeviceRgb b)
    {
        double la = RelativeLuminance(a);
        double lb = RelativeLuminance(b);
        double lighter = Math.Max(la, lb);
        double darker  = Math.Min(la, lb);
        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary><c>#RRGGBB</c> of a colour, for log lines.</summary>
    internal static string ToHex(DeviceRgb color)
    {
        float[] v = color.GetColorValue();
        int C(int i) => (int)Math.Round(v[i] * 255.0);
        return string.Create(CultureInfo.InvariantCulture, $"#{C(0):X2}{C(1):X2}{C(2):X2}");
    }
}
