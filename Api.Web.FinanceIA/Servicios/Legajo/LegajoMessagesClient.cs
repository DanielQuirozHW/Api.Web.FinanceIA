// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// The legajo pipeline's minimal raw-HTTP Messages client, shared by the classification (M10 Phase 2a)
/// and the extraction (Phase 2b) passes — like ClaudeService, the project calls the API without the SDK.
///
/// <para>
/// One request forces one tool (<c>tool_choice {type:"tool"}</c>) at temperature 0 with NO
/// <c>thinking</c> field. The response is read by block TYPE: the <c>tool_use</c> block naming the
/// request's tool, never <c>content[0]</c>, so a leading thinking or text block is skipped; a
/// <c>refusal</c> or <c>max_tokens</c> stop is an error before any content is read.
/// </para>
///
/// <para>
/// Retries mirror balance extraction: three attempts, backoff 5 s then 15 s, on a timeout, a network
/// error or a 5xx; a 429 or any other 4xx fails at once. A request with
/// <see cref="LegajoMessagesRequest.RetryOnTimeout"/> false (the extraction) fails on its first timeout,
/// with its own <see cref="LegajoMessagesRequest.TimeoutErrorCode"/>. The caller's consent check runs
/// before EVERY attempt, retries included.
/// </para>
///
/// <para>
/// Every call that reached the API writes ONE ApiCallLogs row (Service "Claude", the request's
/// Operation and DocumentId, the estimated cost) — a call that returned is money spent, and
/// <see cref="IApiCallLogService"/> never throws. Nothing is logged from the request or the response
/// but status codes, token counts and sizes: both carry a person's paperwork.
/// </para>
/// </summary>
public sealed class LegajoMessagesClient
{
    /// <summary>ApiCallLogs.Operation of the classification pass.</summary>
    public const string ClassificationOperation = ApiCallLogOperations.LegajoClassification;

    /// <summary>ApiCallLogs.Operation of the extraction pass.</summary>
    public const string ExtractionOperation = ApiCallLogOperations.LegajoExtraction;

    private const string ApiUrl           = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";
    private const int    MaxAttempts      = 3;

    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IApiCallLogService _apiCallLog;
    private readonly ILogger<LegajoMessagesClient> _logger;

    /// <summary>Initializes a new instance of <see cref="LegajoMessagesClient"/>.</summary>
    public LegajoMessagesClient(
        IHttpClientFactory httpClientFactory,
        IApiCallLogService apiCallLog,
        ILogger<LegajoMessagesClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _apiCallLog        = apiCallLog;
        _logger            = logger;
    }

