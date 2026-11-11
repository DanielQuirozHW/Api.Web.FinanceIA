// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-04

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Clases.Exceptions;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// The extraction reads the JSON object of the answer even when the model writes prose around it
/// (<see cref="ClaudeService.ExtractJsonObject"/>), every reader of that answer reads the same object, and an
/// answer with no readable JSON object fails with its own code (<see cref="ClaudeService.AnswerUnparseable"/>),
/// a diagnostic that locates the fault without quoting the answer, and a client text that is not "unavailable".
/// Every answer here is SYNTHETIC: no real model output, figure or name.
/// </summary>
public sealed class ClaudeExtractionJsonExtractionTests
{
    private static readonly byte[] FakePdf = "%PDF-1.4 fixture"u8.ToArray();

    // A clean, synthetic answer with braces inside a string value and a cash_flow block (read in isolation).
    private const string CleanAnswer =
        "{\"ratios\": {}, \"alerts\": [], \"anomalies\": [\"Observación sintética.\"], \"executive_summary\": \"Resumen {sintético} con llaves.\", " +
        "\"confidence_score\": 0.9, \"fiscal_year_detected\": 2025, \"amounts_unit\": \"miles\", " +
        "\"balance_check\": { \"assets\": 1000, \"current_assets\": 600, \"non_current_assets\": 400, \"liabilities\": 700, " +
        "\"current_liabilities\": 500, \"non_current_liabilities\": 200, \"equity\": 300, \"net_revenue\": 2000, \"net_profit\": 100, " +
        "\"balanced\": true, \"difference_pct\": 0 }, " +
        "\"cash_flow\": { \"present\": true, \"method\": \"indirect\", \"cash_beginning\": 40, \"cash_end\": 55, \"net_change\": 15, " +
        "\"operating\": 30, \"investing\": -10, \"financing\": -5 }}";

    // SHA-256 of the BalanceJson CleanAnswer produced on DEVELOP 6f1a48f, BEFORE ExtractJsonObject existed.
    // A clean answer must keep producing exactly these bytes.
    private const string CleanBalanceJsonSha256 = "73beb7a794a17790d749da606076c74fb7453bf40e31bf6c39d1a1d50e872b3f";

    private const string Preamble = "Aquí está el análisis del balance solicitado:\n\n";
    private const string Trailer  = "\n\nQuedo a disposición por cualquier consulta.";

    // The stubbed RESPONSE, not the request: no pinned InputHash depends on it. It carries one core total
    // (net_revenue) because an answer with none is a file with no financial statements, which the
    // extraction now fails (claude_no_financial_statements). A lone net_revenue runs no structure check.
    private const string OkModelJson =
        """{"ratios":{},"alerts":[],"anomalies":[],"executive_summary":"R.","confidence_score":0.9,"fiscal_year_detected":2025,"balance_check":{"net_revenue":1000,"balanced":true,"difference_pct":0}}""";

    // ── ExtractJsonObject ─────────────────────────────────────────────────────

    [Fact]
    public void A_clean_object_comes_back_unchanged_and_untrimmed()
    {
        var extracted = ClaudeService.ExtractJsonObject(CleanAnswer);

        Assert.Equal(CleanAnswer, extracted.Json);
        Assert.False(extracted.IsTrimmed);
        Assert.Null(extracted.Error);
    }

    [Fact]
    public void A_prose_preamble_is_dropped_and_measured_in_characters()
    {
        var extracted = ClaudeService.ExtractJsonObject(Preamble + CleanAnswer);

        Assert.Equal(CleanAnswer, extracted.Json);
        Assert.Equal(Preamble.Length, extracted.PreambleLength);   // chars, not UTF-8 bytes
        Assert.Equal(0, extracted.TrailerLength);
        Assert.True(extracted.IsTrimmed);
    }

    [Fact]
    public void Trailing_prose_is_dropped()
    {
        var extracted = ClaudeService.ExtractJsonObject(CleanAnswer + Trailer);

        Assert.Equal(CleanAnswer, extracted.Json);
        Assert.Equal(0, extracted.PreambleLength);
        Assert.Equal(Trailer.Trim().Length, extracted.TrailerLength);
    }

