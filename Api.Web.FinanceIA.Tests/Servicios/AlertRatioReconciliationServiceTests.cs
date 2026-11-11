// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-09

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

// ── Test class ────────────────────────────────────────────────────────────────

public sealed class AlertRatioReconciliationServiceTests
{
    private readonly AlertRatioReconciliationService _sut = new();

    // ── Fixture ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The real Agropecuaria Millagro balance figures behind the reported defect.
    /// Non-current bank debt (1.670.594.267,95) is 37,4% of total liabilities but 22,6%
    /// of equity; the model divided by equity yet labeled it "del total del pasivo".
    /// </summary>
    private static BalanceAccounts Agropecuaria() => new()
    {
        TotalLiabilities = 4_462_924_519.89,
        Equity           = 7_400_335_837.16,
    };

    private const string Numerator = "$1.670.594.267,95";

    // ── The reported defect: wrong denominator gets corrected ───────────────────

    [Fact]
    public void Corrects_percentage_when_value_matches_equity_but_label_says_liabilities()
    {
        const string original =
            "Las deudas bancarias no corrientes ascienden a $1.670.594.267,95, " +
            "representando el 22,6% del total del pasivo.";

        var result = _sut.Reconcile(original, Agropecuaria());

        Assert.True(result.Corrected);
        Assert.Equal(
            "Las deudas bancarias no corrientes ascienden a $1.670.594.267,95, " +
            "representando el 37,4% del total del pasivo.",
            result.Description);
        // Only the number changed — the wrong figure is gone, everything else intact.
        Assert.DoesNotContain("22,6%", result.Description);
        Assert.NotNull(result.Note);
        Assert.Contains("37,4", result.Note);
        Assert.Contains("22,6", result.Note);          // what the text used to say
    }

    // ── Already-correct alerts are never rewritten ──────────────────────────────

    [Fact]
    public void Leaves_alert_untouched_when_percentage_already_matches_its_label()
    {
        const string original =
            "Las deudas bancarias no corrientes ascienden a $1.670.594.267,95, " +
            "representando el 37,4% del total del pasivo.";

        var result = _sut.Reconcile(original, Agropecuaria());

        Assert.False(result.Corrected);
        Assert.Equal(original, result.Description);
        Assert.Null(result.Note);
    }

    [Fact]
    public void Leaves_alert_untouched_when_equity_percentage_matches_equity_label()
    {
        const string original =
            "Las deudas bancarias no corrientes ascienden a $1.670.594.267,95, " +
            "representando el 22,6% del patrimonio neto.";

        var result = _sut.Reconcile(original, Agropecuaria());

        Assert.False(result.Corrected);
        Assert.Equal(original, result.Description);
    }

    [Fact]
    public void Rounding_difference_within_tolerance_is_not_a_correction()
    {
        // 37,43% actual vs 37,4% stated — a rounding gap, not a mislabeled denominator.
        const string original =
            "Deuda de $1.670.594.267,95, representando el 37,43% del total del pasivo.";

        var result = _sut.Reconcile(original, Agropecuaria());

        Assert.False(result.Corrected);
        Assert.Equal(original, result.Description);
    }

    // ── Correcting against other recognized denominators ────────────────────────

    [Fact]
    public void Corrects_against_equity_when_labeled_patrimonio()
    {
        const string original =
            "Deuda de $1.670.594.267,95, representando el 50% del patrimonio neto.";

        var result = _sut.Reconcile(original, Agropecuaria());

        Assert.True(result.Corrected);
        Assert.Contains("22,6% del patrimonio neto", result.Description);
    }

    [Fact]
    public void Maps_pasivo_corriente_to_current_liabilities_not_total()
    {
        var accounts = new BalanceAccounts
        {
            TotalLiabilities   = 4_462_924_519.89,
            CurrentLiabilities = 835_297_133.98,   // numerator is exactly 200% of this
        };
        const string original =
            "Deuda de $1.670.594.267,95, representando el 90% del pasivo corriente.";

        var result = _sut.Reconcile(original, accounts);

        Assert.True(result.Corrected);
        Assert.Contains("200,0% del pasivo corriente", result.Description);
    }

    // ── Fail-safe: no change unless a contradiction is provable ─────────────────

    [Fact]
    public void No_percentage_clause_no_change()
    {
        const string original = "Las deudas bancarias no corrientes ascienden a $1.670.594.267,95.";

        var result = _sut.Reconcile(original, Agropecuaria());

        Assert.False(result.Corrected);
        Assert.Equal(original, result.Description);
    }

