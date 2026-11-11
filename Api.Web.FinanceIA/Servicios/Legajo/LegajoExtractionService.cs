// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.Extensions.Options;

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// <see cref="ILegajoExtractionService"/> over the shared <see cref="LegajoMessagesClient"/>, on its
/// own named client "ClaudeLegajoExtractionClient" (<c>Legajo:ExtractionTimeoutSeconds</c>, default
/// 300 s: up to 16k output tokens outlast the classification's 120 s).
///
/// <para>
/// STRICT TOOL USE IS OFF, deliberately. The request still forces the one tool
/// (<c>tool_choice {type:"tool"}</c>, <c>temperature 0</c>, no <c>thinking</c>), and the schema is
/// written in the strict dialect, but without <c>strict: true</c>: every leaf of these schemas is a
/// nullable <c>{value, page, quote, confidence}</c> object — dozens of union-typed parameters — and
/// strict mode compiles a grammar per schema whose complexity limit only a paid live call can probe; a
/// rejected schema would fail EVERY extraction. Strict mode could not express what matters here anyway
/// (pages ≥ 1, confidence 0..1, dates, CUITs, the arithmetic), so <see cref="LegajoToolInputReader"/>
/// validates the tool input in full, and an invalid one is treated like a malformed answer: the
/// processor retries the pass reading the pages as images.
/// </para>
///
/// <para>
/// Read mode: Document when every page reads well, the file is under 5 MB and it has at most 40 pages;
/// Images otherwise — every page rendered at <c>Legajo:ExtractionImageDpi</c> (200) and capped at a
/// 1568 px long edge (the most the model reads; a request with more than 20 images rejects any image
/// over 2000 px). More than 40 pages fails without a call, as does a payload over 30 MB.
/// </para>
///
/// <para>
/// The request carries the document and its page count — never the legajo's name, CUIT or declared
/// data. Nothing is logged from the request or the response except codes, counts and sizes.
/// </para>
/// </summary>
public sealed class LegajoExtractionService : ILegajoExtractionService
{
    /// <summary>The named HttpClient extraction calls are sent on.</summary>
    public const string HttpClientName = "ClaudeLegajoExtractionClient";

    /// <summary>Above this size the document goes as images even when every page reads well.</summary>
    public const long DocumentBlockMaxBytes = 5 * 1024 * 1024;

    /// <summary>The most pages an extraction reads, in either mode. More fails with <see cref="LegajoExtractionErrors.TooManyPages"/>.</summary>
    public const int MaxPages = 40;

    /// <summary>The long edge every rendered page is capped at (the standard vision tier's limit).</summary>
    public const int MaxImageLongEdgePx = 1568;

    /// <summary>The base64 payload above which no request is sent (the API's limit is 32 MB for the whole body).</summary>
    public const long MaxPayloadBytes = 30L * 1024 * 1024;

    private readonly LegajoMessagesClient _messages;
    private readonly ILegajoPageRenderer _pageRenderer;
    private readonly LegajoExtractionOptions _options;
    private readonly LegajoOptions _legajoOptions;
    private readonly ILogger<LegajoExtractionService> _logger;
    private readonly string _model;

    /// <summary>Initializes a new instance of <see cref="LegajoExtractionService"/>.</summary>
    public LegajoExtractionService(
        LegajoMessagesClient messages,
        ILegajoPageRenderer pageRenderer,
        IOptions<LegajoExtractionOptions> options,
        IOptions<LegajoOptions> legajoOptions,
        ILogger<LegajoExtractionService> logger)
    {
        _messages      = messages;
        _pageRenderer  = pageRenderer;
        _options       = options.Value;
        _legajoOptions = legajoOptions.Value;
        _logger        = logger;

        // Guarded: an id that rejects the forced tool_choice / temperature 0 every legajo request
        // carries (claude-sonnet-5-5, claude-sonnet-5, anything unchecked) would fail EVERY
        // extraction. It falls back to the default and logs an Error once (LegajoModelSupport).
        _model = LegajoModelSupport.ResolveOrDefault(
            _options.LegajoExtractionModel,
            LegajoExtractionOptions.DefaultModel,
            LegajoModelSupport.ExtractionModelSetting,
            logger);
    }

    /// <inheritdoc />
    public string Model => _model;

    /// <summary>Document when every page is Good, the file is under 5 MB and has at most 40 pages; Images otherwise.</summary>
    public static string ChooseReadMode(long sizeBytes, PageTextQualityReport quality)
    {
        ArgumentNullException.ThrowIfNull(quality);

        return quality.AllPagesGood && sizeBytes < DocumentBlockMaxBytes && quality.Pages.Count <= MaxPages
            ? LegajoReadModes.Document
            : LegajoReadModes.Images;
    }

    /// <inheritdoc />
    public async Task<LegajoExtractionPassResult> ExtractAsync(
        LegajoCallContext context,
        LegajoExtractionSchema schema,
        byte[] pdfBytes,
        PageTextQualityReport quality,
        string readMode,
        Func<CancellationToken, Task<bool>> isStillAllowedAsync,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(pdfBytes);
        ArgumentNullException.ThrowIfNull(quality);
        ArgumentNullException.ThrowIfNull(isStillAllowedAsync);

        var stopwatch = Stopwatch.StartNew();

        LegajoExtractionPassResult NotSent(string code) => new()
        {
            Succeeded   = false,
            ErrorCode   = code,
            RequestSent = false,
            Model       = Model,
            ReadMode    = readMode,
            DurationMs  = (int)stopwatch.ElapsedMilliseconds
        };

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _logger.LogError("Legajo extraction is not configured: Anthropic:ApiKey is missing");
            return NotSent(LegajoClassificationErrors.NotConfigured);
        }

