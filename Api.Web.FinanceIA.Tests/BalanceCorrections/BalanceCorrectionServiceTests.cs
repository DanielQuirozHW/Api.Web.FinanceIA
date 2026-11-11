// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-02

using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Api.Web.FinanceIA.Servicios.BalanceCorrections;
using Api.Web.FinanceIA.Servicios.ScoreReliability;
using Api.Web.FinanceIA.Tests.AnalysisRuns;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Api.Web.FinanceIA.Tests.BalanceCorrections;

/// <summary>
/// POST /api/analyses/{resultId}/balance-corrections end to end over <see cref="RescoreTestWorld"/>: the
/// REAL service, repositories, ratio and scoring engines; the database read back. The FINEX shape is a
/// synthetic fixture (<see cref="BalanceCorrectionFixtures"/>).
/// </summary>
public sealed class BalanceCorrectionServiceTests
{
    private readonly RescoreTestWorld _world = new();

    // ── The FINEX shape ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Finex_correcting_the_misread_rubro_rescores_the_corrected_balance_in_one_write()
    {
        var entity = await _world.SeedEntityAsync();
        var (analysis, seeded, _) = await SeedFinexAsync(entity);
        var extractionCalls = _world.Claude.AnalyzeCalls;

        var outcome = await CorrectAsync(seeded.ResultId, BalanceCorrectionFixtures.FinexCommand());

        Assert.True(outcome.IsSuccess);
        Assert.Equal(extractionCalls, _world.Claude.AnalyzeCalls);   // never a new extraction

        var corrected = (await _world.ReadResultAsync(seeded.ResultId))!;
        var balance   = JsonNode.Parse(corrected.BalanceJson!)!.AsObject();

        // The corrected figure, and the check it failed now passes.
        Assert.Equal(0d, balance["current_assets_items"]![1]!["amount"]!.GetValue<double>());
        var integrity = ExtractionIntegrityReader.Read(corrected.BalanceJson, analysis.FiscalYear)!;
        Assert.True(integrity.Passed);
        Assert.True(integrity.Verified);
        Assert.Empty(integrity.FailedChecks);
        Assert.True(balance["integrity"]!["manuallyCorrected"]!.GetValue<bool>());

        // The trail keeps the original reading.
        var entry = Assert.Single(balance["manual_corrections"]!.AsArray())!;
        Assert.Equal(BalanceCorrectionFixtures.MisreadPath, entry["path"]!.GetValue<string>());
        Assert.Equal(BalanceCorrectionFixtures.MisreadAmount, entry["original"]!.GetValue<double>());
        Assert.Equal(_world.UserId.ToString(), entry["by"]!.GetValue<string>());

        // One AnalysisEdits row, with the invariant-culture values and the reason.
        var edit = Assert.Single(await ReadEditsAsync());
        Assert.Equal(analysis.AnalysisId, edit.AnalysisId);
        Assert.Equal(RescoreTestWorld.Tenant, edit.TenantId);
        Assert.Equal(BalanceCorrectionFixtures.MisreadPath, edit.FieldPath);
        Assert.Equal("119378233.22", edit.OldValue);
        Assert.Equal("0", edit.NewValue);
        Assert.Equal(BalanceCorrectionFixtures.Reason, edit.EditReason);
        Assert.Equal(_world.UserId, edit.EditedBy);
        Assert.Equal(_world.Clock.Now.UtcDateTime, edit.EditedAt);

        // The re-score landed with it: one history row marked as a correction, carrying the old blob.
        Assert.Equal(1, corrected.RescoreCount);
        Assert.Equal(_world.Clock.Now.UtcDateTime, corrected.RescoredAt);
        var history  = Assert.Single(await _world.ReadHistoryAsync());
        var previous = JsonDocument.Parse(history.PreviousValuesJson).RootElement;
        Assert.Equal(RescorePreviousValues.BalanceCorrectionTrigger, previous.GetProperty("trigger").GetString());
        Assert.Equal(seeded.BalanceJson, previous.GetProperty("balanceJson").GetString());

        // The Anomalies column is the original reading, untouched.
        Assert.Equal(seeded.Anomalies, corrected.Anomalies);

        // The reliability: manual_correction, and no integrity reason.
        var codes = ReliabilityCodes(corrected);
        Assert.Contains(ScoreReliabilityCodes.ManualCorrection, codes);
        Assert.DoesNotContain(ScoreReliabilityCodes.IntegrityFailed, codes);
        Assert.DoesNotContain(ScoreReliabilityCodes.IntegrityUnverified, codes);

        // The narrative was told the same, and its prompt block carries no name, reason or user id.
        var narrativeReliability = _world.Claude.NarrativeReliabilities.Last();
        Assert.Contains(narrativeReliability!.Reasons, r => r.Code == ScoreReliabilityCodes.ManualCorrection);
        var block = ScoreReliabilityPromptBlock.Build(narrativeReliability)!;
        Assert.Contains("corrigió manualmente 1 cifra", block);
        Assert.DoesNotContain(BalanceCorrectionFixtures.Reason, block);
        Assert.DoesNotContain(_world.UserId.ToString(), block);
        Assert.DoesNotContain(entity.BusinessName, block);
        Assert.DoesNotContain(RescoreTestWorld.Cuit, block);
    }

