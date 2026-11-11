// Project : Api.Web.FinanceIA.Tests
// Created : 2026-07-17

using Api.Web.FinanceIA.Servicios;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Filespec;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Api.Web.FinanceIA.Tests.Servicios;

public sealed class PdfAttachmentExtractorTests
{
    // MinAttachmentBytes in the extractor is 10 KB; pad past it so fixtures qualify.
    private const int PaddingChars = 16 * 1024;

    private static readonly Guid DocumentId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string TenantId = "tenant-test";

    private static byte[] Unwrap(byte[] outerBytes) =>
        PdfAttachmentExtractor.UnwrapEmbeddedBalance(
            outerBytes, NullLogger<PdfAttachmentExtractorTests>.Instance, DocumentId, TenantId);

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public void Unwrap_AttachmentOnFileAttachmentAnnotation_SelectsAttachment()
    {
        var balance = CreatePdf(pageCount: 3, pad: true);
        var oblea   = CreateOblea(balance, "oblea_1234.pdf", viaNameTree: false);

        // Guard the fixture: if this ever grew a name-tree entry the test would pass
        // through the pre-existing path and prove nothing about annotations.
        Assert.Equal(0, CountNameTreeEmbeddedFiles(oblea));

        var result = Unwrap(oblea);

        Assert.Equal(balance, result);
    }

    [Fact]
    public void Unwrap_PlainPdfWithNoAttachments_ReturnsOriginalBytesUntouched()
    {
        var plain = CreatePdf(pageCount: 2, pad: true);

        var result = Unwrap(plain);

        Assert.Same(plain, result);
    }

    [Fact]
    public void Unwrap_AttachmentInEmbeddedFilesNameTree_StillSelectsAttachment()
    {
        var balance = CreatePdf(pageCount: 3, pad: true);
        var oblea   = CreateOblea(balance, "oblea_1234.pdf", viaNameTree: true);

        var result = Unwrap(oblea);

        Assert.Equal(balance, result);
    }

    /// <summary>
    /// The shape of a real CPCEN oblea: a cover sheet whose page annotation carries the
    /// signed oblea, which in turn carries the balance and the professional's report in
    /// its own name tree. The balance is two levels down, so a single pass is not enough.
    /// </summary>
    [Fact]
    public void Unwrap_NestedAttachments_DescendsToInnermostBalance()
    {
        var balance = CreatePdf(pageCount: 13, pad: true, padChars: 3 * PaddingChars);
        var report  = CreatePdf(pageCount: 3,  pad: true);

        // The report must stay far enough below the balance that the size gap clears the
        // extractor's 20 % ambiguity threshold, otherwise the oblea is kept instead.
        Assert.True((balance.Length - report.Length) / (double)balance.Length > 0.20);

        var oblea = CreateOblea(balance, "balance.pdf", viaNameTree: true, extra: report, extraName: "informe.pdf");
        var cover = CreateOblea(oblea, "oblea_562026196D.pdf", viaNameTree: false);

        var result = Unwrap(cover);

        Assert.Equal(balance, result);
    }

    // ── Name-aware choice among several attachments ──────────────────────────

    /// <summary>
    /// Sizes within 20 % would make the size rule give up and keep the oblea. A single
    /// attachment named as the statements wins anyway — even when it is the SMALLER one,
    /// which proves the name, not the size, decided.
    /// </summary>
    [Fact]
    public void Unwrap_OneStatementsNameAndSizesClose_NameWins()
    {
        var eecc    = CreatePdf(pageCount: 3, pad: true, padChars: PaddingChars);
        var informe = CreatePdf(pageCount: 3, pad: true, padChars: PaddingChars + 1024);

        AssertSizesAmbiguous(eecc, informe);
        Assert.True(eecc.Length < informe.Length, "the statements must be the smaller file");

        var oblea = CreateOblea(eecc, "EECC AL 30-06-2025 (2).pdf", viaNameTree: true,
                                extra: informe, extraName: "INFORME DEL AUDITOR (1).pdf");

        var result = Unwrap(oblea);

        Assert.Equal(eecc, result);
    }

