// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-16

using System;
using System.Collections.Generic;
using System.Linq;
using Api.Web.FinanceIA.Models.Financia;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Covers <see cref="ExtractionDeterminismReport.Build"/> — the pure aggregation behind the
/// determinism harness. The harness itself only runs against a real PDF and a paid API, so the
/// logic that decides "this code is stable", "this severity moved", "this field flipped between
/// null and a value" is locked here instead, against synthetic runs.
///
/// Every figure and code below is invented. No client data, and no network call: these run in a
/// normal pass in milliseconds.
/// </summary>
public sealed class ExtractionDeterminismReportTests
{
    // ── Section 1: alert catalogue ────────────────────────────────────────────

    [Fact]
    public void Build_SortsCodesPresentInAllRunsAheadOfOneOffs()
    {
        var report = Build(
            Ok(1, EmptyBalance, Alerts(("high", "TEST_ALWAYS", "Always", "Seen every run"),
                                       ("low",  "TEST_ONEOFF", "Once",   "Seen once"))),
            Ok(2, EmptyBalance, Alerts(("high", "TEST_ALWAYS", "Always", "Seen every run"))));

        Assert.Contains("TEST_ALWAYS: 2/2", report);
        Assert.Contains("TEST_ONEOFF: 1/2", report);
        Assert.True(
            report.IndexOf("TEST_ALWAYS: 2/2", StringComparison.Ordinal)
            < report.IndexOf("TEST_ONEOFF: 1/2", StringComparison.Ordinal),
            "A code seen in every run must be listed before a one-off.");
    }

    [Fact]
    public void Build_WhenOneCodeGetsTwoSeverities_FlagsItLoudly()
    {
        var report = Build(
            Ok(1, EmptyBalance, Alerts(("high",     "TEST_MOVES", "Moves", "Severity changes"))),
            Ok(2, EmptyBalance, Alerts(("critical", "TEST_MOVES", "Moves", "Severity changes"))));

        Assert.Contains("*** SEVERITY UNSTABLE", report);
        Assert.Contains("critical x1", report);
        Assert.Contains("high x1", report);
    }

    [Fact]
    public void Build_WhenSeverityIsConsistent_DoesNotFlagIt()
    {
        var report = Build(
            Ok(1, EmptyBalance, Alerts(("high", "TEST_STEADY", "Steady", "Same every run"))),
            Ok(2, EmptyBalance, Alerts(("high", "TEST_STEADY", "Steady", "Same every run"))));

        Assert.DoesNotContain("*** SEVERITY UNSTABLE", report);
        Assert.Contains("high x2", report);
    }

    [Fact]
    public void Build_ListsOnlyFullyStableCodesAsDeterministicCandidates()
    {
        var report = Build(
            Ok(1, EmptyBalance, Alerts(("high", "TEST_ALWAYS", "Always", "Every run"),
                                       ("low",  "TEST_SOMETIMES", "Sometimes", "Not every run"))),
            Ok(2, EmptyBalance, Alerts(("high", "TEST_ALWAYS", "Always", "Every run"))));

        var candidates = report[report.IndexOf("### 1b.", StringComparison.Ordinal)..];

        Assert.Contains("TEST_ALWAYS", candidates);
        Assert.DoesNotContain("TEST_SOMETIMES", candidates);
    }

    [Fact]
    public void Build_CarriesOneExampleTitleAndDescriptionPerCode()
    {
        var report = Build(
            Ok(1, EmptyBalance, Alerts(("high", "TEST_DOC", "Short title", "A human-readable explanation."))));

        Assert.Contains("Short title", report);
        Assert.Contains("A human-readable explanation.", report);
    }

    // ── Section 2: balance variance ───────────────────────────────────────────

    [Fact]
    public void Build_WhenAFieldFlipsBetweenNullAndAValue_ReportsItAsAFlip()
    {
        var report = Build(
            Ok(1, """{"assets":100.0,"financial_expenses":-5.0}""", NoAlerts),
            Ok(2, """{"assets":100.0,"financial_expenses":null}""",  NoAlerts));

        Assert.Contains("### 2a. NULL vs VALUE FLIPS (1)", report);
        Assert.Contains("*** FLIP *** financial_expenses", report);
        Assert.Contains("FOCUS financial_expenses: *** FLIPS between null/absent and a value ***", report);
    }

    [Fact]
    public void Build_WhenAFieldIsAbsentInSomeRuns_CountsThatAsAFlipToo()
    {
        var report = Build(
            Ok(1, """{"assets":100.0,"financial_expenses":-5.0}""", NoAlerts),
            Ok(2, """{"assets":100.0}""",                           NoAlerts));

        Assert.Contains("*** FLIP *** financial_expenses", report);
        Assert.Contains("(absent)", report);
    }

