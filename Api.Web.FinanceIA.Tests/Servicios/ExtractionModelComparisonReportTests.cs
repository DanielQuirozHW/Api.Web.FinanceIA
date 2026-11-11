// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-01

using Api.Web.FinanceIA.Models.Financia;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// The pure half of the model-comparison harness, on synthetic data: variant parsing, the field
/// classification, percentiles, the cost bound and the output files. No PDF, no API call, no figure
/// from a real balance.
/// </summary>
public sealed class ExtractionModelComparisonReportTests
{
    private const string Defaults = "claude-sonnet-4-6,claude-sonnet-5-5";

    // ── Variants ──────────────────────────────────────────────────────────────

    [Fact]
    public void ParseVariants_defaults_to_sonnet46_then_sonnet55_with_the_first_as_baseline()
    {
        var variants = ExtractionModelComparisonReport.ParseVariants(null, null, Defaults);

        Assert.Equal(["claude-sonnet-4-6", "claude-sonnet-5-5"], variants.Select(v => v.Label));
        Assert.All(variants, v => Assert.Null(v.Effort));
    }

    [Fact]
    public void ParseVariants_expands_efforts_only_for_models_that_support_them()
    {
        var variants = ExtractionModelComparisonReport.ParseVariants(
            "claude-sonnet-4-6, claude-sonnet-5-5", "High, medium", Defaults);

        Assert.Equal(
            ["claude-sonnet-4-6", "claude-sonnet-5-5@high", "claude-sonnet-5-5@medium"],
            variants.Select(v => v.Label));
        Assert.Null(variants[0].Effort);
        Assert.Equal("high", variants[1].Effort);
    }

    [Theory]
    [InlineData("claude-sonnet-4-6,claude-made-up", null)]
    [InlineData("claude-sonnet-5-5", "turbo")]
    [InlineData("claude-sonnet-4-6,claude-sonnet-4-6", null)]
    public void ParseVariants_rejects_unknown_models_invalid_efforts_and_duplicates(string models, string? efforts)
    {
        Assert.Throws<ArgumentException>(() => ExtractionModelComparisonReport.ParseVariants(models, efforts, Defaults));
    }

    // ── Field classification ──────────────────────────────────────────────────

    [Theory]
    [InlineData("100", "100", FieldDeltaKind.Identical)]
    [InlineData("100", "100.0", FieldDeltaKind.Identical)]
    [InlineData("1000", "1004", FieldDeltaKind.WithinTolerance)]
    [InlineData("1000", "1006", FieldDeltaKind.Different)]
    [InlineData("1000", null, FieldDeltaKind.NullFlip)]
    [InlineData("null", "5", FieldDeltaKind.NullFlip)]
    [InlineData(null, "null", FieldDeltaKind.Identical)]
    [InlineData("true", "false", FieldDeltaKind.Different)]
    [InlineData("0", "0", FieldDeltaKind.Identical)]
    public void ClassifyField_uses_a_half_percent_tolerance_and_flags_null_flips(string? baseline, string? candidate, FieldDeltaKind expected)
    {
        Assert.Equal(expected, ExtractionModelComparisonReport.ClassifyField("a.b", baseline, candidate).Kind);
    }

    [Fact]
    public void Percentile_is_nearest_rank()
    {
        double[] values = [5, 1, 4, 2, 3, 10, 9, 8, 7, 6];

        Assert.Equal(5, ExtractionModelComparisonReport.Percentile(values, 50));
        Assert.Equal(10, ExtractionModelComparisonReport.Percentile(values, 95));
        Assert.Equal(1, ExtractionModelComparisonReport.Percentile(values, 0));
        Assert.Null(ExtractionModelComparisonReport.Percentile([], 95));
    }

    // ── Cost bound ────────────────────────────────────────────────────────────

