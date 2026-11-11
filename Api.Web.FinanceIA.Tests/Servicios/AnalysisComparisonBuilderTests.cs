// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-06

using System.Text.Json;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.AnalysisComparison;
using Xunit;
using Codes = Api.Web.FinanceIA.Models.Financia.AnalysisComparisonCodes;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// The rows, deltas, trends and notes of "Cambios respecto al análisis anterior", on synthetic analyses.
/// Pure: <see cref="AnalysisComparisonBuilder"/> reads two snapshots and nothing else.
/// </summary>
public sealed class AnalysisComparisonBuilderTests
{
    private const string Breakdown =
        """{"scoreRatios":20,"scoreBcra":10,"scoreMacro":50,"bcraExcluded":false,"ventasVariacion":-0.1,"scoringAlgoVersion":"11"}""";

    private const string Ratios =
        """{"current_ratio":1.5,"acid_test":1.0,"debt_ratio":0.6,"leverage":1.5,"roe":12.345,"roa":5,"net_margin":0,"interest_coverage":null}""";

    private const string Balance =
        """{"assets":1000000,"current_assets":400000,"liabilities":600000,"current_liabilities":300000,"equity":400000,"net_revenue":2000000,"operating_result":150000,"net_profit":100000,"inventory":null}""";

    private static AnalysisComparisonSnapshot Snapshot(int fiscalYear = 2025, Action<AnalysisComparisonSnapshot>? configure = null)
    {
        var snapshot = new AnalysisComparisonSnapshot
        {
            ResultId           = Guid.NewGuid(),
            AnalysisId         = Guid.NewGuid(),
            EntityId           = Guid.NewGuid(),
            AnalysisKind       = AnalysisKinds.Balance,
            Status             = AnalysisStatuses.Approved,
            FiscalYear         = fiscalYear,
            PromptVersion      = "2.20",
            CreatedAt          = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc),
            ReviewedAt         = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc),
            AiSuggestedScore   = 30m,
            AiSuggestedLevel   = "Medium",
            BalanceJson        = Balance,
            RatiosJson         = Ratios,
            ScoreBreakdownJson = Breakdown,
            BcraDeudaEstadoRaw                = ((int)BcraDeudaConsultaEstado.DebtFound).ToString(),
            BcraDeudaSituacionMaximaRaw       = "2",
            BcraDeudaMontoTotalRaw            = "1500.5",
            BcraChequesEstadoRaw              = ((int)BcraChequesConsultaEstado.ChequesFound).ToString(),
            BcraChequesTotalRechazadosRaw     = "3",
            BcraChequesMontoTotalRechazadoRaw = "45000.40",
        };
        configure?.Invoke(snapshot);
        return snapshot;
    }

    private static AnalysisComparisonGroup? Group(AnalysisComparisonResponse response, string key)
        => response.Groups!.SingleOrDefault(g => g.Key == key);

    private static AnalysisComparisonRow Row(AnalysisComparisonResponse response, string group, string key)
        => Assert.Single(Group(response, group)!.Rows, r => r.Key == key);

    private static AnalysisComparisonResponse Build(AnalysisComparisonSnapshot current, AnalysisComparisonSnapshot previous)
        => AnalysisComparisonBuilder.Build(current, previous).Response;

    // ── Headers ───────────────────────────────────────────────────────────────

    [Fact]
    public void Headers_carry_the_nosis_score_level_and_decision_time()
    {
        var previous = Snapshot(2024, s => { s.Status = AnalysisStatuses.PreApproved; s.ReviewedAt = null; s.PreApprovedAt = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc); });
        var current  = Snapshot(2025, s => { s.Status = AnalysisStatuses.PendingReview; s.ReviewedAt = null; s.AiSuggestedScore = 25m; });

        var response = Build(current, previous);

        Assert.True(response.Available);
        Assert.Null(response.Reason);
        Assert.Equal(750, response.Current!.Score);
        Assert.Null(response.Current.DecidedAt);
        Assert.Equal(AnalysisStatuses.PendingReview, response.Current.Status);
        Assert.Equal(700, response.Previous!.Score);
        Assert.Equal(previous.PreApprovedAt, response.Previous.DecidedAt);
        Assert.Equal(previous.ResultId, response.Previous.ResultId);
        Assert.Equal(2024, response.Previous.FiscalYear);
        Assert.Equal("2.20", response.Previous.PromptVersion);
        Assert.False(response.SameFiscalYear);
    }

    // ── Score group ───────────────────────────────────────────────────────────

    [Fact]
    public void Score_rows_compare_on_the_1000_scale()
    {
        var current  = Snapshot(configure: s => { s.AiSuggestedScore = 25m; s.AiSuggestedLevel = "Low"; });
        var previous = Snapshot(configure: s => s.ScoreBreakdownJson =
            """{"scoreRatios":30,"scoreBcra":10,"scoreMacro":40}""");

        var response = Build(current, previous);

        var score = Row(response, Codes.Groups.Score, "score");
        Assert.Equal(Codes.Units.Score, score.Unit);
        Assert.Equal(750m, score.Current);
        Assert.Equal(700m, score.Previous);
        Assert.Equal(50m, score.Delta);
        Assert.Equal(7.1m, score.DeltaPct);
        Assert.Equal(Codes.Directions.UpIsBetter, score.Direction);
        Assert.Equal(Codes.Trends.Better, score.Trend);

        var ratios = Row(response, Codes.Groups.Score, "score_ratios");
        Assert.Equal(800m, ratios.Current);
        Assert.Equal(700m, ratios.Previous);
        Assert.Equal(Codes.Trends.Better, ratios.Trend);

        Assert.Equal(Codes.Trends.Same, Row(response, Codes.Groups.Score, "score_bcra").Trend);

        var macro = Row(response, Codes.Groups.Score, "score_macro");
        Assert.Equal(500m, macro.Current);
        Assert.Equal(600m, macro.Previous);
        Assert.Equal(Codes.Trends.Worse, macro.Trend);
    }

    [Theory]
    [InlineData("Low", "Medium", "better")]
    [InlineData("High", "Medium", "worse")]
    [InlineData("Medium", "Medium", "same")]
    [InlineData("Medium", "Unknown", "n/a")]
    public void Level_is_text_with_a_severity_trend(string current, string previous, string trend)
    {
        var response = Build(
            Snapshot(configure: s => s.AiSuggestedLevel = current),
            Snapshot(configure: s => s.AiSuggestedLevel = previous));

        var level = Row(response, Codes.Groups.Score, "level");
        Assert.Equal(Codes.Units.Text, level.Unit);
        Assert.Equal(current, level.Current);
        Assert.Equal(previous, level.Previous);
        Assert.Null(level.Delta);
        Assert.Null(level.DeltaPct);
        Assert.Equal(Codes.Directions.DownIsBetter, level.Direction);
        Assert.Equal(trend, level.Trend);
    }

    [Fact]
    public void An_excluded_bcra_has_no_bcra_sub_score()
    {
        var current = Snapshot(configure: s => s.ScoreBreakdownJson =
            """{"scoreRatios":20,"scoreBcra":50,"scoreMacro":50,"bcraExcluded":true}""");

        var row = Row(Build(current, Snapshot()), Codes.Groups.Score, "score_bcra");

        Assert.Null(row.Current);
        Assert.Equal(900m, row.Previous);
        Assert.Null(row.Delta);
        Assert.Equal(Codes.Trends.NotAvailable, row.Trend);
    }

    // ── Ratios group ──────────────────────────────────────────────────────────

    [Fact]
    public void Ratios_follow_the_detail_order_units_and_directions()
    {
        var previous = Snapshot(configure: s => s.RatiosJson =
            """{"current_ratio":1.2,"acid_test":1.0,"debt_ratio":0.5,"leverage":1.0,"roe":10,"roa":0,"net_margin":2.5,"interest_coverage":null}""");

        var response = Build(Snapshot(), previous);
        var group    = Group(response, Codes.Groups.Ratios)!;

        // interest_coverage is null on both sides: skipped.
        Assert.Equal(["current_ratio", "acid_test", "debt_ratio", "leverage", "roe", "roa", "net_margin"],
            group.Rows.Select(r => r.Key));

        var current = Row(response, Codes.Groups.Ratios, "current_ratio");
        Assert.Equal(Codes.Units.Ratio, current.Unit);
        Assert.Equal(1.5m, current.Current);
        Assert.Equal(0.3m, current.Delta);
        Assert.Equal(25.0m, current.DeltaPct);
        Assert.Equal(Codes.Trends.Better, current.Trend);

        Assert.Equal(Codes.Trends.Same, Row(response, Codes.Groups.Ratios, "acid_test").Trend);

        var debt = Row(response, Codes.Groups.Ratios, "debt_ratio");
        Assert.Equal(Codes.Directions.DownIsBetter, debt.Direction);
        Assert.Equal(Codes.Trends.Worse, debt.Trend);

        var roe = Row(response, Codes.Groups.Ratios, "roe");
        Assert.Equal(Codes.Units.Percent, roe.Unit);
        Assert.Equal(12.35m, roe.Current); // rounded as displayed, two decimals

        // previous 0 → no percentage change, but a delta and a trend.
        var roa = Row(response, Codes.Groups.Ratios, "roa");
        Assert.Equal(5m, roa.Delta);
        Assert.Null(roa.DeltaPct);
        Assert.Equal(Codes.Trends.Better, roa.Trend);

        var margin = Row(response, Codes.Groups.Ratios, "net_margin");
        Assert.Equal(-2.5m, margin.Delta);
        Assert.Equal(-100.0m, margin.DeltaPct);
        Assert.Equal(Codes.Trends.Worse, margin.Trend);
    }

    [Fact]
    public void One_side_null_keeps_the_row_without_a_delta()
    {
        var previous = Snapshot(configure: s => s.RatiosJson = """{"current_ratio":null}""");

        var row = Row(Build(Snapshot(), previous), Codes.Groups.Ratios, "current_ratio");

        Assert.Equal(1.5m, row.Current);
        Assert.Null(row.Previous);
        Assert.Null(row.Delta);
        Assert.Null(row.DeltaPct);
        Assert.Equal(Codes.Trends.NotAvailable, row.Trend);
    }

    // ── Balance group ─────────────────────────────────────────────────────────

    [Fact]
    public void Balance_of_the_same_fiscal_year_is_judged()
    {
        var previous = Snapshot(configure: s => s.BalanceJson =
            """{"assets":900000,"liabilities":600000,"equity":500000,"net_revenue":2000000,"net_profit":-50000}""");

        var response = Build(Snapshot(), previous);

        var assets = Row(response, Codes.Groups.Balance, "assets");
        Assert.Equal(Codes.Units.Pesos, assets.Unit);
        Assert.Equal(Codes.Directions.Neutral, assets.Direction);
        Assert.Equal(100000m, assets.Delta);
        Assert.Equal(11.1m, assets.DeltaPct);
        Assert.Equal(Codes.Trends.NotAvailable, assets.Trend); // neutral

        Assert.Equal(Codes.Trends.Same, Row(response, Codes.Groups.Balance, "liabilities").Trend);
        Assert.Equal(Codes.Trends.Worse, Row(response, Codes.Groups.Balance, "equity").Trend);
        Assert.Equal(Codes.Trends.Same, Row(response, Codes.Groups.Balance, "net_revenue").Trend);

        var profit = Row(response, Codes.Groups.Balance, "net_profit");
        Assert.Equal(150000m, profit.Delta);
        Assert.Null(profit.DeltaPct); // a loss turned into a profit: no meaningful percentage (Q28)
        Assert.Equal(Codes.Trends.Better, profit.Trend);

        // inventory null on both sides: skipped.
        Assert.DoesNotContain(Group(response, Codes.Groups.Balance)!.Rows, r => r.Key == "inventory");
    }

    [Fact]
    public void Nominal_pesos_of_different_fiscal_years_keep_figures_but_no_trend()
    {
        var previous = Snapshot(2024, s => s.BalanceJson = """{"net_revenue":1000000,"equity":200000}""");

        var response = Build(Snapshot(2025), previous);

        var revenue = Row(response, Codes.Groups.Balance, "net_revenue");
        Assert.Equal(1000000m, revenue.Delta);
        Assert.Equal(100.0m, revenue.DeltaPct);
        Assert.Equal(Codes.Trends.NotAvailable, revenue.Trend);

        // Pesos rows in the BCRA group too; non-pesos rows are still judged.
        Assert.Equal(Codes.Trends.NotAvailable, Row(response, Codes.Groups.Bcra, "total_debt").Trend);
        Assert.Equal(Codes.Trends.Same, Row(response, Codes.Groups.Bcra, "rejected_cheques_count").Trend);
        Assert.Equal(Codes.Trends.Same, Row(response, Codes.Groups.Ratios, "current_ratio").Trend);

        Assert.Contains(Codes.Notes.NominalAmounts, response.Notes!);
        Assert.DoesNotContain(Codes.Notes.SameFiscalYear, response.Notes!);
    }

    // ── BCRA group ────────────────────────────────────────────────────────────

    [Fact]
    public void Bcra_rows_read_the_stored_scalars()
    {
        var previous = Snapshot(configure: s =>
        {
            s.BcraDeudaSituacionMaximaRaw       = "1";
            s.BcraDeudaMontoTotalRaw            = "1000";
            s.BcraChequesTotalRechazadosRaw     = "0";
            s.BcraChequesMontoTotalRechazadoRaw = "0";
        });

        var response = Build(Snapshot(), previous);

        var situation = Row(response, Codes.Groups.Bcra, "worst_situation");
        Assert.Equal(Codes.Units.Count, situation.Unit);
        Assert.Equal(2m, situation.Current);
        Assert.Equal(1m, situation.Previous);
        Assert.Equal(1m, situation.Delta);
        Assert.Null(situation.DeltaPct); // a category has no percentage change
        Assert.Equal(Codes.Trends.Worse, situation.Trend);

        var debt = Row(response, Codes.Groups.Bcra, "total_debt");
        Assert.Equal(1500500m, debt.Current);  // miles de pesos × 1000
        Assert.Equal(1000000m, debt.Previous);
        Assert.Equal(Codes.Trends.Worse, debt.Trend);

        var count = Row(response, Codes.Groups.Bcra, "rejected_cheques_count");
        Assert.Equal(3m, count.Current);
        Assert.Equal(0m, count.Previous);
        Assert.Null(count.DeltaPct);
        Assert.Equal(Codes.Trends.Worse, count.Trend);

        Assert.Equal(45000m, Row(response, Codes.Groups.Bcra, "rejected_cheques_amount").Current);
    }

    [Fact]
    public void No_debt_and_no_cheques_reported_are_situacion_1_and_zero()
    {
        var current = Snapshot(configure: s =>
        {
            s.BcraDeudaEstadoRaw          = ((int)BcraDeudaConsultaEstado.NoDebtReported).ToString();
            s.BcraDeudaSituacionMaximaRaw = null;
            s.BcraDeudaMontoTotalRaw      = null;
            s.BcraChequesEstadoRaw        = ((int)BcraChequesConsultaEstado.NoChequesReported).ToString();
        });

        var response = Build(current, Snapshot());

        Assert.Equal(1m, Row(response, Codes.Groups.Bcra, "worst_situation").Current);
        Assert.Equal(0m, Row(response, Codes.Groups.Bcra, "total_debt").Current);
        Assert.Equal(Codes.Trends.Better, Row(response, Codes.Groups.Bcra, "total_debt").Trend);
        Assert.Equal(0m, Row(response, Codes.Groups.Bcra, "rejected_cheques_count").Current);
        Assert.Equal(0m, Row(response, Codes.Groups.Bcra, "rejected_cheques_amount").Current);
    }

    [Fact]
    public void A_failed_bcra_lookup_is_null_never_zero_and_both_failed_skips_the_group()
    {
        static void Failed(AnalysisComparisonSnapshot s)
        {
            s.BcraDeudaEstadoRaw   = ((int)BcraDeudaConsultaEstado.LookupFailed).ToString();
            s.BcraChequesEstadoRaw = ((int)BcraChequesConsultaEstado.LookupFailed).ToString();
        }

        var oneFailed = Build(Snapshot(configure: Failed), Snapshot());
        var debt = Row(oneFailed, Codes.Groups.Bcra, "total_debt");
        Assert.Null(debt.Current);
        Assert.Equal(Codes.Trends.NotAvailable, debt.Trend);
        Assert.Null(Row(oneFailed, Codes.Groups.Bcra, "rejected_cheques_count").Current);

        var bothFailed = AnalysisComparisonBuilder.Build(Snapshot(configure: Failed), Snapshot(configure: Failed));
        Assert.Null(Group(bothFailed.Response, Codes.Groups.Bcra));
        Assert.Empty(bothFailed.UnreadableKeys); // a failed lookup is a known state, not an unreadable blob
    }

    // ── LUFE group ────────────────────────────────────────────────────────────

    [Fact]
    public void Lufe_compares_the_sales_variation_the_score_used()
    {
        var previous = Snapshot(configure: s => s.ScoreBreakdownJson = """{"ventasVariacion":0.05}""");

        var row = Row(Build(Snapshot(), previous), Codes.Groups.Lufe, "sales_variation");

        Assert.Equal(Codes.Units.Percent, row.Unit);
        Assert.Equal(-10m, row.Current);
        Assert.Equal(5m, row.Previous);
        Assert.Equal(-15m, row.Delta);
        Assert.Equal(Codes.Trends.Worse, row.Trend);
    }

    [Fact]
    public void A_group_with_every_row_null_on_both_sides_is_omitted()
    {
        static void NoSales(AnalysisComparisonSnapshot s) => s.ScoreBreakdownJson = """{"scoreRatios":20}""";

        var response = Build(Snapshot(configure: NoSales), Snapshot(configure: NoSales));

        Assert.Null(Group(response, Codes.Groups.Lufe));
        Assert.DoesNotContain(Group(response, Codes.Groups.Score)!.Rows, r => r.Key == "score_macro");
    }

    // ── Notes ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Same_fiscal_year_is_noted_and_amounts_are_not_called_nominal()
    {
        var response = Build(Snapshot(2025), Snapshot(2025));

        Assert.True(response.SameFiscalYear);
        Assert.Equal([Codes.Notes.SameFiscalYear], response.Notes);
    }

    [Theory]
    [InlineData("""{"scoringAlgoVersion":"10"}""", true)]
    [InlineData("""{"scoreRatios":20}""", true)]          // absent = version 1
    [InlineData("""{"scoringAlgoVersion":11}""", false)]  // a number reads the same
    [InlineData(null, false)]                              // no breakdown: unknown, not a change
    public void Engine_change_is_the_scoring_algorithm_version(string? previousBreakdown, bool changed)
    {
        var response = Build(Snapshot(), Snapshot(configure: s => s.ScoreBreakdownJson = previousBreakdown));

        Assert.Equal(changed, response.Notes!.Contains(Codes.Notes.EngineChanged));
    }

    [Fact]
    public void Integrity_failures_are_noted_per_side_and_a_presentation_caveat_is_not()
    {
        const string failed =
            """{"assets":1000,"integrity":{"passed":false,"checks":[{"name":"accounting_equation","passed":false}]}}""";
        const string passed =
            """{"assets":1000,"integrity":{"passed":true,"checks":[{"name":"accounting_equation","passed":true}]}}""";

        var response = Build(
            Snapshot(configure: s => s.BalanceJson = failed),
            Snapshot(configure: s => s.BalanceJson = passed));

        Assert.Contains(Codes.Notes.IntegrityFailedCurrent, response.Notes!);
        Assert.DoesNotContain(Codes.Notes.IntegrityFailedPrevious, response.Notes!);

        var reversed = Build(
            Snapshot(configure: s => s.BalanceJson = passed),
            Snapshot(configure: s => s.BalanceJson = failed));
        Assert.Equal([Codes.Notes.SameFiscalYear, Codes.Notes.IntegrityFailedPrevious], reversed.Notes);
    }

    // ── Malformed blobs ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("{not json")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"current_ratio":"high"}""")]
    public void A_malformed_ratios_blob_omits_only_the_ratios_group(string ratios)
    {
        var outcome = AnalysisComparisonBuilder.Build(Snapshot(), Snapshot(configure: s => s.RatiosJson = ratios));

        Assert.Null(Group(outcome.Response, Codes.Groups.Ratios));
        Assert.NotNull(Group(outcome.Response, Codes.Groups.Balance));
        Assert.NotNull(Group(outcome.Response, Codes.Groups.Score));
        Assert.Equal(["ratios:previous"], outcome.UnreadableKeys);
    }

    [Fact]
    public void A_malformed_balance_omits_the_balance_group_and_never_throws()
    {
        var outcome = AnalysisComparisonBuilder.Build(Snapshot(configure: s => s.BalanceJson = "{oops"), Snapshot());

        Assert.Null(Group(outcome.Response, Codes.Groups.Balance));
        Assert.NotNull(Group(outcome.Response, Codes.Groups.Ratios));
        Assert.DoesNotContain(Codes.Notes.IntegrityFailedCurrent, outcome.Response.Notes!);
        Assert.Equal(["balance:current"], outcome.UnreadableKeys);
    }

    [Fact]
    public void A_malformed_breakdown_keeps_score_and_level_but_drops_sub_scores_and_lufe()
    {
        var outcome = AnalysisComparisonBuilder.Build(Snapshot(configure: s => s.ScoreBreakdownJson = "{\"scoreRatios\":"), Snapshot());

        Assert.Equal(["score", "level"], Group(outcome.Response, Codes.Groups.Score)!.Rows.Select(r => r.Key));
        Assert.Null(Group(outcome.Response, Codes.Groups.Lufe));
        Assert.DoesNotContain(Codes.Notes.EngineChanged, outcome.Response.Notes!);
        Assert.Equal(["score:current"], outcome.UnreadableKeys);
    }

    [Fact]
    public void An_unparseable_bcra_scalar_nulls_that_value_and_is_reported()
    {
        var outcome = AnalysisComparisonBuilder.Build(
            Snapshot(configure: s => { s.BcraDeudaMontoTotalRaw = "abc"; s.BcraChequesEstadoRaw = "99"; }),
            Snapshot());

        Assert.Null(Row(outcome.Response, Codes.Groups.Bcra, "total_debt").Current);
        Assert.Null(Row(outcome.Response, Codes.Groups.Bcra, "rejected_cheques_count").Current);
        Assert.Equal(2m, Row(outcome.Response, Codes.Groups.Bcra, "worst_situation").Current);
        Assert.Equal(["bcra:current"], outcome.UnreadableKeys);
    }

    [Fact]
    public void Blank_blobs_are_absent_not_unreadable()
    {
        var outcome = AnalysisComparisonBuilder.Build(
            Snapshot(configure: s => { s.RatiosJson = null; s.BalanceJson = " "; s.ScoreBreakdownJson = "null"; }),
            Snapshot());

        Assert.Empty(outcome.UnreadableKeys);
        Assert.Null(Row(outcome.Response, Codes.Groups.Ratios, "current_ratio").Current);
        Assert.Null(Row(outcome.Response, Codes.Groups.Balance, "assets").Current);
    }

    // ── Contract ──────────────────────────────────────────────────────────────

    [Fact]
    public void Every_emitted_string_is_from_the_code_vocabulary()
    {
        var response = Build(Snapshot(2025), Snapshot(2024, s => s.ScoreBreakdownJson = "{}"));

        Assert.All(response.Notes!, n => Assert.Contains(n, Codes.Notes.All));
        Assert.Equal(Codes.Groups.Company, response.Groups!.Select(g => g.Key));
        Assert.All(response.Groups!.SelectMany(g => g.Rows), r =>
        {
            Assert.Contains(r.Unit, Codes.Units.All);
            Assert.Contains(r.Direction, Codes.Directions.All);
            Assert.Contains(r.Trend, Codes.Trends.All);
            Assert.False(string.IsNullOrWhiteSpace(r.Label));
        });
    }

    [Fact]
    public void Serialises_with_the_portal_contract_keys()
    {
        var json = JsonSerializer.Serialize(Build(Snapshot(), Snapshot()), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(
            ["available", "reason", "current", "previous", "sameFiscalYear", "notes", "groups"],
            root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(
            ["analysisId", "resultId", "fiscalYear", "status", "decidedAt", "score", "level", "promptVersion"],
            root.GetProperty("current").EnumerateObject().Select(p => p.Name));

        var row = root.GetProperty("groups")[0].GetProperty("rows")[0];
        Assert.Equal(
            ["key", "label", "unit", "current", "previous", "delta", "deltaPct", "direction", "trend"],
            row.EnumerateObject().Select(p => p.Name));
        Assert.Equal(JsonValueKind.Number, row.GetProperty("current").ValueKind);

        var level = root.GetProperty("groups")[0].GetProperty("rows")[1];
        Assert.Equal(JsonValueKind.String, level.GetProperty("current").ValueKind);
    }

    [Fact]
    public void Unavailable_serialises_to_available_and_reason_with_null_payload()
    {
        var json = JsonSerializer.Serialize(
            AnalysisComparisonResponse.Unavailable(Codes.Reasons.NoPrevious),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);

        Assert.False(document.RootElement.GetProperty("available").GetBoolean());
        Assert.Equal("no_previous", document.RootElement.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("groups").ValueKind);
    }

    // ── deltaPct (Q28) and the pinned company response ────────────────────────

    [Theory]
    [InlineData(110, 100, 10.0)]
    [InlineData(90, 100, -10.0)]
    [InlineData(0, 100, -100.0)]
    [InlineData(100, 100, 0.0)]
    public void PercentChange_is_over_a_positive_previous(double current, double previous, double expected)
        => Assert.Equal((decimal)expected, AnalysisComparisonBuilder.PercentChange((decimal)current, (decimal)previous));

    [Theory]
    [InlineData(100, 0)]      // no base
    [InlineData(100, -50)]    // previous negative (a loss turned into a profit)
    [InlineData(-100, -50)]   // previous negative, same sign: still no positive base
    [InlineData(-10, 100)]    // crossed zero
    [InlineData(0, 0)]
    public void PercentChange_is_null_without_a_positive_base_or_across_signs(double current, double previous)
        => Assert.Null(AnalysisComparisonBuilder.PercentChange((decimal)current, (decimal)previous));

    [Fact]
    public void PercentChange_is_null_with_a_side_missing()
    {
        Assert.Null(AnalysisComparisonBuilder.PercentChange(null, 100m));
        Assert.Null(AnalysisComparisonBuilder.PercentChange(100m, null));
    }

    [Fact]
    public void A_sign_change_keeps_delta_and_trend_but_no_delta_pct()
    {
        var previous = Snapshot(configure: s => s.BalanceJson = """{"equity":100000,"net_profit":20000}""");
        var current  = Snapshot(configure: s => s.BalanceJson = """{"equity":-30000,"net_profit":20000}""");

        var equity = Row(Build(current, previous), Codes.Groups.Balance, "equity");

        Assert.Equal(-130000m, equity.Delta);
        Assert.Null(equity.DeltaPct);
        Assert.Equal(Codes.Trends.Worse, equity.Trend);
    }

    /// <summary>
    /// A full company comparison serialised exactly as DEVELOP serialised it before the persona change — the
    /// expected JSON was generated by the previous builder — except the three rows Q28 changes, whose deltaPct is
    /// now null: operating result (−20 000 → 150 000, was +850 %), net profit (−50 000 → 100 000, was +300 %) and
    /// the LUFE sales variation (−10 % on both sides, was 0 %: a negative previous is no base).
    /// No <c>currency</c> key on the sides, no new group, no new note.
    /// </summary>
    [Fact]
    public void A_company_comparison_is_byte_identical_to_before_but_for_q28()
    {
        static Guid Id(int n) => new($"00000000-0000-0000-0000-{n:000000000000}");

        static AnalysisComparisonSnapshot Side(int n, decimal score, string level, string balance) => new()
        {
            ResultId = Id(n), AnalysisId = Id(n + 1), EntityId = Id(99), AnalysisKind = AnalysisKinds.Balance,
            Status = AnalysisStatuses.Approved, FiscalYear = 2025, PromptVersion = "2.20",
            CreatedAt  = new DateTime(2026, 10, n, 12, 0, 0, DateTimeKind.Utc),
            ReviewedAt = new DateTime(2026, 10, n + 1, 12, 0, 0, DateTimeKind.Utc),
            AiSuggestedScore = score, AiSuggestedLevel = level, BalanceJson = balance,
            RatiosJson = Ratios, ScoreBreakdownJson = Breakdown,
            BcraDeudaEstadoRaw = "0", BcraDeudaSituacionMaximaRaw = "2", BcraDeudaMontoTotalRaw = "1500.5",
            BcraChequesEstadoRaw = "0", BcraChequesTotalRechazadosRaw = "3", BcraChequesMontoTotalRechazadoRaw = "45000.40",
        };

        var current  = Side(10, 30m, "Medium", Balance);
        var previous = Side(1, 45m, "High",
            """{"assets":900000,"current_assets":350000,"liabilities":620000,"current_liabilities":310000,"equity":280000,"net_revenue":1800000,"operating_result":-20000,"net_profit":-50000,"inventory":10}""");

        var json = JsonSerializer.Serialize(Build(current, previous), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(PinnedCompanyComparison, json);
    }

    private const string PinnedCompanyComparison =
        """
        {"available":true,"reason":null,"current":{"analysisId":"00000000-0000-0000-0000-000000000011","resultId":"00000000-0000-0000-0000-000000000010","fiscalYear":2025,"status":"Approved","decidedAt":"2026-10-11T12:00:00Z","score":700,"level":"Medium","promptVersion":"2.20"},"previous":{"analysisId":"00000000-0000-0000-0000-000000000002","resultId":"00000000-0000-0000-0000-000000000001","fiscalYear":2025,"status":"Approved","decidedAt":"2026-10-02T12:00:00Z","score":550,"level":"High","promptVersion":"2.20"},"sameFiscalYear":true,"notes":["same_fiscal_year"],"groups":[{"key":"score","label":"Score","rows":[{"key":"score","label":"Score","unit":"score","current":700,"previous":550,"delta":150,"deltaPct":27.3,"direction":"up_is_better","trend":"better"},{"key":"level","label":"Nivel de riesgo","unit":"text","current":"Medium","previous":"High","delta":null,"deltaPct":null,"direction":"down_is_better","trend":"better"},{"key":"score_ratios","label":"Score de ratios","unit":"score","current":800,"previous":800,"delta":0,"deltaPct":0,"direction":"up_is_better","trend":"same"},{"key":"score_bcra","label":"Score BCRA","unit":"score","current":900,"previous":900,"delta":0,"deltaPct":0,"direction":"up_is_better","trend":"same"},{"key":"score_macro","label":"Score de contexto macro","unit":"score","current":500,"previous":500,"delta":0,"deltaPct":0,"direction":"up_is_better","trend":"same"}]},{"key":"ratios","label":"Ratios financieros","rows":[{"key":"current_ratio","label":"Liquidez corriente","unit":"ratio","current":1.5,"previous":1.5,"delta":0.0,"deltaPct":0,"direction":"up_is_better","trend":"same"},{"key":"acid_test","label":"Prueba \u00E1cida","unit":"ratio","current":1,"previous":1,"delta":0,"deltaPct":0,"direction":"up_is_better","trend":"same"},{"key":"debt_ratio","label":"Endeudamiento","unit":"ratio","current":0.6,"previous":0.6,"delta":0.0,"deltaPct":0,"direction":"down_is_better","trend":"same"},{"key":"leverage","label":"Apalancamiento","unit":"ratio","current":1.5,"previous":1.5,"delta":0.0,"deltaPct":0,"direction":"down_is_better","trend":"same"},{"key":"roe","label":"ROE","unit":"percent","current":12.35,"previous":12.35,"delta":0.00,"deltaPct":0,"direction":"up_is_better","trend":"same"},{"key":"roa","label":"ROA","unit":"percent","current":5,"previous":5,"delta":0,"deltaPct":0,"direction":"up_is_better","trend":"same"},{"key":"net_margin","label":"Margen neto","unit":"percent","current":0,"previous":0,"delta":0,"deltaPct":null,"direction":"up_is_better","trend":"same"}]},{"key":"balance","label":"Balance","rows":[{"key":"assets","label":"Activo total","unit":"pesos","current":1000000,"previous":900000,"delta":100000,"deltaPct":11.1,"direction":"neutral","trend":"n/a"},{"key":"current_assets","label":"Activo corriente","unit":"pesos","current":400000,"previous":350000,"delta":50000,"deltaPct":14.3,"direction":"neutral","trend":"n/a"},{"key":"liabilities","label":"Pasivo total","unit":"pesos","current":600000,"previous":620000,"delta":-20000,"deltaPct":-3.2,"direction":"neutral","trend":"n/a"},{"key":"current_liabilities","label":"Pasivo corriente","unit":"pesos","current":300000,"previous":310000,"delta":-10000,"deltaPct":-3.2,"direction":"neutral","trend":"n/a"},{"key":"equity","label":"Patrimonio neto","unit":"pesos","current":400000,"previous":280000,"delta":120000,"deltaPct":42.9,"direction":"up_is_better","trend":"better"},{"key":"net_revenue","label":"Ventas netas","unit":"pesos","current":2000000,"previous":1800000,"delta":200000,"deltaPct":11.1,"direction":"up_is_better","trend":"better"},{"key":"operating_result","label":"Resultado operativo","unit":"pesos","current":150000,"previous":-20000,"delta":170000,"deltaPct":null,"direction":"up_is_better","trend":"better"},{"key":"net_profit","label":"Resultado neto","unit":"pesos","current":100000,"previous":-50000,"delta":150000,"deltaPct":null,"direction":"up_is_better","trend":"better"},{"key":"inventory","label":"Bienes de cambio","unit":"pesos","current":null,"previous":10,"delta":null,"deltaPct":null,"direction":"neutral","trend":"n/a"}]},{"key":"bcra","label":"BCRA","rows":[{"key":"worst_situation","label":"Peor situaci\u00F3n BCRA","unit":"count","current":2,"previous":2,"delta":0,"deltaPct":null,"direction":"down_is_better","trend":"same"},{"key":"total_debt","label":"Deuda en el sistema financiero","unit":"pesos","current":1500500,"previous":1500500,"delta":0,"deltaPct":0,"direction":"down_is_better","trend":"same"},{"key":"rejected_cheques_count","label":"Cheques rechazados","unit":"count","current":3,"previous":3,"delta":0,"deltaPct":0,"direction":"down_is_better","trend":"same"},{"key":"rejected_cheques_amount","label":"Monto de cheques rechazados","unit":"pesos","current":45000,"previous":45000,"delta":0,"deltaPct":0,"direction":"down_is_better","trend":"same"}]},{"key":"lufe","label":"LUFE","rows":[{"key":"sales_variation","label":"Variaci\u00F3n de ventas declaradas (LUFE)","unit":"percent","current":-10.0,"previous":-10.0,"delta":0.0,"deltaPct":null,"direction":"up_is_better","trend":"same"}]}]}
        """;
}
