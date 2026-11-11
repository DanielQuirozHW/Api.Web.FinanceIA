// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-03

using System.Globalization;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.BalanceCorrections;
using Api.Web.FinanceIA.Tests.AnalysisRuns;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.BalanceCorrections;

/// <summary>
/// POST /api/analyses/{resultId}/balance-corrections/suggestions end to end over <see cref="RescoreTestWorld"/>:
/// the REAL service, repositories and integrity checks; the model scripted. Every refusal and failure leaves
/// the result exactly as it was; the only write is the ApiCallLogs cost row.
/// </summary>
public sealed class BalanceCorrectionSuggestionServiceTests
{
    private readonly RescoreTestWorld   _world   = new();
    private readonly FakeStorage        _storage = new();
    private readonly CapturingApiCallLog _calls  = new();
    private readonly RecordingLogger    _logger  = new();
    private readonly MemoryCache        _cache   = new(new MemoryCacheOptions());
    private readonly BalanceSuggestionInFlight _inFlight = new();
    private readonly MutableClock       _clock   = new(new DateTimeOffset(2026, 11, 3, 15, 0, 0, TimeSpan.Zero));

    // ── Refusals: the coded problems, nothing spent, nothing written ────────────────

    [Fact]
    public async Task An_unknown_result_is_404()
    {
        var outcome = await SuggestAsync(Guid.NewGuid());

        Assert.Equal(404, outcome.StatusCode);
        Assert.Null(outcome.ErrorCode);
        Assert.Empty(_world.Claude.SuggestionCalls);
    }

    [Fact]
    public async Task Another_tenants_result_is_404()
    {
        var entity = await _world.SeedEntityAsync(tenantId: RescoreTestWorld.OtherTenant);
        var (_, other, _) = await _world.SeedAnalysisAsync(entity, configureResult: r => r.BalanceJson = BalanceCorrectionFixtures.FinexBalanceJson());

        Assert.Equal(404, (await SuggestAsync(other.ResultId)).StatusCode);
        Assert.Empty(_world.Claude.SuggestionCalls);
    }

    [Fact]
    public async Task A_persona_humana_analysis_is_409_person_analysis_not_correctable()
    {
        var entity = await _world.SeedEntityAsync();
        var (_, persona, _) = await _world.SeedAnalysisAsync(entity,
            a => { a.AnalysisKind = AnalysisKinds.PersonaHumana; a.DocumentId = null; },
            r => r.BalanceJson = null);

        await AssertRefusedAsync(persona, 409, RescoreErrorCodes.PersonAnalysisNotCorrectable);
    }

    [Fact]
    public async Task A_decided_analysis_is_the_rescores_409_before_any_call()
    {
        var seeded = await SeedAsync(BalanceCorrectionFixtures.FinexBalanceJson(), a => a.Status = AnalysisStatuses.Approved);

        await AssertRefusedAsync(seeded, 409, RescoreErrorCodes.NotPending);
    }

    [Fact]
    public async Task A_voted_analysis_is_the_rescores_409_before_any_call()
    {
        var seeded = await SeedAsync(BalanceCorrectionFixtures.FinexBalanceJson());
        await _world.AddVoteAsync(seeded.AnalysisId);

        await AssertRefusedAsync(seeded, 409, RescoreErrorCodes.HasVotes);
    }

    [Theory]
    [InlineData("passed")]
    [InlineData("only_soft_check")]
    public async Task A_balance_with_no_failed_targeted_check_is_409_suggestion_not_applicable(string shape)
    {
        // "only_soft_check": a sign check alone (net_profit_reconciliation on its own IS applicable).
        var seeded = await SeedAsync(shape == "passed"
            ? BalanceCorrectionFixtures.CorrectedFigures()
            : BalanceSuggestionFixtures.SignOnlyBalanceJson());

        await AssertRefusedAsync(seeded, 409, RescoreErrorCodes.SuggestionNotApplicable);
        Assert.Empty(_storage.Reads);
    }

    [Fact]
    public async Task A_document_that_cannot_be_read_is_409_document_unavailable()
    {
        var seeded = await SeedAsync(BalanceCorrectionFixtures.FinexBalanceJson());
        _storage.ReadThrows = new FileNotFoundException("missing");

        await AssertRefusedAsync(seeded, 409, RescoreErrorCodes.DocumentUnavailable);
    }

