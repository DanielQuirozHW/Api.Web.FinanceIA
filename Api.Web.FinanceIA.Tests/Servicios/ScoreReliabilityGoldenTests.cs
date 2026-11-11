// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-02

using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Xunit;
using static Api.Web.FinanceIA.Tests.Servicios.ScoringTestHarness;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Golden pins for score credibility 1b-2a: recording the reliability facts moves no score and
/// changes nothing else in <c>ScoreBreakdownJson</c>. Each expected blob below was CAPTURED from
/// <c>AnalysisScoringStage.BuildScoreBreakdownJson</c> on DEVELOP @ ab19442, before the change, for the
/// same fixture. After the change the blob must equal it byte-for-byte once <c>reliabilityFacts</c> is
/// removed — and, on the low-coverage fallback path, once the new per-ratio <c>measured:false</c> is.
/// <para>
/// Scoring algorithm 10 edited the captures deliberately, and only in two ways: <c>scoringAlgoVersion</c>
/// reads "10", and the three keys it added — <c>profitabilityFloorWaived</c>,
/// <c>profitabilityFloorWaivedRatios</c>, <c>operatingResultSign</c> — sit before it at their neutral
/// values. No fixture here has zero current liabilities or a waived floor, so every score is unchanged.
/// </para>
/// <para>
/// The score level contract (reporting only, no algorithm bump) edited them once more, in one way: the
/// top-level <c>levelCutoffs</c> — the cutoffs the level was classified with, the defaults 35 / 60 / 80
/// for these unconfigured fixtures — sits directly before <c>scoringAlgoVersion</c>. Every score and
/// level is unchanged.
/// </para>
/// <para>
/// Scoring algorithm 11 edited them deliberately, in three ways: <c>scoringAlgoVersion</c> reads "11",
/// <c>criticalFloors.prueba_acida</c> reads the new default 50 (was 60), and the two keys it added —
/// <c>acidTestFloorWaived</c>, <c>currentRatioVsWarn</c> — sit right after <c>operatingResultSign</c> at
/// their neutral values. No fixture here has a critical acid test, so every score is unchanged.
/// </para>
/// </summary>
public sealed class ScoreReliabilityGoldenTests
{
    // ── Captured on ab19442 ─────────────────────────────────────────────────────

    private const decimal FullScore = 34.06m;
    private const string  FullLevel = "Low";
    private const string  FullBreakdown = """
        {"scoreRatios":21.00,"scoreBcra":21.00,"scoreMacro":30,"breakdown":{"liquidez_corriente":{"score":0,"status":"Saludable","excluded":false,"effectiveWeight":0.20},"prueba_acida":{"score":0,"status":"Saludable","excluded":false,"effectiveWeight":0.10},"endeudamiento":{"score":50,"status":"Atencion","excluded":false,"effectiveWeight":0.20},"leverage":{"score":0,"status":"Saludable","excluded":false,"effectiveWeight":0.10},"cobertura_intereses":{"score":50,"status":"Atencion","excluded":false,"effectiveWeight":0.15},"roe":{"score":0,"status":"Saludable","excluded":false,"effectiveWeight":0.10},"roa":{"score":0,"status":"Saludable","excluded":false,"effectiveWeight":0.08},"margen_neto":{"score":50,"status":"Atencion","excluded":false,"effectiveWeight":0.07}},"scoreBeforeFloor":22.35,"appliedFloor":0,"criticalRatios":[],"criticalFloorTrigger":100.00,"criticalFloors":{"liquidez_corriente":60.00,"prueba_acida":50.00,"endeudamiento":65.00,"leverage":65.00,"cobertura_intereses":70.00,"roe":55.00,"roa":55.00,"margen_neto":55.00},"thresholdFallbacks":[],"bcraTrendDelta":-1,"bcraTrendAdj":-5.00,"chequesAdj":6.00,"chequesProporcionRecuperada":0.6,"ventasAdj":10.00,"ventasVariacion":-0.4052,"ventasBase":"interanual","ventasVariacionEsReal":true,"casfogMoraAdj":1.71,"casfogProporcionMora":0.1,"sectorProfile":"Comercio","bcraExcluded":false,"bcraExclusionReason":null,"effectiveWeightRatios":0.50,"effectiveWeightBcra":0.35,"effectiveWeightMacro":0.15,"nonComputableRatios":[],"ratiosLowCoverageFallback":false,"ratiosSurvivingWeight":1.00,"profitabilityFloorWaived":false,"profitabilityFloorWaivedRatios":[],"operatingResultSign":null,"acidTestFloorWaived":false,"currentRatioVsWarn":null,"levelCutoffs":{"low":35,"medium":60,"high":80},"scoringAlgoVersion":"11"}
        """;

