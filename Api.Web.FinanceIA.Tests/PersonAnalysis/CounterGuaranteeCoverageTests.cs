// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-29

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.PersonEvaluation;
using Xunit;
using static Api.Web.FinanceIA.Tests.PersonAnalysis.PersonEvaluationFixtures;
using static Api.Web.FinanceIA.Tests.PersonAnalysis.PersonEvaluationFiadorTests;

namespace Api.Web.FinanceIA.Tests.PersonAnalysis;

/// <summary>
/// The aggregate counter-guarantee coverage, recomputed from frozen Fiador rows (M10 Phase 5a-2): a shared
/// asset counted once (or every share, when the shares fit), each fiador capped at his amount at charge and
/// floored at 0, a suggested rejection left out of the headline, the frozen bands and the current minimum,
/// and the decision rule it drives. The requested amount is 1.000.000.
/// </summary>
public sealed class CounterGuaranteeCoverageTests
{
    private static readonly AnalysisParticipant Solicitante = new()
    {
        ParticipantId  = Guid.NewGuid(),
        TenantId       = Tenant,
        AnalysisId     = Guid.NewGuid(),
        Role           = ParticipantRoles.Solicitante,
        AmountAtCharge = 1_000_000m,
        Currency       = "ARS",
        FiltersJson    = "[]"
    };

    /// <summary>A frozen Fiador row of one evaluation, as the POST writes it.</summary>
    private static AnalysisParticipant Row(
        LegajoItem[] items, decimal amountAtCharge = 1_000_000m, PersonEvaluationInput? person = null, int order = 0,
        decimal guaranteesGiven = 0m)
    {
        var evaluation = PersonEvaluationService.FiadorEvaluation(
            Fiador(items, person: (person ?? Empty()) with { RequestedAmount = amountAtCharge }, guaranteesGiven: guaranteesGiven));

        return new AnalysisParticipant
        {
            ParticipantId  = Guid.NewGuid(),
            TenantId       = Tenant,
            AnalysisId     = Solicitante.AnalysisId,
            PersonLegajoId = Guid.NewGuid(),
            Role           = ParticipantRoles.Fiador,
            FiadorId       = Guid.NewGuid(),
            GuaranteeType  = GuaranteeTypes.Fianza,
            AmountAtCharge = amountAtCharge,
            Currency       = "ARS",
            FiltersJson    = PersonEvaluationJson.Serialize(evaluation.Filters),
            IndicatorsJson = PersonEvaluationJson.Serialize(evaluation.Indicators),
            CoverageJson   = PersonEvaluationJson.Serialize(evaluation.Coverage),
            CreatedAt      = AsOfUtc.AddSeconds(order),
            EvaluationVersion = PersonEvaluationService.Version
        };
    }

    private static LegajoItem House(decimal market, string partida = "P1", decimal? pct = null)
        => Asset(LegajoItemKinds.Inmueble, market, keyKind: LegajoMatchKeyKinds.Partida, key: partida, pct: pct);

    private static CounterGuaranteeCoverage Of(int minPct = 100, params AnalysisParticipant[] rows)
        => PersonEvaluationJson.CounterGuaranteeCoverageOf(Solicitante, PersonEvaluationJson.FiadorRowsOf(rows), minPct)!;

    [Fact]
    public void WithoutFiadores_ThereIsNoCoverage()
    {
        Assert.Null(PersonEvaluationJson.CounterGuaranteeCoverageOf(Solicitante, [], 100));

        var rules = PersonEvaluationJson.DecisionRules(new Dictionary<string, decimal>(), Solicitante, []);
        Assert.Null(rules.CounterGuaranteeCoverage);
        Assert.False(rules.CounterGuaranteeBelowMin);
    }

    [Fact]
    public void TwoFiadoresSharingAProperty_CountItOnce_AtTheLargerValue()
    {
        var a = Row([House(2_000_000m)], order: 0);   // executable 1.000.000
        var b = Row([House(1_600_000m)], order: 1);   // executable   800.000, same partida

        var coverage = Of(100, a, b);

        Assert.Equal(1_000_000m, coverage.ExecutableTotal);
        Assert.Equal(1m, coverage.Ratio);
        Assert.True(coverage.MeetsMin);
        Assert.Equal(1_000_000m, coverage.Contributions.Single(c => c.ParticipantId == a.ParticipantId).Contribution);
        var duplicate = coverage.Contributions.Single(c => c.ParticipantId == b.ParticipantId);
        Assert.Equal(0m, duplicate.Contribution);
        Assert.Single(duplicate.DuplicateItemIds);
    }

