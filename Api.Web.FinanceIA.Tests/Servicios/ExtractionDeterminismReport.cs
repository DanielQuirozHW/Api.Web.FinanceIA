// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-16

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Tests.Servicios;

// ── Inputs ────────────────────────────────────────────────────────────────────

/// <summary>
/// One extraction run's decision-relevant output. Free-text prose (executive summary,
/// alert descriptions) is deliberately NOT compared — it differs on every run and would
/// drown the structured signal. Descriptions survive only as the single illustrative
/// example in section 1.
/// </summary>
/// <param name="Index">1-based run number, in execution order.</param>
/// <param name="Succeeded">False when the API call threw; the run is then reported but excluded from every comparison.</param>
public sealed record RunOutcome(
    int         Index,
    bool        Succeeded,
    string?     Error,
    string?     BalanceJson,
    string?     AlertsJson,
    string?     AnomaliesJson,
    decimal?    ConfidenceScore,
    string?     HighestAlertLevel,
    string?     ExtractionMethod,
    string?     ModelUsed,
    string?     OutputHash,
    int?        InputTokens,
    int?        OutputTokens,
    RatiosData? ComputedRatios);

/// <summary>Provenance stamped at the top of the report so a stored copy is self-describing.</summary>
public sealed record DeterminismReportHeader(
    string GeneratedUtc,
    int    RequestedRuns,
    string PromptVersion,
    string DocumentType,
    int?   FiscalYear,
    long   PdfByteLength,
    string PdfSha256);

/// <summary>
/// Mirror of the persisted alert element. The two production shapes
/// (<c>ClaudeService.AlertJson</c>, <c>AnalysisController.ClaudeAlertJson</c>) are both
/// <c>private</c>, so the harness declares its own with the same four JSON keys.
/// </summary>
public sealed class DeterminismAlert
{
    [JsonPropertyName("level")]       public string? Level       { get; set; }
    [JsonPropertyName("code")]        public string? Code        { get; set; }
    [JsonPropertyName("title")]       public string? Title       { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
}

// ── Report builder ────────────────────────────────────────────────────────────

/// <summary>
/// Pure aggregation and rendering of an N-run determinism comparison. No I/O, no API, no
/// xUnit — which is what lets the report logic itself be unit-tested against synthetic runs.
/// </summary>
public static class ExtractionDeterminismReport
{
    /// <summary>Mirrors <c>AnalysisController.BcraJsonOpts</c>, which is private and therefore
    /// unreachable from here. One property, no converters, no naming policy: the mapping rests
    /// entirely on the [JsonPropertyName] attributes of the target types.</summary>
    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private const string NullToken       = "null";
    private const string AbsentToken     = "(absent)";
    private const string CashFlowPrefix  = "cash_flow";
    private const int    DescriptionClip = 400;

    // Same rate the controller books extraction against (AnalysisController.cs:272-274).
    private const decimal InputCostPerMTok  = 3m;
    private const decimal OutputCostPerMTok = 15m;

    /// <summary>Renders the full report. Deterministic: same inputs always produce the same text.</summary>
    public static string Build(DeterminismReportHeader header, IReadOnlyList<RunOutcome> runs)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(runs);

        var sb        = new StringBuilder();
        var succeeded = runs.Where(r => r.Succeeded).OrderBy(r => r.Index).ToList();

        AppendHeader(sb, header, runs, succeeded);
        AppendAlertCatalogue(sb, succeeded);
        AppendBalanceVariance(sb, succeeded);
        AppendRatioVariance(sb, succeeded);
        AppendRest(sb, succeeded);
        AppendHeadline(sb, succeeded);

        return sb.ToString();
    }

    // ── Header ────────────────────────────────────────────────────────────────