    [Fact]
    public void UpperBound_grows_with_pages_and_counts_both_passes_at_full_max_tokens()
    {
        var one = ExtractionModelComparisonReport.UpperBoundUsdPerRun(ExtractionModelCatalog.Sonnet46, 1, 30_000);
        var ten = ExtractionModelComparisonReport.UpperBoundUsdPerRun(ExtractionModelCatalog.Sonnet46, 10, 30_000);

        Assert.True(ten > one);
        // Output alone: 2 passes × 8000 tokens × $15/M = 0.24.
        Assert.True(one > 0.24m);
        // Sonnet 5.5: 2 × 32000 × $10/M = 0.64 of output.
        Assert.True(ExtractionModelComparisonReport.UpperBoundUsdPerRun(ExtractionModelCatalog.Sonnet55, 1, 30_000) > 0.64m);
    }

    // ── Comparison and outputs ────────────────────────────────────────────────

    [Fact]
    public void CompareAll_pairs_runs_by_index_adds_the_noise_floor_and_skips_failures()
    {
        var variants = ExtractionModelComparisonReport.ParseVariants(null, null, Defaults);
        var runs = new List<ComparisonRun>
        {
            Run(variants[0], 1, "1000", 1.5, 40m, "Medium"),
            Run(variants[0], 2, "1001", 1.5, 40m, "Medium"),
            Run(variants[1], 1, "1100", 1.6, 30m, "Low"),
            Run(variants[1], 2, null,   null, null, null, succeeded: false),
        };

        var comparisons = ExtractionModelComparisonReport.CompareAll(variants, runs);

        Assert.Equal(2, comparisons.Count);

        var floor = Assert.Single(comparisons, c => c.GroupLabel.EndsWith(ExtractionModelComparisonReport.NoiseFloorSuffix, StringComparison.Ordinal));
        Assert.Equal(FieldDeltaKind.WithinTolerance, floor.Fields.Single(f => f.Path == "assets").Kind);

        var candidate = Assert.Single(comparisons, c => c.GroupLabel == "claude-sonnet-5-5");
        Assert.Equal(FieldDeltaKind.Different, candidate.Fields.Single(f => f.Path == "assets").Kind);
        Assert.Equal(-10m, candidate.ScoreDelta);
        Assert.True(candidate.BandChanged);
        var ratio = candidate.Ratios.Single(r => r.Name == "current_ratio");
        Assert.Equal(0.1, ratio.Absolute!.Value, 6);
    }

    [Fact]
    public void Outputs_open_with_the_warning_and_the_markdown_carries_aliases_not_file_names()
    {
        var variants  = ExtractionModelComparisonReport.ParseVariants(null, null, Defaults);
        var documents = new List<ComparisonDocument> { new("doc-01", "Cliente, S.A. 2025.pdf", "abc", 2048, 3) };
        var runs = new List<ComparisonRun>
        {
            Run(variants[0], 1, "1000", 1.5, 40m, "Medium"),
            Run(variants[1], 1, "1000", 1.5, 40m, "Medium"),
        };
        var comparisons = ExtractionModelComparisonReport.CompareAll(variants, runs);
        var header = new ComparisonHeader(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), "2.18", "Balance", 2025, 1, 1.23m, "x.csv", "x-mapa.csv");

        var md  = ExtractionModelComparisonReport.BuildMarkdown(header, documents, variants, runs, comparisons);
        var csv = ExtractionModelComparisonReport.BuildCsv(runs, comparisons);
        var map = ExtractionModelComparisonReport.BuildMap(documents);

        Assert.StartsWith($"> **{ExtractionModelComparisonReport.Warning}**", md, StringComparison.Ordinal);
        Assert.Contains("doc-01", md, StringComparison.Ordinal);
        Assert.DoesNotContain("Cliente", md, StringComparison.Ordinal);

        Assert.StartsWith("# " + ExtractionModelComparisonReport.Warning, csv, StringComparison.Ordinal);
        Assert.Contains("doc-01,claude-sonnet-5-5,1,field,assets,1000,1000,,0,Identical", csv, StringComparison.Ordinal);

