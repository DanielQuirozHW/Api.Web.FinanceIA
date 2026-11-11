// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-08

using System.Text.Json;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.AnalysisComparison;
using Api.Web.FinanceIA.Servicios.PersonEvaluation;
using Xunit;
using Codes = Api.Web.FinanceIA.Models.Financia.AnalysisComparisonCodes;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// "Cambios respecto al análisis anterior" for persona humana, on synthetic frozen evaluations serialised with
/// <see cref="PersonEvaluationJson"/> — the same writer the evaluation uses — so the columns read as stored.
/// </summary>
public sealed class AnalysisComparisonBuilderPersonaTests
{
    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static PersonFilterResult Filter(string id, string outcome, Dictionary<string, string?>? inputs = null) => new()
    {
        Id       = id,
        Outcome  = outcome,
        Evidence = "e",
        Source   = "s",
        Inputs   = inputs ?? [],
    };

    private static PersonIndicatorResult Indicator(string id, decimal? value, string unit = "ratio") => new()
    {
        Id       = id,
        Status   = value is null ? PersonIndicatorStatuses.SinDatos : PersonIndicatorStatuses.Ok,
        Value    = value,
        Unit     = unit,
        Evidence = "e",
    };

    private static PersonCoverageResult Coverage(int present, int total) => new()
    {
        FiscalRegime    = "Monotributo",
        Items           = [],
        RequiredPresent = present,
        RequiredTotal   = total,
        Index           = total == 0 ? 0m : Math.Round((decimal)present / total, 4),
        Snapshots       = [],
        CannotConclude  = [],
    };

    private static string Sources(PersonEvaluationParameters? parameters = null, int? agroCuotasPorAnio = null)
        => PersonEvaluationJson.Serialize(new PersonEvaluationSources
        {
            Documents  = [],
            Parameters = parameters ?? PersonEvaluationParameters.Defaults,
            Agro       = agroCuotasPorAnio is { } cuotas
                ? new PersonAgroMode { Active = true, Origin = "override", CuotasPorAnio = cuotas, MesInicioCampania = 5 }
                : null,
        });

    /// <summary>A complete evaluation: no rejection, one alert, situación 2, every headline indicator.</summary>
    private static PersonComparisonFacts Facts(Action<PersonComparisonFacts>? configure = null)
    {
        var facts = new PersonComparisonFacts
        {
            FiltersJson = PersonEvaluationJson.Serialize(new List<PersonFilterResult>
            {
                Filter("bcra_situacion", PersonFilterOutcomes.Ok, new() { ["situacionActual"] = "1", ["peorSituacionVentana"] = "2" }),
                Filter("bcra_eventos_graves", PersonFilterOutcomes.Alerta),
                Filter("arca_cuit_activo", PersonFilterOutcomes.Ok),
            }),
            IndicatorsJson = PersonEvaluationJson.Serialize(new List<PersonIndicatorResult>
            {
                Indicator("cobertura_servicio_deuda", 1.8765m),
                Indicator("cuota_ingreso", 0.2512m),
                Indicator("solvencia_pn", 25_000_000.4m, "ARS"),
                Indicator("patrimonio_operativo", 0.4m),
                Indicator("endeudamiento", 0.3m),
            }),
            CoverageJson         = PersonEvaluationJson.Serialize(Coverage(6, 8)),
            DocumentVersionsJson = Sources(),
            EvaluationVersion    = "0.10",
            AmountAtCharge       = 5_000_000m,
            Currency             = "ARS",
            InstallmentAmount    = 250_000m,
            CuotasPorAnio        = 12,
            FiadorIds            = [new Guid("00000000-0000-0000-0000-0000000000f1")],
        };
        configure?.Invoke(facts);
        return facts;
    }

    private static AnalysisComparisonSnapshot Snapshot(PersonComparisonFacts facts, int day) => new()
    {
        ResultId      = Guid.NewGuid(),
        AnalysisId    = Guid.NewGuid(),
        EntityId      = Guid.NewGuid(),
        AnalysisKind  = AnalysisKinds.PersonaHumana,
        Status        = AnalysisStatuses.Approved,
        FiscalYear    = 2026,
        PromptVersion = "pf-0.10",
        CreatedAt     = new DateTime(2026, 10, day, 12, 0, 0, DateTimeKind.Utc),
        ReviewedAt    = new DateTime(2026, 10, day + 1, 12, 0, 0, DateTimeKind.Utc),
        Person        = facts,
    };

