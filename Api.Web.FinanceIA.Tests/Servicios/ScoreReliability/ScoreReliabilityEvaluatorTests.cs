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
/// The score-reliability rules (score credibility 1b-2b), one case per code, plus the level precedence, the
/// reason order, unknown dates and every threshold boundary against the score date.
/// </summary>
public sealed class ScoreReliabilityEvaluatorTests
{
    private static ScoreReliabilityResponse Verdict(
        ScoreReliabilityFacts? facts = null, ScoringResult? scoring = null, ScoreReliabilityRow? row = null)
        => Evaluate(row ?? Row(), Json(scoring, facts ?? CleanFacts()))!;

    private static IReadOnlyList<string> CodesOf(ScoreReliabilityResponse verdict)
        => verdict.Reasons.Select(r => r.Code).ToList();

    private static void AssertOnly(ScoreReliabilityResponse verdict, string code, string level)
    {
        Assert.Equal([code], CodesOf(verdict));
        Assert.Equal(level, verdict.Level);
        Assert.Equal(ScoreReliabilityCodes.SeverityOf(code), verdict.Reasons[0].Severity);
    }

    // ── Levels ────────────────────────────────────────────────────────────────

    [Fact]
    public void Clean_facts_are_complete_with_no_reasons()
    {
        var verdict = Verdict();

        Assert.Equal(ScoreReliabilityLevels.Complete, verdict.Level);
        Assert.Empty(verdict.Reasons);
        Assert.Equal("6", verdict.PolicyVersion);  // policy 6: a column mix is not a presentation
    }

    [Fact]
    public void Persona_humana_has_no_verdict_even_with_a_score()
        => Assert.Null(Evaluate(Row(kind: AnalysisKinds.PersonaHumana), Json()));

    [Fact]
    public void No_score_is_none_with_score_unavailable_alone()
    {
        // Everything else wrong too: none outranks it all, and nothing else is reported.
        var verdict = Evaluate(Row(score: null, enrichment: "Degraded", fiscalYear: 2015), "{not json")!;

        AssertOnly(verdict, Codes.ScoreUnavailable, ScoreReliabilityLevels.None);
        Assert.Equal(ScoreReliabilitySeverities.None, verdict.Reasons[0].Severity);
    }

    [Fact]
    public void Provisional_outranks_caveats_and_reasons_are_ordered_none_provisional_caveat_then_catalogue()
    {
        var facts = CleanFacts() with
        {
            MacroImputed = true,                                   // caveat (late in the catalogue)
            SectorFallback = true, SectorRaw = "Minería",          // caveat
            Integrity = CleanFacts().Integrity! with { Verified = false, NotVerifiedReasons = ["x"] }, // provisional
        };
        var scoring = CleanScoring();
        scoring.RatiosLowCoverageFallback = true;                  // provisional (after integrity)

        var verdict = Verdict(facts, scoring, Row(enrichment: "NotApplicable")); // caveat bcra_not_applicable

        Assert.Equal(ScoreReliabilityLevels.Provisional, verdict.Level);
        Assert.Equal(
            [Codes.IntegrityUnverified, Codes.RatiosLowCoverage, Codes.SectorFallback, Codes.BcraNotApplicable, Codes.MacroImputed],
            CodesOf(verdict));
        Assert.Equal(
            verdict.Reasons.OrderBy(r => ScoreReliabilityCodes.OrderOf(r.Code)).Select(r => r.Code),
            CodesOf(verdict));
    }

    [Fact]
    public void Caveats_alone_are_with_caveats()
        => AssertOnly(Verdict(CleanFacts() with { MacroImputed = true }), Codes.MacroImputed, ScoreReliabilityLevels.WithCaveats);