        if (quality.PageCount > MaxPages)
            return NotSent(LegajoExtractionErrors.TooManyPages);

        List<JsonNode> contentBlocks;
        try
        {
            var built = BuildContentBlocks(pdfBytes, quality, readMode);
            if (built.ErrorCode is not null)
                return NotSent(built.ErrorCode);

            contentBlocks = built.Blocks;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                "Legajo extraction could not render the PDF ({ExceptionType}); no request sent",
                ex.GetType().Name);
            return NotSent(LegajoClassificationErrors.PdfUnreadable);
        }

        var request = new LegajoMessagesRequest
        {
            Operation      = LegajoMessagesClient.ExtractionOperation,
            TenantId       = context.TenantId,
            DocumentId     = context.DocumentId,
            Model          = Model,
            ApiKey         = _options.ApiKey,
            HttpClientName = HttpClientName,
            RequestJson    = BuildRequestJson(schema, Model, MaxTokensFor(schema, _legajoOptions), contentBlocks),
            ToolName       = schema.ToolName,
            Clock          = stopwatch,
            RetryOnTimeout   = false,
            TimeoutErrorCode = LegajoExtractionErrors.Timeout
        };

        var sent = await _messages
            .SendAsync(request, input => ReadToolInput(schema, input), isStillAllowedAsync, ct)
            .ConfigureAwait(false);

        if (sent.Output is { IsValid: false } rejected)
        {
            _logger.LogWarning(
                "Legajo extraction output for document {DocumentId} tenant {TenantId} rejected: {ViolationCount} violation(s)",
                context.DocumentId, context.TenantId, rejected.Violations.Count);
        }

        return new LegajoExtractionPassResult
        {
            Succeeded    = sent.Succeeded,
            ErrorCode    = sent.ErrorCode,
            RequestSent  = sent.RequestSent,
            Reading      = sent.Output,
            Model        = Model,
            ReadMode     = readMode,
            HttpStatus   = sent.HttpStatus,
            InputTokens  = sent.InputTokens,
            OutputTokens = sent.OutputTokens,
            InputHash    = sent.InputHash,
            OutputHash   = sent.OutputHash,
            DurationMs   = sent.DurationMs
        };
    }

    /// <summary>
    /// The request's max_tokens: the schema's own ceiling when it declares one, else
    /// <c>Legajo:ExtractionMaxTokens</c>; never below 1024.
    /// </summary>
    public static int MaxTokensFor(LegajoExtractionSchema schema, LegajoOptions options)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(options);

        return Math.Max(1024, schema.MaxTokens ?? options.ExtractionMaxTokens);
    }

    /// <summary>
    /// Serializes the request body: the forced tool WITHOUT <c>strict</c> (see the class remarks).
    /// Internal so a test can assert what it contains — and what it does not.
    /// </summary>
    internal static string BuildRequestJson(
        LegajoExtractionSchema schema, string model, int maxTokens, IEnumerable<JsonNode> contentBlocks)
        => LegajoMessagesClient.BuildRequestJson(
            model,
            maxTokens,
            schema.SystemPrompt,
            schema.ToolName,
            schema.ToolDescription,
            schema.ToolSchemaJson,
            strict: false,
            contentBlocks);

    /// <summary>The tool input read against the schema: the reading, and InvalidOutput when it has violations.</summary>
    internal static (LegajoExtractionReading? Output, string? ErrorCode) ReadToolInput(
        LegajoExtractionSchema schema, JsonElement input)
    {
        var reading = LegajoToolInputReader.Read(schema.Root, input);
        return (reading, reading.IsValid ? null : LegajoExtractionErrors.InvalidOutput);
    }

    private (List<JsonNode> Blocks, string? ErrorCode) BuildContentBlocks(
        byte[] pdfBytes, PageTextQualityReport quality, string readMode)
    {
        var blocks = new List<JsonNode>();

        if (readMode == LegajoReadModes.Document)
        {
            var data = Convert.ToBase64String(pdfBytes);
            if (data.Length > MaxPayloadBytes)
                return (blocks, LegajoExtractionErrors.RequestTooLarge);

            blocks.Add(new JsonObject
            {
                ["type"] = "document",
                ["source"] = new JsonObject
                {
                    ["type"]       = "base64",
                    ["media_type"] = "application/pdf",
                    ["data"]       = data
                }
            });
            blocks.Add(new JsonObject
            {
                ["type"] = "text",
                ["text"] = LegajoExtractionPrompts.BuildUserText(quality.PageCount ?? 0)
            });
            return (blocks, null);
        }

        // One page beyond the cap is rendered to tell "exactly 40" from "more than 40" when the page
        // count is unknown (a PDF the text analyzer could not open).
        var dpi   = Math.Max(72, _legajoOptions.ExtractionImageDpi);
        var jpegs = _pageRenderer.RenderJpegs(pdfBytes, dpi, MaxPages + 1, MaxImageLongEdgePx);
        if (jpegs.Count == 0)
            throw new InvalidOperationException("The PDF rendered no page.");
        if (jpegs.Count > MaxPages)
            return (blocks, LegajoExtractionErrors.TooManyPages);

        long payload = 0;
        foreach (var jpeg in jpegs)
        {
            var data = Convert.ToBase64String(jpeg);
            payload += data.Length;
            if (payload > MaxPayloadBytes)
                return (blocks, LegajoExtractionErrors.RequestTooLarge);

            blocks.Add(new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject
                {
                    ["type"]       = "base64",
                    ["media_type"] = "image/jpeg",
                    ["data"]       = data
                }
            });
        }

        blocks.Add(new JsonObject
        {
            ["type"] = "text",
            ["text"] = LegajoExtractionPrompts.BuildUserText(jpegs.Count)
        });

        return (blocks, null);
    }
}
