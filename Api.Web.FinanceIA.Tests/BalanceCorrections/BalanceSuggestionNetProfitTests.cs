// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-03

using System.Text.Json;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.BalanceCorrections;
using Xunit;

namespace Api.Web.FinanceIA.Tests.BalanceCorrections;

/// <summary>
/// "Sugerir corrección" for a balance that fails ONLY net_profit_reconciliation: the reading is right and the
/// document's presentation is at fault (a pre-tax subtotal that omits a line, no pre-tax line at all), so
/// the result before tax that reconciles is DERIVED from the confirmed net result and income tax; a net
/// result read with the wrong sign is caught by its printed value. Synthetic balances, the REAL checks.
/// </summary>
public sealed class BalanceSuggestionNetProfitTests
{
    private static readonly IBalanceIntegrityService Integrity = new BalanceIntegrityService();

    private const string SignedDerivation =
        "Calculado: resultado del ejercicio de 11.861.910,77 más el impuesto a las ganancias de 2.330.167,58. " +
        "El documento no expone un subtotal antes de impuestos que cierre (por ejemplo, el RECPAM figura después " +
        "del impuesto o el subtotal omite partidas).";

    // ── Derivation wording: a sign is never doubled ─────────────────────────────

    [Theory]
    // Signed reading, a charge stored negative: net − (−tax) — the tax is ADDED back, quoted without its sign.
    [InlineData(11_861_910.77, -2_330_167.58, false,
        "Calculado: resultado del ejercicio de 11.861.910,77 más el impuesto a las ganancias de 2.330.167,58. ")]
    // Signed reading, a benefit stored positive: net − tax.
    [InlineData(11_861_910.77, 2_330_167.58, false,
        "Calculado: resultado del ejercicio de 11.861.910,77 menos el impuesto a las ganancias de 2.330.167,58. ")]
    // Magnitude reading: net + |tax|, whatever the stored sign.
    [InlineData(11_861_910.77, 2_330_167.58, true,
        "Calculado: resultado del ejercicio de 11.861.910,77 más el impuesto a las ganancias, informado como " +
        "importe positivo, de 2.330.167,58. ")]
    [InlineData(11_861_910.77, -2_330_167.58, true,
        "Calculado: resultado del ejercicio de 11.861.910,77 más el impuesto a las ganancias, informado como " +
        "importe positivo, de 2.330.167,58. ")]
    // A loss keeps its one sign; the tax is still quoted as a magnitude.
    [InlineData(-1_234.5, -300d, false,
        "Calculado: resultado del ejercicio de \u22121.234,50 más el impuesto a las ganancias de 300,00. ")]
    // No tax: nothing to add back.
    [InlineData(5_000d, 0d, false,
        "Calculado: resultado del ejercicio de 5.000,00 menos el impuesto a las ganancias de 0,00. ")]
    public void Each_derivation_step_is_worded_with_one_sign_at_most(
        double netProfit, double incomeTax, bool magnitude, string expectedStart)
    {
        var text = BalanceSuggestionVerifier.DerivationText(netProfit, incomeTax, magnitude);

        Assert.Equal(expectedStart + BalanceSuggestionVerifier.DerivationReason, text);
        Assert.DoesNotContain("(\u2212", text);
        Assert.DoesNotContain("impuesto a las ganancias de \u2212", text);
        Assert.DoesNotContain("positivo, de \u2212", text);
        Assert.DoesNotContain("menos el impuesto a las ganancias de \u2212", text);
        Assert.True(text.Count(c => c == '\u2212') <= 1);
    }

    // ── Planner ─────────────────────────────────────────────────────────────────

    [Fact]
    public void A_failed_net_profit_reconciliation_asks_the_net_result_the_tax_and_the_pre_tax_result()
    {
        var plan = BalanceSuggestionPlanner.Build(BalanceSuggestionFixtures.NetProfitBalanceJson(), 2024)!;

        Assert.Equal([BalanceIntegrityService.NetProfitReconciliationCheck], plan.FailedChecks);
        Assert.True(plan.Question.AsksNetProfit);
        Assert.Equal(["net_profit", "income_tax", "result_before_tax"], plan.Question.Fields.Select(f => f.Path));
        Assert.Equal(
            ["Resultado del ejercicio / Ganancia (Pérdida) del ejercicio", "Impuesto a las ganancias",
             "Resultado antes del impuesto a las ganancias"],
            plan.Question.Fields.Select(f => f.LabelHint));
        Assert.Empty(plan.Question.ItemGroups);
        Assert.Equal(BalanceSuggestionFixtures.PrintedPreTaxSubtotal, plan.StoredByPath["result_before_tax"]);
    }