    // ── Provisional codes ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(nameof(BcraDeudaConsultaEstado.LookupFailed), BcraExclusionReasons.LookupFailed, ScoreReliabilityMessages.BcraUnavailableLookupFailed)]
    [InlineData(null, BcraExclusionReasons.NoBcraData, ScoreReliabilityMessages.BcraUnavailableNoData)]
    [InlineData(null, BcraExclusionReasons.NoDeudaBlock, ScoreReliabilityMessages.BcraUnavailableNoDeudaBlock)]
    public void Degraded_without_the_deuda_is_bcra_unavailable(string? deudaEstado, string exclusion, string message)
    {
        var scoring = CleanScoring();
        scoring.BcraExcluded        = true;
        scoring.BcraExclusionReason = exclusion;
        var facts = CleanFacts() with
        {
            Bcra = deudaEstado is null ? null : new ScoreReliabilityBcraFacts { DeudaEstado = deudaEstado },
        };

        var verdict = Verdict(facts, scoring, Row(enrichment: "Degraded"));

        AssertOnly(verdict, Codes.BcraUnavailable, ScoreReliabilityLevels.Provisional);
        Assert.Equal(message, verdict.Reasons[0].Message);
    }

    [Fact]
    public void Bcra_excluded_by_the_engine_is_bcra_unavailable_even_when_the_status_says_complete()
    {
        var scoring = CleanScoring();
        scoring.BcraExcluded        = true;
        scoring.BcraExclusionReason = BcraExclusionReasons.LookupFailed;

        AssertOnly(Verdict(scoring: scoring), Codes.BcraUnavailable, ScoreReliabilityLevels.Provisional);
    }

    [Fact]
    public void Degraded_with_only_the_cheques_failing_is_a_caveat_not_provisional()
    {
        var facts = CleanFacts() with
        {
            Bcra = CleanFacts().Bcra! with { ChequesEstado = nameof(BcraChequesConsultaEstado.LookupFailed) },
        };

        var verdict = Verdict(facts, row: Row(enrichment: "Degraded"));

        AssertOnly(verdict, Codes.ChequesUnavailable, ScoreReliabilityLevels.WithCaveats);
        Assert.Equal(ScoreReliabilityMessages.ChequesUnavailable, verdict.Reasons[0].Message);
    }

    [Fact]
    public void Degraded_with_the_deuda_answered_is_cheques_unavailable_even_without_a_cheques_block()
    {
        var facts = CleanFacts() with
        {
            Bcra = new ScoreReliabilityBcraFacts { DeudaEstado = nameof(BcraDeudaConsultaEstado.NoDebtReported), PeriodoConsultado = "202605" },
        };

        AssertOnly(Verdict(facts, row: Row(enrichment: "Degraded")), Codes.ChequesUnavailable, ScoreReliabilityLevels.WithCaveats);
    }

    [Fact]
    public void Degraded_on_a_row_without_facts_is_bcra_unavailable()
    {
        var verdict = Evaluate(Row(enrichment: "Degraded"), JsonBeforeFacts())!;

        Assert.Equal([Codes.BcraUnavailable, Codes.FactsUnavailable], CodesOf(verdict));
        Assert.Equal(ScoreReliabilityLevels.Provisional, verdict.Level);
    }

    [Fact]
    public void Integrity_failed_names_how_many_checks_failed()
    {
        var facts = CleanFacts() with { Integrity = CleanFacts().Integrity! with { Passed = false, FailedChecks = 2 } };

        var verdict = Verdict(facts);

        AssertOnly(verdict, Codes.IntegrityFailed, ScoreReliabilityLevels.Provisional);
        Assert.Contains("2 controles", verdict.Reasons[0].Message);
    }

    [Fact]
    public void Integrity_that_never_ran_is_unverified_only()
    {
        // integrity_unavailable: not passed only because nothing was proven — not a failure.
        var facts = CleanFacts() with
        {
            Integrity = new ScoreReliabilityIntegrityFacts { Passed = false, Verified = false, FailedChecks = 0, NotVerifiedReasons = ["integrity_unavailable"] },
        };

        AssertOnly(Verdict(facts), Codes.IntegrityUnverified, ScoreReliabilityLevels.Provisional);
    }

    [Fact]
    public void Integrity_unverified()
    {
        var facts = CleanFacts() with { Integrity = CleanFacts().Integrity! with { Verified = false } };

        AssertOnly(Verdict(facts), Codes.IntegrityUnverified, ScoreReliabilityLevels.Provisional);
    }

