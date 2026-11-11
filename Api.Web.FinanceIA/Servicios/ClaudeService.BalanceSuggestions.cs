// Project : Api.Web.FinanceIA
// Created : 2026-11-03

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Api.Web.FinanceIA.Clases.Exceptions;
using Api.Web.FinanceIA.Servicios.BalanceCorrections;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// The balance-correction suggestion (Phase 10-F4a): ONE targeted question to the extraction model about
/// the figures of the failed integrity checks, over the page images the second extraction pass builds.
/// </summary>
/// <remarks>
/// Nothing here is forked from the extraction. The pages go through the second pass's own planning
/// (<see cref="PlanSecondPassAsync"/>: glyph orientation, the scan-orientation detector, wide pages also
/// as halves, the 3000 px cap) and renderer (<see cref="RenderPdfPages"/>), at the second pass's tiers;
/// the images are named with <see cref="DescribeSplitImages"/> and read under the same two rules; the
/// request is the extraction model's spec (<see cref="BuildExtractionRequestJson(ExtractionModelSelection, string, List{object})"/>)
/// on the extraction's client and timeout (<see cref="SendExtractionRequestAsync"/>); the answer is read
/// like the extraction's (<see cref="ReadAnswer"/>: stop_reason first, then the first TEXT block). Only the
/// system prompt and the closing text are its own.
/// </remarks>
public sealed partial class ClaudeService
{
    /// <summary>
    /// ONE attempt. The suggestion is synchronous and the App Service front end cuts a request at ~230 s,
    /// while one attempt of a 5.x model may take its whole 300 s timeout: the extraction's three attempts
    /// with backoff cannot fit. The analyst can ask again.
    /// </summary>
    internal const int SuggestionAttempts = 1;

    // The label the shared reader's messages carry for this request.
    private const string SuggestionOperationLabel = "correction suggestion";

    // ReplyFailure codes of this request (the reader's own codes are AnswerMaxTokens & co.).
    internal const string SuggestionNotConfigured   = "not_configured";
    internal const string SuggestionRenderFailed    = "render_failed";
    internal const string SuggestionTimeout         = "timeout";
    internal const string SuggestionNetworkError    = "network_error";
    internal const string SuggestionInvalidResponse = "invalid_response";

    /// <summary>
    /// The suggestion's fixed system prompt. It names no company and carries no figure: the figures and
    /// label hints travel in the closing text block, the identities nowhere.
    /// </summary>
    internal const string SuggestionSystemPrompt =
        "You check specific figures of the balance sheet (estados contables) of an Argentine company against " +
        "images of its pages. You do not extract the whole statement: you answer only about the figures and the " +
        "rubro lists you are asked for.\n\n" +
        "How to read:\n" +
        "- Use only the CURRENT fiscal year column, never the comparative (prior year) column.\n" +
        "- Read each figure from the half of the page and the row where its own label is printed; never take a " +
        "value from the other half or from a neighbouring row.\n" +
        "- A cell printed as '-', '–' or left empty next to a label is zero for that label.\n" +
        "- Report amounts exactly in the unit printed on the document (do not convert thousands or millions), " +
        "as plain JSON numbers: no thousands separators, '.' as the decimal separator, a negative amount with a " +
        "leading '-' (an amount printed in parentheses is negative).\n" +
        "- A label hint is the usual wording; the document may word it slightly differently (for example " +
        "'Total del Pasivo Corriente'). Use the line that is that total.\n\n" +
        "For each figure return: path (as given); printed_value (null when you cannot find it); matches_stored " +
        "(true when the printed amount equals the stored value you were given); image (the 1-based number of the " +
        "image you read it from, counting the images in the order they are attached); half ('full' when that " +
        "image is a whole page, 'left' or 'right' when it is a half); band ([top, bottom] of that row on that " +
        "image, as fractions of the image height, 0 at the top edge and 1 at the bottom edge); label (the label " +
        "exactly as printed, at most 80 characters).\n\n" +
        "For each rubro list return every line printed under that rubro in the current year column, in printed " +
        "order, with the same members plus stored_index: the index [i] of the stored line it corresponds to " +
        "(match by amount and position), or null when no stored line corresponds to it. A stored line that no " +
        "printed line corresponds to is simply not referenced.\n\n" +
        "Answer with ONLY one JSON object, no prose and no Markdown, in exactly this shape:\n" +
        "{\"fields\":[{\"path\":\"current_liabilities\",\"printed_value\":0,\"matches_stored\":true,\"image\":1," +
        "\"half\":\"full\",\"band\":[0.0,0.0],\"label\":\"...\"}]," +
        "\"items\":[{\"array\":\"current_liabilities_items\",\"lines\":[{\"stored_index\":0,\"printed_value\":0," +
        "\"matches_stored\":true,\"image\":1,\"half\":\"left\",\"band\":[0.0,0.0],\"label\":\"...\"}]}]}";

