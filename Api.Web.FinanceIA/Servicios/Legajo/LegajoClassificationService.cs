// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.Extensions.Options;

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// <see cref="ILegajoClassificationService"/> over the shared <see cref="LegajoMessagesClient"/>, on
/// the named "ClaudeClient" (120 s timeout).
///
/// <para>
/// The request forces the one tool, with <c>strict: true</c> so the tool input is schema-valid,
/// <c>temperature 0</c>, and NO <c>thinking</c> field — Haiku 4.5 does not think unless asked. The
/// client reads the response by block TYPE, retries (three attempts, 5 s / 15 s), re-checks consent
/// before every attempt, and writes the ApiCallLogs row. The wire format is Phase 2a's, byte for byte
/// (LegajoClassificationPayloadGoldenTests).
/// </para>
///
/// <para>
/// Nothing is logged from the request or the response except status codes, token counts and sizes:
/// both carry the person's paperwork.
/// </para>
/// </summary>
public sealed class LegajoClassificationService : ILegajoClassificationService
{
    /// <summary>Above this size the document goes as images even when every page reads well (ClaudeService's threshold).</summary>
    public const long DocumentBlockMaxBytes = 5 * 1024 * 1024;

    /// <summary>At most this many pages go to the model, in either read mode.</summary>
    public const int MaxPages = 30;

    /// <summary>Resolution of the image read mode.</summary>
    public const int ImageDpi = 100;

    /// <summary>Output ceiling: the tool input is a few hundred tokens.</summary>
    public const int MaxTokens = 1024;

    /// <summary>The named HttpClient the classification is sent on.</summary>
    public const string HttpClientName = "ClaudeClient";

    private const int MaxQuoteLength = 300;
    private const int MaxNoteLength  = 500;

    private readonly LegajoMessagesClient _messages;
    private readonly ILegajoPageRenderer _pageRenderer;
    private readonly LegajoClassificationOptions _options;
    private readonly ILogger<LegajoClassificationService> _logger;
    private readonly string _model;

    /// <summary>Initializes a new instance of <see cref="LegajoClassificationService"/>.</summary>
    public LegajoClassificationService(
        LegajoMessagesClient messages,
        ILegajoPageRenderer pageRenderer,
        IOptions<LegajoClassificationOptions> options,
        ILogger<LegajoClassificationService> logger)
    {
        _messages     = messages;
        _pageRenderer = pageRenderer;
        _options      = options.Value;
        _logger       = logger;

        // Guarded like the extraction: the classification also forces its tool at temperature 0, so
        // an id that rejects either falls back to the default with one Error (LegajoModelSupport).
        _model = LegajoModelSupport.ResolveOrDefault(
            _options.LegajoClassificationModel,
            LegajoClassificationOptions.DefaultModel,
            LegajoModelSupport.ClassificationModelSetting,
            logger);
    }

    /// <summary>The model id this instance sends.</summary>
    public string Model => _model;

    /// <inheritdoc />
    public async Task<LegajoClassificationResult> ClassifyAsync(
        LegajoCallContext context,
        byte[] pdfBytes,
        PageTextQualityReport quality,
        Func<CancellationToken, Task<bool>> isStillAllowedAsync,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(pdfBytes);
        ArgumentNullException.ThrowIfNull(quality);
        ArgumentNullException.ThrowIfNull(isStillAllowedAsync);

        var stopwatch = Stopwatch.StartNew();
        var readMode  = ChooseReadMode(pdfBytes.LongLength, quality);

        LegajoClassificationResult NotSent(string code) => new()
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
            _logger.LogError("Legajo classification is not configured: Anthropic:ApiKey is missing");
            return NotSent(LegajoClassificationErrors.NotConfigured);
        }

        List<JsonNode> contentBlocks;
        try
        {
            contentBlocks = BuildContentBlocks(pdfBytes, quality, readMode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                "Legajo classification could not render the PDF ({ExceptionType}); no request sent",
                ex.GetType().Name);
            return NotSent(LegajoClassificationErrors.PdfUnreadable);
        }

