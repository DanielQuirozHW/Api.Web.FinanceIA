// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-01

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Api.Web.FinanceIA.Clases.Exceptions;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Tests.Legajo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// The JSON <see cref="ClaudeService.AnalyzeDocumentAsync"/> puts on the wire for the balance
/// extraction, and how it reads the answer back. The default — no <c>Anthropic:ExtractionModel</c> —
/// is a PINNED payload: claude-sonnet-4-6, temperature 0, max_tokens 16000, same prompt, same key order,
/// and therefore a fixed InputHash. It differs from the payload sent when the model was a constant
/// ONLY in max_tokens (8000 then) and in the prompt edits made since (2.19, 2.20, 2.21) — each one undone
/// back to the previous pin — and the one max_tokens escalation differs from it only in max_tokens
/// again (32000). No live API call is made.
/// </summary>
public sealed class ClaudeExtractionPayloadTests
{
    private static readonly byte[] FakePdf = "%PDF-1.4 fixture"u8.ToArray();

    // SHA-256 of the default payload for FakePdf / "balance" / 2025. RE-PINNED DELIBERATELY on 2026-11-09 for
    // prompt 2.21 (alerts only for what the document shows): the model raised alerts whose only content was a ratio
    // level ("Deuda bancaria elevada") — the scoring engine already computes and grades the eight ratios — and spelled
    // the catalogue engine's facts freely. The SystemPrompt gained ONE contiguous insertion, ClaudeService.AlertScopeRules
    // (between the alert-title block and the ratio units), ONE inserted clause, ClaudeService.InterestCoverageAlertCodeClause
    // (the financial_expenses rule names COBERTURA_INTERESES_INDETERMINABLE), and ONE substituted title example
    // ("Deuda bancaria elevada" → ClaudeService.DocumentOnlyAlertTitleExample). Those are the ONLY bytes that moved —
    // Pre221PayloadSha256 below proves it. Captured by running this test against the 2.21 code. A literal on
    // purpose: the replica below could drift together with the code it replicates; a pinned hash cannot. Never
    // update it without saying, here, why the request changed.
    // Re-pinned pre-publication on 2026-11-10: the revenue drop's code in AlertScopeRules went from VENTAS_EN_CAIDA
    // (AttentionOverlaps' LUFE sales fact) to ClaudeService.IngresosEnCaidaAlertCode; only bytes inside that block
    // moved, so stripping 2.21 still lands on Pre221PayloadSha256.
    private const string DefaultPayloadSha256 =
        "0220ee8417c383d8c6c1d75fb1c57895309a507ca1ea5bebe410006ca6b8a38d";

    // The previous pin (prompt 2.20), RE-PINNED on 2026-11-04 (the answer is the JSON object and nothing else): long
    // balances answered with visible reasoning before the JSON, overflowed max_tokens 16000, escalated to 32000 and timed
    // out or came back prose-first. The SystemPrompt gained ONE contiguous insertion, ClaudeService.JsonOnlyAnswerRule,
    // right after the "respond EXCLUSIVELY with a valid JSON object" paragraph. Undo the three 2.21 edits and this hash
    // comes back.
    private const string Pre221PayloadSha256 =
        "9c056888ecb88100602f1c3177e55bba9658275d83be27ba2e4fc3a61d84e1de";

    // The 2.20 title example, as it stood before 2.21 replaced it.
    private const string Pre221AlertTitleExample = "\"Deuda bancaria elevada\"";

    // The previous pin (prompt 2.19), RE-PINNED on 2026-11-04 for trial balances ("Balance de Presentación" /
    // sumas y saldos): the SystemPrompt gained two contiguous insertions, ClaudeService.TrialBalanceSchemaLines
    // (document_kind + the trial_balance block in the schema) and ClaudeService.TrialBalanceRules (how to detect
    // one and keep its printed figures). Strip the 2.20 line and this hash comes back.
    private const string Pre220PayloadSha256 =
        "ef42f0fe1afec6ad6189b579d3b1ecae666aef915dcb1c52e7c5a7e75a8b4d34";

    // The pin before that (prompt 2.18, max_tokens 16000), RE-PINNED on 2026-11-03 when Sonnet46.MaxTokens went
    // from 8000 to 16000: complete balances with full notes already answered in 7.4k–8.4k tokens and a 10-page one
    // (EL OMBU) was cut off at 8000 on every run. Strip the 2.19 insertions and this hash comes back.
    // (Since 2.20, strip the 2.20 line first; since 2.21, undo the 2.21 edits before that.)
    private const string Pre219PayloadSha256 =
        "81e9a5ed805b412d9915c6ed1adc8b5b7493575a6a627723983b8d58494d72ba";

