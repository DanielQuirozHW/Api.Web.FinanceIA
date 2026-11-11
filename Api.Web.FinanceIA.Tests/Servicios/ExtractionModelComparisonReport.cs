// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-01

using System.Globalization;
using System.Text;
using System.Text.Json;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>One extraction configuration under comparison: a model, and an effort where it takes one.</summary>
public sealed record ComparisonVariant(string Label, ExtractionModelSpec Spec, string? Effort);

/// <summary>One input PDF, known in every report only by its <see cref="Alias"/> (doc-01, doc-02…).</summary>
public sealed record ComparisonDocument(string Alias, string FileName, string Sha256, long Bytes, int Pages);

/// <summary>What one real <c>AnalyzeDocumentAsync</c> call produced, plus the fixed-input score.</summary>
public sealed record ComparisonRun
{
    public required string            DocAlias      { get; init; }
    public required ComparisonVariant Variant       { get; init; }
    public required int               RunIndex      { get; init; }
    public required bool              Succeeded     { get; init; }
    public string?                    FailureType   { get; init; }
    public string?                    FailureDetail { get; init; }
    public decimal?                   Confidence    { get; init; }
    public string?                    Method        { get; init; }
    public int                        InputTokens   { get; init; }
    public int                        OutputTokens  { get; init; }
    public decimal?                   CostUsd       { get; init; }
    public double                     LatencyMs     { get; init; }
    public IReadOnlyDictionary<string, string>? Fields { get; init; }
    public RatiosData?                Ratios        { get; init; }
    public decimal?                   ScoreRatios   { get; init; }
    public decimal?                   Score         { get; init; }
    public string?                    Level         { get; init; }

    // ── How the call went (no figures) ──
    /// <summary>A pass hit max_tokens and was re-sent once at the escalated cap.</summary>
    public bool                       Escalated           { get; init; }
    /// <summary>The largest output of any single pass (the one that counts against max_tokens).</summary>
    public int?                       MaxPassOutputTokens { get; init; }
    /// <summary><c>integrity.extraction.answerTrimmed</c>: the kept answer had text around its JSON.</summary>
    public bool                       AnswerTrimmed       { get; init; }
    /// <summary>Characters of text before / after the JSON object, from the trim Warning (null = none logged).</summary>
    public int?                       PreambleChars       { get; init; }
    public int?                       TrailerChars        { get; init; }
    /// <summary><c>ClaudeApiException.FailureCode</c> of a failed call.</summary>
    public string?                    FailureCode         { get; init; }
    /// <summary><c>integrity.passed</c>; null when the BalanceJson carries no integrity report.</summary>
    public bool?                      IntegrityPassed     { get; init; }
    /// <summary>Names of the integrity checks that ran and failed.</summary>
    public IReadOnlyList<string>?     FailedChecks        { get; init; }
    /// <summary>
    /// The model's alert codes, in answer order (<see cref="ExtractionModelComparisonReport.ReadAlertCodes"/>); never a
    /// title or a description. Null for a failed call, or a baseline snapshot written before codes were recorded.
    /// </summary>
    public IReadOnlyList<string>?     AlertCodes          { get; init; }
}

/// <summary>
/// One run as written to <c>…-runs.json</c>, so a later harness run (another commit, another prompt) can
/// use it as its baseline. Carries clients' figures: it lives in the output folder, outside the repo.
/// </summary>
public sealed record SnapshotRun
{
    public required string  DocAlias     { get; init; }
    public required string  VariantLabel { get; init; }
    public required string  ModelId      { get; init; }
    public string?          Effort       { get; init; }
    public required int     RunIndex     { get; init; }
    public required bool    Succeeded    { get; init; }
    public string?          FailureType  { get; init; }
    public string?          FailureCode  { get; init; }
    public decimal?         Confidence   { get; init; }
    public string?          Method       { get; init; }
    public int              InputTokens  { get; init; }
    public int              OutputTokens { get; init; }
    public decimal?         CostUsd      { get; init; }
    public double           LatencyMs    { get; init; }
    public Dictionary<string, string>? Fields { get; init; }
    public RatiosData?      Ratios       { get; init; }
    public decimal?         ScoreRatios  { get; init; }
    public decimal?         Score        { get; init; }
    public string?          Level        { get; init; }
    public bool             Escalated           { get; init; }
    public int?             MaxPassOutputTokens { get; init; }
    public bool             AnswerTrimmed       { get; init; }
    public int?             PreambleChars       { get; init; }
    public int?             TrailerChars        { get; init; }
    public bool?            IntegrityPassed     { get; init; }
    public List<string>?    FailedChecks        { get; init; }
    public List<string>?    AlertCodes          { get; init; }
}

/// <summary>The whole <c>…-runs.json</c>: the prompt version that produced it, its documents and runs.</summary>
public sealed record ComparisonSnapshot(string PromptVersion, IReadOnlyList<ComparisonDocument> Documents, IReadOnlyList<SnapshotRun> Runs);

/// <summary>How one key balance figure (or one *_items array, all its entries together) compares.</summary>
public sealed record KeyFieldStatus(string Key, FieldDeltaKind Kind, int Paths);

/// <summary>How one balance field compares with the baseline.</summary>
public enum FieldDeltaKind
{
    Identical,
    WithinTolerance,
    Different,
    NullFlip
}

/// <summary>One field of one comparison. Values are kept for the CSV only; the Markdown shows paths.</summary>
public sealed record FieldDelta(string Path, FieldDeltaKind Kind, double? RelativeDelta, string? Baseline, string? Candidate);

/// <summary>One ratio of one comparison.</summary>
public sealed record RatioDelta(string Name, double? Baseline, double? Candidate, double? Absolute, double? Relative, bool NullFlip);

/// <summary>A candidate run against the baseline run with the same index (or, for the noise floor, baseline run 1).</summary>
public sealed record RunComparison(
    string                    DocAlias,
    string                    GroupLabel,
    int                       RunIndex,
    IReadOnlyList<FieldDelta> Fields,
    IReadOnlyList<RatioDelta> Ratios,
    decimal?                  ScoreRatiosDelta,
    decimal?                  ScoreDelta,
    bool?                     BandChanged,
    string?                   BaselineLevel,
    string?                   CandidateLevel);

