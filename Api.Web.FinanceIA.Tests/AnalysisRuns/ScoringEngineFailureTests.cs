// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-02

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Tests.Servicios;
using Xunit;

namespace Api.Web.FinanceIA.Tests.AnalysisRuns;

/// <summary>
/// When the scoring engine fails, its neutral 50 / Medium placeholder must never be persisted as a
/// score. Over <see cref="RescoreTestWorld"/>: the REAL pipeline, the REAL ScoringService and the REAL
/// re-score. The engine is made to fail the way its own catch-all test does: two ScoringConfig rows
/// colliding on (TenantId, ConfigKey) make LoadConfigAsync's ToDictionary throw.
/// </summary>
public sealed class ScoringEngineFailureTests
{
    private readonly RescoreTestWorld _world = new();

    private async Task BreakTheScoringEngineAsync()
    {
        await using var db = _world.NewContext();
        db.ScoringConfigs.AddRange(
            ScoringTestHarness.Row(RescoreTestWorld.Tenant, "weight_component_bcra", 0.35m),
            ScoringTestHarness.Row(RescoreTestWorld.Tenant, "weight_component_bcra", 0.40m));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_new_analysis_whose_scoring_fails_is_saved_without_a_score_and_keeps_its_extraction()
    {
        var entity = await _world.SeedEntityAsync();
        await BreakTheScoringEngineAsync();

        var result = await _world.RunPipelineAsync(entity);

        // No placeholder 50 / Medium: the existing "saved without a score" path.
        Assert.Null(result.AiSuggestedScore);
        Assert.Null(result.AiSuggestedLevel);
        Assert.Null(result.ScoreBreakdownJson);
        Assert.Null(result.ScoringNarrative);

        // The (paid) extraction is kept: the balance, the recomputed ratios, the anomalies.
        Assert.False(string.IsNullOrWhiteSpace(result.BalanceJson));
        Assert.Contains("\"assets\":8000000", result.BalanceJson);
        Assert.False(string.IsNullOrWhiteSpace(result.RatiosJson));
        Assert.Contains("Anomalía informada por el modelo.",
            System.Text.Json.JsonSerializer.Deserialize<List<string>>(result.Anomalies!)!);
    }

    [Fact]
    public async Task The_same_pipeline_with_a_working_engine_persists_a_real_score()
    {
        // Control for the test above: the world scores normally when the engine runs.
        var entity = await _world.SeedEntityAsync();

        var result = await _world.RunPipelineAsync(entity);

        Assert.NotNull(result.AiSuggestedScore);
        Assert.NotNull(result.ScoreBreakdownJson);
    }

    [Fact]
    public async Task A_rescore_whose_scoring_fails_refuses_and_leaves_the_real_score_untouched()
    {
        var entity   = await _world.SeedEntityAsync();
        var original = await _world.RunPipelineAsync(entity);
        Assert.NotNull(original.AiSuggestedScore);
        var before = RescoreTestWorld.Fingerprint(original);

        await BreakTheScoringEngineAsync();

        var outcome = await _world.Service().RescoreAsync(
            original.ResultId, RescoreTestWorld.Tenant, _world.UserId, "10.0.0.1", "trace-1", CancellationToken.None);

        Assert.Equal(500, outcome.StatusCode);
        Assert.Equal(RescoreErrorCodes.Failed, outcome.ErrorCode);
        Assert.Null(outcome.Summary);

        var after = (await _world.ReadResultAsync(original.ResultId))!;
        Assert.Equal(original.AiSuggestedScore, after.AiSuggestedScore);
        Assert.Equal(before, RescoreTestWorld.Fingerprint(after));
        Assert.Empty(await _world.ReadHistoryAsync());
    }
}