    [Fact]
    public void Build_WhenEveryFieldHoldsSteady_ReportsNoFlips()
    {
        var report = Build(
            Ok(1, """{"assets":100.0,"equity":40.0}""", NoAlerts),
            Ok(2, """{"assets":100.0,"equity":40.0}""", NoAlerts));

        Assert.Contains("### 2a. NULL vs VALUE FLIPS (0)", report);
        Assert.Contains("assets = 100.0", report);
    }

    [Fact]
    public void Build_SeparatesCashFlowLeavesFromTheCoreBalanceFields()
    {
        var report = Build(
            Ok(1, """{"assets":100.0,"cash_flow":{"operating":7.0}}""", NoAlerts),
            Ok(2, """{"assets":100.0,"cash_flow":{"operating":8.0}}""", NoAlerts));

        var core     = Slice(report, "### 2b.", "### 2c.");
        var cashFlow = Slice(report, "### 2c.", "## 3.");

        Assert.Contains("assets", core);
        Assert.DoesNotContain("cash_flow.operating", core);
        Assert.Contains("cash_flow.operating", cashFlow);
    }

    // ── Section 3: computed ratios ────────────────────────────────────────────

    [Fact]
    public void Build_WhenARatioFlipsToNull_ReportsItSeparatelyFromNumericWobble()
    {
        var report = Build(
            Ok(1, EmptyBalance, NoAlerts, new RatiosData { InterestCoverage = 3.2,  CurrentRatio = 1.50 }),
            Ok(2, EmptyBalance, NoAlerts, new RatiosData { InterestCoverage = null, CurrentRatio = 1.51 }));

        var flips  = Slice(report, "### 3a.", "### 3b.");
        var wobble = Slice(report, "### 3b.", "### 3c.");

        Assert.Contains("*** FLIP *** interest_coverage", flips);
        Assert.DoesNotContain("interest_coverage", wobble);
        Assert.Contains("current_ratio", wobble);
    }

    [Fact]
    public void Build_WhenARatioOnlyWobbles_KeepsItOutOfTheFlipSection()
    {
        var report = Build(
            Ok(1, EmptyBalance, NoAlerts, new RatiosData { CurrentRatio = 1.50 }),
            Ok(2, EmptyBalance, NoAlerts, new RatiosData { CurrentRatio = 1.51 }));

        Assert.Contains("### 3a. NULL <-> VALUE FLIPS (0)", report);
        Assert.Contains("current_ratio: 2 distinct values", Slice(report, "### 3b.", "### 3c."));
    }

    [Fact]
    public void Build_ReportsInterestCoverageFocusEvenWhenStable()
    {
        var report = Build(
            Ok(1, EmptyBalance, NoAlerts, new RatiosData { InterestCoverage = 2.0 }),
            Ok(2, EmptyBalance, NoAlerts, new RatiosData { InterestCoverage = 2.0 }));

        Assert.Contains("FOCUS interest_coverage: stable at 2", report);
    }

    // ── Section 4 and 5 ───────────────────────────────────────────────────────

    [Fact]
    public void Build_ReportsAnomalyCountsAndWhetherTheArraysMatch()
    {
        var report = Build(
            Ok(1, EmptyBalance, NoAlerts, anomaliesJson: """["shared","only-in-one"]"""),
            Ok(2, EmptyBalance, NoAlerts, anomaliesJson: """["shared"]"""));

        Assert.Contains("run 1: 2 anomalies", report);
        Assert.Contains("run 2: 1 anomaly", report);
        Assert.Contains("byte-identical in every run : 1 of 2", report);
        Assert.Contains("whole array identical       : NO", report);
    }

    [Fact]
    public void Build_HeadlineIgnoresCashFlowWhenComparingBalanceChecks()
    {
        // Core figures match; only the cash_flow subtree moved. The headline must still
        // report the balance_check as identical, or 2c noise would swamp the signal.
        var report = Build(
            Ok(1, """{"assets":100.0,"cash_flow":{"operating":7.0}}""", NoAlerts),
            Ok(2, """{"assets":100.0,"cash_flow":{"operating":9.0}}""", NoAlerts));

        var headline = report[report.IndexOf("## 5. HEADLINE", StringComparison.Ordinal)..];

        Assert.Contains("balance_check (core fields)", headline);
        Assert.Contains("runs sharing the most common variant : 2 of 2", headline);
    }

    [Fact]
    public void Build_HeadlineReportsIdenticalAlertCodeSets()
    {
        var report = Build(
            Ok(1, EmptyBalance, Alerts(("high", "TEST_A", "A", "a"), ("low", "TEST_B", "B", "b"))),
            // Same codes, different order and different severity — the code SET is identical.
            Ok(2, EmptyBalance, Alerts(("low", "TEST_B", "B", "b"), ("critical", "TEST_A", "A", "a"))));

        var headline = report[report.IndexOf("## 5. HEADLINE", StringComparison.Ordinal)..];
        var alertPart = headline[headline.IndexOf("alert code set", StringComparison.Ordinal)..];

        Assert.Contains("all runs identical                   : YES", alertPart);
    }

