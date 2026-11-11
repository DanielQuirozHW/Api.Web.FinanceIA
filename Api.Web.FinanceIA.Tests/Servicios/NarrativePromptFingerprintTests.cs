// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-14

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Fingerprint guard for <see cref="ClaudeService.BuildNarrativePrompt"/>, mirroring
/// <c>ExtractionPromptTests.SystemPrompt_Change_RequiresVersionBump</c>. Unlike SystemPrompt,
/// BuildNarrativePrompt is a function of its inputs, not a constant — so this hashes its OUTPUT
/// over a fixed set of canonical fixtures (below), concatenated in a stable order, rather than
/// hashing the method itself.
/// </summary>
public sealed class NarrativePromptFingerprintTests
{
    // ── Fixtures ─────────────────────────────────────────────────────────────
    // Seven canonical inputs, each isolating one branch of BuildNarrativePrompt so a hash
    // failure can be attributed to a specific fixture. Every branch enumerated for this task
    // (BcraExcluded, an excluded ratio, the applied-floor block, bcraData present, macroData
    // present, all-ratios-null, ratios-present) is exercised by at least one fixture below —
    // a branch hit by zero fixtures could be edited without ever changing this hash.

    private static ScoringResult MakeScoring(
        Dictionary<string, RatioBreakdownEntry> breakdown,
        bool bcraExcluded = false,
        List<string>? criticalRatios = null,
        decimal appliedFloor = 0m,
        decimal scoreBeforeFloor = 40m,
        decimal score = 40m) => new()
    {
        Score                 = score,
        ScoreBeforeFloor      = scoreBeforeFloor,
        AppliedFloor          = appliedFloor,
        // The engine default since scoring algorithm 9 (critical only at DANGER). Set explicitly so the
        // floor fixture renders the trigger sentence a real result would carry, not the 0 of an unset field.
        CriticalFloorTrigger  = 100m,
        Level                 = "Critical",
        ScoreRatios           = 24m,
        ScoreBcra             = 50m,
        ScoreMacro            = 50m,
        CriticalRatios        = criticalRatios ?? new List<string>(),
        Breakdown             = breakdown,
        BcraExcluded          = bcraExcluded,
        EffectiveWeightRatios = bcraExcluded ? 0.60m : 0.50m,
        EffectiveWeightBcra   = bcraExcluded ? 0m    : 0.35m,
        EffectiveWeightMacro  = bcraExcluded ? 0.40m : 0.15m,
    };

    // AllHealthy: every one of the 8 ratios populated and present in Breakdown, none excluded,
    // no floor, BCRA not excluded — the "everything computable" baseline.
    private static string AllHealthy() => ClaudeService.BuildNarrativePrompt(
        MakeScoring(new Dictionary<string, RatioBreakdownEntry>
        {
            ["liquidez_corriente"]  = new RatioBreakdownEntry(80m, "Saludable"),
            ["prueba_acida"]        = new RatioBreakdownEntry(75m, "Saludable"),
            ["endeudamiento"]       = new RatioBreakdownEntry(60m, "Atencion"),
            ["leverage"]            = new RatioBreakdownEntry(55m, "Atencion"),
            ["cobertura_intereses"] = new RatioBreakdownEntry(90m, "Saludable"),
            ["roe"]                 = new RatioBreakdownEntry(70m, "Saludable"),
            ["roa"]                 = new RatioBreakdownEntry(65m, "Saludable"),
            ["margen_neto"]         = new RatioBreakdownEntry(72m, "Saludable"),
        }),
        new RatiosData
        {
            CurrentRatio     = 1.50,
            AcidTest         = 1.10,
            DebtRatio        = 0.45,
            Leverage         = 1.80,
            InterestCoverage = 3.20,
            Roe              = 18.5,
            Roa              = 9.4,
            NetMargin        = 11.2,
        },
        null, null);

    // BcraExcluded: covers the two BcraExcluded branches (REGLAS bullet + score-header weights).
    private static string BcraExcludedFixture() => ClaudeService.BuildNarrativePrompt(
        MakeScoring(
            new Dictionary<string, RatioBreakdownEntry> { ["leverage"] = new RatioBreakdownEntry(85m, "Critico") },
            bcraExcluded: true),
        new RatiosData { Leverage = 2.67 },
        null, null);

