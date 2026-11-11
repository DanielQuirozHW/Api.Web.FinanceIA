// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-22

using System.Text.Json;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.AnalysisRuns;

/// <summary>
/// "Reanalizar con datos actualizados" end to end over <see cref="RescoreTestWorld"/>: the real pipeline
/// writes the analysis, the real service re-scores it, and the database is read back. Every test also
/// checks the one thing a re-score may never do — call the Claude extraction.
/// </summary>
public sealed class AnalysisRescoreServiceTests
{
    private readonly RescoreTestWorld _world = new();

    private Task<AnalysisRescoreOutcome> RescoreAsync(Guid resultId, IAnalysisRescoreRepository? repository = null)
        => _world.Service(repository).RescoreAsync(
            resultId, RescoreTestWorld.Tenant, _world.UserId, "10.0.0.1", "trace-1", CancellationToken.None);

    // ── The point of the feature ────────────────────────────────────────────────

    [Fact]
    public async Task Rescore_of_a_degraded_analysis_with_BCRA_back_restores_the_BCRA_component_and_writes_history()
    {
        var entity = await _world.SeedEntityAsync();

        // The fresh analysis ran while the BCRA was down.
        _world.Bcra.Throws = new HttpRequestException("BCRA down");
        var degraded = await _world.RunPipelineAsync(entity);
        Assert.Equal(nameof(BcraEnrichmentOutcome.Degraded), degraded.EnrichmentStatus);
        Assert.True(Breakdown(degraded).GetProperty("bcraExcluded").GetBoolean());
        var extractionCalls = _world.Claude.AnalyzeCalls;

        // The BCRA answers again.
        _world.Bcra.Throws = null;
        var outcome = await RescoreAsync(degraded.ResultId);

        Assert.True(outcome.IsSuccess);
        var rescored = (await _world.ReadResultAsync(degraded.ResultId))!;
        Assert.Equal(nameof(BcraEnrichmentOutcome.Complete), rescored.EnrichmentStatus);
        Assert.Null(rescored.EnrichmentNotes);
        Assert.False(Breakdown(rescored).GetProperty("bcraExcluded").GetBoolean());
        Assert.NotNull(rescored.BcraDataJson);
        Assert.Equal(1, rescored.RescoreCount);
        Assert.Equal(_world.UserId, rescored.RescoredBy);
        Assert.Equal(_world.Clock.Now.UtcDateTime, rescored.RescoredAt);

        var history = Assert.Single(await _world.ReadHistoryAsync());
        Assert.Equal(degraded.AiSuggestedScore, history.PreviousScore);
        Assert.Equal(rescored.AiSuggestedScore, history.NewScore);
        Assert.Equal(nameof(BcraEnrichmentOutcome.Degraded), history.PreviousEnrichmentStatus);
        Assert.Equal(nameof(BcraEnrichmentOutcome.Complete), history.NewEnrichmentStatus);
        Assert.Equal(degraded.AnalysisId, history.AnalysisId);

        // The previous values are the exact strings that were on the row.
        var previous = JsonSerializer.Deserialize<RescorePreviousValues>(history.PreviousValuesJson, CamelCase)!;
        Assert.Equal(degraded.ScoreBreakdownJson, previous.ScoreBreakdownJson);
        Assert.Equal(degraded.ScoringNarrative,   previous.ScoringNarrative);
        Assert.Equal(degraded.BcraDataJson,       previous.BcraDataJson);
        Assert.Equal(degraded.EnrichmentNotes,    previous.EnrichmentNotes);

        // The response block, and the sources it reports.
        var summary = outcome.Summary!;
        Assert.Equal(history.PreviousScore, summary.PreviousScore);
        Assert.Equal(history.NewScore,      summary.NewScore);
        var bcra = Assert.Single(summary.Sources, s => s.Source == RescoreSource.Bcra);
        Assert.True(bcra.Live);
        Assert.Equal(_world.Clock.Now.UtcDateTime, bcra.AsOf);
        Assert.Equal(entity.LufeDataUpdatedAt, Assert.Single(summary.Sources, s => s.Source == RescoreSource.Lufe).AsOf);
        Assert.Equal(entity.NosisDataUpdatedAt, Assert.Single(summary.Sources, s => s.Source == RescoreSource.Nosis).AsOf);

        // Audited, narrative logged as Narrative, no extraction.
        Assert.Contains(_world.Audit.Entries, e => e.Action == "Analysis.Rescored" && e.EntityId == degraded.AnalysisId.ToString());
        Assert.Contains(_world.ApiCalls.Entries, e => e.Operation == ApiCallLogOperations.Narrative && e.AnalysisId == degraded.AnalysisId);
        Assert.Equal(extractionCalls, _world.Claude.AnalyzeCalls);
    }

