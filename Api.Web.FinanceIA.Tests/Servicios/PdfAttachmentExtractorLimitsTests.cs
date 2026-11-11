// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-08

using System.Text;
using Api.Web.FinanceIA.Servicios;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Tokens;
using Xunit;
using static Api.Web.FinanceIA.Tests.Servicios.AttachmentPdfBuilder;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// K24: every decode is bounded by <see cref="PdfAttachmentExtractor.MaxDecodedAttachmentBytes"/> and a LIVE
/// attachment must carry the %PDF header. An attachment over the cap, or a ".pdf" one that is not a PDF,
/// behaves like one that failed to decode at that site.
/// </summary>
public sealed class PdfAttachmentExtractorLimitsTests
{
    private static readonly Guid DocumentId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private const string TenantId = "tenant-test";
    private const long Cap = PdfAttachmentExtractor.MaxDecodedAttachmentBytes;

    private static EmbeddedBalanceUnwrap Unwrap(byte[] outer, ILogger? logger = null) =>
        PdfAttachmentExtractor.Unwrap(
            outer, logger ?? NullLogger<PdfAttachmentExtractorLimitsTests>.Instance, DocumentId, TenantId);

    /// <summary>%PDF header followed by zeros, <paramref name="length"/> bytes: a PDF by its header only.</summary>
    private static byte[] PdfHeaderThenZeros(long length)
    {
        var data   = new byte[length];
        var header = "%PDF-1.7\n"u8;
        header.CopyTo(data);
        return data;
    }

    // ── Decoded size cap ──────────────────────────────────────────────────────

    [Fact]
    public void A_name_tree_flate_stream_inflating_past_the_cap_is_skipped_and_flagged()
    {
        var outer  = RawWrapper(("Balance.pdf", Flate(PdfHeaderThenZeros(Cap + 1))));
        var logger = new CapturingLogger();

        var result = Unwrap(outer, logger);

        Assert.Same(outer, result.Bytes);
        Assert.True(result.AttachmentUnread);
        Assert.False(result.FromOrphanedFileSpec);
        Assert.Equal(PdfAttachmentExtractor.AttachmentTooLargeFailure, result.FailureType);
        var warning = Assert.Single(logger.Warnings, m => m.Contains("skipped for its size"));
        Assert.Contains("name_tree", warning);
        Assert.Contains(TenantId, warning);
        Assert.Contains(DocumentId.ToString(), warning);
        Assert.DoesNotContain("Balance", warning);
    }

    [Fact]
    public void An_orphan_inflating_past_the_cap_does_not_qualify()
    {
        var outer = OrphanedCatalogWrapper(
            [new OrphanAttachment("Trabajo Profesional", Data: null) { Raw = Flate(PdfHeaderThenZeros(Cap + 1)) }]);
        var logger = new CapturingLogger();

        var result = Unwrap(outer, logger);

        Assert.Same(outer, result.Bytes);
        Assert.True(result.AttachmentUnread);
        Assert.True(result.FromOrphanedFileSpec);
        Assert.Equal(PdfAttachmentExtractor.AttachmentTooLargeFailure, result.FailureType);
        Assert.Contains(logger.Warnings, m => m.Contains("skipped for its size") && m.Contains("orphan"));
    }

    [Fact]
    public void An_annotation_inflating_past_the_cap_is_flagged_without_reaching_the_orphan_walk()
    {
        var outer  = AnnotationWrapper("Balance.pdf", Flate(PdfHeaderThenZeros(Cap + 1)));
        var logger = new CapturingLogger();

        var result = Unwrap(outer, logger);

        Assert.Same(outer, result.Bytes);
        Assert.True(result.AttachmentUnread);
        Assert.False(result.FromOrphanedFileSpec);
        var warning = Assert.Single(logger.Warnings, m => m.Contains("skipped for its size"));
        Assert.Contains("annotation", warning);
    }

    /// <summary>
    /// The stream cannot be decoded at all (unknown filter): only skipping it on its declared size, without
    /// decoding, yields AttachmentTooLarge rather than NotSupportedException.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_declared_size_over_the_cap_is_skipped_without_decoding(bool viaParamsSize)
    {
        var raw = new RawStream(
            new byte[2048],
            Filter:        "NoSuchDecode",
            DeclaredSize:  viaParamsSize ? Cap + 1 : null,
            DecodedLength: viaParamsSize ? null : Cap + 1);
        var outer  = RawWrapper(("Balance.pdf", raw));
        var logger = new CapturingLogger();

        var result = Unwrap(outer, logger);

        Assert.Same(outer, result.Bytes);
        Assert.True(result.AttachmentUnread);
        Assert.Equal(PdfAttachmentExtractor.AttachmentTooLargeFailure, result.FailureType);
        Assert.Contains(logger.Warnings, m => m.Contains("skipped for its size") && m.Contains("declared"));
    }