    // ExcludedRatio: covers the excluded-ratio REGLAS bullet and the Desglose "no medido" line.
    private static string ExcludedRatio() => ClaudeService.BuildNarrativePrompt(
        MakeScoring(new Dictionary<string, RatioBreakdownEntry>
        {
            ["cobertura_intereses"] = new RatioBreakdownEntry(50m, "Atencion", Excluded: true, EffectiveWeight: 0m),
            ["leverage"]            = new RatioBreakdownEntry(72m, "Atencion"),
        }),
        new RatiosData { Leverage = 1.95 },
        null, null);

    // FloorApplied: covers the critical-ratio floor block.
    private static string FloorApplied() => ClaudeService.BuildNarrativePrompt(
        MakeScoring(
            new Dictionary<string, RatioBreakdownEntry> { ["leverage"] = new RatioBreakdownEntry(85m, "Critico") },
            criticalRatios: ["leverage"], appliedFloor: 65m, scoreBeforeFloor: 38m, score: 65m),
        new RatiosData { Leverage = 2.67 },
        null, null);

    // BcraDataPresent: covers the "Situación BCRA" section.
    private static string BcraDataPresent() => ClaudeService.BuildNarrativePrompt(
        MakeScoring(new Dictionary<string, RatioBreakdownEntry> { ["roe"] = new RatioBreakdownEntry(70m, "Saludable") }),
        new RatiosData { Roe = 18.5 },
        new BcraData { Deuda = new BcraDeudaData { SituacionMaxima = 2, MontoTotal = 1500m } },
        null);

    // MacroDataPresent: covers the "Contexto macroeconómico" section.
    private static string MacroDataPresent() => ClaudeService.BuildNarrativePrompt(
        MakeScoring(new Dictionary<string, RatioBreakdownEntry> { ["roa"] = new RatioBreakdownEntry(65m, "Saludable") }),
        new RatiosData { Roa = 9.4 },
        null,
        new BcraMacroData { InflacionMensual = 4.2, TasaReferencia = 38.5 });

    // RatiosAllNull: covers the all-"n/d" path through the "Valores de los ratios:" block.
    private static string RatiosAllNull() => ClaudeService.BuildNarrativePrompt(
        MakeScoring(new Dictionary<string, RatioBreakdownEntry> { ["leverage"] = new RatioBreakdownEntry(50m, "Atencion") }),
        new RatiosData(),
        null, null);

    private const string FixtureSeparator = "\n\n=== FIXTURE BOUNDARY ===\n\n";

    private static string AllFixturesCombined() => string.Join(FixtureSeparator, new[]
    {
        AllHealthy(),
        BcraExcludedFixture(),
        ExcludedRatio(),
        FloorApplied(),
        BcraDataPresent(),
        MacroDataPresent(),
        RatiosAllNull(),
    });

    // ── Key-set drift guard ──────────────────────────────────────────────────

    [Fact]
    public void RatioFormula_And_RatioDisplayLabel_HaveSameKeySet()
    {
        var labelKeys   = ClaudeService.RatioDisplayLabel.Keys.OrderBy(k => k, StringComparer.Ordinal);
        var formulaKeys = ClaudeService.RatioFormula.Keys.OrderBy(k => k, StringComparer.Ordinal);

        Assert.Equal(labelKeys, formulaKeys);
    }

    // ── Content check (readable signal alongside the opaque hash) ────────────

    [Fact]
    public void BuildNarrativePrompt_ValoresBlock_IncludesFormula_EvenWhenValueIsNd()
    {
        var prompt = RatiosAllNull();

        // A formula alone implies no value: the parenthetical must render even for "n/d".
        Assert.Contains("Endeudamiento (pasivo total / activo total) : n/d", prompt);
        Assert.Contains("Margen neto ((resultado neto / ingresos netos) × 100) : n/d %", prompt);
    }