    // The pin before that: the 2.18 payload at max_tokens 8000, captured BEFORE the extraction model became
    // configurable. Strip the 2.20 line and the 2.19 insertions, swap 16000 back to 8000, and this hash comes back.
    private const string Pre16000PayloadSha256 =
        "43c8237855d8efc407ac0311bb97ba45a268bcb2c2b90f4f5ec8c40b52f78a4c";

    private const string MaxTokens16000 = "\"max_tokens\":16000";
    private const string MaxTokens8000  = "\"max_tokens\":8000";
    private const string MaxTokens32000 = "\"max_tokens\":32000";

    // The stubbed RESPONSE, not the request: no pinned InputHash depends on it. It carries one core total
    // (net_revenue) because an answer with none is a file with no financial statements, which the
    // extraction now fails (claude_no_financial_statements). A lone net_revenue runs no structure check.
    private const string OkModelJson =
        """{"ratios":{},"alerts":[],"anomalies":[],"executive_summary":"R.","confidence_score":0.9,"fiscal_year_detected":2025,"balance_check":{"net_revenue":1000,"balanced":true,"difference_pct":0}}""";

    // ── The default payload must not move ─────────────────────────────────────

    [Fact]
    public async Task Default_payload_is_the_pinned_one_and_differs_from_the_constant_model_payload_only_in_max_tokens()
    {
        var handler = new RecordingHandler(Envelope(OkModelJson));

        var result = await BuildSut(handler, new ExtractionModelOptions())
            .AnalyzeDocumentAsync(FakePdf, "balance", 2025, CancellationToken.None);

        var expected = DefaultPayload(FakePdf, "balance", 2025);

        Assert.Single(handler.Captured);
        Assert.Equal(expected, handler.Captured[0]);
        Assert.Equal(Sha256(expected), result.InputHash);
        Assert.Equal(DefaultPayloadSha256, Sha256(handler.Captured[0]));

        // The 2.21 re-pin moved the alert-scope block, the interest-coverage code clause and the title example, and
        // nothing else: strip the two insertions and put the old example back (as they travel, JSON-escaped inside
        // "system") and the 2.20 pin returns.
        var pre221 = Swapped(
            Without(Without(handler.Captured[0], ClaudeService.AlertScopeRules), ClaudeService.InterestCoverageAlertCodeClause),
            ClaudeService.DocumentOnlyAlertTitleExample,
            Pre221AlertTitleExample);
        Assert.NotEqual(handler.Captured[0], pre221);
        Assert.Equal(Pre221PayloadSha256, Sha256(pre221));

        // The 2.20 re-pin moved the JSON-only line and nothing else: strip it too and the 2.19 pin returns.
        var pre220 = Without(pre221, ClaudeService.JsonOnlyAnswerRule);
        Assert.NotEqual(pre221, pre220);
        Assert.Equal(Pre220PayloadSha256, Sha256(pre220));

        // The 2.19 re-pin moved the two trial-balance insertions and nothing else: strip them too and the 2.18
        // pin returns.
        var pre219 = WithoutTrialBalanceInsertions(pre220);
        Assert.NotEqual(pre220, pre219);
        Assert.Equal(Pre219PayloadSha256, Sha256(pre219));

        // The 16000 re-pin moved max_tokens and nothing else: put 8000 back too and the older pin returns.
        Assert.Single(Occurrences(pre219, MaxTokens16000));
        Assert.Equal(Pre16000PayloadSha256, Sha256(pre219.Replace(MaxTokens16000, MaxTokens8000, StringComparison.Ordinal)));

        // One pass, well under its cap.
        var pass = Assert.Single(result.ExtractionPasses);
        Assert.Equal(new ExtractionPassUsage("claude-sonnet-4-6", 16000, 20, false), pass);
        Assert.Equal("claude-sonnet-4-6", result.ModelUsed);
        Assert.Null(result.EffortUsed);
        Assert.Equal("2.21", result.PromptVersion);
        Assert.Equal("document", result.ExtractionMethod);
        Assert.Equal(ClaudeService.ExtractionHttpClientName, handler.LastClientName);

        // The happy path records that no second pass ran — in the persisted BalanceJson only; the
        // request above is the single, unchanged one.
        using var balance = JsonDocument.Parse(result.BalanceJson);
        var extraction = balance.RootElement.GetProperty("integrity").GetProperty("extraction");
        Assert.False(extraction.GetProperty("secondPassRan").GetBoolean());
        Assert.Equal("first", extraction.GetProperty("keptPass").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("claude-sonnet-4-6")]
    [InlineData(" CLAUDE-SONNET-4-6 ")]
    public async Task Default_model_named_or_blank_sends_the_same_bytes(string? configured)
    {
        var handler = new RecordingHandler(Envelope(OkModelJson));
        var logger  = new RecordingLogger();

        await BuildSut(handler, new ExtractionModelOptions { ExtractionModel = configured }, logger)
            .AnalyzeDocumentAsync(FakePdf, "balance", 2025, CancellationToken.None);

        Assert.Equal(DefaultPayloadSha256, Sha256(handler.Captured[0]));
        Assert.Empty(logger.Entries);
    }

    // ── Sonnet 5.5: no temperature, a larger max_tokens, effort only when configured ──

    [Fact]
    public async Task Sonnet55_without_effort_omits_temperature_and_output_config()
    {
        var handler = new RecordingHandler(Envelope(OkModelJson, leadingThinking: true));

        var result = await BuildSut(handler, new ExtractionModelOptions { ExtractionModel = "claude-sonnet-5-5" })
            .AnalyzeDocumentAsync(FakePdf, "balance", 2025, CancellationToken.None);

        using var doc = JsonDocument.Parse(handler.Captured[0]);
        var root = doc.RootElement;

        Assert.Equal("claude-sonnet-5-5", root.GetProperty("model").GetString());
        Assert.Equal(32000, root.GetProperty("max_tokens").GetInt32());
        Assert.True(root.GetProperty("max_tokens").GetInt32() > ExtractionModelCatalog.Sonnet46.MaxTokens);
        Assert.False(root.TryGetProperty("temperature", out _));
        Assert.False(root.TryGetProperty("output_config", out _));
        Assert.False(root.TryGetProperty("thinking", out _));
        Assert.Equal(ClaudeService.SystemPrompt, root.GetProperty("system").GetString());
        Assert.Equal(["model", "max_tokens", "system", "messages"], PropertyNames(root));

        Assert.Equal("claude-sonnet-5-5", result.ModelUsed);
        Assert.Null(result.EffortUsed);
        Assert.Equal(Sha256(handler.Captured[0]), result.InputHash);
    }

    [Theory]
    [InlineData("medium", "medium")]
    [InlineData(" HIGH ", "high")]
    [InlineData("xhigh", "xhigh")]
    public async Task Sonnet55_with_effort_sends_output_config_effort(string configured, string expected)
    {
        var handler = new RecordingHandler(Envelope(OkModelJson));

        var result = await BuildSut(handler, new ExtractionModelOptions
            {
                ExtractionModel  = "claude-sonnet-5-5",
                ExtractionEffort = configured
            })
            .AnalyzeDocumentAsync(FakePdf, "balance", 2025, CancellationToken.None);

        using var doc = JsonDocument.Parse(handler.Captured[0]);
        var root = doc.RootElement;

        Assert.Equal(["model", "max_tokens", "system", "messages", "output_config"], PropertyNames(root));
        Assert.Equal(expected, root.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Single(root.GetProperty("output_config").EnumerateObject());
        Assert.False(root.TryGetProperty("temperature", out _));
        Assert.Equal(expected, result.EffortUsed);
    }

    [Fact]
    public async Task Sonnet5_has_the_same_request_surface_as_Sonnet55()
    {
        var handler = new RecordingHandler(Envelope(OkModelJson));

        await BuildSut(handler, new ExtractionModelOptions { ExtractionModel = "claude-sonnet-5", ExtractionEffort = "low" })
            .AnalyzeDocumentAsync(FakePdf, "balance", 2025, CancellationToken.None);

        using var doc = JsonDocument.Parse(handler.Captured[0]);
        Assert.Equal("claude-sonnet-5", doc.RootElement.GetProperty("model").GetString());
        Assert.False(doc.RootElement.TryGetProperty("temperature", out _));
        Assert.Equal("low", doc.RootElement.GetProperty("output_config").GetProperty("effort").GetString());
    }

    [Fact]
    public async Task Effort_on_the_default_model_is_ignored_with_one_warning()
    {
        var handler = new RecordingHandler(Envelope(OkModelJson));
        var logger  = new RecordingLogger();
        var options = new ExtractionModelOptions { ExtractionEffort = "high" };

        await BuildSut(handler, options, logger).AnalyzeDocumentAsync(FakePdf, "balance", 2025, CancellationToken.None);
        await BuildSut(handler, options, logger).AnalyzeDocumentAsync(FakePdf, "balance", 2025, CancellationToken.None);

        Assert.All(handler.Captured, c => Assert.Equal(DefaultPayloadSha256, Sha256(c)));
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(ExtractionModelSelection.EffortSetting, entry.Message);
    }

    // ── An unusable setting never breaks an analysis ──────────────────────────

    [Fact]
    public async Task Unknown_model_falls_back_to_the_default_with_one_error()
    {
        var handler    = new RecordingHandler(Envelope(OkModelJson));
        var logger     = new RecordingLogger();
        var unknownId  = $"claude-made-up-{Guid.NewGuid():N}";
        var options    = new ExtractionModelOptions { ExtractionModel = unknownId };

        var first  = await BuildSut(handler, options, logger).AnalyzeDocumentAsync(FakePdf, "balance", 2025, CancellationToken.None);
        var second = await BuildSut(handler, options, logger).AnalyzeDocumentAsync(FakePdf, "balance", 2025, CancellationToken.None);

        Assert.All(handler.Captured, c => Assert.Equal(DefaultPayloadSha256, Sha256(c)));
        Assert.Equal("claude-sonnet-4-6", first.ModelUsed);
        Assert.Equal("claude-sonnet-4-6", second.ModelUsed);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains(ExtractionModelSelection.ModelSetting, entry.Message);
        Assert.Contains(unknownId, entry.Message);
    }

    [Fact]
    public async Task Invalid_effort_sends_none_with_one_error()
    {
        var handler = new RecordingHandler(Envelope(OkModelJson));
        var logger  = new RecordingLogger();
        var effort  = $"turbo-{Guid.NewGuid():N}";
        var options = new ExtractionModelOptions { ExtractionModel = "claude-sonnet-5-5", ExtractionEffort = effort };

        var result = await BuildSut(handler, options, logger).AnalyzeDocumentAsync(FakePdf, "balance", 2025, CancellationToken.None);
        await BuildSut(handler, options, logger).AnalyzeDocumentAsync(FakePdf, "balance", 2025, CancellationToken.None);

        using var doc = JsonDocument.Parse(handler.Captured[0]);
        Assert.Equal("claude-sonnet-5-5", doc.RootElement.GetProperty("model").GetString());
        Assert.False(doc.RootElement.TryGetProperty("output_config", out _));
        Assert.Null(result.EffortUsed);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains(ExtractionModelSelection.EffortSetting, entry.Message);
    }

    [Theory]
    [InlineData(null, "claude-sonnet-4-6", 120)]
    [InlineData(null, "claude-sonnet-5-5", 300)]
    [InlineData(200, "claude-sonnet-5-5", 200)]
    [InlineData(5, "claude-sonnet-4-6", 30)]
    [InlineData(100_000, "claude-sonnet-4-6", 900)]
    public void Timeout_comes_from_the_spec_unless_overridden_and_is_clamped(int? configured, string model, int expectedSeconds)
    {
        var selection = ExtractionModelSelection.Resolve(
            new ExtractionModelOptions { ExtractionModel = model, ExtractionTimeoutSeconds = configured },
            new RecordingLogger());

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), selection.Timeout);
    }

