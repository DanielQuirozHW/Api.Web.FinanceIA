// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-02

using System.Text.Json;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.ScoreReliability;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Api.Web.FinanceIA.Tests.AnalysisRuns;

/// <summary>
/// Score credibility 1b-3: the pipeline and the re-score hand the narrative the reliability verdict of the
/// score they JUST computed — the one adapter, over the row as it is persisted — and a re-score measures it
/// at its own instant (RescoredAt = now), not at the original generation.
/// </summary>
public sealed class NarrativeReliabilityPipelineTests
{
    private readonly RescoreTestWorld _world = new();

    private static string Wire(ScoreReliabilityResponse? verdict) => JsonSerializer.Serialize(verdict);

    private async Task<Analysis> ReadAnalysisAsync(Guid analysisId)
    {
        await using var db = _world.NewContext();
        return await db.Analyses.AsNoTracking().SingleAsync(a => a.AnalysisId == analysisId);
    }

    [Fact]
    public async Task Pipeline_passes_the_verdict_of_the_row_it_persists()
    {
        var entity = await _world.SeedEntityAsync();

        var result   = await _world.RunPipelineAsync(entity);
        var analysis = await ReadAnalysisAsync(result.AnalysisId);

        var passed = Assert.Single(_world.Claude.NarrativeReliabilities);
        Assert.NotNull(passed);

        var expected = ScoreReliabilityAdapter.Evaluate(
            new ScoreReliabilityRow(
                result.AiSuggestedScore, result.EnrichmentStatus, AnalysisKinds.Balance,
                analysis.FiscalYear, result.GeneratedAt, RescoredAt: null),
            result.ScoreBreakdownJson).Reliability;

        // The verdict the detail screen will show for this row.
        Assert.Equal(AnalysisKinds.Balance, analysis.AnalysisKind);
        Assert.Equal(Wire(expected), Wire(passed));
    }

    [Fact]
    public async Task Rescore_passes_the_verdict_of_the_NEW_score_measured_at_now()
    {
        var entity = await _world.SeedEntityAsync();
        // Stored row: Degraded (BCRA lookup threw), generated Oct-2026, fiscal year 2024.
        var (analysis, seeded, _) = await _world.SeedAnalysisAsync(entity);
        Assert.Equal(nameof(BcraEnrichmentOutcome.Degraded), seeded.EnrichmentStatus);

        // Five months later: Dec-2024 → Mar-2027 is 27 months, against 22 at the original generation.
        _world.Clock.Now = new DateTimeOffset(2027, 3, 15, 12, 0, 0, TimeSpan.Zero);

        var outcome = await _world.Service().RescoreAsync(
            seeded.ResultId, RescoreTestWorld.Tenant, _world.UserId, "10.0.0.1", "trace-1", CancellationToken.None);
        Assert.True(outcome.IsSuccess);

        var rescored = (await _world.ReadResultAsync(seeded.ResultId))!;
        Assert.Equal(_world.Clock.Now.UtcDateTime, rescored.RescoredAt);

        var passed = Assert.Single(_world.Claude.NarrativeReliabilities);
        Assert.NotNull(passed);

        var expected = ScoreReliabilityAdapter.Evaluate(
            new ScoreReliabilityRow(
                rescored.AiSuggestedScore, rescored.EnrichmentStatus, AnalysisKinds.Balance,
                analysis.FiscalYear, seeded.GeneratedAt, rescored.RescoredAt),
            rescored.ScoreBreakdownJson).Reliability;
        Assert.Equal(Wire(expected), Wire(passed));

        // The NEW enrichment (Complete), not the stored Degraded one.
        Assert.Equal(nameof(BcraEnrichmentOutcome.Complete), rescored.EnrichmentStatus);
        Assert.DoesNotContain(passed!.Reasons, r => r.Code == ScoreReliabilityCodes.BcraUnavailable);

        // Aged at RescoredAt, not at GeneratedAt.
        var stale = Assert.Single(passed.Reasons, r => r.Code == ScoreReliabilityCodes.BalanceStale);
        Assert.Equal(27, stale.Args!.Months);
    }
}
