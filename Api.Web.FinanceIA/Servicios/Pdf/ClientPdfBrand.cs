// Project : Api.Web.FinanceIA
// Created : 2026-11-08

using iText.IO.Image;
using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf.Canvas;
using iText.Layout;
using iText.Layout.Element;
using iText.Layout.Properties;

namespace Api.Web.FinanceIA.Servicios.Pdf;

/// <summary>
/// The client's brand in page 1's header slot: its logo fitted into the box the Financia symbol + wordmark use,
/// aspect ratio preserved, with <c>branding.company_name</c> beside it in the heading face. Only the header slot
/// changes: the accent rule, the report title, the date and the hairline stay where they are, and the footer
/// keeps "Elaborado con Financia" (<see cref="IPdfBrand"/> is never a client). The slot is centred on the band's
/// middle; the Financia lockup's own placement (<see cref="HeaderLockup"/>) does not apply to it. Built per export
/// from a co-branded <see cref="TenantPdfBranding"/>; the geometry is a pure function (<see cref="Layout"/>) so it
/// is tested without parsing a PDF.
/// </summary>
internal sealed class ClientPdfBrand
{
    /// <summary>The logo box height: the 26 pt Financia symbol's footprint plus a hair.</summary>
    internal const float LogoBoxHeight = 28f;

    /// <summary>The widest the logo may be drawn.</summary>
    internal const float LogoBoxMaxWidth = 150f;

    /// <summary>Space between the logo (or its plate) and the company name.</summary>
    internal const float NameGap = 8f;

    /// <summary>The company name's size, shrunk to fit down to <see cref="NameMinSize"/>, then ellipsised.</summary>
    internal const float NameMaxSize = 13f;

    /// <inheritdoc cref="NameMaxSize"/>
    internal const float NameMinSize = 9f;

    /// <summary>
    /// Dark theme only: the white plate's padding around the logo, and its corner radius. The logo keeps its light
    /// size; the plate (28 + 8 pt) still sits well inside the band's 50 pt below the accent rule.
    /// </summary>
    internal const float PlatePadding = 4f;

    /// <inheritdoc cref="PlatePadding"/>
    internal const float PlateRadius = 3f;

    /// <summary>The gap kept between the brand slot and the report title / date block.</summary>
    internal const float TitleGap = 18f;

    private const string Ellipsis = "…";

    private readonly PdfBrandLogo _logo;
    private readonly string?      _companyName;

    /// <summary>Builds the slot from a co-branded <paramref name="branding"/>.</summary>
    /// <exception cref="ArgumentException">The branding carries no logo.</exception>
    internal ClientPdfBrand(TenantPdfBranding branding)
    {
        ArgumentNullException.ThrowIfNull(branding);
        _logo        = branding.Logo ?? throw new ArgumentException("A client brand needs a logo.", nameof(branding));
        _companyName = string.IsNullOrWhiteSpace(branding.CompanyName) ? null : branding.CompanyName.Trim();
    }

    /// <summary>Where each part of the slot lands. <see cref="Plate"/> is null on the light theme.</summary>
    internal readonly record struct SlotLayout(
        Rectangle  Logo,
        Rectangle? Plate,
        string?    NameText,
        float      NameX,
        float      NameSize);