    // ── The response: thinking block, stop reasons ────────────────────────────

    [Fact]
    public async Task A_leading_thinking_block_is_skipped()
    {
        var handler = new RecordingHandler(Envelope(OkModelJson, leadingThinking: true));

        var result = await BuildSut(handler, new ExtractionModelOptions { ExtractionModel = "claude-sonnet-5-5" })
            .AnalyzeDocumentAsync(FakePdf, "balance", 2025, CancellationToken.None);

        Assert.Equal(0.9m, result.ConfidenceScore);
        Assert.Equal(10, result.InputTokens);
        Assert.Equal(20, result.OutputTokens);
    }

    [Theory]
    [InlineData("max_tokens", "max_tokens (32000)")]
    [InlineData("refusal", "stop_reason=refusal")]
    public async Task Max_tokens_and_refusal_fail_the_pass_without_a_retry(string stopReason, string expectedFragment)
    {
        // A complete, valid JSON body: the stop reason alone must fail the pass, before any content.
        var handler = new RecordingHandler(Envelope(OkModelJson, stopReason: stopReason, leadingThinking: true));

        var ex = await Assert.ThrowsAsync<ClaudeApiException>(() =>
            BuildSut(handler, new ExtractionModelOptions { ExtractionModel = "claude-sonnet-5-5" })
                .AnalyzeDocumentAsync(FakePdf, "balance", 2025, CancellationToken.None));

        Assert.Contains(expectedFragment, ex.DiagnosticMessage);
        Assert.Contains("claude-sonnet-5-5", ex.DiagnosticMessage);
        Assert.Single(handler.Captured);
    }