    [Theory]
    [InlineData("```json\n{0}\n```")]
    [InlineData("```\n{0}\n```")]
    public void A_fenced_object_is_read_as_before_and_is_not_trimmed(string format)
    {
        var extracted = ClaudeService.ExtractJsonObject(format.Replace("{0}", CleanAnswer, StringComparison.Ordinal));

        Assert.Equal(CleanAnswer, extracted.Json);
        Assert.False(extracted.IsTrimmed);
    }

    [Fact]
    public void A_preamble_before_a_fenced_object_is_dropped_with_the_fence()
    {
        var extracted = ClaudeService.ExtractJsonObject("Este es el resultado:\n```json\n" + CleanAnswer + "\n```");

        Assert.Equal(CleanAnswer, extracted.Json);
        Assert.True(extracted.PreambleLength > 0);
        Assert.Equal(3, extracted.TrailerLength);   // the closing ```
    }

    [Fact]
    public void Braces_brackets_and_fences_inside_string_values_are_safe()
    {
        const string answer = "{\"a\": \"} { ] [ ``` \\\"}\\\"\", \"b\": {\"c\": \"{\"}, \"d\": [\"}\"]}";

        var extracted = ClaudeService.ExtractJsonObject("Resultado: " + answer + " fin");

        Assert.Equal(answer, extracted.Json);
        Assert.Equal("Resultado: ".Length, extracted.PreambleLength);
        Assert.Equal("fin".Length, extracted.TrailerLength);
    }

    [Fact]
    public void Of_two_objects_the_first_wins_and_the_second_is_trailer()
    {
        const string second = "{\"confidence_score\": 0.1}";

        var extracted = ClaudeService.ExtractJsonObject(CleanAnswer + "\n" + second);

        Assert.Equal(CleanAnswer, extracted.Json);
        Assert.Equal(second.Length, extracted.TrailerLength);
    }

    [Theory]
    [InlineData("No pude leer el documento.")]
    [InlineData("")]
    [InlineData("```json\n```")]
    public void No_object_at_all_gives_no_json_and_no_reader_error(string answer)
    {
        var extracted = ClaudeService.ExtractJsonObject(answer);

        Assert.Null(extracted.Json);
        Assert.Null(extracted.Error);
    }