    [Fact]
    public void Integrity_unverified_names_the_check_that_did_not_run()
    {
        var facts = CleanFacts() with
        {
            Integrity = CleanFacts().Integrity! with { Verified = false, NotVerifiedReasons = ["liabilities_components_skipped"] },
        };

        var verdict = Verdict(facts);

        AssertOnly(verdict, Codes.IntegrityUnverified, ScoreReliabilityLevels.Provisional);
        Assert.Equal(
            "No se pudo verificar la estructura del balance extraído: no se pudo comprobar que el pasivo total " +
            "coincida con la suma del pasivo corriente y el no corriente.",
            verdict.Reasons[0].Message);
        Assert.Null(verdict.Reasons[0].Args);
    }

    /// <summary>
    /// The landscape misread, end to end: a non-zero rubro under a null pasivo no corriente makes
    /// liabilities_components RUN and fail, so the extraction is verified-and-failed — integrity_failed, no
    /// longer integrity_unverified.
    /// </summary>
    [Fact]
    public void A_contradictory_component_is_integrity_failed_not_unverified()
    {
        var report = new BalanceIntegrityService().Evaluate(new ExtractedBalance
        {
            Current = new BalanceAccounts
            {
                TotalAssets = 2_000_000d, CurrentAssets = 1_300_000d, NonCurrentAssets = 700_000d,
                TotalLiabilities = 800_000d, CurrentLiabilities = 800_000d, Equity = 1_200_000d,
            },
            NonCurrentLiabilitiesItems = [new BalanceLineItem { Label = "Otros pasivos", Amount = 700_000d }],
        }).Report;
        var blob = new System.Text.Json.Nodes.JsonObject
        {
            ["integrity"] = System.Text.Json.JsonSerializer.SerializeToNode(report),
        }.ToJsonString();
        var read = ExtractionIntegrityReader.Read(blob)!;

        var facts = CleanFacts() with
        {
            Integrity = CleanFacts().Integrity! with
            {
                Passed             = read.Passed,
                Verified           = read.Verified,
                NotVerifiedReasons = read.NotVerifiedReasons,
                FailedChecks       = read.FailedChecks.Count,
                FailedCheckNames   = [.. read.FailedChecks],
            },
        };

        var codes = CodesOf(Verdict(facts));

        Assert.Contains(Codes.IntegrityFailed, codes);
        Assert.DoesNotContain(Codes.IntegrityUnverified, codes);
        Assert.DoesNotContain(Codes.IntegrityPresentation, codes);
    }

    [Fact]
    public void Fiscal_year_mismatch_names_the_requested_year()
    {
        var facts = CleanFacts() with { Integrity = CleanFacts().Integrity! with { FiscalYearStatus = FiscalYearCheck.Mismatch } };

        var verdict = Verdict(facts);

        AssertOnly(verdict, Codes.FiscalYearMismatch, ScoreReliabilityLevels.Provisional);
        Assert.Contains("2025", verdict.Reasons[0].Message);
    }

    [Fact]
    public void Ratios_low_coverage_names_the_unmeasured_ratios_in_spanish()
    {
        var scoring = CleanScoring();
        scoring.RatiosLowCoverageFallback = true;
        foreach (var key in new[] { "margen_neto", "leverage", "prueba_acida" })
            scoring.Breakdown[key] = new RatioBreakdownEntry(50m, "Atencion", false, 0.1m, null, Measured: false);

        var verdict = Verdict(scoring: scoring);

        AssertOnly(verdict, Codes.RatiosLowCoverage, ScoreReliabilityLevels.Provisional);
        Assert.Contains("Prueba ácida, Apalancamiento y Margen neto", verdict.Reasons[0].Message);
    }

    [Fact]
    public void Ratios_low_coverage_before_the_facts_falls_back_to_non_computable_ratios()
    {
        var scoring = CleanScoring();
        scoring.RatiosLowCoverageFallback = true;
        scoring.NonComputableRatios       = ["roe", "roa"];

        var verdict = Evaluate(Row(), JsonBeforeFacts(scoring))!;

        Assert.Equal([Codes.RatiosLowCoverage, Codes.FactsUnavailable], CodesOf(verdict));
        Assert.Contains("ROE y ROA", verdict.Reasons[0].Message);
    }

    // ── Caveat codes ──────────────────────────────────────────────────────────

    [Fact]
    public void Cheques_lookup_failed_while_counting_is_a_caveat_whatever_the_status()
    {
        var facts = CleanFacts() with
        {
            Bcra = CleanFacts().Bcra! with { ChequesEstado = nameof(BcraChequesConsultaEstado.LookupFailed) },
        };

        AssertOnly(Verdict(facts), Codes.ChequesUnavailable, ScoreReliabilityLevels.WithCaveats);
    }