    // ── One escalation on max_tokens (Sonnet 4.6) ─────────────────────────────

    [Fact]
    public async Task Max_tokens_on_the_default_model_is_resent_once_differing_only_in_max_tokens()
    {
        var handler = new RecordingHandler(Envelope(OkModelJson, stopReason: "max_tokens"), Envelope(OkModelJson));

        var result = await BuildSut(handler, new ExtractionModelOptions())
            .AnalyzeDocumentAsync(FakePdf, "balance", 2025, CancellationToken.None);

        Assert.Equal(2, handler.Captured.Count);
        // The first request is the pinned default one; the escalated one is that request with the cap
        // doubled, byte for byte otherwise.
        Assert.Equal(DefaultPayloadSha256, Sha256(handler.Captured[0]));
        Assert.Single(Occurrences(handler.Captured[1], MaxTokens32000));
        Assert.Equal(handler.Captured[0].Replace(MaxTokens16000, MaxTokens32000, StringComparison.Ordinal), handler.Captured[1]);

        // Both calls were billed: their tokens are summed into the pass (and so into the one cost row).
        Assert.Equal(20, result.InputTokens);
        Assert.Equal(40, result.OutputTokens);
        Assert.Equal("claude-sonnet-4-6", result.ModelUsed);

        // The hashes describe the call that answered: the escalated request.
        Assert.Equal(Sha256(handler.Captured[1]), result.InputHash);
        var pass = Assert.Single(result.ExtractionPasses);
        Assert.Equal(new ExtractionPassUsage("claude-sonnet-4-6", 32000, 20, true), pass);
    }

