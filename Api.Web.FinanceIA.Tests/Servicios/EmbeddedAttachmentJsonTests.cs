// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-03

using System.Text.Json;
using Api.Web.FinanceIA.Servicios;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// <see cref="EmbeddedAttachmentJson.MarkUnread"/> adds <c>integrity.extraction.embeddedAttachment</c>
/// and nothing else, and returns any input it cannot place the key in UNCHANGED.
/// </summary>
public sealed class EmbeddedAttachmentJsonTests
{
    private const string WithExtraction =
        """{"assets":1000,"integrity":{"passed":true,"checks":[],"extraction":{"method":"document","secondPassRan":false,"keptPass":"first","keptReason":"only_pass"}}}""";

    [Fact]
    public void Marks_the_extraction_node_and_keeps_everything_else()
    {
        var marked = EmbeddedAttachmentJson.MarkUnread(WithExtraction)!;

        using var doc = JsonDocument.Parse(marked);
        var extraction = doc.RootElement.GetProperty("integrity").GetProperty("extraction");
        Assert.Equal("unread", extraction.GetProperty("embeddedAttachment").GetString());
        Assert.Equal("first", extraction.GetProperty("keptPass").GetString());
        Assert.Equal(1000, doc.RootElement.GetProperty("assets").GetInt32());

        // Nothing but the one key was added.
        Assert.Equal(WithExtraction.Replace("\"only_pass\"}", "\"only_pass\",\"embeddedAttachment\":\"unread\"}"), marked);
    }

    [Theory]
    [InlineData("""{"assets":1000}""")]                                      // pre-2.18: no integrity node
    [InlineData("""{"assets":1000,"integrity":null}""")]                     // the integrity evaluation threw
    [InlineData("""{"assets":1000,"integrity":{"passed":true,"checks":[]}}""")] // no extraction node
    [InlineData("""{"assets": """)]                                          // malformed
    [InlineData("[]")]
    [InlineData("")]
    [InlineData(null)]
    public void Returns_any_other_input_unchanged(string? balanceJson)
    {
        Assert.Same(balanceJson, EmbeddedAttachmentJson.MarkUnread(balanceJson));
    }

    [Fact]
    public void An_orphaned_source_alone_adds_only_its_key()
    {
        var marked = EmbeddedAttachmentJson.Mark(WithExtraction, unread: false, fromOrphanedFileSpec: true)!;

        Assert.Equal(
            WithExtraction.Replace("\"only_pass\"}", "\"only_pass\",\"embeddedAttachmentSource\":\"orphaned_filespec\"}"),
            marked);
    }

    [Fact]
    public void Unread_and_orphaned_source_are_both_written_in_that_order()
    {
        var marked = EmbeddedAttachmentJson.Mark(WithExtraction, unread: true, fromOrphanedFileSpec: true)!;

        Assert.Equal(
            WithExtraction.Replace(
                "\"only_pass\"}",
                "\"only_pass\",\"embeddedAttachment\":\"unread\",\"embeddedAttachmentSource\":\"orphaned_filespec\"}"),
            marked);
    }

    [Fact]
    public void Neither_flag_returns_the_same_string()
    {
        Assert.Same(WithExtraction, EmbeddedAttachmentJson.Mark(WithExtraction, unread: false, fromOrphanedFileSpec: false));
    }

    [Theory]
    [InlineData("""{"assets":1000}""")]
    [InlineData("""{"assets":1000,"integrity":null}""")]
    [InlineData("""{"assets": """)]
    [InlineData(null)]
    public void The_source_leaves_any_other_input_unchanged(string? balanceJson)
    {
        Assert.Same(balanceJson, EmbeddedAttachmentJson.Mark(balanceJson, unread: false, fromOrphanedFileSpec: true));
    }

    [Fact]
    public void The_anomaly_is_es_AR_and_carries_no_percent_sign()
    {
        Assert.Equal(
            "El PDF trae el balance como archivo adjunto y no se pudo abrir. Subí el balance directamente.",
            EmbeddedAttachmentJson.UnreadAnomaly);
        Assert.DoesNotContain("%", EmbeddedAttachmentJson.UnreadAnomaly);
    }
}