    [Fact]
    public async Task Identical_inputs_give_the_score_a_fresh_pipeline_run_gave()
    {
        var entity = await _world.SeedEntityAsync();
        var fresh  = await _world.RunPipelineAsync(entity);
        Assert.Equal(nameof(BcraEnrichmentOutcome.Complete), fresh.EnrichmentStatus);
        var extractionCalls = _world.Claude.AnalyzeCalls;

        var outcome = await RescoreAsync(fresh.ResultId);

        Assert.True(outcome.IsSuccess);
        var rescored = (await _world.ReadResultAsync(fresh.ResultId))!;
        Assert.NotNull(fresh.AiSuggestedScore);
        Assert.Equal(fresh.AiSuggestedScore,   rescored.AiSuggestedScore);
        Assert.Equal(fresh.AiSuggestedLevel,   rescored.AiSuggestedLevel);
        Assert.Equal(fresh.ScoreBreakdownJson, rescored.ScoreBreakdownJson);
        Assert.Equal(fresh.RatiosJson,         rescored.RatiosJson);
        Assert.Equal(fresh.BcraDataJson,       rescored.BcraDataJson);
        Assert.Equal(fresh.ScoringNarrative,   rescored.ScoringNarrative);
        Assert.Equal(extractionCalls, _world.Claude.AnalyzeCalls);
    }

    [Fact]
    public async Task Rescore_reads_the_entity_snapshots_as_they_are_now()
    {
        var entity = await _world.SeedEntityAsync();
        var fresh  = await _world.RunPipelineAsync(entity);

        await using (var db = _world.NewContext())
        {
            var live = await db.Entities.SingleAsync(e => e.EntityId == entity.EntityId);
            live.NosisDataJson      = """{"score":810}""";
            live.NosisDataUpdatedAt = RescoreTestWorld.Utc(2026, 10, 20);
            db.ChangeTracker.DetectChanges();
            await db.SaveChangesAsync();
        }

        var outcome = await RescoreAsync(fresh.ResultId);

        var rescored = (await _world.ReadResultAsync(fresh.ResultId))!;
        Assert.Equal("""{"score":810}""", rescored.NosisDataJson);
        Assert.Equal(RescoreTestWorld.Utc(2026, 10, 20),
            Assert.Single(outcome.Summary!.Sources, s => s.Source == RescoreSource.Nosis).AsOf);
    }

    // ── What a re-score must never touch ────────────────────────────────────────

    [Fact]
    public async Task Two_rescores_leave_every_extraction_output_byte_identical()
    {
        var entity = await _world.SeedEntityAsync();
        var fresh  = await _world.RunPipelineAsync(entity);

        // The pipeline appended the two cash-flow anomalies to the model's one.
        var anomalies = JsonSerializer.Deserialize<List<string>>(fresh.Anomalies!)!;
        Assert.Equal(3, anomalies.Count);

        Assert.True((await RescoreAsync(fresh.ResultId)).IsSuccess);
        _world.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.True((await RescoreAsync(fresh.ResultId)).IsSuccess);

        var after = (await _world.ReadResultAsync(fresh.ResultId))!;
        Assert.Equal(fresh.Anomalies,        after.Anomalies);
        Assert.Equal(3, JsonSerializer.Deserialize<List<string>>(after.Anomalies!)!.Count);
        Assert.Equal(fresh.BalanceJson,      after.BalanceJson);
        Assert.Equal(fresh.RiskAlerts,       after.RiskAlerts);
        Assert.Equal(fresh.ExecutiveSummary, after.ExecutiveSummary);
        Assert.Equal(fresh.CatalogueAlerts,  after.CatalogueAlerts);
        Assert.Equal(fresh.ValidationStatus, after.ValidationStatus);
        Assert.Equal(fresh.AiInputHash,      after.AiInputHash);
        Assert.Equal(fresh.AiOutputTokens,   after.AiOutputTokens);
        Assert.Equal(fresh.GeneratedAt,      after.GeneratedAt);
        Assert.Equal(2, after.RescoreCount);
        Assert.Equal(2, (await _world.ReadHistoryAsync()).Count);

        // The Analyses row is untouched too.
        await using var db = _world.NewContext();
        var analysis = await db.Analyses.AsNoTracking().SingleAsync(a => a.AnalysisId == fresh.AnalysisId);
        Assert.Equal(AnalysisStatuses.PendingReview, analysis.Status);
        Assert.Equal(1, await db.Analyses.CountAsync());
    }

