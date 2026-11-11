// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-09

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.BalanceCorrections;
using Xunit;

namespace Api.Web.FinanceIA.Tests.BalanceCorrections;

/// <summary>
/// The rest of Q18: a correction suggestion over a net_profit_reconciliation that failed as a RECORDED column mix
/// (result before tax and tax read from the prior-year column). Nothing is "Calculado" from the mixed columns; the
/// message says what happened; the current-year figures are proposed only when they are printed, located and verified.
/// Synthetic, TATAIKE-like figures; the REAL integrity checks.
/// </summary>
public sealed class BalanceSuggestionColumnMixTests
{
    private static readonly IBalanceIntegrityService Integrity = new BalanceIntegrityService();

    // ── The fixture is what it claims ───────────────────────────────────────────

    [Fact]
    public void The_fixture_fails_only_net_profit_reconciliation_and_records_a_column_mix()
    {
        var report = BalanceCorrectionApplier.DryRun(BalanceSuggestionFixtures.ColumnMixBalanceJson(), [], Integrity).Report!;

        var failed = Assert.Single(report.Checks, c => !c.Passed);
        Assert.Equal(BalanceIntegrityService.NetProfitReconciliationCheck, failed.Name);
        Assert.True(failed.ColumnMix);
    }

    // ── Planner and question ────────────────────────────────────────────────────

    [Fact]
    public void The_plan_carries_the_mix_only_when_the_stored_check_recorded_it()
    {
        var mixed    = BalanceSuggestionPlanner.Build(BalanceSuggestionFixtures.ColumnMixBalanceJson(), 2024)!;
        var unrecord = BalanceSuggestionPlanner.Build(BalanceSuggestionFixtures.ColumnMixBalanceJson(recordVerdict: false), 2024)!;
        var plain    = BalanceSuggestionPlanner.Build(BalanceSuggestionFixtures.NetProfitBalanceJson(), 2024)!;

        Assert.True(mixed.NetProfitColumnMix);
        Assert.True(mixed.Question.NetProfitColumnMix);
        Assert.False(unrecord.NetProfitColumnMix);   // never re-judged on read
        Assert.False(unrecord.Question.NetProfitColumnMix);
        Assert.False(plain.NetProfitColumnMix);
    }

    [Fact]
    public void The_question_names_the_comparative_column_only_under_a_recorded_mix()
    {
        var rendered = new ClaudeService.RenderedPdf([], 2, 2, [new(100, 100, 0), new(100, 100, 0)]);

        var mixedText = ClaudeService.SuggestionText(
            BalanceSuggestionPlanner.Build(BalanceSuggestionFixtures.ColumnMixBalanceJson(), 2024)!.Question, rendered);
        var unrecordedText = ClaudeService.SuggestionText(
            BalanceSuggestionPlanner.Build(BalanceSuggestionFixtures.ColumnMixBalanceJson(recordVerdict: false), 2024)!.Question, rendered);

        Assert.Contains(ClaudeService.NetProfitQuestionNote + " " + ClaudeService.NetProfitColumnMixNote, mixedText);
        Assert.DoesNotContain(ClaudeService.NetProfitColumnMixNote, unrecordedText);
        Assert.Equal(unrecordedText, mixedText.Replace(" " + ClaudeService.NetProfitColumnMixNote, string.Empty));
    }

    // ── Verifier ────────────────────────────────────────────────────────────────

    [Fact]
    public void Reconfirmed_prior_year_figures_derive_nothing_and_are_never_a_document_error()
    {
        var verdict = Verify(BalanceSuggestionFixtures.ColumnMixAnswer(
            BalanceSuggestionFixtures.MixStoredPreTax, BalanceSuggestionFixtures.MixStoredTax));

        Assert.Equal(BalanceSuggestionOutcomes.NoSuggestion, verdict.Outcome);
        Assert.Equal(BalanceSuggestionVerifier.ColumnMixNoSuggestionMessage, verdict.Message);
        Assert.Empty(verdict.Suggestions);
        Assert.Equal(0, verdict.Derived);
        Assert.DoesNotContain("Calculado", verdict.Message);
    }