    [Fact]
    public async Task Max_tokens_again_after_the_escalation_fails_the_pass_with_both_calls_tokens()
    {
        var handler = new RecordingHandler(
            Envelope(OkModelJson, stopReason: "max_tokens"),
            Envelope(OkModelJson, stopReason: "max_tokens"),
            Envelope(OkModelJson));

        var ex = await Assert.ThrowsAsync<ClaudeApiException>(() =>
            BuildSut(handler, new ExtractionModelOptions())
                .AnalyzeDocumentAsync(FakePdf, "balance", 2025, CancellationToken.None));

        // Exactly one escalation: never a third request.
        Assert.Equal(2, handler.Captured.Count);
        Assert.Contains("hit max_tokens (32000) after escalation", ex.DiagnosticMessage);
        Assert.Contains("claude-sonnet-4-6", ex.DiagnosticMessage);
        Assert.Equal(ClaudeService.AnswerMaxTokens, ex.FailureCode);
        Assert.Equal(20, ex.InputTokens);
        Assert.Equal(40, ex.OutputTokens);
        Assert.Equal("claude-sonnet-4-6", ex.Model);
        // The client-facing text never changes: no reason, no figure reaches an API response.
        Assert.Equal("El servicio de análisis de IA no está disponible temporalmente.", ex.Message);
    }

    [Fact]
    public async Task The_escalated_call_gets_one_attempt_and_its_failure_carries_the_first_calls_tokens()
    {
        // A 5xx on a normal pass is retried; on the escalated call it is not (one attempt).
        var handler = new RecordingHandler(Envelope(OkModelJson, stopReason: "max_tokens"), "{}", Envelope(OkModelJson))
        {
            Statuses = [HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK]
        };

        var ex = await Assert.ThrowsAsync<ClaudeApiException>(() =>
            BuildSut(handler, new ExtractionModelOptions())
                .AnalyzeDocumentAsync(FakePdf, "balance", 2025, CancellationToken.None));

        Assert.Equal(2, handler.Captured.Count);
        Assert.Equal(503, ex.ApiStatusCode);
        Assert.Null(ex.FailureCode);   // a transport failure, not an unusable answer: the status classifies it
        Assert.Equal(10, ex.InputTokens);
        Assert.Equal(20, ex.OutputTokens);
        Assert.Contains("escalated call after max_tokens 16000", ex.DiagnosticMessage);
    }