    [Fact]
    public async Task A_deleted_document_is_409_document_unavailable()
    {
        var seeded = await SeedAsync(BalanceCorrectionFixtures.FinexBalanceJson(), document: d => d.IsDeleted = true);

        await AssertRefusedAsync(seeded, 409, RescoreErrorCodes.DocumentUnavailable);
        Assert.Empty(_storage.Reads);
    }

    [Fact]
    public async Task A_model_failure_is_502_with_a_failure_cost_row_and_nothing_else_written()
    {
        var seeded = await SeedAsync(BalanceCorrectionFixtures.FinexBalanceJson());
        var before = RescoreTestWorld.Fingerprint((await _world.ReadResultAsync(seeded.ResultId))!);
        _world.Claude.Suggestion = _ => new BalanceSuggestionReply
        {
            Model        = BalanceSuggestionFixtures.Model,
            RequestSent  = true,
            HttpStatus   = 200,
            InputTokens  = 9_000,
            OutputTokens = 32_000,
            DurationMs   = 100,
            FailureCode  = "max_tokens",
        };

        var outcome = await SuggestAsync(seeded.ResultId);

        Assert.Equal(502, outcome.StatusCode);
        Assert.Equal(RescoreErrorCodes.SuggestionUnavailable, outcome.ErrorCode);
        Assert.Equal(before, RescoreTestWorld.Fingerprint((await _world.ReadResultAsync(seeded.ResultId))!));
        Assert.Empty(await ReadEditsAsync());

        var row = Assert.Single(_calls.Rows);
        Assert.False(row.IsSuccess);
        Assert.Equal("max_tokens", row.ErrorMessage);
        Assert.Equal(ApiCallLogOperations.Extraction, row.Operation);
        Assert.Equal(seeded.AnalysisId, row.AnalysisId);
        Assert.Equal(ClaudeModelPricing.EstimateCostUsd(BalanceSuggestionFixtures.Model, 9_000, 32_000), row.Cost);
    }

    [Fact]
    public async Task An_http_error_row_carries_the_code_then_the_api_reason()
    {
        var seeded = await SeedAsync(BalanceCorrectionFixtures.FinexBalanceJson());
        _world.Claude.Suggestion = _ => new BalanceSuggestionReply
        {
            Model         = BalanceSuggestionFixtures.Model,
            RequestSent   = true,
            HttpStatus    = 400,
            DurationMs    = 10,
            FailureCode   = "http_400",
            FailureDetail = "Claude API error: HTTP 400 invalid_request_error: bad image [req_x].",
        };

        Assert.Equal(502, (await SuggestAsync(seeded.ResultId)).StatusCode);

        var row = Assert.Single(_calls.Rows);
        Assert.False(row.IsSuccess);
        Assert.Equal("http_400 | Claude API error: HTTP 400 invalid_request_error: bad image [req_x].", row.ErrorMessage);
    }

    [Fact]
    public async Task A_render_failure_before_the_request_is_502_with_no_request_row_but_the_detector_row()
    {
        var seeded = await SeedAsync(BalanceCorrectionFixtures.FinexBalanceJson());
        _world.Claude.Suggestion = _ => new BalanceSuggestionReply
        {
            Model                = BalanceSuggestionFixtures.Model,
            FailureCode          = "render_failed",
            OrientationDetection = new ScanOrientationUsage
            {
                Model = "claude-haiku-4-5", InputTokens = 500, OutputTokens = 20, DurationMs = 900, Succeeded = true,
                HttpStatus = 200, PagesAsked = 2, PagesAnswered = 2
            },
        };

        var outcome = await SuggestAsync(seeded.ResultId);

        Assert.Equal(502, outcome.StatusCode);
        var row = Assert.Single(_calls.Rows);
        Assert.Equal("claude-haiku-4-5", row.Model);
        Assert.True(row.IsSuccess);
    }