    [Theory]
    [InlineData("Estados_Contables_2024.pdf")]
    [InlineData("estados financieros.pdf")]
    [InlineData("Presentación Única de Balances.pdf")]
    public void Unwrap_StatementsNameVariants_NameWins(string statementsName)
    {
        var statements = CreatePdf(pageCount: 3, pad: true, padChars: PaddingChars);
        var other      = CreatePdf(pageCount: 3, pad: true, padChars: PaddingChars + 1024);

        AssertSizesAmbiguous(statements, other);

        var oblea = CreateOblea(statements, statementsName, viaNameTree: true,
                                extra: other, extraName: "Certificación de firma.pdf");

        Assert.Equal(statements, Unwrap(oblea));
    }

    [Fact]
    public void Unwrap_NoNameMatchesAndSizesApart_FallsBackToLargest()
    {
        var large = CreatePdf(pageCount: 13, pad: true, padChars: 3 * PaddingChars);
        var small = CreatePdf(pageCount: 3,  pad: true);

        Assert.True((large.Length - small.Length) / (double)large.Length > 0.20);

        var oblea = CreateOblea(small, "anexo_a.pdf", viaNameTree: true, extra: large, extraName: "anexo_b.pdf");

        Assert.Equal(large, Unwrap(oblea));
    }

    [Fact]
    public void Unwrap_NoNameMatchesAndSizesClose_KeepsOriginalBytes()
    {
        var first  = CreatePdf(pageCount: 3, pad: true, padChars: PaddingChars);
        var second = CreatePdf(pageCount: 3, pad: true, padChars: PaddingChars + 1024);

        AssertSizesAmbiguous(first, second);

        var oblea = CreateOblea(first, "anexo_a.pdf", viaNameTree: true, extra: second, extraName: "anexo_b.pdf");

        Assert.Same(oblea, Unwrap(oblea));
    }

    /// <summary>
    /// A deprioritized term disqualifies a name even when it also says "balance", so two
    /// "balance" names where one is the auditor's report still resolve to the statements.
    /// </summary>
    [Fact]
    public void Unwrap_AuditorReportOnTheBalance_DoesNotCountAsStatements()
    {
        var balance = CreatePdf(pageCount: 3, pad: true, padChars: PaddingChars);
        var report  = CreatePdf(pageCount: 3, pad: true, padChars: PaddingChars + 1024);

        AssertSizesAmbiguous(balance, report);

        var oblea = CreateOblea(balance, "Balance 2024.pdf", viaNameTree: true,
                                extra: report, extraName: "Informe del auditor sobre el balance.pdf");

        Assert.Equal(balance, Unwrap(oblea));
    }

    /// <summary>
    /// The real reference case: cover sheet → oblea_*.pdf (page annotation) → { EECC, INFORME
    /// DEL AUDITOR } in the oblea's name tree, sizes close enough that size alone would stop
    /// at the oblea. The descent reaches the statements two levels down.
    /// </summary>
    [Fact]
    public void Unwrap_NestedTwoLevelsWithCloseSizes_NameWinsAtInnerLevel()
    {
        var eecc    = CreatePdf(pageCount: 13, pad: true, padChars: 2 * PaddingChars);
        var informe = CreatePdf(pageCount: 3,  pad: true, padChars: 2 * PaddingChars + 2048);

        AssertSizesAmbiguous(eecc, informe);

        var oblea = CreateOblea(eecc, "EECC AL 30-06-2025 (2).pdf", viaNameTree: true,
                                extra: informe, extraName: "INFORME DEL AUDITOR (1).pdf");
        var cover = CreateOblea(oblea, "oblea_562026196D.pdf", viaNameTree: false);

        Assert.Equal(eecc, Unwrap(cover));
    }