    [Fact]
    public void The_existing_misread_result_before_tax_balance_is_now_applicable()
    {
        var plan = BalanceSuggestionPlanner.Build(BalanceCorrectionFixtures.MisreadResultBeforeTaxJson(), 2024)!;

        Assert.Equal([BalanceIntegrityService.NetProfitReconciliationCheck], plan.FailedChecks);
    }

    [Fact]
    public void A_balance_failing_only_a_sign_check_has_no_plan()
    {
        var json = BalanceSuggestionFixtures.SignOnlyBalanceJson();
        var report = BalanceCorrectionApplier.DryRun(json, [], Integrity).Report!;
        Assert.Equal([BalanceIntegrityService.SignNetRevenueCheck], report.Checks.Where(c => !c.Passed).Select(c => c.Name));

        Assert.Null(BalanceSuggestionPlanner.Build(json, 2024));
    }

    [Fact]
    public void The_prompt_says_the_pre_tax_line_may_be_missing_only_when_the_net_result_is_asked()
    {
        var rendered = new ClaudeService.RenderedPdf([], 2, 2, [new(100, 100, 0), new(100, 100, 0)]);

        var netPlan = BalanceSuggestionPlanner.Build(BalanceSuggestionFixtures.NetProfitBalanceJson(), 2024)!;
        var netText = ClaudeService.SuggestionText(netPlan.Question, rendered);
        Assert.Contains(ClaudeService.NetProfitQuestionNote, netText);
        Assert.Contains("result_before_tax: usually printed as \"Resultado antes del impuesto a las ganancias\"; stored value 12500000.", netText);
        Assert.Contains("net_profit: usually printed as \"Resultado del ejercicio / Ganancia (Pérdida) del ejercicio\"; stored value 11861910.77.", netText);

        var structurePlan = BalanceSuggestionPlanner.Build(BalanceSuggestionFixtures.SidewaysBalanceJson(), 2024)!;
        Assert.False(structurePlan.Question.AsksNetProfit);
        Assert.DoesNotContain(ClaudeService.NetProfitQuestionNote, ClaudeService.SuggestionText(structurePlan.Question, rendered));
    }

    // ── Verifier: derived result_before_tax ─────────────────────────────────────

    [Fact]
    public void Case_1_a_printed_pre_tax_subtotal_that_omits_a_line_gets_a_derived_result_before_tax()
    {
        var plan   = BalanceSuggestionPlanner.Build(BalanceSuggestionFixtures.NetProfitBalanceJson(), 2024)!;
        var images = BalanceSuggestionFixtures.NetProfitImages();

        // The model confirms the three printed figures — the subtotal as printed is the wrong one.
        var verdict = BalanceSuggestionVerifier.Verify(plan, BalanceSuggestionFixtures.NetProfitAnswer(), images, Integrity);

        Assert.Equal(BalanceSuggestionOutcomes.Fixes, verdict.Outcome);
        Assert.Equal(BalanceSuggestionVerifier.DerivedFixesMessage, verdict.Message);
        Assert.Equal([BalanceIntegrityService.NetProfitReconciliationCheck], verdict.FailedChecksBefore);
        Assert.Empty(verdict.FailedChecksAfter);
        Assert.Equal(1, verdict.Derived);

        var item = Assert.Single(verdict.Suggestions);
        Assert.Equal("result_before_tax", item.Path);
        Assert.Equal(BalanceSuggestionBases.Derived, item.Basis);
        Assert.Equal(BalanceSuggestionFixtures.PrintedPreTaxSubtotal, item.CurrentValue);
        Assert.Equal(BalanceSuggestionFixtures.DerivedResultBeforeTax, item.SuggestedValue, 2);
        Assert.Equal(SignedDerivation, item.Derivation);
        Assert.Equal("Resultado antes del impuesto a las ganancias", item.PrintedLabel);

        // Shown where the net result is printed; the tax line's crop beside it; both pages referenced.
        Assert.Equal(1, item.Page);
        Assert.NotNull(item.Evidence);
        Assert.NotNull(item.EvidenceSecondary);
        Assert.Equal(new BalanceSuggestionHighlight(1, 0.40, 0.44, 0d, 1d), item.Highlight);
        Assert.Equal(2, item.SecondaryPage);
        Assert.Equal([1, 2], BalanceSuggestionEvidence.Pages(verdict.Suggestions, images).Select(p => p.Page));

        // Applying the derived figure through the real applier passes every check.
        var applied = BalanceCorrectionApplier.DryRun(
            plan.BalanceJson,
            [new BalanceCorrectionEntry(BalanceCorrectionPath.TryParse(item.Path)!, item.SuggestedValue)],
            Integrity).Report!;
        Assert.True(applied.Passed);
    }

