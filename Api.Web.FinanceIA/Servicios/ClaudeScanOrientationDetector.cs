// Project : Api.Web.FinanceIA
// Created : 2026-11-03

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.Legajo;
using PDFtoImage;
using SkiaSharp;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// <see cref="IScanOrientationDetector"/> as ONE small call to <see cref="Model"/>: a low-resolution
/// thumbnail of each page asked about, and a forced tool that reports, per image, the clockwise turn
/// that makes it read upright.
///
/// <para>
/// WHY A MODEL AND NOT TESSERACT OSD: the .NET Tesseract packages (<c>Tesseract</c> 5.2.0,
/// <c>TesseractOCR</c> 5.5.2) ship Windows DLLs only, so the ubuntu CI leg could not load them without
/// a manual native install, and OSD also needs an <c>osd.traineddata</c> neither package carries. A
/// thumbnail of a scanned page costs a few hundred Haiku tokens.
/// </para>
///
/// <para>
/// The request is built by <see cref="LegajoMessagesClient.BuildRequestJson"/> — forced
/// <c>tool_choice</c>, <c>strict</c> tool, <c>temperature 0</c> — which <see cref="Model"/> accepts
/// (<see cref="Models.LegajoModelSupport.ForcedToolUseModels"/>; a test pins it). It goes on the named
/// "ClaudeClient" with ONE attempt bounded by <see cref="Timeout"/> and no retry: the analysis is
/// synchronous for the Portal and the App Service front end cuts a request at ~230 s.
/// </para>
///
/// <para>
/// Nothing identifying is written into the prompt: the text is fixed, the images are the same pages the
/// extraction already reads. Nothing from the request or the answer is logged — status codes, counts and
/// exception TYPES only.
/// </para>
/// </summary>
public sealed class ClaudeScanOrientationDetector : IScanOrientationDetector
{
    /// <summary>The model that judges orientation. The ONLY place this id lives for this purpose.</summary>
    internal const string Model = "claude-haiku-4-5";

    /// <summary>The named HttpClient the call is sent on (the legajo's; 120 s client timeout).</summary>
    public const string HttpClientName = "ClaudeClient";

    /// <summary>Long edge of a thumbnail, in pixels.</summary>
    internal const int ThumbnailLongEdgePx = 512;

    /// <summary>At most this many pages in one call (the API's image cap for a 200k-context model).</summary>
    internal const int MaxPages = 100;

    /// <summary>The tool the model must call.</summary>
    internal const string ToolName = "report_page_orientation";

    private const int    ThumbnailDpi     = 72;
    private const int    JpegQuality      = 75;
    private const int    MaxTokens        = 1024;
    private const string ApiUrl           = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";

    internal const string SystemPrompt =
        "You judge the orientation of scanned document pages. For each numbered image, decide how many " +
        "degrees the image must be turned CLOCKWISE for its printed text to read normally, left to right " +
        "and top to bottom: 0 (already upright), 90, 180 or 270. Text lines that run from the bottom of " +
        "the image to the top need 90; lines that run from the top to the bottom need 270; text that is " +
        "upside down needs 180. Judge from the direction of the text lines, headings and table headers, " +
        "not from the shape of the page: a wide table is often printed sideways on a tall page. Report " +
        "every image you can judge, by its number; leave out an image you cannot judge. Call the tool " +
        "exactly once.";

    internal const string ToolDescription =
        "Reports, for each numbered image, the clockwise turn in degrees (0, 90, 180 or 270) that makes its text read upright.";

    internal const string ToolSchemaJson =
        "{\"type\":\"object\",\"properties\":{\"pages\":{\"type\":\"array\",\"items\":{\"type\":\"object\"," +
        "\"properties\":{\"index\":{\"type\":\"integer\",\"description\":\"The image number, starting at 1.\"}," +
        "\"rotation\":{\"type\":\"integer\",\"description\":\"Clockwise degrees: 0, 90, 180 or 270.\"}}," +
        "\"required\":[\"index\",\"rotation\"],\"additionalProperties\":false}}}," +
        "\"required\":[\"pages\"],\"additionalProperties\":false}";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ClaudeScanOrientationDetector> _logger;

