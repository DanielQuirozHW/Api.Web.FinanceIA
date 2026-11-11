// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-28

using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Servicios.Legajo;
using Api.Web.FinanceIA.Tests.Legajo;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// <see cref="LegajoMessagesClient"/> on its own: the non-strict body variant, the ApiCallLogs row it
/// writes (operation, document, cost; none when nothing was sent), the named client it sends on, and a
/// reader that rejects the tool input. The classification-specific behaviour (parsing, retries, 429)
/// is covered through LegajoClassificationServiceTests, which now run over this client.
/// </summary>
public sealed class LegajoMessagesClientTests
{
    private const string ToolName = "record_test";

    private static readonly Guid DocumentId = Guid.NewGuid();

    private static LegajoMessagesRequest Request(string clientName = "TestClient") => new()
    {
        Operation      = LegajoMessagesClient.ExtractionOperation,
        TenantId       = LegajoTestData.Tenant,
        DocumentId     = DocumentId,
        Model          = "claude-sonnet-4-6",
        ApiKey         = "test-key",
        HttpClientName = clientName,
        RequestJson    = "{}",
        ToolName       = ToolName,
        Clock          = Stopwatch.StartNew()
    };

    private static string Envelope(JsonObject input, string stopReason = "tool_use") => new JsonObject
    {
        ["stop_reason"] = stopReason,
        ["content"] = new JsonArray
        {
            new JsonObject { ["type"] = "thinking", ["thinking"] = "", ["signature"] = "s" },
            new JsonObject { ["type"] = "tool_use", ["id"] = "t1", ["name"] = ToolName, ["input"] = input }
        },
        ["usage"] = new JsonObject { ["input_tokens"] = 1000, ["output_tokens"] = 200 }
    }.ToJsonString();

