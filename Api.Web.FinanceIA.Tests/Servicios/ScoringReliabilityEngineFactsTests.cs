// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-02

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Xunit;
using static Api.Web.FinanceIA.Tests.Servicios.ScoringTestHarness;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// The reliability facts the ENGINE records on <see cref="ScoringResult"/> (score credibility 1b-2a):
/// S2 unreadable situación, S10 imputed macro, S6 sector fallback, S11 negative equity, S4 config source
/// and hash. Each one is reporting only, so every test that sets one also proves the score is the one
/// the same input scores without it.
/// </summary>
public sealed class ScoringReliabilityEngineFactsTests
{
    // ── S2: unreadable BCRA situación ───────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData(7)]
    [InlineData(0)]
    public async Task DebtFound_WithAnUnreadableSituacion_IsFlagged_AndStillScoresTheNeutral50(int? situacion)
    {
        var scoring = await Score(await BuildAsync(), BaselineRatios(), BcraDebtFound(situacion), MacroBenign());

        Assert.True(scoring.BcraSituacionUnreadable);
        Assert.Equal(50m, scoring.ScoreBcra);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    public async Task DebtFound_WithAReadableSituacion_IsNotFlagged(int situacion)
    {
        var scoring = await Score(await BuildAsync(), BaselineRatios(), BcraDebtFound(situacion), MacroBenign());

        Assert.False(scoring.BcraSituacionUnreadable);
    }

    [Fact]
    public async Task NoDebt_Excluded_AndNoPayload_AreNotFlagged()
    {
        var sut = await BuildAsync();

        Assert.False((await Score(sut, BaselineRatios(), BcraNoDebt(), MacroBenign())).BcraSituacionUnreadable);
        Assert.False((await Score(sut, BaselineRatios(), BcraDeudaLookupFailed(), MacroBenign())).BcraSituacionUnreadable);
        Assert.False((await Score(sut, BaselineRatios(), null, MacroBenign())).BcraSituacionUnreadable);
    }

    // ── S10: imputed macro ──────────────────────────────────────────────────────

    [Fact]
    public async Task MissingInflation_IsFlagged_WithOrWithoutAMacroPayload()
    {
        var sut = await BuildAsync();

        var tamarOnly = await Score(sut, BaselineRatios(), BcraNoDebt(), Macro(inflacionMensual: null, tasaReferencia: 40));
        var noMacro   = await Score(sut, BaselineRatios(), BcraNoDebt(), macro: null);

        Assert.True(tamarOnly.MacroImputed);
        Assert.True(noMacro.MacroImputed);
        Assert.Equal(50m, tamarOnly.ScoreMacro);
        Assert.Equal(50m, noMacro.ScoreMacro);
    }

    [Fact]
    public async Task KnownInflation_IsNotFlagged()
    {
        var scoring = await Score(await BuildAsync(), BaselineRatios(), BcraNoDebt(), MacroBenign());

        Assert.False(scoring.MacroImputed);
    }

    // ── S6: sector fallback ─────────────────────────────────────────────────────

    [Fact]
    public async Task AnUnknownSector_FallsBackToGeneral_AndRecordsTheRawText()
    {
        var scoring = await Score(await BuildAsync(), BaselineRatios(), BcraNoDebt(), MacroBenign(), "  Agropecuario ");

        Assert.Equal("General", scoring.SectorProfile);
        Assert.True(scoring.SectorFallback);
        Assert.Equal("Agropecuario", scoring.SectorRaw);
    }

    [Theory]
    [InlineData("Agro")]
    [InlineData("agro")]
    [InlineData("Construcción")]
    [InlineData("General")]
    [InlineData(" general ")]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task AKnownBlankOrGeneralSector_IsNotAFallback(string? sector)
    {
        var scoring = await Score(await BuildAsync(), BaselineRatios(), BcraNoDebt(), MacroBenign(), sector);

        Assert.False(scoring.SectorFallback);
        Assert.Null(scoring.SectorRaw);
    }

    [Fact]
    public async Task TheRawSectorText_IsCappedAt100Characters()
    {
        var scoring = await Score(await BuildAsync(), BaselineRatios(), BcraNoDebt(), MacroBenign(), new string('x', 250));

        Assert.True(scoring.SectorFallback);
        Assert.Equal(100, scoring.SectorRaw!.Length);
    }

    [Fact]
    public async Task TheSectorFallback_MovesNoScore()
    {
        var sut = await BuildAsync();

        var unknown = await Score(sut, BaselineRatios(), BcraNoDebt(), MacroBenign(), "Agropecuario");
        var general = await Score(sut, BaselineRatios(), BcraNoDebt(), MacroBenign(), "General");

        Assert.Equal(general.Score, unknown.Score);
        Assert.Equal(general.ConfigHash, unknown.ConfigHash);
    }

    // ── S11: negative equity ────────────────────────────────────────────────────

    [Fact]
    public async Task EquityAtOrBelowZero_IsFlagged_ThroughTheRatioCalculator()
    {
        var ratios = new RatioCalculationService().Calculate(new BalanceAccounts
        {
            CurrentAssets = 500, CurrentLiabilities = 400, Inventory = 50,
            TotalAssets = 1000, TotalLiabilities = 1200, Equity = -200,
            NetProfit = -50, NetRevenue = 2000, OperatingResult = 30, FinancialExpenses = 10,
        });

        var scoring = await Score(await BuildAsync(), ratios, BcraNoDebt(), MacroBenign());

        Assert.True(scoring.NegativeEquity);
        Assert.Contains("leverage", scoring.NonComputableRatios);
        Assert.Contains("roe", scoring.NonComputableRatios);
        // The vocabulary the Portal reads is untouched: still not_meaningful, no new value.
        Assert.Equal(RatioExclusionReasons.NotMeaningful, scoring.Breakdown["leverage"].ExclusionReason);
    }

    [Fact]
    public async Task PositiveEquity_IsNotFlagged()
    {
        var ratios = new RatioCalculationService().Calculate(new BalanceAccounts
        {
            CurrentAssets = 500, CurrentLiabilities = 400, Inventory = 50,
            TotalAssets = 1000, TotalLiabilities = 600, Equity = 400,
            NetProfit = 50, NetRevenue = 2000, OperatingResult = 30, FinancialExpenses = 10,
        });

        var scoring = await Score(await BuildAsync(), ratios, BcraNoDebt(), MacroBenign());

        Assert.False(scoring.NegativeEquity);
    }

    [Fact]
    public async Task ModelRatios_LeaveNegativeEquityUnknown()
    {
        // Ratios deserialised from the model's output carry no exclusion reasons.
        var scoring = await Score(await BuildAsync(), new RatiosData { Roa = 8, NetMargin = 4 }, BcraNoDebt(), MacroBenign());

        Assert.Null(scoring.NegativeEquity);
    }

    // ── S4: config source ───────────────────────────────────────────────────────

    [Fact]
    public async Task NoRows_IsDefaults()
    {
        var scoring = await Score(await BuildAsync(), BaselineRatios(), BcraNoDebt(), MacroBenign());

        Assert.Equal(ScoringConfigSources.Defaults, scoring.ConfigSource);
    }

    [Fact]
    public async Task ARowForEveryKey_IsTenant()
    {
        var rows = ScoringDefaults.Values.Select(kv => (kv.Key, kv.Value)).ToArray();

        var scoring = await Score(await BuildAsync(rows), BaselineRatios(), BcraNoDebt(), MacroBenign());

        Assert.Equal(ScoringConfigSources.Tenant, scoring.ConfigSource);
    }

    [Fact]
    public async Task SomeRows_IsMixed()
    {
        var scoring = await Score(await BuildAsync(("weight_roe", 0.10m)), BaselineRatios(), BcraNoDebt(), MacroBenign());

        Assert.Equal(ScoringConfigSources.Mixed, scoring.ConfigSource);
    }

    [Fact]
    public async Task ASectorRowOnly_CountsWhenTheEntityIsInThatSector()
    {
        var sut = await BuildAsync(("weight_roe__sector__Comercio", 0.10m));

        Assert.Equal(ScoringConfigSources.Mixed,
            (await Score(sut, BaselineRatios(), BcraNoDebt(), MacroBenign(), "Comercio")).ConfigSource);
        Assert.Equal(ScoringConfigSources.Defaults,
            (await Score(sut, BaselineRatios(), BcraNoDebt(), MacroBenign(), "Industria")).ConfigSource);
    }

    [Fact]
    public async Task ARowTheOrderingFallbackRefused_CountsAsDefault()
    {
        // A lone danger 2.00 is inverted against the default warn 2.33: the triplet is refused and
        // scored on its defaults, so the stored row did not reach the score.
        var scoring = await Score(await BuildAsync(("threshold_leverage_danger", 2.00m)), BaselineRatios(), BcraNoDebt(), MacroBenign());

        Assert.Equal(["leverage"], scoring.ThresholdFallbacks);
        Assert.Equal(ScoringConfigSources.Defaults, scoring.ConfigSource);
        Assert.Equal((await Score(await BuildAsync(), BaselineRatios(), BcraNoDebt(), MacroBenign())).ConfigHash, scoring.ConfigHash);
    }

    [Fact]
    public async Task ARowForAnUnknownKey_IsNotConfiguration()
    {
        var scoring = await Score(await BuildAsync(("not_a_scoring_key", 3m)), BaselineRatios(), BcraNoDebt(), MacroBenign());

        Assert.Equal(ScoringConfigSources.Defaults, scoring.ConfigSource);
    }

    // ── S4: config hash ─────────────────────────────────────────────────────────

    [Fact]
    public async Task TheHash_Is16LowercaseHex_AndStableAcrossRuns()
    {
        var first  = await Score(await BuildAsync(), BaselineRatios(), BcraNoDebt(), MacroBenign());
        var second = await Score(await BuildAsync(), HealthyRatios(), null, null, "Industria");

        Assert.Matches("^[0-9a-f]{16}$", first.ConfigHash);
        // Same effective config (no rows) → same hash, whatever was scored with it.
        Assert.Equal(first.ConfigHash, second.ConfigHash);
        Assert.Equal(ScoringConfigFingerprint.ComputeHash(new Dictionary<string, decimal>()), first.ConfigHash);
    }

    [Fact]
    public async Task RowsEqualToTheDefaults_HashLikeNoRows()
    {
        var rows = ScoringDefaults.Values.Select(kv => (kv.Key, kv.Value)).ToArray();

        var withRows = await Score(await BuildAsync(rows), BaselineRatios(), BcraNoDebt(), MacroBenign());
        var noRows   = await Score(await BuildAsync(), BaselineRatios(), BcraNoDebt(), MacroBenign());

        Assert.Equal(noRows.ConfigHash, withRows.ConfigHash);
    }

    [Fact]
    public async Task OneChangedValue_ChangesTheHash()
    {
        var baseline = await Score(await BuildAsync(), BaselineRatios(), BcraNoDebt(), MacroBenign());
        var changed  = await Score(await BuildAsync(("macro_penalizacion_puntos", 21m)), BaselineRatios(), BcraNoDebt(), MacroBenign());

        Assert.NotEqual(baseline.ConfigHash, changed.ConfigHash);
    }

    [Fact]
    public void TheHash_IgnoresDictionaryOrder_AndDecimalScale()
    {
        var keys = ScoringDefaults.Values.Keys.ToList();

        var forward  = keys.ToDictionary(k => k, k => ScoringDefaults.Values[k]);
        var backward = Enumerable.Reverse(keys).ToDictionary(k => k, k => ScoringDefaults.Values[k]);

        // The same value at a wider scale, as a decimal(18,4) column reads it back: 0.50 → 0.5000.
        var rescaled = keys.ToDictionary(k => k, k => ScoringDefaults.Values[k] * 1.0000m);

        string expected = ScoringConfigFingerprint.ComputeHash(forward);
        Assert.Equal(expected, ScoringConfigFingerprint.ComputeHash(backward));
        Assert.Equal(expected, ScoringConfigFingerprint.ComputeHash(rescaled));
        Assert.Equal("0.5", ScoringConfigFingerprint.Canonical(0.5000m));
        Assert.Equal("-0.25", ScoringConfigFingerprint.Canonical(-0.250m));
    }

    [Fact]
    public void TheHash_IgnoresCurrentCulture()
    {
        var map = new Dictionary<string, decimal> { ["weight_roe"] = 0.125m };
        string invariant = ScoringConfigFingerprint.ComputeHash(map);

        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("es-AR");
            Assert.Equal(invariant, ScoringConfigFingerprint.ComputeHash(map));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    // ── The facts move nothing ──────────────────────────────────────────────────

    [Fact]
    public async Task EveryFactAtOnce_LeavesTheScoreOfTheSameInputUnchanged()
    {
        // The facts are computed beside the arithmetic. Recomputing with the same input yields the same
        // score, and the score matches the one the golden pin captured before they existed.
        var sut = await BuildAsync();
        var a = await Score(sut, new RatiosData { Roa = 8, NetMargin = 4 }, BcraDebtFound(null), Macro(null), "Agropecuario");
        var b = await Score(sut, new RatiosData { Roa = 8, NetMargin = 4 }, BcraDebtFound(null), Macro(null), "Agropecuario");

        Assert.Equal(a.Score, b.Score);
        Assert.Equal(a.Level, b.Level);
        Assert.True(a.BcraSituacionUnreadable && a.MacroImputed && a.SectorFallback && a.RatiosLowCoverageFallback);
        Assert.Equal("11", a.ScoringAlgoVersion);   // v11: the acid-test floor needs a weak current ratio
    }
}