    [Fact]
    public void Cheques_lookup_failed_that_does_not_count_is_not_reported()
    {
        var facts = CleanFacts() with
        {
            Bcra = CleanFacts().Bcra! with { ChequesEstado = nameof(BcraChequesConsultaEstado.LookupFailed), ChequesEvaluated = false },
        };

        Assert.Empty(Verdict(facts).Reasons);
    }

    [Fact]
    public void Unread_ratios_on_the_renormalise_path_are_not_measured()
    {
        var scoring = CleanScoring();
        scoring.Breakdown["roa"]                 = new RatioBreakdownEntry(50m, "Atencion", true, 0m, RatioExclusionReasons.Unread);
        scoring.Breakdown["liquidez_corriente"]  = new RatioBreakdownEntry(50m, "Atencion", true, 0m, RatioExclusionReasons.Unread);

        var verdict = Verdict(scoring: scoring);

        AssertOnly(verdict, Codes.RatiosNotMeasured, ScoreReliabilityLevels.WithCaveats);
        Assert.Contains("No se pudieron medir Liquidez corriente y ROA", verdict.Reasons[0].Message);
    }

    // ── Interest coverage under RT 6 (policy 4) ───────────────────────────────

    [Theory]
    [InlineData(RatioExclusionReasons.Unread)]
    [InlineData(null)]   // ratios from the model: the cause is unknown, the ratio is still left out
    public void Interest_coverage_as_the_only_unread_ratio_is_complete_with_no_reasons(string? exclusionReason)
    {
        var verdict = Verdict(scoring: InterestCoverageUnreadScoring(exclusionReason));

        Assert.Equal(ScoreReliabilityLevels.Complete, verdict.Level);
        Assert.Empty(verdict.Reasons);
        Assert.Equal(ScoreReliabilityPolicy.PolicyVersion, verdict.PolicyVersion);
    }