    /// <summary>The backoff wait. A seam so tests do not sleep 20 seconds.</summary>
    internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } = Task.Delay;

    /// <summary>
    /// Serializes a forced-tool Messages body. <paramref name="strict"/> adds <c>"strict": true</c> to
    /// the tool; when false the key is omitted. The property order is the wire format Phase 2a's
    /// classification shipped with — pinned by LegajoClassificationPayloadGoldenTests.
    /// </summary>
    internal static string BuildRequestJson(
        string model,
        int maxTokens,
        string systemPrompt,
        string toolName,
        string toolDescription,
        string toolSchemaJson,
        bool strict,
        IEnumerable<JsonNode> contentBlocks)
    {
        var content = new JsonArray();
        foreach (var block in contentBlocks)
            content.Add(block);

        var tool = new JsonObject
        {
            ["name"]        = toolName,
            ["description"] = toolDescription
        };
        if (strict)
            tool["strict"] = true;
        tool["input_schema"] = JsonNode.Parse(toolSchemaJson);

        var payload = new JsonObject
        {
            ["model"]       = model,
            ["max_tokens"]  = maxTokens,
            ["temperature"] = 0,
            ["system"]      = systemPrompt,
            ["tools"]       = new JsonArray { tool },
            ["tool_choice"] = new JsonObject
            {
                ["type"] = "tool",
                ["name"] = toolName
            },
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"]    = "user",
                    ["content"] = content
                }
            }
        };

        return payload.ToJsonString();
    }

    /// <summary>
    /// Sends <paramref name="request"/> with retries and reads the tool input with
    /// <paramref name="readToolInput"/>. Never throws for an API or parsing failure — the result
    /// carries a code; only cancellation propagates.
    /// </summary>
    /// <param name="request">The body and its bookkeeping.</param>
    /// <param name="readToolInput">
    /// The caller's reading of the tool_use input: an output, an error code, or both (an extraction
    /// keeps what it rejected so the failed run can record why). A non-null code fails the call.
    /// </param>
    /// <param name="isStillAllowedAsync">Evaluated immediately before every attempt; false aborts with <see cref="LegajoClassificationErrors.ConsentRevoked"/>.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    public async Task<LegajoMessagesResult<TOutput>> SendAsync<TOutput>(
        LegajoMessagesRequest request,
        Func<JsonElement, (TOutput? Output, string? ErrorCode)> readToolInput,
        Func<CancellationToken, Task<bool>> isStillAllowedAsync,
        CancellationToken ct)
        where TOutput : class
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(readToolInput);
        ArgumentNullException.ThrowIfNull(isStillAllowedAsync);

        var result = await SendCoreAsync(request, readToolInput, isStillAllowedAsync, ct).ConfigureAwait(false);

        // The cost row, for every call that reached the API — success or failure.
        if (result.RequestSent)
        {
            await _apiCallLog.LogAsync(
                    tenantId:         request.TenantId,
                    service:          "Claude",
                    endpoint:         "/v1/messages",
                    responseStatus:   result.HttpStatus,
                    inputTokens:      result.InputTokens,
                    outputTokens:     result.OutputTokens,
                    estimatedCostUsd: ClaudeModelPricing.EstimateCostUsd(request.Model, result.InputTokens, result.OutputTokens),
                    isSuccess:        result.Succeeded,
                    errorMessage:     AnthropicErrorDiagnostics.WithDetail(result.ErrorCode, result.ErrorDetail),
                    durationMs:       result.DurationMs,
                    operation:        request.Operation,
                    model:            request.Model,
                    userId:           null,
                    documentId:       request.DocumentId,
                    ct:               CancellationToken.None)
                .ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Reads the Messages envelope. Finds the tool_use block by TYPE and NAME — never content[0] —
    /// so a leading thinking or text block is skipped. Internal for the parsing tests.
    /// </summary>
    internal static (TOutput? Output, string? ErrorCode, int InputTokens, int OutputTokens) ParseResponse<TOutput>(
        string body, string toolName, Func<JsonElement, (TOutput? Output, string? ErrorCode)> readToolInput)
        where TOutput : class
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return (null, LegajoClassificationErrors.InvalidResponse, 0, 0);
        }

        using (document)
        {
            var root = document.RootElement;
            var inputTokens  = 0;
            var outputTokens = 0;

            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                inputTokens  = ReadInt(usage, "input_tokens") ?? 0;
                outputTokens = ReadInt(usage, "output_tokens") ?? 0;
            }

            var stopReason = root.TryGetProperty("stop_reason", out var sr) && sr.ValueKind == JsonValueKind.String
                ? sr.GetString()
                : null;

            if (stopReason == "refusal")
                return (null, LegajoClassificationErrors.Refused, inputTokens, outputTokens);

            if (stopReason == "max_tokens")
                return (null, LegajoClassificationErrors.Truncated, inputTokens, outputTokens);

            if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                return (null, LegajoClassificationErrors.InvalidResponse, inputTokens, outputTokens);

            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object
                    || ReadString(block, "type") != "tool_use"
                    || ReadString(block, "name") != toolName
                    || !block.TryGetProperty("input", out var input)
                    || input.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var (output, error) = readToolInput(input);
                if (error is null && output is null)
                    error = LegajoClassificationErrors.InvalidResponse;

                return (output, error, inputTokens, outputTokens);
            }

            return (null, LegajoClassificationErrors.InvalidResponse, inputTokens, outputTokens);
        }
    }

    private async Task<LegajoMessagesResult<TOutput>> SendCoreAsync<TOutput>(
        LegajoMessagesRequest request,
        Func<JsonElement, (TOutput? Output, string? ErrorCode)> readToolInput,
        Func<CancellationToken, Task<bool>> isStillAllowedAsync,
        CancellationToken ct)
        where TOutput : class
    {
        var inputHash   = Sha256Hex(request.RequestJson);
        var httpClient  = _httpClientFactory.CreateClient(request.HttpClientName);
        var sent        = false;
        int? lastStatus = null;

        LegajoMessagesResult<TOutput> Fail(string code, int? status, string outputHash = "", int inTok = 0,
                                           int outTok = 0, TOutput? output = null) => new()
        {
            Succeeded    = false,
            ErrorCode    = code,
            RequestSent  = sent,
            Output       = output,
            HttpStatus   = status,
            InputHash    = inputHash,
            OutputHash   = outputHash,
            InputTokens  = inTok,
            OutputTokens = outTok,
            DurationMs   = (int)request.Clock.ElapsedMilliseconds
        };

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (attempt > 0)
                await DelayAsync(Backoff[attempt - 1], ct).ConfigureAwait(false);

            // Consent is re-checked before EVERY attempt, retries included: a revoke during a
            // backoff wait must stop the next request, not only the first.
            if (!await isStillAllowedAsync(ct).ConfigureAwait(false))
                return Fail(LegajoClassificationErrors.ConsentRevoked, lastStatus);

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
            httpRequest.Headers.Add("x-api-key", request.ApiKey);
            httpRequest.Headers.Add("anthropic-version", AnthropicVersion);
            httpRequest.Content = new StringContent(request.RequestJson, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                sent = true;
                response = await httpClient.SendAsync(httpRequest, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("{Operation} timed out on attempt {Attempt}", request.Operation, attempt + 1);
                if (request.RetryOnTimeout && attempt < MaxAttempts - 1) continue;
                return Fail(request.TimeoutErrorCode, null);
            }
            catch (HttpRequestException)
            {
                _logger.LogWarning("{Operation} network error on attempt {Attempt}", request.Operation, attempt + 1);
                if (attempt < MaxAttempts - 1) continue;
                return Fail(LegajoClassificationErrors.NetworkError, null);
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                lastStatus = status;
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var outputHash = Sha256Hex(body);

                if (response.IsSuccessStatusCode)
                {
                    var parsed = ParseResponse(body, request.ToolName, readToolInput);
                    if (parsed.ErrorCode is not null)
                    {
                        _logger.LogWarning(
                            "{Operation} response rejected: {ErrorCode} ({ResponseBytes} bytes)",
                            request.Operation, parsed.ErrorCode, body.Length);
                        return Fail(parsed.ErrorCode, status, outputHash, parsed.InputTokens, parsed.OutputTokens,
                                    parsed.Output);
                    }

                    return new LegajoMessagesResult<TOutput>
                    {
                        Succeeded    = true,
                        RequestSent  = true,
                        Output       = parsed.Output,
                        HttpStatus   = status,
                        InputTokens  = parsed.InputTokens,
                        OutputTokens = parsed.OutputTokens,
                        InputHash    = inputHash,
                        OutputHash   = outputHash,
                        DurationMs   = (int)request.Clock.ElapsedMilliseconds
                    };
                }

                // The error body is NOT logged: an API error can echo request content. Only its type and
                // request id are; the clamped message goes to the ApiCallLogs row (ErrorDetail).
                var apiError = AnthropicErrorDiagnostics.Parse(
                    status, body,
                    response.Headers.TryGetValues(AnthropicErrorDiagnostics.RequestIdHeader, out var ids) ? ids.FirstOrDefault() : null);
                _logger.LogWarning(
                    "{Operation} returned HTTP {StatusCode} on attempt {Attempt} ErrorType={ErrorType} RequestId={RequestId}",
                    request.Operation, status, attempt + 1, apiError.ErrorType, apiError.RequestId);

                if (status == 429)
                    return Fail(LegajoClassificationErrors.RateLimited, status, outputHash) with { ErrorDetail = apiError.ToString() };

                if (status >= 500 && attempt < MaxAttempts - 1)
                    continue;

                return Fail(LegajoClassificationErrors.ApiError, status, outputHash) with { ErrorDetail = apiError.ToString() };
            }
        }

        // Unreachable: the last attempt always returns. Kept so the compiler can prove it.
        return Fail(LegajoClassificationErrors.ApiError, lastStatus);
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var number)
            ? number
            : null;

    private static string Sha256Hex(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
