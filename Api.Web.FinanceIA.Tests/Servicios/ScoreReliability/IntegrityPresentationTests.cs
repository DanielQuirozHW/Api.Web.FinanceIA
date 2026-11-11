// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-03

using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Api.Web.FinanceIA.Servicios.ScoreReliability;
using Xunit;
using static Api.Web.FinanceIA.Tests.Servicios.ScoreReliability.ScoreReliabilityFixture;
using Codes = Api.Web.FinanceIA.Servicios.ScoreReliability.ScoreReliabilityCodes;

namespace Api.Web.FinanceIA.Tests.Servicios.ScoreReliability;

/// <summary>
/// Reliability policy 5: a failure of ONLY <c>net_profit_reconciliation</c> (RECPAM after the tax, no pre-tax
/// subtotal, a subtotal without the financial results) is a presentation caveat, <c>integrity_presentation</c> —
/// no <c>integrity_failed</c>, no <c>critical_floor_unverified</c>, no figures-review requirement. Any other failed
/// check, alone or beside it, and any block recorded without the check names, behaves exactly as before. Every
/// breakdown is written by the REAL <see cref="Api.Web.FinanceIA.Servicios.AnalysisRuns.AnalysisScoringStage.BuildScoreBreakdownJson"/>.
/// </summary>
public sealed class IntegrityPresentationTests
{
    private const string NetProfit = BalanceIntegrityService.NetProfitReconciliationCheck;

    // The ratios a net_profit line feeds (BalanceIntegrityService.RatiosByLine).
    private static readonly IReadOnlyList<string> NetProfitRatios = ["roe", "roa", "margen_neto"];

    /// <summary>
    /// Verified integrity that failed exactly <paramref name="names"/>; null names = a pre-v4 block. A failed
    /// net_profit check is, by default, known NOT to be a sign flip, and its column mix is unknown (null — a block from
    /// before facts version 6, or no comparative column), which is the input every test before policy 6 had.
    /// </summary>
    private static ScoreReliabilityFacts FailedWith(
        IReadOnlyList<string>? names, int? count = null, bool verified = true, bool? signFlip = false, bool? columnMix = null)
        => CleanFacts() with
        {
            Integrity = CleanFacts().Integrity! with
            {
                Passed            = false,
                Verified          = verified,
                FailedChecks      = count ?? names?.Count ?? 1,
                FailedCheckNames  = names,
                NetProfitSignFlip = names?.Contains(NetProfit) == true ? signFlip : null,
                NetProfitColumnMix = names?.Contains(NetProfit) == true ? columnMix : null,
                AffectedRatios    = NetProfitRatios,
            },
        };

    private static ScoreReliabilityResponse Verdict(ScoreReliabilityFacts facts, ScoringResult? scoring = null)
        => Evaluate(Row(score: (scoring ?? CleanScoring()).Score), Json(scoring ?? CleanScoring(), facts))!;

    private static IReadOnlyList<string> CodesOf(ScoreReliabilityResponse verdict)
        => verdict.Reasons.Select(r => r.Code).ToList();

    // ── The verdict ───────────────────────────────────────────────────────────

    [Fact]
    public void Only_net_profit_failed_is_with_caveats_and_names_the_presentation()
    {
        var verdict = Verdict(FailedWith([NetProfit]));

        Assert.Equal(ScoreReliabilityLevels.WithCaveats, verdict.Level);
        Assert.Equal([Codes.IntegrityPresentation], CodesOf(verdict));
        Assert.Equal("6", verdict.PolicyVersion);  // policy 6: a column mix is not a presentation

        var reason = verdict.Reasons.Single();
        Assert.Equal(ScoreReliabilitySeverities.Caveat, reason.Severity);
        Assert.Null(reason.Args);
        // Re-pinned deliberately: the caveat names the possible causes and asserts none (it once claimed the RECPAM).
        Assert.Equal(
            "El resultado neto no concilia con el resultado antes de impuestos y el impuesto leídos: puede deberse a cómo " +
            "está presentado el estado de resultados (por ejemplo, el RECPAM después del impuesto) o a una cifra tomada de " +
            "la columna del ejercicio anterior. Las cifras del balance cierran.",
            reason.Message);
    }

