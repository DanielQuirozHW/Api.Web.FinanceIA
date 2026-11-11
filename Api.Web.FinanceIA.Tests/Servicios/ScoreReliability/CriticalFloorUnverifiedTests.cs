// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-02

using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Api.Web.FinanceIA.Servicios.BalanceCorrections;
using Api.Web.FinanceIA.Servicios.ScoreReliability;
using Xunit;
using static Api.Web.FinanceIA.Tests.Servicios.ScoreReliability.ScoreReliabilityFixture;
using Codes = Api.Web.FinanceIA.Servicios.ScoreReliability.ScoreReliabilityCodes;
using Harness = Api.Web.FinanceIA.Tests.Servicios.ScoringTestHarness;

namespace Api.Web.FinanceIA.Tests.Servicios.ScoreReliability;

/// <summary>
/// Score credibility 1b-5: <c>critical_floor_unverified</c> — the critical floor fixed the score from a ratio
/// computed with figures that do not add up — and the decision requirements derived from the same verdict.
/// Every breakdown is written by the REAL <see cref="AnalysisScoringStage.BuildScoreBreakdownJson"/>.
/// </summary>
public sealed class CriticalFloorUnverifiedTests
{
    private static ScoreReliabilityResponse Verdict(ScoringResult scoring, ScoreReliabilityFacts facts, ScoreReliabilityRow? row = null)
        => Evaluate(row ?? Row(score: scoring.Score), Json(scoring, facts))!;

    private static ScoreReliabilityFacts FailedOver(IReadOnlyList<string>? affected)
        => CleanFacts() with { Integrity = FailedIntegrity(affected) };

    private static IReadOnlyList<string> CodesOf(ScoreReliabilityResponse verdict)
        => verdict.Reasons.Select(r => r.Code).ToList();

    // ── The rule ──────────────────────────────────────────────────────────────

    [Fact]
    public void Floor_applied_on_failed_integrity_over_a_critical_ratio_raises_it_beside_integrity_failed()
    {
        var verdict = Verdict(FlooredScoring("liquidez_corriente"), FailedOver(["liquidez_corriente", "prueba_acida"]));

        Assert.Equal([Codes.IntegrityFailed, Codes.CriticalFloorUnverified], CodesOf(verdict));
        Assert.Equal(ScoreReliabilityLevels.Provisional, verdict.Level);

        var reason = verdict.Reasons.Single(r => r.Code == Codes.CriticalFloorUnverified);
        Assert.Equal(ScoreReliabilitySeverities.Provisional, reason.Severity);
        Assert.Equal(["liquidez_corriente"], reason.Args!.RatioKeys);
        Assert.Equal(820, reason.Args.Score);
        Assert.Equal(
            "El score está fijado por el piso crítico de Liquidez corriente, calculado con cifras del balance que no cierran. " +
            "Sin ese piso sería 820/1000. Corregí las cifras o confirmalas contra el documento antes de decidir.",
            reason.Message);
    }

    [Fact]
    public void Only_the_critical_ratios_the_failed_lines_touch_are_named_in_portal_order()
    {
        var verdict = Verdict(FlooredScoring("roe", "prueba_acida", "liquidez_corriente"),
                              FailedOver(["prueba_acida", "liquidez_corriente", "endeudamiento"]));

        var reason = verdict.Reasons.Single(r => r.Code == Codes.CriticalFloorUnverified);
        Assert.Equal(["liquidez_corriente", "prueba_acida"], reason.Args!.RatioKeys);
        Assert.StartsWith("El score está fijado por el piso crítico de Liquidez corriente y Prueba ácida, calculados con", reason.Message);
    }

    [Fact]
    public void A_critical_ratio_the_failed_checks_do_not_touch_raises_nothing()
    {
        var verdict = Verdict(FlooredScoring("liquidez_corriente"), FailedOver(["roe", "margen_neto"]));

        Assert.Equal([Codes.IntegrityFailed], CodesOf(verdict));
    }

    [Fact]
    public void An_empty_affected_list_is_known_and_raises_nothing()
        => Assert.Equal([Codes.IntegrityFailed], CodesOf(Verdict(FlooredScoring("liquidez_corriente"), FailedOver([]))));