        var request = new LegajoMessagesRequest
        {
            Operation      = LegajoMessagesClient.ClassificationOperation,
            TenantId       = context.TenantId,
            DocumentId     = context.DocumentId,
            Model          = Model,
            ApiKey         = _options.ApiKey,
            HttpClientName = HttpClientName,
            RequestJson    = BuildRequestJson(Model, contentBlocks),
            ToolName       = LegajoClassificationPrompt.ToolName,
            Clock          = stopwatch
        };

        var sent = await _messages
            .SendAsync(request, ReadToolInput, isStillAllowedAsync, ct)
            .ConfigureAwait(false);

        return new LegajoClassificationResult
        {
            Succeeded    = sent.Succeeded,
            ErrorCode    = sent.ErrorCode,
            RequestSent  = sent.RequestSent,
            Output       = sent.Succeeded ? sent.Output : null,
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
    /// Document when every page reads well, the file is small enough for a document block, and it
    /// has at most <see cref="MaxPages"/> pages; Images otherwise.
    /// </summary>
    public static string ChooseReadMode(long sizeBytes, PageTextQualityReport quality)
        => quality.AllPagesGood && sizeBytes < DocumentBlockMaxBytes && quality.Pages.Count <= MaxPages
            ? LegajoReadModes.Document
            : LegajoReadModes.Images;

    /// <summary>
    /// Serializes the request body. Internal so a test can assert what it contains — and what it
    /// does not (the legajo's name and CUIT).
    /// </summary>
    internal static string BuildRequestJson(string model, IEnumerable<JsonNode> contentBlocks)
        => LegajoMessagesClient.BuildRequestJson(
            model,
            MaxTokens,
            LegajoClassificationPrompt.SystemPrompt,
            LegajoClassificationPrompt.ToolName,
            LegajoClassificationPrompt.ToolDescription,
            LegajoClassificationPrompt.ToolSchemaJson,
            strict: true,
            contentBlocks);

    /// <summary>
    /// Reads the Messages envelope through the shared client's parser. Internal for the parsing tests.
    /// </summary>
    internal static (LegajoClassificationOutput? Output, string? ErrorCode, int InputTokens, int OutputTokens)
        ParseResponse(string body)
        => LegajoMessagesClient.ParseResponse(body, LegajoClassificationPrompt.ToolName, ReadToolInput);

    private static (LegajoClassificationOutput? Output, string? ErrorCode) ReadToolInput(JsonElement input)
    {
        var output = MapToolInput(input);
        return output is null ? (null, LegajoClassificationErrors.InvalidResponse) : (output, null);
    }

    private List<JsonNode> BuildContentBlocks(byte[] pdfBytes, PageTextQualityReport quality, string readMode)
    {
        var blocks = new List<JsonNode>();
        var totalPages = quality.PageCount ?? 0;

        if (readMode == LegajoReadModes.Document)
        {
            blocks.Add(new JsonObject
            {
                ["type"] = "document",
                ["source"] = new JsonObject
                {
                    ["type"]       = "base64",
                    ["media_type"] = "application/pdf",
                    ["data"]       = Convert.ToBase64String(pdfBytes)
                }
            });
            blocks.Add(new JsonObject
            {
                ["type"] = "text",
                ["text"] = LegajoClassificationPrompt.BuildUserText(totalPages, totalPages)
            });
            return blocks;
        }

        var jpegs = _pageRenderer.RenderJpegs(pdfBytes, ImageDpi, MaxPages);
        if (jpegs.Count == 0)
            throw new InvalidOperationException("The PDF rendered no page.");

        foreach (var jpeg in jpegs)
        {
            blocks.Add(new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject
                {
                    ["type"]       = "base64",
                    ["media_type"] = "image/jpeg",
                    ["data"]       = Convert.ToBase64String(jpeg)
                }
            });
        }

        blocks.Add(new JsonObject
        {
            ["type"] = "text",
            ["text"] = LegajoClassificationPrompt.BuildUserText(Math.Max(totalPages, jpegs.Count), jpegs.Count)
        });

        return blocks;
    }