    [Fact]
    public void Case_2_no_printed_pre_tax_line_gets_a_derived_result_before_tax()
    {
        var plan   = BalanceSuggestionPlanner.Build(BalanceSuggestionFixtures.NetProfitBalanceJson(), 2024)!;
        var answer = BalanceSuggestionFixtures.NetProfitAnswer(resultBeforeTax: null);   // RECPAM after the tax: no line

        var verdict = BalanceSuggestionVerifier.Verify(plan, answer, BalanceSuggestionFixtures.NetProfitImages(), Integrity);

        Assert.Equal(BalanceSuggestionOutcomes.Fixes, verdict.Outcome);
        var item = Assert.Single(verdict.Suggestions);
        Assert.Equal(BalanceSuggestionBases.Derived, item.Basis);
        Assert.Equal(BalanceSuggestionFixtures.DerivedResultBeforeTax, item.SuggestedValue, 2);
        Assert.Equal(SignedDerivation, item.Derivation);
        Assert.Null(item.PrintedLabel);
        Assert.NotNull(item.Evidence);
    }

    [Fact]
    public void A_tax_stored_as_a_positive_magnitude_uses_the_magnitude_reading()
    {
        const double tax = 2_330_167.58;
        var plan   = BalanceSuggestionPlanner.Build(BalanceSuggestionFixtures.NetProfitBalanceJson(incomeTax: tax), 2024)!;
        var answer = BalanceSuggestionFixtures.NetProfitAnswer(incomeTax: tax);

        var verdict = BalanceSuggestionVerifier.Verify(plan, answer, BalanceSuggestionFixtures.NetProfitImages(), Integrity);

        Assert.Equal(BalanceSuggestionOutcomes.Fixes, verdict.Outcome);
        var item = Assert.Single(verdict.Suggestions);
        Assert.Equal(BalanceSuggestionFixtures.DerivedResultBeforeTax, item.SuggestedValue, 2);   // net + |tax|, not net − tax
        Assert.StartsWith(
            "Calculado: resultado del ejercicio de 11.861.910,77 más el impuesto a las ganancias, informado como " +
            "importe positivo, de 2.330.167,58.",
            item.Derivation);
    }

    [Fact]
    public void A_net_result_read_without_its_parentheses_is_a_printed_suggestion()
    {
        // The document prints a loss: (11.861.910,77) before a tax charge, so (9.531.743,19) before tax.
        const double preTaxLoss = -9_531_743.19;
        var plan   = BalanceSuggestionPlanner.Build(BalanceSuggestionFixtures.NetProfitBalanceJson(preTaxLoss), 2024)!;
        var answer = BalanceSuggestionFixtures.NetProfitAnswer(
            netProfit: -BalanceSuggestionFixtures.NetProfit, netMatches: false, resultBeforeTax: preTaxLoss);

        var verdict = BalanceSuggestionVerifier.Verify(plan, answer, BalanceSuggestionFixtures.NetProfitImages(), Integrity);

        Assert.Equal(BalanceSuggestionOutcomes.Fixes, verdict.Outcome);
        Assert.Equal(BalanceSuggestionVerifier.FixesMessage, verdict.Message);
        Assert.Equal(0, verdict.Derived);
        var item = Assert.Single(verdict.Suggestions);
        Assert.Equal("net_profit", item.Path);
        Assert.Equal(BalanceSuggestionBases.Printed, item.Basis);
        Assert.Equal(-BalanceSuggestionFixtures.NetProfit, item.SuggestedValue);
        Assert.Equal(BalanceSuggestionFixtures.NetProfit, item.CurrentValue);
        Assert.Null(item.Derivation);
        Assert.Null(item.EvidenceSecondary);
        Assert.Null(item.SecondaryPage);
        Assert.Equal("Ganancia del ejercicio", item.PrintedLabel);
    }

    [Fact]
    public void Every_figure_confirmed_and_no_derivation_possible_is_a_document_error()
    {
        // The derived figure (net − tax = 1,1e15) is outside the correction's range: nothing reconciles.
        var json = BalanceSuggestionFixtures.NetProfitBalanceJson(
            resultBeforeTax: 100_000_000_000_000d, incomeTax: -200_000_000_000_000d, netProfit: 900_000_000_000_000d);
        var plan   = BalanceSuggestionPlanner.Build(json, 2024)!;
        var answer = BalanceSuggestionFixtures.NetProfitAnswer(
            netProfit: 900_000_000_000_000d, incomeTax: -200_000_000_000_000d, resultBeforeTax: 100_000_000_000_000d);

        var verdict = BalanceSuggestionVerifier.Verify(plan, answer, BalanceSuggestionFixtures.NetProfitImages(), Integrity);

        Assert.Equal(BalanceSuggestionOutcomes.DocumentError, verdict.Outcome);
        Assert.Empty(verdict.Suggestions);
        Assert.Equal(
            "El balance presentado no cierra: el resultado del ejercicio no coincide con el resultado antes del " +
            "impuesto a las ganancias neto de ese impuesto. Las cifras extraídas coinciden con las impresas en el " +
            "documento, así que no hay una corrección que sugerir.",
            verdict.Message);
    }