    [Fact]
    public void A_floor_that_did_not_bind_raises_nothing()
    {
        // Critical, but the composite already exceeded its floor (65 >= 60): the score was not fixed by it.
        var notBinding = FlooredScoring("liquidez_corriente");
        notBinding.ScoreBeforeFloor = 65m;
        notBinding.Score            = 65m;

        Assert.Equal([Codes.IntegrityFailed], CodesOf(Verdict(notBinding, FailedOver(["liquidez_corriente"]))));
    }

    [Fact]
    public void No_critical_ratio_raises_nothing()
        => Assert.Equal([Codes.IntegrityFailed],
                        CodesOf(Verdict(CleanScoring(), FailedOver(["liquidez_corriente"]))));

    [Fact]
    public void Passed_integrity_raises_nothing_even_under_a_floor()
    {
        var verdict = Verdict(FlooredScoring("liquidez_corriente"), CleanFacts());

        Assert.Equal(ScoreReliabilityLevels.Complete, verdict.Level);
        Assert.Empty(verdict.Reasons);
    }

    [Fact]
    public void Integrity_that_never_ran_is_unverified_only()
    {
        var facts = CleanFacts() with
        {
            Integrity = new ScoreReliabilityIntegrityFacts
            {
                Passed = false, Verified = false, FailedChecks = 0, NotVerifiedReasons = ["integrity_unavailable"], AffectedRatios = [],
            },
        };

        Assert.Equal([Codes.IntegrityUnverified], CodesOf(Verdict(FlooredScoring("liquidez_corriente"), facts)));
    }

    [Fact]
    public void Facts_recorded_before_affected_ratios_fall_back_to_every_critical_ratio()
    {
        // A version-2 block: integrity failed, but which ratios it touched was never recorded.
        var verdict = Verdict(FlooredScoring("prueba_acida", "liquidez_corriente"), FailedOver(affected: null));

        var reason = verdict.Reasons.Single(r => r.Code == Codes.CriticalFloorUnverified);
        Assert.Equal(["liquidez_corriente", "prueba_acida"], reason.Args!.RatioKeys);
    }

    [Fact]
    public void A_blob_without_score_before_floor_keeps_the_older_test_and_drops_the_number()
    {
        var root = JsonNode.Parse(Json(FlooredScoring("liquidez_corriente"), FailedOver(["liquidez_corriente"])))!.AsObject();
        root.Remove("scoreBeforeFloor");

        var verdict = Evaluate(Row(score: 60m), root.ToJsonString())!;

        var reason = verdict.Reasons.Single(r => r.Code == Codes.CriticalFloorUnverified);
        Assert.Null(reason.Args!.Score);
        Assert.DoesNotContain("Sin ese piso", reason.Message, StringComparison.Ordinal);
        Assert.Equal(
            "El score quedó fijado por el piso crítico de Liquidez corriente, calculado con cifras del balance que no superaron " +
            "los controles de consistencia.",
            ScoreReliabilityPromptBlock.Phrase(reason));
    }

    [Fact]
    public void Persona_humana_has_no_verdict_and_no_requirements()
    {
        var verdict = Evaluate(Row(kind: AnalysisKinds.PersonaHumana),
                               Json(FlooredScoring("liquidez_corriente"), FailedOver(["liquidez_corriente"])));

        Assert.Null(verdict);
        Assert.Null(DecisionReviewGate.RequirementsFor(verdict));
    }

    [Fact]
    public void The_list_reads_the_same_verdict_from_its_own_narrow_parse()
    {
        var json = Json(FlooredScoring("liquidez_corriente"), FailedOver(["liquidez_corriente"]));
        var row  = Row(score: 60m);

        var (list, state) = AnalysisController.TryDeserializeListBreakdown(json);
        var fromList = ScoreReliabilityAdapter.Evaluate(row, list!.ToReliabilityBreakdown(), state).Reliability;

        Assert.Equal(Wire(Evaluate(row, json)), Wire(fromList));
        Assert.Contains(fromList!.Reasons, r => r.Code == Codes.CriticalFloorUnverified);
    }