    private static LegajoClassificationOutput? MapToolInput(JsonElement input)
    {
        var rawDocumentType = ReadString(input, "document_type");
        if (rawDocumentType is null)
            return null;

        // Canonicalised case-insensitively BEFORE anything compares it: "ganancias" is Ganancias, and
        // the type-mismatch rule must not fire on casing.
        var documentType = Canonical(rawDocumentType, LegajoClassificationPrompt.DetectableTypes)
                           ?? LegajoClassificationPrompt.UnknownType;

        string? cuit  = null;
        int?    page  = null;
        string? quote = null;
        if (input.TryGetProperty("holder_cuit", out var holder) && holder.ValueKind == JsonValueKind.Object)
        {
            cuit  = LegajoCuit.Digits(ReadString(holder, "cuit"));
            page  = ReadInt(holder, "page") is { } p && p >= 1 ? p : null;
            quote = Truncate(ReadString(holder, "quote"), MaxQuoteLength);
        }

        // "Desconocido" (or anything else) canonicalises to null: the column admits the three types only.
        var engagement = Canonical(ReadString(input, "engagement_type"), EngagementTypes.Known);

        var segments = new List<LegajoClassificationSegment>();
        if (input.TryGetProperty("segments", out var segs) && segs.ValueKind == JsonValueKind.Array)
        {
            foreach (var seg in segs.EnumerateArray())
            {
                if (seg.ValueKind != JsonValueKind.Object) continue;

                var type = ReadString(seg, "document_type");
                var from = ReadInt(seg, "from_page");
                var to   = ReadInt(seg, "to_page");
                if (type is null || from is null || to is null || from < 1 || to < from) continue;

                type = Canonical(type, LegajoClassificationPrompt.DetectableTypes) ?? LegajoClassificationPrompt.UnknownType;

                segments.Add(new LegajoClassificationSegment(type, from.Value, to.Value));
            }
        }

        var fiscalYear = ReadInt(input, "fiscal_year") is { } fy && fy is >= 1990 and <= 2100 ? fy : (int?)null;

        var confidence = input.TryGetProperty("confidence", out var conf) && conf.ValueKind == JsonValueKind.Number
            && conf.TryGetDecimal(out var c)
                ? Math.Clamp(c, 0m, 1m)
                : 0m;

        return new LegajoClassificationOutput
        {
            DocumentType    = documentType,
            FiscalYear      = fiscalYear,
            PeriodEnd       = ReadDate(input, "period_end"),
            MeasurementDate = ReadDate(input, "measurement_date"),
            HolderCuit      = cuit,
            HolderCuitPage  = cuit is null ? null : page,
            HolderCuitQuote = cuit is null ? null : quote,
            EngagementType  = engagement,
            IsComposite     = input.TryGetProperty("is_composite", out var comp) && comp.ValueKind == JsonValueKind.True,
            Segments        = segments,
            Confidence      = Math.Round(confidence, 4),
            Note            = Truncate(ReadString(input, "note"), MaxNoteLength)
        };
    }

    /// <summary>The canonical spelling of <paramref name="value"/> in <paramref name="known"/>, case-insensitively and trimmed; null when absent.</summary>
    private static string? Canonical(string? value, IReadOnlyList<string> known)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : known.FirstOrDefault(k => string.Equals(k, value.Trim(), StringComparison.OrdinalIgnoreCase));

    private static DateOnly? ReadDate(JsonElement element, string name)
        => DateOnly.TryParseExact(ReadString(element, name), "yyyy-MM-dd", CultureInfo.InvariantCulture,
               DateTimeStyles.None, out var date)
           && date.Year is >= 1990 and <= 2100
            ? date
            : null;

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var number)
            ? number
            : null;

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        return trimmed.Length > max ? trimmed[..max] : trimmed;
    }

}