    [Fact]
    public void TheLargerHolderKeepsTheAsset_WhicheverComesFirst()
    {
        var small = Row([House(1_600_000m)], order: 0);
        var large = Row([House(2_000_000m)], order: 1);

        var coverage = Of(100, small, large);

        Assert.Equal(1_000_000m, coverage.ExecutableTotal);
        Assert.Single(coverage.Contributions.Single(c => c.ParticipantId == small.ParticipantId).DuplicateItemIds);
    }

    [Fact]
    public void SharesThatFit_AreEachCounted()
    {
        var a = Row([House(1_000_000m, pct: 50m)], order: 0);   // 500.000
        var b = Row([House(800_000m, pct: 50m)], order: 1);     // 400.000

        var coverage = Of(100, a, b);

        Assert.Equal(900_000m, coverage.ExecutableTotal);
        Assert.All(coverage.Contributions, c => Assert.Empty(c.DuplicateItemIds));
    }

    [Theory]
    [InlineData(60.0, 50.0)]   // above 100 %
    [InlineData(50.0, null)]   // a share not printed
    public void SharesThatDoNotFit_AreCountedOnce(double first, double? second)
    {
        var a = Row([House(1_000_000m, pct: (decimal)first)], order: 0);
        var b = Row([House(800_000m, pct: (decimal?)second)], order: 1);

        Assert.Equal(500_000m, Of(100, a, b).ExecutableTotal);
    }

    [Fact]
    public void AssetsWithoutATrustedKey_AreNeverDeduplicated()
    {
        var a = Row([Asset(LegajoItemKinds.Rodado, 1_000_000m)], order: 0);
        var b = Row([Asset(LegajoItemKinds.Rodado, 1_000_000m)], order: 1);

        Assert.Equal(1_200_000m, Of(100, a, b).ExecutableTotal);   // 600.000 each: no key, no de-duplication
    }

    [Fact]
    public void EachFiador_IsCappedAtHisAmountAtCharge()
    {
        var a = Row([House(10_000_000m)], amountAtCharge: 300_000m);   // 5.000.000 executable

        var coverage = Of(100, a);

        Assert.Equal(300_000m, coverage.ExecutableTotal);
        Assert.Equal(0.3m, coverage.Ratio);
        Assert.False(coverage.MeetsMin);
    }

    [Fact]
    public void ANegativeNet_DoesNotReduceAnotherFiador()
    {
        var indebted = Row([House(200_000m, "P2"), Asset(LegajoItemKinds.Deuda, 900_000m, side: LegajoItemSides.Pasivo)], order: 0);
        var solvent  = Row([House(1_200_000m)], order: 1);

        var coverage = Of(100, indebted, solvent);

        Assert.Equal(0m, coverage.Contributions.Single(c => c.ParticipantId == indebted.ParticipantId).Contribution);
        Assert.Equal(600_000m, coverage.ExecutableTotal);
    }

    [Fact]
    public void GuaranteesGiven_ReduceTheContribution()
    {
        Assert.Equal(700_000m, Of(100, Row([House(2_000_000m)], guaranteesGiven: 300_000m)).ExecutableTotal);
    }

    [Fact]
    public void AFiadorWithASuggestedRejection_IsLeftOutOfTheHeadline_ButShown()
    {
        var clean    = Row([House(1_000_000m, "P1")], order: 0);   // 500.000
        var rejected = Row([House(1_000_000m, "P2")], order: 1,
            person: Empty() with { Bcra = Bcra(Deuda(situacion: 5), Cheques(BcraChequesConsultaEstado.NoChequesReported)) });

        var coverage = Of(100, clean, rejected);

        Assert.Equal(500_000m, coverage.ExecutableTotal);
        Assert.Equal(1_000_000m - 100_000m, coverage.ExecutableTotalIncludingFlagged);   // his BCRA debt is his pasivo
        Assert.Equal(1, coverage.FlaggedFiadoresCount);
        Assert.Equal(0.5m, coverage.Ratio);
        var flagged = coverage.Contributions.Single(c => c.ParticipantId == rejected.ParticipantId);
        Assert.False(flagged.Counted);
        Assert.Equal(FiadorAssetReasons.RechazoSugerido, flagged.Reason);
        Assert.Equal(0m, flagged.Contribution);
        Assert.Equal(400_000m, flagged.ContributionIncludingFlagged);
    }

