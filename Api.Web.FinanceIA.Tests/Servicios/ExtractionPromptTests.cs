// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-10

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.Pdf;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

public sealed class ExtractionPromptTests
{
    [Fact]
    public void SystemPrompt_ForUnmatchedCashFlowLine_InstructsOmission()
    {
        var prompt = ClaudeService.SystemPrompt;

        Assert.Contains("no forced attribution", prompt);
        Assert.Contains("NEVER map it to the nearest-looking key", prompt);
        Assert.Contains("surface as the section residual", prompt);
    }

    [Fact]
    public void SystemPrompt_FinancialInvestments_AreNotFixedAssetMovements()
    {
        var prompt = ClaudeService.SystemPrompt;

        Assert.Contains("\"Inversiones\"", prompt);
        Assert.Contains("neither capex nor asset_disposals", prompt);
    }

    [Fact]
    public void SystemPrompt_FinancialPlacements_DirectionIsNeverInferredFromSign()
    {
        var prompt = ClaudeService.SystemPrompt;

        Assert.Contains("NEVER infer direction from the sign", prompt);
        Assert.Contains("NEVER to asset_disposals", prompt);
    }

    [Fact]
    public void SystemPrompt_ForSectionMovementsWithoutSubtotal_InstructsAnomaly()
    {
        var prompt = ClaudeService.SystemPrompt;

        Assert.Contains("NEVER invent, infer or sum a section subtotal the statement does not report", prompt);
        Assert.Contains("identified movements while the statement does not report its subtotal", prompt);
        Assert.Contains("it must not state or imply a total", prompt);
    }

    [Fact]
    public void SystemPrompt_CitedFigure_KeepsSourceLineOwnName()
    {
        var prompt = ClaudeService.SystemPrompt;

        Assert.Contains("keeps its source line's own name", prompt);
        Assert.Contains("NEVER apply a component's name to the aggregate's total", prompt);
        Assert.Contains("counts that concept twice", prompt);
    }

    [Fact]
    public void SystemPrompt_BalanceFigures_NegativesAreSignedNumbersNeverStrings()
    {
        var prompt = ClaudeService.SystemPrompt;

        Assert.Contains("printed in parentheses, e.g. (1.234.567)", prompt);
        Assert.Contains("with a trailing minus (1.234.567-) is NEGATIVE", prompt);
        Assert.Contains("never as a string", prompt);
        Assert.Contains("equity is negative", prompt);
    }

    [Fact]
    public void SystemPrompt_BalanceFigures_ComeOnlyFromTheAnalysedColumn()
    {
        var prompt = ClaudeService.SystemPrompt;

        Assert.Contains("never from the comparative column", prompt);
        Assert.Contains("If you cannot tell which column", prompt);
        Assert.Contains("\"prior_year\"", prompt);
    }

    [Fact]
    public void SystemPrompt_BalanceFigures_AmountsAsPrintedWithDeclaredUnit()
    {
        var prompt = ClaudeService.SystemPrompt;

        Assert.Contains("\"amounts_unit\": \"pesos\" | \"miles\" | \"millones\" | null", prompt);
        Assert.Contains("NEVER multiply them", prompt);
    }

    [Fact]
    public void SystemPrompt_BalanceFigures_SubtotalsAreNeverDerivedAndRubrosAreListed()
    {
        var prompt = ClaudeService.SystemPrompt;

        Assert.Contains("NEVER derive one as the difference or the sum of other totals", prompt);
        Assert.Contains("\"current_liabilities_items\"", prompt);
        Assert.Contains("Include lines whose amount is zero", prompt);
        Assert.Contains("\"result_before_tax\"", prompt);
        Assert.Contains("\"income_tax\"", prompt);
        Assert.Contains("\"liabilities\": number|null", prompt);
    }

    [Fact]
    public void SystemPrompt_TrialBalance_IsDetected_AndItsFiguresStayAsPrinted()
    {
        var prompt = ClaudeService.SystemPrompt;

        Assert.Contains(ClaudeService.TrialBalanceSchemaLines, prompt);
        Assert.Contains(ClaudeService.TrialBalanceRules, prompt);
        Assert.Contains("\"document_kind\": \"financial_statements\" | \"trial_balance\"", prompt);
        Assert.Contains("\"Balance de Presentación\"", prompt);
        Assert.Contains("balance_check follows THIS schema's sign convention, never the ledger's", prompt);
        Assert.Contains("trial_balance repeats figures EXACTLY AS PRINTED", prompt);
        Assert.Contains("Never list a group total together with the accounts it adds up", prompt);
        Assert.Contains("never add the result to it yourself", prompt);

        // The schema lines sit inside the schema, the rules before the honesty rules.
        Assert.True(prompt.IndexOf(ClaudeService.TrialBalanceSchemaLines, StringComparison.Ordinal)
                    < prompt.IndexOf("  \"balance_check\": {", StringComparison.Ordinal));
        Assert.True(prompt.IndexOf(ClaudeService.TrialBalanceRules, StringComparison.Ordinal)
                    < prompt.IndexOf("EXTRACTION HONESTY RULES:", StringComparison.Ordinal));
    }

