// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-03

using System.Net;
using System.Text;
using System.Text.Json;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.BalanceCorrections;
using Api.Web.FinanceIA.Tests.Legajo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// <see cref="ClaudeService.SuggestBalanceCorrectionsAsync"/> over canned envelopes and PDFs built here:
/// the request reuses the second pass's pages (wide pages also as halves, no PDF document block) and the
/// extraction model's spec; ONE attempt; the answer is read like the extraction's (stop_reason first, the
/// first TEXT block); every failure is a code, never an exception. Synthetic figures only.
/// </summary>
public sealed class ClaudeBalanceSuggestionRequestTests
{
    private static readonly BalanceSuggestionQuestion Question = new(
        2024,
        [new BalanceSuggestionField("current_liabilities", "Total Pasivo Corriente", 200_000d)],
        [new BalanceSuggestionItemGroup("current_liabilities_items", "rubros del Pasivo Corriente", [30_000d, 120_000d, 40_000d])]);

    private const string AnswerJson =
        """{"fields":[{"path":"current_liabilities","printed_value":"150000","matches_stored":false,"image":2,"half":"left","band":[0.4,0.44],"label":"Total Pasivo Corriente"}],"items":[]}""";

    [Fact]
    public async Task A_wide_page_goes_whole_and_as_halves_with_the_spec_of_the_extraction_model_and_no_document_block()
    {
        var (sut, handler, _) = Build(ExtractionModelCatalog.Sonnet55.Id, Ok(AnswerJson));

        var reply = await sut.SuggestBalanceCorrectionsAsync(
            LegajoPdfFactory.SizedPdf(842, 595), Question, CancellationToken.None);

        Assert.True(reply.Succeeded);
        var body = Assert.Single(handler.Captured);   // ONE request, one attempt
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // The 5.x spec: its max_tokens, NO temperature key, no thinking; the suggestion's own system prompt.
        Assert.Equal(ExtractionModelCatalog.Sonnet55.Id, root.GetProperty("model").GetString());
        Assert.Equal(ExtractionModelCatalog.Sonnet55.MaxTokens, root.GetProperty("max_tokens").GetInt32());
        Assert.False(root.TryGetProperty("temperature", out _));
        Assert.False(root.TryGetProperty("thinking", out _));
        Assert.Equal(ClaudeService.SuggestionSystemPrompt, root.GetProperty("system").GetString());

        // A landscape page: the page, its left half, its right half — then the question. No PDF block.
        var types = root.GetProperty("messages")[0].GetProperty("content").EnumerateArray()
            .Select(b => b.GetProperty("type").GetString()).ToArray();
        Assert.Equal(new[] { "image", "image", "image", "text" }, types);
        Assert.Equal(["full", "left", "right"], reply.Images.Select(i => i.Half));
        Assert.All(reply.Images, i => Assert.Equal(1, i.Page));
        Assert.Equal([1, 2, 3], reply.Images.Select(i => i.ImageNumber));

        var text = root.GetProperty("messages")[0].GetProperty("content")[3].GetProperty("text").GetString()!;
        Assert.Contains("page 1 is image 1 (full page), image 2 (its left half) and image 3 (its right half)", text);
        Assert.Contains(ClaudeService.TwoSidedSheetRule, text);
        Assert.Contains(ClaudeService.DashIsZeroRule, text);
        Assert.Contains("current_liabilities: usually printed as \"Total Pasivo Corriente\"; stored value 200000.", text);
        Assert.Contains("current_liabilities_items (rubros del Pasivo Corriente): stored lines [0] 30000; [1] 120000; [2] 40000.", text);
        Assert.Contains("closes in 2024", text);

        // The answer, read off the first text block; a quoted number is read too.
        Assert.Equal(150_000d, reply.Answer!.Fields!.Single().PrintedValue);
        Assert.Equal(ExtractionModelCatalog.Sonnet55.Id, reply.Model);
        Assert.Equal(10, reply.InputTokens);
        Assert.Equal(20, reply.OutputTokens);
        Assert.Equal(200, reply.HttpStatus);
    }