    [Fact]
    public void Only_net_profit_failed_under_a_binding_floor_on_roe_raises_no_critical_floor_unverified()
    {
        var verdict = Verdict(FailedWith([NetProfit]), FlooredScoring("roe"));

        Assert.Equal([Codes.IntegrityPresentation], CodesOf(verdict));
        Assert.Equal(ScoreReliabilityLevels.WithCaveats, verdict.Level);
    }

    [Fact]
    public void Only_net_profit_failed_takes_no_decision_requirement()
    {
        var requirements = DecisionReviewGate.RequirementsFor(Verdict(FailedWith([NetProfit]), FlooredScoring("roe")))!;

        Assert.False(requirements.AcknowledgeCaveats);
        Assert.False(requirements.FiguresReviewNote);
        Assert.False(requirements.RejectionReasonRequired);
    }

    [Theory]
    [InlineData(BalanceIntegrityService.CurrentAssetsItemsCheck)]
    [InlineData(BalanceIntegrityService.ColumnSwapCheck)]
    [InlineData(BalanceIntegrityService.SignNetRevenueCheck)]
    public void Net_profit_beside_any_other_failed_check_is_provisional_as_before(string other)
    {
        var verdict = Verdict(FailedWith([NetProfit, other]), FlooredScoring("roe"));

        Assert.Equal(ScoreReliabilityLevels.Provisional, verdict.Level);
        Assert.Equal([Codes.IntegrityFailed, Codes.CriticalFloorUnverified], CodesOf(verdict));
        Assert.Equal(ScoreReliabilityMessages.IntegrityFailed(2), verdict.Reasons[0].Message);

        var requirements = DecisionReviewGate.RequirementsFor(verdict)!;
        Assert.True(requirements.AcknowledgeCaveats);
        Assert.True(requirements.FiguresReviewNote);
        Assert.True(requirements.RejectionReasonRequired);
    }

    [Fact]
    public void Another_check_failing_alone_is_provisional_as_before()
    {
        var verdict = Verdict(FailedWith([BalanceIntegrityService.AccountingEquationCheck]));

        Assert.Equal([Codes.IntegrityFailed], CodesOf(verdict));
        Assert.Equal(ScoreReliabilityLevels.Provisional, verdict.Level);
    }

    [Fact]
    public void A_block_recorded_before_the_names_keeps_integrity_failed()
    {
        // Facts version 3: one failed check, no names. It may have been net_profit or a column swap of it.
        var verdict = Verdict(FailedWith(names: null, count: 1), FlooredScoring("roe"));

        Assert.Equal([Codes.IntegrityFailed, Codes.CriticalFloorUnverified], CodesOf(verdict));
        Assert.True(DecisionReviewGate.RequirementsFor(verdict)!.FiguresReviewNote);
    }

    [Fact]
    public void An_unverified_balance_still_says_so_beside_the_presentation_caveat()
    {
        var verdict = Verdict(FailedWith([NetProfit], verified: false));

        Assert.Equal([Codes.IntegrityUnverified, Codes.IntegrityPresentation], CodesOf(verdict));
        Assert.Equal(ScoreReliabilityLevels.Provisional, verdict.Level);
        Assert.False(DecisionReviewGate.RequirementsFor(verdict)!.FiguresReviewNote);
    }

    [Fact]
    public void A_passed_integrity_raises_neither()
        => Assert.Empty(Verdict(CleanFacts() with
        {
            Integrity = CleanFacts().Integrity! with { FailedCheckNames = [] },
        }).Reasons);

    // ── The predicate ─────────────────────────────────────────────────────────

    // The column-mix argument (policy 6) is null — "could not be checked", today's input — on every pre-existing row;
    // the rows after them pin the mix: true is a misread, false and null keep the verdict the flip gives.
    [Theory]
    [InlineData(new[] { NetProfit }, false, null, true)]
    [InlineData(new[] { NetProfit }, true,  null, false)]   // a sign flip is a misread
    [InlineData(new[] { NetProfit }, null,  null, false)]   // an unknown flip is not assumed away
    [InlineData(new[] { NetProfit, NetProfit }, false, null, true)]
    [InlineData(new[] { NetProfit, BalanceIntegrityService.AssetsComponentsCheck }, false, null, false)]
    [InlineData(new[] { BalanceIntegrityService.SignInventoryCheck }, null, null, false)]
    [InlineData(new string[0], false, null, false)]
    [InlineData(new[] { NetProfit }, false, true,  false)]  // a column mix is a misread
    [InlineData(new[] { NetProfit }, false, false, true)]   // checked, not a mix: still a presentation
    [InlineData(new[] { NetProfit }, true,  true,  false)]  // both misreads
    [InlineData(new[] { NetProfit }, null,  false, false)]  // the mix never buys out an unknown flip
    [InlineData(new[] { BalanceIntegrityService.SignInventoryCheck }, null, true, false)]
    public void Fails_only_on_presentation(string[] failed, bool? signFlip, bool? columnMix, bool expected)
        => Assert.Equal(expected, BalanceIntegrityService.FailsOnlyOnPresentation(failed, signFlip, columnMix));