    private const decimal RenormaliseScore = 17.62m;
    private const string  RenormaliseLevel = "Low";
    private const string  RenormaliseBreakdown = """
        {"scoreRatios":26.25,"scoreBcra":0,"scoreMacro":30,"breakdown":{"liquidez_corriente":{"score":0,"status":"Saludable","excluded":false,"effectiveWeight":0.25},"prueba_acida":{"score":0,"status":"Saludable","excluded":false,"effectiveWeight":0.125},"endeudamiento":{"score":50,"status":"Atencion","excluded":false,"effectiveWeight":0.25},"leverage":{"score":50,"status":"Atencion","excluded":true,"effectiveWeight":0,"exclusionReason":"not_meaningful"},"cobertura_intereses":{"score":50,"status":"Atencion","excluded":false,"effectiveWeight":0.1875},"roe":{"score":50,"status":"Atencion","excluded":true,"effectiveWeight":0,"exclusionReason":"not_meaningful"},"roa":{"score":0,"status":"Saludable","excluded":false,"effectiveWeight":0.1},"margen_neto":{"score":50,"status":"Atencion","excluded":false,"effectiveWeight":0.0875}},"scoreBeforeFloor":17.62,"appliedFloor":0,"criticalRatios":[],"criticalFloorTrigger":100.00,"criticalFloors":{"liquidez_corriente":60.00,"prueba_acida":50.00,"endeudamiento":65.00,"leverage":65.00,"cobertura_intereses":70.00,"roe":55.00,"roa":55.00,"margen_neto":55.00},"thresholdFallbacks":[],"bcraTrendDelta":0,"bcraTrendAdj":0,"chequesAdj":0,"chequesProporcionRecuperada":null,"ventasAdj":0,"ventasVariacion":null,"ventasBase":null,"ventasVariacionEsReal":null,"casfogMoraAdj":0,"casfogProporcionMora":null,"sectorProfile":"General","bcraExcluded":false,"bcraExclusionReason":null,"effectiveWeightRatios":0.50,"effectiveWeightBcra":0.35,"effectiveWeightMacro":0.15,"nonComputableRatios":["leverage","roe"],"ratiosLowCoverageFallback":false,"ratiosSurvivingWeight":0.80,"profitabilityFloorWaived":false,"profitabilityFloorWaivedRatios":[],"operatingResultSign":null,"acidTestFloorWaived":false,"currentRatioVsWarn":null,"levelCutoffs":{"low":35,"medium":60,"high":80},"scoringAlgoVersion":"11"}
        """;

