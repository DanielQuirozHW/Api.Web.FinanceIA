// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-28

using System.Net;
using System.Text;
using System.Text.Json;
using Api.Web.FinanceIA.Clases.Exceptions;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Api.Web.FinanceIA.Models;
using Microsoft.Extensions.Options;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Covers the JSON ClaudeService.ChatAsync actually puts on the wire — the first tests in this
/// project to capture an OUTGOING request body rather than stub a response. That seam is what the
/// ephemeral-attachment feature needs: the whole guarantee is about the SHAPE of the final user
/// turn, and the interface-level CapturingClaudeService used by the controller tests sits above the
/// serialiser and therefore cannot see it.
///
/// Two invariants live here. First, a turn WITHOUT an attachment must serialise exactly as it did
/// before the parameter existed — asserted as a golden string, because a change that quietly
/// promoted every turn to the block form would still satisfy a shape assertion. Second, a turn WITH
/// one carries two text blocks, question first, and only on the FINAL turn: history stays plain
/// strings, which is what stops a spreadsheet being replayed on every later turn.
///
/// No live API call is made — the handler below answers every request, and the key is a fixture.
/// </summary>
public sealed class ClaudeChatPayloadTests
{
    // ── Fixtures ──────────────────────────────────────────────────────────────

    // The minimum envelope AnthropicResponse accepts: ChatAsync needs content[0].text to be
    // non-null and reads usage for the billing counts. Deserialised with NO options, so the
    // snake_case names below are load-bearing. Everything else Anthropic really returns (id,
    // model, role, stop_reason) is ignored, and is omitted so this fixture states exactly what
    // ChatAsync depends on.
    private const string OkEnvelope =
        """{"content":[{"type":"text","text":"respuesta del modelo"}],"usage":{"input_tokens":11,"output_tokens":22}}""";

    // ASCII-only, and free of every character JavaScriptEncoder.Default rewrites (< > & + ' and
    // every non-ASCII rune become \uXXXX). Deliberate: the invariant under test is the payload
    // SHAPE, and a golden full of ó would be unreadable while failing for encoder reasons that
    // have nothing to do with this code. Real es-AR text is covered by the ValueKind test below,
    // which does not depend on escaping at all.
    private const string GoldenNoAttachmentPayload =
        """{"model":"claude-sonnet-4-6","max_tokens":1000,"temperature":0,"system":"SYS","messages":[{"role":"user","content":"H1"},{"role":"assistant","content":"A1"},{"role":"user","content":"Q"}]}""";

    // The model every pre-existing test in this file passes, so the golden above still describes the
    // DEFAULT chat call rather than an arbitrary one. Read from the catalogue instead of repeated as
    // a literal: if the id ever changes there, this file must fail on the golden, not quietly keep
    // asserting a string nothing sends any more.
    private static readonly string Sonnet46 = ChatModelCatalog.Sonnet46.Id;
    private static readonly string Haiku45  = ChatModelCatalog.Haiku45.Id;
    private static readonly string Sonnet5  = ChatModelCatalog.Sonnet5.Id;

    private const string Markdown = "## Flujo\n\n| Mes | Monto |\n| --- | --- |\n| Enero | 1500 |\n";

    private static readonly IReadOnlyList<ConversationMessage> TwoTurnHistory =
    [
        new ConversationMessage { Role = "user",      Content = "H1" },
        new ConversationMessage { Role = "assistant", Content = "A1" }
    ];