    [Fact]
    public void Without_a_recorded_mix_the_same_reading_is_still_derived_as_before()
    {
        var plan    = BalanceSuggestionPlanner.Build(BalanceSuggestionFixtures.ColumnMixBalanceJson(recordVerdict: false), 2024)!;
        var verdict = BalanceSuggestionVerifier.Verify(
            plan,
            BalanceSuggestionFixtures.ColumnMixAnswer(BalanceSuggestionFixtures.MixStoredPreTax, BalanceSuggestionFixtures.MixStoredTax),
            BalanceSuggestionFixtures.NetProfitImages(),
            Integrity);

        Assert.Equal(BalanceSuggestionOutcomes.Fixes, verdict.Outcome);
        var item = Assert.Single(verdict.Suggestions);
        Assert.Equal(BalanceSuggestionBases.Derived, item.Basis);
        Assert.Equal(1_400_000d, item.SuggestedValue);
        Assert.DoesNotContain(BalanceSuggestionVerifier.ColumnMixSentence, verdict.Message);
    }

    [Fact]
    public void Located_current_year_figures_that_reconcile_are_proposed_as_printed()
    {
        var verdict = Verify(BalanceSuggestionFixtures.ColumnMixAnswer(
            BalanceSuggestionFixtures.MixCurrentPreTax, BalanceSuggestionFixtures.MixCurrentTax));

        Assert.Equal(BalanceSuggestionOutcomes.Fixes, verdict.Outcome);
        Assert.Equal(
            BalanceSuggestionVerifier.ColumnMixSentence + " " + BalanceSuggestionVerifier.ColumnMixPrintedSentence + " " +
            BalanceSuggestionVerifier.FixesMessage,
            verdict.Message);
        Assert.Equal(0, verdict.Derived);

        var byPath = verdict.Suggestions.ToDictionary(s => s.Path);
        Assert.Equal(["income_tax", "result_before_tax"], byPath.Keys.Order());
        Assert.All(verdict.Suggestions, s =>
        {
            Assert.Equal(BalanceSuggestionBases.Printed, s.Basis);
            Assert.Null(s.Derivation);
            Assert.NotNull(s.Evidence);
        });
        Assert.Equal(BalanceSuggestionFixtures.MixCurrentPreTax, byPath["result_before_tax"].SuggestedValue);
        Assert.Equal(BalanceSuggestionFixtures.MixStoredPreTax, byPath["result_before_tax"].CurrentValue);
        Assert.Equal(BalanceSuggestionFixtures.MixCurrentTax, byPath["income_tax"].SuggestedValue);
    }

    [Fact]
    public void Current_year_figures_without_a_location_are_not_proposed()
    {
        var verdict = Verify(BalanceSuggestionFixtures.ColumnMixAnswer(
            BalanceSuggestionFixtures.MixCurrentPreTax, BalanceSuggestionFixtures.MixCurrentTax, located: false));

        Assert.Equal(BalanceSuggestionOutcomes.NoSuggestion, verdict.Outcome);
        Assert.Equal(BalanceSuggestionVerifier.ColumnMixNoSuggestionMessage, verdict.Message);
        Assert.Empty(verdict.Suggestions);
        Assert.Equal(0, verdict.Derived);
    }

    [Fact]
    public void A_half_corrected_pair_that_still_does_not_reconcile_is_not_proposed()
    {
        // The pre-tax result read from the current column, the tax again from the comparative one.
        var verdict = Verify(BalanceSuggestionFixtures.ColumnMixAnswer(
            BalanceSuggestionFixtures.MixCurrentPreTax, BalanceSuggestionFixtures.MixStoredTax));

        Assert.Equal(BalanceSuggestionOutcomes.NoSuggestion, verdict.Outcome);
        Assert.Equal(BalanceSuggestionVerifier.ColumnMixNoSuggestionMessage, verdict.Message);
        Assert.Empty(verdict.Suggestions);
        Assert.Equal(0, verdict.Derived);
    }

    private static BalanceSuggestionVerdict Verify(BalanceSuggestionAnswer answer) =>
        BalanceSuggestionVerifier.Verify(
            BalanceSuggestionPlanner.Build(BalanceSuggestionFixtures.ColumnMixBalanceJson(), 2024)!,
            answer,
            BalanceSuggestionFixtures.NetProfitImages(),
            Integrity);
}