    [Fact]
    public void Nothing_is_derived_from_a_tax_the_model_did_not_find()
    {
        var plan   = BalanceSuggestionPlanner.Build(BalanceSuggestionFixtures.NetProfitBalanceJson(), 2024)!;
        var answer = BalanceSuggestionFixtures.NetProfitAnswer(incomeTax: null);

        var verdict = BalanceSuggestionVerifier.Verify(plan, answer, BalanceSuggestionFixtures.NetProfitImages(), Integrity);

        Assert.Equal(BalanceSuggestionOutcomes.NoSuggestion, verdict.Outcome);
        Assert.Empty(verdict.Suggestions);
    }

    [Fact]
    public void Nothing_is_derived_from_a_net_result_the_model_denies()
    {
        var plan   = BalanceSuggestionPlanner.Build(BalanceSuggestionFixtures.NetProfitBalanceJson(), 2024)!;
        var answer = BalanceSuggestionFixtures.NetProfitAnswer(netMatches: false);   // same value, but "does not match"

        var verdict = BalanceSuggestionVerifier.Verify(plan, answer, BalanceSuggestionFixtures.NetProfitImages(), Integrity);

        Assert.Equal(BalanceSuggestionOutcomes.NoSuggestion, verdict.Outcome);
    }

    [Theory]
    [InlineData(-2_330_167.58, "−2.330.167,58")]
    [InlineData(11_861_910.77, "11.861.910,77")]
    [InlineData(1_234.5, "1.234,50")]
    [InlineData(0d, "0,00")]
    [InlineData(-0.001, "0,00")]
    public void Amounts_are_written_in_es_AR(double value, string expected)
        => Assert.Equal(expected, BalanceSuggestionVerifier.Amount(value));

    // ── Contract ────────────────────────────────────────────────────────────────

    [Fact]
    public void Basis_derivation_and_the_secondary_evidence_are_serialized_and_the_secondary_page_is_not()
    {
        var item = new BalanceCorrectionSuggestionItem
        {
            Path              = "result_before_tax",
            SuggestedValue    = 1d,
            Basis             = BalanceSuggestionBases.Derived,
            Derivation        = "Calculado: …",
            EvidenceSecondary = new BalanceSuggestionEvidenceResponse("image/jpeg", "AAAA"),
            SecondaryPage     = 2,
        };

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(item, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var root = doc.RootElement;

        Assert.Equal("derived", root.GetProperty("basis").GetString());
        Assert.Equal("Calculado: …", root.GetProperty("derivation").GetString());
        Assert.Equal("AAAA", root.GetProperty("evidenceSecondary").GetProperty("data").GetString());
        Assert.False(root.TryGetProperty("secondaryPage", out _));
    }

    [Fact]
    public void An_item_without_basis_reads_as_printed()
    {
        var item = JsonSerializer.Deserialize<BalanceCorrectionSuggestionItem>(
            """{"path":"current_liabilities","suggestedValue":150000000}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal(BalanceSuggestionBases.Printed, item.Basis);
        Assert.Null(item.Derivation);
        Assert.Null(item.EvidenceSecondary);
    }

    [Fact]
    public void The_cache_size_estimate_counts_the_secondary_evidence_and_the_derivation()
    {
        var bare = new BalanceCorrectionSuggestionResponse
        {
            Outcome     = BalanceSuggestionOutcomes.Fixes,
            Message     = "m",
            Model       = "x",
            Suggestions = [new BalanceCorrectionSuggestionItem { Path = "result_before_tax", SuggestedValue = 1d }],
        };
        var derived = bare with
        {
            Suggestions =
            [
                bare.Suggestions[0] with
                {
                    Derivation        = new string('d', 100),
                    EvidenceSecondary = new BalanceSuggestionEvidenceResponse("image/jpeg", new string('A', 1000)),
                },
            ],
        };

        Assert.Equal(BalanceSuggestionCache.EstimatedBytes(bare) + 1100, BalanceSuggestionCache.EstimatedBytes(derived));
    }
}