    [Fact]
    public void SystemPrompt_TheAnswerIsTheJsonObjectAlone_RightAfterTheExclusivelyRule()
    {
        var prompt = ClaudeService.SystemPrompt;
        const string exclusivelyParagraphEnd = "markdown formatting, or code fences.\n\n";

        // Exactly once, as its own paragraph, immediately after the EXCLUSIVELY paragraph and before the schema.
        var at = prompt.IndexOf(ClaudeService.JsonOnlyAnswerRule, StringComparison.Ordinal);
        Assert.True(at > 0);
        Assert.Equal(at, prompt.LastIndexOf(ClaudeService.JsonOnlyAnswerRule, StringComparison.Ordinal));
        Assert.EndsWith(exclusivelyParagraphEnd, prompt[..at], StringComparison.Ordinal);
        Assert.StartsWith("Required schema:\n", prompt[(at + ClaudeService.JsonOnlyAnswerRule.Length)..], StringComparison.Ordinal);
        Assert.Contains("respond EXCLUSIVELY with a valid JSON object", prompt[..at], StringComparison.Ordinal);

        Assert.StartsWith("Your answer must start with { and contain nothing before or after the JSON object", ClaudeService.JsonOnlyAnswerRule, StringComparison.Ordinal);
        Assert.Contains("no explanation, no reasoning, no Markdown.", ClaudeService.JsonOnlyAnswerRule, StringComparison.Ordinal);
        Assert.EndsWith("\n\n", ClaudeService.JsonOnlyAnswerRule, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemPrompt_AlertScope_SitsBetweenTitleAndRatioUnits_AndNamesTheEngineCodes()
    {
        var prompt = ClaudeService.SystemPrompt;
        var block  = ClaudeService.AlertScopeRules;

        // Exactly once, as its own section, right after the alert-title block and right before the ratio units.
        var at = prompt.IndexOf(block, StringComparison.Ordinal);
        Assert.True(at > 0);
        Assert.Equal(at, prompt.LastIndexOf(block, StringComparison.Ordinal));
        Assert.EndsWith("Write the title in Spanish even though these instructions are in English.\n\n", prompt[..at], StringComparison.Ordinal);
        Assert.StartsWith("CRITICAL — ratio units:\n", prompt[(at + block.Length)..], StringComparison.Ordinal);
        Assert.EndsWith("\n\n", block, StringComparison.Ordinal);

        // A ratio level is not an alert: every one of the eight ratio keys is named.
        foreach (var ratio in new[] { "current_ratio", "acid_test", "debt_ratio", "leverage", "roe", "roa", "net_margin", "interest_coverage" })
            Assert.Contains(ratio, block, StringComparison.Ordinal);
        Assert.Contains("is high, low, weak or strong", block, StringComparison.Ordinal);
        Assert.Contains("An empty \"alerts\" array is a valid answer", block, StringComparison.Ordinal);
        Assert.Contains("auditor qualifications", block, StringComparison.Ordinal);

        // The facts the engine also states carry the engine's own code, taken from its constants…
        foreach (var code in new[]
                 {
                     AlertRuleEngine.PasivoCienCorriente, AlertRuleEngine.InventarioElevado, ClaudeService.IngresosEnCaidaAlertCode,
                     AlertRuleEngine.CoberturaInteresesIndeterminable, AlertRuleEngine.FlujoNoConcilia, AlertRuleEngine.FlujoFinanciacionCero,
                 })
        {
            Assert.Contains($"→ {code}\n", block, StringComparison.Ordinal);
        }

        // …but never the debt-ratio level, which is exactly what the block forbids.
        Assert.DoesNotContain(AlertRuleEngine.EndeudamientoElevado, prompt, StringComparison.Ordinal);

        // The title example no longer invites a ratio-level alert.
        Assert.DoesNotContain("Deuda bancaria elevada", prompt, StringComparison.Ordinal);
        Assert.Contains($"e.g. \"Caída de ingresos\", {ClaudeService.DocumentOnlyAlertTitleExample}, ", prompt, StringComparison.Ordinal);

        // The financial_expenses rule names the code of the one alert it allows.
        Assert.Contains(
            "you may raise an informational alert with code " + AlertRuleEngine.CoberturaInteresesIndeterminable +
            " noting that interest coverage is not determinable",
            prompt,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SystemPrompt_RevenueDropCode_OverlapsNoPdfFact()
    {
        // The balance-sheet revenue drop is not the LUFE "Ventas declaradas" fact: its code must overlap nothing, or
        // the PDF would hide the model's alert whenever the LUFE bullet prints.
        var revenueDrop = ClaudeService.IngresosEnCaidaAlertCode;
        Assert.Null(AttentionOverlaps.FactOf(revenueDrop));
        Assert.False(AttentionOverlaps.IsListed(revenueDrop));
        foreach (var list in AttentionOverlaps.Codes.Values.Concat(AttentionOverlaps.ModelEquivalents.Values))
            Assert.DoesNotContain(list, listed => string.Equals(listed, revenueDrop, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(AlertRuleEngine.ImplementedCodes, engine => string.Equals(engine, revenueDrop, StringComparison.OrdinalIgnoreCase));

        // …and the prompt never names a code of the LUFE sales fact, so it cannot steer the model back onto it.
        foreach (var ventas in AttentionOverlaps.Codes[AttentionFact.Ventas])
            Assert.DoesNotContain(ventas, ClaudeService.SystemPrompt, StringComparison.OrdinalIgnoreCase);

        // Every engine code the prompt names is a catalogue code the PDF already matches a model alert against.
        foreach (var code in new[]
                 {
                     AlertRuleEngine.PasivoCienCorriente, AlertRuleEngine.InventarioElevado, AlertRuleEngine.CoberturaInteresesIndeterminable,
                     AlertRuleEngine.FlujoNoConcilia, AlertRuleEngine.FlujoFinanciacionCero,
                 })
        {
            Assert.Contains(code, AttentionOverlaps.ModelEquivalents[code]);
            Assert.True(AttentionOverlaps.SameFinding(code, code));
        }
    }

    // Guard: any edit to SystemPrompt must be paired with an ExtractionPromptVersion bump.
    // Two branches collided on this constant in one day; a per-version fingerprint makes a
    // forgotten bump fail the build. When this fails legitimately:
    //   1) bump ExtractionPromptVersion in ClaudeService.cs, then
    //   2) add a registry entry below for the new version using the hash in the failure message.
    // A narrative-only prompt change also bumps this shared constant → register the (unchanged)
    // SystemPrompt hash under the new version.
    [Fact]
    public void SystemPrompt_Change_RequiresVersionBump()
    {
        var fingerprints = new Dictionary<string, string>
        {
            ["2.9"]  = "a76adc47e66e2a6d27a15df52114e80cba937da3050f080c1d7f34cac62d90d5",
            ["2.10"] = "a6879d1a7a47b5ddd95f4b965373e73525124beec60d51578c4cc456560029f6",
            // 2.11 changed only the narrative prompt (BuildNarrativePrompt); the extraction
            // SystemPrompt is byte-identical to 2.10, so its fingerprint is unchanged.
            ["2.11"] = "a6879d1a7a47b5ddd95f4b965373e73525124beec60d51578c4cc456560029f6",
            // 2.12 edits the extraction SystemPrompt (anomaly hygiene): anomalies are findings
            // about the document (not our own corrections), a percentage must carry its
            // denominator, and self-declared method/basis is a claim to verify. New fingerprint.
            ["2.12"] = "9361df525f78dbd8deae05a97fca73994cef0c333c385ba60c7713b8d61402fa",
            // 2.13 changed only the narrative prompt (BuildNarrativePrompt): an excluded ratio's
            // breakdown line no longer leaks its neutral-50 "Atencion"/"Critico" status, and two
            // REGLAS/IMPORTANTE instructions now forbid describing an excluded ratio as graded;
            // the extraction SystemPrompt is byte-identical to 2.12, so its fingerprint is unchanged.
            ["2.13"] = "9361df525f78dbd8deae05a97fca73994cef0c333c385ba60c7713b8d61402fa",
            // 2.14 edits the extraction SystemPrompt: a new "executive_summary must not restate
            // ratio values" rule bars the extraction pass from stating the 8 computed ratios'
            // numeric values in prose, since RatioCalculationService recomputes them
            // deterministically afterwards and the two can silently disagree. New fingerprint.
            ["2.14"] = "5580b23f44048c7c9e7ecbb40da09494250b7035083034028cb1182a8cf1e0c8",
            // 2.15 edits the extraction SystemPrompt: the "percentages of balance-sheet
            // magnitudes" rule now also requires the sentence stating a percentage to state the
            // numerator's own currency figure, not just name the denominator — a bare percentage
            // cannot be independently verified against the source document. New fingerprint.
            // Re-registered pre-publication: the worked example's amounts were originally a real
            // client's balance figures (an anchoring risk — every tenant's extraction shares this
            // prompt) and were swapped for synthetic round numbers before 2.15 shipped, so the
            // fingerprint is updated in place rather than bumped to a new version.
            ["2.15"] = "87fea5a20dbb9826d85e02c0feadbd8b538b63d620bca16ae8ae7b84e37976fb",
            // 2.16 edits the extraction SystemPrompt: a new "a cited figure keeps its source line's own
            // name" rule — a figure taken from an aggregating balance-sheet/income-statement line must be
            // named as the statement names it (or its component cited separately, under the component's own
            // figure), never the aggregate's total relabelled with a narrower component name; and a component
            // already inside an aggregate must never be added back to it. New fingerprint.
            // Re-registered pre-publication: the worked example's dividends-payable component ($200.000.000)
            // and its aggregating-note scenario coincided with a real client's figures — swapped for a
            // different synthetic split ($850.000.000 = $700.000.000 + $150.000.000) before 2.16 ships, so
            // the fingerprint is updated in place rather than bumped to a new version.
            ["2.16"] = "6df0c559df765890ccdb4952da5efb29f3fc26aa513834f3be612e4d2b34742d",
            // 2.17 changed only the NARRATIVE prompt (scoring algorithm 9: the critical-floor block now
            // states the trigger rule and the ceiling the floor imposes); the extraction SystemPrompt is
            // byte-identical to 2.16, so this fingerprint is unchanged.
            ["2.17"] = "6df0c559df765890ccdb4952da5efb29f3fc26aa513834f3be612e4d2b34742d",
            // 2.18 edits the extraction SystemPrompt (M11 — balance-extraction integrity): a new
            // "balance-sheet and income-statement figures" section (negatives in parentheses or with a
            // trailing minus, figures as signed JSON numbers, the analysed column only, amounts as
            // printed + amounts_unit, no derived subtotals, rubro lines, prior_year) and the schema
            // gains amounts_unit, prior_year, result_before_tax, income_tax and the *_items arrays;
            // assets/liabilities/equity become number|null. Synthetic examples only. New fingerprint.
            ["2.18"] = "fc24816db8c4e39a58aa2444f06fe79121fe432c49d34c2376edb4b863523188",
            // 2.19 edits the extraction SystemPrompt (trial balances): the schema gains document_kind and a
            // trial_balance block (ledger figures as printed: credit-sign flag, printed equity, classified results
            // accounts), and a "CRITICAL — trial balances" section says how to detect one, that balance_check keeps
            // THIS schema's signs, and that the model never adds the result to equity nor computes the P&L from the
            // ledger (TrialBalanceNormalizer does). Two contiguous insertions, nothing else moved. New fingerprint.
            ["2.19"] = "99e95ecd391ce5d6ebe45f7b2b0f714fc20f899f26acd99938feb1d7b99e2507",
            // 2.20 edits the extraction SystemPrompt: ONE contiguous insertion, ClaudeService.JsonOnlyAnswerRule, right
            // after the "respond EXCLUSIVELY with a valid JSON object" paragraph — the answer must start with { and carry
            // nothing before or after the JSON object (no explanation, no reasoning, no Markdown). Long balances wrote
            // visible reasoning first and overflowed max_tokens. Nothing else moved. New fingerprint.
            ["2.20"] = "4bee842418511f0f47fb5870a60256a358370d0ed66231bd6e132236a986964c",
            // 2.21 edits the extraction SystemPrompt: a "which alerts to raise" section (ClaudeService.AlertScopeRules)
            // between the alert-title block and the ratio units — no alert whose only content is a ratio level (the
            // engine grades those), alerts for what only the document shows, and the catalogue's exact code for a fact
            // the engine also states; the financial_expenses rule names COBERTURA_INTERESES_INDETERMINABLE; the title
            // example "Deuda bancaria elevada" becomes "Salvedad del auditor". Nothing else moved. New fingerprint.
            // Re-registered pre-publication (2026-11-10): the revenue drop's code was VENTAS_EN_CAIDA, which
            // AttentionOverlaps maps to the LUFE sales fact (the PDF would hide the alert behind the LUFE bullet); it is
            // now INGRESOS_EN_CAIDA, listed nowhere. 2.21 never shipped, so the fingerprint is updated in place.
            ["2.21"] = "1334c3111cc5bd00d736fddd56f00e5bee6937a167b60abe04a9781bfadc6594",
        };

        var version = ClaudeService.ExtractionPromptVersion;
        var actual = Sha256Hex(ClaudeService.SystemPrompt);

        Assert.True(
            fingerprints.TryGetValue(version, out var expected),
            $"No SystemPrompt fingerprint registered for ExtractionPromptVersion \"{version}\". " +
            $"Add [\"{version}\"] = \"{actual}\" to this test's registry.");

        Assert.True(
            expected == actual,
            $"SystemPrompt changed but ExtractionPromptVersion is still \"{version}\". " +
            $"Bump ExtractionPromptVersion and register [\"<new version>\"] = \"{actual}\".");
    }

    private static string Sha256Hex(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