    private static void AppendHeader(
        StringBuilder sb,
        DeterminismReportHeader header,
        IReadOnlyList<RunOutcome> runs,
        IReadOnlyList<RunOutcome> succeeded)
    {
        sb.AppendLine("# Extraction determinism report");
        sb.AppendLine();
        sb.AppendLine($"Generated (UTC)  : {header.GeneratedUtc}");
        sb.AppendLine($"Runs requested   : {header.RequestedRuns}");
        sb.AppendLine($"Runs succeeded   : {succeeded.Count}");
        sb.AppendLine($"Runs failed      : {runs.Count - succeeded.Count}");
        sb.AppendLine($"Prompt version   : {header.PromptVersion}");
        sb.AppendLine($"Document type    : {header.DocumentType}");
        sb.AppendLine($"Fiscal year arg  : {Describe(header.FiscalYear)}");
        sb.AppendLine($"PDF byte length  : {header.PdfByteLength.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"PDF SHA-256      : {header.PdfSha256}");
        sb.AppendLine();
        sb.AppendLine("Every run used the same bytes, document type and fiscal year, so prompt");
        sb.AppendLine("version and model are identical by construction. Free-text prose");
        sb.AppendLine("(executive_summary, alert descriptions) is excluded from every diff.");
        sb.AppendLine();

        var failed = runs.Where(r => !r.Succeeded).OrderBy(r => r.Index).ToList();
        if (failed.Count > 0)
        {
            sb.AppendLine("## 0. FAILED RUNS");
            sb.AppendLine();
            sb.AppendLine("Recorded and skipped; the batch carried on so one transient error costs one run.");
            sb.AppendLine();
            foreach (var run in failed)
            {
                sb.AppendLine($"  run {run.Index}: {run.Error ?? "(no detail)"}");
            }
            sb.AppendLine();
        }

        if (succeeded.Count == 0)
        {
            sb.AppendLine("No successful run — nothing to compare.");
            sb.AppendLine();
        }
    }

    // ── Section 1 ─────────────────────────────────────────────────────────────

    private static void AppendAlertCatalogue(StringBuilder sb, IReadOnlyList<RunOutcome> runs)
    {
        sb.AppendLine("## 1. ALERT CATALOGUE EVIDENCE");
        sb.AppendLine();
        sb.AppendLine("Seed catalogue for the deterministic rule engine. Severities are compared");
        sb.AppendLine("case-insensitively. A code carrying more than one severity across runs is a");
        sb.AppendLine("rule whose severity cannot simply be lifted from the model.");
        sb.AppendLine();

        if (runs.Count == 0)
        {
            sb.AppendLine("  (no successful runs)");
            sb.AppendLine();
            return;
        }

        var stats      = new Dictionary<string, AlertCodeStat>(StringComparer.Ordinal);
        var malformed  = new List<int>();

        foreach (var run in runs)
        {
            if (!TryParseAlerts(run.AlertsJson, out var alerts))
            {
                malformed.Add(run.Index);
                continue;
            }

            foreach (var group in alerts.GroupBy(a => NormalizeCode(a.Code), StringComparer.Ordinal))
            {
                if (!stats.TryGetValue(group.Key, out var stat))
                {
                    stat = new AlertCodeStat(group.Key);
                    stats[group.Key] = stat;
                }

                stat.Observe(run.Index, group);
            }
        }

        if (malformed.Count > 0)
        {
            sb.AppendLine($"  *** UNPARSEABLE AlertsJson in run(s): {string.Join(", ", malformed)} ***");
            sb.AppendLine();
        }

        if (stats.Count == 0)
        {
            sb.AppendLine("  (no alerts observed in any run)");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("### 1a. Codes by stability");
        sb.AppendLine();

        // All-runs codes first, then partial, then one-offs; alphabetical within a tier.
        var ordered = stats.Values
            .OrderByDescending(s => s.RunsPresent)
            .ThenBy(s => s.Code, StringComparer.Ordinal)
            .ToList();

        foreach (var stat in ordered)
        {
            sb.AppendLine($"  {stat.Code}: {stat.RunsPresent}/{runs.Count}");
            sb.AppendLine($"    runs       : {string.Join(", ", stat.RunIndexes)}");
            sb.AppendLine($"    severities : {stat.DescribeSeverities()}");

            if (stat.HasUnstableSeverity)
            {
                sb.AppendLine($"    *** SEVERITY UNSTABLE: one code, {stat.DistinctSeverityCount} different severities ***");
            }

            sb.AppendLine($"    title      : {Describe(stat.ExampleTitle)}");
            sb.AppendLine($"    description: {Describe(Clip(stat.ExampleDescription, DescriptionClip))}");
            sb.AppendLine();
        }

        var always = ordered.Where(s => s.RunsPresent == runs.Count).ToList();

        sb.AppendLine($"### 1b. Codes appearing in 100% of runs — candidate deterministic rules ({always.Count})");
        sb.AppendLine();

        if (always.Count == 0)
        {
            sb.AppendLine("  (none — no alert code survived every run)");
        }
        else
        {
            foreach (var stat in always)
            {
                var severityNote = stat.HasUnstableSeverity
                    ? "  <-- severity still varies; the rule must set it, not the model"
                    : string.Empty;
                sb.AppendLine($"  {stat.Code}{severityNote}");
            }
        }

        sb.AppendLine();
    }

    // ── Section 2 ─────────────────────────────────────────────────────────────

    private static void AppendBalanceVariance(StringBuilder sb, IReadOnlyList<RunOutcome> runs)
    {
        sb.AppendLine("## 2. BALANCE_CHECK VARIANCE");
        sb.AppendLine();

        if (runs.Count == 0)
        {
            sb.AppendLine("  (no successful runs)");
            sb.AppendLine();
            return;
        }

        var perRun    = new List<(int Index, IReadOnlyDictionary<string, string> Leaves)>();
        var malformed = new List<int>();

        foreach (var run in runs)
        {
            if (TryFlattenNumericLeaves(run.BalanceJson, out var leaves))
                perRun.Add((run.Index, leaves));
            else
                malformed.Add(run.Index);
        }

        if (malformed.Count > 0)
        {
            sb.AppendLine($"  *** UNPARSEABLE BalanceJson in run(s): {string.Join(", ", malformed)} ***");
            sb.AppendLine();
        }

        if (perRun.Count == 0)
        {
            sb.AppendLine("  (no parseable BalanceJson)");
            sb.AppendLine();
            return;
        }

        var fields = BuildFieldComparisons(perRun);
        var core   = fields.Where(f => !IsCashFlow(f.Path)).ToList();
        var flow   = fields.Where(f =>  IsCashFlow(f.Path)).ToList();

        // 2a — the flips lead, because they are what moves the score.
        var flips = fields.Where(f => f.IsNullValueFlip).ToList();

        sb.AppendLine($"### 2a. NULL vs VALUE FLIPS ({flips.Count}) — most serious");
        sb.AppendLine();
        sb.AppendLine("A field the model reported in some runs and omitted or nulled in others.");
        sb.AppendLine("These change which ratios can be computed at all, and therefore the score.");
        sb.AppendLine();

        if (flips.Count == 0)
        {
            sb.AppendLine("  (none — no field flipped between null/absent and a value)");
        }
        else
        {
            foreach (var field in flips)
            {
                sb.AppendLine($"  *** FLIP *** {field.Path}");
                sb.AppendLine($"    {field.DescribePerRun()}");
            }
        }

        sb.AppendLine();

        AppendFieldBlock(sb, "### 2b. Core balance fields — distinct values", core, perRun.Count);
        AppendFocus(sb, core, "financial_expenses");

        AppendFieldBlock(sb, "### 2c. cash_flow subtree — distinct values", flow, perRun.Count);
        sb.AppendLine("cash_flow feeds the narrative and reconciliation, not the ratios, so it is");
        sb.AppendLine("reported separately and excluded from the section 5 headline.");
        sb.AppendLine();
    }

    // ── Section 3 ─────────────────────────────────────────────────────────────

    private static void AppendRatioVariance(StringBuilder sb, IReadOnlyList<RunOutcome> runs)
    {
        sb.AppendLine("## 3. COMPUTED RATIO VARIANCE");
        sb.AppendLine();
        sb.AppendLine("Ratios recomputed per run from that run's BalanceJson by the same");
        sb.AppendLine("IRatioCalculationService the controller uses — so this measures extraction");
        sb.AppendLine("variance, not arithmetic. The controller keeps these over the model's own.");
        sb.AppendLine();

        if (runs.Count == 0)
        {
            sb.AppendLine("  (no successful runs)");
            sb.AppendLine();
            return;
        }

        var comparisons = new List<FieldComparison>();

        foreach (var accessor in RatioAccessors)
        {
            var values = runs
                .Select(r => (r.Index, Value: FormatRatio(accessor.Get(r.ComputedRatios))))
                .ToList();

            comparisons.Add(new FieldComparison(accessor.Label, values));
        }

        var flips  = comparisons.Where(c => c.IsNullValueFlip).ToList();
        var wobble = comparisons.Where(c => !c.IsNullValueFlip && !c.IsStable).ToList();
        var stable = comparisons.Where(c => c.IsStable).ToList();

        sb.AppendLine($"### 3a. NULL <-> VALUE FLIPS ({flips.Count}) — the serious kind");
        sb.AppendLine();
        sb.AppendLine("A ratio that exists in some runs and not others: the analysis silently gains");
        sb.AppendLine("or loses a whole dimension. Distinct from a last-decimal wobble.");
        sb.AppendLine();

        if (flips.Count == 0)
        {
            sb.AppendLine("  (none)");
        }
        else
        {
            foreach (var c in flips)
            {
                sb.AppendLine($"  *** FLIP *** {c.Path}");
                sb.AppendLine($"    {c.DescribePerRun()}");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"### 3b. Numeric wobble ({wobble.Count}) — always present, value moved");
        sb.AppendLine();

        if (wobble.Count == 0)
        {
            sb.AppendLine("  (none)");
        }
        else
        {
            foreach (var c in wobble)
            {
                sb.AppendLine($"  {c.Path}: {c.DistinctCount} distinct values");
                sb.AppendLine($"    {c.DescribeDistinct()}");
                sb.AppendLine($"    {c.DescribePerRun()}");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"### 3c. Stable ratios ({stable.Count})");
        sb.AppendLine();

        if (stable.Count == 0)
        {
            sb.AppendLine("  (none)");
        }
        else
        {
            foreach (var c in stable)
            {
                sb.AppendLine($"  {c.Path}: {c.SingleValue}");
            }
        }

        sb.AppendLine();
        AppendFocus(sb, comparisons, "interest_coverage");
    }

    // ── Section 4 ─────────────────────────────────────────────────────────────

    private static void AppendRest(StringBuilder sb, IReadOnlyList<RunOutcome> runs)
    {
        sb.AppendLine("## 4. REST");
        sb.AppendLine();

        if (runs.Count == 0)
        {
            sb.AppendLine("  (no successful runs)");
            sb.AppendLine();
            return;
        }

        // ── Anomalies ──
        sb.AppendLine("### 4a. Anomalies");
        sb.AppendLine();

        var anomaliesPerRun = new List<(int Index, List<string> Items)>();
        foreach (var run in runs)
        {
            if (TryParseAnomalies(run.AnomaliesJson, out var items))
                anomaliesPerRun.Add((run.Index, items));
            else
                sb.AppendLine($"  *** UNPARSEABLE AnomaliesJson in run {run.Index} ***");
        }

        foreach (var (index, items) in anomaliesPerRun)
        {
            sb.AppendLine($"  run {index}: {items.Count} {(items.Count == 1 ? "anomaly" : "anomalies")}");
        }

        sb.AppendLine();

        if (anomaliesPerRun.Count > 0)
        {
            var sharedByAll = anomaliesPerRun
                .Select(r => (IEnumerable<string>)r.Items.Distinct(StringComparer.Ordinal))
                .Aggregate((a, b) => a.Intersect(b, StringComparer.Ordinal))
                .Count();

            var totalDistinct = anomaliesPerRun
                .SelectMany(r => r.Items)
                .Distinct(StringComparer.Ordinal)
                .Count();

            var identicalArrays = anomaliesPerRun
                .Select(r => string.Join("", r.Items))
                .Distinct(StringComparer.Ordinal)
                .Count() == 1;

            sb.AppendLine($"  byte-identical in every run : {sharedByAll} of {totalDistinct} distinct anomaly strings");
            sb.AppendLine($"  whole array identical       : {(identicalArrays ? "YES" : "NO")}");
            sb.AppendLine();
        }

        // ── Scalars ──
        sb.AppendLine("### 4b. Scalars");
        sb.AppendLine();
        sb.AppendLine($"  confidence_score      : {DescribeDistinct(runs.Select(r => Describe(r.ConfidenceScore)))}");
        sb.AppendLine($"  fiscal_year_detected  : {DescribeDistinct(runs.Select(r => ReadLeaf(r.BalanceJson, "fiscal_year_detected")))}");
        sb.AppendLine($"  highest_alert_level   : {DescribeDistinct(runs.Select(r => Describe(r.HighestAlertLevel)))}");
        sb.AppendLine();

        // ── Run metadata ──
        sb.AppendLine("### 4c. Run metadata");
        sb.AppendLine();
        sb.AppendLine("  run | extraction_method | model | output_hash (12) | in_tok | out_tok");

        foreach (var run in runs)
        {
            var hash = run.OutputHash is { Length: > 12 } ? run.OutputHash[..12] : Describe(run.OutputHash);
            sb.AppendLine(
                $"  {run.Index,3} | {Describe(run.ExtractionMethod),-17} | {Describe(run.ModelUsed)} | " +
                $"{hash} | {Describe(run.InputTokens)} | {Describe(run.OutputTokens)}");
        }

        sb.AppendLine();

        var distinctHashes = runs.Select(r => Describe(r.OutputHash)).Distinct(StringComparer.Ordinal).Count();
        sb.AppendLine($"  distinct raw-response hashes : {distinctHashes} of {runs.Count}");
        sb.AppendLine("  (OutputHash is SHA-256 of the raw API response: 1 would mean byte-identical replies)");

        var methods = runs.Select(r => Describe(r.ExtractionMethod)).Distinct(StringComparer.Ordinal).ToList();
        if (methods.Contains("image-fallback", StringComparer.Ordinal))
        {
            sb.AppendLine("  *** the confidence-based image fallback fired in at least one run — a second");
            sb.AppendLine("      extraction pass ran there, which is itself a variance source ***");
        }

        var inTokens  = runs.Sum(r => (long?)r.InputTokens  ?? 0);
        var outTokens = runs.Sum(r => (long?)r.OutputTokens ?? 0);
        var cost      = (inTokens * InputCostPerMTok + outTokens * OutputCostPerMTok) / 1_000_000m;

        sb.AppendLine();
        sb.AppendLine($"  total tokens  : {inTokens} in / {outTokens} out");
        sb.AppendLine($"  approx. cost  : USD {cost.ToString("0.####", CultureInfo.InvariantCulture)} " +
                      "(controller's sonnet rate, 3/15 per MTok)");
        sb.AppendLine();
    }

    // ── Section 5 ─────────────────────────────────────────────────────────────

    private static void AppendHeadline(StringBuilder sb, IReadOnlyList<RunOutcome> runs)
    {
        sb.AppendLine("## 5. HEADLINE");
        sb.AppendLine();

        if (runs.Count == 0)
        {
            sb.AppendLine("  (no successful runs)");
            sb.AppendLine();
            return;
        }

        // balance_check identity, core fields only (cash_flow excluded — see 2c).
        var balanceKeys = new List<string>();
        foreach (var run in runs)
        {
            if (!TryFlattenNumericLeaves(run.BalanceJson, out var leaves)) continue;

            balanceKeys.Add(string.Join(
                "",
                leaves.Where(kv => !IsCashFlow(kv.Key))
                      .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                      .Select(kv => $"{kv.Key}={kv.Value}")));
        }

        AppendIdentityLine(sb, "balance_check (core fields)", balanceKeys, runs.Count);

        // Alert-code-set identity.
        var alertKeys = new List<string>();
        foreach (var run in runs)
        {
            if (!TryParseAlerts(run.AlertsJson, out var alerts)) continue;

            alertKeys.Add(string.Join(
                "",
                alerts.Select(a => NormalizeCode(a.Code))
                      .Distinct(StringComparer.Ordinal)
                      .OrderBy(c => c, StringComparer.Ordinal)));
        }

        AppendIdentityLine(sb, "alert code set", alertKeys, runs.Count);
        sb.AppendLine();
    }

    private static void AppendIdentityLine(StringBuilder sb, string label, List<string> keys, int runCount)
    {
        if (keys.Count == 0)
        {
            sb.AppendLine($"  {label}: (nothing comparable)");
            return;
        }

        var groups = keys.GroupBy(k => k, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ToList();
        var modal  = groups[0].Count();

        sb.AppendLine($"  {label}:");
        sb.AppendLine($"    runs sharing the most common variant : {modal} of {runCount}");
        sb.AppendLine($"    distinct variants                    : {groups.Count}");
        sb.AppendLine($"    all runs identical                   : {(groups.Count == 1 ? "YES" : "NO")}");
    }

    // ── Field comparison ──────────────────────────────────────────────────────

    private static List<FieldComparison> BuildFieldComparisons(
        List<(int Index, IReadOnlyDictionary<string, string> Leaves)> perRun)
    {
        var paths = perRun
            .SelectMany(r => r.Leaves.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        return paths
            .Select(path => new FieldComparison(
                path,
                perRun.Select(r => (r.Index, Value: r.Leaves.TryGetValue(path, out var v) ? v : AbsentToken)).ToList()))
            .ToList();
    }

    private static void AppendFieldBlock(
        StringBuilder sb, string heading, List<FieldComparison> fields, int runCount)
    {
        var moved = fields.Where(f => !f.IsStable).ToList();
        var fixedFields = fields.Where(f => f.IsStable).ToList();

        sb.AppendLine($"{heading} ({fields.Count} fields, {moved.Count} moved)");
        sb.AppendLine();

        if (fields.Count == 0)
        {
            sb.AppendLine("  (none present)");
            sb.AppendLine();
            return;
        }

        foreach (var field in moved)
        {
            var flag = field.IsNullValueFlip ? "  *** NULL/VALUE FLIP ***" : string.Empty;
            sb.AppendLine($"  {field.Path}: {field.DistinctCount} distinct values{flag}");
            sb.AppendLine($"    {field.DescribeDistinct()}");
            sb.AppendLine($"    {field.DescribePerRun()}");
        }

        if (moved.Count > 0) sb.AppendLine();

        sb.AppendLine($"  stable across all {runCount} runs ({fixedFields.Count}):");
        foreach (var field in fixedFields)
        {
            sb.AppendLine($"    {field.Path} = {field.SingleValue}");
        }

        sb.AppendLine();
    }

    private static void AppendFocus(StringBuilder sb, List<FieldComparison> fields, string suffix)
    {
        var match = fields.FirstOrDefault(f =>
            f.Path.Equals(suffix, StringComparison.Ordinal) ||
            f.Path.EndsWith("." + suffix, StringComparison.Ordinal));

        sb.Append($"  FOCUS {suffix}: ");

        if (match is null)
        {
            sb.AppendLine("not present in any run");
        }
        else if (match.IsNullValueFlip)
        {
            sb.AppendLine($"*** FLIPS between null/absent and a value *** — {match.DescribePerRun()}");
        }
        else if (match.IsStable)
        {
            sb.AppendLine($"stable at {match.SingleValue}");
        }
        else
        {
            sb.AppendLine($"{match.DistinctCount} distinct values — {match.DescribeDistinct()}");
        }

        sb.AppendLine();
    }

    /// <summary>One field's value in each run, plus the verdicts drawn from it.</summary>
    private sealed class FieldComparison
    {
        private readonly List<(int Index, string Value)> _values;

        public FieldComparison(string path, List<(int Index, string Value)> values)
        {
            Path    = path;
            _values = values;
        }

        public string Path { get; }

        public int DistinctCount => _values.Select(v => v.Value).Distinct(StringComparer.Ordinal).Count();

        public bool IsStable => DistinctCount <= 1;

        public string SingleValue => _values.Count == 0 ? AbsentToken : _values[0].Value;

        /// <summary>True when the field held a real value in some runs and null/absent in others.</summary>
        public bool IsNullValueFlip
        {
            get
            {
                var hasValue = _values.Any(v => !IsMissing(v.Value));
                var hasNone  = _values.Any(v =>  IsMissing(v.Value));
                return hasValue && hasNone;
            }
        }

        public string DescribeDistinct() =>
            "values: " + string.Join(" | ", _values.Select(v => v.Value).Distinct(StringComparer.Ordinal));

        public string DescribePerRun() =>
            string.Join("  ", _values.Select(v => $"run{v.Index}={v.Value}"));

        private static bool IsMissing(string value) =>
            string.Equals(value, NullToken, StringComparison.Ordinal) ||
            string.Equals(value, AbsentToken, StringComparison.Ordinal);
    }

    /// <summary>Accumulates one alert code's behaviour across runs.</summary>
    private sealed class AlertCodeStat
    {
        private readonly SortedSet<int>            _runIndexes  = new();
        private readonly Dictionary<string, int>   _severityRuns = new(StringComparer.Ordinal);

        public AlertCodeStat(string code) => Code = code;

        public string Code { get; }
        public string? ExampleTitle { get; private set; }
        public string? ExampleDescription { get; private set; }

        public int RunsPresent => _runIndexes.Count;
        public IEnumerable<int> RunIndexes => _runIndexes;
        public int DistinctSeverityCount => _severityRuns.Count;
        public bool HasUnstableSeverity => _severityRuns.Count > 1;

        /// <summary>Folds in every occurrence of this code within a single run.</summary>
        public void Observe(int runIndex, IEnumerable<DeterminismAlert> occurrences)
        {
            _runIndexes.Add(runIndex);

            var severitiesThisRun = new HashSet<string>(StringComparer.Ordinal);

            foreach (var alert in occurrences)
            {
                severitiesThisRun.Add(NormalizeSeverity(alert.Level));

                if (string.IsNullOrWhiteSpace(ExampleTitle) && !string.IsNullOrWhiteSpace(alert.Title))
                    ExampleTitle = alert.Title.Trim();

                if (string.IsNullOrWhiteSpace(ExampleDescription) && !string.IsNullOrWhiteSpace(alert.Description))
                    ExampleDescription = alert.Description.Trim();
            }

            // Count runs per severity, not occurrences — "3/5 runs called it high" is the useful shape.
            foreach (var severity in severitiesThisRun)
            {
                _severityRuns[severity] = _severityRuns.TryGetValue(severity, out var n) ? n + 1 : 1;
            }
        }

        public string DescribeSeverities() =>
            "{" + string.Join(", ", _severityRuns
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key} x{kv.Value}")) + "}";
    }

    // ── Parsing helpers ───────────────────────────────────────────────────────

    private static bool TryParseAlerts(string? json, out List<DeterminismAlert> alerts)
    {
        alerts = new List<DeterminismAlert>();
        if (string.IsNullOrWhiteSpace(json)) return true;

        try
        {
            alerts = JsonSerializer.Deserialize<List<DeterminismAlert>>(json, JsonOpts) ?? new List<DeterminismAlert>();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseAnomalies(string? json, out List<string> anomalies)
    {
        anomalies = new List<string>();
        if (string.IsNullOrWhiteSpace(json)) return true;

        try
        {
            anomalies = JsonSerializer.Deserialize<List<string>>(json, JsonOpts) ?? new List<string>();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Flattens every numeric, boolean and null leaf of the balance payload to a dotted path.
    /// Strings are skipped: in this payload they are prose or labels, not decision-relevant
    /// figures. Numbers keep their raw JSON text so no formatting round-trip can mask a change.
    /// </summary>
    internal static bool TryFlattenNumericLeaves(string? json, out IReadOnlyDictionary<string, string> leaves)
    {
        var sink = new Dictionary<string, string>(StringComparer.Ordinal);
        leaves   = sink;

        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            Flatten(document.RootElement, string.Empty, sink);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void Flatten(JsonElement element, string path, IDictionary<string, string> sink)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Flatten(property.Value, path.Length == 0 ? property.Name : $"{path}.{property.Name}", sink);
                }
                break;

            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Flatten(item, $"{path}[{i}]", sink);
                    i++;
                }
                break;

            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                sink[path] = element.GetRawText();
                break;

            case JsonValueKind.Null:
                sink[path] = NullToken;
                break;
        }
    }

    private static string ReadLeaf(string? json, string path) =>
        TryFlattenNumericLeaves(json, out var leaves) && leaves.TryGetValue(path, out var value)
            ? value
            : AbsentToken;

    // ── Ratio accessors ───────────────────────────────────────────────────────

    /// <summary>
    /// Every <see cref="RatiosData"/> property in declaration order, labelled by its
    /// [JsonPropertyName]. Reflected rather than hand-listed so a ratio added later is
    /// picked up without touching the harness.
    /// </summary>
    private static readonly (string Label, Func<RatiosData?, double?> Get)[] RatioAccessors =
        typeof(RatiosData)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(double?))
            .OrderBy(p => p.MetadataToken)
            .Select(p => (
                Label: p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name,
                Get:   new Func<RatiosData?, double?>(r => r is null ? null : (double?)p.GetValue(r))))
            .ToArray();

    // ── Formatting ────────────────────────────────────────────────────────────

    private static bool IsCashFlow(string path) =>
        path.Equals(CashFlowPrefix, StringComparison.Ordinal) ||
        path.StartsWith(CashFlowPrefix + ".", StringComparison.Ordinal) ||
        path.StartsWith(CashFlowPrefix + "[", StringComparison.Ordinal);

    private static string NormalizeCode(string? code) =>
        string.IsNullOrWhiteSpace(code) ? "(no code)" : code.Trim();

    private static string NormalizeSeverity(string? level) =>
        string.IsNullOrWhiteSpace(level) ? "(no level)" : level.Trim().ToLowerInvariant();

    private static string FormatRatio(double? value) =>
        value is null ? NullToken : value.Value.ToString("R", CultureInfo.InvariantCulture);

    private static string DescribeDistinct(IEnumerable<string> values)
    {
        var distinct = values.Distinct(StringComparer.Ordinal).ToList();
        return $"{distinct.Count} distinct — " + string.Join(" | ", distinct);
    }

    private static string Describe(string? value) => string.IsNullOrWhiteSpace(value) ? AbsentToken : value;

    private static string Describe(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? AbsentToken;

    private static string Describe(decimal? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? AbsentToken;

    private static string? Clip(string? value, int max) =>
        value is not null && value.Length > max ? value[..(max - 1)] + "…" : value;
}