    [Fact]
    public void BuildRequestJson_NonStrict_OmitsTheStrictKey_KeepsForcedToolAndNoThinking()
    {
        var body = LegajoMessagesClient.BuildRequestJson(
            "m", 16000, "system", ToolName, "desc", """{"type":"object"}""", strict: false,
            [new JsonObject { ["type"] = "text", ["text"] = "x" }]);

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var tool = root.GetProperty("tools")[0];

        Assert.False(tool.TryGetProperty("strict", out _));
        Assert.Equal(16000, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal("tool", root.GetProperty("tool_choice").GetProperty("type").GetString());
        Assert.False(root.TryGetProperty("thinking", out _));
    }

    [Fact]
    public async Task SendAsync_4xx_WritesTheCodeAndTheApiReasonToTheRow()
    {
        var handler  = new ScriptedHandler((HttpStatusCode.BadRequest,
            "{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"tool_choice: forced tool use is not supported\"},\"request_id\":\"req_l1\"}"));
        var apiCalls = new RecordingApiCallLogService();
        var sut      = LegajoTestClients.Messages(handler, apiCalls);

        var result = await sut.SendAsync<string>(Request(), i => (i.GetRawText(), null),
            _ => Task.FromResult(true), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(1, handler.Hits); // a 4xx is not retried
        var row = Assert.Single(apiCalls.Calls);
        Assert.Equal(
            "claude_api_error | HTTP 400 invalid_request_error: tool_choice: forced tool use is not supported [req_l1]",
            row.Error);
    }

    [Fact]
    public async Task SendAsync_Success_WritesOneApiCallLogRow_WithOperationDocumentAndCost()
    {
        var handler  = new ScriptedHandler((HttpStatusCode.OK, Envelope(new JsonObject { ["a"] = 1 })));
        var apiCalls = new RecordingApiCallLogService();
        var factory  = new StubHttpClientFactory(handler);
        var sut      = LegajoTestClients.Messages(handler, apiCalls, factory);

        var result = await sut.SendAsync<string>(Request("LongClient"), i => (i.GetRawText(), null),
            _ => Task.FromResult(true), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1000, result.InputTokens);
        Assert.Equal(["LongClient"], factory.ClientNames);

        var call = Assert.Single(apiCalls.Calls);
        Assert.Equal("Claude", call.Service);
        Assert.Equal(LegajoMessagesClient.ExtractionOperation, call.Operation);
        Assert.Equal(DocumentId, call.DocumentId);
        Assert.True(call.IsSuccess);
        Assert.Equal(0.006m, call.Cost);   // 1000 × $3 + 200 × $15, per million
    }

    [Fact]
    public async Task SendAsync_ReaderRejectsTheInput_FailsWithItsCode_KeepsTheOutput_LogsAFailure()
    {
        var handler  = new ScriptedHandler((HttpStatusCode.OK, Envelope(new JsonObject { ["a"] = 1 })));
        var apiCalls = new RecordingApiCallLogService();
        var sut      = LegajoTestClients.Messages(handler, apiCalls);

        var result = await sut.SendAsync<string>(Request(), _ => ("kept", "custom_invalid"),
            _ => Task.FromResult(true), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("custom_invalid", result.ErrorCode);
        Assert.Equal("kept", result.Output);

        var call = Assert.Single(apiCalls.Calls);
        Assert.False(call.IsSuccess);
        Assert.Equal("custom_invalid", call.Error);
    }

    [Fact]
    public async Task SendAsync_TimeoutNotRetried_OneAttempt_ItsOwnCode_OneApiCallLogRow()
    {
        var handler  = new ScriptedHandler((HttpStatusCode.OK, Envelope(new JsonObject()))) { TimeoutsFirst = 3 };
        var apiCalls = new RecordingApiCallLogService();
        var sut      = LegajoTestClients.Messages(handler, apiCalls);
        var request  = Request() with { RetryOnTimeout = false, TimeoutErrorCode = LegajoExtractionErrors.Timeout };

        var result = await sut.SendAsync<string>(request, i => (i.GetRawText(), null),
            _ => Task.FromResult(true), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("extraction_timeout", result.ErrorCode);
        Assert.True(result.RequestSent);
        Assert.Equal(1, handler.Hits);

        var call = Assert.Single(apiCalls.Calls);
        Assert.False(call.IsSuccess);
        Assert.Equal("extraction_timeout", call.Error);
    }

    [Fact]
    public async Task SendAsync_TimeoutByDefault_IsRetried()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, Envelope(new JsonObject { ["a"] = 1 }))) { TimeoutsFirst = 1 };

        var result = await LegajoTestClients.Messages(handler).SendAsync<string>(Request(), i => (i.GetRawText(), null),
            _ => Task.FromResult(true), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2, handler.Hits);
    }

    [Fact]
    public async Task SendAsync_NotAllowed_NoRequest_NoApiCallLogRow()
    {
        var handler  = new ScriptedHandler((HttpStatusCode.OK, Envelope(new JsonObject())));
        var apiCalls = new RecordingApiCallLogService();
        var sut      = LegajoTestClients.Messages(handler, apiCalls);

        var result = await sut.SendAsync<string>(Request(), _ => ("x", null),
            _ => Task.FromResult(false), CancellationToken.None);

        Assert.Equal(LegajoClassificationErrors.ConsentRevoked, result.ErrorCode);
        Assert.False(result.RequestSent);
        Assert.Equal(0, handler.Hits);
        Assert.Empty(apiCalls.Calls);
    }

    [Fact]
    public async Task SendAsync_MaxTokensStop_IsTruncated_EvenWithAToolUseBlock()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, Envelope(new JsonObject(), stopReason: "max_tokens")));
        var sut     = LegajoTestClients.Messages(handler);

        var result = await sut.SendAsync<string>(Request(), _ => ("x", null),
            _ => Task.FromResult(true), CancellationToken.None);

        Assert.Equal(LegajoClassificationErrors.Truncated, result.ErrorCode);
        Assert.Null(result.Output);
    }
}