    /// <summary>
    /// Appended to the closing text when net_profit_reconciliation is asked about (and only then, so every
    /// other question is the same text as before): the pre-tax line may legitimately be missing, and the net
    /// result's sign is what the parentheses say.
    /// </summary>
    internal const string NetProfitQuestionNote =
        "Some statements print no line for result_before_tax (no subtotal before income tax, or one that " +
        "leaves out financial results or RECPAM shown after the tax): if you cannot find that line, return " +
        "printed_value null for it — that is expected. Read net_profit with its printed sign: an amount in " +
        "parentheses is negative.";

    /// <summary>
    /// Added after <see cref="NetProfitQuestionNote"/> only when the stored net_profit_reconciliation recorded a
    /// column mix; every other question is byte-identical to before.
    /// </summary>
    internal const string NetProfitColumnMixNote =
        "The stored result_before_tax and income_tax add up to the PRIOR year's net result: they were most likely " +
        "read from the comparative column. Read both from the current fiscal year column only; if that column " +
        "prints no such line, return printed_value null for it.";

    private static readonly JsonSerializerOptions SuggestionAnswerOptions = new()
    {
        NumberHandling              = JsonNumberHandling.AllowReadingFromString,
        PropertyNameCaseInsensitive = true,
    };

    /// <inheritdoc />
    public async Task<BalanceSuggestionReply> SuggestBalanceCorrectionsAsync(
        byte[] pdfBytes, BalanceSuggestionQuestion question, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);
        ArgumentNullException.ThrowIfNull(question);

        var model  = _extraction.Spec.Id;
        var apiKey = _configuration["Anthropic:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogError("Correction suggestion not run: Anthropic:ApiKey is missing.");
            return new BalanceSuggestionReply { Model = model, FailureCode = SuggestionNotConfigured };
        }

        // ── The second pass's pages: upright, wide ones also as halves, at its tiers ─────────
        var (dpi, maxPages) = pdfBytes.Length < LargeFileSizeBytes
            ? (FallbackDpi, FallbackMaxPages)
            : ImageTier(pdfBytes.Length);

        SecondPassPlan? plan = null;
        RenderedPdf     rendered;
        var             renderStopwatch = Stopwatch.StartNew();
        try
        {
            if (pdfBytes.Length == 0)
                throw new InvalidOperationException("The document is empty.");

            plan     = await PlanSecondPassAsync(pdfBytes, maxPages, apiKey, ct).ConfigureAwait(false);
            rendered = RenderPdfPages(pdfBytes, dpi, maxPages, plan.Rotations, plan.Layout);
            if (rendered.ImageBlocks.Count == 0)
                throw new InvalidOperationException("The document rendered no page.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                "Correction suggestion not run: the document could not be rendered ({ExceptionType}).",
                ex.GetType().Name);
            return new BalanceSuggestionReply
            {
                Model                = model,
                FailureCode          = SuggestionRenderFailed,
                OrientationDetection = plan?.Detection,
            };
        }

        renderStopwatch.Stop();
        var images = rendered.Images ?? [];

        // Counts only — never a figure, a label or an image.
        _logger.LogInformation(
            "Correction suggestion request: pages={RenderedPages} images={Images} split={SplitPages} rotated={RotatedPages} " +
            "orientation_source={OrientationSource} fields={Fields} item_groups={ItemGroups} render_ms={RenderMs} " +
            "request_mb={RequestMb:F1} dpi={Dpi} model={Model}",
            rendered.RenderedPages, images.Count, rendered.SplitPages, rendered.Pages.Count(p => p.Rotation != 0),
            plan.OrientationSource, question.Fields.Count, question.ItemGroups.Count, renderStopwatch.ElapsedMilliseconds,
            rendered.Base64Bytes / (1024d * 1024d), dpi, model);

        // ── ONE request: images in render order, then the question ───────────────────────
        var blocks      = new List<object>(rendered.ImageBlocks) { new { type = "text", text = SuggestionText(question, rendered) } };
        var requestJson = BuildExtractionRequestJson(_extraction, SuggestionSystemPrompt, blocks);