    [Fact]
    public async Task Finex_before_the_correction_the_score_carries_the_integrity_reason()
    {
        var entity = await _world.SeedEntityAsync();
        var (_, seeded, _) = await SeedFinexAsync(entity);

        Assert.True((await _world.Service().RescoreAsync(
            seeded.ResultId, RescoreTestWorld.Tenant, _world.UserId, null, null, CancellationToken.None)).IsSuccess);

        var codes = ReliabilityCodes((await _world.ReadResultAsync(seeded.ResultId))!);
        Assert.Contains(ScoreReliabilityCodes.IntegrityFailed, codes);
        Assert.DoesNotContain(ScoreReliabilityCodes.ManualCorrection, codes);
    }

    // ── A misread result before tax (net_profit_reconciliation) ─────────────────

    /// <summary>
    /// Reliability policy 5: net_profit_reconciliation as the ONE failed check reads as a presentation of the
    /// income statement, not figures that do not add up — the presentation caveat, never integrity_failed.
    /// </summary>
    [Fact]
    public async Task A_net_profit_only_failure_scores_with_the_presentation_caveat()
    {
        var entity = await _world.SeedEntityAsync();
        var (_, seeded, _) = await SeedMisreadResultBeforeTaxAsync(entity);

        Assert.True((await _world.Service().RescoreAsync(
            seeded.ResultId, RescoreTestWorld.Tenant, _world.UserId, null, null, CancellationToken.None)).IsSuccess);

        var result    = (await _world.ReadResultAsync(seeded.ResultId))!;
        var integrity = ExtractionIntegrityReader.Read(result.BalanceJson, 2024)!;
        Assert.False(integrity.Passed);
        Assert.Equal([BalanceIntegrityService.NetProfitReconciliationCheck], integrity.FailedChecks);

        var codes = ReliabilityCodes(result);
        Assert.Contains(ScoreReliabilityCodes.IntegrityPresentation, codes);
        Assert.DoesNotContain(ScoreReliabilityCodes.IntegrityFailed, codes);
        Assert.DoesNotContain(ScoreReliabilityCodes.CriticalFloorUnverified, codes);
        Assert.DoesNotContain(ScoreReliabilityCodes.ManualCorrection, codes);
    }

    // ── Column mix (policy 6) ─────────────────────────────────────────────────────

    [Fact]
    public async Task A_recorded_column_mix_rescores_as_integrity_failed()
    {
        var entity = await _world.SeedEntityAsync();
        var (_, seeded, _) = await SeedMixedColumnsAsync(entity, recordVerdict: true);

        Assert.True((await _world.Service().RescoreAsync(
            seeded.ResultId, RescoreTestWorld.Tenant, _world.UserId, null, null, CancellationToken.None)).IsSuccess);

        var codes = ReliabilityCodes((await _world.ReadResultAsync(seeded.ResultId))!);
        Assert.Contains(ScoreReliabilityCodes.IntegrityFailed, codes);
        Assert.DoesNotContain(ScoreReliabilityCodes.IntegrityPresentation, codes);
    }

    [Fact]
    public async Task A_stored_blob_without_the_column_mix_verdict_rescores_exactly_as_before()
    {
        // Its prior_year WOULD reveal the mix; a re-score reads the stored report and never re-judges it.
        var entity = await _world.SeedEntityAsync();
        var (_, seeded, _) = await SeedMixedColumnsAsync(entity, recordVerdict: false);

        Assert.True((await _world.Service().RescoreAsync(
            seeded.ResultId, RescoreTestWorld.Tenant, _world.UserId, null, null, CancellationToken.None)).IsSuccess);

        var result = (await _world.ReadResultAsync(seeded.ResultId))!;
        Assert.Null(ExtractionIntegrityReader.Read(result.BalanceJson, 2024)!.NetProfitColumnMix);
        var codes = ReliabilityCodes(result);
        Assert.Contains(ScoreReliabilityCodes.IntegrityPresentation, codes);
        Assert.DoesNotContain(ScoreReliabilityCodes.IntegrityFailed, codes);
    }

