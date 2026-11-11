// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-13

using Api.Web.FinanceIA.Servicios;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

public sealed class FileSignatureValidatorTests
{
    private static readonly byte[] PdfSignature = { 0x25, 0x50, 0x44, 0x46, 0x2D }; // "%PDF-"
    private static readonly byte[] ZipSignature = { 0x50, 0x4B, 0x03, 0x04 };       // "PK\x03\x04"

    private static byte[] Concat(byte[] first, byte[] second)
    {
        var result = new byte[first.Length + second.Length];
        first.CopyTo(result, 0);
        second.CopyTo(result, first.Length);
        return result;
    }

    // ── Matches: judges by leading bytes, signature anywhere in the window ────────

    [Theory]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D }, true)]                    // "%PDF-" at offset 0
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37 }, true)]  // "%PDF-1.7…" (longer)
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, false)]                         // JPEG
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, false)] // PNG
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04 }, false)]                         // ZIP
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46 }, false)]                         // "%PDF" — one byte short of "%PDF-"
    [InlineData(new byte[] { }, false)]                                               // empty
    public void Matches_Pdf_JudgesByLeadingBytes(byte[] leadingBytes, bool expected)
    {
        Assert.Equal(expected, FileSignatureValidator.Matches(leadingBytes, FileSignatureType.Pdf));
    }

    [Fact]
    public void Matches_SignatureAtOffsetZero_ReturnsTrue()
    {
        Assert.True(FileSignatureValidator.Matches(PdfSignature, FileSignatureType.Pdf));
    }

    [Fact]
    public void Matches_SignatureAfterShortPreamble_ReturnsTrue()
    {
        // The fix: scanners and PDF tooling routinely emit a UTF-8 BOM (or a few newlines) before
        // %PDF-. Such files read and analyse fine today and must not be rejected.
        var bytes = Concat(new byte[] { 0xEF, 0xBB, 0xBF, 0x0A, 0x0A }, PdfSignature); // BOM + 2×LF + "%PDF-"

        Assert.True(FileSignatureValidator.Matches(bytes, FileSignatureType.Pdf));
    }

    [Fact]
    public void Matches_SignatureEndingExactlyAtWindowEdge_ReturnsTrue()
    {
        // Last start offset whose whole 5-byte signature still fits inside the window.
        var window = FileSignatureValidator.SignatureSearchWindowBytes;
        var bytes  = new byte[window];
        PdfSignature.CopyTo(bytes, window - PdfSignature.Length);

        Assert.True(FileSignatureValidator.Matches(bytes, FileSignatureType.Pdf));
    }

    [Fact]
    public void Matches_SignatureStraddlingWindowEdge_ReturnsFalse()
    {
        // Starts one byte too late: its final byte falls outside the window.
        var window = FileSignatureValidator.SignatureSearchWindowBytes;
        var bytes  = new byte[window + PdfSignature.Length];
        PdfSignature.CopyTo(bytes, window - PdfSignature.Length + 1);

        Assert.False(FileSignatureValidator.Matches(bytes, FileSignatureType.Pdf));
    }

    [Fact]
    public void Matches_SignatureOnlyBeyondWindow_ReturnsFalse()
    {
        var window = FileSignatureValidator.SignatureSearchWindowBytes;
        var bytes  = new byte[window + 64];
        PdfSignature.CopyTo(bytes, window + 10); // wholly beyond the window

        Assert.False(FileSignatureValidator.Matches(bytes, FileSignatureType.Pdf));
    }

    [Fact]
    public void Matches_NoSignatureAnywhere_ReturnsFalse()
    {
        var bytes = new byte[2048];
        Array.Fill(bytes, (byte)0x41); // 'A' repeated — never the signature

        Assert.False(FileSignatureValidator.Matches(bytes, FileSignatureType.Pdf));
    }

    [Fact]
    public void Matches_PdfBytes_AcceptedRegardlessOfAnyDeclaredContentType()
    {
        // The validator has no Content-Type input by design: a file whose bytes are a real PDF is
        // accepted even if the caller declared it as, say, image/png.
        var pdfBytes = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x34 }; // "%PDF-1.4"

        Assert.True(FileSignatureValidator.Matches(pdfBytes, FileSignatureType.Pdf));
    }

    [Fact]
    public void Matches_SpoofedContentType_NonPdfBytesRejected()
    {
        // The motivating case: a client declares application/pdf but the bytes are a PNG. The
        // header check is defeated by the spoof; the byte check is what rejects it.
        var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        Assert.False(FileSignatureValidator.Matches(pngBytes, FileSignatureType.Pdf));
    }

    [Fact]
    public void GetSignatureLength_Pdf_IsFive()
    {
        Assert.Equal(5, FileSignatureValidator.GetSignatureLength(FileSignatureType.Pdf));
    }

    // ── MatchesAsync: bounded read of the window from a stream ────────────────────

    [Fact]
    public async Task MatchesAsync_ValidPdfStream_ReturnsTrue()
    {
        using var stream = new MemoryStream(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x39 });

        Assert.True(await FileSignatureValidator.MatchesAsync(stream, FileSignatureType.Pdf));
    }

    [Fact]
    public async Task MatchesAsync_SignatureAfterPreambleStream_ReturnsTrue()
    {
        using var stream = new MemoryStream(Concat(new byte[] { 0xEF, 0xBB, 0xBF }, PdfSignature)); // BOM + "%PDF-"

        Assert.True(await FileSignatureValidator.MatchesAsync(stream, FileSignatureType.Pdf));
    }

    [Fact]
    public async Task MatchesAsync_SpoofedPdfStream_ReturnsFalse()
    {
        // Declared application/pdf, actually a ZIP archive.
        using var stream = new MemoryStream(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00 });

        Assert.False(await FileSignatureValidator.MatchesAsync(stream, FileSignatureType.Pdf));
    }

    [Fact]
    public async Task MatchesAsync_EmptyStream_ReturnsFalseWithoutThrowing()
    {
        using var stream = new MemoryStream(Array.Empty<byte>());

        Assert.False(await FileSignatureValidator.MatchesAsync(stream, FileSignatureType.Pdf));
    }

    [Fact]
    public async Task MatchesAsync_StreamShorterThanSignature_ReturnsFalse()
    {
        using var stream = new MemoryStream(new byte[] { 0x25, 0x50, 0x44, 0x46 }); // "%PDF" — 4 bytes

        Assert.False(await FileSignatureValidator.MatchesAsync(stream, FileSignatureType.Pdf));
    }

    [Fact]
    public async Task MatchesAsync_LargeStream_ReadIsBoundedByWindowAndNotDrained()
    {
        // The property that actually matters: the validator reads at most the window and never
        // drains the whole stream, regardless of file size.
        var window = FileSignatureValidator.SignatureSearchWindowBytes;
        var bytes  = new byte[window * 8];
        PdfSignature.CopyTo(bytes, 3); // valid PDF: signature just after a tiny preamble
        using var stream = new MemoryStream(bytes);

        var result = await FileSignatureValidator.MatchesAsync(stream, FileSignatureType.Pdf);

        Assert.True(result);
        Assert.True(stream.Position <= window,
            $"read {stream.Position} bytes, expected at most the {window}-byte window");
        Assert.True(stream.Position < bytes.Length,
            "the whole stream was drained; the read must stay bounded to the window");
    }

    // ── Xlsx: the same table, but anchored at offset 0 ────────────────────────────

    [Fact]
    public void Matches_Xlsx_SignatureAtOffsetZero_ReturnsTrue()
    {
        Assert.True(FileSignatureValidator.Matches(ZipSignature, FileSignatureType.Xlsx));
    }

    [Fact]
    public void Matches_Xlsx_SignatureAfterPreamble_ReturnsFalse()
    {
        // The reason Xlsx is anchored while Pdf is not. A ZIP local file header is defined to sit
        // at offset 0, so accepting it anywhere in the window — as the PDF rule does — would pass
        // any file that merely happens to contain these four bytes early, such as a PDF carrying an
        // embedded archive. That would make the cheap reject meaningless.
        var bytes = Concat(new byte[] { 0xEF, 0xBB, 0xBF }, ZipSignature);

        Assert.False(FileSignatureValidator.Matches(bytes, FileSignatureType.Xlsx));
    }

    [Fact]
    public void Matches_Xlsx_SignatureEmbeddedInsideAPdf_ReturnsFalse()
    {
        var bytes = Concat(PdfSignature, Concat(new byte[64], ZipSignature));

        Assert.False(FileSignatureValidator.Matches(bytes, FileSignatureType.Xlsx));
    }

    [Theory]
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00 }, true)]   // real .xlsx opening bytes
    [InlineData(new byte[] { 0x50, 0x4B, 0x05, 0x06 }, false)]              // empty-archive record, not a local header
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D }, false)]        // PDF
    [InlineData(new byte[] { 0x50, 0x4B, 0x03 }, false)]                    // one byte short
    [InlineData(new byte[] { }, false)]                                     // empty
    public void Matches_Xlsx_JudgesByLeadingBytes(byte[] leadingBytes, bool expected)
    {
        Assert.Equal(expected, FileSignatureValidator.Matches(leadingBytes, FileSignatureType.Xlsx));
    }

    [Fact]
    public async Task MatchesAsync_Xlsx_ReadsFromTheStreamAndAnchors()
    {
        using var valid = new MemoryStream(Concat(ZipSignature, new byte[512]));
        Assert.True(await FileSignatureValidator.MatchesAsync(valid, FileSignatureType.Xlsx));

        using var shifted = new MemoryStream(Concat(new byte[1], Concat(ZipSignature, new byte[512])));
        Assert.False(await FileSignatureValidator.MatchesAsync(shifted, FileSignatureType.Xlsx));
    }

    [Fact]
    public void Matches_AddingXlsx_LeftPdfBehaviourUnchanged()
    {
        // Anchoring is per type. PDF must keep accepting a signature after a preamble, which is the
        // whole reason the search window exists.
        Assert.True(FileSignatureValidator.Matches(
            Concat(new byte[] { 0xEF, 0xBB, 0xBF }, PdfSignature), FileSignatureType.Pdf));

        Assert.Equal(4, FileSignatureValidator.GetSignatureLength(FileSignatureType.Xlsx));
        Assert.Equal(5, FileSignatureValidator.GetSignatureLength(FileSignatureType.Pdf));
    }
}
