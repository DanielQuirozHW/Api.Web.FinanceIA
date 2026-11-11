// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-02

using System.Text.Json;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Xunit;
using static Api.Web.FinanceIA.Tests.Servicios.ScoringTestHarness;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// <c>exclusionReason</c> on each per-ratio breakdown entry: "unread" (an operand was not extracted)
/// vs "not_meaningful" (operands read, formula meaningless). Reporting only: which ratios are excluded
/// and every score stay exactly what they were.
/// </summary>
public sealed class RatioExclusionReasonTests
{
    private readonly RatioCalculationService _ratios = new();

    private static BalanceAccounts Healthy() => new()
    {
        CurrentAssets      = 48_000_000,
        TotalAssets        = 80_000_000,
        CurrentLiabilities = 18_000_000,
        TotalLiabilities   = 27_000_000,
        Equity             = 53_000_000,
        NetRevenue         = 145_000_000,
        NetProfit          = 14_500_000,
        Inventory          = 12_000_000,
        OperatingResult    = 22_000_000,
        FinancialExpenses  = 3_500_000,
    };

    // ── RatioCalculationService ─────────────────────────────────────────────────

    [Fact]
    public void A_fully_read_healthy_balance_has_no_exclusion_reason()
    {
        Assert.Empty(_ratios.Calculate(Healthy()).ExclusionReasons!);
    }

    [Fact]
    public void A_null_operand_is_unread()
    {
        var r = _ratios.Calculate(Healthy() with { Inventory = null, FinancialExpenses = null, NetRevenue = null });

        Assert.Equal(RatioExclusionReasons.Unread, r.ExclusionReasons!["prueba_acida"]);
        Assert.Equal(RatioExclusionReasons.Unread, r.ExclusionReasons["cobertura_intereses"]);
        Assert.Equal(RatioExclusionReasons.Unread, r.ExclusionReasons["margen_neto"]);
        Assert.Equal(3, r.ExclusionReasons.Count);
    }

    [Fact]
    public void Non_positive_equity_is_not_meaningful_for_leverage_and_roe()
    {
        var r = _ratios.Calculate(Healthy() with { Equity = -1_000_000 });

        Assert.Null(r.Leverage);
        Assert.Null(r.Roe);
        Assert.Equal(RatioExclusionReasons.NotMeaningful, r.ExclusionReasons!["leverage"]);
        Assert.Equal(RatioExclusionReasons.NotMeaningful, r.ExclusionReasons["roe"]);
    }

    [Fact]
    public void A_zero_denominator_is_not_meaningful()
    {
        // Current assets 0 as well: since scoring algorithm 10 zero current liabilities against a POSITIVE
        // numerator is the no_current_liabilities marker instead (pinned in ScoringServiceV10Tests), so the
        // not_meaningful case for the two liquidity ratios is the one with nothing to pay with either.
        var r = _ratios.Calculate(Healthy() with
        {
            CurrentAssets = 0, Inventory = 0,
            CurrentLiabilities = 0, TotalAssets = 0, FinancialExpenses = 0, NetRevenue = 0, Equity = 0,
        });

        foreach (var key in new[] { "liquidez_corriente", "prueba_acida", "endeudamiento", "cobertura_intereses", "roa", "margen_neto", "leverage", "roe" })
            Assert.Equal(RatioExclusionReasons.NotMeaningful, r.ExclusionReasons![key]);
    }

    [Fact]
    public void Negative_inventory_is_read_but_not_meaningful()
    {
        var r = _ratios.Calculate(Healthy() with { Inventory = -5 });

        Assert.Null(r.AcidTest);
        Assert.Equal(RatioExclusionReasons.NotMeaningful, r.ExclusionReasons!["prueba_acida"]);
    }

    [Fact]
    public void Unread_wins_when_an_operand_is_null_even_if_another_makes_the_formula_meaningless()
    {
        var r = _ratios.Calculate(Healthy() with { Equity = -1, TotalLiabilities = null });

        Assert.Equal(RatioExclusionReasons.Unread, r.ExclusionReasons!["leverage"]);
        Assert.Equal(RatioExclusionReasons.NotMeaningful, r.ExclusionReasons["roe"]);
    }

    [Fact]
    public void The_reasons_never_reach_the_persisted_RatiosJson()
    {
        var json = JsonSerializer.Serialize(_ratios.Calculate(Healthy() with { Inventory = null }));

        Assert.DoesNotContain("ExclusionReasons", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unread", json);
    }

    // ── ScoringService → breakdown ──────────────────────────────────────────────

    [Fact]
    public async Task An_excluded_ratio_carries_its_reason_and_a_scored_one_carries_none()
    {
        var sut    = await BuildAsync();
        var ratios = _ratios.Calculate(Healthy() with { Equity = -1_000_000, FinancialExpenses = null });

        var r = await Score(sut, ratios, BcraNoDebt(), MacroBenign());

        Assert.True(Entry(r, "leverage").Excluded);
        Assert.Equal(RatioExclusionReasons.NotMeaningful, Entry(r, "leverage").ExclusionReason);
        Assert.Equal(RatioExclusionReasons.NotMeaningful, Entry(r, "roe").ExclusionReason);
        Assert.Equal(RatioExclusionReasons.Unread, Entry(r, "cobertura_intereses").ExclusionReason);
        Assert.False(Entry(r, "liquidez_corriente").Excluded);
        Assert.Null(Entry(r, "liquidez_corriente").ExclusionReason);
    }

    [Fact]
    public async Task The_reason_moves_no_score()
    {
        var sut          = await BuildAsync();
        var withReasons  = _ratios.Calculate(Healthy() with { Equity = -1_000_000 });
        var sameNoReason = _ratios.Calculate(Healthy() with { Equity = -1_000_000 });
        sameNoReason.ExclusionReasons = null;

        var a = await Score(sut, withReasons, BcraNoDebt(), MacroBenign());
        var b = await Score(sut, sameNoReason, BcraNoDebt(), MacroBenign());

        Assert.Equal(b.Score, a.Score);
        Assert.Equal(b.ScoreRatios, a.ScoreRatios);
        Assert.Equal(b.NonComputableRatios, a.NonComputableRatios);
        Assert.Null(Entry(b, "leverage").ExclusionReason);   // model-supplied ratios: unknown → null
        Assert.True(Entry(b, "leverage").Excluded);
    }

    // ── The persisted JSON shape ────────────────────────────────────────────────

    [Fact]
    public void A_non_excluded_entry_serialises_exactly_as_before()
    {
        var json = JsonSerializer.Serialize(new RatioBreakdownEntry(10m, "Saludable", false, 0.2m));

        Assert.Equal("""{"score":10,"status":"Saludable","excluded":false,"effectiveWeight":0.2}""", json);
    }

    [Fact]
    public void An_excluded_entry_serialises_its_reason()
    {
        var json = JsonSerializer.Serialize(new RatioBreakdownEntry(50m, "Atencion", true, 0m, RatioExclusionReasons.Unread));

        Assert.Contains("\"exclusionReason\":\"unread\"", json);
    }

    [Fact]
    public void An_old_blob_deserialises_with_a_null_reason()
    {
        var entry = JsonSerializer.Deserialize<RatioBreakdownEntry>(
            """{"score":50,"status":"Atencion","excluded":true,"effectiveWeight":0}""")!;

        Assert.True(entry.Excluded);
        Assert.Null(entry.ExclusionReason);
    }
}