    [Fact]
    public async Task A_correction_re_judges_the_figures_and_finds_the_column_mix_on_an_old_blob()
    {
        // A correction rebuilds integrity from the figures (as it does for the sign flip): an unrelated corrected
        // line, and the mix the old blob never recorded is now found — integrity_failed, ValidationStatus Warning.
        var entity = await _world.SeedEntityAsync();
        var (analysis, seeded, _) = await SeedMixedColumnsAsync(entity, recordVerdict: false);

        Assert.True((await CorrectAsync(seeded.ResultId,
            BalanceCorrectionFixtures.Command("financial_expenses", 21_000_000d))).IsSuccess);

        var corrected = (await _world.ReadResultAsync(seeded.ResultId))!;
        var integrity = ExtractionIntegrityReader.Read(corrected.BalanceJson, analysis.FiscalYear)!;
        Assert.True(integrity.NetProfitColumnMix);
        Assert.False(integrity.PresentationOnly);
        Assert.Equal("Warning", corrected.ValidationStatus);

        var codes = ReliabilityCodes(corrected);
        Assert.Contains(ScoreReliabilityCodes.IntegrityFailed, codes);
        Assert.DoesNotContain(ScoreReliabilityCodes.IntegrityPresentation, codes);
    }

    [Fact]
    public async Task A_correction_that_leaves_only_net_profit_off_sets_ValidationStatus_Passed_with_the_caveat()
    {
        var entity = await _world.SeedEntityAsync();
        var (analysis, seeded, _) = await SeedMisreadResultBeforeTaxAsync(entity, r => r.ValidationStatus = "Warning");

        // Still not the figure the statement prints: net_profit_reconciliation keeps failing, alone.
        Assert.True((await CorrectAsync(seeded.ResultId,
            BalanceCorrectionFixtures.Command("result_before_tax", 10_000_000d))).IsSuccess);

        var corrected = (await _world.ReadResultAsync(seeded.ResultId))!;
        var integrity = ExtractionIntegrityReader.Read(corrected.BalanceJson, analysis.FiscalYear)!;
        Assert.False(integrity.Passed);
        Assert.True(integrity.PresentationOnly);
        Assert.Equal("Passed", corrected.ValidationStatus);

        var codes = ReliabilityCodes(corrected);
        Assert.Contains(ScoreReliabilityCodes.IntegrityPresentation, codes);
        Assert.Contains(ScoreReliabilityCodes.ManualCorrection, codes);
        Assert.DoesNotContain(ScoreReliabilityCodes.IntegrityFailed, codes);
    }

    [Fact]
    public async Task Correcting_result_before_tax_clears_integrity_failed_and_shows_manual_correction()
    {
        var entity = await _world.SeedEntityAsync();
        var (analysis, seeded, _) = await SeedMisreadResultBeforeTaxAsync(entity);

        var outcome = await CorrectAsync(seeded.ResultId,
            BalanceCorrectionFixtures.Command("result_before_tax", BalanceCorrectionFixtures.ResultBeforeTax));

        Assert.True(outcome.IsSuccess);

        var corrected = (await _world.ReadResultAsync(seeded.ResultId))!;
        var balance   = JsonNode.Parse(corrected.BalanceJson!)!.AsObject();
        Assert.Equal(BalanceCorrectionFixtures.ResultBeforeTax, balance["result_before_tax"]!.GetValue<double>());

        // net_profit_reconciliation ran over the corrected figure and passed.
        var integrity = ExtractionIntegrityReader.Read(corrected.BalanceJson, analysis.FiscalYear)!;
        Assert.True(integrity.Passed);
        Assert.True(integrity.Verified);
        Assert.Empty(integrity.FailedChecks);
        Assert.Contains(balance["integrity"]!["checks"]!.AsArray(),
            c => c!["name"]!.GetValue<string>() == BalanceIntegrityService.NetProfitReconciliationCheck
                 && c["passed"]!.GetValue<bool>());

        // The verdict: manual_correction, and no integrity reason any more.
        var codes = ReliabilityCodes(corrected);
        Assert.Contains(ScoreReliabilityCodes.ManualCorrection, codes);
        Assert.DoesNotContain(ScoreReliabilityCodes.IntegrityFailed, codes);
        Assert.DoesNotContain(ScoreReliabilityCodes.IntegrityUnverified, codes);

        // One AnalysisEdits row for the figure, with the invariant-culture values.
        var edit = Assert.Single(await ReadEditsAsync());
        Assert.Equal("result_before_tax", edit.FieldPath);
        Assert.Equal("9000000", edit.OldValue);
        Assert.Equal("90000000", edit.NewValue);
    }

