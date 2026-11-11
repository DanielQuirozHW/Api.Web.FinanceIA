// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-02

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Api.Web.FinanceIA.Servicios.ScoreReliability;
using Xunit;
using static Api.Web.FinanceIA.Tests.Servicios.ScoreReliability.ScoreReliabilityFixture;
using Codes = Api.Web.FinanceIA.Servicios.ScoreReliability.ScoreReliabilityCodes;

namespace Api.Web.FinanceIA.Tests.Servicios.ScoreReliability;

/// <summary>
/// The prompt-safe "CALIDAD DE LOS DATOS" block (score credibility 1b-3): one REAL verdict per code, each
/// rendered through its fixed phrase — with the numbers, dates, periods and ratio labels the evaluator
/// recorded — and never through its #301 message.
/// </summary>
public sealed class ScoreReliabilityPromptBlockTests
{
    /// <summary>The identifying sector text the PII test plants (a company name and a CUIT).</summary>
    internal const string IdentifyingSector = "ACME SA 30-71224873-0";

    private static ScoreReliabilityResponse Verdict(
        ScoreReliabilityFacts? facts = null, ScoringResult? scoring = null, ScoreReliabilityRow? row = null)
        => Evaluate(row ?? Row(), Json(scoring, facts ?? CleanFacts()))!;

    private static ScoringResult ScoringWith(Action<ScoringResult> change)
    {
        var scoring = CleanScoring();
        change(scoring);
        return scoring;
    }

