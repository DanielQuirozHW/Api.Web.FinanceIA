// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-08

using System.Security.Cryptography;
using System.Text;
using Api.Web.FinanceIA.Servicios;
using iText.Kernel.Pdf;
using Microsoft.Extensions.Logging.Abstractions;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// A reproducible fingerprint of what an export DRAWS: SHA-256 over every page's decoded content stream and every
/// XObject stream reachable from the page resources (recursively, resource names in ordinal order). Everything
/// that legitimately differs between two runs of the same render is left out — the document information
/// (CreationDate / ModDate), the trailer /ID and the font programs (iText gives each subset a random prefix) — so
/// two renders of the same input with the same clock fingerprint identically, and any change to a colour, a path
/// or a glyph changes the hash.
/// </summary>
internal static class PdfContentFingerprint
{
    /// <summary>The fixed clock every fingerprinted render uses.</summary>
    internal static readonly DateTime FixedUtc = new(2026, 11, 8, 20, 5, 0, DateTimeKind.Utc);

    /// <summary>A service whose printed "now" is <see cref="FixedUtc"/>.</summary>
    internal static PdfExportService FixedClockService() =>
        new(new CashFlowNarrativeService(),
            new CashFlowReconciliationService(),
            new AlertRatioReconciliationService(),
            NullLogger<PdfExportService>.Instance)
        {
            UtcNow = static () => FixedUtc
        };

    internal static string Of(byte[] pdfBytes)
    {
        using var reader = new PdfReader(new MemoryStream(pdfBytes));
        using var pdf    = new PdfDocument(reader);
        using var sha    = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        for (int i = 1; i <= pdf.GetNumberOfPages(); i++)
        {
            PdfPage page = pdf.GetPage(i);
            Append(sha, $"page {i}");
            sha.AppendData(page.GetContentBytes());
            AppendXObjects(sha, page.GetResources().GetPdfObject(), depth: 0);
        }

        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private static void AppendXObjects(IncrementalHash sha, PdfDictionary? resources, int depth)
    {
        if (resources is null || depth > 8) return;

        PdfDictionary? xobjects = resources.GetAsDictionary(PdfName.XObject);
        if (xobjects is null) return;

        foreach (PdfName name in xobjects.KeySet().OrderBy(n => n.GetValue(), StringComparer.Ordinal))
        {
            if (xobjects.GetAsStream(name) is not { } stream) continue;

            Append(sha, $"xobject {depth} {name.GetValue()}");
            sha.AppendData(stream.GetBytes());
            AppendXObjects(sha, stream.GetAsDictionary(PdfName.Resources), depth + 1);
        }
    }

    private static void Append(IncrementalHash sha, string marker) => sha.AppendData(Encoding.ASCII.GetBytes(marker + "\n"));
}