    [Fact]
    public async Task A_failed_narrative_keeps_the_new_score_and_stores_no_narrative()
    {
        var entity = await _world.SeedEntityAsync();
        var (_, seeded, _) = await _world.SeedAnalysisAsync(entity);
        _world.Claude.NarrativeFails = true;

        var outcome = await RescoreAsync(seeded.ResultId);

        Assert.True(outcome.IsSuccess);
        var rescored = (await _world.ReadResultAsync(seeded.ResultId))!;
        Assert.Null(rescored.ScoringNarrative);
        Assert.NotNull(rescored.AiSuggestedScore);
        Assert.Equal(ScoringService.ScoringAlgoVersion, Breakdown(rescored).GetProperty("scoringAlgoVersion").GetString());
        // No usage, nothing to bill.
        Assert.DoesNotContain(_world.ApiCalls.Entries, e => e.Operation == ApiCallLogOperations.Narrative);
        Assert.Equal(0, _world.Claude.AnalyzeCalls);
    }

    // ── Score level contract: the re-score records the cutoffs it classified with ─

    private async Task SetLevelCutoffsAsync(decimal low, decimal medium, decimal high)
    {
        await using var db = _world.NewContext();
        foreach (var (key, value) in new[]
                 {
                     (ScoreLevelCutoffs.LowKey, low), (ScoreLevelCutoffs.MediumKey, medium), (ScoreLevelCutoffs.HighKey, high)
                 })
        {
            db.ScoringConfigs.Add(new ScoringConfig
            {
                ConfigId    = Guid.NewGuid(),
                TenantId    = RescoreTestWorld.Tenant,
                ConfigKey   = key,
                ConfigValue = value,
                UpdatedAt   = RescoreTestWorld.Utc(2026, 1, 1)
            });
        }

        db.ChangeTracker.DetectChanges();
        await db.SaveChangesAsync();
    }

    private static ScoreLevelCutoffs? LevelCutoffs(AnalysisResult result)
        => Breakdown(result).TryGetProperty("levelCutoffs", out var node) && node.ValueKind == JsonValueKind.Object
            ? new ScoreLevelCutoffs(
                node.GetProperty("low").GetDecimal(),
                node.GetProperty("medium").GetDecimal(),
                node.GetProperty("high").GetDecimal())
            : null;

    [Fact]
    public async Task A_rescore_writes_levelCutoffs_onto_a_row_that_predates_them()
    {
        var entity = await _world.SeedEntityAsync();
        var (_, seeded, _) = await _world.SeedAnalysisAsync(entity);
        Assert.Null(LevelCutoffs(seeded));   // the seeded row is an old one: {"scoringAlgoVersion":"8"}

        Assert.True((await RescoreAsync(seeded.ResultId)).IsSuccess);

        var rescored = (await _world.ReadResultAsync(seeded.ResultId))!;
        Assert.Equal(new ScoreLevelCutoffs(35m, 60m, 80m), LevelCutoffs(rescored));
    }