    /// <summary>
    /// <paramref name="pixelWidth"/>×<paramref name="pixelHeight"/> scaled to the largest size that fits
    /// <paramref name="boxWidth"/>×<paramref name="boxHeight"/>: the ratio is kept exactly, never distorted.
    /// </summary>
    internal static (float Width, float Height) FitLogo(int pixelWidth, int pixelHeight, float boxWidth, float boxHeight)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0 || boxWidth <= 0 || boxHeight <= 0)
            return (0f, 0f);

        double scale = Math.Min(boxWidth / (double)pixelWidth, boxHeight / (double)pixelHeight);
        return ((float)(pixelWidth * scale), (float)(pixelHeight * scale));
    }

    /// <summary>The x the slot must not cross: the left edge of the right-aligned title/date block, less a gap.</summary>
    internal static float BrandRightLimit(float right, float titleWidth, float dateWidth) =>
        right - Math.Max(titleWidth, dateWidth) - TitleGap;

    /// <summary>
    /// Lays the slot out from <paramref name="left"/> (the side margin) to <paramref name="limit"/>, centred on
    /// <paramref name="middle"/>. <paramref name="measure"/> is the heading face's width of a string at a size.
    /// </summary>
    internal static SlotLayout Layout(
        int pixelWidth, int pixelHeight, string? companyName,
        float left, float middle, float limit, bool onPlate,
        Func<string, float, float> measure)
    {
        ArgumentNullException.ThrowIfNull(measure);

        float pad   = onPlate ? PlatePadding : 0f;
        float boxW  = Math.Max(0f, Math.Min(LogoBoxMaxWidth, limit - left) - (2f * pad));
        (float w, float h) = FitLogo(pixelWidth, pixelHeight, boxW, LogoBoxHeight);

        var logo = new Rectangle(left + pad, middle - (h / 2f), w, h);
        Rectangle? plate = onPlate
            ? new Rectangle(left, logo.GetY() - pad, w + (2f * pad), h + (2f * pad))
            : null;

        float nameX = left + w + (2f * pad) + NameGap;
        (string? text, float size) = FitName(companyName, limit - nameX, measure);
        return new SlotLayout(logo, plate, text, nameX, size);
    }

    /// <summary>Draws the slot on page 1's header. <paramref name="onPlate"/> is true on the dark theme.</summary>
    internal void Draw(
        PdfCanvas pdfCanvas, Canvas canvas, PdfFont headingBold, Color ink,
        float left, float middle, float limit, bool onPlate)
    {
        ArgumentNullException.ThrowIfNull(pdfCanvas);
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(headingBold);

        SlotLayout layout = Layout(
            _logo.PixelWidth, _logo.PixelHeight, _companyName, left, middle, limit, onPlate,
            (text, size) => headingBold.GetWidth(text, size));

        if (layout.Plate is { } plate)
        {
            // A logo is drawn for a white page; on navy paper it sits on its own white plate so a dark-ink,
            // transparent logo stays legible.
            pdfCanvas.SaveState()
                .SetFillColor(PdfPalette.Light.Paper)
                .RoundRectangle(plate.GetX(), plate.GetY(), plate.GetWidth(), plate.GetHeight(), PlateRadius)
                .Fill()
                .RestoreState();
        }

        // A fresh ImageData per export from the immutable bytes: iText image objects are never shared between
        // documents (the same rule the brand fonts follow).
        ImageData image = ImageDataFactory.Create(_logo.Bytes);
        pdfCanvas.AddImageFittedIntoRectangle(image, layout.Logo, false);

        if (layout.NameText is not null)
        {
            canvas.ShowTextAligned(
                new Paragraph(new Text(layout.NameText).SetFont(headingBold).SetFontSize(layout.NameSize).SetFontColor(ink))
                    .SetMargin(0).SetMultipliedLeading(1f),
                layout.NameX, middle - (layout.NameSize * 0.42f), TextAlignment.LEFT);
        }
    }

    // The largest size from NameMaxSize down to NameMinSize (0.5 pt steps) at which the name fits; at the minimum,
    // the longest prefix + "…" that fits. Nothing when not even one letter and the ellipsis fit.
    private static (string? Text, float Size) FitName(string? name, float available, Func<string, float, float> measure)
    {
        if (string.IsNullOrWhiteSpace(name) || available <= 0f)
            return (null, 0f);

        for (float size = NameMaxSize; size >= NameMinSize; size -= 0.5f)
        {
            if (measure(name, size) <= available)
                return (name, size);
        }

        for (int length = name.Length - 1; length >= 1; length--)
        {
            string candidate = name[..length].TrimEnd() + Ellipsis;
            if (measure(candidate, NameMinSize) <= available)
                return (candidate, NameMinSize);
        }

        return (null, 0f);
    }
}