    private static AnalysisComparisonBuilder.Outcome Build(PersonComparisonFacts current, PersonComparisonFacts previous)
        => AnalysisComparisonBuilder.BuildPersona(Snapshot(current, 20), Snapshot(previous, 1));

    private static AnalysisComparisonGroup? Group(AnalysisComparisonResponse response, string key)
        => response.Groups!.SingleOrDefault(g => g.Key == key);

    private static AnalysisComparisonRow Row(AnalysisComparisonResponse response, string group, string key)
        => Assert.Single(Group(response, group)!.Rows, r => r.Key == key);

    private static void AssertValues(AnalysisComparisonRow row, decimal current, decimal previous, decimal? delta = null)
    {
        Assert.Equal(current, Assert.IsType<decimal>(row.Current));
        Assert.Equal(previous, Assert.IsType<decimal>(row.Previous));
        if (delta is not null)
            Assert.Equal(delta, row.Delta);
    }

    private static string RejectingFilters => PersonEvaluationJson.Serialize(new List<PersonFilterResult>
    {
        Filter("bcra_situacion", PersonFilterOutcomes.Rechazo, new() { ["situacionActual"] = "4", ["peorSituacionVentana"] = "5" }),
        Filter("bcra_eventos_graves", PersonFilterOutcomes.Alerta),
        Filter("arca_sucesion", PersonFilterOutcomes.Alerta),
    });

    // ── The full comparison ───────────────────────────────────────────────────

