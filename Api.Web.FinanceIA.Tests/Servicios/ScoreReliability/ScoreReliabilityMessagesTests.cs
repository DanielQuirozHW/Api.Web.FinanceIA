// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-02

using System.Text.RegularExpressions;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Api.Web.FinanceIA.Servicios.ScoreReliability;
using Xunit;
using static Api.Web.FinanceIA.Tests.Servicios.ScoreReliability.ScoreReliabilityFixture;

namespace Api.Web.FinanceIA.Tests.Servicios.ScoreReliability;

/// <summary>
/// The es-AR text: every code is reachable and every message an analyst reads is Spanish — no code, no
/// snake_case key, no English ratio key, no English word. Ratio names are the Portal's labels
/// (<see cref="ClaudeService.RatioDisplayLabel"/>, the map the narrative and the PDF already share).
/// </summary>
public sealed partial class ScoreReliabilityMessagesTests
{
    [GeneratedRegex(@"\b[a-z]+_[a-z_]+\b")]
    private static partial Regex SnakeCase();

    private static readonly string[] EnglishWords =
    [
        "the", "and", "with", "from", "failed", "unavailable", "lookup", "fallback", "stale", "missing",
        "unknown", "measured", "period", "debt", "sales", "threshold", "not", "none", "null", "true", "false",
    ];