    [Fact]
    public async Task A_refusal_on_the_default_model_is_never_escalated()
    {
        var handler = new RecordingHandler(Envelope(OkModelJson, stopReason: "refusal"), Envelope(OkModelJson));

        var ex = await Assert.ThrowsAsync<ClaudeApiException>(() =>
            BuildSut(handler, new ExtractionModelOptions())
                .AnalyzeDocumentAsync(FakePdf, "balance", 2025, CancellationToken.None));

        Assert.Single(handler.Captured);
        Assert.Equal(ClaudeService.AnswerRefusal, ex.FailureCode);
    }

    [Theory]
    // Prose with no JSON at all is claude_unparseable_answer now: ClaudeExtractionJsonExtractionTests.
    [InlineData("[1, 2, 3]",                       ClaudeService.AnswerSchemaInvalid)]   // JSON, not a balance
    [InlineData("null",                            ClaudeService.AnswerEmpty)]
    public async Task An_answer_that_is_not_a_balance_carries_its_own_failure_code(string modelText, string expected)
    {
        var handler = new RecordingHandler(Envelope(modelText));

        var ex = await Assert.ThrowsAsync<ClaudeApiException>(() =>
            BuildSut(handler, new ExtractionModelOptions())
                .AnalyzeDocumentAsync(FakePdf, "balance", 2025, CancellationToken.None));

        Assert.Single(handler.Captured);
        Assert.Equal(expected, ex.FailureCode);
        Assert.Null(ex.ApiStatusCode);
        Assert.Equal(10, ex.InputTokens);   // the call was billed and its usage is known
        Assert.Equal(20, ex.OutputTokens);
        Assert.Equal("El servicio de análisis de IA no está disponible temporalmente.", ex.Message);
    }

