// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-01

using System.Globalization;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// Pins the two config-robustness rules in <c>SettingsService.ValidateScoringConfig</c>.
/// <para>
/// GAP THIS FILLS: no test in the project had ever constructed <see cref="SettingsService"/> or
/// reached <c>ValidateScoringConfig</c> — the only scoring-config tests are the two authorization
/// reflection suites, which invoke no service. Every rule the validator enforces was unexercised.
/// </para>
/// <para>
/// The validator is <c>private static</c>, so the only way in is
/// <c>UpdateScoringConfigAsync</c>. It runs on the full post-merge map (defaults → tenant DB rows →
/// submitted items) BEFORE any persistence, so a rejected payload leaves the table untouched — which
/// is why these fixtures can seed nothing and still submit a single key. Failure shape is a bare
/// <see cref="InvalidOperationException"/> carrying an English sentence;
/// <c>SettingsController</c> turns that into a 400 ProblemDetails with the message as
/// <c>detail</c>.
/// </para>
/// <para>
/// These are the FIRST of the two defence-in-depth layers. The engine-layer counterparts — what
/// happens when such a config reaches <c>Compute()</c> anyway, from a row written before these
/// rules existed — are pinned in
/// <c>ScoringServiceConfigTests.Calculate_WhenRatioAndMacroWeightsAreBothZero_AndBcraExcluded_FallsBackToTheDefaultWeights</c>
/// and
/// <c>ScoringServiceFloorAndLevelTests.Calculate_WhenTheConfiguredFloorExceeds100_TheAppliedFloorIsClampedTo100</c>.
/// </para>
/// </summary>
public sealed class SettingsServiceScoringValidationTests
{
    private const string Tenant = "tenant-settings";

    // ── 1. Component weights must be COMPUTABLE, not merely normalised ─────────────────────────
    //
    // 0 / 1.00 / 0 sums to 1.0 and clears the sum-to-1.0 rule, but it is unusable: when BCRA is
    // excluded the engine renormalises over ratios+macro only, and both are zero. With the sum
    // pinned at 1.0, "ratios == 0 and macro == 0" IS "bcra is the only non-zero weight", so
    // requiring two non-zero weights rejects exactly the collapsing configs and nothing else.

    [Fact]
    public async Task UpdateScoringConfig_WhenOnlyOneComponentWeightIsNonZero_IsRejected()
    {
        var sut = BuildService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.UpdateScoringConfigAsync(
                Guid.NewGuid(), Tenant,
                Request(
                    ("weight_component_ratios", 0m),
                    ("weight_component_bcra",   1.00m),
                    ("weight_component_macro",  0m)),
                ipAddress: null, requestId: null, ct: CancellationToken.None));