    /// <summary>
    /// A tenant with custom cutoffs: the re-score's breakdown records exactly that config, and the
    /// stored level is the one those cutoffs give — the score itself does not move.
    /// </summary>
    [Fact]
    public async Task A_rescore_under_custom_cutoffs_records_them_and_stores_the_matching_level()
    {
        var entity = await _world.SeedEntityAsync();
        var fresh  = await _world.RunPipelineAsync(entity);
        decimal s  = fresh.AiSuggestedScore!.Value;
        Assert.Equal(new ScoreLevelCutoffs(35m, 60m, 80m), LevelCutoffs(fresh));

        // Cutoffs placed around the score so it can only be Medium under them.
        await SetLevelCutoffsAsync(s - 1m, s + 1m, s + 2m);
        Assert.True((await RescoreAsync(fresh.ResultId)).IsSuccess);

        var rescored = (await _world.ReadResultAsync(fresh.ResultId))!;
        Assert.Equal(s, rescored.AiSuggestedScore);
        Assert.Equal("Medium", rescored.AiSuggestedLevel);
        Assert.Equal(new ScoreLevelCutoffs(s - 1m, s + 1m, s + 2m), LevelCutoffs(rescored));
    }

    // ── The BCRA guard ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_BCRA_still_failing_aborts_with_503_and_changes_nothing()
    {
        var entity = await _world.SeedEntityAsync();
        var (_, seeded, _) = await _world.SeedAnalysisAsync(entity);
        var before = RescoreTestWorld.Fingerprint((await _world.ReadResultAsync(seeded.ResultId))!);
        _world.Bcra.Throws = new HttpRequestException("BCRA down");

        var outcome = await RescoreAsync(seeded.ResultId);

        Assert.Equal(503, outcome.StatusCode);
        Assert.Equal(RescoreErrorCodes.BcraUnavailable, outcome.ErrorCode);
        Assert.Equal(60, outcome.RetryAfterSeconds);
        Assert.Equal(before, RescoreTestWorld.Fingerprint((await _world.ReadResultAsync(seeded.ResultId))!));
        Assert.Empty(await _world.ReadHistoryAsync());
        Assert.Equal(0, _world.Claude.NarrativeCalls);
        Assert.Empty(_world.Audit.Entries);
        Assert.Equal(0, _world.Claude.AnalyzeCalls);
    }

    [Fact]
    public async Task A_BCRA_answering_nothing_usable_aborts_with_503()
    {
        var entity = await _world.SeedEntityAsync();
        var (_, seeded, _) = await _world.SeedAnalysisAsync(entity);
        _world.Bcra.Payload = null;

        var outcome = await RescoreAsync(seeded.ResultId);

        Assert.Equal(503, outcome.StatusCode);
        Assert.Empty(await _world.ReadHistoryAsync());
    }

    [Fact]
    public async Task An_entity_that_no_longer_resolves_aborts_with_503()
    {
        var entity = await _world.SeedEntityAsync();
        var (_, seeded, _) = await _world.SeedAnalysisAsync(entity);

        await using (var db = _world.NewContext())
        {
            db.Entities.Remove(await db.Entities.SingleAsync(e => e.EntityId == entity.EntityId));
            db.ChangeTracker.DetectChanges();
            await db.SaveChangesAsync();
        }

        var outcome = await RescoreAsync(seeded.ResultId);

        Assert.Equal(503, outcome.StatusCode);
        Assert.Equal(RescoreErrorCodes.BcraUnavailable, outcome.ErrorCode);
        Assert.Equal(0, _world.Bcra.Calls);
        Assert.Empty(await _world.ReadHistoryAsync());
    }

    [Fact]
    public async Task An_entity_without_a_CUIT_is_rescored_without_the_BCRA()
    {
        var entity = await _world.SeedEntityAsync(cuit: null);
        var (_, seeded, _) = await _world.SeedAnalysisAsync(entity);

        var outcome = await RescoreAsync(seeded.ResultId);

        Assert.True(outcome.IsSuccess);
        Assert.Equal(nameof(BcraEnrichmentOutcome.NotApplicable), outcome.Summary!.NewEnrichmentStatus);
        var bcra = Assert.Single(outcome.Summary.Sources, s => s.Source == RescoreSource.Bcra);
        Assert.Null(bcra.AsOf);
        Assert.False(bcra.Live);
        Assert.Equal(0, _world.Bcra.Calls);
    }

    // ── Cheques outage (score credibility 1b-1, V4) and the BCRA fetch date (V13) ──

    private static BcraData BcraWithCheques(BcraChequesData cheques)
    {
        var payload = RescoreTestWorld.BcraWithDebt();
        payload.Cheques = cheques;
        return payload;
    }

    private static BcraChequesData FailedCheques() => new() { Estado = BcraChequesConsultaEstado.LookupFailed };