    [Fact]
    public void Every_group_in_order_with_its_rows_units_and_directions()
    {
        var previous = Facts(f =>
        {
            f.FiltersJson = RejectingFilters;
            f.IndicatorsJson = PersonEvaluationJson.Serialize(new List<PersonIndicatorResult>
            {
                Indicator("cobertura_servicio_deuda", 1.2m),
                Indicator("cuota_ingreso", 0.40m),
                Indicator("solvencia_pn", 20_000_000m, "ARS"),
                Indicator("patrimonio_operativo", 0.5m),
            });
            f.CoverageJson = PersonEvaluationJson.Serialize(Coverage(4, 8));
        });

        var outcome  = Build(Facts(), previous);
        var response = outcome.Response;

        Assert.Empty(outcome.UnreadableKeys);
        Assert.True(response.Available);
        Assert.Null(response.SameFiscalYear);
        Assert.Equal(Codes.Groups.Persona, response.Groups!.Select(g => g.Key));

        var expected = new (string Group, string Key, string Unit, string Direction)[]
        {
            (Codes.Groups.Suggestion, "suggestion",               Codes.Units.Text,    Codes.Directions.DownIsBetter),
            (Codes.Groups.Filters,    "blocking_filters",         Codes.Units.Count,   Codes.Directions.DownIsBetter),
            (Codes.Groups.Filters,    "warnings",                 Codes.Units.Count,   Codes.Directions.DownIsBetter),
            (Codes.Groups.Indicators, "cobertura_servicio_deuda", Codes.Units.Ratio,   Codes.Directions.UpIsBetter),
            (Codes.Groups.Indicators, "cuota_ingreso",            Codes.Units.Percent, Codes.Directions.DownIsBetter),
            (Codes.Groups.Indicators, "solvencia_pn",             Codes.Units.Pesos,   Codes.Directions.UpIsBetter),
            (Codes.Groups.Indicators, "patrimonio_operativo",     Codes.Units.Percent, Codes.Directions.DownIsBetter),
            (Codes.Groups.Bcra,       "worst_situation",          Codes.Units.Count,   Codes.Directions.DownIsBetter),
            (Codes.Groups.Coverage,   "coverage_index",           Codes.Units.Percent, Codes.Directions.UpIsBetter),
            (Codes.Groups.Coverage,   "required_present",         Codes.Units.Count,   Codes.Directions.UpIsBetter),
            (Codes.Groups.Coverage,   "required_total",           Codes.Units.Count,   Codes.Directions.Neutral),
            (Codes.Groups.Terms,      "requested_amount",         Codes.Units.Amount,  Codes.Directions.Neutral),
            (Codes.Groups.Terms,      "installment",              Codes.Units.Amount,  Codes.Directions.Neutral),
            (Codes.Groups.Terms,      "fiadores",                 Codes.Units.Count,   Codes.Directions.Neutral),
        };
        Assert.Equal(expected,
            response.Groups!.SelectMany(g => g.Rows.Select(r => (g.Key, r.Key, r.Unit, r.Direction))).ToArray());

        Assert.Equal(Codes.Trends.Better, Row(response, Codes.Groups.Suggestion, "suggestion").Trend);

        var blocking = Row(response, Codes.Groups.Filters, "blocking_filters");
        AssertValues(blocking, 0m, 1m, -1m);
        Assert.Null(blocking.DeltaPct);
        Assert.Equal(Codes.Trends.Better, blocking.Trend);
        Assert.Equal(Codes.Trends.Better, Row(response, Codes.Groups.Filters, "warnings").Trend);

        var dscr = Row(response, Codes.Groups.Indicators, "cobertura_servicio_deuda");
        AssertValues(dscr, 1.88m, 1.2m);
        Assert.Equal(Codes.Trends.Better, dscr.Trend);

        var cuota = Row(response, Codes.Groups.Indicators, "cuota_ingreso");
        AssertValues(cuota, 25.12m, 40m, -14.88m);
        Assert.Equal(-37.2m, cuota.DeltaPct);
        Assert.Equal(Codes.Trends.Better, cuota.Trend);

        var solvencia = Row(response, Codes.Groups.Indicators, "solvencia_pn");
        AssertValues(solvencia, 25_000_000m, 20_000_000m, 5_000_000m);
        Assert.Equal(25.0m, solvencia.DeltaPct);
        Assert.Equal(Codes.Trends.NotAvailable, solvencia.Trend); // nominal pesos of two dates

        Assert.Equal(Codes.Trends.Better, Row(response, Codes.Groups.Indicators, "patrimonio_operativo").Trend);
        Assert.DoesNotContain(Group(response, Codes.Groups.Indicators)!.Rows, r => r.Key == "endeudamiento");
        Assert.DoesNotContain(Group(response, Codes.Groups.Indicators)!.Rows, r => r.Key == "cuota_ingreso_estres");

        var bcra = Row(response, Codes.Groups.Bcra, "worst_situation");
        AssertValues(bcra, 2m, 5m);
        Assert.Equal(Codes.Trends.Better, bcra.Trend);

        var index = Row(response, Codes.Groups.Coverage, "coverage_index");
        AssertValues(index, 75m, 50m);
        Assert.Equal(Codes.Trends.Better, index.Trend);
        Assert.Equal(Codes.Trends.Same, Row(response, Codes.Groups.Coverage, "required_total").Trend);

        var amount = Row(response, Codes.Groups.Terms, "requested_amount");
        AssertValues(amount, 5_000_000m, 5_000_000m, 0m);
        Assert.Equal(Codes.Trends.Same, amount.Trend); // an unchanged figure is "same" whatever its direction

        Assert.Equal([Codes.Notes.NominalAmounts], response.Notes);
        Assert.Equal("ARS", response.Current!.Currency);
        Assert.Equal("ARS", response.Previous!.Currency);
        Assert.Null(response.Current.Score);
        Assert.Null(response.Current.Level);
        Assert.Equal("pf-0.10", response.Current.PromptVersion);
    }

