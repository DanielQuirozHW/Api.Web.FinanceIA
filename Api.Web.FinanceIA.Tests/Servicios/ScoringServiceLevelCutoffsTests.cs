// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-04

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Api.Web.FinanceIA.Servicios.ScoreReliability;
using Xunit;
using static Api.Web.FinanceIA.Tests.Servicios.ScoringTestHarness;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// The score level contract on the engine side: the cutoffs a level was classified with are the ones
/// recorded (<see cref="ScoringResult.LevelCutoffs"/> → <c>ScoreBreakdownJson.levelCutoffs</c>), they
/// are the tenant's config or the defaults per key, recording them moves no score, and a row persisted
/// before the key existed still reads — as "no cutoffs", never as an error or a guess.
/// </summary>
public sealed class ScoringServiceLevelCutoffsTests
{
    private static readonly ScoreLevelCutoffs Defaults = new(35m, 60m, 80m);

    private static Task<ScoringResult> ScoreBaselineAsync(ScoringService sut, string? sector = null)
        => Score(sut, BaselineRatios(), BcraNoDebt(), MacroBenign(), sector);

    private static (string Key, decimal Value)[] Cutoffs(decimal low, decimal medium, decimal high) =>
    [
        (ScoreLevelCutoffs.LowKey, low),
        (ScoreLevelCutoffs.MediumKey, medium),
        (ScoreLevelCutoffs.HighKey, high),
    ];

    /// <summary>Reads <c>levelCutoffs</c> off a persisted blob, the way a typed reader would.</summary>
    private sealed record LevelCutoffsEnvelope(
        [property: JsonPropertyName("levelCutoffs")] ScoreLevelCutoffs? LevelCutoffs);

    private static ScoreLevelCutoffs? ReadCutoffs(string breakdownJson)
        => JsonSerializer.Deserialize<LevelCutoffsEnvelope>(breakdownJson, AnalysisPayloadJson.BcraJsonOpts)!.LevelCutoffs;

    // ── The engine records what it classified with ──────────────────────────────

    [Fact]
    public async Task NoConfigRows_RecordsTheDefaults()
    {
        var result = await ScoreBaselineAsync(await BuildAsync());

        Assert.Equal(Defaults, result.LevelCutoffs);
        Assert.Equal(Defaults.Classify(result.Score), result.Level);
    }

    [Fact]
    public async Task TenantRows_AreRecordedExactly()
    {
        var result = await ScoreBaselineAsync(await BuildAsync(Cutoffs(30m, 55m, 75m)));

        Assert.Equal(new ScoreLevelCutoffs(30m, 55m, 75m), result.LevelCutoffs);
    }

    /// <summary>A partial config resolves per key: the stored one, defaults for the rest.</summary>
    [Fact]
    public async Task APartialConfig_FallsBackPerKey()
    {
        var result = await ScoreBaselineAsync(await BuildAsync((ScoreLevelCutoffs.MediumKey, 50m)));

        Assert.Equal(new ScoreLevelCutoffs(35m, 50m, 80m), result.LevelCutoffs);
    }

    [Fact]
    public async Task AnotherTenantsRows_AreIgnored()
    {
        var sut = await BuildFromRowsAsync(
            Row(OtherTenant, ScoreLevelCutoffs.LowKey, 10m),
            Row(OtherTenant, ScoreLevelCutoffs.MediumKey, 20m),
            Row(OtherTenant, ScoreLevelCutoffs.HighKey, 30m));

        var result = await ScoreBaselineAsync(sut);

        Assert.Equal(Defaults, result.LevelCutoffs);
    }