    // ── Fingerprint registry ─────────────────────────────────────────────────
    // Guard: any edit to BuildNarrativePrompt's OUTPUT (for any of the fixtures above) must be
    // paired with an ExtractionPromptVersion bump — same shared constant as the SystemPrompt
    // guard in ExtractionPromptTests, since a narrative-only change bumps it too. When this
    // fails legitimately:
    //   1) bump ExtractionPromptVersion in ClaudeService.cs, then
    //   2) add a registry entry below for the new version using the hash in the failure message.
    [Fact]
    public void BuildNarrativePrompt_Fingerprint_MatchesRegisteredHash()
    {
        var fingerprints = new Dictionary<string, string>
        {
            // 2.13 baseline, registered retroactively: this fingerprint guard did not exist at
            // 2.13, so this hash was computed once from the pre-Part-1 BuildNarrativePrompt
            // (before ratio formulas were inlined into "Valores de los ratios:") applied to the
            // same seven fixtures above, and pinned here so the registry has a starting point.
            ["2.13"] = "d9587de8c7e35f067fa9a6edb372498ebdf4c21a31da2615311ed2be9a0b5828",
            // 2.14: Part 1 inlines each ratio's formula into "Valores de los ratios:" and adds
            // the denominator-lock REGLAS ESTRICTAS bullet.
            ["2.14"] = "dde7020f8e4f34a97b9846fe5140f31186f538737b219acce47e9ee61c3ebac5",
            // 2.15 changed only the extraction SystemPrompt (percentage-must-state-its-own-amount
            // rule); BuildNarrativePrompt's output is byte-identical to 2.14, so this fingerprint
            // is unchanged.
            ["2.15"] = "dde7020f8e4f34a97b9846fe5140f31186f538737b219acce47e9ee61c3ebac5",
            // 2.16 changed only the extraction SystemPrompt (line-item naming rule for cited balance-sheet /
            // income-statement figures); BuildNarrativePrompt's output is byte-identical to 2.15, so this
            // fingerprint is unchanged.
            ["2.16"] = "dde7020f8e4f34a97b9846fe5140f31186f538737b219acce47e9ee61c3ebac5",
            // 2.17 (scoring algorithm 9): the "AJUSTE POR RATIO CRÍTICO" block now states when a ratio
            // counts as critical (at its danger threshold, or at a tenant-configured trigger) and the
            // /1000 ceiling the floor imposes. The FloorApplied fixture also sets CriticalFloorTrigger to
            // the engine default 100. New fingerprint.
            ["2.17"] = "063fb2e799a54baf724da2f5eccc68a40e8c219aff8ff20723ea36234a953989",
            // 2.18 changed only the EXTRACTION SystemPrompt (M11 — balance-extraction integrity); the
            // narrative prompt is byte-identical to 2.17, so this fingerprint is unchanged.
            ["2.18"] = "063fb2e799a54baf724da2f5eccc68a40e8c219aff8ff20723ea36234a953989",
            // 2.19 changed only the EXTRACTION SystemPrompt (trial balances); the narrative prompt is
            // byte-identical to 2.18, so this fingerprint is unchanged.
            ["2.19"] = "063fb2e799a54baf724da2f5eccc68a40e8c219aff8ff20723ea36234a953989",
            // 2.20 changed only the EXTRACTION SystemPrompt (the JSON-only answer line); the narrative prompt is
            // byte-identical to 2.19, so this fingerprint is unchanged.
            ["2.20"] = "063fb2e799a54baf724da2f5eccc68a40e8c219aff8ff20723ea36234a953989",
            // 2.21 changed only the EXTRACTION SystemPrompt (which alerts to raise, and their codes); the narrative
            // prompt is byte-identical to 2.20, so this fingerprint is unchanged.
            ["2.21"] = "063fb2e799a54baf724da2f5eccc68a40e8c219aff8ff20723ea36234a953989",
        };

        var version = ClaudeService.ExtractionPromptVersion;
        var actual  = Sha256Hex(AllFixturesCombined());

        Assert.True(
            fingerprints.TryGetValue(version, out var expected),
            $"No narrative-prompt fingerprint registered for ExtractionPromptVersion \"{version}\". " +
            $"Add [\"{version}\"] = \"{actual}\" to this test's registry.");

        Assert.True(
            expected == actual,
            $"BuildNarrativePrompt output changed but ExtractionPromptVersion is still \"{version}\". " +
            $"Bump ExtractionPromptVersion and register [\"<new version>\"] = \"{actual}\".");
    }

    private static string Sha256Hex(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