    // ── Decision requirements ─────────────────────────────────────────────────

    [Fact]
    public void Requirements_follow_the_level_and_integrity_failed()
    {
        var complete = DecisionReviewGate.RequirementsFor(Verdict(CleanScoring(), CleanFacts()))!;
        Assert.False(complete.AcknowledgeCaveats);
        Assert.False(complete.FiguresReviewNote);
        Assert.False(complete.RejectionReasonRequired);

        // with_caveats asks nothing (review-gate-calm).
        var caveat = DecisionReviewGate.RequirementsFor(Verdict(CleanScoring(), CleanFacts() with { MacroImputed = true }))!;
        Assert.False(caveat.AcknowledgeCaveats);
        Assert.False(caveat.FiguresReviewNote);
        Assert.False(caveat.RejectionReasonRequired);

        var failed = DecisionReviewGate.RequirementsFor(Verdict(FlooredScoring("liquidez_corriente"), FailedOver(["liquidez_corriente"])))!;
        Assert.True(failed.AcknowledgeCaveats);
        Assert.True(failed.FiguresReviewNote);
        Assert.True(failed.RejectionReasonRequired);

        var none = DecisionReviewGate.RequirementsFor(Evaluate(Row(score: null), Json()))!;
        Assert.True(none.AcknowledgeCaveats);
        Assert.False(none.FiguresReviewNote);
        Assert.False(none.RejectionReasonRequired);
    }

    // ── The HSE shape, end to end through the real engine and the real correction ──────────────

    /// <summary>
    /// A SYNTHETIC balance in the shape of the HSE case — never a real client file. A rotated scan made the
    /// reading take pasivo corriente as 500.000.000 instead of 61.000.000: current_liabilities_items and
    /// liabilities_components fail, liquidez reads 0.40 instead of 3.28, and its critical floor fixes the score.
    /// </summary>
    private static string HseBalanceJson(double currentLiabilities)
    {
        var root = new JsonObject
        {
            ["assets"]                  = 500_000_000d,
            ["current_assets"]          = 200_000_000d,
            ["non_current_assets"]      = 300_000_000d,
            ["liabilities"]             = 111_000_000d,
            ["current_liabilities"]     = currentLiabilities,
            ["non_current_liabilities"] = 50_000_000d,
            ["equity"]                  = 389_000_000d,
            ["net_revenue"]             = 900_000_000d,
            ["net_profit"]              = 60_000_000d,
            ["inventory"]               = 30_000_000d,
            ["operating_result"]        = 100_000_000d,
            ["financial_expenses"]      = 10_000_000d,
            ["fiscal_year_detected"]    = 2025,
            ["amounts_unit"]            = "pesos",
            ["result_before_tax"]       = 90_000_000d,
            ["income_tax"]              = -30_000_000d,
            ["current_liabilities_items"] = new JsonArray
            {
                new JsonObject { ["label"] = "Deudas comerciales", ["amount"] = 41_000_000d },
                new JsonObject { ["label"] = "Deudas fiscales",    ["amount"] = 20_000_000d },
            },
            ["non_current_liabilities_items"] = new JsonArray
            {
                new JsonObject { ["label"] = "Deudas bancarias", ["amount"] = 50_000_000d },
            },
        };

        var balance = new ExtractedBalance
        {
            Current                    = root.Deserialize<BalanceAccounts>()!,
            CurrentLiabilitiesItems    = root["current_liabilities_items"]!.Deserialize<List<BalanceLineItem>>(),
            NonCurrentLiabilitiesItems = root["non_current_liabilities_items"]!.Deserialize<List<BalanceLineItem>>(),
            ResultBeforeTax            = 90_000_000d,
            IncomeTax                  = -30_000_000d,
            AppliedScale               = 1d,
        };

        var report = new BalanceIntegrityService().Evaluate(balance).Report with
        {
            AmountsUnit        = BalanceAmountsUnit.Pesos,
            AmountsUnitAssumed = false,
        };
        root["integrity"] = JsonSerializer.SerializeToNode(report);
        return root.ToJsonString();
    }