    /// <summary>
    /// Custom cutoffs move the LEVEL and nothing else: the same score, classified against the numbers
    /// recorded beside it. Cutoffs are placed around the observed score so each level is reached.
    /// </summary>
    [Fact]
    public async Task CustomCutoffs_ChangeTheLevelNeverTheScore_AndTheLevelMatchesTheRecordedCutoffs()
    {
        var baseline = await ScoreBaselineAsync(await BuildAsync());
        decimal s = baseline.Score;

        var medium   = await ScoreBaselineAsync(await BuildAsync(Cutoffs(s - 1m, s + 1m, s + 2m)));
        var high     = await ScoreBaselineAsync(await BuildAsync(Cutoffs(s - 2m, s - 1m, s + 1m)));
        var critical = await ScoreBaselineAsync(await BuildAsync(Cutoffs(s - 3m, s - 2m, s - 1m)));
        var low      = await ScoreBaselineAsync(await BuildAsync(Cutoffs(s, s + 1m, s + 2m)));   // <= is inclusive

        Assert.All(new[] { medium, high, critical, low }, r => Assert.Equal(s, r.Score));
        Assert.Equal("Medium",   medium.Level);
        Assert.Equal("High",     high.Level);
        Assert.Equal("Critical", critical.Level);
        Assert.Equal("Low",      low.Level);

        Assert.All(new[] { medium, high, critical, low },
            r => Assert.Equal(r.LevelCutoffs!.Classify(r.Score), r.Level));
    }

    /// <summary>
    /// A sector row (only a DBA can write one for these keys) changes what THAT sector's analysis was
    /// classified with — which is why the row's own levelCutoffs, not the tenant read, is authoritative.
    /// </summary>
    [Fact]
    public async Task ASectorOverride_IsRecordedForThatSector_ButNotInTheTenantRead()
    {
        var sut = await BuildFromRowsAsync(
            Row(Tenant, ScoreLevelCutoffs.LowKey, 30m),
            Row(Tenant, ScoreLevelCutoffs.LowKey + SectorProfiles.SectorSuffix("Comercio"), 20m));

        var comercio = await ScoreBaselineAsync(sut, "Comercio");
        var general  = await ScoreBaselineAsync(sut);
        var tenant   = await sut.GetLevelCutoffsAsync(Tenant, CancellationToken.None);

        Assert.Equal(new ScoreLevelCutoffs(20m, 60m, 80m), comercio.LevelCutoffs);
        Assert.Equal(new ScoreLevelCutoffs(30m, 60m, 80m), general.LevelCutoffs);
        Assert.Equal(new ScoreLevelCutoffs(30m, 60m, 80m), tenant);
    }

    // ── The tenant read (GET /api/billing/usage) ────────────────────────────────

    [Fact]
    public async Task GetLevelCutoffsAsync_NoRows_ReturnsTheDefaults()
    {
        var sut = await BuildAsync();

        Assert.Equal(Defaults, await sut.GetLevelCutoffsAsync(Tenant, CancellationToken.None));
    }

    [Fact]
    public async Task GetLevelCutoffsAsync_EqualsWhatTheEngineRecords()
    {
        var sut = await BuildAsync(Cutoffs(25m, 50m, 70m));

        var read   = await sut.GetLevelCutoffsAsync(Tenant, CancellationToken.None);
        var scored = await ScoreBaselineAsync(sut);

        Assert.Equal(new ScoreLevelCutoffs(25m, 50m, 70m), read);
        Assert.Equal(scored.LevelCutoffs, read);
    }

    [Fact]
    public async Task GetLevelCutoffsAsync_IsTenantScoped()
    {
        var sut = await BuildFromRowsAsync(Row(OtherTenant, ScoreLevelCutoffs.LowKey, 10m));

        Assert.Equal(Defaults, await sut.GetLevelCutoffsAsync(Tenant, CancellationToken.None));
        Assert.Equal(new ScoreLevelCutoffs(10m, 60m, 80m), await sut.GetLevelCutoffsAsync(OtherTenant, CancellationToken.None));
    }

    // ── ScoreBreakdownJson ──────────────────────────────────────────────────────