    /// <summary>Every catalogue code, from a real verdict that raises it, with a fragment its phrase must carry.</summary>
    public static TheoryData<string, Func<ScoreReliabilityResponse>, string> EveryCode() => new()
    {
        { Codes.ScoreUnavailable, () => Verdict(row: Row(score: null)), "no tiene un score calculado" },
        { Codes.BcraUnavailable, () => Verdict(
              CleanFacts() with { Bcra = null },
              ScoringWith(s => { s.BcraExcluded = true; s.BcraExclusionReason = BcraExclusionReasons.LookupFailed; }),
              Row(enrichment: "Degraded")),
          "sin el componente BCRA" },
        { Codes.IntegrityFailed, () => Verdict(CleanFacts() with { Integrity = CleanFacts().Integrity! with { Passed = false, FailedChecks = 2 } }),
          "no superó 2 controles de consistencia" },
        { Codes.CriticalFloorUnverified, () => Verdict(
              CleanFacts() with { Integrity = FailedIntegrity(["liquidez_corriente", "prueba_acida"]) },
              FlooredScoring("liquidez_corriente")),
          "piso crítico de Liquidez corriente, calculado con cifras del balance que no superaron los controles de consistencia. Sin ese piso el score sería 820/1000." },
        { Codes.IntegrityPresentation, () => Verdict(CleanFacts() with
              {
                  Integrity = CleanFacts().Integrity! with
                  {
                      Passed = false, FailedChecks = 1, FailedCheckNames = [BalanceIntegrityService.NetProfitReconciliationCheck],
                      NetProfitSignFlip = false,
                  },
              }),
          // Re-pinned deliberately: the phrase now also names the comparative column as a possible cause.
          "el RECPAM expuesto después del impuesto) o a una cifra tomada de la columna del ejercicio anterior. Las cifras del balance cierran." },
        { Codes.IntegrityUnverified, () => Verdict(CleanFacts() with { Integrity = CleanFacts().Integrity! with { Verified = false } }),
          "No se pudo verificar la estructura" },
        { Codes.FiscalYearMismatch, () => Verdict(CleanFacts() with { Integrity = CleanFacts().Integrity! with { FiscalYearStatus = FiscalYearCheck.Mismatch } }),
          "con el ejercicio 2025 solicitado" },
        { Codes.RatiosLowCoverage, () => Verdict(scoring: ScoringWith(s =>
              {
                  s.RatiosLowCoverageFallback = true;
                  foreach (var key in new[] { "margen_neto", "prueba_acida" })
                      s.Breakdown[key] = new RatioBreakdownEntry(50m, "Atencion", false, 0.1m, null, Measured: false);
              })),
          "Prueba ácida y Margen neto no se midieron" },
        { Codes.ChequesUnavailable, () => Verdict(CleanFacts() with { Bcra = CleanFacts().Bcra! with { ChequesEstado = nameof(BcraChequesConsultaEstado.LookupFailed) } }),
          "cheques rechazados" },
        { Codes.RatiosNotMeasured, () => Verdict(scoring: ScoringWith(s =>
              s.Breakdown["roa"] = new RatioBreakdownEntry(50m, "Atencion", true, 0m, RatioExclusionReasons.Unread))),
          "No se pudo medir ROA por falta de datos" },
        { Codes.NegativeEquity, () => Verdict(CleanFacts() with { NegativeEquity = true }), "patrimonio neto es negativo" },
        { Codes.RatiosNotMeaningful, () => Verdict(scoring: ScoringWith(s =>
              s.Breakdown["leverage"] = new RatioBreakdownEntry(50m, "Atencion", true, 0m, RatioExclusionReasons.NotMeaningful))),
          "de forma significativa Apalancamiento, que se excluyó" },
        { Codes.RatiosFromModel, () => Verdict(CleanFacts() with { RatiosSource = ScoreReliabilityFactsBuilder.RatiosModel }),
          "lectura automática del documento" },
        { Codes.AmountsUnitAssumed, () => Verdict(CleanFacts() with { Integrity = CleanFacts().Integrity! with { AmountsUnitAssumed = true } }),
          "unidad de los importes" },
        { Codes.TrialBalance, () => Verdict(CleanFacts() with { Integrity = CleanFacts().Integrity! with { DocumentKind = TrialBalanceNormalizer.DocumentKind } }),
          "balance de sumas y saldos del sistema contable" },
        { Codes.ManualCorrection, () => Verdict(CleanFacts() with { ManualCorrections = 2, ManualCorrectionsLastAt = new DateTime(2026, 6, 14, 15, 0, 0, DateTimeKind.Utc) }),
          "corrigió manualmente 2 cifras del balance después de la lectura automática (última corrección: 14/06/2026)" },
        { Codes.SectorFallback, () => Verdict(CleanFacts() with { SectorFallback = true, SectorRaw = IdentifyingSector }),
          "perfil General" },
        { Codes.BalanceStale, () => Verdict(row: Row(fiscalYear: 2023)), "ejercicio 2023 tiene al menos 30 meses" },
        { Codes.LufeCarriedForward, () => Verdict(CleanFacts() with
              {
                  Lufe = CleanFacts().Lufe! with
                  {
                      VentasEstado = ScoreReliabilityLufeVentasEstados.CarriedForward,
                      // 01:30Z on the 4th is still the 3rd in Argentina.
                      VentasAsOf   = new DateTime(2026, 4, 4, 1, 30, 0, DateTimeKind.Utc),
                  },
              }),
          "obtenida el 03/04/2026" },
        { Codes.LufeUnavailable, () => Verdict(CleanFacts() with { Lufe = CleanFacts().Lufe! with { VentasEstado = ScoreReliabilityLufeVentasEstados.LookupFailed } }),
          "serie de ventas de LUFE" },
        { Codes.LufeStale, () => Verdict(CleanFacts() with
              {
                  Lufe = CleanFacts().Lufe! with
                  {
                      SnapshotAt          = new DateTime(2025, 10, 10, 12, 0, 0, DateTimeKind.Utc),
                      VentasUltimoPeriodo = "202508",
                  },
              }),
          "se obtuvieron el 10/10/2025, 8 meses antes de la fecha del score; la última venta informada es de 08/2025, 10 meses antes" },
        { Codes.CasfogUnavailable, () => Verdict(CleanFacts() with { Casfog = new ScoreReliabilityCasfogFacts { Estado = nameof(CasfogConsultaEstado.LookupFailed) } }),
          "No se pudo consultar CASFOG" },
        { Codes.CasfogStale, () => Verdict(CleanFacts() with { Casfog = CleanFacts().Casfog! with { FetchedAt = new DateTime(2025, 11, 20, 12, 0, 0, DateTimeKind.Utc) } }),
          "se obtuvieron el 20/11/2025, 7 meses antes" },
        { Codes.BcraSituacionUnreadable, () => Verdict(CleanFacts() with { BcraSituacionUnreadable = true }), "No se pudo interpretar la situación" },
        { Codes.BcraPeriodLag, () => Verdict(CleanFacts() with { Bcra = CleanFacts().Bcra! with { PeriodoConsultado = "202512" } }),
          "período 12/2025, 6 meses antes" },
        { Codes.BcraNotApplicable, () => Verdict(
              CleanFacts() with { Bcra = null },
              ScoringWith(s => { s.BcraExcluded = true; s.BcraExclusionReason = BcraExclusionReasons.NoBcraData; }),
              Row(enrichment: "NotApplicable")),
          "identificación fiscal registrada" },
        { Codes.MacroImputed, () => Verdict(CleanFacts() with { MacroImputed = true }), "dato de inflación" },
        { Codes.ConfigThresholdFallback, () => Verdict(scoring: ScoringWith(s => s.ThresholdFallbacks = ["roa", "endeudamiento"])),
          "umbrales configurados para Endeudamiento y ROA" },
        { Codes.FactsUnavailable, () => Evaluate(Row(), JsonBeforeFacts())!, "No quedó registrada la verificación completa" },
    };