    [Fact]
    public async Task The_new_score_is_the_engines_score_of_the_corrected_balance()
    {
        // Correct a figure the ratios read (the pasivo corriente), then compare with a plain re-score of an
        // identical analysis seeded directly with those corrected figures: same engine, same inputs.
        var entity = await _world.SeedEntityAsync();
        var (_, seeded, _) = await SeedFinexAsync(entity);

        var outcome = await CorrectAsync(seeded.ResultId, BalanceCorrectionFixtures.Command("current_liabilities", 100_000_000d));
        Assert.True(outcome.IsSuccess);
        var corrected = (await _world.ReadResultAsync(seeded.ResultId))!;

        var reference = new RescoreTestWorld();
        var refEntity = await reference.SeedEntityAsync();
        var (_, refSeeded, _) = await reference.SeedAnalysisAsync(refEntity, configureResult: r => r.BalanceJson = corrected.BalanceJson);
        Assert.True((await reference.Service().RescoreAsync(
            refSeeded.ResultId, RescoreTestWorld.Tenant, reference.UserId, null, null, CancellationToken.None)).IsSuccess);
        var expected = (await reference.ReadResultAsync(refSeeded.ResultId))!;

        Assert.Equal(expected.AiSuggestedScore, corrected.AiSuggestedScore);
        Assert.Equal(expected.AiSuggestedLevel, corrected.AiSuggestedLevel);
        Assert.Equal(expected.RatiosJson, corrected.RatiosJson);
        Assert.Equal(outcome.Summary!.NewScore, corrected.AiSuggestedScore);

        // 200M / 100M: the liquidity the corrected figure implies, not the reading's 200M / 150M.
        var ratios = JsonDocument.Parse(corrected.RatiosJson!).RootElement;
        Assert.Equal(2d, ratios.GetProperty("current_ratio").GetDouble(), 6);
    }

    [Fact]
    public async Task A_second_correction_of_the_same_path_keeps_the_first_original()
    {
        var entity = await _world.SeedEntityAsync();
        var (_, seeded, _) = await SeedFinexAsync(entity);

        Assert.True((await CorrectAsync(seeded.ResultId, BalanceCorrectionFixtures.FinexCommand(value: 5d))).IsSuccess);
        _world.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.True((await CorrectAsync(seeded.ResultId, BalanceCorrectionFixtures.FinexCommand(value: 0d))).IsSuccess);

        var trail = JsonNode.Parse((await _world.ReadResultAsync(seeded.ResultId))!.BalanceJson!)!["manual_corrections"]!.AsArray();
        Assert.Equal(2, trail.Count);
        Assert.All(trail, e => Assert.Equal(BalanceCorrectionFixtures.MisreadAmount, e!["original"]!.GetValue<double>()));

        var edits = await ReadEditsAsync();
        Assert.Equal(2, edits.Count);
        Assert.Equal(["119378233.22", "5"], edits.OrderBy(e => e.EditedAt).Select(e => e.OldValue));
        Assert.Equal(2, (await _world.ReadHistoryAsync()).Count);
    }

    // ── Refusals: nothing written ───────────────────────────────────────────────

    [Fact]
    public async Task A_persona_humana_analysis_is_409_person_analysis_not_correctable()
    {
        var entity = await _world.SeedEntityAsync();
        var (_, seeded, _) = await _world.SeedAnalysisAsync(entity,
            a => { a.AnalysisKind = AnalysisKinds.PersonaHumana; a.DocumentId = null; },
            r => r.BalanceJson = null);

        await AssertRefusedAsync(seeded, 409, RescoreErrorCodes.PersonAnalysisNotCorrectable);
        Assert.Empty(_world.PersonRescore.Calls);
    }

    public static TheoryData<string> EligibilityRefusals() =>
    [
        RescoreErrorCodes.NotPending,
        RescoreErrorCodes.HasVotes + ":vote",
        RescoreErrorCodes.HasVotes + ":preapproved",
        RescoreErrorCodes.HasVotes + ":reviewed",
        RescoreErrorCodes.Processing,
        RescoreErrorCodes.ResultMissing,
    ];

    [Theory]
    [MemberData(nameof(EligibilityRefusals))]
    public async Task Every_rescore_eligibility_refusal_is_the_same_409(string refusal)
    {
        var entity = await _world.SeedEntityAsync();
        var (analysis, seeded, _) = await _world.SeedAnalysisAsync(entity,
            a =>
            {
                if (refusal == RescoreErrorCodes.NotPending)          a.Status        = AnalysisStatuses.Approved;
                if (refusal.EndsWith(":preapproved", StringComparison.Ordinal)) a.PreApprovedBy = Guid.NewGuid();
                if (refusal.EndsWith(":reviewed", StringComparison.Ordinal))    a.ReviewedBy    = Guid.NewGuid();
            },
            r => r.BalanceJson = refusal == RescoreErrorCodes.ResultMissing ? null : BalanceCorrectionFixtures.FinexBalanceJson(),
            documentStatus: refusal == RescoreErrorCodes.Processing ? "Processing" : "Completed");

        if (refusal.EndsWith(":vote", StringComparison.Ordinal))
            await _world.AddVoteAsync(analysis.AnalysisId);

        await AssertRefusedAsync(seeded, 409, refusal.Split(':')[0]);
    }