    // ── Outcomes ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Finex_the_model_proposes_zero_for_the_dash_and_the_suggestion_fixes_the_balance()
    {
        var seeded = await SeedAsync(BalanceCorrectionFixtures.FinexBalanceJson());
        var before = RescoreTestWorld.Fingerprint((await _world.ReadResultAsync(seeded.ResultId))!);
        _world.Claude.Suggestion = _ => BalanceSuggestionFixtures.Reply(
            BalanceSuggestionFixtures.FinexFixAnswer(), [BalanceSuggestionFixtures.Image(1, 2, RenderedImage.FullHalf)]);

        var outcome = await SuggestAsync(seeded.ResultId);

        Assert.True(outcome.IsSuccess);
        var response = outcome.Response!;
        Assert.Equal(BalanceSuggestionOutcomes.Fixes, response.Outcome);
        Assert.Equal(BalanceSuggestionFixtures.Model, response.Model);
        Assert.Equal([BalanceIntegrityService.CurrentAssetsItemsCheck], response.FailedChecksBefore);
        Assert.Empty(response.FailedChecksAfter);
        var suggestion = Assert.Single(response.Suggestions);
        Assert.Equal(BalanceCorrectionFixtures.MisreadPath, suggestion.Path);
        Assert.Equal(0d, suggestion.SuggestedValue);

        // The question carried numbers in printed units and fixed hints; the PDF went through storage.
        var (pdf, question) = Assert.Single(_world.Claude.SuggestionCalls);
        Assert.Single(_storage.Reads);
        Assert.Equal(200_000d, question.Fields.Single().StoredPrinted);
        Assert.Equal(119_378.23322, question.ItemGroups.Single().StoredPrinted[1]!.Value, 6);

        // A suggestion is never applied: the row is untouched; the cost row is the only write.
        Assert.Equal(before, RescoreTestWorld.Fingerprint((await _world.ReadResultAsync(seeded.ResultId))!));
        Assert.Empty(await ReadEditsAsync());
        Assert.Empty(await _world.ReadHistoryAsync());
        var row = Assert.Single(_calls.Rows);
        Assert.True(row.IsSuccess);
        Assert.Equal(200, row.ResponseStatus);
        Assert.Equal(12_000, row.InputTokens);
        Assert.Equal(800, row.OutputTokens);
        Assert.Equal(BalanceSuggestionFixtures.Model, row.Model);
        Assert.Equal(ApiCallLogOperations.Extraction, row.Operation);
        Assert.Equal(_world.UserId, row.UserId);
        Assert.Equal(ClaudeModelPricing.EstimateCostUsd(BalanceSuggestionFixtures.Model, 12_000, 800), row.Cost);
    }

    [Fact]
    public async Task Sideways_scan_the_total_and_the_extra_item_fix_every_check()
    {
        var seeded = await SeedAsync(BalanceSuggestionFixtures.SidewaysBalanceJson());
        _world.Claude.Suggestion = _ => BalanceSuggestionFixtures.Reply(
            BalanceSuggestionFixtures.SidewaysFixAnswer(), BalanceSuggestionFixtures.SidewaysImages());

        var response = (await SuggestAsync(seeded.ResultId)).Response!;

        Assert.Equal(BalanceSuggestionOutcomes.Fixes, response.Outcome);
        Assert.Empty(response.FailedChecksAfter);
        Assert.Equal(["current_liabilities", "current_liabilities_items[2].amount"], response.Suggestions.Select(s => s.Path));
        Assert.Equal(150_000_000d, response.Suggestions[0].SuggestedValue);
        Assert.NotNull(response.Suggestions[0].Evidence);
    }

    [Fact]
    public async Task The_suggested_figure_carries_where_it_is_printed_on_its_page_entry()
    {
        var seeded = await SeedAsync(BalanceSuggestionFixtures.SidewaysBalanceJson());
        _storage.Bytes = BalanceSuggestionTextMatchTests.SidewaysPdf();
        _world.Claude.Suggestion = _ => BalanceSuggestionFixtures.Reply(
            BalanceSuggestionFixtures.SidewaysFixAnswer(), BalanceSuggestionTextMatchTests.SidewaysImages(_storage.Bytes));

        var response = (await SuggestAsync(seeded.ResultId)).Response!;

        var match = Assert.IsType<BalanceSuggestionTextMatch>(response.Suggestions[0].TextMatch);
        Assert.Equal(2, match.Page);
        Assert.Contains(response.Pages, p => p.Page == match.Page);
        Assert.Null(response.Suggestions[1].TextMatch);
        Assert.Contains(_logger.Messages, m => m.Contains("with text match 1 (secondary 0, searched 1)", StringComparison.Ordinal));

        // Served from the cache with it, at no cost.
        var cached = (await SuggestAsync(seeded.ResultId)).Response!;
        Assert.NotNull(cached.CachedAt);
        Assert.Equal(match, cached.Suggestions[0].TextMatch);
        Assert.Single(_world.Claude.SuggestionCalls);
    }