        Assert.StartsWith("# " + ExtractionModelComparisonReport.Warning, map, StringComparison.Ordinal);
        Assert.Contains("doc-01,\"Cliente, S.A. 2025.pdf\",abc,2048,3", map, StringComparison.Ordinal);
    }

    [Fact]
    public void Csv_numbers_are_invariant_whatever_the_current_culture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("es-AR");
            var variants = ExtractionModelComparisonReport.ParseVariants(null, null, Defaults);
            var csv = ExtractionModelComparisonReport.BuildCsv([Run(variants[0], 1, "1000", 1.25, 40.5m, "Medium")], []);

            Assert.Contains(",score,,40.5,,,", csv, StringComparison.Ordinal);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData(null, "")]
    public void CsvEscape_follows_rfc4180(string? value, string expected)
    {
        Assert.Equal(expected, ExtractionModelComparisonReport.CsvEscape(value));
    }

    // ── Before/after a commit: integrity, key figures, snapshot ───────────────

    [Fact]
    public void ReadIntegrity_returns_the_failed_check_names_and_the_trim_flag_only()
    {
        const string json =
            """{"balance_check":{"assets":1000},"integrity":{"passed":false,"checks":[{"name":"assets_components","passed":true},{"name":"accounting_equation","passed":false,"difference":12}],"extraction":{"answerTrimmed":true}}}""";

        var (passed, failed, trimmed) = ExtractionModelComparisonReport.ReadIntegrity(json);

        Assert.False(passed);
        Assert.Equal(["accounting_equation"], failed);
        Assert.True(trimmed);

        Assert.Equal((null, 0, false), Shape(ExtractionModelComparisonReport.ReadIntegrity("""{"integrity":null}""")));
        Assert.Equal((null, 0, false), Shape(ExtractionModelComparisonReport.ReadIntegrity("not json")));
        Assert.Equal((true, 0, false), Shape(ExtractionModelComparisonReport.ReadIntegrity("""{"integrity":{"passed":true,"checks":[]}}""")));

        static (bool?, int, bool) Shape((bool? P, IReadOnlyList<string> F, bool T) r) => (r.P, r.F.Count, r.T);
    }

    [Fact]
    public void Key_figures_take_the_worst_status_of_their_paths_and_items_cover_every_entry()
    {
        var comparison = new RunComparison("doc-01", "claude-sonnet-4-6", 1,
        [
            new FieldDelta("balance_check.assets", FieldDeltaKind.Identical, 0, "1", "1"),
            new FieldDelta("balance_check.liabilities", FieldDeltaKind.WithinTolerance, 0.001, "1000", "1001"),
            new FieldDelta("balance_check.current_assets_items[0].amount", FieldDeltaKind.Identical, 0, "5", "5"),
            new FieldDelta("balance_check.current_assets_items[1].amount", FieldDeltaKind.Different, 0.5, "5", "10"),
            new FieldDelta("balance_check.current_assets_items_total", FieldDeltaKind.NullFlip, null, "1", null),   // not an entry of the array
            new FieldDelta("balance_check.net_profit", FieldDeltaKind.NullFlip, null, "3", null),
        ],
        [], null, null, false, "Low", "Low");

        var status = ExtractionModelComparisonReport.SummarizeKeyFields(comparison).ToDictionary(s => s.Key);

        Assert.Equal(FieldDeltaKind.Identical, status["balance_check.assets"].Kind);
        Assert.Equal(FieldDeltaKind.WithinTolerance, status["balance_check.liabilities"].Kind);
        Assert.Equal(FieldDeltaKind.NullFlip, status["balance_check.net_profit"].Kind);
        Assert.Equal((FieldDeltaKind.Different, 2), (status["balance_check.current_assets_items"].Kind, status["balance_check.current_assets_items"].Paths));
        Assert.Equal(0, status["balance_check.equity"].Paths);

        var line = ExtractionModelComparisonReport.KeyFieldLine(comparison);
        Assert.Contains("current_assets_items=Different", line, StringComparison.Ordinal);
        Assert.Contains("equity=absent", line, StringComparison.Ordinal);
        Assert.Contains("band same", line, StringComparison.Ordinal);
        Assert.DoesNotContain("1001", line, StringComparison.Ordinal);   // statuses, never values
    }

    [Fact]
    public void RunSummaryLine_reports_escalation_trim_and_integrity_without_figures()
    {
        var variant = ExtractionModelComparisonReport.ParseVariants("claude-sonnet-4-6", null, Defaults)[0];
        var run = Run(variant, 1, "987654", 1.5, 40m, "Medium") with
        {
            OutputTokens        = 20600,
            MaxPassOutputTokens = 20600,
            Escalated           = true,
            AnswerTrimmed       = true,
            PreambleChars       = 412,
            TrailerChars        = 0,
            IntegrityPassed     = false,
            FailedChecks        = ["accounting_equation"],
            Confidence          = 0.875m,
            AlertCodes          = ["PASIVO_100_CORRIENTE", "SALVEDAD_AUDITOR"],
        };

        var line = ExtractionModelComparisonReport.RunSummaryLine(run);

        Assert.Contains("escalated YES", line, StringComparison.Ordinal);
        Assert.Contains("trimmed yes (preamble 412 chars, trailer 0 chars)", line, StringComparison.Ordinal);
        Assert.Contains("integrity FAILED: accounting_equation", line, StringComparison.Ordinal);
        Assert.Contains("out 20600 tok", line, StringComparison.Ordinal);
        Assert.Contains("confidence 0.88", line, StringComparison.Ordinal);
        Assert.EndsWith("alerts [PASIVO_100_CORRIENTE, SALVEDAD_AUDITOR]", line, StringComparison.Ordinal);
        Assert.DoesNotContain("987654", line, StringComparison.Ordinal);

        Assert.EndsWith("alerts []", ExtractionModelComparisonReport.RunSummaryLine(run with { AlertCodes = [] }), StringComparison.Ordinal);

        var failed = Run(variant, 2, null, null, null, null, succeeded: false) with { FailureCode = "claude_timeout" };
        Assert.Contains("FAILED ClaudeApiException [claude_timeout]", ExtractionModelComparisonReport.RunSummaryLine(failed), StringComparison.Ordinal);
        Assert.EndsWith("confidence — | alerts —", ExtractionModelComparisonReport.RunSummaryLine(failed), StringComparison.Ordinal);

        var csv = ExtractionModelComparisonReport.BuildCsv([run], []);
        Assert.Contains("doc-01,claude-sonnet-4-6,1,run,escalated,,true,,,", csv, StringComparison.Ordinal);
        Assert.Contains("doc-01,claude-sonnet-4-6,1,run,preamble_chars,,412,,,", csv, StringComparison.Ordinal);
        Assert.Contains("doc-01,claude-sonnet-4-6,1,run,integrity_failed_checks,,accounting_equation,,,", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadAlertCodes_keeps_codes_in_order_and_never_titles_descriptions_or_free_text()
    {
        const string alerts =
            """[{"level":"medium","code":"PASIVO_100_CORRIENTE","title":"Pasivo íntegramente corriente","description":"$987.654"},""" +
            """{"level":"low","code":"Salvedad del auditor sobre 987654","title":"t","description":"d"},""" +
            """{"level":"high","code":"CONTINGENCIA_JUDICIAL","title":"t","description":"d"},{"level":"low"}]""";

        var codes = ExtractionModelComparisonReport.ReadAlertCodes(alerts);

        Assert.Equal(
            ["PASIVO_100_CORRIENTE", ExtractionModelComparisonReport.NonCodePlaceholder, "CONTINGENCIA_JUDICIAL", ExtractionModelComparisonReport.NonCodePlaceholder],
            codes);
        Assert.Empty(ExtractionModelComparisonReport.ReadAlertCodes("[]")!);
        Assert.Null(ExtractionModelComparisonReport.ReadAlertCodes(null));
        Assert.Null(ExtractionModelComparisonReport.ReadAlertCodes("not json"));
        Assert.Null(ExtractionModelComparisonReport.ReadAlertCodes("{}"));
    }

    [Fact]
    public void A_snapshot_round_trips_as_the_baseline_of_the_next_run()
    {
        var variant   = ExtractionModelComparisonReport.ParseVariants("claude-sonnet-4-6", null, Defaults)[0];
        var documents = new List<ComparisonDocument> { new("doc-01", "a.pdf", "sha-a", 10, 1) };
        var before    = new List<ComparisonRun>
        {
            Run(variant, 1, "1000", 1.5, 40m, "Medium") with { Escalated = true, PreambleChars = 30, FailedChecks = ["column_swap"], IntegrityPassed = false, AlertCodes = ["INVENTARIO_ELEVADO"] },
        };

        var json = ExtractionModelComparisonReport.BuildSnapshot("2.19", documents, before);
        var (baseVariant, baseRuns) = ExtractionModelComparisonReport.LoadBaseline(json, documents);

        Assert.Equal("antes 2.19 · claude-sonnet-4-6", baseVariant.Label);
        Assert.Equal("claude-sonnet-4-6", baseVariant.Spec.Id);
        var loaded = Assert.Single(baseRuns);
        Assert.True(loaded.Escalated);
        Assert.Equal(30, loaded.PreambleChars);
        Assert.Equal(["column_swap"], loaded.FailedChecks);
        Assert.Equal(["INVENTARIO_ELEVADO"], loaded.AlertCodes);

        // A snapshot written before alert codes were recorded loads with none — "—", never "no alerts".
        var legacy = json.Replace("\"AlertCodes\"", "\"UnknownField\"", StringComparison.Ordinal);
        Assert.Null(Assert.Single(ExtractionModelComparisonReport.LoadBaseline(legacy, documents).Runs).AlertCodes);
        Assert.Equal(1.5, loaded.Ratios!.CurrentRatio);

        // The loaded run is the baseline: the new run compares against it, key figure by key figure.
        var after = Run(variant, 1, "1000", 1.5, 40m, "Medium");
        var comparisons = ExtractionModelComparisonReport.CompareAll([baseVariant, variant], [.. baseRuns, after]);
        var c = Assert.Single(comparisons);
        Assert.Equal("claude-sonnet-4-6", c.GroupLabel);
        Assert.Equal(FieldDeltaKind.Identical, c.Fields.Single(f => f.Path == "assets").Kind);
    }

    [Fact]
    public void A_snapshot_over_different_pdfs_is_refused_before_anything_is_spent()
    {
        var variant = ExtractionModelComparisonReport.ParseVariants("claude-sonnet-4-6", null, Defaults)[0];
        var json = ExtractionModelComparisonReport.BuildSnapshot("2.19",
            [new ComparisonDocument("doc-01", "a.pdf", "sha-a", 10, 1)], [Run(variant, 1, "1", 1.0, 1m, "Low")]);

        Assert.Throws<InvalidOperationException>(() =>
            ExtractionModelComparisonReport.LoadBaseline(json, [new ComparisonDocument("doc-01", "a.pdf", "sha-OTHER", 10, 1)]));
        Assert.Throws<InvalidOperationException>(() =>
            ExtractionModelComparisonReport.LoadBaseline(json,
            [
                new ComparisonDocument("doc-01", "a.pdf", "sha-a", 10, 1),
                new ComparisonDocument("doc-02", "b.pdf", "sha-b", 10, 1),
            ]));
    }

    private static ComparisonRun Run(
        ComparisonVariant variant, int index, string? assets, double? currentRatio, decimal? score, string? level,
        bool succeeded = true) => new()
    {
        DocAlias     = "doc-01",
        Variant      = variant,
        RunIndex     = index,
        Succeeded    = succeeded,
        FailureType  = succeeded ? null : "ClaudeApiException",
        Method       = succeeded ? "document" : null,
        InputTokens  = 100,
        OutputTokens = 50,
        CostUsd      = 0.001m,
        LatencyMs    = 1234,
        Fields       = assets is null ? null : new Dictionary<string, string> { ["assets"] = assets },
        Ratios       = currentRatio is null ? null : new RatiosData { CurrentRatio = currentRatio },
        ScoreRatios  = score,
        Score        = score,
        Level        = level
    };
}