    [Fact]
    public async Task The_cooldown_is_the_rescores_429_and_writes_nothing()
    {
        var entity = await _world.SeedEntityAsync();
        var (_, seeded, _) = await SeedFinexAsync(entity, r => r.RescoredAt = _world.Clock.Now.UtcDateTime.AddSeconds(-10));

        var outcome = await AssertRefusedAsync(seeded, 429, RescoreErrorCodes.TooManyRequests);
        Assert.NotNull(outcome.RetryAfterSeconds);
    }

    [Fact]
    public async Task A_correction_starts_the_cooldown_for_a_rescore_too()
    {
        var entity = await _world.SeedEntityAsync();
        var (_, seeded, _) = await SeedFinexAsync(entity);
        Assert.True((await CorrectAsync(seeded.ResultId, BalanceCorrectionFixtures.FinexCommand())).IsSuccess);

        var rescore = await _world.Service().RescoreAsync(
            seeded.ResultId, RescoreTestWorld.Tenant, _world.UserId, null, null, CancellationToken.None);

        Assert.Equal(429, rescore.StatusCode);
    }

    [Fact]
    public async Task Bcra_still_unavailable_is_503_and_writes_nothing()
    {
        var entity = await _world.SeedEntityAsync();
        var (_, seeded, _) = await SeedFinexAsync(entity);
        _world.Bcra.Throws = new HttpRequestException("BCRA down");

        var before  = RescoreTestWorld.Fingerprint((await _world.ReadResultAsync(seeded.ResultId))!);
        var outcome = await CorrectAsync(seeded.ResultId, BalanceCorrectionFixtures.FinexCommand());

        Assert.Equal(503, outcome.StatusCode);
        Assert.Equal(RescoreErrorCodes.BcraUnavailable, outcome.ErrorCode);
        Assert.NotNull(outcome.RetryAfterSeconds);
        Assert.Equal(before, RescoreTestWorld.Fingerprint((await _world.ReadResultAsync(seeded.ResultId))!));
        Assert.Empty(await _world.ReadHistoryAsync());
        Assert.Empty(await ReadEditsAsync());
        Assert.Equal(0, _world.Claude.NarrativeCalls);
    }

    [Fact]
    public async Task A_line_that_does_not_exist_or_an_unchanged_value_is_400_before_anything_is_spent()
    {
        var entity = await _world.SeedEntityAsync();
        var (_, seeded, _) = await SeedFinexAsync(entity);

        var missing = await AssertRefusedAsync(
            seeded, 400, RescoreErrorCodes.ValidationError, BalanceCorrectionFixtures.Command("current_assets_items[7].amount", 1d));
        Assert.Equal("corrections[0].path", Assert.Single(missing.ValidationErrors!).Field);

        var unchanged = await AssertRefusedAsync(
            seeded, 400, RescoreErrorCodes.ValidationError, BalanceCorrectionFixtures.Command("equity", 300_000_000d));
        Assert.Contains("sin cambios", Assert.Single(unchanged.ValidationErrors!).Message);
    }

    [Fact]
    public async Task An_unreadable_stored_balance_is_409_analysis_result_missing()
    {
        var entity = await _world.SeedEntityAsync();
        var (_, seeded, _) = await _world.SeedAnalysisAsync(entity, configureResult: r => r.BalanceJson = "[1,2,3]");

        await AssertRefusedAsync(seeded, 409, RescoreErrorCodes.ResultMissing, bcraCalled: false);
    }

    [Fact]
    public async Task Another_tenants_result_is_not_found()
    {
        var entity = await _world.SeedEntityAsync(tenantId: RescoreTestWorld.OtherTenant);
        var (_, seeded, _) = await SeedFinexAsync(entity);

        var outcome = await CorrectAsync(seeded.ResultId, BalanceCorrectionFixtures.FinexCommand());

        Assert.Equal(404, outcome.StatusCode);
        Assert.Empty(await ReadEditsAsync());
    }