        // Not the sum rule — this payload sums to exactly 1.0 and passes it.
        Assert.Contains("At least two", ex.Message);
    }

    // Two non-zero weights always leave a non-zero renormalisation denominator, so both rows are
    // computable and must be accepted. The 0.50 / 0.50 / 0 row is the load-bearing one: it proves
    // the rule is two-of-three, not all-three — a stricter check would reject a tenant who has
    // legitimately zeroed the macro component.

    [Theory]
    [InlineData("0.50", "0.35", "0.15")]   // the shipped default, and what DDL 007 seeds
    [InlineData("0.50", "0.50", "0.00")]   // macro deliberately zeroed — still computable
    public async Task UpdateScoringConfig_WhenTwoOrMoreComponentWeightsAreNonZero_IsAccepted(
        string ratios, string bcra, string macro)
    {
        var sut = BuildService();

        var dto = await sut.UpdateScoringConfigAsync(
            Guid.NewGuid(), Tenant,
            Request(
                ("weight_component_ratios", Dec(ratios)),
                ("weight_component_bcra",   Dec(bcra)),
                ("weight_component_macro",  Dec(macro))),
            ipAddress: null, requestId: null, ct: CancellationToken.None);

        Assert.NotNull(dto);
    }

    // ── 2. Every critical_floor_* value must sit on the 0-100 score scale ──────────────────────
    //
    // A floor is a point on the final-score scale. 150 is not one: applied after the composite is
    // clamped, it used to become the score outright. The guard walks
    // ScoringDefaults.CriticalFloorKeys — all nine keys, the eight per-ratio floors plus the
    // trigger threshold, which is compared against a partial score that PartialScore bounds to
    // [0,100] too — so a key added to that array in future is covered without touching this rule.

    [Fact]
    public async Task UpdateScoringConfig_WhenACriticalFloorExceeds100_IsRejected()
    {
        var sut = BuildService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.UpdateScoringConfigAsync(
                Guid.NewGuid(), Tenant,
                Request(("critical_floor_endeudamiento", 150.00m)),
                ipAddress: null, requestId: null, ct: CancellationToken.None));

        Assert.Contains("critical_floor_endeudamiento", ex.Message);   // the rule names the key
        Assert.Contains("between 0 and 100", ex.Message);
    }

    // 100 is the inclusive upper bound — a floor that pins the score at the top of the scale is
    // extreme but coherent, and rejecting it would be an off-by-one. 65 is the shipped default for
    // this key, so it must round-trip untouched.

    [Theory]
    [InlineData("100.00")]   // the bound itself — inclusive
    [InlineData("65.00")]    // the shipped default for this key
    public async Task UpdateScoringConfig_WhenACriticalFloorIsWithinScale_IsAccepted(string floor)
    {
        var sut = BuildService();

        var dto = await sut.UpdateScoringConfigAsync(
            Guid.NewGuid(), Tenant,
            Request(("critical_floor_endeudamiento", Dec(floor))),
            ipAddress: null, requestId: null, ct: CancellationToken.None);

        Assert.NotNull(dto);
    }

    // ── 3. The rejected-cheques penalty keys (§7.1) ────────────────────────────────────────────
    //
    // Two keys of different kinds sharing one decimal(10,4) column, so they need different rules.
    //
    // bcra_cheques_penalizacion_plena is a number of POINTS on the 0-100 scoreBcra scale — the same
    // kind of value as a critical floor, and out of range for the same reason: a 500-point "penalty"
    // is not a penalty. The engine clamps it too, so this is the first of two layers.

    [Fact]
    public async Task UpdateScoringConfig_WhenTheChequesPenaltyExceeds100_IsRejected()
    {
        var sut = BuildService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.UpdateScoringConfigAsync(
                Guid.NewGuid(), Tenant,
                Request(("bcra_cheques_penalizacion_plena", 150.00m)),
                ipAddress: null, requestId: null, ct: CancellationToken.None));

        Assert.Contains("bcra_cheques_penalizacion_plena", ex.Message);
        Assert.Contains("between 0 and 100", ex.Message);
    }

    [Theory]
    [InlineData("15.00")]    // the shipped default
    [InlineData("100.00")]   // the inclusive upper bound
    [InlineData("0.00")]     // a tenant that keeps the signal but wants it toothless
    public async Task UpdateScoringConfig_WhenTheChequesPenaltyIsWithinScale_IsAccepted(string plena)
    {
        var sut = BuildService();

        var dto = await sut.UpdateScoringConfigAsync(
            Guid.NewGuid(), Tenant,
            Request(("bcra_cheques_penalizacion_plena", Dec(plena))),
            ipAddress: null, requestId: null, ct: CancellationToken.None);

        Assert.NotNull(dto);
    }

    // bcra_cheques_enabled is a BOOLEAN with nowhere boolean to live. The engine reads "> 0" as on,
    // which would take 7 or 0.5 as "enabled" — values whose author plainly meant something else, and
    // which would sit in the tenant's config forever looking like a threshold. Only 0 and 1 are
    // meanings this key has.

    [Theory]
    [InlineData("2")]
    [InlineData("0.5")]
    public async Task UpdateScoringConfig_WhenTheChequesFlagIsNotZeroOrOne_IsRejected(string flag)
    {
        var sut = BuildService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.UpdateScoringConfigAsync(
                Guid.NewGuid(), Tenant,
                Request(("bcra_cheques_enabled", Dec(flag))),
                ipAddress: null, requestId: null, ct: CancellationToken.None));

        Assert.Contains("bcra_cheques_enabled", ex.Message);
        Assert.Contains("0 (off) or 1 (on)", ex.Message);
    }

    [Theory]
    [InlineData("0")]   // the tenant switches the signal off
    [InlineData("1")]   // the shipped default
    public async Task UpdateScoringConfig_WhenTheChequesFlagIsZeroOrOne_IsAccepted(string flag)
    {
        var sut = BuildService();

        var dto = await sut.UpdateScoringConfigAsync(
            Guid.NewGuid(), Tenant,
            Request(("bcra_cheques_enabled", Dec(flag))),
            ipAddress: null, requestId: null, ct: CancellationToken.None);

        Assert.NotNull(dto);
    }

    // ── 4. The LUFE sales-drop penalty keys (§7.3a) ────────────────────────────────────────────
    //
    // THE REGRESSION GUARD FIRST. ValidateScoringConfig opens with a blanket "every value must be
    // >= 0" walk over the MERGED map — defaults overlaid with the tenant's rows — and §7.3 ships TWO
    // NEGATIVE defaults (lufe_ventas_umbral_inicio = -0.10, lufe_ventas_umbral_plena = -0.35, since
    // the recalibration gave the start threshold a dead-band). Without the SignedScoringKeys exemption
    // that blanket rule rejects the shipped defaults themselves, so EVERY PUT would fail for EVERY
    // tenant on EVERY key, including keys that have nothing to do with LUFE. This test fails loudly
    // if the exemption is ever removed or the key list drifts.

    [Fact]
    public async Task UpdateScoringConfig_WithTheShippedNegativeDefaults_StillSucceeds()
    {
        var sut = BuildService();

        // An edit to an unrelated key: the negative sales thresholds are present only because
        // MergeWithDefaults seeds every default into the map being validated.
        var dto = await sut.UpdateScoringConfigAsync(
            Guid.NewGuid(), Tenant,
            Request(("macro_penalizacion_puntos", 6.00m)),
            ipAddress: null, requestId: null, ct: CancellationToken.None);

        Assert.NotNull(dto);
    }

    // The exemption is narrow: it buys the two threshold keys out of the >= 0 rule and nothing else.
    // Every other key stays negative-rejecting, including the LUFE keys that are not thresholds.
    [Theory]
    [InlineData("lufe_ventas_penalizacion_plena")]
    [InlineData("lufe_ventas_enabled")]
    [InlineData("macro_penalizacion_puntos")]
    public async Task UpdateScoringConfig_WhenANonSignedKeyIsNegative_IsStillRejected(string key)
    {
        var sut = BuildService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.UpdateScoringConfigAsync(
                Guid.NewGuid(), Tenant,
                Request((key, Dec("-1.00"))),
                ipAddress: null, requestId: null, ct: CancellationToken.None));

        Assert.Contains(key, ex.Message);
    }

    // The thresholds are signed VARIATION FRACTIONS. [-1,0] is the whole meaningful range: -1 is a
    // total collapse to zero sales, and a POSITIVE threshold would make growth penalise — inverting
    // the signal's entire premise.
    [Theory]
    [InlineData("lufe_ventas_umbral_inicio",  "0.10")]    // positive → growth would penalise
    [InlineData("lufe_ventas_umbral_plena",   "0.10")]
    [InlineData("lufe_ventas_umbral_plena",  "-1.50")]    // beyond a total collapse
    public async Task UpdateScoringConfig_WhenAThresholdLeavesMinusOneToZero_IsRejected(
        string key, string value)
    {
        var sut = BuildService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.UpdateScoringConfigAsync(
                Guid.NewGuid(), Tenant,
                Request((key, Dec(value))),
                ipAddress: null, requestId: null, ct: CancellationToken.None));

        Assert.Contains(key, ex.Message);
        Assert.Contains("between -1 and 0", ex.Message);
    }

    // Parameterised by KEY as well as value: 0.00 is the inclusive upper bound of the range, but it
    // is only a legal umbral_PLENA when umbral_inicio is 0.00 too. Since the recalibration moved the
    // default start threshold to −0.10, a lone "umbral_plena = 0.00" now trips the ordering rule
    // below instead — so the bound is exercised on umbral_inicio, where it is still reachable.
    [Theory]
    [InlineData("lufe_ventas_umbral_inicio", "0.00")]    // the inclusive upper bound
    [InlineData("lufe_ventas_umbral_inicio", "-0.10")]   // the shipped default for umbral_inicio
    [InlineData("lufe_ventas_umbral_plena",  "-0.35")]   // the shipped default for umbral_plena
    [InlineData("lufe_ventas_umbral_plena",  "-1.00")]   // the inclusive lower bound
    public async Task UpdateScoringConfig_WhenAThresholdIsInRange_IsAccepted(string key, string value)
    {
        var sut = BuildService();

        var dto = await sut.UpdateScoringConfigAsync(
            Guid.NewGuid(), Tenant,
            Request((key, Dec(value))),
            ipAddress: null, requestId: null, ct: CancellationToken.None);

        Assert.NotNull(dto);
    }

    // The penalty ramps DOWNWARD from the start threshold to the full-penalty one. Inverted, the
    // config no longer describes a penalty at all.
    [Fact]
    public async Task UpdateScoringConfig_WhenTheFullThresholdSitsAboveTheStart_IsRejected()
    {
        var sut = BuildService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.UpdateScoringConfigAsync(
                Guid.NewGuid(), Tenant,
                Request(("lufe_ventas_umbral_inicio", Dec("-0.40")),
                        ("lufe_ventas_umbral_plena",  Dec("-0.10"))),
                ipAddress: null, requestId: null, ct: CancellationToken.None));

        Assert.Contains("lufe_ventas_umbral_plena", ex.Message);
        Assert.Contains("less than or equal to", ex.Message);
    }

    // Equal thresholds are legal and mean a step at that point — the engine handles that arm
    // explicitly rather than dividing by a zero range.
    [Fact]
    public async Task UpdateScoringConfig_WhenTheThresholdsAreEqual_IsAccepted()
    {
        var sut = BuildService();

        var dto = await sut.UpdateScoringConfigAsync(
            Guid.NewGuid(), Tenant,
            Request(("lufe_ventas_umbral_inicio", Dec("-0.20")),
                    ("lufe_ventas_umbral_plena",  Dec("-0.20"))),
            ipAddress: null, requestId: null, ct: CancellationToken.None);

        Assert.NotNull(dto);
    }

    [Fact]
    public async Task UpdateScoringConfig_WhenTheVentasPenaltyExceeds100_IsRejected()
    {
        var sut = BuildService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.UpdateScoringConfigAsync(
                Guid.NewGuid(), Tenant,
                Request(("lufe_ventas_penalizacion_plena", 150.00m)),
                ipAddress: null, requestId: null, ct: CancellationToken.None));

        Assert.Contains("lufe_ventas_penalizacion_plena", ex.Message);
        Assert.Contains("between 0 and 100", ex.Message);
    }

    [Theory]
    [InlineData("0.5")]   // "half on" is not a state this flag has
    [InlineData("7")]
    public async Task UpdateScoringConfig_WhenTheVentasFlagIsNotZeroOrOne_IsRejected(string flag)
    {
        var sut = BuildService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.UpdateScoringConfigAsync(
                Guid.NewGuid(), Tenant,
                Request(("lufe_ventas_enabled", Dec(flag))),
                ipAddress: null, requestId: null, ct: CancellationToken.None));

        Assert.Contains("lufe_ventas_enabled", ex.Message);
        Assert.Contains("must be 0 (off) or 1 (on)", ex.Message);
    }

    [Theory]
    [InlineData("0")]   // the tenant switches the signal off
    [InlineData("1")]   // the shipped default
    public async Task UpdateScoringConfig_WhenTheVentasFlagIsZeroOrOne_IsAccepted(string flag)
    {
        var sut = BuildService();

        var dto = await sut.UpdateScoringConfigAsync(
            Guid.NewGuid(), Tenant,
            Request(("lufe_ventas_enabled", Dec(flag))),
            ipAddress: null, requestId: null, ct: CancellationToken.None);

        Assert.NotNull(dto);
    }

    // ── The CASFOG guarantee-arrears keys (§7.4) ──────────────────────────────
    //
    // These umbrales are the mirror of the ventas ones: a share of a balance, so POSITIVE by
    // construction and in [0,1]. They are deliberately NOT in SignedScoringKeys, which means the
    // blanket ">= 0" rule already covers their lower bound and this block only adds the upper one —
    // and it also means the shipped defaults (0.05 and 0.40) pass that blanket rule untouched, which
    // is the failure mode §7.3a had to work around.

    [Theory]
    [InlineData("casfog_mora_umbral_inicio", "1.50")]    // beyond the whole balance
    [InlineData("casfog_mora_umbral_plena",  "2.00")]
    public async Task UpdateScoringConfig_WhenACasfogThresholdExceedsOne_IsRejected(string key, string value)
    {
        var sut = BuildService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.UpdateScoringConfigAsync(
                Guid.NewGuid(), Tenant,
                Request((key, Dec(value))),
                ipAddress: null, requestId: null, ct: CancellationToken.None));

        Assert.Contains(key, ex.Message);
        Assert.Contains("between 0 and 1", ex.Message);
    }

    // A negative threshold is caught by the blanket rule rather than by the [0,1] bound, because these
    // keys are not exempted from it. Either way it is rejected — this pins that the exemption was NOT
    // extended to them by accident.
    [Theory]
    [InlineData("casfog_mora_umbral_inicio")]
    [InlineData("casfog_mora_umbral_plena")]
    public async Task UpdateScoringConfig_WhenACasfogThresholdIsNegative_IsRejected(string key)
    {
        var sut = BuildService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.UpdateScoringConfigAsync(
                Guid.NewGuid(), Tenant,
                Request((key, Dec("-0.10"))),
                ipAddress: null, requestId: null, ct: CancellationToken.None));

        Assert.Contains(key, ex.Message);
        Assert.Contains("greater than or equal to zero", ex.Message);
    }

    [Theory]
    [InlineData("casfog_mora_umbral_inicio", "0.00")]    // the inclusive lower bound
    [InlineData("casfog_mora_umbral_inicio", "0.05")]    // the shipped default
    [InlineData("casfog_mora_umbral_plena",  "0.40")]    // the shipped default
    [InlineData("casfog_mora_umbral_plena",  "1.00")]    // the inclusive upper bound
    public async Task UpdateScoringConfig_WhenACasfogThresholdIsInRange_IsAccepted(string key, string value)
    {
        var sut = BuildService();

        var dto = await sut.UpdateScoringConfigAsync(
            Guid.NewGuid(), Tenant,
            Request((key, Dec(value))),
            ipAddress: null, requestId: null, ct: CancellationToken.None);

        Assert.NotNull(dto);
    }

    // This ramp runs UPWARD, so the ordering rule is the mirror of the ventas one: the full-penalty
    // threshold must sit at or ABOVE the start.
    [Fact]
    public async Task UpdateScoringConfig_WhenTheCasfogFullThresholdSitsBelowTheStart_IsRejected()
    {
        var sut = BuildService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.UpdateScoringConfigAsync(
                Guid.NewGuid(), Tenant,
                Request(("casfog_mora_umbral_inicio", Dec("0.40")),
                        ("casfog_mora_umbral_plena",  Dec("0.10"))),
                ipAddress: null, requestId: null, ct: CancellationToken.None));

        Assert.Contains("casfog_mora_umbral_plena", ex.Message);
        Assert.Contains("greater than or equal to", ex.Message);
    }

    // Equal thresholds are legal and mean a step at that point — the engine handles that arm
    // explicitly rather than dividing by a zero range.
    [Fact]
    public async Task UpdateScoringConfig_WhenTheCasfogThresholdsAreEqual_IsAccepted()
    {
        var sut = BuildService();

        var dto = await sut.UpdateScoringConfigAsync(
            Guid.NewGuid(), Tenant,
            Request(("casfog_mora_umbral_inicio", Dec("0.20")),
                    ("casfog_mora_umbral_plena",  Dec("0.20"))),
            ipAddress: null, requestId: null, ct: CancellationToken.None);

        Assert.NotNull(dto);
    }

    [Fact]
    public async Task UpdateScoringConfig_WhenTheCasfogPenaltyExceeds100_IsRejected()
    {
        var sut = BuildService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.UpdateScoringConfigAsync(
                Guid.NewGuid(), Tenant,
                Request(("casfog_mora_penalizacion_plena", 150.00m)),
                ipAddress: null, requestId: null, ct: CancellationToken.None));

        Assert.Contains("casfog_mora_penalizacion_plena", ex.Message);
        Assert.Contains("between 0 and 100", ex.Message);
    }

    [Theory]
    [InlineData("0.5")]   // "half on" is not a state this flag has
    [InlineData("7")]
    public async Task UpdateScoringConfig_WhenTheCasfogFlagIsNotZeroOrOne_IsRejected(string flag)
    {
        var sut = BuildService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.UpdateScoringConfigAsync(
                Guid.NewGuid(), Tenant,
                Request(("casfog_mora_enabled", Dec(flag))),
                ipAddress: null, requestId: null, ct: CancellationToken.None));

        Assert.Contains("casfog_mora_enabled", ex.Message);
        Assert.Contains("must be 0 (off) or 1 (on)", ex.Message);
    }

    [Theory]
    [InlineData("0")]   // the tenant switches the signal off
    [InlineData("1")]   // the shipped default
    public async Task UpdateScoringConfig_WhenTheCasfogFlagIsZeroOrOne_IsAccepted(string flag)
    {
        var sut = BuildService();

        var dto = await sut.UpdateScoringConfigAsync(
            Guid.NewGuid(), Tenant,
            Request(("casfog_mora_enabled", Dec(flag))),
            ipAddress: null, requestId: null, ct: CancellationToken.None);

        Assert.NotNull(dto);
    }

    // ── 4b. The profitability-floor flag (scoring algorithm 10) ────────────────────────────────
    //
    // critical_floor_profitability_requires_operating_loss is a 0/1 flag stored as a decimal, exactly like
    // the three penalty enable flags: the engine reads "> 0" as on, so 0.5 or 7 must be refused at the door.

    [Theory]
    [InlineData("0.5")]
    [InlineData("2")]
    [InlineData("7")]
    public async Task UpdateScoringConfig_WhenTheProfitabilityFloorFlagIsNotZeroOrOne_IsRejected(string flag)
    {
        var sut = BuildService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.UpdateScoringConfigAsync(
                Guid.NewGuid(), Tenant,
                Request(("critical_floor_profitability_requires_operating_loss", Dec(flag))),
                ipAddress: null, requestId: null, ct: CancellationToken.None));

        Assert.Contains("critical_floor_profitability_requires_operating_loss", ex.Message);
        Assert.Contains("must be 0 (off) or 1 (on)", ex.Message);
    }

    [Theory]
    [InlineData("0")]   // the tenant restores the version-9 floor
    [InlineData("1")]   // the shipped default
    public async Task UpdateScoringConfig_WhenTheProfitabilityFloorFlagIsZeroOrOne_IsAccepted(string flag)
    {
        var sut = BuildService();

        var dto = await sut.UpdateScoringConfigAsync(
            Guid.NewGuid(), Tenant,
            Request(("critical_floor_profitability_requires_operating_loss", Dec(flag))),
            ipAddress: null, requestId: null, ct: CancellationToken.None);

        Assert.NotNull(dto);
    }

    // ── 4c. The acid-test-floor flag (scoring algorithm 11) ────────────────────────────────────
    //
    // critical_floor_acid_test_requires_weak_current_ratio is the same kind of 0/1 flag as 4b. A negative
    // value is already refused by the blanket ">= 0" rule, with its own message, so it is not repeated here.

    [Theory]
    [InlineData("0.5")]
    [InlineData("2")]
    [InlineData("7")]
    public async Task UpdateScoringConfig_WhenTheAcidTestFloorFlagIsNotZeroOrOne_IsRejected(string flag)
    {
        var sut = BuildService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.UpdateScoringConfigAsync(
                Guid.NewGuid(), Tenant,
                Request(("critical_floor_acid_test_requires_weak_current_ratio", Dec(flag))),
                ipAddress: null, requestId: null, ct: CancellationToken.None));

        Assert.Contains("critical_floor_acid_test_requires_weak_current_ratio", ex.Message);
        Assert.Contains("must be 0 (off) or 1 (on)", ex.Message);
    }

    [Theory]
    [InlineData("0")]   // the tenant restores the version-10 acid-test floor
    [InlineData("1")]   // the shipped default
    public async Task UpdateScoringConfig_WhenTheAcidTestFloorFlagIsZeroOrOne_IsAccepted(string flag)
    {
        var sut = BuildService();

        var dto = await sut.UpdateScoringConfigAsync(
            Guid.NewGuid(), Tenant,
            Request(("critical_floor_acid_test_requires_weak_current_ratio", Dec(flag))),
            ipAddress: null, requestId: null, ct: CancellationToken.None);

        Assert.NotNull(dto);
    }

    // ── 5. Threshold ordering on BASE saves (scoring algorithm 9) ──────────────────────────────
    //
    // Until v9 only the sector PUT checked ok / warn / danger ordering, so a partial base PUT could
    // store a triplet the engine now has to refuse. The base PUT applies the same rule
    // (ScoringDefaults.IsThresholdTripletOrdered), to the base triplets and to every sector profile
    // layered over them.

    // A lone danger of 2.00 sits under the v9 default warn 2.33.
    [Fact]
    public async Task UpdateScoringConfig_WhenABaseTripletEndsUpOutOfOrder_IsRejected()
    {
        var sut = BuildService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.UpdateScoringConfigAsync(
                Guid.NewGuid(), Tenant,
                Request(("threshold_leverage_danger", 2.00m)),
                ipAddress: null, requestId: null, ct: CancellationToken.None));

        Assert.Contains("'leverage'", ex.Message);
        Assert.Contains("lower-is-better", ex.Message);
    }

    // A full, ordered triplet is accepted, including the whole v8 leverage calibration.
    [Fact]
    public async Task UpdateScoringConfig_WhenTheWholeTripletIsOrdered_IsAccepted()
    {
        var sut = BuildService();

        var dto = await sut.UpdateScoringConfigAsync(
            Guid.NewGuid(), Tenant,
            Request(
                ("threshold_leverage_ok",     1.00m),
                ("threshold_leverage_warn",   1.50m),
                ("threshold_leverage_danger", 2.50m)),
            ipAddress: null, requestId: null, ct: CancellationToken.None);

        Assert.NotNull(dto);
    }

    // A base change can invert a SECTOR profile layered over it: with Comercio's lone danger override
    // at 3.00, raising the base warn to 3.50 makes Comercio 1.00 / 3.50 / 3.00. The base triplet
    // (1.00 / 3.50 / 5.67) is fine on its own; the save is refused because of the sector, and says so.
    [Fact]
    public async Task UpdateScoringConfig_WhenABaseChangeInvertsASectorProfile_IsRejected_NamingTheSector()
    {
        var sut = BuildService(SeedRow("threshold_leverage_danger__sector__Comercio", 3.00m));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.UpdateScoringConfigAsync(
                Guid.NewGuid(), Tenant,
                Request(("threshold_leverage_warn", 3.50m)),
                ipAddress: null, requestId: null, ct: CancellationToken.None));

        Assert.Contains("'leverage'", ex.Message);
        Assert.Contains("sector profile 'Comercio'", ex.Message);
    }

    // The same sector row with a base change that keeps it ordered is accepted.
    [Fact]
    public async Task UpdateScoringConfig_WhenEverySectorProfileStaysOrdered_IsAccepted()
    {
        var sut = BuildService(SeedRow("threshold_leverage_danger__sector__Comercio", 3.00m));

        var dto = await sut.UpdateScoringConfigAsync(
            Guid.NewGuid(), Tenant,
            Request(("threshold_leverage_warn", 2.00m)),
            ipAddress: null, requestId: null, ct: CancellationToken.None);

        Assert.NotNull(dto);
    }

    // ── 6. Score thresholds sit on the 0-100 score scale ───────────────────────────────────────
    //
    // score_threshold_low/medium/high are the cutoffs Classify compares the final score against, and
    // the final score is clamped to [0,100]. A cutoff above 100 cannot be reached, and the Portal
    // refuses such a triplet and falls back to the defaults, so the BFF refuses it at the source.
    // Both bounds are inclusive. The ordering rule (low < medium < high) still applies.

    [Theory]
    [InlineData("score_threshold_high", "100.00")]   // the inclusive upper bound
    [InlineData("score_threshold_high", "80.00")]    // the shipped default
    [InlineData("score_threshold_low",  "0.00")]     // the inclusive lower bound
    public async Task UpdateScoringConfig_WhenAScoreThresholdIsWithinScale_IsAccepted(string key, string value)
    {
        var sut = BuildService();

        var dto = await sut.UpdateScoringConfigAsync(
            Guid.NewGuid(), Tenant,
            Request((key, Dec(value))),
            ipAddress: null, requestId: null, ct: CancellationToken.None);

        Assert.NotNull(dto);
        Assert.Equal(Dec(value), dto!.ScoreThresholds.Single(i => i.Key == key).Value);
    }

    [Fact]
    public async Task UpdateScoringConfig_WhenTheHighScoreThresholdExceeds100_IsRejected()
    {
        var sut = BuildService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.UpdateScoringConfigAsync(
                Guid.NewGuid(), Tenant,
                Request(("score_threshold_high", Dec("100.01"))),
                ipAddress: null, requestId: null, ct: CancellationToken.None));

        Assert.Equal(
            "Configuration value for 'score_threshold_high' must be between 0 and 100 (current: 100.01).",
            ex.Message);
    }

    // An ordered triplet entirely above the scale is refused by the range rule, not passed by the
    // ordering one — and the message names the first key out of range.
    [Fact]
    public async Task UpdateScoringConfig_WhenAnOrderedTripletIsAboveTheScale_IsRejectedByTheRange()
    {
        var sut = BuildService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.UpdateScoringConfigAsync(
                Guid.NewGuid(), Tenant,
                Request(
                    ("score_threshold_low",    Dec("100.50")),
                    ("score_threshold_medium", Dec("101.00")),
                    ("score_threshold_high",   Dec("102.00"))),
                ipAddress: null, requestId: null, ct: CancellationToken.None));

        Assert.Contains("'score_threshold_low'", ex.Message);
        Assert.Contains("between 0 and 100", ex.Message);
    }

    // The ordering rule is unchanged: a medium of 30 under the default low of 35 is refused with the
    // same message as before.
    [Fact]
    public async Task UpdateScoringConfig_WhenScoreThresholdsAreOutOfOrder_IsStillRejected()
    {
        var sut = BuildService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.UpdateScoringConfigAsync(
                Guid.NewGuid(), Tenant,
                Request(("score_threshold_medium", Dec("30.00"))),
                ipAddress: null, requestId: null, ct: CancellationToken.None));

        Assert.Equal(
            "Score thresholds must be ordered: low (35) < medium (30.00) < high (80).",
            ex.Message);
    }

    // A refused PUT writes nothing: the stored config still reads the default.
    [Fact]
    public async Task UpdateScoringConfig_WhenAScoreThresholdIsRefused_NothingIsPersisted()
    {
        var sut = BuildService();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.UpdateScoringConfigAsync(
                Guid.NewGuid(), Tenant,
                Request(("score_threshold_high", 150.00m)),
                ipAddress: null, requestId: null, ct: CancellationToken.None));

        var stored = await sut.GetScoringConfigAsync(Tenant, CancellationToken.None);
        Assert.Equal(80m, stored.ScoreThresholds.Single(i => i.Key == "score_threshold_high").Value);
    }

    // ── Harness ───────────────────────────────────────────────────────────────
    //
    // Hand-rolled, as everywhere else in this project — no mocking library. The EF in-memory
    // provider backs the ScoringConfigs table — empty unless a test seeds rows — so MergeWithDefaults
    // yields the ScoringDefaults map plus whatever the seed and the payload override.

    // ── 4d. GET surfaces both floor-rule flags as the floorRules group (K22) ───────────────────

    private const string ProfitabilityFlag = "critical_floor_profitability_requires_operating_loss";
    private const string AcidTestFlag      = "critical_floor_acid_test_requires_weak_current_ratio";

    [Fact]
    public async Task GetScoringConfig_SurfacesTheFloorRuleFlags_WithTheirDefaults_InOrder()
    {
        var sut = BuildService();

        var dto = await sut.GetScoringConfigAsync(Tenant, CancellationToken.None);

        Assert.Equal(new[] { ProfitabilityFlag, AcidTestFlag }, dto.FloorRules.Select(i => i.Key));
        Assert.All(dto.FloorRules, i => Assert.Equal(1.0000m, i.Value));
        Assert.Equal(ScoringDefaults.Descriptions[ProfitabilityFlag], dto.FloorRules[0].Description);
        Assert.Equal(ScoringDefaults.Descriptions[AcidTestFlag], dto.FloorRules[1].Description);

        // The other groups are what they were.
        Assert.Equal(ScoringDefaults.RatioWeightKeys,     dto.RatioWeights.Select(i => i.Key));
        Assert.Equal(ScoringDefaults.RatioThresholdKeys,  dto.RatioThresholds.Select(i => i.Key));
        Assert.Equal(ScoringDefaults.ComponentWeightKeys, dto.ComponentWeights.Select(i => i.Key));
        Assert.Equal(ScoringDefaults.ScoreThresholdKeys,  dto.ScoreThresholds.Select(i => i.Key));
        Assert.Equal(ScoringDefaults.MacroKeys,           dto.Macro.Select(i => i.Key));
    }

    [Fact]
    public async Task GetScoringConfig_ReflectsAStoredFloorRuleFlag_AndThePutAnswersWithIt()
    {
        var sut = BuildService(SeedRow(AcidTestFlag, 0m));

        var dto = await sut.GetScoringConfigAsync(Tenant, CancellationToken.None);
        Assert.Equal(0m, Assert.Single(dto.FloorRules, i => i.Key == AcidTestFlag).Value);
        Assert.Equal(1m, Assert.Single(dto.FloorRules, i => i.Key == ProfitabilityFlag).Value);

        var updated = await sut.UpdateScoringConfigAsync(
            Guid.NewGuid(), Tenant, Request((ProfitabilityFlag, 0m)),
            ipAddress: null, requestId: null, ct: CancellationToken.None);

        Assert.All(updated!.FloorRules, i => Assert.Equal(0m, i.Value));
    }

    [Fact]
    public async Task FloorRules_OnTheWire_HaveTheSameItemShapeAsTheOtherGroups()
    {
        var dto  = await BuildService().GetScoringConfigAsync(Tenant, CancellationToken.None);
        var wire = System.Text.Json.JsonSerializer.SerializeToElement(dto,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        var floorItem = wire.GetProperty("floorRules")[0];
        var macroItem = wire.GetProperty("macro")[0];
        Assert.Equal(
            macroItem.EnumerateObject().Select(p => p.Name),
            floorItem.EnumerateObject().Select(p => p.Name));
        Assert.Equal(new[] { "key", "value", "description" }, floorItem.EnumerateObject().Select(p => p.Name));
    }

    private static SettingsService BuildService(params ScoringConfig[] seed)
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"settings-{Guid.NewGuid()}")
            .Options;

        var db = new FinanciaDbContext(options);

        if (seed.Length > 0)
        {
            db.ScoringConfigs.AddRange(seed);
            db.ChangeTracker.DetectChanges();   // AutoDetectChanges is disabled on the context
            db.SaveChanges();
        }

        return new SettingsService(
            db,
            new StubAuditService(),
            new ThrowingTenantProfileService(),
            NullLogger<SettingsService>.Instance);
    }

    private static ScoringConfig SeedRow(string key, decimal value) => new()
    {
        ConfigId    = Guid.NewGuid(),
        TenantId    = Tenant,
        ConfigKey   = key,
        ConfigValue = value,
        Description = "seeded by SettingsServiceScoringValidationTests",
        UpdatedAt   = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    // These tests exercise the scoring-config path only, which never touches the tenant profile.
    // Throwing rather than returning a default keeps an accidental dependency from passing quietly.
    private sealed class ThrowingTenantProfileService : ITenantProfileService
    {
        public Task<TenantProfileResolution> GetEffectiveAsync(string tenantId, CancellationToken ct)
            => throw new NotSupportedException("Scoring config must not read the tenant profile.");

        public Task<TenantProfileResolution> SetEntityTypeAsync(string tenantId, string entityType, CancellationToken ct)
            => throw new NotSupportedException("Scoring config must not write the tenant profile.");
    }

    private static UpdateScoringConfigRequest Request(params (string Key, decimal Value)[] items)
        => new()
        {
            Items = items
                .Select(i => new ScoringConfigUpdateItem { Key = i.Key, Value = i.Value })
                .ToList()
        };

    /// <summary>[InlineData] cannot carry a decimal, so exact values travel as strings.</summary>
    private static decimal Dec(string value)
        => decimal.Parse(value, CultureInfo.InvariantCulture);

    private sealed class StubAuditService : IAuditService
    {
        public Task LogAsync(
            string tenantId, Guid? userId, string action, string severity = "Info",
            string? entityType = null, string? entityId = null, string? ipAddress = null,
            string? requestId = null, string? oldValueJson = null, string? newValueJson = null,
            CancellationToken ct = default)
            => Task.CompletedTask;

        public Task LogSessionAsync(
            Guid tenantId, Guid? userId, string email, string eventType,
            string? ipAddress, string? userAgent, string? failureReason = null,
            CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