    [Fact]
    public async Task An_unreadable_text_layer_costs_the_text_match_only()
    {
        // FakeStorage serves no PDF at all by default: the suggestion is still a 200, with no text match.
        var seeded = await SeedAsync(BalanceSuggestionFixtures.SidewaysBalanceJson());
        _world.Claude.Suggestion = _ => BalanceSuggestionFixtures.Reply(
            BalanceSuggestionFixtures.SidewaysFixAnswer(), BalanceSuggestionFixtures.SidewaysImages());

        var outcome = await SuggestAsync(seeded.ResultId);

        Assert.Equal(200, outcome.StatusCode);
        Assert.All(outcome.Response!.Suggestions, s => Assert.Null(s.TextMatch));
        Assert.Contains(_logger.Messages, m => m.Contains("the text layer could not be read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_model_confirming_the_stored_figures_is_a_document_error_with_no_suggestion()
    {
        var seeded = await SeedAsync(BalanceCorrectionFixtures.FinexBalanceJson());
        _world.Claude.Suggestion = _ => BalanceSuggestionFixtures.Reply(BalanceSuggestionFixtures.FinexConfirmedAnswer());

        var response = (await SuggestAsync(seeded.ResultId)).Response!;

        Assert.Equal(BalanceSuggestionOutcomes.DocumentError, response.Outcome);
        Assert.Empty(response.Suggestions);
        Assert.StartsWith("El balance presentado no cierra:", response.Message);
    }

    [Fact]
    public async Task A_net_profit_only_failure_gets_a_derived_result_before_tax_and_a_reopen_is_served_from_the_cache()
    {
        var seeded = await SeedAsync(BalanceSuggestionFixtures.NetProfitBalanceJson());
        _world.Claude.Suggestion = _ => BalanceSuggestionFixtures.Reply(
            BalanceSuggestionFixtures.NetProfitAnswer(resultBeforeTax: null), BalanceSuggestionFixtures.NetProfitImages());

        var response = (await SuggestAsync(seeded.ResultId)).Response!;

        Assert.Equal(BalanceSuggestionOutcomes.Fixes, response.Outcome);
        Assert.Equal(BalanceSuggestionVerifier.DerivedFixesMessage, response.Message);
        Assert.Equal([BalanceIntegrityService.NetProfitReconciliationCheck], response.FailedChecksBefore);
        Assert.Empty(response.FailedChecksAfter);
        var item = Assert.Single(response.Suggestions);
        Assert.Equal("result_before_tax", item.Path);
        Assert.Equal(BalanceSuggestionBases.Derived, item.Basis);
        Assert.Equal(BalanceSuggestionFixtures.DerivedResultBeforeTax, item.SuggestedValue, 2);
        Assert.NotNull(item.Derivation);
        Assert.NotNull(item.Evidence);
        Assert.NotNull(item.EvidenceSecondary);
        Assert.NotNull(item.Highlight);   // its page is in pages[], so it is kept
        Assert.Equal([1, 2], response.Pages.Select(p => p.Page));

        // The question asked the three income-statement figures, with the note on the missing line.
        var (_, question) = Assert.Single(_world.Claude.SuggestionCalls);
        Assert.True(question.AsksNetProfit);
        Assert.Equal(["net_profit", "income_tax", "result_before_tax"], question.Fields.Select(f => f.Path));

        // A re-open over the same figures: the same derived item, no second call, no second cost row.
        var again = (await SuggestAsync(seeded.ResultId)).Response!;
        Assert.NotNull(again.CachedAt);
        Assert.Single(_world.Claude.SuggestionCalls);
        Assert.Single(_calls.Rows);
        Assert.Equal(item, Assert.Single(again.Suggestions));

        // The derivation and the figures reach the Portal, never a log.
        foreach (var message in _logger.Messages)
        {
            Assert.DoesNotContain("Calculado", message);
            Assert.DoesNotContain("11861910", message);
            Assert.DoesNotContain("11.861.910", message);
            Assert.DoesNotContain("14192078", message);
            Assert.DoesNotContain("2330167", message);
            Assert.DoesNotContain(item.EvidenceSecondary!.Data[..40], message);
        }
    }

    // ── Evidence: the full page and the highlight ────────────────────────────────

    [Fact]
    public async Task The_response_carries_the_full_page_once_and_a_highlight_per_located_suggestion()
    {
        var seeded = await SeedAsync(BalanceSuggestionFixtures.SidewaysBalanceJson());
        _world.Claude.Suggestion = _ => BalanceSuggestionFixtures.Reply(
            BalanceSuggestionFixtures.SidewaysFixAnswer(), BalanceSuggestionFixtures.SidewaysImages());

        var response = (await SuggestAsync(seeded.ResultId)).Response!;

        // The total was read on the right half of page 2; the extra rubro line has no location.
        var page = Assert.Single(response.Pages);
        Assert.Equal(2, page.Page);
        Assert.Equal(BalanceSuggestionEvidence.MediaType, page.Image.MediaType);
        Assert.True(Convert.FromBase64String(page.Image.Data).Length <= BalanceSuggestionEvidence.PageMaxBytes);

        var highlight = response.Suggestions[0].Highlight!;
        Assert.Equal(2, highlight.Page);
        Assert.Equal(0.40, highlight.Top);
        Assert.Equal(0.44, highlight.Bottom);
        Assert.Equal(616d / 1400, highlight.Left, 6);   // the right half starts 6% before the midline
        Assert.Equal(1d, highlight.Right);
        Assert.Null(response.Suggestions[1].Highlight);
        Assert.Null(response.CachedAt);
    }

    // ── Cache ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_second_ask_over_the_same_figures_is_served_from_the_cache_at_no_cost()
    {
        var seeded = await SeedAsync(BalanceCorrectionFixtures.FinexBalanceJson());
        ScriptFinexFix();

        var first = (await SuggestAsync(seeded.ResultId)).Response!;
        _clock.Advance(TimeSpan.FromMinutes(5));
        var second = await SuggestAsync(seeded.ResultId);

        Assert.True(second.IsSuccess);
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 15, 0, 0, TimeSpan.Zero), second.Response!.CachedAt);
        Assert.Equal(first with { CachedAt = second.Response.CachedAt }, second.Response);
        Assert.Single(_world.Claude.SuggestionCalls);
        Assert.Single(_calls.Rows);
        Assert.Single(_storage.Reads);
    }

    [Fact]
    public async Task Refresh_asks_again_and_replaces_the_cached_answer()
    {
        var seeded = await SeedAsync(BalanceCorrectionFixtures.FinexBalanceJson());
        ScriptFinexFix();
        await SuggestAsync(seeded.ResultId);

        _clock.Advance(TimeSpan.FromMinutes(10));
        var refreshed = await SuggestAsync(seeded.ResultId, refresh: true);

        Assert.Null(refreshed.Response!.CachedAt);
        Assert.Equal(2, _world.Claude.SuggestionCalls.Count);
        Assert.Equal(2, _calls.Rows.Count);

        var next = await SuggestAsync(seeded.ResultId);
        Assert.Equal(_clock.Now, next.Response!.CachedAt);   // the refreshed entry, not the first one
        Assert.Equal(2, _world.Claude.SuggestionCalls.Count);
    }

    [Fact]
    public async Task Changed_figures_are_a_cache_miss()
    {
        var seeded = await SeedAsync(BalanceCorrectionFixtures.FinexBalanceJson());
        ScriptFinexFix();
        await SuggestAsync(seeded.ResultId);

        await UpdateAsync(seeded, result: r => r.BalanceJson = BalanceCorrectionFixtures.FinexBalanceJson() + " ");
        var after = await SuggestAsync(seeded.ResultId);

        Assert.Null(after.Response!.CachedAt);
        Assert.Equal(2, _world.Claude.SuggestionCalls.Count);
    }

    [Fact]
    public async Task A_model_failure_is_not_cached()
    {
        var seeded = await SeedAsync(BalanceCorrectionFixtures.FinexBalanceJson());
        _world.Claude.Suggestion = _ => new BalanceSuggestionReply
        {
            Model = BalanceSuggestionFixtures.Model, RequestSent = true, HttpStatus = 529, DurationMs = 10, FailureCode = "http_529",
        };
        Assert.Equal(502, (await SuggestAsync(seeded.ResultId)).StatusCode);

        ScriptFinexFix();
        var retried = await SuggestAsync(seeded.ResultId);
        var cached  = await SuggestAsync(seeded.ResultId);

        Assert.Null(retried.Response!.CachedAt);
        Assert.NotNull(cached.Response!.CachedAt);
        Assert.Equal(2, _world.Claude.SuggestionCalls.Count);
    }

    [Fact]
    public async Task A_refusal_still_wins_over_a_cached_answer()
    {
        var seeded = await SeedAsync(BalanceCorrectionFixtures.FinexBalanceJson());
        ScriptFinexFix();
        await SuggestAsync(seeded.ResultId);

        await UpdateAsync(seeded, analysis: a => a.Status = AnalysisStatuses.Approved);
        var outcome = await SuggestAsync(seeded.ResultId);

        Assert.Equal(409, outcome.StatusCode);
        Assert.Equal(RescoreErrorCodes.NotPending, outcome.ErrorCode);
        Assert.Single(_world.Claude.SuggestionCalls);
    }

    [Fact]
    public async Task Another_tenant_never_reads_a_cached_answer()
    {
        var seeded = await SeedAsync(BalanceCorrectionFixtures.FinexBalanceJson());
        ScriptFinexFix();
        await SuggestAsync(seeded.ResultId);

        var outcome = await Service().SuggestAsync(
            seeded.ResultId, RescoreTestWorld.OtherTenant, _world.UserId, refresh: false, CancellationToken.None);

        Assert.Equal(404, outcome.StatusCode);
    }

    // ── Single-flight: concurrent openings over the same figures ─────────────────

    [Fact]
    public async Task Two_concurrent_openings_share_one_model_call()
    {
        var seeded = await SeedAsync(BalanceCorrectionFixtures.FinexBalanceJson());
        var gate   = Gate(FinexFixReply());

        var first  = SuggestAsync(seeded.ResultId);
        await gate.Started.Task.WaitAsync(Timeout);
        var second = SuggestAsync(seeded.ResultId);
        await WaitForParticipantsAsync(seeded, 2);

        gate.Release.SetResult();
        var (a, b) = (await first, await second);

        Assert.True(a.IsSuccess);
        Assert.True(b.IsSuccess);
        Assert.Equal(a.Response, b.Response);
        Assert.Null(b.Response!.CachedAt);
        Assert.Single(_world.Claude.SuggestionCalls);
        Assert.Single(_calls.Rows);
        Assert.Single(_storage.Reads);
        Assert.Equal(0, _inFlight.Count);

        // The shared 200 was cached once: a later opening is a hit, still no second call.
        Assert.NotNull((await SuggestAsync(seeded.ResultId)).Response!.CachedAt);
        Assert.Single(_world.Claude.SuggestionCalls);
    }

    [Fact]
    public async Task A_shared_failure_is_502_for_both_and_the_next_opening_asks_again()
    {
        var seeded = await SeedAsync(BalanceCorrectionFixtures.FinexBalanceJson());
        var gate   = Gate(new BalanceSuggestionReply
        {
            Model = BalanceSuggestionFixtures.Model, RequestSent = true, HttpStatus = 529, DurationMs = 10, FailureCode = "http_529",
        });

        var first  = SuggestAsync(seeded.ResultId);
        await gate.Started.Task.WaitAsync(Timeout);
        var second = SuggestAsync(seeded.ResultId);
        await WaitForParticipantsAsync(seeded, 2);
        gate.Release.SetResult();

        Assert.Equal(502, (await first).StatusCode);
        Assert.Equal(502, (await second).StatusCode);
        Assert.Single(_world.Claude.SuggestionCalls);
        Assert.Equal(0, _inFlight.Count);

        _world.Claude.SuggestionAsync = null;
        ScriptFinexFix();
        var retried = await SuggestAsync(seeded.ResultId);

        Assert.True(retried.IsSuccess);
        Assert.Null(retried.Response!.CachedAt);
        Assert.Equal(2, _world.Claude.SuggestionCalls.Count);
    }

    [Fact]
    public async Task The_first_caller_leaving_does_not_cancel_the_call_for_the_second()
    {
        var seeded = await SeedAsync(BalanceCorrectionFixtures.FinexBalanceJson());
        var gate   = Gate(FinexFixReply());
        using var firstCts = new CancellationTokenSource();

        var first  = SuggestAsync(seeded.ResultId, ct: firstCts.Token);
        await gate.Started.Task.WaitAsync(Timeout);
        var second = SuggestAsync(seeded.ResultId);
        await WaitForParticipantsAsync(seeded, 2);

        firstCts.Cancel();
        await WaitForParticipantsAsync(seeded, 1);
        Assert.False(gate.Token.IsCancellationRequested);

        gate.Release.SetResult();
        var served = await second;

        Assert.True(served.IsSuccess);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Single(_world.Claude.SuggestionCalls);
        Assert.Single(_calls.Rows);
        Assert.Equal(0, _inFlight.Count);
    }

    [Fact]
    public async Task Every_caller_leaving_cancels_the_call_and_caches_nothing()
    {
        var seeded = await SeedAsync(BalanceCorrectionFixtures.FinexBalanceJson());
        var gate   = Gate(FinexFixReply());
        using var firstCts  = new CancellationTokenSource();
        using var secondCts = new CancellationTokenSource();

        var first  = SuggestAsync(seeded.ResultId, ct: firstCts.Token);
        await gate.Started.Task.WaitAsync(Timeout);
        var second = SuggestAsync(seeded.ResultId, ct: secondCts.Token);
        await WaitForParticipantsAsync(seeded, 2);

        secondCts.Cancel();
        await WaitForParticipantsAsync(seeded, 1);
        Assert.False(gate.Token.IsCancellationRequested);   // the first is still waiting

        firstCts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.True(gate.Token.IsCancellationRequested);
        Assert.Empty(_calls.Rows);
        Assert.Equal(0, _inFlight.Count);
        Assert.False(_cache.TryGetValue(
            BalanceSuggestionCache.Key(RescoreTestWorld.Tenant, seeded.ResultId, seeded.BalanceJson!), out _));
    }

    // ── Privacy ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_question_carries_no_stored_label_and_the_logs_no_figure_label_or_image()
    {
        var seeded = await SeedAsync(BalanceSuggestionFixtures.SidewaysBalanceJson());
        _world.Claude.Suggestion = _ => BalanceSuggestionFixtures.Reply(
            BalanceSuggestionFixtures.SidewaysFixAnswer(), BalanceSuggestionFixtures.SidewaysImages());

        var response = (await SuggestAsync(seeded.ResultId)).Response!;
        Assert.Equal(BalanceSuggestionOutcomes.Fixes, response.Outcome);

        // The question the model gets: no stored label, no entity name or CUIT — it has nowhere to carry one.
        var (_, question) = Assert.Single(_world.Claude.SuggestionCalls);
        var text = ClaudeService.SuggestionText(question, new ClaudeService.RenderedPdf([], 2, 2, [new(100, 100, 0), new(100, 100, 0)]));
        Assert.DoesNotContain(BalanceSuggestionFixtures.PlantedLabel, text);
        Assert.DoesNotContain("Juan", text);
        Assert.DoesNotContain(RescoreTestWorld.Cuit, text);
        Assert.DoesNotContain("Empresa de Prueba", text);
        Assert.DoesNotContain("Deudas comerciales", text);   // a stored label, even a harmless one

        // Logs: counts, outcome and timings — never a figure, a label or an image.
        Assert.NotEmpty(_logger.Messages);
        foreach (var message in _logger.Messages)
        {
            Assert.DoesNotContain("150000", message);
            Assert.DoesNotContain("200000", message);
            Assert.DoesNotContain("40000", message);
            Assert.DoesNotContain("Total Pasivo", message);
            Assert.DoesNotContain("Deudas", message);
            Assert.DoesNotContain(BalanceSuggestionFixtures.PlantedLabel, message);
            Assert.DoesNotContain(response.Suggestions[0].Evidence!.Data[..40], message);
            Assert.DoesNotContain(response.Pages[0].Image.Data[..40], message);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private BalanceCorrectionSuggestionService Service()
    {
        var db = _world.NewContext();
        return new BalanceCorrectionSuggestionService(
            new AnalysisResultRepository(db, NullLogger<AnalysisResultRepository>.Instance),
            new AnalysisRepository(db, NullLogger<AnalysisRepository>.Instance),
            _world.Documents,
            new AnalysisRescoreRepository(db, NullLogger<AnalysisRescoreRepository>.Instance),
            _storage,
            _world.Claude,
            _calls,
            new BalanceIntegrityService(),
            _cache,
            _inFlight,
            _clock,
            _logger);
    }

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // Each call builds its own service over its own context, as each request scope does.
    private Task<BalanceCorrectionSuggestionOutcome> SuggestAsync(
        Guid resultId, bool refresh = false, CancellationToken ct = default)
        => Task.Run(() => Service().SuggestAsync(resultId, RescoreTestWorld.Tenant, _world.UserId, refresh, ct));

    private static BalanceSuggestionReply FinexFixReply() =>
        BalanceSuggestionFixtures.Reply(
            BalanceSuggestionFixtures.FinexFixAnswer(), [BalanceSuggestionFixtures.Image(1, 2, RenderedImage.FullHalf)]);

    // The model call held open until Release: Started fires when it is entered, Token is the token it got.
    private SuggestionGate Gate(BalanceSuggestionReply reply)
    {
        var gate = new SuggestionGate();
        _world.Claude.SuggestionAsync = async (_, ct) =>
        {
            gate.Token = ct;
            gate.Started.TrySetResult();
            await gate.Release.Task.WaitAsync(ct);
            return reply;
        };
        return gate;
    }

    private async Task WaitForParticipantsAsync(AnalysisResult seeded, int expected)
    {
        var key   = BalanceSuggestionCache.Key(RescoreTestWorld.Tenant, seeded.ResultId, seeded.BalanceJson!);
        var until = DateTime.UtcNow + Timeout;
        while (_inFlight.ParticipantsOf(key) != expected)
        {
            Assert.True(DateTime.UtcNow < until, $"expected {expected} participant(s), saw {_inFlight.ParticipantsOf(key)}");
            await Task.Delay(5);
        }
    }

    private sealed class SuggestionGate
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken Token { get; set; }
    }

    private async Task UpdateAsync(AnalysisResult seeded, Action<AnalysisResult>? result = null, Action<Analysis>? analysis = null)
    {
        await using var db = _world.NewContext();
        if (result is not null)
            result(await db.AnalysisResults.FirstAsync(r => r.ResultId == seeded.ResultId));
        if (analysis is not null)
            analysis(await db.Analyses.FirstAsync(a => a.AnalysisId == seeded.AnalysisId));
        db.ChangeTracker.DetectChanges();
        await db.SaveChangesAsync();
    }

    private void ScriptFinexFix() =>
        _world.Claude.Suggestion = _ => FinexFixReply();

    private async Task<AnalysisResult> SeedAsync(
        string balanceJson, Action<Analysis>? analysis = null, Action<Document>? document = null)
    {
        var entity = await _world.SeedEntityAsync();
        var (_, seeded, doc) = await _world.SeedAnalysisAsync(entity, analysis, r => r.BalanceJson = balanceJson);
        document?.Invoke(doc);
        return seeded;
    }

    private async Task AssertRefusedAsync(AnalysisResult seeded, int status, string errorCode)
    {
        var before  = RescoreTestWorld.Fingerprint((await _world.ReadResultAsync(seeded.ResultId))!);
        var outcome = await SuggestAsync(seeded.ResultId);

        Assert.Equal(status, outcome.StatusCode);
        Assert.Equal(errorCode, outcome.ErrorCode);
        Assert.Null(outcome.Response);
        Assert.Empty(_world.Claude.SuggestionCalls);
        Assert.Empty(_calls.Rows);
        Assert.Equal(before, RescoreTestWorld.Fingerprint((await _world.ReadResultAsync(seeded.ResultId))!));
    }

    private async Task<List<AnalysisEdit>> ReadEditsAsync()
    {
        await using var db = _world.NewContext();
        return await db.AnalysisEdits.AsNoTracking().ToListAsync();
    }

    private sealed record CallRow(
        string? Operation, string? Model, Guid? AnalysisId, Guid? UserId, int? ResponseStatus,
        int? InputTokens, int? OutputTokens, decimal? Cost, bool IsSuccess, string? ErrorMessage);

    private sealed class CapturingApiCallLog : IApiCallLogService
    {
        public List<CallRow> Rows { get; } = [];

        public Task LogAsync(string tenantId, string service, string endpoint, int? responseStatus,
            int? inputTokens, int? outputTokens, decimal? estimatedCostUsd, bool isSuccess, string? errorMessage,
            int durationMs, string? operation = null, string? model = null, Guid? userId = null,
            Guid? analysisId = null, long? chatMessageId = null, Guid? documentId = null, CancellationToken ct = default)
        {
            lock (Rows)
                Rows.Add(new CallRow(operation, model, analysisId, userId, responseStatus, inputTokens, outputTokens,
                    estimatedCostUsd, isSuccess, errorMessage));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingLogger : ILogger<BalanceCorrectionSuggestionService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Messages)
                Messages.Add(string.Create(CultureInfo.InvariantCulture, $"{formatter(state, exception)} {exception}"));
        }
    }
}