    [Theory]
    [InlineData(16000, null, 32000, 240)]   // the default: 120 s scaled ×2
    [InlineData(16000, 480,  32000, 900)]   // 960 s capped at 900
    [InlineData(16000, 300,  32000, 600)]
    [InlineData(8000,  null, 16000, 240)]   // any spec with a temperature doubles
    [InlineData(20000, 120,  32000, 192)]   // capped at 32000; the timeout scaled by the same factor (1.6)
    public void EscalateForMaxTokens_doubles_the_cap_and_scales_the_timeout_alike(
        int maxTokens, int? timeoutOverride, int expectedMaxTokens, int expectedSeconds)
    {
        var resolved  = ExtractionModelSelection.Resolve(
            new ExtractionModelOptions { ExtractionTimeoutSeconds = timeoutOverride }, new RecordingLogger());
        var selection = resolved with { Spec = resolved.Spec with { MaxTokens = maxTokens } };

        var escalated = selection.EscalateForMaxTokens();

        Assert.NotNull(escalated);
        Assert.Equal(expectedMaxTokens, escalated.Spec.MaxTokens);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), escalated.Timeout);
        // Nothing else of the request moves.
        Assert.Equal(selection.Spec with { MaxTokens = expectedMaxTokens }, escalated.Spec);
        Assert.Equal(selection.Effort, escalated.Effort);
    }

    [Fact]
    public void EscalateForMaxTokens_has_nothing_for_a_5x_spec_or_a_cap_already_at_the_ceiling()
    {
        Assert.Null((ExtractionModelSelection.Default with { Spec = ExtractionModelCatalog.Sonnet55 }).EscalateForMaxTokens());
        Assert.Null((ExtractionModelSelection.Default with { Spec = ExtractionModelCatalog.Sonnet5 }).EscalateForMaxTokens());
        Assert.Null((ExtractionModelSelection.Default with
        {
            Spec = ExtractionModelCatalog.Sonnet46 with { MaxTokens = ExtractionModelCatalog.MaxEscalatedTokens }
        }).EscalateForMaxTokens());

        // The escalation of an escalation is none: there is never a second one.
        var once = ExtractionModelSelection.Default.EscalateForMaxTokens();
        Assert.NotNull(once);
        Assert.Null(once.EscalateForMaxTokens());
    }

    [Theory]
    [InlineData(11999, 16000, false)]
    [InlineData(12000, 16000, true)]
    [InlineData(16000, 16000, true)]
    [InlineData(0,     0,     false)]
    public void IsNearCap_is_75_percent_of_the_cap(int outputTokens, int maxTokens, bool expected)
        => Assert.Equal(expected, ExtractionModelCatalog.IsNearCap(outputTokens, maxTokens));

    [Fact]
    public async Task A_response_without_stop_reason_still_parses()
    {
        var handler = new RecordingHandler(Envelope(OkModelJson, stopReason: null));

        var result = await BuildSut(handler, new ExtractionModelOptions())
            .AnalyzeDocumentAsync(FakePdf, "balance", 2025, CancellationToken.None);

        Assert.Equal(0.9m, result.ConfidenceScore);
    }

    // ── The vision fallback pass uses the same spec ───────────────────────────

    [Fact]
    public async Task Vision_fallback_pass_uses_the_same_model_spec()
    {
        // Low confidence on the document pass, threshold 0.60: the second (image) pass runs.
        var low     = OkModelJson.Replace("\"confidence_score\":0.9", "\"confidence_score\":0.3", StringComparison.Ordinal);
        var handler = new RecordingHandler(Envelope(low), Envelope(OkModelJson, leadingThinking: true));

        var result = await BuildSut(handler, new ExtractionModelOptions { ExtractionModel = "claude-sonnet-5-5", ExtractionEffort = "medium" }, threshold: "0.60")
            .AnalyzeDocumentAsync(LegajoPdfFactory.BlankPdf(1), "balance", 2025, CancellationToken.None);

        Assert.Equal(2, handler.Captured.Count);
        foreach (var body in handler.Captured)
        {
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("claude-sonnet-5-5", doc.RootElement.GetProperty("model").GetString());
            Assert.Equal(32000, doc.RootElement.GetProperty("max_tokens").GetInt32());
            Assert.False(doc.RootElement.TryGetProperty("temperature", out _));
            Assert.Equal("medium", doc.RootElement.GetProperty("output_config").GetProperty("effort").GetString());
        }

        using (var second = JsonDocument.Parse(handler.Captured[1]))
        {
            var firstBlock = second.RootElement.GetProperty("messages")[0].GetProperty("content")[0];
            Assert.Equal("image", firstBlock.GetProperty("type").GetString());
        }

        Assert.Equal("image-fallback", result.ExtractionMethod);
        Assert.Equal("claude-sonnet-5-5", result.ModelUsed);
        Assert.Equal(40, result.OutputTokens);
    }

    // ── Catalogue invariants ──────────────────────────────────────────────────

    [Fact]
    public void Catalogue_default_is_today_and_no_entry_mixes_temperature_with_effort()
    {
        Assert.Same(ExtractionModelCatalog.Sonnet46, ExtractionModelCatalog.Default);
        Assert.Equal(16000, ExtractionModelCatalog.Default.MaxTokens);
        Assert.Equal(0m, ExtractionModelCatalog.Default.Temperature);
        Assert.False(ExtractionModelCatalog.Default.SupportsEffort);
        Assert.Equal(120, ExtractionModelCatalog.Default.TimeoutSeconds);

        foreach (var spec in ExtractionModelCatalog.All)
        {
            // BuildExtractionRequestJson has no shape for both: a spec with a temperature never
            // carries an effort, which is what keeps the default payload a single fixed shape.
            Assert.False(spec.Temperature is not null && spec.SupportsEffort, spec.Id);
            Assert.True(spec.MaxTokens >= 8000, spec.Id);
            Assert.InRange(spec.TimeoutSeconds, ExtractionModelCatalog.MinTimeoutSeconds, ExtractionModelCatalog.MaxTimeoutSeconds);
        }

        Assert.Equal(ExtractionModelCatalog.All.Count, ExtractionModelCatalog.All.Select(s => s.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.True(ExtractionModelCatalog.Sonnet55.MaxTokens >= 32000);
        Assert.Equal(32000, ExtractionModelCatalog.MaxEscalatedTokens);
        Assert.Equal(0.75m, ExtractionModelCatalog.NearCapWarningRatio);
        Assert.Null(ExtractionModelCatalog.Sonnet55.Temperature);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A literal replica of the default request ExecuteExtractionPassAsync builds — the constant-model
    /// request with max_tokens 16000 — for a PDF under 5 MB (one document block + one text block).
    /// </summary>
    // Each 2.19 insertion exactly as it travels in the request: JSON-escaped by the same serializer, quotes trimmed.
    private static string WithoutTrialBalanceInsertions(string payload) =>
        Without(Without(payload, ClaudeService.TrialBalanceSchemaLines), ClaudeService.TrialBalanceRules);

    // One prompt insertion removed exactly as it travels: JSON-escaped by the same serializer, quotes trimmed,
    // present exactly once.
    private static string Without(string payload, string insertion)
    {
        var escaped = JsonSerializer.Serialize(insertion)[1..^1];
        Assert.Single(Occurrences(payload, escaped));
        return payload.Replace(escaped, string.Empty, StringComparison.Ordinal);
    }

    // One prompt substitution undone exactly as it travels: both texts JSON-escaped by the same serializer, quotes
    // trimmed, the current one present exactly once.
    private static string Swapped(string payload, string current, string previous)
    {
        var escapedCurrent  = JsonSerializer.Serialize(current)[1..^1];
        var escapedPrevious = JsonSerializer.Serialize(previous)[1..^1];
        Assert.Single(Occurrences(payload, escapedCurrent));
        Assert.Empty(Occurrences(payload, escapedPrevious));
        return payload.Replace(escapedCurrent, escapedPrevious, StringComparison.Ordinal);
    }

    private static string DefaultPayload(byte[] pdf, string documentType, int? fiscalYear)
    {
        var yearText = fiscalYear.HasValue ? $" for fiscal year {fiscalYear}" : string.Empty;
        var blocks = new List<object>
        {
            new
            {
                type   = "document",
                source = new
                {
                    type       = "base64",
                    media_type = "application/pdf",
                    data       = Convert.ToBase64String(pdf)
                }
            },
            new
            {
                type = "text",
                text = $"Analyse this {documentType} financial document{yearText} and return the JSON result."
            }
        };

        return JsonSerializer.Serialize(new
        {
            model       = "claude-sonnet-4-6",
            max_tokens  = 16000,
            temperature = 0,
            system      = ClaudeService.SystemPrompt,
            messages    = new[]
            {
                new { role = "user", content = blocks }
            }
        }, new JsonSerializerOptions { PropertyNamingPolicy = null });
    }

    private static ClaudeService BuildSut(
        RecordingHandler handler,
        ExtractionModelOptions options,
        ILogger<ClaudeService>? logger = null,
        string threshold = "0") =>
        new(new StubHttpClientFactory(handler),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Anthropic:ApiKey"] = "test-key",
                    ["Anthropic:ExtractionConfidenceFallbackThreshold"] = threshold
                })
                .Build(),
            logger ?? new RecordingLogger(),
            new BalanceIntegrityService(),
            Options.Create(options),
            NullScanOrientationDetector.Instance);

    private static string Envelope(string modelJson, string? stopReason = "end_turn", bool leadingThinking = false)
    {
        var content = new List<object>();
        if (leadingThinking)
            content.Add(new { type = "thinking", thinking = "", signature = "sig" });
        content.Add(new { type = "text", text = modelJson });

        return JsonSerializer.Serialize(new
        {
            content,
            stop_reason = stopReason,
            usage       = new { input_tokens = 10, output_tokens = 20 }
        });
    }

    private static string[] PropertyNames(JsonElement element) =>
        element.EnumerateObject().Select(p => p.Name).ToArray();

    private static IEnumerable<int> Occurrences(string text, string value)
    {
        for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + 1, StringComparison.Ordinal))
            yield return at;
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    // ── Stubs ─────────────────────────────────────────────────────────────────

    private sealed class RecordingHandler(params string[] responseBodies) : HttpMessageHandler
    {
        private int _calls;

        public List<string> Captured { get; } = [];

        public string? LastClientName { get; set; }

        /// <summary>Status of each response, in call order (the last repeats); OK when unset.</summary>
        public HttpStatusCode[]? Statuses { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Read INSIDE SendAsync: ClaudeService disposes the request the moment SendAsync returns.
            Captured.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

            var body   = responseBodies[Math.Min(_calls, responseBodies.Length - 1)];
            var status = Statuses is { Length: > 0 } ? Statuses[Math.Min(_calls, Statuses.Length - 1)] : HttpStatusCode.OK;
            _calls++;

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class StubHttpClientFactory(RecordingHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            handler.LastClientName = name;
            return new HttpClient(handler, disposeHandler: false);
        }
    }

    internal sealed class RecordingLogger : ILogger<ClaudeService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            // Only configuration problems are recorded: the parse log lines are not under test here.
            var message = formatter(state, exception);
            if (message.StartsWith("Configuration setting ", StringComparison.Ordinal))
                Entries.Add((logLevel, message));
        }
    }
}
