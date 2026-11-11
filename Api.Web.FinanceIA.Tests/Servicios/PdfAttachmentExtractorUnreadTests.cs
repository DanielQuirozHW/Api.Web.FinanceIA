// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-03

using Api.Web.FinanceIA.Servicios;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Api.Web.FinanceIA.Tests.Servicios.AttachmentPdfBuilder;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// <see cref="EmbeddedBalanceUnwrap.AttachmentUnread"/>: raised only when the OUTER document is
/// returned although it carries embedded files that were lost — never for a plain PDF or a normal
/// selection — and a failed read never lets the auditor's report stand in for the balance.
/// </summary>
public sealed class PdfAttachmentExtractorUnreadTests
{
    private static readonly Guid DocumentId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static EmbeddedBalanceUnwrap Unwrap(byte[] outer) =>
        PdfAttachmentExtractor.Unwrap(outer, NullLogger<PdfAttachmentExtractorUnreadTests>.Instance, DocumentId, "tenant-test");

    [Fact]
    public void A_plain_pdf_is_not_flagged()
    {
        var plain = Pdf();

        var result = Unwrap(plain);

        Assert.Same(plain, result.Bytes);
        Assert.False(result.AttachmentUnread);
        Assert.Equal(0, result.AttachmentsSeen);
        Assert.Null(result.FailureType);
    }

    [Fact]
    public void A_normal_selection_by_name_is_not_flagged()
    {
        var balance = Pdf(padChars: PaddingChars);
        var informe = Pdf(padChars: PaddingChars + 1024);
        var outer   = Wrapper(("Balance.pdf", balance), ("Informe del auditor.pdf", informe));

        var result = Unwrap(outer);

        Assert.Equal(balance, result.Bytes);
        Assert.False(result.AttachmentUnread);
        Assert.Equal(2, result.AttachmentsSeen);
    }

    [Fact]
    public void Ambiguous_sizes_with_no_statements_name_keep_the_outer_and_are_flagged()
    {
        var outer = Wrapper(("anexo_a.pdf", Pdf(padChars: PaddingChars)), ("anexo_b.pdf", Pdf(padChars: PaddingChars + 1024)));

        var result = Unwrap(outer);

        Assert.Same(outer, result.Bytes);
        Assert.True(result.AttachmentUnread);
        Assert.Equal(2, result.AttachmentsSeen);
        Assert.Null(result.FailureType); // nothing threw: the choice was ambiguous
    }

    [Fact]
    public void A_statements_named_attachment_that_does_not_qualify_is_flagged()
    {
        var tiny  = Pdf(pageCount: 1, padChars: 0);
        var outer = Wrapper(("Balance.pdf", tiny));

        Assert.True(tiny.Length < 10 * 1024, "fixture must be under the 10 KB threshold");

        var result = Unwrap(outer);

        Assert.Same(outer, result.Bytes);
        Assert.True(result.AttachmentUnread);
    }

    [Fact]
    public void A_small_attachment_not_named_as_the_statements_is_not_flagged()
    {
        var outer = Wrapper(("firma.pdf", Pdf(pageCount: 1, padChars: 0)));

        var result = Unwrap(outer);

        Assert.Same(outer, result.Bytes);
        Assert.False(result.AttachmentUnread);
    }

    /// <summary>
    /// The balance cannot be decoded and the auditor's report can. Today's single-candidate rule would
    /// analyse the report; the failure guard keeps the outer document and flags it instead.
    /// </summary>
    [Fact]
    public void A_failed_balance_never_lets_the_lone_report_be_analysed()
    {
        var outer = Wrapper(("Balance.pdf", null), ("Informe del auditor.pdf", Pdf()));

        var result = Unwrap(outer);

        Assert.Same(outer, result.Bytes);
        Assert.True(result.AttachmentUnread);
        Assert.Equal(2, result.AttachmentsSeen);
        Assert.Equal(nameof(NotSupportedException), result.FailureType);
    }

    [Fact]
    public void A_failed_report_still_lets_the_named_balance_be_taken()
    {
        var balance = Pdf();
        var outer   = Wrapper(("Balance.pdf", balance), ("Informe del auditor.pdf", null));

        var result = Unwrap(outer);

        Assert.Equal(balance, result.Bytes);
        Assert.False(result.AttachmentUnread);
    }

    [Fact]
    public void The_bytes_only_api_returns_the_same_bytes()
    {
        var outer = Wrapper(("anexo_a.pdf", Pdf(padChars: PaddingChars)), ("anexo_b.pdf", Pdf(padChars: PaddingChars + 1024)));

        Assert.Same(outer, PdfAttachmentExtractor.UnwrapEmbeddedBalance(
            outer, NullLogger<PdfAttachmentExtractorUnreadTests>.Instance, DocumentId, "tenant-test"));
    }
}