    [Fact]
    public void A_truncated_object_gives_no_json_and_the_readers_position()
    {
        var extracted = ClaudeService.ExtractJsonObject(Preamble + "{\"ratios\": {\"a\": [1, 2,");

        Assert.Null(extracted.Json);
        Assert.NotNull(extracted.Error);
        Assert.Equal(Preamble.Length, extracted.PreambleLength);
        Assert.Equal(0, extracted.Error!.LineNumber);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[1, 2, 3]")]
    [InlineData("42")]
    public void A_complete_non_object_document_is_returned_as_is(string answer)
    {
        var extracted = ClaudeService.ExtractJsonObject(answer);

        Assert.Equal(answer, extracted.Json);
        Assert.False(extracted.IsTrimmed);
    }

    // ── End to end: every reader reads the extracted object ───────────────────

    [Fact]
    public async Task A_clean_answer_produces_the_same_BalanceJson_bytes_as_before()
    {
        var result = await AnalyzeAsync(Envelope(CleanAnswer));

        Assert.Equal(CleanBalanceJsonSha256, Sha256(result.BalanceJson));
        Assert.DoesNotContain("answerTrimmed", result.BalanceJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prose_around_the_object_is_read_flagged_in_provenance_and_logged_by_length_only()
    {
        var logger = new CapturingLogger();

        var result = await AnalyzeAsync(Envelope(Preamble + CleanAnswer + Trailer), logger);

        var balance = JsonNode.Parse(result.BalanceJson)!.AsObject();
        var extraction = balance["integrity"]!["extraction"]!.AsObject();
        Assert.True(extraction["answerTrimmed"]!.GetValue<bool>());

        // ParseCashFlow read the extracted object: the block is there, scaled from miles.
        Assert.True(balance["cash_flow"]!["present"]!.GetValue<bool>());
        Assert.Equal(55_000d, balance["cash_flow"]!["cash_end"]!.GetValue<double>());

        // Everything else is the clean answer's BalanceJson, byte for byte.
        extraction.Remove("answerTrimmed");
        Assert.Equal(CleanBalanceJsonSha256, Sha256(balance.ToJsonString()));

        var warning = Assert.Single(logger.Entries, e => e.Message.Contains("text around its JSON object", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains($"preamble_chars={Preamble.Length}", warning.Message, StringComparison.Ordinal);
        Assert.Contains($"trailer_chars={Trailer.Trim().Length}", warning.Message, StringComparison.Ordinal);
        Assert.Contains("escalated=False", warning.Message, StringComparison.Ordinal);
        Assert.Contains("model=claude-sonnet-4-6", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(logger.Entries, e =>
            e.Message.Contains("Aquí está", StringComparison.Ordinal) || e.Message.Contains("Quedo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_trial_balance_reader_sees_the_extracted_object()
    {
        var answer = ClaudeExtractionParseTests.ModelJson(
            ClaudeExtractionParseTests.TrialBalanceCheck, ClaudeExtractionParseTests.TrialBalanceBlock);

        var result = await AnalyzeAsync(Envelope(Preamble + "```json\n" + answer + "\n```" + Trailer));

        var balance = JsonNode.Parse(result.BalanceJson)!.AsObject();
        Assert.Equal("trial_balance", balance["document_kind"]!.GetValue<string>());
        Assert.Equal("result_added", balance["trial_balance"]!["adjustment"]!.GetValue<string>());
        Assert.Equal(600000d, balance["equity"]!.GetValue<double>());
        Assert.True(balance["integrity"]!["extraction"]!["answerTrimmed"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_valid_object_outside_the_schema_still_is_schema_invalid()
    {
        var ex = await Assert.ThrowsAsync<ClaudeApiException>(() =>
            AnalyzeAsync(Envelope(Preamble + "{\"confidence_score\": {\"nested\": true}}")));

        Assert.Equal(ClaudeService.AnswerSchemaInvalid, ex.FailureCode);
        Assert.Equal(ClaudeApiException.DefaultClientMessage, ex.Message);
    }

    // ── No readable object: its own code, a locating diagnostic, an honest client text ──────────

    [Fact]
    public async Task Prose_with_no_object_fails_as_unparseable_without_quoting_the_answer()
    {
        const string answer = "No pude identificar los estados contables en el documento.";
        var handler = new RecordingHandler(Envelope(answer));

        var ex = await Assert.ThrowsAsync<ClaudeApiException>(() => AnalyzeAsync(handler));

        Assert.Single(handler.Captured);
        Assert.Equal(ClaudeService.AnswerUnparseable, ex.FailureCode);
        Assert.Contains("no '{' found", ex.DiagnosticMessage, StringComparison.Ordinal);
        Assert.Contains("model claude-sonnet-4-6, escalated False", ex.DiagnosticMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("No pude", ex.DiagnosticMessage, StringComparison.Ordinal);
        Assert.Equal(ClaudeApiException.UnparseableAnswerClientMessage, ex.Message);
        Assert.Null(ex.InnerException);
        Assert.Null(ex.ApiStatusCode);
        Assert.Equal(10, ex.InputTokens);   // billed, usage known
        Assert.Equal(20, ex.OutputTokens);
        Assert.Equal("claude-sonnet-4-6", ex.Model);
    }

    [Fact]
    public async Task A_broken_object_fails_as_unparseable_with_the_readers_path_and_position()
    {
        var ex = await Assert.ThrowsAsync<ClaudeApiException>(() =>
            AnalyzeAsync(Envelope("Resultado:\n{\"ratios\": {}, \"alerts\": [ zzz ]}")));

        Assert.Equal(ClaudeService.AnswerUnparseable, ex.FailureCode);
        Assert.Contains("opened at char 11", ex.DiagnosticMessage, StringComparison.Ordinal);
        Assert.Contains("Path: $ | LineNumber: 0 | BytePositionInLine: ", ex.DiagnosticMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("zzz", ex.DiagnosticMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("'z'", ex.DiagnosticMessage, StringComparison.Ordinal);
        Assert.Null(ex.InnerException);   // its message would quote a character of the answer
        Assert.Equal(ClaudeApiException.UnparseableAnswerClientMessage, ex.Message);
    }

    // ── The same through the max_tokens escalation ───────────────────────────

    [Fact]
    public async Task The_escalated_answer_is_read_through_its_preamble()
    {
        var logger  = new CapturingLogger();
        var handler = new RecordingHandler(
            Envelope(OkModelJson, stopReason: "max_tokens"),
            Envelope("I reviewed the whole document. Here is the JSON:\n" + OkModelJson));

        var result = await AnalyzeAsync(handler, logger);

        Assert.Equal(2, handler.Captured.Count);
        Assert.True(Assert.Single(result.ExtractionPasses).Escalated);
        Assert.Equal(20, result.InputTokens);
        Assert.Equal(40, result.OutputTokens);
        Assert.True(JsonNode.Parse(result.BalanceJson)!["integrity"]!["extraction"]!["answerTrimmed"]!.GetValue<bool>());
        Assert.Contains(logger.Entries, e =>
            e.Message.Contains("text around its JSON object", StringComparison.Ordinal)
            && e.Message.Contains("escalated=True", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_escalated_answer_with_no_object_is_unparseable_with_both_calls_tokens()
    {
        var handler = new RecordingHandler(
            Envelope(OkModelJson, stopReason: "max_tokens"),
            Envelope("I could not complete the extraction."));

        var ex = await Assert.ThrowsAsync<ClaudeApiException>(() => AnalyzeAsync(handler));

        Assert.Equal(2, handler.Captured.Count);   // never a third request
        Assert.Equal(ClaudeService.AnswerUnparseable, ex.FailureCode);
        Assert.Contains("escalated True", ex.DiagnosticMessage, StringComparison.Ordinal);
        Assert.Equal(20, ex.InputTokens);
        Assert.Equal(40, ex.OutputTokens);
        Assert.Equal(ClaudeApiException.UnparseableAnswerClientMessage, ex.Message);
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private static Task<ClaudeAnalysisResult> AnalyzeAsync(string envelope, ILogger<ClaudeService>? logger = null) =>
        AnalyzeAsync(new RecordingHandler(envelope), logger);

    private static Task<ClaudeAnalysisResult> AnalyzeAsync(RecordingHandler handler, ILogger<ClaudeService>? logger = null) =>
        new ClaudeService(
                new StubHttpClientFactory(handler),
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Anthropic:ApiKey"] = "test-key",
                        // No image fallback: the fixture PDF cannot be rendered; the pass under test is the first.
                        ["Anthropic:ExtractionConfidenceFallbackThreshold"] = "0"
                    })
                    .Build(),
                logger ?? new CapturingLogger(),
                new BalanceIntegrityService(),
                Options.Create(new ExtractionModelOptions()),
                NullScanOrientationDetector.Instance)
            .AnalyzeDocumentAsync(FakePdf, "balance", 2025, CancellationToken.None);

    private static string Envelope(string modelText, string stopReason = "end_turn") =>
        JsonSerializer.Serialize(new
        {
            content     = new[] { new { type = "text", text = modelText } },
            stop_reason = stopReason,
            usage       = new { input_tokens = 10, output_tokens = 20 }
        });

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class RecordingHandler(params string[] responseBodies) : HttpMessageHandler
    {
        private int _calls;

        public List<string> Captured { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Captured.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

            var body = responseBodies[Math.Min(_calls++, responseBodies.Length - 1)];
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class CapturingLogger : ILogger<ClaudeService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
                Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
