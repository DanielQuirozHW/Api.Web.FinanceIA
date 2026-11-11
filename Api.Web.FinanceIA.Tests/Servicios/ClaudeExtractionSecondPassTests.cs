// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-02

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Tests.Legajo;
using iText.IO.Font.Constants;
using iText.Kernel.Font;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// The integrity-driven second extraction pass, end to end over canned Anthropic envelopes: when it
/// runs, what it sends, which pass is kept and what the kept BalanceJson records. Every figure is
/// synthetic and every fixture PDF is built here — no client document, name or CUIT.
/// </summary>
public sealed class ClaudeExtractionSecondPassTests
{
    // ── Synthetic figures (pesos) ─────────────────────────────────────────────
    // A sound balance: A = AC + ANC, P = PC + PNC, A = P + PN, and the rubros add up.
    private const long Assets      = 731_942_118;
    private const long CurAssets   = 523_871_406;
    private const long NonCurAssets = Assets - CurAssets;
    private const long CurLiab     = 86_590_377;
    private const long NonCurLiab  = 12_004_265;
    private const long Liab        = CurLiab + NonCurLiab;
    private const long Equity      = Assets - Liab;

    private static readonly long[] Figures = [Assets, CurAssets, NonCurAssets, CurLiab, NonCurLiab, Liab, Equity];

    private static string Rubros(long current, long liabilities) =>
        $"\"current_assets_items\": [{{\"label\": \"Caja y bancos\", \"amount\": {current - 1_000_000}}}, {{\"label\": \"Creditos por ventas\", \"amount\": 1000000}}], " +
        $"\"current_liabilities_items\": [{{\"label\": \"Deudas comerciales\", \"amount\": {liabilities}}}]";

    private static string Totals(long curAssets, long nonCurAssets, long curLiab) =>
        $"\"assets\": {Assets}, \"current_assets\": {curAssets}, \"non_current_assets\": {nonCurAssets}, " +
        $"\"liabilities\": {curLiab + NonCurLiab}, \"current_liabilities\": {curLiab}, \"non_current_liabilities\": {NonCurLiab}, " +
        $"\"equity\": {Assets - curLiab - NonCurLiab}, \"balanced\": true, \"difference_pct\": 0";

    /// <summary>Every check passes.</summary>
    private static string Sound(string confidence = "0.80") =>
        Model(Totals(CurAssets, NonCurAssets, CurLiab) + ", " + Rubros(CurAssets, CurLiab), confidence);

    /// <summary>
    /// The two sides swapped while the totals still add up: current assets carry the current
    /// liabilities' figure (and vice versa), the non-current assets absorb the difference. Only the
    /// rubro checks see it — current_assets_items AND current_liabilities_items fail.
    /// </summary>
    private static string SidesSwapped(string confidence = "0.85") =>
        Model(Totals(CurLiab, Assets - CurLiab, CurAssets) + ", " + Rubros(CurAssets, CurLiab), confidence);

    /// <summary>Only current_assets_items fails (the current liabilities read right).</summary>
    private static string CurrentAssetsMisread(string confidence = "0.80") =>
        Model(Totals(CurLiab, Assets - CurLiab, CurLiab) + ", " + Rubros(CurAssets, CurLiab), confidence);

    /// <summary>Sound totals, no rubros at all: the rubro checks are SKIPPED, so the report passes.</summary>
    private static string SoundWithoutRubros(string confidence = "0.95") =>
        Model(Totals(CurAssets, NonCurAssets, CurLiab), confidence);

    private static string Model(string balanceCheck, string confidence) =>
        "{\"ratios\": {}, \"alerts\": [], \"anomalies\": [], \"executive_summary\": \"Resumen.\", " +
        $"\"confidence_score\": {confidence}, \"fiscal_year_detected\": 2025, \"amounts_unit\": \"pesos\", " +
        $"\"balance_check\": {{ {balanceCheck} }}}}";

    // ── Harness ───────────────────────────────────────────────────────────────

    private static (ClaudeService Sut, Handler Handler, AllLogger Logger) Build(
        params (HttpStatusCode Status, string Body)[] responses) => Build(new FakeDetector(), responses);