    [Fact]
    public void TheBandFollowsTheCurrentMinimum_NotTheFrozenFiadorCuts()
    {
        // Frozen with a rojo of 0,5: irrelevant to the aggregate band, which says whether the minimum is met.
        var person = Empty() with { Parameters = With(r => r["pf_cobertura_rojo"] = 0.5m) };
        var row = Row([House(1_200_000m)], person: person);   // ratio 0,6

        var atFifty   = Of(50, row);
        var atHundred = Of(100, row);

        Assert.Equal(0.6m, atFifty.Ratio);
        Assert.Equal(50, atFifty.MinPct);
        Assert.True(atFifty.MeetsMin);
        Assert.Equal(PersonIndicatorBands.Verde, atFifty.Band);
        Assert.False(atHundred.MeetsMin);
        Assert.Equal(PersonIndicatorBands.Rojo, atHundred.Band);
    }

    [Fact]
    public void ASingleFiadorFullyCoveringTheAval_IsVerde_WhileHisOwnIndicatorStaysUncapped()
    {
        var row = Row([House(4_000_000m)]);   // 2.000.000 executable, capped at his 1.000.000 at charge

        var coverage = Of(100, row);

        Assert.Equal(1m, coverage.Ratio);
        Assert.True(coverage.MeetsMin);
        Assert.Equal(PersonIndicatorBands.Verde, coverage.Band);

        var own = Assert.Single(
            PersonEvaluationJson.TryDeserialize<List<PersonIndicatorResult>>(row.IndicatorsJson)!,
            i => i.Id == "cobertura_patrimonial_fiador");
        Assert.Equal(2m, own.Value);
        Assert.Equal(PersonIndicatorBands.Verde, own.Band);   // pf_cobertura_verde 2,0, uncapped
    }

    [Fact]
    public void BelowTheMinimum_IsRojo()
    {
        var coverage = Of(100, Row([House(1_000_000m)]));   // 0,5 against 100 %

        Assert.False(coverage.MeetsMin);
        Assert.Equal(PersonIndicatorBands.Rojo, coverage.Band);
    }

    [Fact]
    public void AMinimumOfZero_IsAlwaysVerde_WhenThereIsARatio()
    {
        var coverage = Of(0, Row([House(0m)]));

        Assert.Equal(0m, coverage.Ratio);
        Assert.Equal(PersonIndicatorBands.Verde, coverage.Band);
    }

    [Fact]
    public void AnOperationNotInPesos_HasNoRatio_AndDoesNotMeetAMinimum()
    {
        var usd = new AnalysisParticipant
        {
            ParticipantId = Guid.NewGuid(), TenantId = Tenant, AnalysisId = Solicitante.AnalysisId,
            Role = ParticipantRoles.Solicitante, AmountAtCharge = 1_000_000m, Currency = "USD"
        };
        var row = Row([House(4_000_000m)], person: Empty() with { Currency = "USD" });

        var coverage = PersonEvaluationJson.CounterGuaranteeCoverageOf(usd, [row], 100)!;

        Assert.Null(coverage.Ratio);
        Assert.Null(coverage.Band);
        Assert.False(coverage.MeetsMin);
        Assert.True(PersonEvaluationJson.CounterGuaranteeCoverageOf(usd, [row], 0)!.MeetsMin);
    }

    [Fact]
    public void BelowTheMinimum_TheApprovalNeedsAReason_EvenWhenTheTenantWaivesIt()
    {
        var config = new Dictionary<string, decimal> { ["pf_aprobacion_requiere_motivo"] = 0m };

        var below = PersonEvaluationJson.DecisionRules(config, Solicitante, [Row([House(1_000_000m)])]);
        var meets = PersonEvaluationJson.DecisionRules(config, Solicitante, [Row([House(2_000_000m)])]);

        Assert.True(below.CounterGuaranteeBelowMin);
        Assert.True(below.RequiresReason);
        Assert.False(meets.CounterGuaranteeBelowMin);
        Assert.False(meets.RequiresReason);
        Assert.Equal(PersonDecisionRules.Confirmations, below.RequiredConfirmations);   // the checkbox is not touched
    }

    [Fact]
    public void TheMinimumIsTheTenantsCurrentOne()
    {
        var rows = new[] { Row([House(1_000_000m)]) };   // 50 %

        Assert.True(PersonEvaluationJson.DecisionRules(
            new Dictionary<string, decimal> { ["pf_contragarantia_cobertura_min_pct"] = 50m }, Solicitante, rows).CounterGuaranteeCoverage!.MeetsMin);
        Assert.False(PersonEvaluationJson.DecisionRules(
            new Dictionary<string, decimal>(), Solicitante, rows).CounterGuaranteeCoverage!.MeetsMin);
    }

    [Fact]
    public void AnUnreadableRow_CountsAsNothing_AndNeverFailsTheRead()
    {
        var broken = Row([House(2_000_000m)]);
        broken.CoverageJson = "{not json";

        var coverage = Of(100, broken);

        Assert.Equal(0m, coverage.ExecutableTotal);
        Assert.Equal(1, coverage.FiadoresCount);
    }
}
