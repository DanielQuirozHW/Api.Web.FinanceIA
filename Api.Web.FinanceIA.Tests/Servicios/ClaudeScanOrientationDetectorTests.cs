// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-03

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Tests.Legajo;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// The scan-orientation call over canned envelopes: what it sends (and only that), how it reads the
/// answer, and that every failure leaves pages unknown instead of failing the caller. Synthetic empty
/// pages only.
/// </summary>
public sealed class ClaudeScanOrientationDetectorTests
{
    // ── The request ───────────────────────────────────────────────────────────

    [Fact]
    public async Task It_asks_haiku_with_a_forced_tool_at_temperature_zero_about_the_requested_pages_only()
    {
        var handler = new Handler(Answer((1, 90), (2, 0)));
        var sut     = Build(handler);

        await sut.DetectAsync(LegajoPdfFactory.BlankPdf(3), [0, 2], "test-key", CancellationToken.None);

        var request = JsonNode.Parse(Assert.Single(handler.Captured))!;
        Assert.Equal("claude-haiku-4-5", request["model"]!.GetValue<string>());
        Assert.Equal(0, request["temperature"]!.GetValue<int>());
        Assert.Equal("tool", request["tool_choice"]!["type"]!.GetValue<string>());
        Assert.Equal(ClaudeScanOrientationDetector.ToolName, request["tool_choice"]!["name"]!.GetValue<string>());
        Assert.True(request["tools"]![0]!["strict"]!.GetValue<bool>());
        Assert.Equal(ClaudeScanOrientationDetector.SystemPrompt, request["system"]!.GetValue<string>());

        var content = request["messages"]![0]!["content"]!.AsArray();
        var images  = content.Where(b => b!["type"]!.GetValue<string>() == "image").ToList();
        var texts   = content.Where(b => b!["type"]!.GetValue<string>() == "text").Select(b => b!["text"]!.GetValue<string>()).ToList();

        Assert.Equal(2, images.Count);
        Assert.Equal(["Image 1", "Image 2", "Report the clockwise turn that makes each of these 2 images read upright."], texts);
        foreach (var image in images)
        {
            using var bitmap = SKBitmap.Decode(Convert.FromBase64String(image!["source"]!["data"]!.GetValue<string>()));
            Assert.True(Math.Max(bitmap.Width, bitmap.Height) <= ClaudeScanOrientationDetector.ThumbnailLongEdgePx,
                $"{bitmap.Width}x{bitmap.Height}");
        }
    }

    [Fact]
    public void Its_model_accepts_the_forced_tool_and_temperature_and_is_priced()
    {
        Assert.True(LegajoModelSupport.SupportsForcedToolUse(ClaudeScanOrientationDetector.Model));
        Assert.NotNull(ClaudeModelPricing.EstimateCostUsd(ClaudeScanOrientationDetector.Model, 1000, 100));
    }

    // ── The answer ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Each_image_number_maps_back_to_its_page_with_the_call_reported()
    {
        var sut = Build(new Handler(Answer((2, 270), (1, 90))));

        var result = await sut.DetectAsync(LegajoPdfFactory.BlankPdf(3), [0, 2], "test-key", CancellationToken.None);

        Assert.Equal([90, 270], result.Rotations);
        var usage = result.Usage!;
        Assert.True(usage.Succeeded);
        Assert.Equal("claude-haiku-4-5", usage.Model);
        Assert.Equal(700, usage.InputTokens);
        Assert.Equal(40, usage.OutputTokens);
        Assert.Equal(200, usage.HttpStatus);
        Assert.Equal(2, usage.PagesAsked);
        Assert.Equal(2, usage.PagesAnswered);
        Assert.Null(usage.ErrorCode);
    }

    [Fact]
    public async Task Missing_out_of_range_odd_and_repeated_entries_leave_those_pages_unknown()
    {
        // Image 1: reported twice (unknown). Image 2: 45° (unknown). Image 3: fine. Image 4: absent.
        // Image 9: no such image (ignored).
        var sut = Build(new Handler(Answer((1, 0), (1, 90), (2, 45), (3, 180), (9, 90))));

        var result = await sut.DetectAsync(LegajoPdfFactory.BlankPdf(4), [0, 1, 2, 3], "test-key", CancellationToken.None);

        Assert.Equal([null, null, 180, null], result.Rotations);
        Assert.True(result.Usage!.Succeeded);
        Assert.Equal(1, result.Usage.PagesAnswered);
    }

    // ── Failures never reach the caller ───────────────────────────────────────