    [Theory]
    [InlineData("EECC AL 30-06-2025 (2).pdf", true)]
    [InlineData("eecc.pdf", true)]
    [InlineData("ESTADOS-CONTABLES.pdf", true)]
    [InlineData("Presentación Única de Balances", true)]
    [InlineData("Trabajo Profesional", true)]
    [InlineData("TRABAJO_PROFESIONAL.pdf", true)]
    [InlineData("Informe del Trabajo Profesional", false)]
    [InlineData("Informe", false)]
    [InlineData("INFORME DEL AUDITOR (1).pdf", false)]
    [InlineData("oblea_562026196D.pdf", false)]
    [InlineData("Certificación balance.pdf", false)]
    [InlineData("anexo.pdf", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsPreferredName_ClassifiesNames(string? name, bool expected)
    {
        Assert.Equal(expected, PdfAttachmentExtractor.IsPreferredName(name));
    }

    [Fact]
    public void Unwrap_AnnotationAttachmentBelowSizeThreshold_ReturnsOriginalBytes()
    {
        var tiny  = CreatePdf(pageCount: 1, pad: false);
        var oblea = CreateOblea(tiny, "oblea_1234.pdf", viaNameTree: false);

        Assert.True(tiny.Length < 10 * 1024, "fixture must be under the 10 KB threshold");

        var result = Unwrap(oblea);

        Assert.Same(oblea, result);
    }

    // ── Fixture helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// A standalone PDF. Compression is off so <paramref name="pad"/> reliably pushes
    /// the file past the extractor's size threshold instead of being flate-compressed away.
    /// </summary>
    private static byte[] CreatePdf(int pageCount, bool pad, int? padChars = null)
    {
        using var buffer = new MemoryStream();

        var properties = new WriterProperties().SetCompressionLevel(CompressionConstants.NO_COMPRESSION);

        using (var doc = new PdfDocument(new PdfWriter(buffer, properties)))
        {
            for (var i = 0; i < pageCount; i++)
                doc.AddNewPage();

            if (pad)
                doc.GetDocumentInfo().SetMoreInfo("Padding", new string('x', padChars ?? PaddingChars));
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// A 1-page cover sheet carrying <paramref name="attachment"/> either as a page
    /// /FileAttachment annotation (what the CPCEN obleas actually use) or in the
    /// document-level EmbeddedFiles name tree. <paramref name="extra"/> adds a second
    /// name-tree attachment, as the real obleas carry alongside the balance.
    /// Compression is off here too: a real oblea is about as large as the documents it
    /// carries (1.57 MB for the sample), whereas compressing the padding away would leave
    /// a container too small to clear the extractor's own size threshold when nested.
    /// </summary>
    private static byte[] CreateOblea(
        byte[]  attachment,
        string  fileName,
        bool    viaNameTree,
        byte[]? extra     = null,
        string  extraName = "extra.pdf")
    {
        using var buffer = new MemoryStream();

        var properties = new WriterProperties().SetCompressionLevel(CompressionConstants.NO_COMPRESSION);

        using (var doc = new PdfDocument(new PdfWriter(buffer, properties)))
        {
            var page = doc.AddNewPage();

            var spec = PdfFileSpec.CreateEmbeddedFileSpec(
                doc, attachment, "balance", fileName, PdfName.ApplicationPdf, null, null);

            if (viaNameTree)
                doc.AddFileAttachment(fileName, spec);
            else
                page.AddAnnotation(new PdfFileAttachmentAnnotation(new Rectangle(10, 10, 20, 20), spec));

            if (extra is not null)
                doc.AddFileAttachment(extraName, PdfFileSpec.CreateEmbeddedFileSpec(
                    doc, extra, "informe", extraName, PdfName.ApplicationPdf, null, null));
        }

        return buffer.ToArray();
    }

    /// <summary>Guards a fixture: the size rule alone must NOT be able to decide it.</summary>
    private static void AssertSizesAmbiguous(byte[] a, byte[] b)
    {
        var larger  = Math.Max(a.Length, b.Length);
        var relDiff = Math.Abs(a.Length - b.Length) / (double)larger;
        Assert.True(relDiff < 0.20, $"fixture sizes must be within 20 % (got {relDiff:P1})");
    }

    private static int CountNameTreeEmbeddedFiles(byte[] pdfBytes)
    {
        using var doc = PigDocument.Open(pdfBytes);
        return doc.Advanced.TryGetEmbeddedFiles(out var files) && files is not null
            ? files.Count
            : 0;
    }
}
