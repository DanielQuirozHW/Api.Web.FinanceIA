// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-08

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Api.Web.FinanceIA.Servicios.Pdf;
using iText.Kernel.Colors;

namespace Api.Web.FinanceIA.Tests.Servicios.PdfBranding;

/// <summary>
/// Synthetic logo bytes for the branding tests. Every image is generated here or is a constant below. Nothing is
/// fetched and nothing resembles a real client's mark.
/// </summary>
internal static class BrandingFixtures
{
    /// <summary>A 40×20 solid JPEG (generated once with an image library and pasted here as base64).</summary>
    internal static readonly byte[] Jpeg40x20 = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAYEBQYFBAYGBQYHBwYIChAKCgkJChQODwwQFxQYGBcUFhYaHSUfGhsjHBYWICwgIyYnKSopGR8tMC0oMCUoKSj/2wBDAQcHBwoIChMKChMoGhYaKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCj/wAARCAAUACgDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDzSiiivtj5oKKKKACiiigAooooAKKKKACiiigD/9k=");

    /// <summary>A GIF89a header: a format the policy refuses.</summary>
    internal static readonly byte[] Gif = Encoding.ASCII.GetBytes("GIF89a").Concat(new byte[64]).ToArray();

    /// <summary>A WebP (RIFF....WEBP) header: a format the policy refuses.</summary>
    internal static readonly byte[] WebP = Encoding.ASCII.GetBytes("RIFF\u0000\u0000\u0000\u0000WEBPVP8 ").Concat(new byte[64]).ToArray();

    /// <summary>An SVG document: refused, whatever it is served as.</summary>
    internal static readonly byte[] Svg = Encoding.UTF8.GetBytes(
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"><rect width=\"10\" height=\"10\"/></svg>");

    /// <summary>A solid opaque PNG of the given size (RGBA, 8 bits per channel).</summary>
    internal static byte[] SolidPng(int width, int height, byte r = 0x1B, byte g = 0x4F, byte b = 0x9C) =>
        Png(width, height, (_, _) => (r, g, b, 0xFF));

    /// <summary>A PNG whose IHDR claims <paramref name="width"/>×<paramref name="height"/> but carries no pixel data.</summary>
    internal static byte[] PngHeaderOnly(int width, int height)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(ms, "IHDR", Ihdr(width, height));
        return ms.ToArray();
    }

    /// <summary>
    /// The synthetic placeholder logo of the screenshots: a ring and three stepped bars in a slate blue, then a
    /// block of "lettering" bars, on a transparent background, 4:1. It is a placeholder, not anyone's mark.
    /// </summary>
    internal static byte[] PlaceholderLogo(int width = 480, int height = 120)
    {
        float s = height / 120f;
        return Png(width, height, (x, y) =>
        {
            float fx = x / s, fy = y / s;

            // A ring centred at (60, 60), radius 40, 12 thick.
            float dx = fx - 60f, dy = fy - 60f;
            float d  = MathF.Sqrt((dx * dx) + (dy * dy));
            bool ring = d is >= 28f and <= 40f;

            // Three stepped bars under the ring's right side.
            bool bar = (fy is >= 30f and <= 42f && fx is >= 120f and <= 230f)
                    || (fy is >= 54f and <= 66f && fx is >= 120f and <= 290f)
                    || (fy is >= 78f and <= 90f && fx is >= 120f and <= 250f);

            // A row of lettering blocks to the right.
            bool letters = fy is >= 46f and <= 74f && fx is >= 310f and <= 470f && ((int)((fx - 310f) / 14f) % 2 == 0);

            return ring || bar || letters ? ((byte)0x23, (byte)0x4E, (byte)0x8A, (byte)0xFF) : ((byte)0, (byte)0, (byte)0, (byte)0);
        });
    }

    /// <summary>A co-branded branding with a wide placeholder logo.</summary>
    internal static TenantPdfBranding CoBranded(string? companyName, DeviceRgb? accent = null, int width = 480, int height = 120) =>
        new(new PdfBrandLogo(PlaceholderLogo(width, height), width, height, PdfLogoFormat.Png), companyName, accent);

    /// <summary>A co-branded branding with a solid logo of the given pixel size.</summary>
    internal static TenantPdfBranding CoBrandedSolid(int width, int height, string? companyName = null) =>
        new(new PdfBrandLogo(SolidPng(width, height), width, height, PdfLogoFormat.Png), companyName, null);

    private static byte[] Png(int width, int height, Func<int, int, (byte R, byte G, byte B, byte A)> pixel)
    {
        byte[] raw = new byte[height * ((width * 4) + 1)];
        int i = 0;
        for (int y = 0; y < height; y++)
        {
            raw[i++] = 0;   // filter: none
            for (int x = 0; x < width; x++)
            {
                (byte r, byte g, byte b, byte a) = pixel(x, y);
                raw[i++] = r; raw[i++] = g; raw[i++] = b; raw[i++] = a;
            }
        }

        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(raw);

        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(ms, "IHDR", Ihdr(width, height));
        WriteChunk(ms, "IDAT", compressed.ToArray());
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static byte[] Ihdr(int width, int height)
    {
        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), (uint)height);
        ihdr[8]  = 8;   // bit depth
        ihdr[9]  = 6;   // RGBA
        return ihdr;
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        stream.Write(len);

        byte[] typeAndData = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        stream.Write(typeAndData);

        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typeAndData));
        stream.Write(crc);
    }

    private static uint Crc32(byte[] data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            c ^= b;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }

        return c ^ 0xFFFFFFFF;
    }
}