    /// <summary>Scenarios that, together, raise every code in the catalogue.</summary>
    private static IEnumerable<ScoreReliabilityResponse> KitchenSink()
    {
        var lowCoverage = CleanScoring();
        lowCoverage.RatiosLowCoverageFallback = true;
        lowCoverage.Breakdown["roa"] = new RatioBreakdownEntry(50m, "Atencion", false, 0.1m, null, Measured: false);
        lowCoverage.ThresholdFallbacks = ["liquidez_corriente"];

        var excluded = CleanScoring();
        excluded.BcraExcluded        = true;
        excluded.BcraExclusionReason = BcraExclusionReasons.LookupFailed;
        excluded.Breakdown["roa"]                 = new RatioBreakdownEntry(50m, "Atencion", true, 0m, RatioExclusionReasons.Unread);
        excluded.Breakdown["leverage"]            = new RatioBreakdownEntry(50m, "Atencion", true, 0m, RatioExclusionReasons.NotMeaningful);
        excluded.Breakdown["cobertura_intereses"] = new RatioBreakdownEntry(50m, "Atencion", true, 0m, RatioExclusionReasons.NotMeaningful);

        var allWrong = CleanFacts() with
        {
            BcraSituacionUnreadable = true,
            MacroImputed            = true,
            SectorFallback          = true,
            SectorRaw               = "Pesca artesanal",
            ManualCorrections       = 3,
            ManualCorrectionsLastAt = new DateTime(2025, 12, 1, 0, 0, 0, DateTimeKind.Utc),
            NegativeEquity          = true,
            RatiosSource            = ScoreReliabilityFactsBuilder.RatiosModel,
            Integrity = new ScoreReliabilityIntegrityFacts
            {
                Passed = false, Verified = false, FailedChecks = 3,
                FiscalYearStatus = FiscalYearCheck.Mismatch, AmountsUnitAssumed = true,
                DocumentKind = TrialBalanceNormalizer.DocumentKind,
            },
            Bcra = CleanFacts().Bcra! with
            {
                PeriodoConsultado = "202512",
                ChequesEstado     = nameof(BcraChequesConsultaEstado.LookupFailed),
            },
            Lufe = new ScoreReliabilityLufeFacts
            {
                VentasEstado        = ScoreReliabilityLufeVentasEstados.CarriedForward,
                VentasAsOf          = new DateTime(2025, 10, 1, 0, 0, 0, DateTimeKind.Utc),
                VentasUltimoPeriodo = "202505",
                SnapshotAt          = new DateTime(2025, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            },
            Casfog = CleanFacts().Casfog! with { FetchedAt = new DateTime(2025, 9, 1, 0, 0, 0, DateTimeKind.Utc) },
        };

        yield return Evaluate(Row(score: null), Json())!;
        yield return Evaluate(Row(fiscalYear: 2023), Json(lowCoverage, allWrong))!;
        yield return Evaluate(Row(enrichment: "Degraded"), Json(excluded, CleanFacts() with { Bcra = null }))!;
        yield return Evaluate(Row(enrichment: "NotApplicable"), JsonBeforeFacts())!;
        yield return Evaluate(Row(), Json(facts: CleanFacts() with
        {
            Lufe   = CleanFacts().Lufe! with { VentasEstado = ScoreReliabilityLufeVentasEstados.LookupFailed },
            Casfog = CleanFacts().Casfog! with { Estado = nameof(CasfogConsultaEstado.LookupFailed) },
        }))!;
        yield return Evaluate(Row(), Json(FlooredScoring("liquidez_corriente"),
                                          CleanFacts() with { Integrity = FailedIntegrity(["liquidez_corriente", "prueba_acida"]) }))!;
        yield return Evaluate(Row(), Json(facts: CleanFacts() with
        {
            Integrity = CleanFacts().Integrity! with
            {
                Passed = false, FailedChecks = 1, FailedCheckNames = [BalanceIntegrityService.NetProfitReconciliationCheck],
                NetProfitSignFlip = false,
            },
        }))!;
        yield return Evaluate(Row(), Json(facts: null))!;
        yield return Evaluate(Row(), "{broken")!;
    }

    [Fact]
    public void Every_code_in_the_catalogue_is_reachable()
    {
        var raised = KitchenSink().SelectMany(v => v.Reasons).Select(r => r.Code).ToHashSet();

        Assert.Equal(ScoreReliabilityCodes.Catalogue.Select(c => c.Code).ToHashSet(), raised);
    }

    [Fact]
    public void Every_message_is_spanish_and_names_no_code_or_key()
    {
        var codes   = ScoreReliabilityCodes.Catalogue.Select(c => c.Code).ToList();
        var reasons = KitchenSink().SelectMany(v => v.Reasons).ToList();

        Assert.NotEmpty(reasons);
        foreach (var reason in reasons)
        {
            var message = reason.Message;

            Assert.False(string.IsNullOrWhiteSpace(message));
            Assert.EndsWith(".", message);
            Assert.DoesNotMatch(SnakeCase(), message);
            Assert.DoesNotContain(codes, code => message.Contains(code, StringComparison.Ordinal));
            // Whole tokens: "roe" may sit inside a Spanish word; a leaked key stands alone.
            Assert.DoesNotContain(RatioKeys, key => Regex.IsMatch(message, $@"\b{key}\b"));
            foreach (var word in EnglishWords)
                Assert.DoesNotMatch(new Regex($@"\b{word}\b", RegexOptions.IgnoreCase), message);
        }
    }

    [Fact]
    public void Ratio_names_are_the_portal_labels()
    {
        foreach (var key in RatioKeys)
            Assert.Equal(ClaudeService.RatioDisplayLabel[key], ScoreReliabilityMessages.Label(key));

        Assert.Equal("Apalancamiento", ScoreReliabilityMessages.Label("leverage"));
        Assert.Equal("otro indicador", ScoreReliabilityMessages.Label("not_a_ratio"));
        Assert.Equal("Liquidez corriente, Prueba ácida y ROE",
                     ScoreReliabilityMessages.Labels(["liquidez_corriente", "prueba_acida", "roe"]));
    }

    [Fact]
    public void Singular_and_plural_agree()
    {
        Assert.StartsWith("No se pudo medir ROA ", ScoreReliabilityMessages.RatiosNotMeasured(["roa"]));
        Assert.StartsWith("No se pudieron medir ROE y ROA ", ScoreReliabilityMessages.RatiosNotMeasured(["roe", "roa"]));
        Assert.Contains("al menos 1 mes ", ScoreReliabilityMessages.BalanceStale(1, 2024));
        Assert.Contains("al menos 20 meses ", ScoreReliabilityMessages.BalanceStale(20, 2024));
    }

    /// <summary>
    /// Dates are the Argentine calendar day: 01:30Z on 2-Nov is 22:30 on 1-Nov in Argentina (UTC−3). Formatted
    /// from UTC it would print the 2nd. Periods (MM/yyyy) are YYYYMM strings with no instant, so unaffected.
    /// </summary>
    [Theory]
    [InlineData("2026-11-02T01:30:00Z", "01/11/2026")]   // after 21:00 AR: still the 1st in Argentina
    [InlineData("2026-11-02T02:59:59Z", "01/11/2026")]   // the last second of the AR day
    [InlineData("2026-11-02T03:00:00Z", "02/11/2026")]   // 00:00 AR: the 2nd begins
    [InlineData("2027-01-01T02:00:00Z", "31/12/2026")]   // across a year boundary
    public void Dates_are_formatted_in_argentine_time(string instant, string expected)
    {
        var utc = DateTime.Parse(instant, System.Globalization.CultureInfo.InvariantCulture,
                                 System.Globalization.DateTimeStyles.AdjustToUniversal);

        Assert.Equal(expected, ScoreReliabilityMessages.Date(utc));
    }

    [Fact]
    public void A_reason_message_carries_the_argentine_day()
    {
        var facts = CleanFacts() with
        {
            Lufe = CleanFacts().Lufe! with
            {
                VentasEstado = ScoreReliabilityLufeVentasEstados.CarriedForward,
                VentasAsOf   = new DateTime(2026, 11, 2, 1, 30, 0, DateTimeKind.Utc),
            },
        };

        var verdict = Evaluate(Row(generated: new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc)), Json(facts: facts))!;

        var message = verdict.Reasons.Single(r => r.Code == ScoreReliabilityCodes.LufeCarriedForward).Message;
        Assert.Contains("01/11/2026", message);
        Assert.DoesNotContain("02/11/2026", message);
    }