    private const decimal FallbackScore = 27.50m;
    private const string  FallbackLevel = "Low";
    private const string  FallbackBreakdown = """
        {"scoreRatios":46.00,"scoreBcra":0,"scoreMacro":30,"breakdown":{"liquidez_corriente":{"score":50,"status":"Atencion","excluded":false,"effectiveWeight":0.20},"prueba_acida":{"score":50,"status":"Atencion","excluded":false,"effectiveWeight":0.10},"endeudamiento":{"score":50,"status":"Atencion","excluded":false,"effectiveWeight":0.20},"leverage":{"score":50,"status":"Atencion","excluded":false,"effectiveWeight":0.10},"cobertura_intereses":{"score":50,"status":"Atencion","excluded":false,"effectiveWeight":0.15},"roe":{"score":50,"status":"Atencion","excluded":false,"effectiveWeight":0.10},"roa":{"score":0,"status":"Saludable","excluded":false,"effectiveWeight":0.08},"margen_neto":{"score":50,"status":"Atencion","excluded":false,"effectiveWeight":0.07}},"scoreBeforeFloor":27.50,"appliedFloor":0,"criticalRatios":[],"criticalFloorTrigger":100.00,"criticalFloors":{"liquidez_corriente":60.00,"prueba_acida":50.00,"endeudamiento":65.00,"leverage":65.00,"cobertura_intereses":70.00,"roe":55.00,"roa":55.00,"margen_neto":55.00},"thresholdFallbacks":[],"bcraTrendDelta":0,"bcraTrendAdj":0,"chequesAdj":0,"chequesProporcionRecuperada":null,"ventasAdj":0,"ventasVariacion":null,"ventasBase":null,"ventasVariacionEsReal":null,"casfogMoraAdj":0,"casfogProporcionMora":null,"sectorProfile":"General","bcraExcluded":false,"bcraExclusionReason":null,"effectiveWeightRatios":0.50,"effectiveWeightBcra":0.35,"effectiveWeightMacro":0.15,"nonComputableRatios":["liquidez_corriente","prueba_acida","endeudamiento","leverage","cobertura_intereses","roe"],"ratiosLowCoverageFallback":true,"ratiosSurvivingWeight":0.15,"profitabilityFloorWaived":false,"profitabilityFloorWaivedRatios":[],"operatingResultSign":null,"acidTestFloorWaived":false,"currentRatioVsWarn":null,"levelCutoffs":{"low":35,"medium":60,"high":80},"scoringAlgoVersion":"11"}
        """;

    // ── Fixtures (exactly the ones the capture used) ────────────────────────────

    /// <summary>Every source fine and measured: all eight ratios, DebtFound with a trend, cheques, CPI, LUFE, CASFOG.</summary>
    private static async Task<ScoringResult> ScoreFullyMeasuredAsync()
        => await Score(await BuildAsync(), BaselineRatios(),
            BcraDebtFound(2, Historial(("202409", 2), ("202403", 3)), ChequesFound(100_000m, 40_000m)),
            MacroConIpc(IpcPlano(2.5, "202601", 30)),
            "Comercio",
            VentasInteranual(-0.20m),
            GarantiasProporcion(0.10m));

    /// <summary>Leverage and ROE not_meaningful (equity &lt;= 0): the renormalise path.</summary>
    private static async Task<ScoringResult> ScoreRenormalisedAsync()
        => await Score(await BuildAsync(), new RatiosData
            {
                CurrentRatio = 1.5, AcidTest = 1.0, DebtRatio = 0.7, Roa = 8, NetMargin = 4, InterestCoverage = 1.5,
                ExclusionReasons = new Dictionary<string, string>
                {
                    ["leverage"] = RatioExclusionReasons.NotMeaningful,
                    ["roe"]      = RatioExclusionReasons.NotMeaningful,
                },
            },
            BcraNoDebt(), MacroBenign());

    /// <summary>Only ROA and net margin computable: the low-coverage fallback path.</summary>
    private static async Task<ScoringResult> ScoreFallbackAsync()
        => await Score(await BuildAsync(), new RatiosData { Roa = 8, NetMargin = 4 }, BcraNoDebt(), MacroBenign());

    private static ScoreReliabilityFacts AnyFacts(ScoringResult scoring)
        => ScoreReliabilityFactsBuilder.Build(scoring, new ScoreReliabilityInputs(
            RatiosComputed: true, BalanceJson: null, FiscalYear: 2024, BcraData: null,
            Lufe: null, LufeSnapshotAt: null, Casfog: null));

    /// <summary>The persisted blob with <c>reliabilityFacts</c> removed, re-serialised compactly in key order.</summary>
    private static string WithoutFacts(string breakdownJson, bool dropMeasured = false)
    {
        var root = JsonNode.Parse(breakdownJson)!.AsObject();
        Assert.True(root.Remove("reliabilityFacts"), "the breakdown must carry reliabilityFacts");

        if (dropMeasured)
        {
            foreach (var (_, entry) in root["breakdown"]!.AsObject())
                entry!.AsObject().Remove("measured");
        }

        return root.ToJsonString();
    }

