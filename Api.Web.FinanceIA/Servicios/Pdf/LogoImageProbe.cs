// Project : Api.Web.FinanceIA
// Created : 2026-11-08

using System.Buffers.Binary;

namespace Api.Web.FinanceIA.Servicios.Pdf;

/// <summary>
/// Identifies a logo by its magic bytes — never by the Content-Type the server claims — and reads its pixel size
/// from the header alone, so an oversized image is refused before anything decodes it. PNG and JPEG only: SVG
/// (a script-capable document), GIF and WebP are refused.
/// </summary>
internal static class LogoImageProbe
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The result of a probe: the format and size, or null with a reason code.</summary>
    internal readonly record struct Probe(PdfLogoFormat? Format, int Width, int Height, string? FailureReason);

    /// <summary>
    /// Probes <paramref name="bytes"/>. A failure carries <see cref="BrandingLogoFailure.Format"/> (not PNG/JPEG,
    /// or a header too short to read) or <see cref="BrandingLogoFailure.Dimensions"/> (zero, or more than
    /// <paramref name="maxSide"/> px on a side).
    /// </summary>
    internal static Probe TryProbe(ReadOnlySpan<byte> bytes, int maxSide)
    {
        PdfLogoFormat? format;
        int width;
        int height;

        if (bytes.StartsWith(PngSignature))
        {
            // IHDR is the first chunk: length(4) "IHDR"(4) width(4) height(4), big-endian.
            if (bytes.Length < 24 || !bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
                return new Probe(null, 0, 0, BrandingLogoFailure.Format);

            format = PdfLogoFormat.Png;
            width  = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(16, 4)));
            height = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(20, 4)));
        }
        else if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            if (!TryReadJpegSize(bytes, out width, out height))
                return new Probe(null, 0, 0, BrandingLogoFailure.Format);

            format = PdfLogoFormat.Jpeg;
        }
        else
        {
            return new Probe(null, 0, 0, BrandingLogoFailure.Format);
        }

        if (width <= 0 || height <= 0 || width > maxSide || height > maxSide)
            return new Probe(null, width, height, BrandingLogoFailure.Dimensions);

        return new Probe(format, width, height, null);
    }

    // Walks the marker segments after SOI to the first start-of-frame (SOF0..SOF15 except DHT C4, JPG C8 and
    // DAC CC), whose payload is precision(1) height(2) width(2).
    private static bool TryReadJpegSize(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width  = 0;
        height = 0;
        int i  = 2;

        while (i + 4 <= bytes.Length)
        {
            if (bytes[i] != 0xFF)
                return false;

            byte marker = bytes[i + 1];
            if (marker == 0xFF) { i++; continue; }                 // fill byte
            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) { i += 2; continue; }   // no length
            if (marker is 0xD9 or 0xDA) return false;              // EOI / SOS before any frame header

            int length = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(i + 2, 2));
            if (length < 2)
                return false;

            bool isFrame = marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC;
            if (isFrame)
            {
                if (i + 9 > bytes.Length)
                    return false;

                height = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(i + 5, 2));
                width  = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(i + 7, 2));
                return true;
            }

            i += 2 + length;
        }

        return false;
    }
}