    /// <summary>Initializes a new instance of <see cref="ClaudeScanOrientationDetector"/>.</summary>
    public ClaudeScanOrientationDetector(
        IHttpClientFactory httpClientFactory,
        ILogger<ClaudeScanOrientationDetector> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger            = logger;
    }

    /// <summary>The bound on the call (thumbnails excluded). A seam so a test does not wait 30 s.</summary>
    internal TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public async Task<ScanOrientationResult> DetectAsync(
        byte[]             pdfBytes,
        IReadOnlyList<int> pageIndexes,
        string             apiKey,
        CancellationToken  ct)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);
        ArgumentNullException.ThrowIfNull(pageIndexes);

        var stopwatch = Stopwatch.StartNew();
        var asked     = pageIndexes.Take(MaxPages).ToList();
        if (asked.Count == 0 || string.IsNullOrWhiteSpace(apiKey))
            return ScanOrientationResult.Unknown(pageIndexes.Count);

        // ── Thumbnails: no request when they cannot be made ──────────────────────
        List<JsonNode> blocks;
        try
        {
            blocks = BuildContentBlocks(RenderThumbnails(pdfBytes, asked));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                "Scan orientation not detected: the thumbnails could not be rendered ({ExceptionType}); the pages are sent as they are.",
                ex.GetType().Name);
            return ScanOrientationResult.Unknown(pageIndexes.Count);
        }

        var requestJson = BuildRequestJson(blocks);

        // ── One attempt, bounded ─────────────────────────────────────────────────
        int?    status = null;
        string? body   = null;
        string? error  = null;
        AnthropicErrorInfo? apiError = null;
        try
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(Timeout);

            using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
            request.Headers.Add("x-api-key", apiKey);
            request.Headers.Add("anthropic-version", AnthropicVersion);
            request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

            using var response = await _httpClientFactory.CreateClient(HttpClientName)
                .SendAsync(request, attemptCts.Token)
                .ConfigureAwait(false);

            status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
                body = await response.Content.ReadAsStringAsync(attemptCts.Token).ConfigureAwait(false);
            else
            {
                error = status == 429 ? LegajoClassificationErrors.RateLimited : LegajoClassificationErrors.ApiError;
                apiError = await AnthropicErrorDiagnostics.ReadAsync(response, attemptCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            error = LegajoClassificationErrors.Timeout;
        }
        catch (HttpRequestException)
        {
            error = LegajoClassificationErrors.NetworkError;
        }

        // ── Read the answer ──────────────────────────────────────────────────────
        var rotations    = new int?[pageIndexes.Count];
        var inputTokens  = 0;
        var outputTokens = 0;
        if (body is not null)
        {
            var parsed = LegajoMessagesClient.ParseResponse(body, ToolName, input => ReadToolInput(input, asked.Count));
            inputTokens  = parsed.InputTokens;
            outputTokens = parsed.OutputTokens;
            error        = parsed.ErrorCode;

            if (parsed.Output is not null)
            {
                foreach (var (image, rotation) in parsed.Output)
                    rotations[image - 1] = rotation; // image is 1..asked.Count (ReadToolInput checks)
            }
        }

        var answered = rotations.Count(r => r.HasValue);
        if (error is not null)
        {
            // The API's error type and request id, never its message (it could echo the request).
            _logger.LogWarning(
                "Scan orientation not detected ({ErrorCode}, status {StatusCode}); {Pages} page(s) are sent as they are. " +
                "ErrorType={ErrorType} RequestId={RequestId}",
                error, status, asked.Count, apiError?.ErrorType, apiError?.RequestId);
        }

        var usage = new ScanOrientationUsage
        {
            Model         = Model,
            InputTokens   = inputTokens,
            OutputTokens  = outputTokens,
            DurationMs    = (int)stopwatch.ElapsedMilliseconds,
            Succeeded     = error is null,
            HttpStatus    = status,
            ErrorCode     = error,
            ErrorDetail   = error is null ? null : apiError?.ToString(),
            PagesAsked    = asked.Count,
            PagesAnswered = answered,
        };

        return new ScanOrientationResult(rotations, usage);
    }

    /// <summary>The request body for the given content blocks. Internal for the payload tests.</summary>
    internal static string BuildRequestJson(IEnumerable<JsonNode> contentBlocks) =>
        LegajoMessagesClient.BuildRequestJson(
            Model, MaxTokens, SystemPrompt, ToolName, ToolDescription, ToolSchemaJson, strict: true, contentBlocks);

    /// <summary>
    /// "Image N" before each thumbnail, then the instruction. Fixed text only — nothing read from the
    /// document. Internal for the payload tests.
    /// </summary>
    internal static List<JsonNode> BuildContentBlocks(IReadOnlyList<byte[]> thumbnails)
    {
        var blocks = new List<JsonNode>();
        for (var i = 0; i < thumbnails.Count; i++)
        {
            blocks.Add(new JsonObject { ["type"] = "text", ["text"] = $"Image {i + 1}" });
            blocks.Add(new JsonObject
            {
                ["type"]   = "image",
                ["source"] = new JsonObject
                {
                    ["type"]       = "base64",
                    ["media_type"] = "image/jpeg",
                    ["data"]       = Convert.ToBase64String(thumbnails[i])
                }
            });
        }

        blocks.Add(new JsonObject
        {
            ["type"] = "text",
            ["text"] = $"Report the clockwise turn that makes each of these {thumbnails.Count} images read upright."
        });

        return blocks;
    }

    /// <summary>
    /// The thumbnail of each page in <paramref name="pageIndexes"/>, rendered straight at its long-edge
    /// cap (never at full resolution first), JPEG. Internal for the tests.
    /// </summary>
    internal static List<byte[]> RenderThumbnails(byte[] pdfBytes, IReadOnlyList<int> pageIndexes)
    {
        var thumbnails = new List<byte[]>(pageIndexes.Count);

#pragma warning disable CA1416 // PDFtoImage is supported on Windows, Linux and macOS — all target platforms
        var sizes = Conversion.GetPageSizes(pdfBytes);
        foreach (var page in pageIndexes)
        {
            if (page < 0 || page >= sizes.Count)
                throw new ArgumentOutOfRangeException(nameof(pageIndexes), page, "The document has no such page.");

            using var bitmap  = Conversion.ToImage(pdfBytes, page: page,
                options: PdfToImagePageRenderer.OptionsFor(sizes[page], ThumbnailDpi, ThumbnailLongEdgePx));
            using var image   = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
            thumbnails.Add(encoded.ToArray());
        }
#pragma warning restore CA1416

        return thumbnails;
    }

    /// <summary>
    /// The tool input as (image number, rotation) pairs. An entry with an image number outside the
    /// request, a rotation other than 0/90/180/270, or an image reported twice is dropped (twice: both
    /// answers, since they disagree or repeat) — that image stays unknown. An answer with no usable
    /// entry still succeeds: the model may judge none.
    /// </summary>
    internal static (List<(int Image, int Rotation)>? Output, string? ErrorCode) ReadToolInput(JsonElement input, int images)
    {
        if (!input.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array)
            return (null, LegajoClassificationErrors.InvalidResponse);

        var seen  = new Dictionary<int, int>();
        var twice = new HashSet<int>();
        foreach (var entry in pages.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !TryInt(entry, "index", out var image)
                || !TryInt(entry, "rotation", out var rotation)
                || image < 1 || image > images
                || rotation is not (0 or 90 or 180 or 270))
            {
                continue;
            }

            if (!seen.TryAdd(image, rotation))
                twice.Add(image);
        }

        return (seen.Where(kv => !twice.Contains(kv.Key)).Select(kv => (kv.Key, kv.Value)).ToList(), null);
    }

    private static bool TryInt(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out value);
    }
}