/// <summary>Provenance stamped at the top of the Markdown report.</summary>
public sealed record ComparisonHeader(
    DateTime UtcNow,
    string   PromptVersion,
    string   DocumentType,
    int?     FiscalYear,
    int      RunsPerVariant,
    decimal  UpperBoundUsd,
    string   CsvFileName,
    string   MapFileName);

/// <summary>
/// The pure half of ExtractionModelComparisonHarnessTests: variant parsing, the cost upper bound, the
/// field / ratio / score comparison and the three output files. Nothing here calls the API, so it is
/// unit-tested in CI with synthetic data (ExtractionModelComparisonReportTests).
/// </summary>
public static class ExtractionModelComparisonReport
{
    /// <summary>The header every output file opens with: they contain clients' figures.</summary>
    public const string Warning = "⚠ DATOS DE CLIENTES: no subir al repo ni compartir.";

    /// <summary>Relative tolerance below which two different figures count as "within tolerance" (0.5 %).</summary>
    public const double Tolerance = 0.005;

    /// <summary>Latency p95 above which the report flags the synchronous Portal path (App Service cuts at ~230 s).</summary>
    public const double SyncPathLatencyWarningMs = 200_000;

    /// <summary>Label of the noise-floor group: the baseline's own repeats against its first run.</summary>
    public const string NoiseFloorSuffix = " (repetición, piso de ruido)";

    // Heuristic token figures for the cost UPPER BOUND (not an estimate of the typical call):
    // a PDF document block costs ~1,500–3,000 text tokens per page plus its page image (~1,600);
    // a 220-DPI page image is downscaled by the API to ≤1568 px, ~1,600 tokens. Rounded up.
    internal const int DocumentPassTokensPerPage = 4_600;
    internal const int ImagePassTokensPerPage    = 1_800;
    internal const double CharsPerToken          = 3.0;
    internal const double Sonnet5TokenizerFactor = 1.35;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly (string Name, Func<RatiosData, double?> Get)[] RatioAccessors =
    [
        ("current_ratio",     r => r.CurrentRatio),
        ("acid_test",         r => r.AcidTest),
        ("debt_ratio",        r => r.DebtRatio),
        ("leverage",          r => r.Leverage),
        ("roe",               r => r.Roe),
        ("roa",               r => r.Roa),
        ("net_margin",        r => r.NetMargin),
        ("interest_coverage", r => r.InterestCoverage),
    ];

    // ── Variants ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Expands the model CSV × the effort CSV into variants, in order. A model that supports effort
    /// becomes one variant per effort (<c>claude-sonnet-5-5@high</c>) when efforts are given; any other
    /// model is one variant with no effort. The FIRST variant is the baseline. Throws
    /// <see cref="ArgumentException"/> for an unknown model, an invalid effort, or a duplicate.
    /// </summary>
    public static IReadOnlyList<ComparisonVariant> ParseVariants(string? modelsCsv, string? effortsCsv, string defaultModels)
    {
        var models = Split(string.IsNullOrWhiteSpace(modelsCsv) ? defaultModels : modelsCsv);
        if (models.Count == 0)
            throw new ArgumentException("No model to compare.");

        var efforts = new List<string>();
        foreach (var raw in Split(effortsCsv))
        {
            efforts.Add(ExtractionModelCatalog.NormalizeEffort(raw)
                ?? throw new ArgumentException(
                    $"Invalid effort '{raw}'. Valid: {string.Join(", ", ExtractionModelCatalog.ValidEfforts)}."));
        }

        var variants = new List<ComparisonVariant>();
        foreach (var raw in models)
        {
            var spec = ExtractionModelCatalog.Find(raw)
                ?? throw new ArgumentException(
                    $"Unknown model '{raw}'. Supported: {string.Join(", ", ExtractionModelCatalog.All.Select(m => m.Id))}.");

            if (spec.SupportsEffort && efforts.Count > 0)
            {
                foreach (var effort in efforts)
                    variants.Add(new ComparisonVariant($"{spec.Id}@{effort}", spec, effort));
            }
            else
            {
                variants.Add(new ComparisonVariant(spec.Id, spec, null));
            }
        }

        var duplicate = variants.GroupBy(v => v.Label, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"Variant '{duplicate.Key}' is listed twice.");

        return variants;
    }

    private static List<string> Split(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? []
            : csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

    // ── Cost upper bound ──────────────────────────────────────────────────────

    /// <summary>
    /// Upper-bound USD of ONE run of one PDF on one variant: both passes assumed (document + image
    /// fallback), each at its full <c>max_tokens</c>, the system prompt in both, and the 5.x tokenizer
    /// inflating input. Retries are NOT included.
    /// </summary>
    public static decimal UpperBoundUsdPerRun(ExtractionModelSpec spec, int pages, int systemPromptChars)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var promptTokens = (int)Math.Ceiling(systemPromptChars / CharsPerToken);
        double input     = 2.0 * promptTokens + Math.Max(1, pages) * (DocumentPassTokensPerPage + ImagePassTokensPerPage);
        if (spec.Id.StartsWith("claude-sonnet-5", StringComparison.OrdinalIgnoreCase))
            input *= Sonnet5TokenizerFactor;

        return ClaudeModelPricing.EstimateCostUsd(spec.Id, (int)Math.Ceiling(input), 2 * spec.MaxTokens)
            ?? throw new InvalidOperationException($"{spec.Id} has no price in ClaudeModelPricing.");
    }

    // ── Comparison ────────────────────────────────────────────────────────────