    // ── Atomicity ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_decision_landing_during_the_correction_rolls_it_all_back(bool viaVote)
    {
        var entity = await _world.SeedEntityAsync();
        var (analysis, seeded, _) = await SeedFinexAsync(entity);
        var before = RescoreTestWorld.Fingerprint((await _world.ReadResultAsync(seeded.ResultId))!);

        var db     = _world.NewContext();
        var racing = new InterceptingRescoreRepository(
            new AnalysisRescoreRepository(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<AnalysisRescoreRepository>.Instance),
            async _ =>
            {
                if (viaVote)
                {
                    await _world.AddVoteAsync(analysis.AnalysisId);
                    return;
                }

                await using var other = _world.NewContext();
                var row = await other.Analyses.SingleAsync(a => a.AnalysisId == analysis.AnalysisId);
                row.Status = AnalysisStatuses.Approved;
                other.ChangeTracker.DetectChanges();
                await other.SaveChangesAsync();
            });

        var outcome = await _world.Service(racing, db: db).CorrectBalanceAsync(
            seeded.ResultId, RescoreTestWorld.Tenant, _world.UserId, BalanceCorrectionFixtures.FinexCommand(),
            null, null, CancellationToken.None);

        Assert.Equal(409, outcome.StatusCode);
        Assert.Equal(before, RescoreTestWorld.Fingerprint((await _world.ReadResultAsync(seeded.ResultId))!));
        Assert.Empty(await _world.ReadHistoryAsync());
        Assert.Empty(await ReadEditsAsync());
    }

    [Fact]
    public async Task A_failing_write_leaves_the_balance_the_score_and_the_audit_rows_untouched()
    {
        var entity = await _world.SeedEntityAsync();
        var (_, seeded, _) = await SeedFinexAsync(entity);
        var before = RescoreTestWorld.Fingerprint((await _world.ReadResultAsync(seeded.ResultId))!);

        var db      = _world.NewContext();
        var failing = new InterceptingRescoreRepository(
            new AnalysisRescoreRepository(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<AnalysisRescoreRepository>.Instance),
            _ => throw new DbUpdateException("the write transaction failed"));

        await Assert.ThrowsAsync<DbUpdateException>(() => _world.Service(failing, db: db).CorrectBalanceAsync(
            seeded.ResultId, RescoreTestWorld.Tenant, _world.UserId, BalanceCorrectionFixtures.FinexCommand(),
            null, null, CancellationToken.None));

        Assert.Equal(before, RescoreTestWorld.Fingerprint((await _world.ReadResultAsync(seeded.ResultId))!));
        Assert.Empty(await _world.ReadHistoryAsync());
        Assert.Empty(await ReadEditsAsync());
        Assert.Empty(_world.Audit.Entries);
    }

    [Fact]
    public async Task The_corrected_balance_the_rescore_and_the_edits_travel_in_ONE_write()
    {
        var entity = await _world.SeedEntityAsync();
        var (_, seeded, _) = await SeedFinexAsync(entity);

        var db       = _world.NewContext();
        var captured = new List<AnalysisRescoreWrite>();
        var capture  = new InterceptingRescoreRepository(
            new AnalysisRescoreRepository(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<AnalysisRescoreRepository>.Instance),
            write => { captured.Add(write); return Task.CompletedTask; });

        Assert.True((await _world.Service(capture, db: db).CorrectBalanceAsync(
            seeded.ResultId, RescoreTestWorld.Tenant, _world.UserId, BalanceCorrectionFixtures.FinexCommand(),
            null, null, CancellationToken.None)).IsSuccess);

        var write = Assert.Single(captured);
        Assert.NotNull(write.Correction);
        Assert.Single(write.Correction!.Edits);
        Assert.Contains("manual_corrections", write.Correction.BalanceJson);
        Assert.Equal(write.ScoreBreakdownJson, (await _world.ReadResultAsync(seeded.ResultId))!.ScoreBreakdownJson);
        Assert.Contains(RescorePreviousValues.BalanceCorrectionTrigger, write.History.PreviousValuesJson);
    }

    [Fact]
    public async Task A_plain_rescore_never_writes_the_balance_and_its_history_row_has_no_trigger()
    {
        var entity = await _world.SeedEntityAsync();
        var (_, seeded, _) = await SeedFinexAsync(entity);

        Assert.True((await _world.Service().RescoreAsync(
            seeded.ResultId, RescoreTestWorld.Tenant, _world.UserId, null, null, CancellationToken.None)).IsSuccess);

        Assert.Equal(seeded.BalanceJson, (await _world.ReadResultAsync(seeded.ResultId))!.BalanceJson);
        var previous = JsonDocument.Parse(Assert.Single(await _world.ReadHistoryAsync()).PreviousValuesJson).RootElement;
        Assert.False(previous.TryGetProperty("trigger", out _));
        Assert.False(previous.TryGetProperty("balanceJson", out _));
        Assert.Empty(await ReadEditsAsync());
    }