    private static (ClaudeService Sut, Handler Handler, AllLogger Logger) Build(
        FakeDetector detector, params (HttpStatusCode Status, string Body)[] responses)
    {
        var handler = new Handler(responses);
        var logger  = new AllLogger();
        var sut = new ClaudeService(
            new Factory(handler),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Anthropic:ApiKey"] = "test-key",
                    ["Anthropic:ExtractionConfidenceFallbackThreshold"] = "0.60"
                })
                .Build(),
            logger,
            new BalanceIntegrityService(),
            Options.Create(new ExtractionModelOptions()),
            detector);

        return (sut, handler, logger);
    }

    private static (HttpStatusCode, string) Ok(string modelJson) => (HttpStatusCode.OK, Envelope(modelJson));

    private static string Envelope(string modelJson) => JsonSerializer.Serialize(new
    {
        content     = new[] { new { type = "text", text = modelJson } },
        stop_reason = "end_turn",
        usage       = new { input_tokens = 10, output_tokens = 20 }
    });

    private static JsonObject Integrity(ClaudeAnalysisResult result) =>
        JsonNode.Parse(result.BalanceJson)!["integrity"]!.AsObject();

    private static JsonObject Extraction(ClaudeAnalysisResult result) => Integrity(result)["extraction"]!.AsObject();

    private static string[] BlockTypes(string requestBody)
    {
        using var doc = JsonDocument.Parse(requestBody);
        return doc.RootElement.GetProperty("messages")[0].GetProperty("content")
            .EnumerateArray().Select(b => b.GetProperty("type").GetString()!).ToArray();
    }

    // ── The trigger ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Sound_first_pass_sends_one_request_and_records_no_second_pass()
    {
        var (sut, handler, _) = Build(Ok(Sound("0.90")));

        var result = await sut.AnalyzeDocumentAsync(LegajoPdfFactory.BlankPdf(1), "balance", 2025, CancellationToken.None);

        Assert.Single(handler.Captured);
        Assert.Equal("document", result.ExtractionMethod);
        Assert.True(result.BalanceIntegrityPassed);

        var extraction = Extraction(result);
        Assert.False(extraction["secondPassRan"]!.GetValue<bool>());
        Assert.Equal("first", extraction["keptPass"]!.GetValue<string>());
        Assert.Equal("only_pass", extraction["keptReason"]!.GetValue<string>());
        Assert.Empty(extraction["secondPassTriggers"]!.AsArray());
    }

    [Fact]
    public async Task A_structure_failure_above_the_threshold_runs_an_image_pass_and_keeps_the_one_that_adds_up()
    {
        // 0.85 reported, capped to 0.69 by the failed checks — above 0.60, so before this change no
        // second pass ran.
        var (sut, handler, _) = Build(Ok(SidesSwapped("0.85")), Ok(Sound("0.80")));

        var result = await sut.AnalyzeDocumentAsync(LegajoPdfFactory.BlankPdf(1), "balance", 2025, CancellationToken.None);

        Assert.Equal(2, handler.Captured.Count);
        Assert.Equal(["document", "text"], BlockTypes(handler.Captured[0]));
        Assert.Equal(["image", "text"], BlockTypes(handler.Captured[1])); // no PDF document block

        Assert.Equal("image-fallback", result.ExtractionMethod);
        Assert.True(result.BalanceIntegrityPassed);
        Assert.Equal(0.80m, result.ConfidenceScore);
        Assert.Equal(20, result.InputTokens);  // both passes are paid for
        Assert.Equal(40, result.OutputTokens);
        Assert.Equal(CurAssets, JsonNode.Parse(result.BalanceJson)!["current_assets"]!.GetValue<double>());

        var extraction = Extraction(result);
        Assert.True(extraction["secondPassRan"]!.GetValue<bool>());
        Assert.Equal("second", extraction["keptPass"]!.GetValue<string>());
        Assert.Equal("integrity_passed", extraction["keptReason"]!.GetValue<string>());
        Assert.Equal("image-fallback", extraction["method"]!.GetValue<string>());
        Assert.Equal(["integrity"], extraction["secondPassTriggers"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.Equal(0, extraction["rotatedPages"]!.GetValue<int>());

        // The anomalies are the kept pass's: none of the first pass's integrity findings survive.
        Assert.Empty(JsonSerializer.Deserialize<List<string>>(result.AnomaliesJson)!);
    }

    [Fact]
    public async Task When_both_fail_the_pass_with_fewer_structure_failures_is_kept_and_the_payload_says_so()
    {
        var (sut, _, _) = Build(Ok(SidesSwapped()), Ok(CurrentAssetsMisread()));

        var result = await sut.AnalyzeDocumentAsync(LegajoPdfFactory.BlankPdf(1), "balance", 2025, CancellationToken.None);

        Assert.False(result.BalanceIntegrityPassed);
        Assert.Equal("image-fallback", result.ExtractionMethod);
        Assert.Equal("fewer_structure_failures", Extraction(result)["keptReason"]!.GetValue<string>());

        var dto = Assert.IsType<ExtractionIntegrityResponse>(ExtractionIntegrityReader.Read(result.BalanceJson));
        Assert.False(dto.Passed);
        Assert.Contains("current_assets_items", dto.FailedChecks);
        Assert.DoesNotContain("current_liabilities_items", dto.FailedChecks);
        Assert.Equal(["liquidez_corriente", "prueba_acida"], dto.AffectedRatios);
        Assert.True(dto.SecondPassRan);
        Assert.Equal("second", dto.KeptPass);
    }

    [Fact]
    public async Task When_the_second_pass_is_worse_the_first_is_kept()
    {
        var (sut, handler, _) = Build(Ok(CurrentAssetsMisread()), Ok(SidesSwapped()));

        var result = await sut.AnalyzeDocumentAsync(LegajoPdfFactory.BlankPdf(1), "balance", 2025, CancellationToken.None);

        Assert.Equal(2, handler.Captured.Count);
        Assert.Equal("document", result.ExtractionMethod);
        Assert.Equal(20, result.InputTokens);
        var extraction = Extraction(result);
        Assert.Equal("first", extraction["keptPass"]!.GetValue<string>());
        Assert.True(extraction["secondPassRan"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_second_pass_that_passes_only_by_skipping_the_failed_checks_does_not_displace_the_first()
    {
        // The image pass drops the rubros: current_assets_items is skipped, so its report "passes" —
        // but it never checked what the first pass failed.
        var (sut, _, _) = Build(Ok(SidesSwapped("0.85")), Ok(SoundWithoutRubros("0.95")));

        var result = await sut.AnalyzeDocumentAsync(LegajoPdfFactory.BlankPdf(1), "balance", 2025, CancellationToken.None);

        Assert.Equal("document", result.ExtractionMethod);
        Assert.False(result.BalanceIntegrityPassed);
        Assert.Equal("tie_first", Extraction(result)["keptReason"]!.GetValue<string>());
    }

    [Fact]
    public async Task Low_confidence_with_sound_figures_behaves_as_before()
    {
        var (sut, handler, _) = Build(Ok(Sound("0.30")), Ok(Sound("0.90")));

        var result = await sut.AnalyzeDocumentAsync(LegajoPdfFactory.BlankPdf(1), "balance", 2025, CancellationToken.None);

        Assert.Equal(2, handler.Captured.Count);
        Assert.Equal("image-fallback", result.ExtractionMethod);
        Assert.Equal(0.90m, result.ConfidenceScore);
        var extraction = Extraction(result);
        Assert.Equal("higher_confidence", extraction["keptReason"]!.GetValue<string>());
        Assert.Equal(["low_confidence"], extraction["secondPassTriggers"]!.AsArray().Select(t => t!.GetValue<string>()));
    }

    // ── Failure of the second pass keeps the first ────────────────────────────

    [Fact]
    public async Task A_second_pass_rejected_by_the_api_keeps_the_first_pass()
    {
        // 400 is not retried, so the test does not wait out the backoff.
        var (sut, handler, logger) = Build(Ok(SidesSwapped()), (HttpStatusCode.BadRequest, "{}"));

        var result = await sut.AnalyzeDocumentAsync(LegajoPdfFactory.BlankPdf(1), "balance", 2025, CancellationToken.None);

        Assert.Equal(2, handler.Captured.Count);
        Assert.Equal("document", result.ExtractionMethod);
        Assert.Equal(10, result.InputTokens);
        var extraction = Extraction(result);
        Assert.False(extraction["secondPassRan"]!.GetValue<bool>());
        Assert.Equal("second_pass_failed", extraction["keptReason"]!.GetValue<string>());
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("second pass failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_second_pass_that_throws_something_other_than_an_api_error_keeps_the_first_pass()
    {
        // Not an HttpRequestException or a timeout (those are retried and wrapped): an unexpected fault
        // escapes the attempt loop as it is. The analysis must not fail over the pass meant to improve it.
        var (sut, handler, logger) = Build(Ok(SidesSwapped()), Ok(Sound()));
        handler.ThrowOn = (1, new InvalidOperationException("unexpected fault in the second pass"));

        var result = await sut.AnalyzeDocumentAsync(LegajoPdfFactory.BlankPdf(1), "balance", 2025, CancellationToken.None);

        Assert.Equal(2, handler.Captured.Count);
        Assert.Equal("document", result.ExtractionMethod);
        Assert.Equal(10, result.InputTokens);
        var extraction = Extraction(result);
        Assert.False(extraction["secondPassRan"]!.GetValue<bool>());
        Assert.Equal("second_pass_failed", extraction["keptReason"]!.GetValue<string>());
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning
            && e.Message.Contains("second pass failed (InvalidOperationException", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_first_pass_carries_its_model_and_the_tokens_the_api_reported()
    {
        // A max_tokens stop after usage came back, twice: the first call and its one escalation (the stub
        // answers every request alike). Both calls were billed, so the failure carries the tokens of both,
        // and the pipeline logs them.
        var envelope = JsonSerializer.Serialize(new
        {
            content     = new[] { new { type = "text", text = "{\"ratios\": {" } },
            stop_reason = "max_tokens",
            usage       = new { input_tokens = 1234, output_tokens = 16000 }
        });
        var (sut, _, _) = Build((HttpStatusCode.OK, envelope));

        var ex = await Assert.ThrowsAsync<Api.Web.FinanceIA.Clases.Exceptions.ClaudeApiException>(() =>
            sut.AnalyzeDocumentAsync(LegajoPdfFactory.BlankPdf(1), "balance", 2025, CancellationToken.None));

        Assert.Equal(1234 * 2, ex.InputTokens);
        Assert.Equal(16000 * 2, ex.OutputTokens);
        Assert.Equal("claude-sonnet-4-6", ex.Model);
        Assert.Contains("after escalation", ex.DiagnosticMessage);
    }

    [Fact]
    public async Task A_first_pass_rejected_by_the_api_carries_its_model_and_no_tokens()
    {
        var (sut, _, _) = Build((HttpStatusCode.BadRequest, "{}"));

        var ex = await Assert.ThrowsAsync<Api.Web.FinanceIA.Clases.Exceptions.ClaudeApiException>(() =>
            sut.AnalyzeDocumentAsync(LegajoPdfFactory.BlankPdf(1), "balance", 2025, CancellationToken.None));

        Assert.Equal(400, ex.ApiStatusCode);
        Assert.Null(ex.InputTokens);
        Assert.Null(ex.OutputTokens);
        Assert.Equal("claude-sonnet-4-6", ex.Model);
    }

    [Fact]
    public async Task An_unrenderable_document_keeps_the_first_pass_without_a_second_request()
    {
        var (sut, handler, logger) = Build(Ok(SidesSwapped()));

        var result = await sut.AnalyzeDocumentAsync("%PDF-1.4 fixture"u8.ToArray(), "balance", 2025, CancellationToken.None);

        Assert.Single(handler.Captured);
        Assert.False(result.BalanceIntegrityPassed);
        Assert.Equal("second_pass_failed", Extraction(result)["keptReason"]!.GetValue<string>());
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("could not be rendered", StringComparison.Ordinal));
    }

    // ── No financial statements ───────────────────────────────────────────────
    // What the model answers for a blank page or a legalization cover: a well-formed balance JSON with
    // none of the seven core totals. The extraction must fail rather than be scored on neutral ratios.

    /// <summary>A balance JSON with no core total at all.</summary>
    private static string NoStatements(string confidence = "0.90") =>
        Model("\"balanced\": false, \"difference_pct\": 0", confidence);

    /// <summary>One core total and nothing else: a partial read, which still goes on as today.</summary>
    private static string OnlyOneCoreTotal(string key, string confidence = "0.90") =>
        Model($"\"{key}\": 1000, \"balanced\": false, \"difference_pct\": 0", confidence);

    [Fact]
    public async Task A_single_pass_with_no_core_total_fails_with_its_own_code_and_the_tokens_of_the_pass()
    {
        var (sut, handler, _) = Build(Ok(NoStatements("0.90")));

        var ex = await Assert.ThrowsAsync<Api.Web.FinanceIA.Clases.Exceptions.ClaudeApiException>(() =>
            sut.AnalyzeDocumentAsync(LegajoPdfFactory.BlankPdf(1), "balance", 2025, CancellationToken.None));

        Assert.Single(handler.Captured);
        Assert.Equal(ClaudeService.AnswerNoFinancialStatements, ex.FailureCode);
        Assert.Equal(Api.Web.FinanceIA.Clases.Exceptions.ClaudeApiException.NoFinancialStatementsClientMessage, ex.Message);
        Assert.Null(ex.ApiStatusCode);
        Assert.Equal(10, ex.InputTokens);
        Assert.Equal(20, ex.OutputTokens);
        Assert.Equal("claude-sonnet-4-6", ex.Model);
        Assert.Equal(1, ex.ExtractionPassesRun);
        Assert.Equal(0.90m, ex.KeptConfidence);
        Assert.Contains("no financial statements", ex.DiagnosticMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_passes_with_no_core_total_fail_with_the_tokens_of_both_and_the_detector_usage()
    {
        // Low confidence runs the second pass; it reads nothing either. BlankPdf has no text layer, so the
        // detector is asked about its page: that billed call travels on the exception for the pipeline.
        var detector = new FakeDetector
        {
            Usage = new ScanOrientationUsage { Model = "claude-haiku-4-5", InputTokens = 500, OutputTokens = 50, Succeeded = true, HttpStatus = 200 }
        };
        var (sut, handler, _) = Build(detector, Ok(NoStatements("0.10")), Ok(NoStatements("0.20")));

        var ex = await Assert.ThrowsAsync<Api.Web.FinanceIA.Clases.Exceptions.ClaudeApiException>(() =>
            sut.AnalyzeDocumentAsync(LegajoPdfFactory.BlankPdf(1), "balance", 2025, CancellationToken.None));

        Assert.Equal(2, handler.Captured.Count);
        Assert.Equal(ClaudeService.AnswerNoFinancialStatements, ex.FailureCode);
        Assert.Equal(20, ex.InputTokens);
        Assert.Equal(40, ex.OutputTokens);
        Assert.Equal("claude-sonnet-4-6", ex.Model);
        Assert.Equal(2, ex.ExtractionPassesRun);
        Assert.Equal(0.20m, ex.KeptConfidence); // the kept pass: the higher confidence
        Assert.Equal(500, ex.OrientationDetection!.InputTokens);
        Assert.Equal("claude-haiku-4-5", ex.OrientationDetection.Model);
    }

    [Fact]
    public async Task A_first_pass_with_no_core_total_still_gets_its_second_pass_and_a_sound_one_is_kept()
    {
        // A rotated scan can read nothing on the document pass and everything on the oriented images.
        var (sut, handler, _) = Build(Ok(NoStatements("0.10")), Ok(Sound("0.80")));

        var result = await sut.AnalyzeDocumentAsync(LegajoPdfFactory.BlankPdf(1), "balance", 2025, CancellationToken.None);

        Assert.Equal(2, handler.Captured.Count);
        Assert.Equal("second", Extraction(result)["keptPass"]!.GetValue<string>());
        Assert.Equal(Assets, JsonNode.Parse(result.BalanceJson)!["assets"]!.GetValue<double>());
        Assert.Equal(20, result.InputTokens);
    }

    [Fact]
    public async Task A_first_pass_with_no_core_total_kept_because_the_second_failed_still_fails()
    {
        // The keep-the-first exit after a failed second pass goes through the same gate. 400: no retry.
        var (sut, handler, _) = Build(Ok(NoStatements("0.10")), (HttpStatusCode.BadRequest, "{}"));

        var ex = await Assert.ThrowsAsync<Api.Web.FinanceIA.Clases.Exceptions.ClaudeApiException>(() =>
            sut.AnalyzeDocumentAsync(LegajoPdfFactory.BlankPdf(1), "balance", 2025, CancellationToken.None));

        Assert.Equal(2, handler.Captured.Count);
        Assert.Equal(ClaudeService.AnswerNoFinancialStatements, ex.FailureCode);
        Assert.Equal(10, ex.InputTokens); // the failed second pass's tokens are unknown, as for a kept first pass
        Assert.Equal(1, ex.ExtractionPassesRun);
    }

    [Theory]
    [InlineData("assets")]
    [InlineData("current_assets")]
    [InlineData("current_liabilities")]
    [InlineData("liabilities")]
    [InlineData("equity")]
    [InlineData("net_revenue")]
    [InlineData("net_profit")]
    public async Task A_partial_read_with_one_core_total_returns_a_result_as_before(string key)
    {
        var (sut, handler, _) = Build(Ok(OnlyOneCoreTotal(key)));

        var result = await sut.AnalyzeDocumentAsync(LegajoPdfFactory.BlankPdf(1), "balance", 2025, CancellationToken.None);

        Assert.Single(handler.Captured);
        Assert.Equal(1000d, JsonNode.Parse(result.BalanceJson)![key]!.GetValue<double>());
    }

    [Fact]
    public void AnyCoreTotal_is_true_for_each_core_total_alone_and_for_a_zero()
    {
        Assert.False(ClaudeService.AnyCoreTotal(null, null, null, null, null, null, null));
        Assert.True(ClaudeService.AnyCoreTotal(1, null, null, null, null, null, null));
        Assert.True(ClaudeService.AnyCoreTotal(null, 1, null, null, null, null, null));
        Assert.True(ClaudeService.AnyCoreTotal(null, null, 1, null, null, null, null));
        Assert.True(ClaudeService.AnyCoreTotal(null, null, null, 1, null, null, null));
        Assert.True(ClaudeService.AnyCoreTotal(null, null, null, null, 1, null, null));
        Assert.True(ClaudeService.AnyCoreTotal(null, null, null, null, null, 1, null));
        Assert.True(ClaudeService.AnyCoreTotal(null, null, null, null, null, null, 1));
        Assert.True(ClaudeService.AnyCoreTotal(null, null, null, null, 0, null, null)); // a printed zero was read
    }

    [Fact]
    public async Task Figures_outside_the_core_totals_alone_are_still_no_financial_statements()
    {
        var (sut, _, _) = Build(Ok(Model(
            "\"inventory\": 500, \"operating_result\": 20, \"non_current_assets\": 40, \"balanced\": false, \"difference_pct\": 0", "0.90")));

        var ex = await Assert.ThrowsAsync<Api.Web.FinanceIA.Clases.Exceptions.ClaudeApiException>(() =>
            sut.AnalyzeDocumentAsync(LegajoPdfFactory.BlankPdf(1), "balance", 2025, CancellationToken.None));

        Assert.Equal(ClaudeService.AnswerNoFinancialStatements, ex.FailureCode);
    }

    // ── Orientation ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_page_drawn_rotated_is_sent_upright_on_the_second_pass()
    {
        var (sut, handler, _) = Build(Ok(SidesSwapped()), Ok(Sound()));

        var result = await sut.AnalyzeDocumentAsync(RotatedStatementPdf(), "balance", 2025, CancellationToken.None);

        Assert.Equal("image-oriented", result.ExtractionMethod);
        Assert.Equal(1, Extraction(result)["rotatedPages"]!.GetValue<int>());

        using var image = FirstImage(handler.Captured[1]);
        Assert.True(image.Width > image.Height, $"{image.Width}x{image.Height}"); // portrait A4, turned
    }

    [Fact]
    public async Task A_large_file_whose_pages_need_no_turning_is_not_read_twice()
    {
        var (sut, handler, logger) = Build(Ok(SidesSwapped()), Ok(Sound()));

        var result = await sut.AnalyzeDocumentAsync(Padded(LegajoPdfFactory.BlankPdf(1)), "balance", 2025, CancellationToken.None);

        Assert.Single(handler.Captured);
        Assert.Equal("image", result.ExtractionMethod);
        Assert.Equal("second_pass_skipped", Extraction(result)["keptReason"]!.GetValue<string>());
        Assert.Contains(logger.Entries, e => e.Message.Contains("second pass skipped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_large_file_with_a_rotated_page_gets_an_oriented_second_pass_at_its_own_tier()
    {
        var (sut, handler, _) = Build(Ok(SidesSwapped()), Ok(Sound()));

        var result = await sut.AnalyzeDocumentAsync(Padded(RotatedStatementPdf()), "balance", 2025, CancellationToken.None);

        Assert.Equal(2, handler.Captured.Count);
        Assert.Equal(["image", "text"], BlockTypes(handler.Captured[0]));
        Assert.Equal("image-oriented", result.ExtractionMethod);

        using var before = FirstImage(handler.Captured[0]);
        using var after  = FirstImage(handler.Captured[1]);
        Assert.True(before.Height > before.Width);
        Assert.Equal((before.Width, before.Height), (after.Height, after.Width)); // same DPI tier, turned
    }

    // ── Scans: orientation from the image, wide pages as halves ───────────────

    [Fact]
    public async Task A_scanned_page_is_asked_about_and_sent_upright_with_its_two_halves()
    {
        // A portrait page with NO text layer: the glyphs cannot decide it, the detector says 90.
        var detector = new FakeDetector { Answer = pages => pages.Select(_ => (int?)90).ToArray() };
        var (sut, handler, _) = Build(detector, Ok(SidesSwapped()), Ok(Sound()));

        var result = await sut.AnalyzeDocumentAsync(LegajoPdfFactory.BlankPdf(1), "balance", 2025, CancellationToken.None);

        Assert.Equal([0], Assert.Single(detector.Calls));
        Assert.Equal(["image", "image", "image", "text"], BlockTypes(handler.Captured[1]));
        Assert.Equal("image-oriented", result.ExtractionMethod);

        var images = Images(handler.Captured[1]);
        try
        {
            var (full, left, right) = (images[0], images[1], images[2]);
            Assert.True(full.Width > full.Height, $"{full.Width}x{full.Height}"); // turned upright
            Assert.Equal((int)Math.Round(full.Width * 0.56), left.Width);
            Assert.Equal(full.Width - (int)Math.Round(full.Width * 0.44), right.Width);
            Assert.All(images, i => Assert.Equal(full.Height, i.Height));
        }
        finally
        {
            images.ForEach(i => i.Dispose());
        }

        var text = LastText(handler.Captured[1]);
        Assert.Contains("page 1 is image 1 (full page), image 2 (its left half) and image 3 (its right half)", text, StringComparison.Ordinal);
        Assert.Contains(ClaudeService.TwoSidedSheetRule, text, StringComparison.Ordinal);
        Assert.Contains(ClaudeService.DashIsZeroRule, text, StringComparison.Ordinal);

        var extraction = Extraction(result);
        Assert.Equal(1, extraction["rotatedPages"]!.GetValue<int>());
        Assert.Equal(1, extraction["splitPages"]!.GetValue<int>());
        Assert.Equal("image", extraction["orientationSource"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_detector_that_judges_nothing_leaves_the_scan_as_it_is_and_the_second_pass_still_runs()
    {
        var (sut, handler, _) = Build(new FakeDetector(), Ok(SidesSwapped()), Ok(Sound()));

        var result = await sut.AnalyzeDocumentAsync(LegajoPdfFactory.BlankPdf(1), "balance", 2025, CancellationToken.None);

        Assert.Equal(2, handler.Captured.Count);
        Assert.Equal(["image", "text"], BlockTypes(handler.Captured[1])); // portrait, not split
        Assert.Equal("image-fallback", result.ExtractionMethod);
        Assert.Equal("none", Extraction(result)["orientationSource"]!.GetValue<string>());
        Assert.Equal(0, Extraction(result)["splitPages"]!.GetValue<int>());
    }

    [Fact]
    public async Task Only_the_pages_without_a_text_layer_are_sent_to_the_detector()
    {
        // Page 1: a rotated statement the glyphs decide. Page 2: no text at all.
        var detector = new FakeDetector();
        var (sut, _, _) = Build(detector, Ok(SidesSwapped()), Ok(Sound()));

        var result = await sut.AnalyzeDocumentAsync(WithBlankPage(RotatedStatementPdf()), "balance", 2025, CancellationToken.None);

        Assert.Equal([1], Assert.Single(detector.Calls));
        Assert.Equal("glyphs", Extraction(result)["orientationSource"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_document_whose_glyphs_decide_every_page_makes_no_detector_call()
    {
        var detector = new FakeDetector();
        var (sut, _, _) = Build(detector, Ok(SidesSwapped()), Ok(Sound()));

        var result = await sut.AnalyzeDocumentAsync(RotatedStatementPdf(), "balance", 2025, CancellationToken.None);

        Assert.Empty(detector.Calls);
        Assert.Null(result.OrientationDetection);
        // A rotated page is landscape once upright: it is split, its glyphs having decided the turn.
        Assert.Equal(1, Extraction(result)["splitPages"]!.GetValue<int>());
        Assert.Equal("glyphs", Extraction(result)["orientationSource"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_detector_call_is_reported_apart_and_never_added_to_the_extraction_tokens()
    {
        var detector = new FakeDetector
        {
            Answer = pages => pages.Select(_ => (int?)0).ToArray(),
            Usage  = new ScanOrientationUsage { Model = "claude-haiku-4-5", InputTokens = 500, OutputTokens = 50, Succeeded = true, HttpStatus = 200 }
        };
        var (sut, _, _) = Build(detector, Ok(SidesSwapped()), Ok(Sound()));

        var result = await sut.AnalyzeDocumentAsync(LegajoPdfFactory.BlankPdf(1), "balance", 2025, CancellationToken.None);

        Assert.Equal(20, result.InputTokens);  // the two Sonnet passes only
        Assert.Equal(40, result.OutputTokens);
        Assert.Equal(500, result.OrientationDetection!.InputTokens);
        Assert.Equal("claude-haiku-4-5", result.OrientationDetection.Model);
        Assert.Equal("image", Extraction(result)["orientationSource"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_landscape_page_needs_no_turn_to_be_split()
    {
        var (sut, handler, _) = Build(new FakeDetector(), Ok(SidesSwapped()), Ok(Sound()));

        var result = await sut.AnalyzeDocumentAsync(LegajoPdfFactory.SizedPdf(842, 595), "balance", 2025, CancellationToken.None);

        Assert.Equal(["image", "image", "image", "text"], BlockTypes(handler.Captured[1]));
        Assert.Equal("image-fallback", result.ExtractionMethod);
        Assert.Equal(0, Extraction(result)["rotatedPages"]!.GetValue<int>());
        Assert.Equal(1, Extraction(result)["splitPages"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_large_scan_with_a_page_to_split_gets_a_second_pass_even_with_nothing_to_turn()
    {
        var (sut, handler, _) = Build(new FakeDetector(), Ok(SidesSwapped()), Ok(Sound()));

        var result = await sut.AnalyzeDocumentAsync(Padded(LegajoPdfFactory.SizedPdf(842, 595)), "balance", 2025, CancellationToken.None);

        Assert.Equal(2, handler.Captured.Count);
        Assert.Equal(["image", "text"], BlockTypes(handler.Captured[0]));
        Assert.Equal(["image", "image", "image", "text"], BlockTypes(handler.Captured[1]));
        Assert.Equal(1, Extraction(result)["splitPages"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_large_scan_turned_by_the_detector_gets_a_second_pass()
    {
        var detector = new FakeDetector { Answer = pages => pages.Select(_ => (int?)270).ToArray() };
        var (sut, handler, _) = Build(detector, Ok(SidesSwapped()), Ok(Sound()));

        var result = await sut.AnalyzeDocumentAsync(Padded(LegajoPdfFactory.BlankPdf(1)), "balance", 2025, CancellationToken.None);

        Assert.Equal(2, handler.Captured.Count);
        Assert.Equal("image-oriented", result.ExtractionMethod);
        Assert.Equal("image", Extraction(result)["orientationSource"]!.GetValue<string>());
    }

    /// <summary>
    /// A landscape statement rotated inside a portrait scan, read across the row by the first (image) pass: the
    /// pasivo corriente equals the total, the pasivo no corriente subtotal is left null and its only rubro carries
    /// the fixed-assets figure from the other half of the row. That contradiction used to SKIP
    /// liabilities_components, so no second pass ran; evaluated with its rubros, the check fails, the second pass
    /// turns the page, and the reading that adds up is kept.
    /// </summary>
    [Fact]
    public async Task A_rubro_contradicting_a_null_subtotal_triggers_the_oriented_second_pass_and_the_sound_one_is_kept()
    {
        var misread = Model(
            $"\"assets\": {Assets}, \"current_assets\": {CurAssets}, \"non_current_assets\": {NonCurAssets}, " +
            $"\"liabilities\": {Liab}, \"current_liabilities\": {Liab}, \"non_current_liabilities\": null, " +
            $"\"equity\": {Equity}, \"balanced\": true, \"difference_pct\": 0, " +
            Rubros(CurAssets, Liab) + ", " +
            $"\"non_current_liabilities_items\": [{{\"label\": \"Otros pasivos\", \"amount\": {NonCurAssets}}}]",
            "0.85");
        var detector = new FakeDetector { Answer = pages => pages.Select(_ => (int?)90).ToArray() };
        var (sut, handler, _) = Build(detector, Ok(misread), Ok(Sound()));

        var result = await sut.AnalyzeDocumentAsync(Padded(LegajoPdfFactory.BlankPdf(1)), "balance", 2025, CancellationToken.None);

        Assert.Equal(2, handler.Captured.Count);
        Assert.Equal("image-oriented", result.ExtractionMethod);
        Assert.True(result.BalanceIntegrityPassed);

        var extraction = Extraction(result);
        Assert.Equal(["integrity"], extraction["secondPassTriggers"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.Equal("second", extraction["keptPass"]!.GetValue<string>());
        Assert.Equal("integrity_passed", extraction["keptReason"]!.GetValue<string>());
        Assert.Equal(1, extraction["rotatedPages"]!.GetValue<int>());
        Assert.Equal("image", extraction["orientationSource"]!.GetValue<string>());
        Assert.Equal(NonCurLiab, JsonNode.Parse(result.BalanceJson)!["non_current_liabilities"]!.GetValue<double>());
    }

    [Fact]
    public async Task A_skipped_large_file_still_reports_the_detector_call_it_paid_for()
    {
        var detector = new FakeDetector
        {
            Answer = pages => pages.Select(_ => (int?)0).ToArray(),
            Usage  = new ScanOrientationUsage { Model = "claude-haiku-4-5", InputTokens = 300, OutputTokens = 20, Succeeded = true, HttpStatus = 200 }
        };
        var (sut, handler, _) = Build(detector, Ok(SidesSwapped()), Ok(Sound()));

        var result = await sut.AnalyzeDocumentAsync(Padded(LegajoPdfFactory.BlankPdf(1)), "balance", 2025, CancellationToken.None);

        Assert.Single(handler.Captured);
        Assert.Equal("second_pass_skipped", Extraction(result)["keptReason"]!.GetValue<string>());
        Assert.Equal("image", Extraction(result)["orientationSource"]!.GetValue<string>());
        Assert.Equal(300, result.OrientationDetection!.InputTokens);
    }

    [Fact]
    public async Task The_large_file_first_pass_keeps_its_closing_text_and_the_second_pass_gets_the_reading_rules()
    {
        var (sut, handler, _) = Build(new FakeDetector(), Ok(SidesSwapped()), Ok(Sound()));

        await sut.AnalyzeDocumentAsync(Padded(LegajoPdfFactory.SizedPdf(842, 595)), "balance", 2025, CancellationToken.None);

        // The words the large-file first pass has always ended with — part of its request bytes.
        Assert.Equal("Analyse this balance financial document for fiscal year 2025 and return the JSON result.",
            LastText(handler.Captured[0]));
        Assert.StartsWith("Analyse this balance financial document for fiscal year 2025 and return the JSON result. ",
            LastText(handler.Captured[1]), StringComparison.Ordinal);
        Assert.EndsWith(ClaudeService.TwoSidedSheetRule + " " + ClaudeService.DashIsZeroRule,
            LastText(handler.Captured[1]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_inside_the_detector_propagates()
    {
        var detector = new FakeDetector { Throw = new OperationCanceledException() };
        var (sut, _, _) = Build(detector, Ok(SidesSwapped()), Ok(Sound()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sut.AnalyzeDocumentAsync(LegajoPdfFactory.BlankPdf(1), "balance", 2025, CancellationToken.None));
    }

    [Fact]
    public async Task With_splitting_off_a_wide_page_goes_as_one_image()
    {
        var handler = new Handler([Ok(SidesSwapped()), Ok(Sound())]);
        var sut = new ClaudeService(
            new Factory(handler),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Anthropic:ApiKey"] = "k" }).Build(),
            new AllLogger(), new BalanceIntegrityService(), Options.Create(new ExtractionModelOptions()),
            NullScanOrientationDetector.Instance) { SplitWidePages = false };

        var result = await sut.AnalyzeDocumentAsync(LegajoPdfFactory.SizedPdf(842, 595), "balance", 2025, CancellationToken.None);

        Assert.Equal(["image", "text"], BlockTypes(handler.Captured[1]));
        Assert.Equal(0, Extraction(result)["splitPages"]!.GetValue<int>());
    }

    // ── Column mix (policy 6) ─────────────────────────────────────────────────
    // A synthetic two-column income statement, each column reconciling as printed:
    //   current  — result before tax 9.390.011, tax −2.877.664, net profit 6.512.347;
    //   previous — result before tax 8.204.611, tax −2.877.933, net profit 5.326.678.
    private const long NetProfitNow   = 6_512_347;
    private const long PreTaxNow      = 9_390_011;
    private const long TaxNow         = -2_877_664;
    private const long NetProfitPrior = 5_326_678;
    private const long PreTaxPrior    = 8_204_611;
    private const long TaxPrior       = -2_877_933;

    private static readonly long[] IncomeFigures = [NetProfitNow, PreTaxNow, TaxNow, NetProfitPrior, PreTaxPrior, TaxPrior];

    /// <summary>A sound balance sheet beside the given income-statement reading and comparative net profit.</summary>
    private static string WithIncome(long netProfit, long preTax, long tax, long? priorNetProfit, string confidence = "0.80") =>
        "{\"ratios\": {}, \"alerts\": [], \"anomalies\": [], \"executive_summary\": \"Resumen.\", " +
        $"\"confidence_score\": {confidence}, \"fiscal_year_detected\": 2025, \"amounts_unit\": \"pesos\", " +
        $"\"balance_check\": {{ {Totals(CurAssets, NonCurAssets, CurLiab)}, {Rubros(CurAssets, CurLiab)}, " +
        $"\"net_profit\": {netProfit}, \"result_before_tax\": {preTax}, \"income_tax\": {tax} }}" +
        (priorNetProfit is { } prior ? $", \"prior_year\": {{ \"net_profit\": {prior} }}" : string.Empty) + "}";

    /// <summary>The case: current net profit, the PREVIOUS column's pre-tax result and tax.</summary>
    private static string Mixed(string confidence = "0.80") =>
        WithIncome(NetProfitNow, PreTaxPrior, TaxPrior, NetProfitPrior, confidence);

    private static string ReadRight(string confidence = "0.80") =>
        WithIncome(NetProfitNow, PreTaxNow, TaxNow, NetProfitPrior, confidence);

    private static string[] Triggers(ClaudeAnalysisResult result) =>
        Extraction(result)["secondPassTriggers"]!.AsArray().Select(t => t!.GetValue<string>()).ToArray();

    [Fact]
    public async Task A_column_mix_triggers_a_second_pass_and_a_clean_one_is_kept()
    {
        var (sut, handler, _) = Build(Ok(Mixed()), Ok(ReadRight()));

        var result = await sut.AnalyzeDocumentAsync(LegajoPdfFactory.BlankPdf(1), "balance", 2025, CancellationToken.None);

        Assert.Equal(2, handler.Captured.Count);
        Assert.Equal(["column_mix"], Triggers(result));
        Assert.Equal("second", Extraction(result)["keptPass"]!.GetValue<string>());
        Assert.Equal("integrity_passed", Extraction(result)["keptReason"]!.GetValue<string>());
        Assert.True(result.BalanceIntegrityPassed);
        Assert.Null(result.BalanceIntegrityNetProfitColumnMix);
    }

    [Fact]
    public async Task A_column_mix_that_survives_both_passes_is_a_misread_not_a_presentation()
    {
        var (sut, handler, _) = Build(Ok(Mixed()), Ok(Mixed()));

        var result = await sut.AnalyzeDocumentAsync(LegajoPdfFactory.BlankPdf(1), "balance", 2025, CancellationToken.None);

        Assert.Equal(2, handler.Captured.Count);
        Assert.Equal("first", Extraction(result)["keptPass"]!.GetValue<string>());
        Assert.Equal("tie_first", Extraction(result)["keptReason"]!.GetValue<string>());
        Assert.Equal([BalanceIntegrityService.NetProfitReconciliationCheck], result.BalanceIntegrityFailedChecks);
        Assert.True(result.BalanceIntegrityNetProfitColumnMix);
        Assert.False(result.BalanceIntegrityNetProfitSignFlip);

        // Persisted on the check, and read back as a misread on every reader.
        var check = Integrity(result)["checks"]!.AsArray()
            .Single(c => c!["name"]!.GetValue<string>() == BalanceIntegrityService.NetProfitReconciliationCheck)!;
        Assert.True(check["columnMix"]!.GetValue<bool>());
        var dto = ExtractionIntegrityReader.Read(result.BalanceJson)!;
        Assert.True(dto.NetProfitColumnMix);
        Assert.False(dto.PresentationOnly);
        Assert.False(Api.Web.FinanceIA.Servicios.AnalysisRuns.BalanceValidationStatus.IntegrityEvidence(
            result.BalanceIntegrityPassed, result.BalanceIntegrityFailedChecks,
            result.BalanceIntegrityNetProfitSignFlip, result.BalanceIntegrityNetProfitColumnMix));
    }

    [Fact]
    public async Task A_second_pass_without_the_mix_wins_even_when_it_still_fails_net_profit()
    {
        // The second reading takes the right column but the statement puts RECPAM after the tax: it still fails
        // net_profit_reconciliation, as a presentation. Before policy 6 the tie kept the mixed first pass.
        var presentation = WithIncome(6_000_013, PreTaxNow, TaxNow, NetProfitPrior);
        var (sut, _, _) = Build(Ok(Mixed()), Ok(presentation));

        var result = await sut.AnalyzeDocumentAsync(LegajoPdfFactory.BlankPdf(1), "balance", 2025, CancellationToken.None);

        Assert.Equal("second", Extraction(result)["keptPass"]!.GetValue<string>());
        Assert.Equal("no_column_mix", Extraction(result)["keptReason"]!.GetValue<string>());
        Assert.False(result.BalanceIntegrityNetProfitColumnMix);
        Assert.True(ExtractionIntegrityReader.Read(result.BalanceJson)!.PresentationOnly);
    }

    [Fact]
    public async Task A_presentation_with_a_comparative_that_does_not_match_sends_one_request_as_before()
    {
        var presentation = WithIncome(6_000_013, PreTaxNow, TaxNow, NetProfitPrior, confidence: "0.90");
        var (sut, handler, _) = Build(Ok(presentation));

        var result = await sut.AnalyzeDocumentAsync(LegajoPdfFactory.BlankPdf(1), "balance", 2025, CancellationToken.None);

        Assert.Single(handler.Captured);
        Assert.Empty(Triggers(result));
        Assert.False(result.BalanceIntegrityNetProfitColumnMix);
        Assert.True(ExtractionIntegrityReader.Read(result.BalanceJson)!.PresentationOnly);
    }

    [Fact]
    public async Task Without_a_comparative_column_the_mixed_reading_sends_one_request_as_before()
    {
        var firstYear = WithIncome(NetProfitNow, PreTaxPrior, TaxPrior, priorNetProfit: null, confidence: "0.90");
        var (sut, handler, _) = Build(Ok(firstYear));

        var result = await sut.AnalyzeDocumentAsync(LegajoPdfFactory.BlankPdf(1), "balance", 2025, CancellationToken.None);

        Assert.Single(handler.Captured);
        Assert.Empty(Triggers(result));
        Assert.Null(result.BalanceIntegrityNetProfitColumnMix);
        Assert.False(Integrity(result)["checks"]!.AsArray()
            .Single(c => c!["name"]!.GetValue<string>() == BalanceIntegrityService.NetProfitReconciliationCheck)!
            .AsObject().ContainsKey("columnMix"));
        Assert.True(ExtractionIntegrityReader.Read(result.BalanceJson)!.PresentationOnly);
    }

    [Fact]
    public async Task No_log_line_carries_an_income_statement_figure_on_the_column_mix_path()
    {
        var (sut, _, logger) = Build(Ok(Mixed()), Ok(Mixed()));

        await sut.AnalyzeDocumentAsync(LegajoPdfFactory.BlankPdf(1), "balance", 2025, CancellationToken.None);

        Assert.Contains(logger.Entries, e => e.Message.Contains("column_mix=True", StringComparison.Ordinal));
        var esAr = CultureInfo.GetCultureInfo("es-AR");
        foreach (var figure in IncomeFigures)
        {
            foreach (var shown in new[] { figure.ToString(CultureInfo.InvariantCulture), Math.Abs(figure).ToString("N0", esAr), Math.Abs(figure).ToString(CultureInfo.InvariantCulture) })
                Assert.DoesNotContain(logger.Entries, e => e.Message.Contains(shown, StringComparison.Ordinal));
        }
    }

    // ── Logs carry names, never figures ───────────────────────────────────────

    [Fact]
    public async Task No_log_line_carries_a_balance_figure()
    {
        var (sut, _, logger) = Build(Ok(SidesSwapped()), Ok(CurrentAssetsMisread()));

        await sut.AnalyzeDocumentAsync(RotatedStatementPdf(), "balance", 2025, CancellationToken.None);

        Assert.NotEmpty(logger.Entries);
        var esAr = CultureInfo.GetCultureInfo("es-AR");
        foreach (var figure in Figures)
        {
            foreach (var shown in new[] { figure.ToString(CultureInfo.InvariantCulture), figure.ToString("N0", esAr), figure.ToString("N0", CultureInfo.InvariantCulture) })
                Assert.DoesNotContain(logger.Entries, e => e.Message.Contains(shown, StringComparison.Ordinal));
        }
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    // A portrait A4 page whose statement is drawn turned 90° (text running up), /Rotate 0.
    private static byte[] RotatedStatementPdf()
    {
        using var buffer = new MemoryStream();
        using (var pdf = new PdfDocument(new PdfWriter(buffer)))
        {
            var page   = pdf.AddNewPage(iText.Kernel.Geom.PageSize.A4);
            var canvas = new PdfCanvas(page);
            var font   = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
            string[] captions = ["Activo corriente", "Activo no corriente", "Total del activo", "Pasivo corriente", "Patrimonio neto"];
            for (var i = 0; i < captions.Length * 2; i++)
            {
                canvas.BeginText().SetFontAndSize(font, 11)
                      .SetTextMatrix(0, 1, -1, 0, 80 + i * 22, 80)
                      .ShowText(captions[i % captions.Length]).EndText();
            }
        }

        return buffer.ToArray();
    }

    // The same document past the 5 MB line: an unreferenced, incompressible stream appended as an
    // indirect object, so the first pass takes the image path.
    private static byte[] Padded(byte[] pdfBytes)
    {
        var filler = new byte[5 * 1024 * 1024 + 256 * 1024];
        new Random(17).NextBytes(filler);

        using var buffer = new MemoryStream();
        using (var pdf = new PdfDocument(new PdfReader(new MemoryStream(pdfBytes)), new PdfWriter(buffer)))
        {
            var stream = new PdfStream(filler);
            stream.SetCompressionLevel(CompressionConstants.NO_COMPRESSION);
            stream.MakeIndirect(pdf);
            pdf.GetCatalog().Put(new PdfName("Filler"), stream);
        }

        var padded = buffer.ToArray();
        Assert.True(padded.Length > 5 * 1024 * 1024, padded.Length.ToString(CultureInfo.InvariantCulture));
        return padded;
    }

    // The document followed by one empty page (no text layer).
    private static byte[] WithBlankPage(byte[] pdfBytes)
    {
        using var buffer = new MemoryStream();
        using (var pdf = new PdfDocument(new PdfReader(new MemoryStream(pdfBytes)), new PdfWriter(buffer)))
        {
            pdf.AddNewPage(iText.Kernel.Geom.PageSize.A4);
        }

        return buffer.ToArray();
    }

    private static List<SKBitmap> Images(string requestBody)
    {
        using var doc = JsonDocument.Parse(requestBody);
        return doc.RootElement.GetProperty("messages")[0].GetProperty("content")
            .EnumerateArray()
            .Where(b => b.GetProperty("type").GetString() == "image")
            .Select(b => SKBitmap.Decode(Convert.FromBase64String(b.GetProperty("source").GetProperty("data").GetString()!)))
            .ToList();
    }

    private static string LastText(string requestBody)
    {
        using var doc = JsonDocument.Parse(requestBody);
        return doc.RootElement.GetProperty("messages")[0].GetProperty("content")
            .EnumerateArray().Last(b => b.GetProperty("type").GetString() == "text")
            .GetProperty("text").GetString()!;
    }

    private static SKBitmap FirstImage(string requestBody)
    {
        using var doc = JsonDocument.Parse(requestBody);
        var image = doc.RootElement.GetProperty("messages")[0].GetProperty("content")
            .EnumerateArray().First(b => b.GetProperty("type").GetString() == "image");
        return SKBitmap.Decode(Convert.FromBase64String(image.GetProperty("source").GetProperty("data").GetString()!));
    }

    // ── Stubs ─────────────────────────────────────────────────────────────────

    private sealed class Handler((HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
    {
        private int _calls;

        public List<string> Captured { get; } = [];

        /// <summary>When set, the request with this 0-based index throws it instead of answering.</summary>
        public (int Call, Exception Exception)? ThrowOn { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Captured.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));

            if (ThrowOn is { } fault && fault.Call == _calls)
            {
                _calls++;
                throw fault.Exception;
            }

            var (status, body) = responses[Math.Min(_calls, responses.Length - 1)];
            _calls++;

            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    // Records every line, whatever its level (ClaudeExtractionPayloadTests.RecordingLogger keeps
    // configuration messages only).
    private sealed class AllLogger : ILogger<ClaudeService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    /// <summary>Records the pages it is asked about; answers with <see cref="Answer"/> (default: nothing judged).</summary>
    private sealed class FakeDetector : IScanOrientationDetector
    {
        public List<int[]> Calls { get; } = [];

        public Func<IReadOnlyList<int>, int?[]> Answer { get; init; } = pages => new int?[pages.Count];

        public ScanOrientationUsage? Usage { get; init; }

        public Exception? Throw { get; init; }

        public Task<ScanOrientationResult> DetectAsync(byte[] pdfBytes, IReadOnlyList<int> pageIndexes, string apiKey, CancellationToken ct)
        {
            Calls.Add(pageIndexes.ToArray());
            if (Throw is not null)
                throw Throw;

            return Task.FromResult(new ScanOrientationResult(Answer(pageIndexes), Usage));
        }
    }

    private sealed class Factory(Handler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