    /// <summary>levelCutoffs is top-level, at the same nesting as scoringAlgoVersion, directly before it.</summary>
    [Fact]
    public async Task Breakdown_CarriesLevelCutoffsTopLevel_DirectlyBeforeScoringAlgoVersion()
    {
        var result = await ScoreBaselineAsync(await BuildAsync(Cutoffs(30m, 55m, 75m)));
        var json   = AnalysisScoringStage.BuildScoreBreakdownJson(result, null);

        var keys = JsonNode.Parse(json)!.AsObject().Select(p => p.Key).ToList();
        int at   = keys.IndexOf("levelCutoffs");
        Assert.True(at >= 0, "levelCutoffs must be a top-level key");
        Assert.Equal("scoringAlgoVersion", keys[at + 1]);
        Assert.Equal("reliabilityFacts", keys[^1]);

        Assert.Equal(new ScoreLevelCutoffs(30m, 55m, 75m), ReadCutoffs(json));

        using var doc = JsonDocument.Parse(json);
        var node = doc.RootElement.GetProperty("levelCutoffs");
        Assert.Equal(new[] { "low", "medium", "high" }, node.EnumerateObject().Select(p => p.Name));
    }

    /// <summary>The engine-failed placeholder classifies nothing, so it records nothing.</summary>
    [Fact]
    public void Breakdown_OfAResultWithoutCutoffs_WritesNull()
    {
        var json = AnalysisScoringStage.BuildScoreBreakdownJson(new ScoringResult { Score = 50m, Level = "Medium" }, null);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("levelCutoffs").ValueKind);
        Assert.Null(ReadCutoffs(json));
    }

    // ── Rows persisted before the key existed ───────────────────────────────────

    public static TheoryData<string> OldBlobs => new()
    {
        // A pre-version-key row, as RescoreTestWorld seeds it.
        """{"scoringAlgoVersion":"8"}""",
        // A version 10 row as it was persisted until this change (no levelCutoffs key).
        """{"scoreRatios":21.00,"scoreBcra":21.00,"scoreMacro":30,"breakdown":{"roa":{"score":0,"status":"Saludable","excluded":false,"effectiveWeight":0.08}},"scoreBeforeFloor":22.35,"appliedFloor":0,"criticalRatios":[],"sectorProfile":"Comercio","bcraExcluded":false,"bcraExclusionReason":null,"effectiveWeightRatios":0.50,"effectiveWeightBcra":0.35,"effectiveWeightMacro":0.15,"nonComputableRatios":[],"ratiosLowCoverageFallback":false,"ratiosSurvivingWeight":1.00,"profitabilityFloorWaived":false,"profitabilityFloorWaivedRatios":[],"operatingResultSign":null,"scoringAlgoVersion":"10","reliabilityFacts":null}""",
    };

    [Theory]
    [MemberData(nameof(OldBlobs))]
    public void AnOldRow_ReadsWithLevelCutoffsNull(string blob)
    {
        Assert.Null(ReadCutoffs(blob));
        Assert.False(JsonNode.Parse(blob)!.AsObject().ContainsKey("levelCutoffs"));
    }

    /// <summary>
    /// The typed readers of the blob (the analyses list, the reliability block behind the detail, the
    /// dashboard, the narrative and the PDF) read an old row and a new row alike: the added key breaks
    /// none of them.
    /// </summary>
    [Theory]
    [MemberData(nameof(OldBlobs))]
    public async Task TypedReaders_ReadOldAndNewBlobsAlike(string oldBlob)
    {
        var scored  = await ScoreBaselineAsync(await BuildAsync(Cutoffs(30m, 55m, 75m)));
        var newBlob = AnalysisScoringStage.BuildScoreBreakdownJson(scored, null);

        foreach (var blob in new[] { oldBlob, newBlob })
        {
            Assert.Equal(ScoreReliabilityBreakdownState.Parsed, ScoreReliabilityAdapter.Parse(blob).State);
            Assert.Equal(ScoreReliabilityBreakdownState.Parsed, AnalysisController.TryDeserializeListBreakdown(blob).State);
        }
    }
}