    [Theory]
    [InlineData(false, true,  "better")]
    [InlineData(true,  false, "worse")]
    [InlineData(false, false, "same")]
    [InlineData(true,  true,  "same")]
    public void Suggestion_trend_reads_from_rejection_to_the_analysts_decision(bool currentRejects, bool previousRejects, string trend)
    {
        var response = Build(
            Facts(f => { if (currentRejects) f.FiltersJson = RejectingFilters; }),
            Facts(f => { if (previousRejects) f.FiltersJson = RejectingFilters; })).Response;

        var row = Row(response, Codes.Groups.Suggestion, "suggestion");
        Assert.Equal(currentRejects ? PersonSuggestions.RechazoSugerido : PersonSuggestions.ParaDecisionDelAnalista, row.Current);
        Assert.Equal(previousRejects ? PersonSuggestions.RechazoSugerido : PersonSuggestions.ParaDecisionDelAnalista, row.Previous);
        Assert.Null(row.Delta);
        Assert.Null(row.DeltaPct);
        Assert.Equal(trend, row.Trend);
    }

    // ── BCRA ─────────────────────────────────────────────────────────────────

    [Fact]
    public void No_debt_reported_is_situacion_1_and_a_failed_lookup_is_null()
    {
        var noHistory = PersonEvaluationJson.Serialize(new List<PersonFilterResult>
        {
            Filter("bcra_situacion", PersonFilterOutcomes.Ok, new() { ["estado"] = "sin_historial" }),
        });
        var failed = PersonEvaluationJson.Serialize(new List<PersonFilterResult>
        {
            Filter("bcra_situacion", PersonFilterOutcomes.SinDatos),
        });

        var row = Row(Build(Facts(f => f.FiltersJson = noHistory), Facts(f => f.FiltersJson = failed)).Response,
            Codes.Groups.Bcra, "worst_situation");

        Assert.Equal(1m, row.Current);
        Assert.Null(row.Previous);
        Assert.Null(row.Delta);
        Assert.Equal(Codes.Trends.NotAvailable, row.Trend);
    }

    [Fact]
    public void Bcra_failed_on_both_sides_drops_the_group()
    {
        var failed = PersonEvaluationJson.Serialize(new List<PersonFilterResult>
        {
            Filter("bcra_situacion", PersonFilterOutcomes.SinDatos),
        });

        var response = Build(Facts(f => f.FiltersJson = failed), Facts(f => f.FiltersJson = failed)).Response;

        Assert.Null(Group(response, Codes.Groups.Bcra));
        Assert.NotNull(Group(response, Codes.Groups.Filters));
    }

    // ── Missing sides ─────────────────────────────────────────────────────────

    [Fact]
    public void An_indicator_one_side_lacks_reads_null_and_one_both_lack_is_dropped()
    {
        var previous = Facts(f => f.IndicatorsJson = PersonEvaluationJson.Serialize(new List<PersonIndicatorResult>
        {
            Indicator("cobertura_servicio_deuda", null), // SinDatos
            Indicator("cuota_ingreso", 0.3m),
        }));
        var current = Facts(f => f.IndicatorsJson = PersonEvaluationJson.Serialize(new List<PersonIndicatorResult>
        {
            Indicator("cobertura_servicio_deuda", 1.5m),
            Indicator("cuota_ingreso", 0.3m),
        }));

        var response = Build(current, previous).Response;
        var rows = Group(response, Codes.Groups.Indicators)!.Rows;

        var dscr = Assert.Single(rows, r => r.Key == "cobertura_servicio_deuda");
        Assert.Equal(1.5m, dscr.Current);
        Assert.Null(dscr.Previous);
        Assert.Null(dscr.Delta);
        Assert.Null(dscr.DeltaPct);
        Assert.Equal(Codes.Trends.NotAvailable, dscr.Trend);

        Assert.Equal(["cobertura_servicio_deuda", "cuota_ingreso"], rows.Select(r => r.Key));
        Assert.DoesNotContain(Codes.Notes.NominalAmounts, response.Notes!); // no pesos row left
    }

    [Fact]
    public void Default_and_blank_columns_are_absent_not_unreadable()
    {
        var empty = Facts(f =>
        {
            f.FiltersJson          = "[]";
            f.IndicatorsJson       = "  ";
            f.CoverageJson         = "{}";
            f.DocumentVersionsJson = "null";
            f.InstallmentAmount    = null;
        });

        var outcome = Build(empty, empty);

        // No filters, indicators or coverage on either side: only the terms remain.
        Assert.Empty(outcome.UnreadableKeys);
        Assert.Equal([Codes.Groups.Terms], outcome.Response.Groups!.Select(g => g.Key));
        Assert.DoesNotContain(Group(outcome.Response, Codes.Groups.Terms)!.Rows, r => r.Key == "installment");
    }