    [Fact]
    public void The_theory_covers_every_catalogue_code()
    {
        var covered = EveryCode().Select(row => (string)row[0]).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(Codes.Catalogue.Select(c => c.Code).OrderBy(c => c, StringComparer.Ordinal),
                     covered.OrderBy(c => c, StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(EveryCode))]
    public void Each_code_renders_its_fixed_phrase_never_its_message(
        string code, Func<ScoreReliabilityResponse> verdictOf, string expectedFragment)
    {
        var verdict = verdictOf();
        var reason  = Assert.Single(verdict.Reasons, r => r.Code == code);

        string block = ScoreReliabilityPromptBlock.Build(verdict)!;

        Assert.StartsWith(ScoreReliabilityPromptBlock.Header + "\n", block, StringComparison.Ordinal);
        Assert.Contains("  • " + ScoreReliabilityPromptBlock.Phrase(reason), block, StringComparison.Ordinal);
        Assert.Contains(expectedFragment, block, StringComparison.Ordinal);
        // The phrase is a function of the code and the args only: whatever the message says, it is unchanged.
        Assert.Equal(ScoreReliabilityPromptBlock.Phrase(reason),
                     ScoreReliabilityPromptBlock.Phrase(reason with { Message = "ACME SA 30-71224873-0" }));
        Assert.DoesNotContain(code, block, StringComparison.Ordinal);
        Assert.DoesNotContain(ScoreReliabilityPromptBlock.GenericPhrase, block, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ScoreReliabilityLevels.Provisional, "Confiabilidad del score: provisoria")]
    [InlineData(ScoreReliabilityLevels.WithCaveats, "Confiabilidad del score: con observaciones")]
    [InlineData(ScoreReliabilityLevels.None,        "Confiabilidad del score: sin score")]
    public void The_level_is_stated_in_spanish(string level, string expected)
    {
        var verdict = level switch
        {
            ScoreReliabilityLevels.Provisional => Verdict(CleanFacts() with { Integrity = CleanFacts().Integrity! with { Verified = false } }),
            ScoreReliabilityLevels.WithCaveats => Verdict(CleanFacts() with { MacroImputed = true }),
            _                                  => Verdict(row: Row(score: null)),
        };

        Assert.Equal(level, verdict.Level);
        Assert.Equal(expected, ScoreReliabilityPromptBlock.Build(verdict)!.Split('\n')[1]);
    }

    [Fact]
    public void A_complete_verdict_is_no_block_at_all()
    {
        var verdict = Verdict();

        Assert.Equal(ScoreReliabilityLevels.Complete, verdict.Level);
        Assert.Null(ScoreReliabilityPromptBlock.Build(verdict));
        Assert.Null(ScoreReliabilityPromptBlock.Build(null));
    }

    [Theory]
    [InlineData(RatioExclusionReasons.Unread)]
    [InlineData(null)]
    public void Interest_coverage_unread_under_RT6_puts_no_block_in_the_narrative_or_the_chat(string? exclusionReason)
    {
        // The block both prompts carry (scoring narrative and analyst chat) comes from this one Build.
        var verdict = Verdict(scoring: InterestCoverageUnreadScoring(exclusionReason));

        Assert.Equal(ScoreReliabilityLevels.Complete, verdict.Level);
        Assert.Null(ScoreReliabilityPromptBlock.Build(verdict));
    }

    [Fact]
    public void Interest_coverage_unread_beside_another_caveat_is_not_named_in_the_block()
    {
        var verdict = Verdict(CleanFacts() with { MacroImputed = true }, InterestCoverageUnreadScoring());

        string block = ScoreReliabilityPromptBlock.Build(verdict)!;

        Assert.Contains("Confiabilidad del score: con observaciones", block, StringComparison.Ordinal);
        Assert.DoesNotContain("Cobertura de intereses", block, StringComparison.Ordinal);
        Assert.DoesNotContain("por falta de datos en el balance", block, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_code_renders_the_generic_phrase_never_its_message()
    {
        var verdict = new ScoreReliabilityResponse
        {
            Level         = ScoreReliabilityLevels.WithCaveats,
            PolicyVersion = "9",
            Reasons =
            [
                new ScoreReliabilityReasonResponse
                {
                    Code = "something_new", Severity = ScoreReliabilitySeverities.Caveat, Message = "Texto con ACME SA.",
                },
            ],
        };

        string block = ScoreReliabilityPromptBlock.Build(verdict)!;

        Assert.EndsWith("  • " + ScoreReliabilityPromptBlock.GenericPhrase, block, StringComparison.Ordinal);
        Assert.DoesNotContain("ACME", block, StringComparison.Ordinal);
        Assert.DoesNotContain("something_new", block, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reason_without_args_drops_only_the_variable_part()
    {
        var reason = new ScoreReliabilityReasonResponse
        {
            Code = Codes.RatiosNotMeasured, Severity = ScoreReliabilitySeverities.Caveat, Message = "x",
        };

        Assert.Equal(
            "Algunos ratios no se pudieron medir por falta de datos en el balance; el score usó los ratios restantes.",
            ScoreReliabilityPromptBlock.Phrase(reason));
    }

    [Fact]
    public void The_identifying_sector_reaches_the_message_but_never_the_block()
    {
        var verdict = Verdict(CleanFacts() with { SectorFallback = true, SectorRaw = IdentifyingSector });

        Assert.Contains(IdentifyingSector, Assert.Single(verdict.Reasons).Message, StringComparison.Ordinal);

        string block = ScoreReliabilityPromptBlock.Build(verdict)!;
        Assert.DoesNotContain("ACME", block, StringComparison.Ordinal);
        Assert.DoesNotContain("30-71224873-0", block, StringComparison.Ordinal);
    }

    [Fact]
    public void The_args_never_reach_the_wire()
    {
        var verdict = Verdict(scoring: ScoringWith(s => s.ThresholdFallbacks = ["roa"]));
        Assert.NotNull(Assert.Single(verdict.Reasons).Args);

        string wire = Wire(verdict);

        Assert.DoesNotContain("args", wire, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ratioKeys", wire, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            Wire(verdict with { Reasons = verdict.Reasons.Select(r => r with { Args = null }).ToList() }),
            wire);
    }
}