    /// <summary>Healthy ratios except the two that divide by pasivo corriente.</summary>
    private static RatiosData HseRatios(double currentRatio, double acidTest) => new()
    {
        CurrentRatio     = currentRatio,
        AcidTest         = acidTest,
        DebtRatio        = 0.22,
        Leverage         = 0.29,
        InterestCoverage = 10.0,
        Roe              = 15.4,
        Roa              = 12.0,
        NetMargin        = 6.7,
    };

    private static async Task<(ScoreReliabilityResponse Verdict, ScoringResult Scoring)> ScoreAsync(string balanceJson, RatiosData ratios)
    {
        var bcra    = Harness.BcraNoDebt();
        var scoring = await Harness.Score(await Harness.BuildAsync(), ratios, bcra, Harness.MacroBenign(), "Comercio");
        var facts   = ScoreReliabilityFactsBuilder.Build(scoring, new ScoreReliabilityInputs(
            RatiosComputed: true, BalanceJson: balanceJson, FiscalYear: 2025, BcraData: bcra,
            Lufe: null, LufeSnapshotAt: null, Casfog: null));

        var verdict = Evaluate(
            Row(score: scoring.Score, fiscalYear: 2025),
            AnalysisScoringStage.BuildScoreBreakdownJson(scoring, facts))!;
        return (verdict, scoring);
    }

    [Fact]
    public async Task Hse_misread_pasivo_corriente_is_floored_unverified_and_a_correction_clears_both_reasons()
    {
        // ── As read: liquidez 0.40, the floor binds ─────────────────────────────
        var misread = HseBalanceJson(currentLiabilities: 500_000_000d);
        var integrity = ExtractionIntegrityReader.Read(misread, 2025)!;
        Assert.False(integrity.Passed);
        Assert.Contains("liquidez_corriente", integrity.AffectedRatios);

        var (before, scoring) = await ScoreAsync(misread, HseRatios(currentRatio: 0.40, acidTest: 0.34));

        Assert.Contains("liquidez_corriente", scoring.CriticalRatios);
        Assert.True(scoring.ScoreBeforeFloor < scoring.AppliedFloor);   // the floor moved the score
        var shownBefore = ScoreScale.ToNosis(scoring.ScoreBeforeFloor);
        Assert.InRange(shownBefore, 780, 860);                           // ~820/1000 without the floor
        Assert.Equal(400, ScoreScale.ToNosis(scoring.Score));            // the liquidez floor of 60

        Assert.Contains(Codes.IntegrityFailed, CodesOf(before));
        var reason = before.Reasons.Single(r => r.Code == Codes.CriticalFloorUnverified);
        Assert.Contains("liquidez_corriente", reason.Args!.RatioKeys);
        Assert.Contains($"Sin ese piso sería {shownBefore}/1000.", reason.Message, StringComparison.Ordinal);

        var required = DecisionReviewGate.RequirementsFor(before)!;
        Assert.True(required.AcknowledgeCaveats);
        Assert.True(required.FiguresReviewNote);

        // ── Corrected by the analyst: the real applier re-evaluates integrity, the re-score rebuilds facts ──
        var command = new BalanceCorrectionCommand(
            "El pasivo corriente del balance es 61.000.000; la lectura tomó otra cifra.",
            [new BalanceCorrectionEntry(BalanceCorrectionPath.TryParse("current_liabilities")!, 61_000_000d)]);
        var corrected = BalanceCorrectionApplier.Apply(
            misread, command, Guid.NewGuid(), ScoreDate, new BalanceIntegrityService());
        Assert.True(corrected.IntegrityPassed);

        var (after, rescored) = await ScoreAsync(corrected.BalanceJson!, HseRatios(currentRatio: 3.28, acidTest: 2.79));

        Assert.False(rescored.ScoreBeforeFloor < rescored.AppliedFloor);
        Assert.DoesNotContain(Codes.IntegrityFailed, CodesOf(after));
        Assert.DoesNotContain(Codes.CriticalFloorUnverified, CodesOf(after));
        Assert.False(DecisionReviewGate.RequirementsFor(after)!.FiguresReviewNote);
    }
}