    /// <summary>Classifies one flattened field. Absent and "null" both count as no value.</summary>
    public static FieldDelta ClassifyField(string path, string? baseline, string? candidate)
    {
        var baseMissing = IsMissing(baseline);
        var candMissing = IsMissing(candidate);

        if (baseMissing && candMissing)
            return new FieldDelta(path, FieldDeltaKind.Identical, 0, baseline, candidate);

        if (baseMissing != candMissing)
            return new FieldDelta(path, FieldDeltaKind.NullFlip, null, baseline, candidate);

        if (string.Equals(baseline, candidate, StringComparison.Ordinal))
            return new FieldDelta(path, FieldDeltaKind.Identical, 0, baseline, candidate);

        if (double.TryParse(baseline, NumberStyles.Float, Inv, out var b) &&
            double.TryParse(candidate, NumberStyles.Float, Inv, out var c))
        {
            if (b == c)
                return new FieldDelta(path, FieldDeltaKind.Identical, 0, baseline, candidate);

            var relative = RelativeDelta(b, c);
            return new FieldDelta(path,
                relative <= Tolerance ? FieldDeltaKind.WithinTolerance : FieldDeltaKind.Different,
                relative, baseline, candidate);
        }

        return new FieldDelta(path, FieldDeltaKind.Different, null, baseline, candidate);
    }

    /// <summary>|a−b| / max(|a|,|b|); 0 when both are 0.</summary>
    public static double RelativeDelta(double a, double b)
    {
        var scale = Math.Max(Math.Abs(a), Math.Abs(b));
        return scale == 0 ? 0 : Math.Abs(a - b) / scale;
    }