    [Fact]
    public void An_attachment_exactly_at_the_cap_is_read()
    {
        var inner = PdfHeaderThenZeros(Cap);
        var outer = RawWrapper(("Balance.pdf", Flate(inner, declaredSize: Cap, decodedLength: Cap)));

        var result = Unwrap(outer);

        Assert.False(result.AttachmentUnread);
        Assert.Equal(Cap, result.Bytes.LongLength);
        Assert.True(result.Bytes.AsSpan().SequenceEqual(inner));
    }

    [Fact]
    public void A_normal_flate_attachment_is_read_as_before()
    {
        var balance = Pdf();
        var outer   = RawWrapper(("Balance.pdf", Flate(balance, declaredSize: balance.Length)));

        var result = Unwrap(outer);

        Assert.Equal(balance, result.Bytes);
        Assert.False(result.AttachmentUnread);
    }

    // ── The bounded inflate is PdfPig's FlateFilter ───────────────────────────

    public static TheoryData<string> InflateCases => new() { "valid", "garbage", "truncated", "header_only", "empty" };

    [Theory]
    [MemberData(nameof(InflateCases))]
    public void The_bounded_inflate_matches_pdfpig_byte_for_byte(string inflateCase)
    {
        var valid   = Zlib(Pdf());
        var encoded = inflateCase switch
        {
            "valid"       => valid,
            "garbage"     => Encoding.ASCII.GetBytes(new string('z', 4096)),
            "truncated"   => valid[..(valid.Length / 2)],
            "header_only" => valid[..2],
            _             => [],
        };

        var dictionary = new DictionaryToken(new Dictionary<NameToken, IToken>
        {
            [NameToken.Create("Filter")] = NameToken.Create("FlateDecode"),
        });
        var expected = new StreamToken(dictionary, encoded).Decode(DefaultFilterProvider.Instance).ToArray();

        Assert.Null(PdfAttachmentExtractor.InflateBounded(encoded, out var actual));
        Assert.Equal(expected, actual);
    }

    // ── A live attachment must be a PDF ───────────────────────────────────────

    [Fact]
    public void A_live_balance_pdf_that_is_not_a_pdf_is_flagged_and_the_cover_kept()
    {
        var outer = Wrapper(("Balance.pdf", NotAPdf()));

        var result = Unwrap(outer);

        Assert.Same(outer, result.Bytes);
        Assert.True(result.AttachmentUnread);
        Assert.False(result.FromOrphanedFileSpec);
    }

    /// <summary>The not-a-PDF ".pdf" counts as unreadable, so the lone report is not analysed in its place.</summary>
    [Fact]
    public void A_live_pdf_name_that_is_not_a_pdf_never_lets_another_attachment_be_taken()
    {
        var outer = Wrapper(("Anexo.pdf", NotAPdf()), ("Informe del auditor.pdf", Pdf()));

        var result = Unwrap(outer);

        Assert.Same(outer, result.Bytes);
        Assert.True(result.AttachmentUnread);
    }

    [Fact]
    public void A_live_attachment_whose_header_sits_within_the_first_1024_bytes_is_selected()
    {
        var balance = new byte[200].Concat(Pdf()).ToArray();
        var outer   = Wrapper(("Balance.pdf", balance));

        var result = Unwrap(outer);

        Assert.Equal(balance, result.Bytes);
        Assert.False(result.AttachmentUnread);
    }

    [Fact]
    public void A_header_past_the_first_1024_bytes_does_not_count()
    {
        Assert.True(PdfAttachmentExtractor.HasPdfHeader(new byte[1020].Concat("%PDF"u8.ToArray()).ToArray()));
        Assert.False(PdfAttachmentExtractor.HasPdfHeader(new byte[1021].Concat("%PDF"u8.ToArray()).ToArray()));
    }

    // ── Annotation decode failure ─────────────────────────────────────────────

    [Fact]
    public void An_annotation_that_cannot_be_decoded_is_flagged_unread_and_never_called_an_orphan()
    {
        var outer = AnnotationWrapper("Balance.pdf", new RawStream(new byte[2048], Filter: "NoSuchDecode"));

        var result = Unwrap(outer);

        Assert.Same(outer, result.Bytes);
        Assert.True(result.AttachmentUnread);
        Assert.False(result.FromOrphanedFileSpec);
        Assert.Equal(1, result.AttachmentsSeen);
        Assert.Equal(nameof(NotSupportedException), result.FailureType);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel                         logLevel,
            EventId                          eventId,
            TState                           state,
            Exception?                       exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }
}