    [Fact]
    public void Unknown_names_are_not_a_presentation_failure()
        => Assert.False(BalanceIntegrityService.FailsOnlyOnPresentation(null, false, null));

    // ── The sign-flip guard ───────────────────────────────────────────────────

    [Fact]
    public void A_recorded_sign_flip_keeps_integrity_failed_and_the_floor_reason()
    {
        var verdict = Verdict(FailedWith([NetProfit], signFlip: true), FlooredScoring("roe"));

        Assert.Equal(ScoreReliabilityLevels.Provisional, verdict.Level);
        Assert.Equal([Codes.IntegrityFailed, Codes.CriticalFloorUnverified], CodesOf(verdict));
        Assert.True(DecisionReviewGate.RequirementsFor(verdict)!.FiguresReviewNote);
    }

    [Fact]
    public void An_unknown_sign_flip_keeps_integrity_failed()
        => Assert.Equal([Codes.IntegrityFailed, Codes.CriticalFloorUnverified],
                        CodesOf(Verdict(FailedWith([NetProfit], signFlip: null), FlooredScoring("roe"))));

    /// <summary>
    /// End to end: the REAL integrity checks over a statement, the report persisted the way the extraction persists
    /// it (root figures + integrity node), the REAL facts builder, and the REAL breakdown under a floor on ROE.
    /// </summary>
    private static ScoreReliabilityResponse VerdictFromStatement(
        double netProfit, double resultBeforeTax, double incomeTax, double? priorNetProfit = null)
    {
        var balance = new ExtractedBalance
        {
            Current = new BalanceAccounts
            {
                TotalAssets = 100_000_000d, CurrentAssets = 40_000_000d, NonCurrentAssets = 60_000_000d,
                TotalLiabilities = 30_000_000d, CurrentLiabilities = 20_000_000d, NonCurrentLiabilities = 10_000_000d,
                Equity = 70_000_000d, NetProfit = netProfit,
            },
            ResultBeforeTax = resultBeforeTax,
            IncomeTax       = incomeTax,
            // No comparative column unless asked: the input every test before policy 6 had.
            PriorYear       = priorNetProfit is { } prior ? new BalanceAccounts { NetProfit = prior } : null,
        };
        var report = new BalanceIntegrityService().Evaluate(balance).Report;

        var balanceJson = new JsonObject
        {
            ["net_profit"]        = netProfit,
            ["result_before_tax"] = resultBeforeTax,
            ["income_tax"]        = incomeTax,
            ["integrity"]         = JsonSerializer.SerializeToNode(report),
        }.ToJsonString();

        var built = ScoreReliabilityFactsBuilder.Build(CleanScoring(),
            new ScoreReliabilityInputs(true, balanceJson, 2025, null, null, null, null));

        return Verdict(CleanFacts() with { Integrity = built.Integrity }, FlooredScoring("roe"));
    }

    [Fact]
    public void A_net_profit_read_with_the_wrong_sign_is_provisional_with_integrity_failed_and_the_floor_reason()
    {
        // Result before tax −12.000 and a tax benefit of 2.000: the statement prints a loss of 10.000;
        // the reading lost the parentheses and reported a profit of 10.000.
        var verdict = VerdictFromStatement(netProfit: 10_000d, resultBeforeTax: -12_000d, incomeTax: 2_000d);

        Assert.Equal(ScoreReliabilityLevels.Provisional, verdict.Level);
        Assert.Equal([Codes.IntegrityFailed, Codes.CriticalFloorUnverified], CodesOf(verdict));
        Assert.Equal(["roe"], verdict.Reasons[1].Args!.RatioKeys);

        var requirements = DecisionReviewGate.RequirementsFor(verdict)!;
        Assert.True(requirements.FiguresReviewNote);
        Assert.True(requirements.RejectionReasonRequired);
    }