    [Fact]
    public void Unrecognized_denominator_no_change()
    {
        const string original = "Ventas por $1.670.594.267,95, representando el 22,6% de las ventas totales.";

        var result = _sut.Reconcile(original, Agropecuaria());

        Assert.False(result.Corrected);
        Assert.Equal(original, result.Description);
    }

    [Fact]
    public void No_currency_amount_cannot_recompute_no_change()
    {
        const string original = "El endeudamiento representa el 22,6% del total del pasivo.";

        var result = _sut.Reconcile(original, Agropecuaria());

        Assert.False(result.Corrected);
        Assert.Equal(original, result.Description);
    }

    [Fact]
    public void Two_currency_amounts_are_ambiguous_no_change()
    {
        const string original =
            "Deudas por $1.670.594.267,95 y $500.000.000,00, representando el 22,6% del total del pasivo.";

        var result = _sut.Reconcile(original, Agropecuaria());

        Assert.False(result.Corrected);
        Assert.Equal(original, result.Description);
    }

    [Fact]
    public void Missing_denominator_figure_no_change()
    {
        // TotalLiabilities not extracted → cannot validate → leave the model's text as-is.
        var accounts = new BalanceAccounts { Equity = 7_400_335_837.16 };
        const string original =
            "Deuda de $1.670.594.267,95, representando el 22,6% del total del pasivo.";

        var result = _sut.Reconcile(original, accounts);

        Assert.False(result.Corrected);
        Assert.Equal(original, result.Description);
    }