    // ── Audit ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task One_balance_corrected_audit_row_with_paths_and_counts_only()
    {
        var entity = await _world.SeedEntityAsync();
        var (analysis, seeded, _) = await SeedFinexAsync(entity);

        Assert.True((await CorrectAsync(seeded.ResultId, BalanceCorrectionFixtures.FinexCommand())).IsSuccess);

        var audit = Assert.Single(_world.Audit.Entries);
        Assert.Equal("Analysis.BalanceCorrected", audit.Action);
        Assert.Equal(analysis.AnalysisId.ToString(), audit.EntityId);
        Assert.Contains(BalanceCorrectionFixtures.MisreadPath, audit.NewValueJson);
        Assert.DoesNotContain("119378233", audit.NewValueJson);
        Assert.DoesNotContain(BalanceCorrectionFixtures.Reason, audit.NewValueJson);
    }

    [Fact]
    public async Task The_history_marks_the_correction_rescore()
    {
        var entity = await _world.SeedEntityAsync();
        var (_, seeded, _) = await SeedFinexAsync(entity);
        Assert.True((await CorrectAsync(seeded.ResultId, BalanceCorrectionFixtures.FinexCommand())).IsSuccess);

        var history = await _world.Service().GetHistoryAsync(seeded.ResultId, RescoreTestWorld.Tenant, CancellationToken.None);

        Assert.Equal(RescorePreviousValues.BalanceCorrectionTrigger, Assert.Single(history!).Trigger);
    }

    // ── ValidationStatus follows the corrected integrity (review-gate-calm) ─────

    [Fact]
    public async Task A_correction_that_makes_integrity_pass_sets_ValidationStatus_Passed_and_keeps_the_old_one_in_history()
    {
        var entity = await _world.SeedEntityAsync();
        var (analysis, seeded, _) = await SeedFinexAsync(entity, r => r.ValidationStatus = "Warning");

        Assert.True((await CorrectAsync(seeded.ResultId, BalanceCorrectionFixtures.FinexCommand())).IsSuccess);

        var corrected = (await _world.ReadResultAsync(seeded.ResultId))!;
        Assert.True(ExtractionIntegrityReader.Read(corrected.BalanceJson, analysis.FiscalYear)!.Passed);
        Assert.Equal("Passed", corrected.ValidationStatus);

        var previous = JsonDocument.Parse(Assert.Single(await _world.ReadHistoryAsync()).PreviousValuesJson).RootElement;
        Assert.Equal("Warning", previous.GetProperty("validationStatus").GetString());
    }

    [Fact]
    public async Task A_correction_that_leaves_integrity_failing_keeps_ValidationStatus_Warning()
    {
        var entity = await _world.SeedEntityAsync();
        var (analysis, seeded, _) = await SeedFinexAsync(entity, r => r.ValidationStatus = "Warning");

        // Still not the zero the balance prints: the rubro keeps failing current_assets_items.
        Assert.True((await CorrectAsync(seeded.ResultId, BalanceCorrectionFixtures.FinexCommand(50_000_000d))).IsSuccess);

        var corrected = (await _world.ReadResultAsync(seeded.ResultId))!;
        Assert.False(ExtractionIntegrityReader.Read(corrected.BalanceJson, analysis.FiscalYear)!.Passed);
        Assert.Equal("Warning", corrected.ValidationStatus);
    }

    [Fact]
    public async Task A_correction_that_breaks_integrity_turns_a_Passed_into_Warning()
    {
        var entity = await _world.SeedEntityAsync();
        var (analysis, seeded, _) = await _world.SeedAnalysisAsync(entity, configureResult: r =>
        {
            r.BalanceJson      = BalanceCorrectionFixtures.CorrectedFigures();
            r.ValidationStatus = "Passed";
        });

        Assert.True((await CorrectAsync(seeded.ResultId,
            BalanceCorrectionFixtures.FinexCommand(BalanceCorrectionFixtures.MisreadAmount))).IsSuccess);

        var corrected = (await _world.ReadResultAsync(seeded.ResultId))!;
        Assert.False(ExtractionIntegrityReader.Read(corrected.BalanceJson, analysis.FiscalYear)!.Passed);
        Assert.Equal("Warning", corrected.ValidationStatus);
    }