    // ── Column mix (policy 6) ─────────────────────────────────────────────────
    // Synthetic two-column statement, both columns reconciling as printed: current 1.000.000 − 350.000 = 650.000,
    // previous 800.000 − 280.000 = 520.000.

    [Fact]
    public void A_pre_tax_result_and_tax_from_the_comparative_column_is_provisional_and_named()
    {
        var verdict = VerdictFromStatement(netProfit: 650_000d, resultBeforeTax: 800_000d, incomeTax: -280_000d, priorNetProfit: 520_000d);

        Assert.Equal(ScoreReliabilityLevels.Provisional, verdict.Level);
        Assert.Equal([Codes.IntegrityFailed, Codes.CriticalFloorUnverified], CodesOf(verdict));
        Assert.Equal(
            "La extracción del balance no superó 1 control de consistencia: los importes leídos pueden contener errores. " +
            "El resultado antes de impuestos y el impuesto parecen tomados de la columna del ejercicio anterior; revisá el estado de resultados.",
            verdict.Reasons[0].Message);
        // Same code, same Args as any integrity failure: no new contract.
        Assert.Equal(1, verdict.Reasons[0].Args!.Count);
        Assert.True(DecisionReviewGate.RequirementsFor(verdict)!.FiguresReviewNote);
    }

    [Fact]
    public void A_presentation_beside_a_comparative_that_does_not_match_is_still_integrity_presentation()
    {
        // RECPAM after the tax: 600.000 printed against 1.000.000 − 350.000, and the prior 520.000 is not 650.000.
        var verdict = VerdictFromStatement(netProfit: 600_000d, resultBeforeTax: 1_000_000d, incomeTax: -350_000d, priorNetProfit: 520_000d);

        Assert.Equal(ScoreReliabilityLevels.WithCaveats, verdict.Level);
        Assert.Equal([Codes.IntegrityPresentation], CodesOf(verdict));
        Assert.False(DecisionReviewGate.RequirementsFor(verdict)!.FiguresReviewNote);
    }

    [Fact]
    public void Without_a_comparative_column_the_mixed_figures_keep_todays_presentation_verdict()
    {
        // The same mixed reading, but a first fiscal year: nothing to compare with, so nothing is assumed.
        var verdict = VerdictFromStatement(netProfit: 650_000d, resultBeforeTax: 800_000d, incomeTax: -280_000d);

        Assert.Equal(ScoreReliabilityLevels.WithCaveats, verdict.Level);
        Assert.Equal([Codes.IntegrityPresentation], CodesOf(verdict));
    }