    [Fact]
    public void Interest_coverage_with_another_unread_ratio_names_only_the_other_one()
    {
        var scoring = InterestCoverageUnreadScoring();
        scoring.Breakdown["roa"] = new RatioBreakdownEntry(50m, "Atencion", true, 0m, RatioExclusionReasons.Unread);
        scoring.NonComputableRatios = ["cobertura_intereses", "roa"];

        var verdict = Verdict(scoring: scoring);

        AssertOnly(verdict, Codes.RatiosNotMeasured, ScoreReliabilityLevels.WithCaveats);
        Assert.Equal(["roa"], verdict.Reasons[0].Args!.RatioKeys);
        Assert.Equal(ScoreReliabilityMessages.RatiosNotMeasured(["roa"]), verdict.Reasons[0].Message);
        Assert.DoesNotContain("Cobertura de intereses", verdict.Reasons[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Low_coverage_still_names_interest_coverage()
    {
        // There the score really is short of data: the RT 6 exclusion does not apply.
        var scoring = CleanScoring();
        scoring.RatiosLowCoverageFallback = true;
        foreach (var key in new[] { "cobertura_intereses", "leverage", "roe" })
            scoring.Breakdown[key] = new RatioBreakdownEntry(50m, "Atencion", false, 0.1m, null, Measured: false);

        var verdict = Verdict(scoring: scoring);

        AssertOnly(verdict, Codes.RatiosLowCoverage, ScoreReliabilityLevels.Provisional);
        Assert.Contains("cobertura_intereses", verdict.Reasons[0].Args!.RatioKeys);
        Assert.Contains("Cobertura de intereses", verdict.Reasons[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Low_coverage_before_the_facts_still_names_interest_coverage()
    {
        var scoring = CleanScoring();
        scoring.RatiosLowCoverageFallback = true;
        scoring.NonComputableRatios       = ["cobertura_intereses", "roe"];

        var verdict = Evaluate(Row(), JsonBeforeFacts(scoring))!;

        Assert.Equal([Codes.RatiosLowCoverage, Codes.FactsUnavailable], CodesOf(verdict));
        Assert.Contains("Cobertura de intereses y ROE", verdict.Reasons[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Interest_coverage_that_is_not_meaningful_is_still_reported()
    {
        // Only the unread case is the presentation format; a zero denominator is a property of the figures.
        var verdict = Verdict(scoring: InterestCoverageUnreadScoring(RatioExclusionReasons.NotMeaningful));

        AssertOnly(verdict, Codes.RatiosNotMeaningful, ScoreReliabilityLevels.WithCaveats);
        Assert.Contains("Cobertura de intereses", verdict.Reasons[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Negative_equity_explains_leverage_and_roe_but_not_other_meaningless_ratios()
    {
        var scoring = CleanScoring();
        scoring.Breakdown["leverage"]            = new RatioBreakdownEntry(50m, "Atencion", true, 0m, RatioExclusionReasons.NotMeaningful);
        scoring.Breakdown["roe"]                 = new RatioBreakdownEntry(50m, "Atencion", true, 0m, RatioExclusionReasons.NotMeaningful);
        scoring.Breakdown["cobertura_intereses"] = new RatioBreakdownEntry(50m, "Atencion", true, 0m, RatioExclusionReasons.NotMeaningful);

        var verdict = Verdict(CleanFacts() with { NegativeEquity = true }, scoring);

        Assert.Equal([Codes.NegativeEquity, Codes.RatiosNotMeaningful], CodesOf(verdict));
        Assert.Equal(ScoreReliabilityLevels.WithCaveats, verdict.Level);
        Assert.Contains("No se pudo calcular Cobertura de intereses", verdict.Reasons[1].Message);
        Assert.DoesNotContain("ROE", verdict.Reasons[1].Message);
    }

    [Fact]
    public void Not_meaningful_ratios_without_negative_equity_are_all_reported()
    {
        var scoring = CleanScoring();
        scoring.Breakdown["leverage"] = new RatioBreakdownEntry(50m, "Atencion", true, 0m, RatioExclusionReasons.NotMeaningful);

        var verdict = Verdict(scoring: scoring);

        AssertOnly(verdict, Codes.RatiosNotMeaningful, ScoreReliabilityLevels.WithCaveats);
        Assert.Contains("Apalancamiento", verdict.Reasons[0].Message);
    }

    [Fact]
    public void Ratios_from_model()
        => AssertOnly(
            Verdict(CleanFacts() with { RatiosSource = ScoreReliabilityFactsBuilder.RatiosModel }),
            Codes.RatiosFromModel, ScoreReliabilityLevels.WithCaveats);

    [Fact]
    public void Amounts_unit_assumed()
        => AssertOnly(
            Verdict(CleanFacts() with { Integrity = CleanFacts().Integrity! with { AmountsUnitAssumed = true } }),
            Codes.AmountsUnitAssumed, ScoreReliabilityLevels.WithCaveats);

    [Fact]
    public void Trial_balance_is_a_caveat_never_provisional()
    {
        var verdict = Verdict(CleanFacts() with { Integrity = CleanFacts().Integrity! with { DocumentKind = TrialBalanceNormalizer.DocumentKind } });

        AssertOnly(verdict, Codes.TrialBalance, ScoreReliabilityLevels.WithCaveats);
        Assert.Equal(
            "El documento es un balance de sumas y saldos del sistema contable, no estados contables certificados.",
            Assert.Single(verdict.Reasons).Message);
    }

    [Fact]
    public void Financial_statements_raise_no_trial_balance_caveat()
        => Assert.DoesNotContain(
            Verdict(CleanFacts() with { Integrity = CleanFacts().Integrity! with { DocumentKind = "financial_statements" } }).Reasons,
            r => r.Code == Codes.TrialBalance);

    [Fact]
    public void Sector_fallback_quotes_the_sector_and_names_the_general_profile()
    {
        var verdict = Verdict(CleanFacts() with { SectorFallback = true, SectorRaw = "Servicios veterinarios" });

        AssertOnly(verdict, Codes.SectorFallback, ScoreReliabilityLevels.WithCaveats);
        Assert.Contains("«Servicios veterinarios»", verdict.Reasons[0].Message);
        Assert.Contains("«General»", verdict.Reasons[0].Message);
    }

    [Fact]
    public void Lufe_carried_forward_carries_the_as_of_date()
    {
        var facts = CleanFacts() with
        {
            Lufe = CleanFacts().Lufe! with
            {
                VentasEstado = ScoreReliabilityLufeVentasEstados.CarriedForward,
                VentasAsOf   = new DateTime(2026, 4, 3, 12, 0, 0, DateTimeKind.Utc),
            },
        };

        var verdict = Verdict(facts);

        AssertOnly(verdict, Codes.LufeCarriedForward, ScoreReliabilityLevels.WithCaveats);
        Assert.Contains("03/04/2026", verdict.Reasons[0].Message);
    }

    [Fact]
    public void Lufe_lookup_failed_is_lufe_unavailable()
        => AssertOnly(
            Verdict(CleanFacts() with { Lufe = CleanFacts().Lufe! with { VentasEstado = ScoreReliabilityLufeVentasEstados.LookupFailed } }),
            Codes.LufeUnavailable, ScoreReliabilityLevels.WithCaveats);

    [Fact]
    public void Casfog_lookup_failed_is_casfog_unavailable_and_never_stale()
    {
        var facts = CleanFacts() with
        {
            Casfog = new ScoreReliabilityCasfogFacts { Estado = nameof(CasfogConsultaEstado.LookupFailed), FetchedAt = new DateTime(2020, 1, 1) },
        };

        AssertOnly(Verdict(facts), Codes.CasfogUnavailable, ScoreReliabilityLevels.WithCaveats);
    }

    [Fact]
    public void Bcra_situacion_unreadable()
        => AssertOnly(Verdict(CleanFacts() with { BcraSituacionUnreadable = true }), Codes.BcraSituacionUnreadable, ScoreReliabilityLevels.WithCaveats);

    [Fact]
    public void Bcra_not_applicable()
    {
        var scoring = CleanScoring();
        scoring.BcraExcluded        = true;
        scoring.BcraExclusionReason = BcraExclusionReasons.NoBcraData;

        AssertOnly(Verdict(CleanFacts() with { Bcra = null }, scoring, Row(enrichment: "NotApplicable")),
                   Codes.BcraNotApplicable, ScoreReliabilityLevels.WithCaveats);
    }

    [Fact]
    public void Macro_imputed()
        => AssertOnly(Verdict(CleanFacts() with { MacroImputed = true }), Codes.MacroImputed, ScoreReliabilityLevels.WithCaveats);

    [Fact]
    public void Config_threshold_fallback_names_the_ratios()
    {
        var scoring = CleanScoring();
        scoring.ThresholdFallbacks = ["roa", "endeudamiento"];

        var verdict = Verdict(scoring: scoring);

        AssertOnly(verdict, Codes.ConfigThresholdFallback, ScoreReliabilityLevels.WithCaveats);
        Assert.Contains("Endeudamiento y ROA", verdict.Reasons[0].Message);
    }

    // ── facts_unavailable and malformed blobs ─────────────────────────────────

    [Fact]
    public void A_row_scored_before_the_facts_is_never_complete()
    {
        var verdict = Evaluate(Row(), JsonBeforeFacts())!;

        AssertOnly(verdict, Codes.FactsUnavailable, ScoreReliabilityLevels.WithCaveats);
        Assert.Equal(
            "Analizado antes de que se registrara la verificación completa de los datos; reanalizalo para verificarlo.",
            verdict.Reasons[0].Message);
    }

    [Fact]
    public void A_row_with_no_breakdown_at_all_is_facts_unavailable()
        => AssertOnly(Evaluate(Row(), null)!, Codes.FactsUnavailable, ScoreReliabilityLevels.WithCaveats);

    [Fact]
    public void Facts_written_as_null_are_facts_unavailable_with_their_own_message()
    {
        var verdict = Evaluate(Row(), Json(facts: null))!;

        AssertOnly(verdict, Codes.FactsUnavailable, ScoreReliabilityLevels.WithCaveats);
        Assert.Equal(ScoreReliabilityMessages.FactsUnavailableNotRecorded, verdict.Reasons[0].Message);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"a string\"")]
    [InlineData("""{"breakdown":"oops"}""")]
    [InlineData("""{"reliabilityFacts":{"integrity":{"passed":"yes"}}}""")]
    [InlineData("""{"reliabilityFacts":42}""")]
    public void A_malformed_breakdown_is_with_caveats_and_never_throws(string json)
    {
        var result = ScoreReliabilityAdapter.Evaluate(Row(), json);

        Assert.True(result.BreakdownUnreadable);
        AssertOnly(result.Reliability!, Codes.FactsUnavailable, ScoreReliabilityLevels.WithCaveats);
        Assert.Equal(ScoreReliabilityMessages.FactsUnavailableUnreadable, result.Reliability!.Reasons[0].Message);
    }

    [Fact]
    public void A_malformed_breakdown_still_reports_what_the_row_scalars_prove()
    {
        var verdict = Evaluate(Row(enrichment: "Degraded", fiscalYear: 2023), "{not json")!;

        Assert.Equal([Codes.BcraUnavailable, Codes.BalanceStale, Codes.FactsUnavailable], CodesOf(verdict));
        Assert.Equal(ScoreReliabilityLevels.Provisional, verdict.Level);
    }

    [Fact]
    public void An_unknown_lufe_estado_is_passed_through_without_a_reason()
    {
        var facts = CleanFacts() with { Lufe = CleanFacts().Lufe! with { VentasEstado = "something_new" } };

        Assert.Empty(Verdict(facts).Reasons);
    }

    // ── Dates: unknown is unknown, never "very old" ───────────────────────────

    [Fact]
    public void Default_and_unparseable_dates_never_fire_an_age_rule()
    {
        var facts = CleanFacts() with
        {
            Bcra   = CleanFacts().Bcra! with { PeriodoConsultado = "20xx13" },
            Lufe   = CleanFacts().Lufe! with { SnapshotAt = new DateTime(1, 1, 1), VentasUltimoPeriodo = "2026" },
            Casfog = CleanFacts().Casfog! with { FetchedAt = default },
        };

        Assert.Empty(Verdict(facts).Reasons);
    }

    [Fact]
    public void An_unknown_score_date_fires_no_age_rule()
        => Assert.Empty(Evaluate(
                new ScoreReliabilityRow(42.5m, "Complete", AnalysisKinds.Balance, 2010, default, null),
                Json(facts: CleanFacts()))!.Reasons);

    [Fact]
    public void An_unspecified_kind_date_is_read_as_utc()
    {
        // datetime2 comes back Unspecified: 30-Nov 23:00 is November, 7 months before June — stale.
        var facts = CleanFacts() with
        {
            Lufe = CleanFacts().Lufe! with { SnapshotAt = new DateTime(2025, 11, 30, 23, 0, 0, DateTimeKind.Unspecified) },
        };

        AssertOnly(Verdict(facts), Codes.LufeStale, ScoreReliabilityLevels.WithCaveats);
    }

    // ── Threshold boundaries, against the score date (2026-06-15) ─────────────

    [Theory]
    [InlineData(2024, 17, false)]   // Dec-2024 → May-2026 (one under)
    [InlineData(2024, 18, true)]    // exactly at: ≥ 18
    [InlineData(2024, 19, true)]    // one month over
    public void Balance_stale_from_31_december_at_least_18_months(int fiscalYear, int months, bool expected)
    {
        var scoreDate = new DateTime(fiscalYear, 12, 31, 0, 0, 0, DateTimeKind.Utc).AddMonths(months);
        var verdict   = Evaluate(Row(fiscalYear: fiscalYear, generated: scoreDate), Json(facts: CleanFacts() with { FiscalYear = fiscalYear }))!;

        Assert.Equal(expected, CodesOf(verdict).Contains(Codes.BalanceStale));
        if (expected)
            Assert.Contains($"al menos {months} meses", verdict.Reasons.Single(r => r.Code == Codes.BalanceStale).Message);
    }

    [Theory]
    [InlineData(2025, 12, false)]   // 6 months: exactly at
    [InlineData(2025, 11, true)]    // 7 months: one over
    public void Lufe_snapshot_stale_over_6_months(int year, int month, bool expected)
    {
        var facts = CleanFacts() with { Lufe = CleanFacts().Lufe! with { SnapshotAt = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc) } };

        Assert.Equal(expected, CodesOf(Verdict(facts)).Contains(Codes.LufeStale));
    }

    [Fact]
    public void Lufe_snapshot_age_is_calendar_months_not_days()
    {
        // 1-Dec-2025 → 30-Jun-2026 is almost 7 months by the day, but 6 calendar months: not stale.
        var facts = CleanFacts() with { Lufe = CleanFacts().Lufe! with { SnapshotAt = new DateTime(2025, 12, 1, 0, 0, 0, DateTimeKind.Utc) } };

        Assert.Empty(Verdict(facts, row: Row(generated: new DateTime(2026, 6, 30, 23, 59, 0, DateTimeKind.Utc))).Reasons);
    }

    [Theory]
    [InlineData("202509", false)]   // 9 months: exactly at
    [InlineData("202508", true)]    // 10 months: one over
    public void Lufe_sales_stale_over_9_months(string ultimoPeriodo, bool expected)
    {
        var facts   = CleanFacts() with { Lufe = CleanFacts().Lufe! with { VentasUltimoPeriodo = ultimoPeriodo } };
        var verdict = Verdict(facts);

        Assert.Equal(expected, CodesOf(verdict).Contains(Codes.LufeStale));
        if (expected)
            Assert.Contains("08/2025", verdict.Reasons.Single().Message);
    }

    [Theory]
    [InlineData(2025, 12, false)]   // 6 months: exactly at
    [InlineData(2025, 11, true)]    // 7 months: one over
    public void Casfog_stale_over_6_months(int year, int month, bool expected)
    {
        var facts   = CleanFacts() with { Casfog = CleanFacts().Casfog! with { FetchedAt = new DateTime(year, month, 20, 12, 0, 0, DateTimeKind.Utc) } };
        var verdict = Verdict(facts);

        Assert.Equal(expected, CodesOf(verdict).Contains(Codes.CasfogStale));
        if (expected)
            Assert.Contains("20/11/2025", verdict.Reasons.Single().Message);
    }

    [Theory]
    [InlineData("202602", false)]   // 4 months: exactly at
    [InlineData("202601", true)]    // 5 months: one over
    public void Bcra_period_lag_over_4_months(string periodo, bool expected)
    {
        var facts   = CleanFacts() with { Bcra = CleanFacts().Bcra! with { PeriodoConsultado = periodo } };
        var verdict = Verdict(facts);

        Assert.Equal(expected, CodesOf(verdict).Contains(Codes.BcraPeriodLag));
        if (expected)
            Assert.Contains("01/2026", verdict.Reasons.Single().Message);
    }

    [Fact]
    public void Bcra_period_is_not_judged_when_the_deuda_lookup_failed()
    {
        var facts = CleanFacts() with
        {
            Bcra = CleanFacts().Bcra! with { DeudaEstado = nameof(BcraDeudaConsultaEstado.LookupFailed), PeriodoConsultado = "201901" },
        };

        Assert.DoesNotContain(Codes.BcraPeriodLag, CodesOf(Verdict(facts)));
    }

    [Fact]
    public void A_rescored_row_is_judged_at_its_rescore_date()
    {
        // CASFOG fetched Jan-2026: 1 month before a Feb generation, 8 months before a Sep re-score. The BCRA
        // period (May-2026) is 4 months before the re-score — at its threshold, so it stays quiet.
        var facts = CleanFacts() with
        {
            Casfog = CleanFacts().Casfog! with { FetchedAt = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc) },
            Bcra   = CleanFacts().Bcra! with { PeriodoConsultado = "202605" },
        };
        var json  = Json(facts: facts);

        var generatedOnly = Evaluate(Row(generated: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)), json)!;
        var rescored      = Evaluate(Row(generated: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
                                         rescored:  new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)), json)!;

        Assert.Empty(generatedOnly.Reasons);
        AssertOnly(rescored, Codes.CasfogStale, ScoreReliabilityLevels.WithCaveats);
    }

    [Fact]
    public void The_verdict_does_not_depend_on_today()
    {
        // Same row, evaluated twice: no clock is read, so nothing can drift between calls.
        var json = ParityBreakdownJson();
        var row  = Row(score: 61.5m, enrichment: "Degraded", fiscalYear: 2024, generated: ParityGeneratedAt, rescored: ParityRescoredAt);

        Assert.Equal(Wire(Evaluate(row, json)), Wire(Evaluate(row, json)));
        Assert.Equal(ParityCodes, ParityExpected().Reasons.Select(r => r.Code));
    }
}