        var    requestStopwatch = Stopwatch.StartNew();
        string rawContent;
        try
        {
            rawContent = await SendExtractionRequestAsync(
                    requestJson, apiKey, SuggestionAttempts, _extraction.Timeout, logErrorMessage: false, ct)
                .ConfigureAwait(false);
        }
        catch (ClaudeApiException ex)
        {
            var code = ex.ApiStatusCode is { } status
                ? string.Create(CultureInfo.InvariantCulture, $"http_{status}")
                : ex.InnerException switch
                {
                    TaskCanceledException => SuggestionTimeout,
                    HttpRequestException  => SuggestionNetworkError,
                    _                     => SuggestionInvalidResponse,
                };

            return new BalanceSuggestionReply
            {
                Model                = model,
                RequestSent          = true,
                HttpStatus           = ex.ApiStatusCode,
                DurationMs           = (int)requestStopwatch.ElapsedMilliseconds,
                Images               = images,
                OrientationDetection = plan.Detection,
                FailureCode          = code,
                FailureDetail        = ex.ApiStatusCode is null ? null : ex.DiagnosticMessage,
            };
        }

        var duration = (int)requestStopwatch.ElapsedMilliseconds;
        var answer   = ReadAnswer(rawContent, SuggestionOperationLabel, _extraction.Spec);

        var reply = new BalanceSuggestionReply
        {
            Model                = model,
            RequestSent          = true,
            HttpStatus           = 200,
            InputTokens          = answer.InputTokens,
            OutputTokens         = answer.OutputTokens,
            DurationMs           = duration,
            Images               = images,
            OrientationDetection = plan.Detection,
        };

        if (answer.FailureCode is not null)
            return reply with { FailureCode = answer.FailureCode };

        // Read like the extraction's answer: the JSON object, without the prose a model may write around it.
        var extracted = ExtractJsonObject(answer.Text!);
        if (extracted.IsTrimmed)
            LogTrimmedAnswer(SuggestionOperationLabel, model, escalated: false, extracted);

        BalanceSuggestionAnswer? parsed = null;
        if (extracted.Json is not null)
        {
            try
            {
                parsed = JsonSerializer.Deserialize<BalanceSuggestionAnswer>(extracted.Json, SuggestionAnswerOptions);
            }
            catch (JsonException)
            {
                parsed = null;
            }
        }

        if (parsed is null)
        {
            _logger.LogWarning(
                "Correction suggestion answer does not match the suggestion schema: model={Model} output_tokens={OutputTokens}",
                model, answer.OutputTokens);
            return reply with { FailureCode = SuggestionInvalidResponse };
        }

        return reply with { Answer = parsed };
    }

    /// <summary>
    /// The suggestion's closing text block: what the images are (the second pass's own wording for the
    /// halves), the two reading rules, the fiscal year, and the figures asked about — path, label HINT and
    /// stored value in printed units (with <see cref="NetProfitQuestionNote"/> when the net result is asked).
    /// Fixed text, paths and numbers only: no stored label, no identifier.
    /// </summary>
    internal static string SuggestionText(BalanceSuggestionQuestion question, RenderedPdf rendered)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(rendered);

        var text = new StringBuilder("These images are the pages of the financial statements, in order");
        if (question.FiscalYear is { } year)
            text.Append(CultureInfo.InvariantCulture, $"; the current fiscal year closes in {year}");
        text.Append('.');

        if (rendered.RenderedPages < rendered.TotalPages)
            text.Append(CultureInfo.InvariantCulture, $" Only the first {rendered.RenderedPages} pages are included.");

        text.Append(DescribeSplitImages(rendered))
            .Append(' ').Append(TwoSidedSheetRule)
            .Append(' ').Append(DashIsZeroRule);

        if (question.Fields.Count > 0)
        {
            text.Append("\n\nFigures to verify (stored values are in the document's printed unit):");
            foreach (var field in question.Fields)
            {
                text.Append("\n- ").Append(field.Path)
                    .Append(": usually printed as \"").Append(field.LabelHint)
                    .Append("\"; stored value ").Append(Number(field.StoredPrinted)).Append('.');
            }
        }

        if (question.AsksNetProfit)
            text.Append("\n\n").Append(NetProfitQuestionNote);

        if (question.AsksNetProfit && question.NetProfitColumnMix)
            text.Append(' ').Append(NetProfitColumnMixNote);

        if (question.ItemGroups.Count > 0)
        {
            text.Append("\n\nRubro lists to verify (stored values are in the document's printed unit):");
            foreach (var group in question.ItemGroups)
            {
                text.Append("\n- ").Append(group.Array).Append(" (").Append(group.LabelHint).Append("): stored lines ")
                    .Append(string.Join("; ", group.StoredPrinted.Select((v, i) =>
                        string.Create(CultureInfo.InvariantCulture, $"[{i}] {Number(v)}"))))
                    .Append('.');
            }
        }

        text.Append("\n\nReturn the JSON object described in your instructions.");
        return text.ToString();

        static string Number(double? value)
            => value is { } v ? v.ToString("0.############", CultureInfo.InvariantCulture) : "null";
    }
}