    [Fact]
    public void A_sign_flip_and_a_column_mix_together_are_a_misread_naming_the_column()
    {
        var verdict = VerdictFromStatement(netProfit: -520_000d, resultBeforeTax: 800_000d, incomeTax: -280_000d, priorNetProfit: 520_000d);

        Assert.Equal(ScoreReliabilityLevels.Provisional, verdict.Level);
        Assert.Equal([Codes.IntegrityFailed, Codes.CriticalFloorUnverified], CodesOf(verdict));
        Assert.EndsWith(ScoreReliabilityMessages.IntegrityColumnMixSentence, verdict.Reasons[0].Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void A_checked_or_unknown_column_mix_keeps_the_presentation_caveat(bool? columnMix)
        => Assert.Equal([Codes.IntegrityPresentation], CodesOf(Verdict(FailedWith([NetProfit], columnMix: columnMix))));

    [Fact]
    public void A_recorded_column_mix_without_a_sign_flip_keeps_integrity_failed_and_the_floor_reason()
    {
        var verdict = Verdict(FailedWith([NetProfit], columnMix: true), FlooredScoring("roe"));

        Assert.Equal(ScoreReliabilityLevels.Provisional, verdict.Level);
        Assert.Equal([Codes.IntegrityFailed, Codes.CriticalFloorUnverified], CodesOf(verdict));
        Assert.Equal(ScoreReliabilityMessages.IntegrityFailedColumnMix(1), verdict.Reasons[0].Message);
    }

    [Fact]
    public void The_prompt_block_restates_a_column_mix_as_the_plain_integrity_failure()
    {
        // The block rebuilds from code + Args only (no free text rides into a prompt): the count, not the cause.
        var block = ScoreReliabilityPromptBlock.Build(Verdict(FailedWith([NetProfit], columnMix: true)));

        Assert.Contains("no superó 1 control de consistencia", block, StringComparison.Ordinal);
        Assert.DoesNotContain("columna", block, StringComparison.Ordinal);
    }

    [Fact]
    public void A_genuine_presentation_gap_is_with_caveats_with_integrity_presentation()
    {
        // The printed pre-tax figure leaves the financial results out: 15.193.935 − 5.317.877 = 9.876.058
        // expected against the 12.280.839 the statement reports as its result.
        var verdict = VerdictFromStatement(netProfit: 12_280_839d, resultBeforeTax: 15_193_935d, incomeTax: -5_317_877d);

        Assert.Equal(ScoreReliabilityLevels.WithCaveats, verdict.Level);
        Assert.Equal([Codes.IntegrityPresentation], CodesOf(verdict));
        Assert.False(DecisionReviewGate.RequirementsFor(verdict)!.FiguresReviewNote);
    }

    // ── Prompt-block, chat and narrative parity ───────────────────────────────

    /// <summary>A verified balance whose only failed check is net_profit_reconciliation.</summary>
    private const string NetProfitOnlyBalance = """
        {"integrity":{"passed":false,
          "checks":[{"name":"assets_components","passed":true},{"name":"liabilities_components","passed":true},
                    {"name":"accounting_equation","passed":true},{"name":"net_profit_reconciliation","passed":false}],
          "affectedRatios":["roe","roa","margen_neto"]}}
        """;

    // Re-pinned deliberately with the message: the prompt restatement names the possible causes and asserts none.
    private const string PresentationPhrase =
        "El resultado neto no concilia con el resultado antes de impuestos y el impuesto leídos; puede deberse a la " +
        "forma en que está presentado el estado de resultados (por ejemplo, el RECPAM expuesto después del impuesto) " +
        "o a una cifra tomada de la columna del ejercicio anterior. Las cifras del balance cierran.";

    [Fact]
    public void The_prompt_block_carries_the_fixed_phrase_at_con_observaciones()
    {
        var block = ScoreReliabilityPromptBlock.Build(Verdict(FailedWith([NetProfit])));

        Assert.Equal(
            ScoreReliabilityPromptBlock.Header + "\n" +
            "Confiabilidad del score: con observaciones\n" +
            "  • " + PresentationPhrase,
            block);
    }

    [Fact]
    public void The_chat_block_lists_no_ratios_from_figures_that_do_not_add_up()
    {
        var block = AnalysisController.BuildChatDataQualityBlock(
            NetProfitOnlyBalance, Verdict(FailedWith([NetProfit])), fiscalYear: 2025)!;

        Assert.Contains(PresentationPhrase, block, StringComparison.Ordinal);
        Assert.DoesNotContain("Ratios calculados con cifras que no superaron", block, StringComparison.Ordinal);
        Assert.DoesNotContain("no superó 1 control de consistencia", block, StringComparison.Ordinal);
    }

    [Fact]
    public void The_chat_block_beside_a_structure_failure_still_lists_the_ratios()
    {
        var block = AnalysisController.BuildChatDataQualityBlock(
            NetProfitOnlyBalance, Verdict(FailedWith([NetProfit, BalanceIntegrityService.CurrentAssetsItemsCheck])), fiscalYear: 2025)!;

        Assert.Contains("Ratios calculados con cifras que no superaron los controles de consistencia: ROE, ROA, Margen neto.",
                        block, StringComparison.Ordinal);
        Assert.DoesNotContain(PresentationPhrase, block, StringComparison.Ordinal);
    }

    [Fact]
    public void The_narrative_prompt_carries_the_same_block()
    {
        var verdict = Verdict(FailedWith([NetProfit]));
        var prompt  = ClaudeService.BuildNarrativePrompt(
            CleanScoring(), new RatiosData { CurrentRatio = 1.5, NetMargin = 11.2 }, null, null, verdict);

        Assert.Contains(ScoreReliabilityPromptBlock.Build(verdict)!, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("controles de consistencia: las cifras extraídas pueden contener errores", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_prompt_phrase_reads_no_message_and_names_no_code()
    {
        var phrase = ScoreReliabilityPromptBlock.Phrase(Verdict(FailedWith([NetProfit])).Reasons.Single());

        Assert.Equal(PresentationPhrase, phrase);
        Assert.DoesNotContain(NetProfit, phrase, StringComparison.Ordinal);
        Assert.DoesNotContain(Codes.IntegrityPresentation, phrase, StringComparison.Ordinal);
    }
}