    [Fact]
    public async Task The_default_model_sends_temperature_zero_and_its_own_max_tokens()
    {
        var (sut, handler, _) = Build(null, Ok(AnswerJson));

        await sut.SuggestBalanceCorrectionsAsync(LegajoPdfFactory.NativePdf("Estado de situacion patrimonial"), Question, CancellationToken.None);

        using var doc = JsonDocument.Parse(Assert.Single(handler.Captured));
        Assert.Equal(0, doc.RootElement.GetProperty("temperature").GetInt32());
        Assert.Equal(ExtractionModelCatalog.Sonnet46.MaxTokens, doc.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal(ExtractionModelCatalog.Sonnet46.Id, doc.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task A_leading_thinking_block_is_skipped_and_fences_are_stripped()
    {
        var envelope = JsonSerializer.Serialize(new
        {
            content = new object[]
            {
                new { type = "thinking", thinking = "reasoning" },
                new { type = "text", text = "```json\n" + AnswerJson + "\n```" }
            },
            stop_reason = "end_turn",
            usage       = new { input_tokens = 5, output_tokens = 7 }
        });
        var (sut, _, _) = Build(null, (HttpStatusCode.OK, envelope));

        var reply = await sut.SuggestBalanceCorrectionsAsync(LegajoPdfFactory.NativePdf("x"), Question, CancellationToken.None);

        Assert.True(reply.Succeeded);
        Assert.Equal("Total Pasivo Corriente", reply.Answer!.Fields!.Single().Label);
    }

    [Theory]
    [InlineData("max_tokens", "max_tokens")]
    [InlineData("refusal", "refusal")]
    public async Task A_stop_reason_that_cuts_the_answer_is_a_failure_code_with_the_tokens(string stopReason, string code)
    {
        var envelope = JsonSerializer.Serialize(new
        {
            content     = new[] { new { type = "text", text = "{\"fields\":[" } },
            stop_reason = stopReason,
            usage       = new { input_tokens = 11, output_tokens = 32000 }
        });
        var (sut, handler, _) = Build(null, (HttpStatusCode.OK, envelope));

        var reply = await sut.SuggestBalanceCorrectionsAsync(LegajoPdfFactory.NativePdf("x"), Question, CancellationToken.None);

        Assert.False(reply.Succeeded);
        Assert.Equal(code, reply.FailureCode);
        Assert.True(reply.RequestSent);
        Assert.Equal(32000, reply.OutputTokens);
        Assert.Single(handler.Captured);   // never retried
    }

    [Fact]
    public async Task Prose_around_the_answer_is_dropped_and_logged_by_length_only()
    {
        // Synthetic prose; the Warning carries its lengths, never its text.
        var (sut, _, logger) = Build(null, Ok("Revisé las cifras pedidas:\n" + AnswerJson + "\nQuedo atento."));

        var reply = await sut.SuggestBalanceCorrectionsAsync(LegajoPdfFactory.NativePdf("x"), Question, CancellationToken.None);

        Assert.True(reply.Succeeded);
        Assert.Equal("Total Pasivo Corriente", reply.Answer!.Fields!.Single().Label);
        var warning = Assert.Single(logger.Messages, m => m.Contains("text around its JSON object", StringComparison.Ordinal));
        Assert.Contains("preamble_chars=27", warning, StringComparison.Ordinal);
        Assert.Contains("trailer_chars=13", warning, StringComparison.Ordinal);
        Assert.DoesNotContain(logger.Messages, m => m.Contains("Revisé", StringComparison.Ordinal) || m.Contains("Quedo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_answer_outside_the_schema_is_invalid_response()
    {
        var (sut, _, _) = Build(null, Ok("I could not find the figures."));

        var reply = await sut.SuggestBalanceCorrectionsAsync(LegajoPdfFactory.NativePdf("x"), Question, CancellationToken.None);

        Assert.Equal(ClaudeService.SuggestionInvalidResponse, reply.FailureCode);
    }

    [Fact]
    public async Task A_5xx_is_not_retried_and_its_body_is_never_logged()
    {
        var (sut, handler, logger) = Build(null, (HttpStatusCode.InternalServerError, "{\"error\":\"secret upstream body\"}"));

        var reply = await sut.SuggestBalanceCorrectionsAsync(LegajoPdfFactory.NativePdf("x"), Question, CancellationToken.None);

        Assert.Equal("http_500", reply.FailureCode);
        Assert.Equal(500, reply.HttpStatus);
        Assert.Single(handler.Captured);
        Assert.DoesNotContain(logger.Messages, m => m.Contains("secret upstream body", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_4xx_reply_carries_the_api_reason_but_the_log_holds_no_message()
    {
        var (sut, _, logger) = Build(null, (HttpStatusCode.BadRequest,
            "{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"secret upstream message\"},\"request_id\":\"req_s1\"}"));

        var reply = await sut.SuggestBalanceCorrectionsAsync(LegajoPdfFactory.NativePdf("x"), Question, CancellationToken.None);

        Assert.Equal("http_400", reply.FailureCode);
        Assert.Equal("Claude API error: HTTP 400 invalid_request_error: secret upstream message [req_s1].", reply.FailureDetail);
        Assert.DoesNotContain(logger.Messages, m => m.Contains("secret upstream message", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, m => m.Contains("req_s1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unrenderable_document_never_reaches_the_api()
    {
        var (sut, handler, _) = Build(null, Ok(AnswerJson));

        var reply = await sut.SuggestBalanceCorrectionsAsync("not a pdf"u8.ToArray(), Question, CancellationToken.None);

        Assert.Equal(ClaudeService.SuggestionRenderFailed, reply.FailureCode);
        Assert.False(reply.RequestSent);
        Assert.Empty(handler.Captured);
    }

    [Fact]
    public async Task The_logs_carry_counts_never_a_figure_or_a_label()
    {
        var (sut, _, logger) = Build(null, Ok(AnswerJson));

        await sut.SuggestBalanceCorrectionsAsync(LegajoPdfFactory.SizedPdf(842, 595), Question, CancellationToken.None);

        Assert.NotEmpty(logger.Messages);
        Assert.All(logger.Messages, m =>
        {
            Assert.DoesNotContain("200000", m);
            Assert.DoesNotContain("150000", m);
            Assert.DoesNotContain("Total Pasivo", m);
        });
    }

    [Fact]
    public void The_extraction_request_still_uses_the_extraction_prompt()
    {
        var json = ClaudeService.BuildExtractionRequestJson(ExtractionModelSelection.Default, []);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(ClaudeService.SystemPrompt, doc.RootElement.GetProperty("system").GetString());
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private static (ClaudeService Sut, Handler Handler, Logger Logger) Build(
        string? extractionModel, params (HttpStatusCode Status, string Body)[] responses)
    {
        var handler = new Handler(responses);
        var logger  = new Logger();
        var sut = new ClaudeService(
            new Factory(handler),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Anthropic:ApiKey"] = "test-key" })
                .Build(),
            logger,
            new BalanceIntegrityService(),
            Options.Create(new ExtractionModelOptions { ExtractionModel = extractionModel }),
            NullScanOrientationDetector.Instance);
        return (sut, handler, logger);
    }

    private static (HttpStatusCode, string) Ok(string text) => (HttpStatusCode.OK, JsonSerializer.Serialize(new
    {
        content     = new[] { new { type = "text", text } },
        stop_reason = "end_turn",
        usage       = new { input_tokens = 10, output_tokens = 20 }
    }));

    private sealed class Handler((HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
    {
        private int _calls;

        public List<string> Captured { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Captured.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            var (status, body) = responses[Math.Min(_calls++, responses.Length - 1)];
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Logger : ILogger<ClaudeService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Messages)
                Messages.Add(formatter(state, exception));
        }
    }
}