    [Fact]
    public async Task A_plain_rescore_never_touches_ValidationStatus()
    {
        var entity = await _world.SeedEntityAsync();
        var (_, seeded, _) = await SeedFinexAsync(entity, r => r.ValidationStatus = "Warning");

        Assert.True((await _world.Service().RescoreAsync(
            seeded.ResultId, RescoreTestWorld.Tenant, _world.UserId, null, null, CancellationToken.None)).IsSuccess);

        Assert.Equal("Warning", (await _world.ReadResultAsync(seeded.ResultId))!.ValidationStatus);
        var previous = JsonDocument.Parse(Assert.Single(await _world.ReadHistoryAsync()).PreviousValuesJson).RootElement;
        Assert.False(previous.TryGetProperty("validationStatus", out _));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private Task<AnalysisRescoreOutcome> CorrectAsync(Guid resultId, BalanceCorrectionCommand command)
        => _world.Service().CorrectBalanceAsync(
            resultId, RescoreTestWorld.Tenant, _world.UserId, command, "10.0.0.1", "trace-1", CancellationToken.None);

    private Task<(Analysis Analysis, AnalysisResult Result, Document Document)> SeedFinexAsync(
        Entity entity, Action<AnalysisResult>? configure = null)
        => _world.SeedAnalysisAsync(entity, configureResult: r =>
        {
            r.BalanceJson = BalanceCorrectionFixtures.FinexBalanceJson();
            configure?.Invoke(r);
        });

    private Task<(Analysis Analysis, AnalysisResult Result, Document Document)> SeedMixedColumnsAsync(
        Entity entity, bool recordVerdict)
        => _world.SeedAnalysisAsync(entity, configureResult: r => r.BalanceJson = BalanceCorrectionFixtures.MixedColumnsJson(recordVerdict));

    private Task<(Analysis Analysis, AnalysisResult Result, Document Document)> SeedMisreadResultBeforeTaxAsync(
        Entity entity, Action<AnalysisResult>? configure = null)
        => _world.SeedAnalysisAsync(entity, configureResult: r =>
        {
            r.BalanceJson = BalanceCorrectionFixtures.MisreadResultBeforeTaxJson();
            configure?.Invoke(r);
        });

    private async Task<List<AnalysisEdit>> ReadEditsAsync()
    {
        await using var db = _world.NewContext();
        return await db.AnalysisEdits.AsNoTracking().ToListAsync();
    }

    private static List<string> ReliabilityCodes(AnalysisResult result)
    {
        var verdict = ScoreReliabilityAdapter.Evaluate(
            new ScoreReliabilityRow(
                result.AiSuggestedScore, result.EnrichmentStatus, AnalysisKinds.Balance, 2024,
                result.GeneratedAt, result.RescoredAt),
            result.ScoreBreakdownJson);

        return verdict.Reliability!.Reasons.Select(r => r.Code).ToList();
    }

    private async Task<AnalysisRescoreOutcome> AssertRefusedAsync(
        AnalysisResult seeded, int status, string errorCode, BalanceCorrectionCommand? command = null, bool bcraCalled = false)
    {
        var before  = RescoreTestWorld.Fingerprint((await _world.ReadResultAsync(seeded.ResultId))!);
        var outcome = await CorrectAsync(seeded.ResultId, command ?? BalanceCorrectionFixtures.FinexCommand());

        Assert.Equal(status, outcome.StatusCode);
        Assert.Equal(errorCode, outcome.ErrorCode);
        Assert.Null(outcome.Summary);
        Assert.Equal(before, RescoreTestWorld.Fingerprint((await _world.ReadResultAsync(seeded.ResultId))!));
        Assert.Empty(await _world.ReadHistoryAsync());
        Assert.Empty(await ReadEditsAsync());
        Assert.Empty(_world.Audit.Entries);
        if (!bcraCalled)
            Assert.Equal(0, _world.Bcra.Calls);
        Assert.Equal(0, _world.Claude.NarrativeCalls);
        Assert.Equal(0, _world.Claude.AnalyzeCalls);
        return outcome;
    }

    /// <summary>Runs <paramref name="beforeApply"/> with the write, just before it is applied.</summary>
    private sealed class InterceptingRescoreRepository(IAnalysisRescoreRepository inner, Func<AnalysisRescoreWrite, Task> beforeApply)
        : IAnalysisRescoreRepository
    {
        public Task<bool> HasAnyVoteAsync(Guid analysisId, string tenantId, CancellationToken ct)
            => inner.HasAnyVoteAsync(analysisId, tenantId, ct);

        public Task<Guid?> GetAnalysisIdAsync(Guid resultId, string tenantId, CancellationToken ct)
            => inner.GetAnalysisIdAsync(resultId, tenantId, ct);

        public Task<(Guid AnalysisId, string? BalanceJson)?> GetCorrectionSourceAsync(Guid resultId, string tenantId, CancellationToken ct)
            => inner.GetCorrectionSourceAsync(resultId, tenantId, ct);

        public Task<IReadOnlyList<AnalysisRescoreHistoryItem>> GetHistoryAsync(Guid analysisId, string tenantId, CancellationToken ct)
            => inner.GetHistoryAsync(analysisId, tenantId, ct);

        public async Task<AnalysisRescoreWriteOutcome> TryApplyAsync(AnalysisRescoreWrite write, CancellationToken ct)
        {
            await beforeApply(write).ConfigureAwait(false);
            return await inner.TryApplyAsync(write, ct).ConfigureAwait(false);
        }
    }
}