    /// <summary>Compares two successful runs; null when either failed.</summary>
    public static RunComparison? Compare(ComparisonRun baseline, ComparisonRun candidate, string groupLabel)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);

        if (!baseline.Succeeded || !candidate.Succeeded)
            return null;

        var baseFields = baseline.Fields  ?? new Dictionary<string, string>();
        var candFields = candidate.Fields ?? new Dictionary<string, string>();

        var fields = baseFields.Keys.Union(candFields.Keys, StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => ClassifyField(p,
                baseFields.TryGetValue(p, out var bv) ? bv : null,
                candFields.TryGetValue(p, out var cv) ? cv : null))
            .ToList();

        var ratios = new List<RatioDelta>();
        foreach (var (name, get) in RatioAccessors)
        {
            var b = baseline.Ratios  is null ? null : get(baseline.Ratios);
            var c = candidate.Ratios is null ? null : get(candidate.Ratios);
            if (b is { } bv && c is { } cv)
                ratios.Add(new RatioDelta(name, bv, cv, cv - bv, RelativeDelta(bv, cv), false));
            else
                ratios.Add(new RatioDelta(name, b, c, null, null, b.HasValue != c.HasValue));
        }

        decimal? Delta(decimal? b, decimal? c) => b is { } bv && c is { } cv ? cv - bv : null;

        bool? bandChanged = baseline.Level is null || candidate.Level is null
            ? null
            : !string.Equals(baseline.Level, candidate.Level, StringComparison.Ordinal);

        return new RunComparison(
            candidate.DocAlias, groupLabel, candidate.RunIndex, fields, ratios,
            Delta(baseline.ScoreRatios, candidate.ScoreRatios),
            Delta(baseline.Score, candidate.Score),
            bandChanged, baseline.Level, candidate.Level);
    }

    /// <summary>
    /// Every comparison: each non-baseline variant's run i against baseline run i, plus the noise floor
    /// (baseline runs 2..R against baseline run 1) when there is more than one run.
    /// </summary>
    public static IReadOnlyList<RunComparison> CompareAll(
        IReadOnlyList<ComparisonVariant> variants, IReadOnlyList<ComparisonRun> runs)
    {
        var baseline    = variants[0];
        var comparisons = new List<RunComparison>();

        foreach (var doc in runs.Select(r => r.DocAlias).Distinct(StringComparer.Ordinal))
        {
            var baseRuns = runs.Where(r => r.DocAlias == doc && r.Variant.Label == baseline.Label)
                               .ToDictionary(r => r.RunIndex);

            if (baseRuns.TryGetValue(1, out var firstBase))
            {
                foreach (var repeat in baseRuns.Values.Where(r => r.RunIndex > 1).OrderBy(r => r.RunIndex))
                {
                    if (Compare(firstBase, repeat, baseline.Label + NoiseFloorSuffix) is { } floor)
                        comparisons.Add(floor);
                }
            }

            foreach (var variant in variants.Skip(1))
            {
                foreach (var run in runs.Where(r => r.DocAlias == doc && r.Variant.Label == variant.Label).OrderBy(r => r.RunIndex))
                {
                    if (baseRuns.TryGetValue(run.RunIndex, out var pair) && Compare(pair, run, variant.Label) is { } c)
                        comparisons.Add(c);
                }
            }
        }

        return comparisons;
    }

    /// <summary>Nearest-rank percentile (p in 0..100) of <paramref name="values"/>; null when empty.</summary>
    public static double? Percentile(IEnumerable<double> values, double p)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0)
            return null;

        var rank = (int)Math.Ceiling(p / 100.0 * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }

    // ── Integrity read-back ───────────────────────────────────────────────────

    /// <summary>
    /// Reads <c>integrity.passed</c>, the names of the checks with <c>passed: false</c> and
    /// <c>integrity.extraction.answerTrimmed</c> out of a BalanceJson. Names only, never a figure.
    /// </summary>
    public static (bool? Passed, IReadOnlyList<string> FailedChecks, bool AnswerTrimmed) ReadIntegrity(string? balanceJson)
    {
        if (string.IsNullOrWhiteSpace(balanceJson))
            return (null, [], false);

        try
        {
            using var doc = JsonDocument.Parse(balanceJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("integrity", out var integrity)
                || integrity.ValueKind != JsonValueKind.Object)
            {
                return (null, [], false);
            }

            bool? passed = integrity.TryGetProperty("passed", out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? p.GetBoolean()
                : null;

            var failed = new List<string>();
            if (integrity.TryGetProperty("checks", out var checks) && checks.ValueKind == JsonValueKind.Array)
            {
                foreach (var check in checks.EnumerateArray())
                {
                    if (check.ValueKind == JsonValueKind.Object
                        && check.TryGetProperty("passed", out var cp) && cp.ValueKind == JsonValueKind.False
                        && check.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                    {
                        failed.Add(name.GetString()!);
                    }
                }
            }

            var trimmed = integrity.TryGetProperty("extraction", out var extraction)
                && extraction.ValueKind == JsonValueKind.Object
                && extraction.TryGetProperty("answerTrimmed", out var t)
                && t.ValueKind == JsonValueKind.True;

            return (passed, failed, trimmed);
        }
        catch (JsonException)
        {
            return (null, [], false);
        }
    }

    // ── Key figures ───────────────────────────────────────────────────────────

    /// <summary>
    /// The balance figures a prompt change must not move. A key ending in <c>_items</c> covers every
    /// entry of that array (<c>key[i].amount</c>); its status is the worst of them.
    /// </summary>
    public static readonly IReadOnlyList<string> KeyFields =
    [
        "balance_check.assets",
        "balance_check.liabilities",
        "balance_check.equity",
        "balance_check.net_profit",
        "balance_check.current_liabilities",
        "balance_check.current_assets_items",
        "balance_check.current_liabilities_items",
        "balance_check.non_current_liabilities_items",
    ];

    /// <summary>
    /// The status of each <see cref="KeyFields"/> entry in one comparison: the worst <see cref="FieldDeltaKind"/>
    /// among its paths (Identical &lt; WithinTolerance &lt; Different &lt; NullFlip), and how many paths it covers.
    /// A key absent on both sides is Identical with 0 paths.
    /// </summary>
    public static IReadOnlyList<KeyFieldStatus> SummarizeKeyFields(RunComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);

        var result = new List<KeyFieldStatus>(KeyFields.Count);
        foreach (var key in KeyFields)
        {
            var paths = comparison.Fields
                .Where(f => f.Path == key || f.Path.StartsWith(key + "[", StringComparison.Ordinal))
                .ToList();

            var worst = paths.Count == 0 ? FieldDeltaKind.Identical : paths.Max(f => f.Kind);
            result.Add(new KeyFieldStatus(key, worst, paths.Count));
        }

        return result;
    }

    /// <summary>One line per run for the test output: how the call went, never a figure.</summary>
    public static string RunSummaryLine(ComparisonRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var outcome = run.Succeeded
            ? "ok"
            : $"FAILED {run.FailureType}{(run.FailureCode is { } code ? $" [{code}]" : string.Empty)}";

        var trimmed = run.AnswerTrimmed || run.PreambleChars is > 0 || run.TrailerChars is > 0
            ? $"yes (preamble {run.PreambleChars?.ToString(Inv) ?? "?"} chars, trailer {run.TrailerChars?.ToString(Inv) ?? "?"} chars)"
            : "no";

        var integrity = run.IntegrityPassed switch
        {
            true  => "passed",
            false => $"FAILED: {string.Join(", ", run.FailedChecks ?? [])}",
            null  => "—"
        };

        var alerts = run.AlertCodes is null ? "—" : $"[{string.Join(", ", run.AlertCodes)}]";

        return $"{run.DocAlias} | {run.Variant.Label} | run {Int(run.RunIndex)} | {outcome} | " +
               $"out {Int(run.OutputTokens)} tok (max pass {run.MaxPassOutputTokens?.ToString(Inv) ?? "—"}) | " +
               $"{Seconds(run.LatencyMs)} s | escalated {(run.Escalated ? "YES" : "no")} | trimmed {trimmed} | " +
               $"integrity {integrity} | confidence {run.Confidence?.ToString("0.00", Inv) ?? "—"} | alerts {alerts}";
    }

    /// <summary>
    /// The <c>code</c> of every alert in a result's AlertsJson, in order. A code is kept only when it reads as a code
    /// (upper-case letters, digits, underscores, ≤ 60 chars); anything else becomes <see cref="NonCodePlaceholder"/>,
    /// so free text the model put in the field never reaches the output. Titles and descriptions are never read.
    /// Null when the JSON is absent or unreadable; empty when the model raised no alert.
    /// </summary>
    public static IReadOnlyList<string>? ReadAlertCodes(string? alertsJson)
    {
        if (string.IsNullOrWhiteSpace(alertsJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(alertsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            var codes = new List<string>();
            foreach (var alert in doc.RootElement.EnumerateArray())
            {
                var code = alert.ValueKind == JsonValueKind.Object
                           && alert.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String
                    ? c.GetString()
                    : null;
                codes.Add(code is not null && AlertCodeShape.IsMatch(code) ? code : NonCodePlaceholder);
            }

            return codes;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>What <see cref="ReadAlertCodes"/> prints in place of a code that does not read as one.</summary>
    public const string NonCodePlaceholder = "<non-code>";

    private static readonly System.Text.RegularExpressions.Regex AlertCodeShape =
        new("^[A-Z0-9_ÑÁÉÍÓÚÜ]{1,60}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>One line per comparison for the test output: the key figures' status, never a value.</summary>
    public static string KeyFieldLine(RunComparison comparison)
    {
        var parts = SummarizeKeyFields(comparison).Select(s =>
        {
            var name = s.Key["balance_check.".Length..];
            return s.Paths == 0 ? $"{name}=absent" : $"{name}={s.Kind}";
        });

        return $"{comparison.DocAlias} | {comparison.GroupLabel} vs baseline | run {Int(comparison.RunIndex)} | " +
               string.Join(", ", parts) + $" | band {comparison.BandChanged switch { true => "CHANGED", false => "same", null => "—" }}";
    }

    // ── Snapshot (a run's results, reused as the next run's baseline) ────────

    private static readonly JsonSerializerOptions SnapshotJson = new() { WriteIndented = true };

    /// <summary>Serialises this run's documents and runs to the <c>…-runs.json</c> format.</summary>
    public static string BuildSnapshot(string promptVersion, IReadOnlyList<ComparisonDocument> documents, IReadOnlyList<ComparisonRun> runs)
    {
        var snapshotRuns = runs.Select(r => new SnapshotRun
        {
            DocAlias            = r.DocAlias,
            VariantLabel        = r.Variant.Label,
            ModelId             = r.Variant.Spec.Id,
            Effort              = r.Variant.Effort,
            RunIndex            = r.RunIndex,
            Succeeded           = r.Succeeded,
            FailureType         = r.FailureType,
            FailureCode         = r.FailureCode,
            Confidence          = r.Confidence,
            Method              = r.Method,
            InputTokens         = r.InputTokens,
            OutputTokens        = r.OutputTokens,
            CostUsd             = r.CostUsd,
            LatencyMs           = r.LatencyMs,
            Fields              = r.Fields is null ? null : new Dictionary<string, string>(r.Fields, StringComparer.Ordinal),
            Ratios              = r.Ratios,
            ScoreRatios         = r.ScoreRatios,
            Score               = r.Score,
            Level               = r.Level,
            Escalated           = r.Escalated,
            MaxPassOutputTokens = r.MaxPassOutputTokens,
            AnswerTrimmed       = r.AnswerTrimmed,
            PreambleChars       = r.PreambleChars,
            TrailerChars        = r.TrailerChars,
            IntegrityPassed     = r.IntegrityPassed,
            FailedChecks        = r.FailedChecks?.ToList(),
            AlertCodes          = r.AlertCodes?.ToList(),
        }).ToList();

        return JsonSerializer.Serialize(new ComparisonSnapshot(promptVersion, documents, snapshotRuns), SnapshotJson);
    }

    /// <summary>
    /// Turns a previous run's snapshot into a baseline variant (<c>antes {prompt} · {label}</c>) and its runs.
    /// Throws <see cref="InvalidOperationException"/> — before anything is spent — when the snapshot's documents
    /// are not exactly this run's (same aliases, same SHA-256), or it holds more than one variant.
    /// </summary>
    public static (ComparisonVariant Variant, IReadOnlyList<ComparisonRun> Runs) LoadBaseline(
        string snapshotJson, IReadOnlyList<ComparisonDocument> currentDocuments)
    {
        ArgumentNullException.ThrowIfNull(currentDocuments);

        var snapshot = JsonSerializer.Deserialize<ComparisonSnapshot>(snapshotJson)
            ?? throw new InvalidOperationException("The baseline snapshot is empty.");

        var expected = currentDocuments.ToDictionary(d => d.Alias, d => d.Sha256, StringComparer.Ordinal);
        var stored   = snapshot.Documents.ToDictionary(d => d.Alias, d => d.Sha256, StringComparer.Ordinal);
        if (expected.Count != stored.Count
            || expected.Any(kv => !stored.TryGetValue(kv.Key, out var sha) || !string.Equals(sha, kv.Value, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "The baseline snapshot was taken over different PDFs (aliases or SHA-256 differ). Use the same folder, unchanged.");
        }

        var labels = snapshot.Runs.Select(r => r.VariantLabel).Distinct(StringComparer.Ordinal).ToList();
        if (labels.Count != 1)
            throw new InvalidOperationException($"The baseline snapshot must hold exactly one variant; it holds {labels.Count}.");

        var first = snapshot.Runs[0];
        var spec  = ExtractionModelCatalog.Find(first.ModelId)
            ?? throw new InvalidOperationException($"The baseline model '{first.ModelId}' is not in ExtractionModelCatalog.");
        var variant = new ComparisonVariant($"antes {snapshot.PromptVersion} · {labels[0]}", spec, first.Effort);

        var runs = snapshot.Runs.Select(r => new ComparisonRun
        {
            DocAlias            = r.DocAlias,
            Variant             = variant,
            RunIndex            = r.RunIndex,
            Succeeded           = r.Succeeded,
            FailureType         = r.FailureType,
            FailureCode         = r.FailureCode,
            Confidence          = r.Confidence,
            Method              = r.Method,
            InputTokens         = r.InputTokens,
            OutputTokens        = r.OutputTokens,
            CostUsd             = r.CostUsd,
            LatencyMs           = r.LatencyMs,
            Fields              = r.Fields,
            Ratios              = r.Ratios,
            ScoreRatios         = r.ScoreRatios,
            Score               = r.Score,
            Level               = r.Level,
            Escalated           = r.Escalated,
            MaxPassOutputTokens = r.MaxPassOutputTokens,
            AnswerTrimmed       = r.AnswerTrimmed,
            PreambleChars       = r.PreambleChars,
            TrailerChars        = r.TrailerChars,
            IntegrityPassed     = r.IntegrityPassed,
            FailedChecks        = r.FailedChecks,
            AlertCodes          = r.AlertCodes,
        }).ToList();

        return (variant, runs);
    }

    // ── Markdown ──────────────────────────────────────────────────────────────

    /// <summary>The Markdown report: warning, provenance, method, summary per variant, then per-document detail.</summary>
    public static string BuildMarkdown(
        ComparisonHeader                 header,
        IReadOnlyList<ComparisonDocument> documents,
        IReadOnlyList<ComparisonVariant>  variants,
        IReadOnlyList<ComparisonRun>      runs,
        IReadOnlyList<RunComparison>      comparisons)
    {
        var sb = new StringBuilder();
        var baseline = variants[0];

        sb.AppendLine($"> **{Warning}**");
        sb.AppendLine();
        sb.AppendLine("# Comparación de modelos de extracción de balances");
        sb.AppendLine();
        sb.AppendLine($"- Generado (UTC): {header.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", Inv)}");
        sb.AppendLine($"- Prompt de extracción: {header.PromptVersion}");
        sb.AppendLine($"- Tipo de documento: {header.DocumentType}; ejercicio: {(header.FiscalYear?.ToString(Inv) ?? "no informado")}");
        sb.AppendLine($"- Balances: {documents.Count} (doc-01…; el mapa a archivos está en `{header.MapFileName}`)");
        sb.AppendLine($"- Variantes: {string.Join(", ", variants.Select(v => $"`{v.Label}`"))}. **Línea base: `{baseline.Label}`**");
        sb.AppendLine($"- Corridas por variante: {header.RunsPerVariant}; llamadas a AnalyzeDocumentAsync: {runs.Count}");
        sb.AppendLine($"- Cota superior de costo estimada antes de correr: USD {Money(header.UpperBoundUsd)} (sin reintentos)");
        sb.AppendLine($"- Costo real estimado (tokens × precio): USD {Money(runs.Sum(r => r.CostUsd ?? 0))}");
        sb.AppendLine($"- Detalle con valores: `{header.CsvFileName}` (números en InvariantCulture: punto decimal, coma separadora, RFC 4180)");
        sb.AppendLine();
        sb.AppendLine("## Método");
        sb.AppendLine();
        sb.AppendLine("- Cada corrida es el `AnalyzeDocumentAsync` real (pasada documento + fallback por imágenes con la misma regla de confianza).");
        sb.AppendLine("- Ratios: `RatioCalculationService` sobre el BalanceJson extraído (igual que `AnalysisScoringStage.ComputeRatios`).");
        sb.AppendLine("- Score: `ScoringService` con `ScoringDefaults` (sin configuración de tenant), **BCRA fijo** = sin deuda (componente 0, no excluido) y **macro fijo** = inflación 5,0 (componente 30); sin sector, LUFE ni CASFOG. Las entradas son idénticas para todas las variantes: toda diferencia de score viene de la extracción. No es el score real de ningún cliente.");
        sb.AppendLine($"- Campos: hojas numéricas/booleanas del BalanceJson (sin `integrity.*`, que es derivado). Idéntico = mismo texto; dentro de tolerancia = |a−b| ≤ {Tolerance.ToString("P1", Inv)} × max(|a|,|b|); null-flip = valor en un lado y null/ausente en el otro.");
        sb.AppendLine("- Cada corrida i de una variante se compara con la corrida i de la línea base. Con más de una corrida, la línea base contra su propia corrida 1 da el **piso de ruido**.");
        sb.AppendLine();

        AppendSummary(sb, variants, runs, comparisons);
        AppendDetail(sb, documents, variants, runs, comparisons);

        return sb.ToString();
    }

    private static void AppendSummary(
        StringBuilder sb, IReadOnlyList<ComparisonVariant> variants,
        IReadOnlyList<ComparisonRun> runs, IReadOnlyList<RunComparison> comparisons)
    {
        sb.AppendLine("## Resumen");
        sb.AppendLine();
        sb.AppendLine("### Por variante (corridas propias)");
        sb.AppendLine();
        sb.AppendLine("| Variante | Corridas | Fallas | Tipos de falla | Fallback imágenes | USD/balance media | USD/balance máx | Latencia p50 s | Latencia p95 s |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|");

        foreach (var variant in variants)
        {
            var own       = runs.Where(r => r.Variant.Label == variant.Label).ToList();
            var ok        = own.Where(r => r.Succeeded).ToList();
            var failures  = own.Where(r => !r.Succeeded).ToList();
            var failTypes = failures.Count == 0
                ? "—"
                : string.Join("; ", failures.GroupBy(f => f.FailureType ?? "?").Select(g => $"{g.Key} ×{g.Count()}"));
            var fallback  = ok.Count == 0 ? "—" : Pct(ok.Count(r => r.Method == "image-fallback"), ok.Count);
            var perBalance = own.GroupBy(r => (r.DocAlias, r.RunIndex)).Select(g => (double)g.Sum(r => r.CostUsd ?? 0)).ToList();
            var p50       = Percentile(own.Select(r => r.LatencyMs), 50);
            var p95       = Percentile(own.Select(r => r.LatencyMs), 95);
            var flag      = p95 > SyncPathLatencyWarningMs ? " ⚠" : string.Empty;

            sb.AppendLine($"| `{variant.Label}` | {own.Count} | {failures.Count} | {failTypes} | {fallback} | " +
                          $"{Money(perBalance.Count == 0 ? 0 : (decimal)perBalance.Average())} | {Money(perBalance.Count == 0 ? 0 : (decimal)perBalance.Max())} | " +
                          $"{Seconds(p50)} | {Seconds(p95)}{flag} |");
        }

        sb.AppendLine();
        sb.AppendLine($"⚠ = latencia p95 sobre {SyncPathLatencyWarningMs / 1000:0} s: riesgo para el Portal, que llama al análisis en forma sincrónica y el front-end de App Service corta a ~230 s.");
        sb.AppendLine();
        sb.AppendLine("### Contra la línea base");
        sb.AppendLine();
        sb.AppendLine("| Grupo | Comparaciones | Campos | Idénticos | ≤ tolerancia (incl. idénticos) | Distintos | Null-flip | ΔScore máx | ΔScore p95 | ΔRatios máx | ΔRatios p95 | Cambios de banda |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");

        foreach (var group in comparisons.GroupBy(c => c.GroupLabel))
        {
            var fields   = group.SelectMany(c => c.Fields).ToList();
            var ident    = fields.Count(f => f.Kind == FieldDeltaKind.Identical);
            var within   = ident + fields.Count(f => f.Kind == FieldDeltaKind.WithinTolerance);
            var diff     = fields.Count(f => f.Kind == FieldDeltaKind.Different);
            var flips    = fields.Count(f => f.Kind == FieldDeltaKind.NullFlip);
            var score    = group.Where(c => c.ScoreDelta.HasValue).Select(c => (double)Math.Abs(c.ScoreDelta!.Value)).ToList();
            var ratios   = group.Where(c => c.ScoreRatiosDelta.HasValue).Select(c => (double)Math.Abs(c.ScoreRatiosDelta!.Value)).ToList();
            var bands    = group.Count(c => c.BandChanged == true);

            sb.AppendLine($"| `{group.Key}` | {group.Count()} | {fields.Count} | {Pct(ident, fields.Count)} | {Pct(within, fields.Count)} | " +
                          $"{Pct(diff, fields.Count)} | {Pct(flips, fields.Count)} | {Num(score.Count == 0 ? null : score.Max())} | " +
                          $"{Num(Percentile(score, 95))} | {Num(ratios.Count == 0 ? null : ratios.Max())} | {Num(Percentile(ratios, 95))} | {bands} |");
        }

        if (!comparisons.Any())
            sb.AppendLine("| — | 0 | — | — | — | — | — | — | — | — | — | — |");

        sb.AppendLine();
        sb.AppendLine("ΔScore = score compuesto (con piso crítico) bajo BCRA/macro fijos; ΔRatios = componente de ratios. Valores absolutos, puntos sobre 100.");
        sb.AppendLine();
        sb.AppendLine("### Peores campos por grupo (sólo ruta, sin valores)");
        sb.AppendLine();

        foreach (var group in comparisons.GroupBy(c => c.GroupLabel))
        {
            var worst = group.SelectMany(c => c.Fields)
                .Where(f => f.Kind is FieldDeltaKind.Different or FieldDeltaKind.NullFlip)
                .GroupBy(f => f.Path, StringComparer.Ordinal)
                .Select(g => (
                    Path:   g.Key,
                    Flips:  g.Count(f => f.Kind == FieldDeltaKind.NullFlip),
                    Diffs:  g.Count(f => f.Kind == FieldDeltaKind.Different),
                    MaxRel: g.Max(f => f.RelativeDelta ?? double.PositiveInfinity)))
                .OrderByDescending(w => w.Flips + w.Diffs)
                .ThenByDescending(w => w.MaxRel)
                .ThenBy(w => w.Path, StringComparer.Ordinal)
                .Take(10)
                .ToList();

            sb.AppendLine($"**`{group.Key}`**");
            sb.AppendLine();
            if (worst.Count == 0)
            {
                sb.AppendLine("Ningún campo fuera de tolerancia.");
            }
            else
            {
                sb.AppendLine("| Campo | Distintos | Null-flip | Δ relativo máx |");
                sb.AppendLine("|---|---|---|---|");
                foreach (var w in worst)
                    sb.AppendLine($"| `{w.Path}` | {w.Diffs} | {w.Flips} | {(double.IsPositiveInfinity(w.MaxRel) ? "n/a" : w.MaxRel.ToString("P2", Inv))} |");
            }

            sb.AppendLine();
        }
    }

    private static void AppendDetail(
        StringBuilder sb, IReadOnlyList<ComparisonDocument> documents, IReadOnlyList<ComparisonVariant> variants,
        IReadOnlyList<ComparisonRun> runs, IReadOnlyList<RunComparison> comparisons)
    {
        sb.AppendLine("## Detalle por balance");
        sb.AppendLine();

        foreach (var doc in documents)
        {
            sb.AppendLine($"### {doc.Alias} ({doc.Pages.ToString(Inv)} páginas, {(doc.Bytes / 1024.0).ToString("0", Inv)} KB)");
            sb.AppendLine();
            sb.AppendLine("| Variante | Corrida | Resultado | Confianza | Método | Tokens in/out | Salida máx. por pasada | Escalada | Texto fuera del JSON | Integridad | USD | Latencia s | Score ratios | Score | Banda |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");

            foreach (var run in runs.Where(r => r.DocAlias == doc.Alias)
                                    .OrderBy(r => IndexOf(variants, r.Variant))
                                    .ThenBy(r => r.RunIndex))
            {
                var outcome = run.Succeeded ? "ok" : $"falla: {run.FailureType}{(run.FailureCode is { } code ? $" [{code}]" : string.Empty)}";
                var trimmed = run.AnswerTrimmed || run.PreambleChars is > 0 || run.TrailerChars is > 0
                    ? $"sí ({run.PreambleChars?.ToString(Inv) ?? "?"} antes / {run.TrailerChars?.ToString(Inv) ?? "?"} después)"
                    : "no";
                var integrity = run.IntegrityPassed switch
                {
                    true  => "ok",
                    false => $"falla: {string.Join(", ", run.FailedChecks ?? [])}",
                    null  => "—"
                };
                sb.AppendLine($"| `{run.Variant.Label}` | {run.RunIndex} | {outcome} | {Num((double?)run.Confidence)} | {run.Method ?? "—"} | " +
                              $"{run.InputTokens.ToString(Inv)}/{run.OutputTokens.ToString(Inv)} | {run.MaxPassOutputTokens?.ToString(Inv) ?? "—"} | " +
                              $"{(run.Escalated ? "sí" : "no")} | {trimmed} | {integrity} | {Money(run.CostUsd ?? 0)} | {Seconds(run.LatencyMs)} | " +
                              $"{Num((double?)run.ScoreRatios)} | {Num((double?)run.Score)} | {run.Level ?? "—"} |");
            }

            sb.AppendLine();

            var docComparisons = comparisons.Where(c => c.DocAlias == doc.Alias).ToList();
            if (docComparisons.Count > 0)
            {
                sb.AppendLine("| Grupo | Corrida | Idénticos | ≤ tol. | Distintos | Null-flip | Ratios con Δ > tol. | ΔScore ratios | ΔScore | Banda |");
                sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
                foreach (var c in docComparisons)
                {
                    var ratioMoves = c.Ratios.Where(r => r.NullFlip || (r.Relative ?? 0) > Tolerance).Select(r => r.Name).ToList();
                    var band = c.BandChanged switch
                    {
                        true  => $"{c.BaselineLevel} → {c.CandidateLevel}",
                        false => "=",
                        null  => "—"
                    };
                    sb.AppendLine($"| `{c.GroupLabel}` | {c.RunIndex} | {c.Fields.Count(f => f.Kind == FieldDeltaKind.Identical)} | " +
                                  $"{c.Fields.Count(f => f.Kind == FieldDeltaKind.WithinTolerance)} | {c.Fields.Count(f => f.Kind == FieldDeltaKind.Different)} | " +
                                  $"{c.Fields.Count(f => f.Kind == FieldDeltaKind.NullFlip)} | {(ratioMoves.Count == 0 ? "—" : string.Join(", ", ratioMoves))} | " +
                                  $"{Signed(c.ScoreRatiosDelta)} | {Signed(c.ScoreDelta)} | {band} |");
                }

                sb.AppendLine();
            }
        }
    }

    // ── CSV ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// One row per run metric, per compared field, per compared ratio and per compared score. Opens with
    /// the warning row. InvariantCulture numbers, comma-separated, RFC 4180 quoting.
    /// </summary>
    public static string BuildCsv(IReadOnlyList<ComparisonRun> runs, IReadOnlyList<RunComparison> comparisons)
    {
        var sb = new StringBuilder();
        Row(sb, "# " + Warning);
        Row(sb, "doc", "group", "run", "kind", "item", "baseline", "candidate", "abs_delta", "rel_delta", "class");

        foreach (var r in runs)
        {
            void Metric(string item, string? value) =>
                Row(sb, r.DocAlias, r.Variant.Label, Int(r.RunIndex), "run", item, null, value, null, null, null);

            Metric("succeeded", r.Succeeded ? "true" : "false");
            Metric("failure_type", r.FailureType);
            Metric("failure_detail", r.FailureDetail);
            Metric("confidence", Dec(r.Confidence));
            Metric("method", r.Method);
            Metric("effort", r.Variant.Effort);
            Metric("input_tokens", Int(r.InputTokens));
            Metric("output_tokens", Int(r.OutputTokens));
            Metric("cost_usd", Dec(r.CostUsd));
            Metric("latency_ms", Dbl(Math.Round(r.LatencyMs)));
            Metric("score_ratios", Dec(r.ScoreRatios));
            Metric("score", Dec(r.Score));
            Metric("level", r.Level);
            Metric("failure_code", r.FailureCode);
            Metric("escalated", r.Escalated ? "true" : "false");
            Metric("max_pass_output_tokens", r.MaxPassOutputTokens?.ToString(Inv));
            Metric("answer_trimmed", r.AnswerTrimmed ? "true" : "false");
            Metric("preamble_chars", r.PreambleChars?.ToString(Inv));
            Metric("trailer_chars", r.TrailerChars?.ToString(Inv));
            Metric("integrity_passed", r.IntegrityPassed switch { true => "true", false => "false", null => null });
            Metric("integrity_failed_checks", r.FailedChecks is { Count: > 0 } f ? string.Join(';', f) : null);
        }

        foreach (var c in comparisons)
        {
            foreach (var f in c.Fields)
                Row(sb, c.DocAlias, c.GroupLabel, Int(c.RunIndex), "field", f.Path, f.Baseline, f.Candidate,
                    null, f.RelativeDelta is { } rd ? Dbl(rd) : null, f.Kind.ToString());

            foreach (var r in c.Ratios)
                Row(sb, c.DocAlias, c.GroupLabel, Int(c.RunIndex), "ratio", r.Name,
                    r.Baseline is { } b ? Dbl(b) : null, r.Candidate is { } cv ? Dbl(cv) : null,
                    r.Absolute is { } a ? Dbl(a) : null, r.Relative is { } rel ? Dbl(rel) : null,
                    r.NullFlip ? "NullFlip" : null);

            Row(sb, c.DocAlias, c.GroupLabel, Int(c.RunIndex), "score", "score_ratios_delta", null, null, Dec(c.ScoreRatiosDelta), null, null);
            Row(sb, c.DocAlias, c.GroupLabel, Int(c.RunIndex), "score", "score_delta", null, null, Dec(c.ScoreDelta), null, null);
            Row(sb, c.DocAlias, c.GroupLabel, Int(c.RunIndex), "score", "band", c.BaselineLevel, c.CandidateLevel, null, null,
                c.BandChanged switch { true => "Changed", false => "Same", null => null });
        }

        return sb.ToString();
    }

    /// <summary>The alias ↔ file map, kept apart from the report. Opens with the warning row.</summary>
    public static string BuildMap(IReadOnlyList<ComparisonDocument> documents)
    {
        var sb = new StringBuilder();
        Row(sb, "# " + Warning);
        Row(sb, "doc", "file_name", "sha256", "bytes", "pages");
        foreach (var d in documents)
            Row(sb, d.Alias, d.FileName, d.Sha256, d.Bytes.ToString(Inv), Int(d.Pages));
        return sb.ToString();
    }

    /// <summary>RFC 4180: quote a field holding a comma, a quote, CR or LF; double inner quotes.</summary>
    public static string CsvEscape(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }

    // ── Formatting ────────────────────────────────────────────────────────────

    private static void Row(StringBuilder sb, params string?[] fields)
    {
        sb.Append(string.Join(',', fields.Select(CsvEscape)));
        sb.Append("\r\n");
    }

    private static bool IsMissing(string? value) =>
        value is null || string.Equals(value, "null", StringComparison.Ordinal);

    private static int IndexOf(IReadOnlyList<ComparisonVariant> variants, ComparisonVariant variant)
    {
        for (var i = 0; i < variants.Count; i++)
        {
            if (variants[i].Label == variant.Label)
                return i;
        }

        return int.MaxValue;
    }

    private static string Int(int value) => value.ToString(Inv);

    private static string? Dec(decimal? value) => value?.ToString(Inv);

    private static string Dbl(double value) => value.ToString("R", Inv);

    private static string Pct(int part, int whole) =>
        whole == 0 ? "—" : ((double)part / whole).ToString("P1", Inv);

    private static string Num(double? value) => value is { } v ? v.ToString("0.##", Inv) : "—";

    private static string Signed(decimal? value) => value is { } v ? v.ToString("+0.##;-0.##;0", Inv) : "—";

    private static string Money(decimal value) => value.ToString("0.0000", Inv);

    private static string Seconds(double? ms) => ms is { } v ? (v / 1000).ToString("0.0", Inv) : "—";
}
