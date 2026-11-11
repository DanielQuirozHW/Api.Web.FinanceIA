// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-06

using System.Net;
using System.Text;
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
/// The reason of a Claude HTTP error, kept on <see cref="ClaudeService"/>'s paths: the balance extraction
/// and the chat put the API's error type, a clamped message and the request id in
/// <see cref="ClaudeApiException.DiagnosticMessage"/> (which ApiCallLogs.ErrorMessage stores), the client
/// message does not move, and the scoring narrative says it in its Warning. Never the raw body.
/// </summary>
public sealed class ClaudeServiceErrorReasonTests
{
    private static readonly byte[] FakePdf = "%PDF-1.4 fixture"u8.ToArray();

    private const string BadRequestBody =
        """{"type":"error","error":{"type":"invalid_request_error","message":"temperature: not supported for this model"},"secret_echo":"document text"}""";

    private const string Expected =
        "HTTP 400 invalid_request_error: temperature: not supported for this model [req_01ABC]";

    [Fact]
    public async Task An_extraction_4xx_carries_type_message_and_request_id_and_keeps_the_client_message()
    {
        var handler = new ErrorHandler(HttpStatusCode.BadRequest, BadRequestBody, "req_01ABC");
        var logger  = new RecordingLogger();

        var ex = await Assert.ThrowsAsync<ClaudeApiException>(() =>
            BuildSut(handler, logger).AnalyzeDocumentAsync(FakePdf, "balance", 2025, CancellationToken.None));

        Assert.Equal($"Claude API error: {Expected}.", ex.DiagnosticMessage);
        Assert.Equal(400, ex.ApiStatusCode);
        Assert.Equal(ClaudeApiException.DefaultClientMessage, ex.Message);
        Assert.DoesNotContain("document text", ex.DiagnosticMessage, StringComparison.Ordinal);
        Assert.Equal(1, handler.Calls); // a 4xx is not retried

        var error = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains(Expected, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("document text", error.Message, StringComparison.Ordinal);
        Assert.Equal("invalid_request_error", error.State["ErrorType"]);
        Assert.Equal("req_01ABC", error.State["RequestId"]);
    }

    [Fact]
    public async Task An_extraction_429_is_still_thrown_at_once_with_its_reason()
    {
        const string body = """{"type":"error","error":{"type":"rate_limit_error","message":"Number of request tokens has exceeded your per-minute rate limit"}}""";
        var handler = new ErrorHandler(HttpStatusCode.TooManyRequests, body, "req_429");

        var ex = await Assert.ThrowsAsync<ClaudeApiException>(() =>
            BuildSut(handler).AnalyzeDocumentAsync(FakePdf, "balance", 2025, CancellationToken.None));

        Assert.Equal(429, ex.ApiStatusCode);
        Assert.Equal(
            "Claude API rate limit exceeded: HTTP 429 rate_limit_error: Number of request tokens has exceeded your per-minute rate limit [req_429].",
            ex.DiagnosticMessage);
        Assert.Equal(ClaudeApiException.DefaultClientMessage, ex.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task A_non_json_error_body_leaves_the_status_and_the_request_id()
    {
        var handler = new ErrorHandler(HttpStatusCode.Forbidden, "<html>forbidden</html>", "req_403");

        var ex = await Assert.ThrowsAsync<ClaudeApiException>(() =>
            BuildSut(handler).AnalyzeDocumentAsync(FakePdf, "balance", 2025, CancellationToken.None));

        Assert.Equal("Claude API error: HTTP 403 [req_403].", ex.DiagnosticMessage);
        Assert.DoesNotContain("html", ex.DiagnosticMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_chat_4xx_carries_the_same_reason_and_keeps_the_client_message()
    {
        var handler = new ErrorHandler(HttpStatusCode.BadRequest, BadRequestBody, "req_01ABC");
        var logger  = new RecordingLogger();

        var ex = await Assert.ThrowsAsync<ClaudeApiException>(() =>
            BuildSut(handler, logger).ChatAsync(ChatModelCatalog.Sonnet46.Id, "SYS", [], "Q", CancellationToken.None));

        Assert.Equal($"Claude chat API error: {Expected}.", ex.DiagnosticMessage);
        Assert.Equal(ClaudeApiException.DefaultClientMessage, ex.Message);
        var error = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.DoesNotContain("document text", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_narrative_4xx_is_a_warning_with_the_reason_and_no_narrative()
    {
        var handler = new ErrorHandler(HttpStatusCode.BadRequest, BadRequestBody, "req_01ABC");
        var logger  = new RecordingLogger();

        var result = await BuildSut(handler, logger).GenerateScoringNarrativeAsync(
            new ScoringResult { Score = 50m, Level = "Medium" }, new RatiosData(), null, null, null, CancellationToken.None);

        Assert.Equal(string.Empty, result.Text);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(Expected, warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("document text", warning.Message, StringComparison.Ordinal);
    }

    private static ClaudeService BuildSut(ErrorHandler handler, ILogger<ClaudeService>? logger = null) =>
        new(new Factory(handler),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Anthropic:ApiKey"] = "test-key",
                    ["Anthropic:ExtractionConfidenceFallbackThreshold"] = "0"
                })
                .Build(),
            logger ?? new RecordingLogger(),
            new BalanceIntegrityService(),
            Options.Create(new ExtractionModelOptions()),
            NullScanOrientationDetector.Instance);

    private sealed class ErrorHandler(HttpStatusCode status, string body, string? requestId) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (requestId is not null)
                response.Headers.Add("request-id", requestId);
            return Task.FromResult(response);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingLogger : ILogger<ClaudeService>
    {
        public List<(LogLevel Level, string Message, IReadOnlyDictionary<string, object?> State)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.ToDictionary(p => p.Key, p => p.Value)
                : [];
            lock (Entries)
                Entries.Add((logLevel, formatter(state, exception), values));
        }
    }
}