    [Fact]
    public void Non_positive_denominator_no_change()
    {
        var accounts = new BalanceAccounts { TotalLiabilities = 0 };
        const string original =
            "Deuda de $1.670.594.267,95, representando el 22,6% del total del pasivo.";

        var result = _sut.Reconcile(original, accounts);

        Assert.False(result.Corrected);
        Assert.Equal(original, result.Description);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_description_no_change(string original)
    {
        var result = _sut.Reconcile(original, Agropecuaria());

        Assert.False(result.Corrected);
        Assert.Equal(original, result.Description);
    }

    // ── Part 1: numerator selection by position (the leaked bug) ────────────────
    // Real leaked example: a sentence naming BOTH the numerator and the denominator's own figure
    // (required by the extraction-prompt rule that every percentage name its total) used to trip
    // the old "exactly one currency amount" guard and reach the PDF uncorrected. Against the
    // pre-Part-1 code, Guard_fails_against_current_code below FAILS (Corrected stays false); this
    // was verified by temporarily reverting Part 1 — see the PR description for both results.

    [Fact]
    public void Guard_fails_against_current_code_two_amounts_numerator_and_named_total()
    {
        var accounts = new BalanceAccounts { TotalAssets = 12_239_430_766.71 };
        const string original =
            "Los bienes de cambio ascienden a $6.236.534.182,74, " +
            "representando el 55,4% del activo total de $12.239.430.766,71.";

        var result = _sut.Reconcile(original, accounts);

        Assert.True(result.Corrected);
        Assert.Contains("51,0% del activo total de $12.239.430.766,71", result.Description);
        Assert.DoesNotContain("55,4%", result.Description);
        Assert.NotNull(result.Note);
    }

    [Fact]
    public void Correct_two_amount_sentence_with_matching_trailing_denominator_no_change()
    {
        var accounts = new BalanceAccounts { TotalAssets = 12_239_430_766.71 };
        const string original =
            "Los bienes de cambio ascienden a $6.236.534.182,74, " +
            "representando el 51,0% del activo total de $12.239.430.766,71.";

        var result = _sut.Reconcile(original, accounts);

        Assert.False(result.Corrected);
        Assert.Equal(original, result.Description);
    }

    [Fact]
    public void Trailing_denominator_amount_disagrees_with_balance_accounts_no_change()
    {
        // The sentence's own named total ($9.000.000.000,00) does not match what BalanceAccounts
        // holds for "activo total" ($12.239.430.766,71) beyond tolerance — we cannot tell which
        // figure to trust, so we must not guess.
        var accounts = new BalanceAccounts { TotalAssets = 12_239_430_766.71 };
        const string original =
            "Los bienes de cambio ascienden a $6.236.534.182,74, " +
            "representando el 55,4% del activo total de $9.000.000.000,00.";

        var result = _sut.Reconcile(original, accounts);

        Assert.False(result.Corrected);
        Assert.Equal(original, result.Description);
    }

    // ── Part 2: executive-summary multi-clause narrative reconciliation ─────────

    [Fact]
    public void ReconcileNarrative_UsesClosestPrecedingAmount_AndConfirmsTrailingDenominatorAmount()
    {
        // Three amounts: an early, unrelated one; the real numerator immediately before the
        // clause; and the denominator's own figure immediately after it. Proves "closest", not
        // "first" or "only", drives numerator selection, and that the trailing cross-check still
        // fires correctly with extra noise elsewhere in the text.
        var accounts = Agropecuaria(); // TotalLiabilities = 4_462_924_519.89
        const string text =
            "El resultado del ejercicio fue de $50.000.000,00. " +
            "Las deudas bancarias no corrientes ascienden a $1.670.594.267,95, " +
            "representando el 90% del total del pasivo de $4.462.924.519,89.";

        var result = _sut.ReconcileNarrative(text, accounts);

        Assert.Single(result.Corrections);
        Assert.Contains("37,4% del total del pasivo de $4.462.924.519,89", result.Text);
        Assert.DoesNotContain("90%", result.Text);
        Assert.Contains("$50.000.000,00", result.Text); // irrelevant earlier amount left untouched
    }

    [Fact]
    public void ReconcileNarrative_CorrectsOnlyTheWrongClause_AmongMultipleClauses()
    {
        var accounts = new BalanceAccounts
        {
            TotalAssets      = 12_239_430_766.71,
            TotalLiabilities = 4_462_924_519.89,
        };
        const string text =
            "Los bienes de cambio ascienden a $6.236.534.182,74, representando el 55,4% del " +
            "activo total de $12.239.430.766,71. Las deudas bancarias no corrientes ascienden " +
            "a $1.670.594.267,95, representando el 37,4% del total del pasivo de $4.462.924.519,89.";

        var result = _sut.ReconcileNarrative(text, accounts);

        Assert.Single(result.Corrections);
        Assert.Contains("51,0% del activo total de $12.239.430.766,71", result.Text);
        Assert.Contains("37,4% del total del pasivo de $4.462.924.519,89", result.Text); // already correct
        Assert.DoesNotContain("55,4%", result.Text);
    }

    [Fact]
    public void ReconcileNarrative_CorrectsMultipleWrongClauses_WithoutOffsetDrift()
    {
        // Proves the rightmost-first rewrite strategy: the first clause's replacement changes the
        // text's length ("9%" → "51,0", +3 chars), which must not corrupt the second clause's
        // already-computed span.
        var accounts = new BalanceAccounts
        {
            TotalAssets      = 12_239_430_766.71,
            TotalLiabilities = 4_462_924_519.89,
        };
        const string text =
            "Los bienes de cambio ascienden a $6.236.534.182,74, representando el 9% del " +
            "activo total de $12.239.430.766,71. Las deudas bancarias no corrientes ascienden " +
            "a $1.670.594.267,95, representando el 90% del total del pasivo de $4.462.924.519,89.";

        var result = _sut.ReconcileNarrative(text, accounts);

        Assert.Equal(2, result.Corrections.Count);
        Assert.Contains("51,0% del activo total de $12.239.430.766,71", result.Text);
        Assert.Contains("37,4% del total del pasivo de $4.462.924.519,89", result.Text);
    }

    [Fact]
    public void ReconcileNarrative_no_clauses_returns_unchanged()
    {
        const string text = "El ejercicio cerró con un resultado positivo de $50.000.000,00.";

        var result = _sut.ReconcileNarrative(text, Agropecuaria());

        Assert.Empty(result.Corrections);
        Assert.Equal(text, result.Text);
    }

    // ── Sentence-bounded numerator search (PR #125 follow-up fix) ───────────────
    // ReconcileNarrative's "closest preceding amount" used to search the WHOLE text, so a clause
    // with no amount in its own sentence could reach back across a period and pick up an
    // unrelated earlier sentence's figure as its numerator.

    [Fact]
    public void ReconcileNarrative_DoesNotReachIntoAnEarlierSentence_ClauseWithNoOwnAmountLeftUntouched()
    {
        // Real Biotrust shape: three clauses, all naming "activo total". The first two each have
        // their own numerator in their own sentence and are individually correct. The third
        // ("bienes de cambio") has NO amount anywhere in its own sentence. Against the pre-fix
        // code, clause 3's search reached back across the period into clause 2's sentence and
        // "corrected" 55,4% into 8,1% — the wrong fix for the wrong reason. Fixed code must leave
        // all three untouched.
        var accounts = new BalanceAccounts { TotalAssets = 12_239_430_766.71 };
        const string text =
            "El activo corriente asciende a $11.248.036.874,60, representando el 91,9% del activo total. " +
            "El activo no corriente asciende a $990.121.846,23 (8,1% del activo total). " +
            "Dentro del activo corriente, los bienes de cambio representan el 55,4% del activo total.";

        var result = _sut.ReconcileNarrative(text, accounts);

        Assert.Empty(result.Corrections);
        Assert.Equal(text, result.Text);
    }

    // ── Unverified-clause reporting (verifiable-percentages v2.15) ──────────────
    // ReconcileNarrative used to silently drop a clause it could neither correct nor confirm —
    // the exact defect that let "los bienes de cambio representan el 55,4% del activo total"
    // (no amount in its own sentence) reach an exported report unflagged, while the same fact in
    // an alert WAS verifiable and got corrected, leaving the document contradicting itself. A
    // clause that cannot be checked must now be reported as such, distinct from a clause that WAS
    // checked and found correct.

    [Fact]
    public void ReconcileNarrative_ClauseWithNoOwnAmount_IsReportedInUnresolvedClauses()
    {
        // Same Biotrust shape as the test above: the third clause ("bienes de cambio... 55,4%...")
        // has no amount in its own sentence, so it can be neither corrected nor confirmed correct
        // — it must surface in UnresolvedClauses, while the first two clauses (successfully
        // checked and already correct) must NOT.
        var accounts = new BalanceAccounts { TotalAssets = 12_239_430_766.71 };
        const string text =
            "El activo corriente asciende a $11.248.036.874,60, representando el 91,9% del activo total. " +
            "El activo no corriente asciende a $990.121.846,23 (8,1% del activo total). " +
            "Dentro del activo corriente, los bienes de cambio representan el 55,4% del activo total.";

        var result = _sut.ReconcileNarrative(text, accounts);

        Assert.Equal(text, result.Text);
        Assert.Empty(result.Corrections);
        var unresolved = Assert.Single(result.UnresolvedClauses);
        Assert.Equal("55,4% del activo total", unresolved);
    }

    [Fact]
    public void ReconcileNarrative_EveryClauseResolvable_UnresolvedClausesIsEmpty()
    {
        // Two clauses, both with a numerator AND a resolvable denominator — one needed a
        // correction, the other was already right. "Verifiable" is independent of "correct":
        // neither clause belongs in UnresolvedClauses.
        var accounts = new BalanceAccounts
        {
            TotalAssets      = 12_239_430_766.71,
            TotalLiabilities = 4_462_924_519.89,
        };
        const string text =
            "Los bienes de cambio ascienden a $6.236.534.182,74, representando el 55,4% del " +
            "activo total de $12.239.430.766,71. Las deudas bancarias no corrientes ascienden " +
            "a $1.670.594.267,95, representando el 37,4% del total del pasivo de $4.462.924.519,89.";

        var result = _sut.ReconcileNarrative(text, accounts);

        Assert.Single(result.Corrections);
        Assert.Empty(result.UnresolvedClauses);
    }

    [Fact]
    public void ReconcileNarrative_CorrectsClauseWhenNumeratorIsInItsOwnSentence()
    {
        // Sentence-bounding must not over-suppress: a clause whose own sentence DOES contain a
        // wrong numerator is still corrected, even with an unrelated prior sentence in the text.
        var accounts = new BalanceAccounts { TotalAssets = 12_239_430_766.71 };
        const string text =
            "El resultado del ejercicio fue positivo. " +
            "Los bienes de cambio ascienden a $6.236.534.182,74, representando el 9% del activo total.";

        var result = _sut.ReconcileNarrative(text, accounts);

        Assert.Single(result.Corrections);
        Assert.Contains("51,0% del activo total", result.Text);
        Assert.DoesNotContain("9% del activo total", result.Text);
    }

    [Fact]
    public void ReconcileNarrative_DoesNotTreatThousandsSeparatorDotsAsSentenceBoundaries()
    {
        // A single sentence containing a 3-group dotted es-AR amount ($12.239.430.766,71),
        // followed later in that SAME sentence by a wrong clause, must still be corrected — proves
        // the sentence splitter doesn't false-match inside the thousands separators.
        var accounts = new BalanceAccounts { TotalAssets = 24_478_861_533.42 };
        const string text =
            "Las disponibilidades ascienden a $12.239.430.766,71 y los bienes de cambio " +
            "representan el 9% del activo total.";

        var result = _sut.ReconcileNarrative(text, accounts);

        Assert.Single(result.Corrections);
        Assert.Contains("50,0% del activo total", result.Text);
        Assert.DoesNotContain("9% del activo total", result.Text);
    }
}