    // ── Terms and notes ───────────────────────────────────────────────────────

    [Fact]
    public void A_different_currency_keeps_the_amounts_but_nulls_their_deltas()
    {
        var response = Build(
            Facts(f => { f.Currency = "USD"; f.AmountAtCharge = 5_000m; f.InstallmentAmount = 300m; }),
            Facts()).Response;

        foreach (var key in new[] { "requested_amount", "installment" })
        {
            var row = Row(response, Codes.Groups.Terms, key);
            Assert.NotNull(row.Current);
            Assert.NotNull(row.Previous);
            Assert.Null(row.Delta);
            Assert.Null(row.DeltaPct);
            Assert.Equal(Codes.Trends.NotAvailable, row.Trend);
        }

        Assert.Equal(0m, Row(response, Codes.Groups.Terms, "fiadores").Delta);
        Assert.Equal("USD", response.Current!.Currency);
        Assert.Equal("ARS", response.Previous!.Currency);
        Assert.Contains(Codes.Notes.CurrencyChanged, response.Notes!);
        Assert.Contains(Codes.Notes.TermsChanged, response.Notes!);
    }

    [Fact]
    public void The_same_currency_compares_the_amounts()
    {
        var response = Build(Facts(f => f.AmountAtCharge = 6_000_000m), Facts()).Response;

        var row = Row(response, Codes.Groups.Terms, "requested_amount");
        Assert.Equal(1_000_000m, row.Delta);
        Assert.Equal(20.0m, row.DeltaPct);
        Assert.DoesNotContain(Codes.Notes.CurrencyChanged, response.Notes!);
    }

    [Theory]
    [InlineData("amount")]
    [InlineData("installment")]
    [InlineData("periodicity")]
    [InlineData("fiadores")]
    [InlineData("agro_periodicity")]
    public void Terms_changed_by_amount_installment_periodicity_or_fiadores(string change)
    {
        var current = Facts(f =>
        {
            switch (change)
            {
                case "amount":      f.AmountAtCharge    = 5_500_000m; break;
                case "installment": f.InstallmentAmount = 260_000m; break;
                case "periodicity": f.CuotasPorAnio     = 2; break;
                case "fiadores":    f.FiadorIds         = [new Guid("00000000-0000-0000-0000-0000000000f2")]; break;
                case "agro_periodicity":
                    f.CuotasPorAnio        = null;
                    f.DocumentVersionsJson = Sources(agroCuotasPorAnio: 1);
                    break;
            }
        });

        var response = Build(current, Facts()).Response;

        Assert.Contains(Codes.Notes.TermsChanged, response.Notes!);
        Assert.DoesNotContain(Codes.Notes.CurrencyChanged, response.Notes!);
    }

    [Fact]
    public void Same_terms_with_fiadores_in_another_order_and_a_null_periodicity_meaning_monthly_are_unchanged()
    {
        var f1 = new Guid("00000000-0000-0000-0000-0000000000f1");
        var f2 = new Guid("00000000-0000-0000-0000-0000000000f2");

        var response = Build(
            Facts(f => { f.FiadorIds = [f2, f1]; f.CuotasPorAnio = null; }),
            Facts(f => f.FiadorIds = [f1, f2])).Response;

        Assert.DoesNotContain(Codes.Notes.TermsChanged, response.Notes!);
    }

    [Fact]
    public void Engine_and_parameter_changes_are_separate_notes()
    {
        var stricter = PersonEvaluationParameters.Defaults with
        {
            BcraSituacionRechazo = PersonEvaluationParameters.Defaults.BcraSituacionRechazo - 1,
        };

        var engineOnly = Build(Facts(f => f.EvaluationVersion = "0.11"), Facts()).Response.Notes!;
        Assert.Contains(Codes.Notes.EngineChanged, engineOnly);
        Assert.DoesNotContain(Codes.Notes.ParametersChanged, engineOnly);

        var parametersOnly = Build(Facts(f => f.DocumentVersionsJson = Sources(stricter)), Facts()).Response.Notes!;
        Assert.Contains(Codes.Notes.ParametersChanged, parametersOnly);
        Assert.DoesNotContain(Codes.Notes.EngineChanged, parametersOnly);
    }