    [Fact]
    public async Task A_server_error_leaves_every_page_unknown_with_a_warning()
    {
        var logger = new Logger();
        var sut    = Build(new Handler((HttpStatusCode.InternalServerError, "{\"error\":\"boom\"}")), logger);

        var result = await sut.DetectAsync(LegajoPdfFactory.BlankPdf(2), [0, 1], "test-key", CancellationToken.None);

        Assert.Equal([null, null], result.Rotations);
        Assert.False(result.Usage!.Succeeded);
        Assert.Equal(500, result.Usage.HttpStatus);
        Assert.Equal("claude_api_error", result.Usage.ErrorCode);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.DoesNotContain("boom", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_4xx_keeps_the_api_reason_as_error_detail_but_logs_no_message()
    {
        var logger = new Logger();
        var sut    = Build(new Handler((HttpStatusCode.BadRequest,
            "{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"image too large\"},\"request_id\":\"req_o1\"}")), logger);

        var result = await sut.DetectAsync(LegajoPdfFactory.BlankPdf(2), [0, 1], "test-key", CancellationToken.None);

        Assert.Equal("claude_api_error", result.Usage!.ErrorCode);
        Assert.Equal("HTTP 400 invalid_request_error: image too large [req_o1]", result.Usage.ErrorDetail);
        Assert.All(logger.Entries, e => Assert.DoesNotContain("image too large", e.Message, StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e => e.Message.Contains("req_o1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_success_has_no_error_detail()
    {
        var sut = Build(new Handler(Answer((1, 0), (2, 0))));

        var result = await sut.DetectAsync(LegajoPdfFactory.BlankPdf(2), [0, 1], "test-key", CancellationToken.None);

        Assert.Null(result.Usage!.ErrorDetail);
    }

    [Fact]
    public async Task An_unreadable_answer_leaves_every_page_unknown()
    {
        var sut = Build(new Handler((HttpStatusCode.OK, "not json")));

        var result = await sut.DetectAsync(LegajoPdfFactory.BlankPdf(1), [0], "test-key", CancellationToken.None);

        Assert.Equal([null], result.Rotations);
        Assert.Equal("claude_invalid_response", result.Usage!.ErrorCode);
    }

    [Fact]
    public async Task A_timeout_leaves_every_page_unknown()
    {
        var handler = new Handler(Answer((1, 90))) { Delay = TimeSpan.FromSeconds(30) };
        var sut     = Build(handler);

        var result = await sut.DetectAsync(LegajoPdfFactory.BlankPdf(1), [0], "test-key", CancellationToken.None);

        Assert.Equal([null], result.Rotations);
        Assert.Equal("claude_timeout", result.Usage!.ErrorCode);
        Assert.Null(result.Usage.HttpStatus);
    }

    [Fact]
    public async Task A_network_error_leaves_every_page_unknown()
    {
        var handler = new Handler(Answer((1, 90))) { Throw = new HttpRequestException("socket") };
        var sut     = Build(handler);

        var result = await sut.DetectAsync(LegajoPdfFactory.BlankPdf(1), [0], "test-key", CancellationToken.None);

        Assert.Equal([null], result.Rotations);
        Assert.Equal("claude_network_error", result.Usage!.ErrorCode);
    }

    [Fact]
    public async Task A_document_that_cannot_be_rendered_sends_nothing()
    {
        var handler = new Handler(Answer((1, 90)));
        var sut     = Build(handler);

        var result = await sut.DetectAsync("%PDF-1.4 fixture"u8.ToArray(), [0], "test-key", CancellationToken.None);

        Assert.Empty(handler.Captured);
        Assert.Equal([null], result.Rotations);
        Assert.Null(result.Usage);
    }

    [Fact]
    public async Task The_callers_cancellation_propagates()
    {
        using var cts = new CancellationTokenSource();
        var handler = new Handler(Answer((1, 90))) { Delay = TimeSpan.FromSeconds(30), OnSend = cts.Cancel };
        var sut     = Build(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sut.DetectAsync(LegajoPdfFactory.BlankPdf(1), [0], "test-key", cts.Token));
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private static ClaudeScanOrientationDetector Build(Handler handler, Logger? logger = null) =>
        new(new Factory(handler), logger ?? new Logger()) { Timeout = TimeSpan.FromMilliseconds(500) };

    private static (HttpStatusCode, string) Answer(params (int Index, int Rotation)[] pages)
    {
        var entries = new JsonArray();
        foreach (var (index, rotation) in pages)
            entries.Add(new JsonObject { ["index"] = index, ["rotation"] = rotation });

        var envelope = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"]  = "tool_use",
                    ["id"]    = "toolu_1",
                    ["name"]  = ClaudeScanOrientationDetector.ToolName,
                    ["input"] = new JsonObject { ["pages"] = entries }
                }
            },
            ["stop_reason"] = "tool_use",
            ["usage"]       = new JsonObject { ["input_tokens"] = 700, ["output_tokens"] = 40 }
        };

        return (HttpStatusCode.OK, envelope.ToJsonString());
    }

    private sealed class Handler((HttpStatusCode Status, string Body) response) : HttpMessageHandler
    {
        public List<string> Captured { get; } = [];

        public TimeSpan Delay { get; init; }

        public Exception? Throw { get; init; }

        public Action? OnSend { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Captured.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            OnSend?.Invoke();

            if (Throw is not null)
                throw Throw;
            if (Delay > TimeSpan.Zero)
                await Task.Delay(Delay, cancellationToken);

            return new HttpResponseMessage(response.Status) { Content = new StringContent(response.Body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Factory(Handler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Logger : ILogger<ClaudeScanOrientationDetector>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