    // ── Fault isolation ───────────────────────────────────────────────────────

    [Fact]
    public void Build_WhenARunFailed_RecordsItAndStillComparesTheRest()
    {
        var report = Build(
            Ok(1, """{"assets":100.0}""", Alerts(("high", "TEST_ALWAYS", "Always", "Every run"))),
            Failed(2, "HttpRequestException: transient"),
            Ok(3, """{"assets":100.0}""", Alerts(("high", "TEST_ALWAYS", "Always", "Every run"))));

        Assert.Contains("## 0. FAILED RUNS", report);
        Assert.Contains("run 2: HttpRequestException: transient", report);

        // The failed run is excluded from the denominators, not counted as a disagreement.
        Assert.Contains("TEST_ALWAYS: 2/2", report);
        Assert.Contains("Runs failed      : 1", report);
    }

    [Fact]
    public void Build_WhenEveryRunFailed_StillProducesAReportInsteadOfThrowing()
    {
        var report = Build(
            Failed(1, "TaskCanceledException: timeout"),
            Failed(2, "TaskCanceledException: timeout"));

        Assert.Contains("No successful run — nothing to compare.", report);
        Assert.Contains("## 1. ALERT CATALOGUE EVIDENCE", report);
        Assert.Contains("## 5. HEADLINE", report);
    }

    [Fact]
    public void Build_WhenAlertsJsonIsMalformed_ReportsItRatherThanFailing()
    {
        var report = Build(Ok(1, EmptyBalance, "{not json"));

        Assert.Contains("*** UNPARSEABLE AlertsJson in run(s): 1 ***", report);
    }

    [Fact]
    public void RunOutcome_CapturesNoFreeTextProse_SoTheDiffCannotDrownInIt()
    {
        // Structural guard: the harness never captures the executive summary, so no future
        // edit to the renderer can start diffing prose that changes on every run.
        var captured = typeof(RunOutcome).GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain("ExecutiveSummary", captured);
        Assert.Contains("BalanceJson", captured);
        Assert.Contains("AlertsJson", captured);
    }

    // ── Fixture helpers ───────────────────────────────────────────────────────

    private const string EmptyBalance = "{}";
    private const string NoAlerts     = "[]";

    private static string Build(params RunOutcome[] runs)
        => ExtractionDeterminismReport.Build(Header(runs.Length), runs);

    private static DeterminismReportHeader Header(int runs)
        => new(
            GeneratedUtc : "2026-08-16 00:00:00Z",
            RequestedRuns: runs,
            PromptVersion: "test",
            DocumentType : "Balance",
            FiscalYear   : null,
            PdfByteLength: 1024,
            PdfSha256    : "0000");

    private static RunOutcome Ok(
        int         index,
        string      balanceJson,
        string      alertsJson,
        RatiosData? ratios        = null,
        string      anomaliesJson = "[]")
        => new(
            Index            : index,
            Succeeded        : true,
            Error            : null,
            BalanceJson      : balanceJson,
            AlertsJson       : alertsJson,
            AnomaliesJson    : anomaliesJson,
            ConfidenceScore  : 0.90m,
            HighestAlertLevel: "high",
            ExtractionMethod : "document",
            ModelUsed        : "test-model",
            OutputHash       : $"hash-{index}",
            InputTokens      : 100,
            OutputTokens     : 200,
            ComputedRatios   : ratios);

    private static RunOutcome Failed(int index, string error)
        => new(
            Index            : index,
            Succeeded        : false,
            Error            : error,
            BalanceJson      : null,
            AlertsJson       : null,
            AnomaliesJson    : null,
            ConfidenceScore  : null,
            HighestAlertLevel: null,
            ExtractionMethod : null,
            ModelUsed        : null,
            OutputHash       : null,
            InputTokens      : null,
            OutputTokens     : null,
            ComputedRatios   : null);

    /// <summary>Builds an alerts JSON array from (level, code, title, description) tuples.</summary>
    private static string Alerts(params (string Level, string Code, string Title, string Description)[] alerts)
    {
        var parts = new List<string>(alerts.Length);

        foreach (var (level, code, title, description) in alerts)
        {
            parts.Add(
                $$"""{"level":"{{level}}","code":"{{code}}","title":"{{title}}","description":"{{description}}"}""");
        }

        return "[" + string.Join(",", parts) + "]";
    }

    /// <summary>Returns the report text between two headings, so a section can be asserted in isolation.</summary>
    private static string Slice(string report, string from, string to)
    {
        var start = report.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Heading '{from}' not found in the report.");

        var end = report.IndexOf(to, start, StringComparison.Ordinal);
        return end < 0 ? report[start..] : report[start..end];
    }
}