    private static ClaudeService BuildSut(RecordingHandler handler) =>
        new(new StubHttpClientFactory(handler),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // The only key ChatAsync reads. No live call is made regardless: the handler
                    // answers every request before it can leave the process.
                    ["Anthropic:ApiKey"] = "test-key"
                })
                .Build(),
            NullLogger<ClaudeService>.Instance,
            new BalanceIntegrityService(),
            Options.Create(new ExtractionModelOptions()),
            NullScanOrientationDetector.Instance);

    private static JsonElement FinalTurn(string requestJson, out JsonDocument doc)
    {
        doc = JsonDocument.Parse(requestJson);
        var messages = doc.RootElement.GetProperty("messages");
        return messages[messages.GetArrayLength() - 1];
    }

    // ── No attachment: the payload must not move ──────────────────────────────

    [Fact]
    public async Task ChatAsync_without_an_attachment_sends_a_byte_identical_payload()
    {
        // THE regression gate for the ephemeral-attachment change. An ordinary turn must serialise
        // to exactly what it always did: same key order (anonymous-type declaration order), the
        // literal snake_case max_tokens (SerializerOptions sets PropertyNamingPolicy = null), and
        // content as a plain STRING rather than a one-element block array.
        var handler = new RecordingHandler(OkEnvelope);

        await BuildSut(handler).ChatAsync(Sonnet46, "SYS", TwoTurnHistory, "Q", CancellationToken.None);

        Assert.Equal(GoldenNoAttachmentPayload, handler.CapturedRequestJson);
    }

    [Fact]
    public async Task ChatAsync_leaves_the_final_turn_content_a_plain_string_without_an_attachment()
    {
        // The same invariant expressed structurally, so it survives any future, unrelated addition
        // to the payload that would legitimately move the golden. Uses the accented es-AR the
        // golden deliberately avoids, proving the shape holds independently of encoder escaping.
        var handler = new RecordingHandler(OkEnvelope);

        await BuildSut(handler).ChatAsync(
            Sonnet46, "Sos un analista financiero senior.", TwoTurnHistory, "¿Cómo fue el flujo?",
            CancellationToken.None);

        var final = FinalTurn(handler.CapturedRequestJson!, out var doc);
        using (doc)
        {
            Assert.Equal(JsonValueKind.String, final.GetProperty("content").ValueKind);
            Assert.Equal("¿Cómo fue el flujo?", final.GetProperty("content").GetString());
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t")]
    public async Task ChatAsync_treats_a_blank_attachment_as_absent(string? attachment)
    {
        // IsNullOrWhiteSpace, not IsNullOrEmpty: a Portal that posts "" or a stray newline must not
        // silently switch the turn to the block form and start paying for the framing text.
        var handler = new RecordingHandler(OkEnvelope);

        await BuildSut(handler).ChatAsync(
            Sonnet46, "SYS", TwoTurnHistory, "Q", CancellationToken.None, attachmentMarkdown: attachment);

        Assert.Equal(GoldenNoAttachmentPayload, handler.CapturedRequestJson);
    }

    // ── With an attachment: two text blocks on the final turn ─────────────────

    [Fact]
    public async Task ChatAsync_with_an_attachment_sends_two_text_blocks_on_the_final_turn()
    {
        var handler = new RecordingHandler(OkEnvelope);

        await BuildSut(handler).ChatAsync(
            Sonnet46, "SYS", TwoTurnHistory, "¿Cierra el flujo?", CancellationToken.None,
            attachmentMarkdown: Markdown);

        var final = FinalTurn(handler.CapturedRequestJson!, out var doc);
        using (doc)
        {
            var content = final.GetProperty("content");

            Assert.Equal(JsonValueKind.Array, content.ValueKind);
            Assert.Equal(2, content.GetArrayLength());
            Assert.Equal("text", content[0].GetProperty("type").GetString());
            Assert.Equal("text", content[1].GetProperty("type").GetString());

            // ORDER is the assertion: the analyst's question first, so the model reads it as the
            // request rather than as a caption on the spreadsheet underneath it.
            Assert.Equal("¿Cierra el flujo?", content[0].GetProperty("text").GetString());

            // Asserted against the production constant, never a copy of the literal —
            // InternalsVisibleTo exists so a reworded framing breaks the build, not the meaning.
            Assert.Equal(ClaudeService.ChatAttachmentFraming + Markdown,
                         content[1].GetProperty("text").GetString());
        }
    }

    [Fact]
    public async Task ChatAsync_sends_the_system_prompt_and_the_attachment_with_bare_line_feeds()
    {
        // Defence in depth behind the controller. A CRLF system prompt (AppendLine on Windows) or CRLF
        // attachment Markdown would otherwise reach the model with mixed endings, because the framing
        // ends in a literal "\n\n". Both inputs carry a CR here, so this fails on Linux as well.
        var handler = new RecordingHandler(OkEnvelope);

        await BuildSut(handler).ChatAsync(
            Sonnet46, "SYS1\r\nSYS2\rSYS3", TwoTurnHistory, "Q", CancellationToken.None,
            attachmentMarkdown: "| Mes | Monto |\r\n| --- | --- |\r\n");

        Assert.DoesNotContain("\\r", handler.CapturedRequestJson!, StringComparison.Ordinal);

        var final = FinalTurn(handler.CapturedRequestJson!, out var doc);
        using (doc)
        {
            Assert.Equal("SYS1\nSYS2\nSYS3", doc.RootElement.GetProperty("system").GetString());
            Assert.Equal(ClaudeService.ChatAttachmentFraming + "| Mes | Monto |\n| --- | --- |\n",
                         final.GetProperty("content")[1].GetProperty("text").GetString());
        }
    }

    [Fact]
    public void The_framing_presents_the_attachment_as_data_and_not_as_instructions()
    {
        // The prompt-injection guarantee, stated where a reviewer can check it. A spreadsheet cell
        // is free text, so the framing has to say out loud that what follows is data. Pinning the
        // two load-bearing words rather than the whole sentence leaves the wording free to improve.
        Assert.Contains("DATOS", ClaudeService.ChatAttachmentFraming, StringComparison.Ordinal);
        Assert.Contains("NO instrucciones", ClaudeService.ChatAttachmentFraming, StringComparison.Ordinal);

        // Ends with a blank line so the Markdown's first "## Hoja" still renders as a heading.
        Assert.EndsWith("\n\n", ClaudeService.ChatAttachmentFraming, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChatAsync_leaves_history_turns_plain_strings_when_the_turn_has_an_attachment()
    {
        // Only the FINAL turn is ever promoted. A history turn that became a block array would be a
        // change to what the model is told about the PAST, not just about this turn — and it is the
        // reason an attachment can never compound across a conversation.
        var handler = new RecordingHandler(OkEnvelope);

        await BuildSut(handler).ChatAsync(
            Sonnet46, "SYS", TwoTurnHistory, "Q", CancellationToken.None, attachmentMarkdown: Markdown);

        using var doc = JsonDocument.Parse(handler.CapturedRequestJson!);
        var messages = doc.RootElement.GetProperty("messages");

        Assert.Equal(3, messages.GetArrayLength());
        Assert.Equal(JsonValueKind.String, messages[0].GetProperty("content").ValueKind);
        Assert.Equal(JsonValueKind.String, messages[1].GetProperty("content").ValueKind);
        Assert.Equal(JsonValueKind.Array,  messages[2].GetProperty("content").ValueKind);
    }

    // ── Per-model call shape ──────────────────────────────────────────────────
    //
    // The chat model became a parameter so a tenant could choose one, and the assumption that came
    // with that change — "swapping the model string is the only difference" — is FALSE for Claude
    // Sonnet 5. These tests pin the two ways it differs, because both fail at runtime as an opaque
    // 502 rather than as anything a reader of ClaudeService would notice.

    [Fact]
    public async Task ChatAsync_omits_temperature_entirely_for_a_model_that_rejects_it()
    {
        // THE reason ChatAsync builds two payload shapes. Claude Sonnet 5 answers HTTP 400 to any
        // non-default sampling parameter, and temperature = 0 — which CLAUDE.md states as a rule and
        // which every other Claude call here sends — is a non-default value. The field has to be
        // ABSENT, not null: "temperature":null fails the same way.
        var handler = new RecordingHandler(OkEnvelope);

        await BuildSut(handler).ChatAsync(Sonnet5, "SYS", TwoTurnHistory, "Q", CancellationToken.None);

        using var doc = JsonDocument.Parse(handler.CapturedRequestJson!);

        Assert.False(doc.RootElement.TryGetProperty("temperature", out _));
        Assert.Equal("claude-sonnet-5", doc.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task ChatAsync_gives_a_thinking_model_room_beyond_the_thousand_tokens_the_others_get()
    {
        // max_tokens caps thinking and answer TOGETHER, and Sonnet 5 runs adaptive thinking by
        // default at effort high. The 1000 that has always sufficed for the non-thinking models would
        // be spent reasoning before a word of the answer was written, returning a truncated reply or
        // a response with no text block at all.
        var handler = new RecordingHandler(OkEnvelope);

        await BuildSut(handler).ChatAsync(Sonnet5, "SYS", TwoTurnHistory, "Q", CancellationToken.None);

        using var doc = JsonDocument.Parse(handler.CapturedRequestJson!);

        Assert.Equal(ChatModelCatalog.Sonnet5.MaxTokens, doc.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.True(doc.RootElement.GetProperty("max_tokens").GetInt32() > 1000);
    }

    [Fact]
    public async Task ChatAsync_keeps_temperature_zero_for_a_model_that_accepts_it()
    {
        // The other half of the branch: switching model must not quietly drop temperature for the two
        // models whose determinism depends on it. Haiku rather than Sonnet 4.6 so this is not just
        // the golden test again.
        var handler = new RecordingHandler(OkEnvelope);

        await BuildSut(handler).ChatAsync(Haiku45, "SYS", TwoTurnHistory, "Q", CancellationToken.None);

        using var doc = JsonDocument.Parse(handler.CapturedRequestJson!);

        Assert.Equal(0, doc.RootElement.GetProperty("temperature").GetDecimal());
        Assert.Equal(1000, doc.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal("claude-haiku-4-5", doc.RootElement.GetProperty("model").GetString());
    }

    [Theory]
    [InlineData("gpt-4")]
    [InlineData("claude-sonnet-4-6-20251114")]   // a real id with a date suffix appended — not a model
    [InlineData("claude-opus-5")]                 // real model, not in this project's catalogue
    public async Task ChatAsync_refuses_a_model_outside_the_catalogue_without_calling_the_api(string model)
    {
        // Defence in depth, not the allow-list check — that is the controller's. What this protects
        // is narrower: the catalogue is what says how a request for a given model must be SHAPED, so
        // an unknown id has no defensible payload and must not reach the wire on a guess.
        var handler = new RecordingHandler(OkEnvelope);

        await Assert.ThrowsAsync<ClaudeApiException>(() =>
            BuildSut(handler).ChatAsync(model, "SYS", TwoTurnHistory, "Q", CancellationToken.None));

        Assert.Null(handler.CapturedRequestJson);
    }

    // ── Reading the reply out of the envelope ─────────────────────────────────

    [Fact]
    public async Task ChatAsync_reads_past_a_leading_thinking_block_to_the_text_one()
    {
        // The second thing that made Sonnet 5 unusable. A thinking model puts a `thinking` block
        // AHEAD of its answer, and a thinking block has no `text` property at all — so the old
        // content[0] read returned null and threw on a response that was valid and already paid for.
        // Every turn would have surfaced to the analyst as a 502.
        const string ThinkingFirstEnvelope =
            """{"content":[{"type":"thinking","thinking":""},{"type":"text","text":"la respuesta"}],"usage":{"input_tokens":11,"output_tokens":22}}""";

        var result = await BuildSut(new RecordingHandler(ThinkingFirstEnvelope))
            .ChatAsync(Sonnet5, "SYS", TwoTurnHistory, "Q", CancellationToken.None);

        Assert.Equal("la respuesta", result.Text);
        Assert.Equal(11, result.InputTokens);
    }

    [Fact]
    public async Task ChatAsync_still_fails_when_the_response_carries_no_text_block_at_all()
    {
        // Reading past a thinking block must not become "accept anything". A response that thought
        // and then ran out of max_tokens before writing an answer has nothing to persist, and a
        // silent empty assistant turn would be worse than the error the caller already handles.
        const string ThinkingOnlyEnvelope =
            """{"content":[{"type":"thinking","thinking":""}],"usage":{"input_tokens":11,"output_tokens":22}}""";

        await Assert.ThrowsAsync<ClaudeApiException>(() =>
            BuildSut(new RecordingHandler(ThinkingOnlyEnvelope))
                .ChatAsync(Sonnet5, "SYS", TwoTurnHistory, "Q", CancellationToken.None));
    }

    [Fact]
    public async Task ChatAsync_returns_the_reply_and_usage_from_the_envelope()
    {
        // Proves OkEnvelope really is the minimum AnthropicResponse accepts, so a later reader does
        // not "helpfully" pad it with id/model/stop_reason and obscure what ChatAsync depends on.
        var result = await BuildSut(new RecordingHandler(OkEnvelope))
            .ChatAsync(Sonnet46, "SYS", TwoTurnHistory, "Q", CancellationToken.None);

        Assert.Equal("respuesta del modelo", result.Text);
        Assert.Equal(11, result.InputTokens);
        Assert.Equal(22, result.OutputTokens);
    }

    // ── Stubs ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Records the OUTGOING request body — the only way to prove what actually reaches the
    /// Anthropic API — and answers with a fixed valid envelope so ChatAsync completes normally.
    /// </summary>
    private sealed class RecordingHandler(string responseBody) : HttpMessageHandler
    {
        public string? CapturedRequestJson { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Read INSIDE SendAsync: ClaudeService disposes the HttpRequestMessage the moment
            // SendAsync returns, taking the content stream this body lives in with it.
            CapturedRequestJson = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }
}