    private async Task DisableChequesPenaltyAsync()
    {
        await using var db = _world.NewContext();
        db.ScoringConfigs.Add(new ScoringConfig
        {
            ConfigId    = Guid.NewGuid(),
            TenantId    = RescoreTestWorld.Tenant,
            ConfigKey   = "bcra_cheques_enabled",
            ConfigValue = 0m,
            UpdatedAt   = RescoreTestWorld.Utc(2026, 1, 1)
        });
        db.ChangeTracker.DetectChanges();
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_new_analysis_whose_cheques_lookup_failed_is_Degraded_with_the_cheques_note()
    {
        var entity = await _world.SeedEntityAsync();
        _world.Bcra.Payload = BcraWithCheques(FailedCheques());

        var result = await _world.RunPipelineAsync(entity);

        Assert.Equal(nameof(BcraEnrichmentOutcome.Degraded), result.EnrichmentStatus);
        Assert.Equal(BcraEnrichmentClassifier.NotesChequesUnavailable, result.EnrichmentNotes);
        // The BCRA component itself is in the score: only the cheques penalty could be missing.
        Assert.False(Breakdown(result).GetProperty("bcraExcluded").GetBoolean());
    }

    [Fact]
    public async Task A_new_analysis_whose_cheques_lookup_failed_stays_Complete_when_the_penalty_is_off()
    {
        await DisableChequesPenaltyAsync();
        var entity = await _world.SeedEntityAsync();
        _world.Bcra.Payload = BcraWithCheques(FailedCheques());

        var result = await _world.RunPipelineAsync(entity);

        Assert.Equal(nameof(BcraEnrichmentOutcome.Complete), result.EnrichmentStatus);
        Assert.Null(result.EnrichmentNotes);
    }

    [Fact]
    public async Task A_cheques_lookup_failing_on_rescore_aborts_with_503_and_the_stored_penalty_survives()
    {
        var entity = await _world.SeedEntityAsync();

        // The analysis ran with real rejected cheques, nothing recovered: it carries the penalty.
        _world.Bcra.Payload = BcraWithCheques(new BcraChequesData
        {
            Estado              = BcraChequesConsultaEstado.ChequesFound,
            TotalRechazados     = 3,
            MontoTotalRechazado = 9000m,
            MontoTotalImpago    = 9000m
        });
        var scored = await _world.RunPipelineAsync(entity);
        Assert.Equal(nameof(BcraEnrichmentOutcome.Complete), scored.EnrichmentStatus);
        Assert.True(Breakdown(scored).GetProperty("chequesAdj").GetDecimal() > 0m);
        var before = RescoreTestWorld.Fingerprint((await _world.ReadResultAsync(scored.ResultId))!);
        var narrativeCalls = _world.Claude.NarrativeCalls;

        // On re-score the cheques lookup fails: re-scoring now would silently drop the penalty.
        _world.Bcra.Payload = BcraWithCheques(FailedCheques());
        var outcome = await RescoreAsync(scored.ResultId);

        Assert.Equal(503, outcome.StatusCode);
        Assert.Equal(RescoreErrorCodes.BcraUnavailable, outcome.ErrorCode);
        Assert.Equal(60, outcome.RetryAfterSeconds);
        Assert.Equal(before, RescoreTestWorld.Fingerprint((await _world.ReadResultAsync(scored.ResultId))!));
        Assert.Empty(await _world.ReadHistoryAsync());
        Assert.Equal(narrativeCalls, _world.Claude.NarrativeCalls);   // nothing spent
    }

    [Fact]
    public async Task A_cheques_lookup_failing_on_rescore_is_harmless_when_the_penalty_is_off()
    {
        await DisableChequesPenaltyAsync();
        var entity = await _world.SeedEntityAsync();
        var (_, seeded, _) = await _world.SeedAnalysisAsync(entity);
        _world.Bcra.Payload = BcraWithCheques(FailedCheques());

        var outcome = await RescoreAsync(seeded.ResultId);

        Assert.True(outcome.IsSuccess);
        Assert.Equal(nameof(BcraEnrichmentOutcome.Complete), outcome.Summary!.NewEnrichmentStatus);
    }

    [Fact]
    public async Task The_BCRA_source_is_dated_when_the_deuda_was_fetched_not_when_the_rescore_ran()
    {
        var entity = await _world.SeedEntityAsync();
        var (_, seeded, _) = await _world.SeedAnalysisAsync(entity);
        var fetchedAt = _world.Clock.Now.UtcDateTime.AddHours(-20);   // served from BcraService's 24 h cache
        var payload = RescoreTestWorld.BcraWithDebt();
        payload.Deuda!.FetchedAt = fetchedAt;
        _world.Bcra.Payload = payload;

        var outcome = await RescoreAsync(seeded.ResultId);

        Assert.True(outcome.IsSuccess);
        var bcra = Assert.Single(outcome.Summary!.Sources, s => s.Source == RescoreSource.Bcra);
        Assert.True(bcra.Live);
        Assert.Equal(fetchedAt, bcra.AsOf);

        var history = Assert.Single(await _world.ReadHistoryAsync());
        Assert.Contains(fetchedAt.ToString("yyyy-MM-ddTHH:mm:ss"), history.SourcesJson, StringComparison.Ordinal);
    }

    // ── The refusals (409) ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_persona_humana_analysis_is_handed_to_the_re_evaluation_never_re_scored()
    {
        var entity = await _world.SeedEntityAsync();
        var (analysis, seeded, _) = await _world.SeedAnalysisAsync(entity,
            a => { a.AnalysisKind = AnalysisKinds.PersonaHumana; a.DocumentId = null; },
            r => { r.BalanceJson = null; r.AiSuggestedScore = null; r.AiSuggestedLevel = null; });

        // The recording branch answers a refusal, so the shared assertion also proves the balance path
        // touched nothing: no BCRA lookup, no model call, no write.
        await AssertRefusedAsync(seeded, 409, "recorded");

        var call = Assert.Single(_world.PersonRescore.Calls);
        Assert.Equal(analysis.AnalysisId, call.AnalysisId);
        Assert.Equal(_world.Clock.Now.UtcDateTime, call.Now);
    }

    [Fact]
    public async Task A_persona_humana_analysis_goes_through_the_same_rules_and_cooldown_first()
    {
        var entity = await _world.SeedEntityAsync();
        var (_, decided, _) = await _world.SeedAnalysisAsync(entity,
            a => { a.AnalysisKind = AnalysisKinds.PersonaHumana; a.DocumentId = null; a.Status = AnalysisStatuses.Approved; },
            r => r.BalanceJson = null);
        var (_, preApproved, _) = await _world.SeedAnalysisAsync(entity,
            a => { a.AnalysisKind = AnalysisKinds.PersonaHumana; a.DocumentId = null; a.PreApprovedBy = Guid.NewGuid(); },
            r => r.BalanceJson = null);
        var (_, justRescored, _) = await _world.SeedAnalysisAsync(entity,
            a => { a.AnalysisKind = AnalysisKinds.PersonaHumana; a.DocumentId = null; },
            r => { r.BalanceJson = null; r.RescoredAt = _world.Clock.Now.UtcDateTime.AddSeconds(-5); });

        await AssertRefusedAsync(decided, 409, RescoreErrorCodes.NotPending);
        await AssertRefusedAsync(preApproved, 409, RescoreErrorCodes.HasVotes);

        var cooled = await RescoreAsync(justRescored.ResultId);
        Assert.Equal(429, cooled.StatusCode);
        Assert.Equal(RescoreErrorCodes.TooManyRequests, cooled.ErrorCode);

        Assert.Empty(_world.PersonRescore.Calls);
    }

    [Fact]
    public async Task A_result_without_a_balance_is_refused()
    {
        var entity = await _world.SeedEntityAsync();
        var (_, seeded, _) = await _world.SeedAnalysisAsync(entity, configureResult: r => r.BalanceJson = null);

        await AssertRefusedAsync(seeded, 409, RescoreErrorCodes.ResultMissing);
    }

    [Theory]
    [InlineData(AnalysisStatuses.PreApproved)]
    [InlineData(AnalysisStatuses.Approved)]
    [InlineData(AnalysisStatuses.Rejected)]
    public async Task An_analysis_no_longer_pending_is_refused(string status)
    {
        var entity = await _world.SeedEntityAsync();
        var (_, seeded, _) = await _world.SeedAnalysisAsync(entity, a => a.Status = status);

        await AssertRefusedAsync(seeded, 409, RescoreErrorCodes.NotPending);
    }

    [Fact]
    public async Task An_analysis_with_a_vote_is_refused()
    {
        var entity = await _world.SeedEntityAsync();
        var (analysis, seeded, _) = await _world.SeedAnalysisAsync(entity);
        await _world.AddVoteAsync(analysis.AnalysisId, AnalysisVoteValues.Rejected);

        await AssertRefusedAsync(seeded, 409, RescoreErrorCodes.HasVotes);
    }

    [Fact]
    public async Task An_analysis_returned_for_rework_after_a_pre_approval_is_refused()
    {
        // Back in PendingReview, votes wiped — but the pre-approval record stays on the row.
        var entity = await _world.SeedEntityAsync();
        var (_, seeded, _) = await _world.SeedAnalysisAsync(entity, a =>
        {
            a.PreApprovedBy = Guid.NewGuid();
            a.PreApprovedAt = RescoreTestWorld.Utc(2026, 10, 5);
            a.ReviewedBy    = Guid.NewGuid();
            a.ReviewedAt    = RescoreTestWorld.Utc(2026, 10, 6);
        });

        await AssertRefusedAsync(seeded, 409, RescoreErrorCodes.HasVotes);
    }

    [Fact]
    public async Task An_analysis_returned_without_a_pre_approval_is_still_refused()
    {
        var entity = await _world.SeedEntityAsync();
        var (_, seeded, _) = await _world.SeedAnalysisAsync(entity, a => a.ReviewedBy = Guid.NewGuid());

        await AssertRefusedAsync(seeded, 409, RescoreErrorCodes.HasVotes);
    }

    [Fact]
    public async Task An_analysis_whose_document_is_processing_is_refused()
    {
        var entity = await _world.SeedEntityAsync();
        var (_, seeded, _) = await _world.SeedAnalysisAsync(entity, documentStatus: "Processing");

        await AssertRefusedAsync(seeded, 409, RescoreErrorCodes.Processing);
    }

    [Fact]
    public async Task A_result_of_another_tenant_is_not_found()
    {
        var entity = await _world.SeedEntityAsync(tenantId: RescoreTestWorld.OtherTenant);
        var (_, seeded, _) = await _world.SeedAnalysisAsync(entity);

        var outcome = await RescoreAsync(seeded.ResultId);

        Assert.Equal(404, outcome.StatusCode);
        Assert.Equal(0, _world.Bcra.Calls);
    }

    // ── Cooldown and concurrency ────────────────────────────────────────────────

    [Fact]
    public async Task A_second_rescore_inside_the_cooldown_is_429_with_the_remaining_seconds()
    {
        var entity = await _world.SeedEntityAsync();
        var (_, seeded, _) = await _world.SeedAnalysisAsync(entity);

        Assert.True((await RescoreAsync(seeded.ResultId)).IsSuccess);

        _world.Clock.Advance(TimeSpan.FromSeconds(30));
        var refused = await RescoreAsync(seeded.ResultId);
        Assert.Equal(429, refused.StatusCode);
        Assert.Equal(RescoreErrorCodes.TooManyRequests, refused.ErrorCode);
        Assert.Equal(90, refused.RetryAfterSeconds);
        Assert.Single(await _world.ReadHistoryAsync());

        _world.Clock.Advance(TimeSpan.FromSeconds(91));
        Assert.True((await RescoreAsync(seeded.ResultId)).IsSuccess);
        Assert.Equal(2, (await _world.ReadHistoryAsync()).Count);
    }

    [Fact]
    public async Task A_lost_race_is_409_and_the_winner_stays()
    {
        var entity = await _world.SeedEntityAsync();
        var (_, seeded, _) = await _world.SeedAnalysisAsync(entity);

        // Another request re-scores the same result while this one is computing.
        var racing = new RacingRescoreRepository(
            new AnalysisRescoreRepository(_world.NewContext(), NullLogger<AnalysisRescoreRepository>.Instance),
            beforeApply: async () =>
            {
                _world.Clock.Advance(TimeSpan.FromSeconds(1));
                Assert.True((await RescoreAsync(seeded.ResultId)).IsSuccess);
            });

        var outcome = await RescoreAsync(seeded.ResultId, racing);

        Assert.Equal(409, outcome.StatusCode);
        Assert.Equal(RescoreErrorCodes.InProgress, outcome.ErrorCode);
        var history = Assert.Single(await _world.ReadHistoryAsync());
        var result  = (await _world.ReadResultAsync(seeded.ResultId))!;
        Assert.Equal(1, result.RescoreCount);
        Assert.Equal(history.RescoredAt, result.RescoredAt);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_decision_landing_while_computing_aborts_the_write(bool viaVote)
    {
        var entity = await _world.SeedEntityAsync();
        var (analysis, seeded, _) = await _world.SeedAnalysisAsync(entity);
        var before = RescoreTestWorld.Fingerprint((await _world.ReadResultAsync(seeded.ResultId))!);

        var racing = new RacingRescoreRepository(
            new AnalysisRescoreRepository(_world.NewContext(), NullLogger<AnalysisRescoreRepository>.Instance),
            beforeApply: async () =>
            {
                if (viaVote)
                {
                    await _world.AddVoteAsync(analysis.AnalysisId);
                    return;
                }

                await using var db = _world.NewContext();
                var row = await db.Analyses.SingleAsync(a => a.AnalysisId == analysis.AnalysisId);
                row.Status = AnalysisStatuses.Approved;
                db.ChangeTracker.DetectChanges();
                await db.SaveChangesAsync();
            });

        var outcome = await RescoreAsync(seeded.ResultId, racing);

        Assert.Equal(409, outcome.StatusCode);
        Assert.Equal(viaVote ? RescoreErrorCodes.HasVotes : RescoreErrorCodes.NotPending, outcome.ErrorCode);
        Assert.Equal(before, RescoreTestWorld.Fingerprint((await _world.ReadResultAsync(seeded.ResultId))!));
        Assert.Empty(await _world.ReadHistoryAsync());
    }

    // ── History ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task History_is_newest_first_and_scoped_to_the_tenant()
    {
        var entity = await _world.SeedEntityAsync();
        var (_, seeded, _) = await _world.SeedAnalysisAsync(entity);

        Assert.True((await RescoreAsync(seeded.ResultId)).IsSuccess);
        _world.Clock.Advance(TimeSpan.FromMinutes(3));
        _world.Bcra.Payload = RescoreTestWorld.BcraWithDebt(situacion: 3);
        Assert.True((await RescoreAsync(seeded.ResultId)).IsSuccess);

        var history = await _world.Service().GetHistoryAsync(seeded.ResultId, RescoreTestWorld.Tenant, CancellationToken.None);

        Assert.NotNull(history);
        Assert.Equal(2, history.Count);
        Assert.True(history[0].RescoredAt > history[1].RescoredAt);
        Assert.Equal(history[1].NewScore, history[0].PreviousScore);

        Assert.Null(await _world.Service().GetHistoryAsync(seeded.ResultId, RescoreTestWorld.OtherTenant, CancellationToken.None));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static JsonElement Breakdown(AnalysisResult result)
        => JsonDocument.Parse(result.ScoreBreakdownJson!).RootElement;

    private async Task AssertRefusedAsync(AnalysisResult seeded, int status, string errorCode)
    {
        var before  = RescoreTestWorld.Fingerprint((await _world.ReadResultAsync(seeded.ResultId))!);
        var outcome = await RescoreAsync(seeded.ResultId);

        Assert.Equal(status, outcome.StatusCode);
        Assert.Equal(errorCode, outcome.ErrorCode);
        Assert.Null(outcome.Summary);
        Assert.Equal(before, RescoreTestWorld.Fingerprint((await _world.ReadResultAsync(seeded.ResultId))!));
        Assert.Empty(await _world.ReadHistoryAsync());
        Assert.Equal(0, _world.Bcra.Calls);
        Assert.Equal(0, _world.Claude.NarrativeCalls);
        Assert.Equal(0, _world.Claude.AnalyzeCalls);
    }

    /// <summary>Runs <paramref name="beforeApply"/> just before the write — a concurrent request landing.</summary>
    private sealed class RacingRescoreRepository(IAnalysisRescoreRepository inner, Func<Task> beforeApply)
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
            await beforeApply().ConfigureAwait(false);
            return await inner.TryApplyAsync(write, ct).ConfigureAwait(false);
        }
    }
}
