// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-03

using Api.Web.FinanceIA.Servicios;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// The CPCE Córdoba "Legalización de trámites con firma digital" wrapper: a 1-page ENCRYPTED cover
/// (empty user password) carrying the balance under the name-tree key "Balance" and the auditor's
/// report under "Informe". The fixtures are synthetic — see Fixtures/EncryptedOblea/README.md.
/// </summary>
public sealed class PdfAttachmentExtractorEncryptedTests
{
    private static readonly Guid DocumentId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string TenantId = "tenant-test";

    public static TheoryData<string> Wrappers => new()
    {
        "cpce_plain.pdf",               // control: same structure, not encrypted
        "cpce_aes128.pdf",              // V4 / R4 / AESV2 — the production case (xref stream, linearized)
        "cpce_aes128_xreftable.pdf",    // V4 / R4 / AESV2, exact P = -1596, classic xref table
        "cpce_aes256.pdf",              // V5 / R6 / AESV3
        "cpce_rc4128.pdf",              // V2 / R3 / RC4-128
    };

    [Theory]
    [MemberData(nameof(Wrappers))]
    public void Unwrap_CpceWrapper_YieldsTheBalanceAttachment(string fixture)
    {
        var outer    = Fixture(fixture);
        var expected = Fixture("balance.expected.pdf");

        var result = PdfAttachmentExtractor.UnwrapEmbeddedBalance(
            outer, NullLogger<PdfAttachmentExtractorEncryptedTests>.Instance, DocumentId, TenantId);

        Assert.NotSame(outer, result);
        Assert.Equal(expected, result);
    }

    [Theory]
    [MemberData(nameof(Wrappers))]
    public void Unwrap_CpceWrapper_IsNotFlaggedUnread(string fixture)
    {
        var result = PdfAttachmentExtractor.Unwrap(
            Fixture(fixture), NullLogger<PdfAttachmentExtractorEncryptedTests>.Instance, DocumentId, TenantId);

        Assert.False(result.AttachmentUnread);
        Assert.Equal(2, result.AttachmentsSeen);
    }

    /// <summary>
    /// Pins the ROOT CAUSE, and why the extractor walks the name tree itself: PdfPig 0.1.15's
    /// <c>Advanced.TryGetEmbeddedFiles</c> reads the name tree from a copy of the catalog taken before
    /// decryption, so in an encrypted document whose name tree sits directly in the catalog the keys
    /// are ciphertext — while the attachment bytes themselves are decrypted correctly. If a PdfPig
    /// upgrade makes this fail, the bug is fixed upstream; the extractor's own walk stays correct
    /// either way.
    /// </summary>
    [Theory]
    [InlineData("cpce_aes128.pdf")]
    [InlineData("cpce_aes128_xreftable.pdf")]
    [InlineData("cpce_aes256.pdf")]
    [InlineData("cpce_rc4128.pdf")]
    public void PdfPig_TryGetEmbeddedFiles_ReturnsUndecryptedKeys_ButDecryptedBytes(string fixture)
    {
        using var doc = PdfDocument.Open(Fixture(fixture));

        Assert.True(doc.IsEncrypted);
        Assert.True(doc.Advanced.TryGetEmbeddedFiles(out var files));
        Assert.NotNull(files);
        Assert.Equal(2, files.Count);

        Assert.DoesNotContain(files, f => f.Name is "Balance" or "Informe");
        Assert.All(files, f => Assert.True(f.Bytes[..4].SequenceEqual("%PDF"u8)));
    }

    [Fact]
    public void PdfPig_TryGetEmbeddedFiles_ReturnsReadableKeys_WhenNotEncrypted()
    {
        using var doc = PdfDocument.Open(Fixture("cpce_plain.pdf"));

        Assert.True(doc.Advanced.TryGetEmbeddedFiles(out var files));
        Assert.Equal(["Balance", "Informe"], files!.Select(f => f.Name).Order());
    }

    private static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "EncryptedOblea", name));
}