    [Fact]
    public void Periods_are_unaffected_by_the_time_zone()
        => Assert.Contains("período 01/2026", ScoreReliabilityMessages.BcraPeriodLag((2026, 1), 5));

    // ── integrity_unverified: one clause per check that did not run ───────────
    // The clauses are the Portal's NOT_VERIFIED_REASON_ES sentences (apps/portal/src/lib/extraction-integrity.ts),
    // first letter lowercased, final period dropped.

    [Fact]
    public void Integrity_unverified_names_one_skipped_check()
        => Assert.Equal(
            "No se pudo verificar la estructura del balance extraído: no se pudo comprobar que el pasivo total " +
            "coincida con la suma del pasivo corriente y el no corriente.",
            ScoreReliabilityMessages.IntegrityUnverified(["liabilities_components_skipped"]));

    [Fact]
    public void Integrity_unverified_names_two_skipped_checks_in_order()
        => Assert.Equal(
            "No se pudo verificar la estructura del balance extraído: no se pudo comprobar que el activo total " +
            "coincida con la suma del activo corriente y el no corriente; no se pudo comprobar que el activo total " +
            "coincida con la suma del pasivo total y el patrimonio neto.",
            ScoreReliabilityMessages.IntegrityUnverified(["assets_components_skipped", "accounting_equation_skipped"]));

    [Fact]
    public void Integrity_unverified_names_an_unavailable_evaluation()
        => Assert.Equal(
            "No se pudo verificar la estructura del balance extraído: no se pudo evaluar si las cifras del balance cierran.",
            ScoreReliabilityMessages.IntegrityUnverified(["integrity_unavailable"]));

    public static TheoryData<string[]?> UnknownReasons => new()
    {
        null,
        Array.Empty<string>(),
        new[] { "some_future_check_skipped" },
        new[] { "liabilities_components_skipped", "some_future_check_skipped" },
    };

    /// <summary>No reason, or any this build does not know: the generic sentence, never a code.</summary>
    [Theory]
    [MemberData(nameof(UnknownReasons))]
    public void Integrity_unverified_without_known_reasons_keeps_the_generic_sentence(string[]? reasons)
    {
        var message = ScoreReliabilityMessages.IntegrityUnverified(reasons);

        Assert.Equal(ScoreReliabilityMessages.IntegrityUnverifiedGeneric, message);
        Assert.Equal(
            "No se pudo verificar la estructura del balance extraído: los controles de totales no llegaron a ejecutarse.",
            message);
        Assert.DoesNotMatch(SnakeCase(), message);
    }
}