    [Fact]
    public void Where_a_parameter_came_from_is_not_a_parameter_change()
    {
        var sameValuesFromTenantRows = PersonEvaluationParameters.Defaults with { TenantOverrides = ["pf_bcra_sit_rechazo"] };

        var notes = Build(Facts(f => f.DocumentVersionsJson = Sources(sameValuesFromTenantRows)), Facts()).Response.Notes!;

        Assert.DoesNotContain(Codes.Notes.ParametersChanged, notes);
    }

    [Fact]
    public void Unknown_parameters_on_one_side_raise_no_parameter_note()
    {
        var notes = Build(Facts(f => f.DocumentVersionsJson = "{}"), Facts()).Response.Notes!;

        Assert.DoesNotContain(Codes.Notes.ParametersChanged, notes);
    }

    [Fact]
    public void Same_fiscal_year_is_never_a_persona_note()
    {
        var notes = Build(Facts(), Facts()).Response.Notes!;

        Assert.DoesNotContain(Codes.Notes.SameFiscalYear, notes);
        Assert.Equal([Codes.Notes.NominalAmounts], notes);
    }

    // ── Fault isolation ───────────────────────────────────────────────────────

    [Fact]
    public void An_unreadable_column_drops_its_groups_reports_its_key_and_never_throws()
    {
        var broken = Facts(f =>
        {
            f.FiltersJson          = "{not json";
            f.IndicatorsJson       = """{"id":"x"}""";        // an object where an array belongs
            f.CoverageJson         = """{"requiredTotal":"x"}""";
            f.DocumentVersionsJson = "[1,";
        });

        var outcome = Build(Facts(), broken);

        Assert.Equal(
            ["filters:previous", "indicators:previous", "coverage:previous", "parameters:previous"],
            outcome.UnreadableKeys);
        Assert.Equal([Codes.Groups.Terms], outcome.Response.Groups!.Select(g => g.Key));
        Assert.DoesNotContain(Codes.Notes.ParametersChanged, outcome.Response.Notes!);
    }

    [Fact]
    public void A_side_without_facts_is_a_programming_error()
    {
        var current = Snapshot(Facts(), 20);
        current.Person = null;

        Assert.Throws<ArgumentException>(() => AnalysisComparisonBuilder.BuildPersona(current, Snapshot(Facts(), 1)));
    }

    // ── Contract ──────────────────────────────────────────────────────────────

    [Fact]
    public void Every_emitted_string_is_from_the_code_vocabulary()
    {
        var response = Build(
            Facts(f => { f.Currency = "USD"; f.EvaluationVersion = "0.11"; }),
            Facts(f => f.FiltersJson = RejectingFilters)).Response;

        Assert.All(response.Notes!, n => Assert.Contains(n, Codes.Notes.All));
        Assert.All(response.Groups!, g => Assert.Contains(g.Key, Codes.Groups.Persona));
        Assert.All(response.Groups!.SelectMany(g => g.Rows), r =>
        {
            Assert.Contains(r.Unit, Codes.Units.All);
            Assert.Contains(r.Direction, Codes.Directions.All);
            Assert.Contains(r.Trend, Codes.Trends.All);
        });
    }

    [Fact]
    public void Serialises_with_currency_on_the_sides_and_text_for_the_suggestion()
    {
        var json = JsonSerializer.Serialize(Build(Facts(), Facts()).Response, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(
            ["analysisId", "resultId", "fiscalYear", "status", "decidedAt", "score", "level", "promptVersion", "currency"],
            root.GetProperty("current").EnumerateObject().Select(p => p.Name));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("sameFiscalYear").ValueKind);

        var suggestion = root.GetProperty("groups")[0].GetProperty("rows")[0];
        Assert.Equal(JsonValueKind.String, suggestion.GetProperty("current").ValueKind);
    }
}