    private static string Compact(string json) => JsonNode.Parse(json)!.ToJsonString();

    // ── The pins ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FullyMeasured_ScoresAndSerialisesExactlyAsBefore_PlusTheFactsBlock()
    {
        var scoring = await ScoreFullyMeasuredAsync();
        var json    = AnalysisScoringStage.BuildScoreBreakdownJson(scoring, AnyFacts(scoring));

        Assert.Equal(FullScore, scoring.Score);
        Assert.Equal(FullLevel, scoring.Level);
        Assert.Equal(Compact(FullBreakdown), WithoutFacts(json));
        Assert.Equal("11", scoring.ScoringAlgoVersion);
    }

    [Fact]
    public async Task FullyMeasured_FactsBlockIsTheLastKey_SoEveryEarlierByteIsUnchanged()
    {
        var scoring = await ScoreFullyMeasuredAsync();
        var json    = AnalysisScoringStage.BuildScoreBreakdownJson(scoring, AnyFacts(scoring));

        // The pre-change blob, minus its closing brace, is a literal prefix of the new one.
        var before = Compact(FullBreakdown);
        Assert.StartsWith(before[..^1] + ",\"reliabilityFacts\":{", json);
    }

    [Fact]
    public async Task Renormalise_IsByteIdentical_AndCarriesNoMeasuredKey()
    {
        var scoring = await ScoreRenormalisedAsync();
        var json    = AnalysisScoringStage.BuildScoreBreakdownJson(scoring, AnyFacts(scoring));

        Assert.Equal(RenormaliseScore, scoring.Score);
        Assert.Equal(RenormaliseLevel, scoring.Level);
        Assert.Equal(Compact(RenormaliseBreakdown), WithoutFacts(json));
        Assert.DoesNotContain("\"measured\"", json);
    }

    [Fact]
    public async Task Fallback_DiffersOnlyByMeasuredFalseOnTheNonComputableRatios()
    {
        var scoring = await ScoreFallbackAsync();
        var json    = AnalysisScoringStage.BuildScoreBreakdownJson(scoring, AnyFacts(scoring));

        Assert.Equal(FallbackScore, scoring.Score);
        Assert.Equal(FallbackLevel, scoring.Level);
        Assert.True(scoring.RatiosLowCoverageFallback);
        Assert.Equal(Compact(FallbackBreakdown), WithoutFacts(json, dropMeasured: true));
    }

    [Fact]
    public async Task Fallback_MarksExactlyTheNonComputableRatiosUnmeasured()
    {
        var scoring = await ScoreFallbackAsync();
        var json    = AnalysisScoringStage.BuildScoreBreakdownJson(scoring, AnyFacts(scoring));

        using var doc = JsonDocument.Parse(json);
        foreach (var entry in doc.RootElement.GetProperty("breakdown").EnumerateObject())
        {
            bool computable = entry.Name is "roa" or "margen_neto";
            if (computable)
            {
                Assert.False(entry.Value.TryGetProperty("measured", out _), entry.Name);
            }
            else
            {
                Assert.False(entry.Value.GetProperty("measured").GetBoolean());
                // Reporting only: still not excluded, still the neutral 50 at its nominal weight.
                Assert.False(entry.Value.GetProperty("excluded").GetBoolean());
                Assert.Equal(50m, entry.Value.GetProperty("score").GetDecimal());
            }
        }
    }

    [Fact]
    public async Task AllComputable_CarriesNoMeasuredKey()
    {
        var scoring = await Score(await BuildAsync(), BaselineRatios(), BcraNoDebt(), MacroBenign());

        Assert.All(scoring.Breakdown.Values, entry => Assert.Null(entry.Measured));
        Assert.DoesNotContain("\"measured\"", AnalysisScoringStage.BuildScoreBreakdownJson(scoring, AnyFacts(scoring)));
    }

    [Fact]
    public async Task UnbuildableFacts_PersistAsNull_NotAMissingKey()
    {
        var scoring = await ScoreFullyMeasuredAsync();

        using var doc = JsonDocument.Parse(AnalysisScoringStage.BuildScoreBreakdownJson(scoring, null));
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("reliabilityFacts").ValueKind);
    }
}
