// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-02

using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.BalanceCorrections;
using Xunit;

namespace Api.Web.FinanceIA.Tests.BalanceCorrections;

/// <summary>
/// The validation of POST /api/analyses/{resultId}/balance-corrections: the closed path whitelist, the
/// value rules, the reason and count bounds, and the balance rules (the line exists, the value changes).
/// Messages are es-AR, name the field and the path, and never quote a value.
/// </summary>
public sealed class BalanceCorrectionValidatorTests
{
    private const string GoodReason = "La cifra fue mal leída del balance escaneado.";

    // ── Whitelist ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("current_assets")]
    [InlineData("non_current_assets")]
    [InlineData("assets")]
    [InlineData("current_liabilities")]
    [InlineData("non_current_liabilities")]
    [InlineData("liabilities")]
    [InlineData("equity")]
    [InlineData("net_revenue")]
    [InlineData("net_profit")]
    [InlineData("operating_result")]
    [InlineData("financial_expenses")]
    [InlineData("inventory")]
    [InlineData("prepaid_expenses")]
    [InlineData("result_before_tax")]
    [InlineData("income_tax")]
    [InlineData("current_assets_items[0].amount")]
    [InlineData("current_liabilities_items[12].amount")]
    [InlineData("non_current_liabilities_items[999].amount")]
    public void Every_whitelisted_path_is_accepted(string path)
    {
        var (command, errors) = Validate(Request(path, "123.5"));

        Assert.Empty(errors);
        Assert.Equal(path, Assert.Single(command!.Corrections).Path.Raw);
    }

    [Fact]
    public void The_whitelist_is_exactly_the_15_figures_and_the_3_arrays()
    {
        Assert.Equal(15, BalanceCorrectionPath.TopLevelKeys.Count);
        Assert.Equal(["result_before_tax", "income_tax"], BalanceCorrectionPath.TopLevelKeys.Skip(13));
        Assert.Equal(
            ["current_assets_items", "current_liabilities_items", "non_current_liabilities_items"],
            BalanceCorrectionPath.ItemArrays);
    }

    [Theory]
    [InlineData("balance_check.current_assets_items[0].amount")]   // no such node in the persisted blob
    [InlineData("current_assets_items[0].label")]                  // labels are not editable
    [InlineData("current_assets_items[0]")]
    [InlineData("current_assets_items[-1].amount")]
    [InlineData("current_assets_items[01].amount")]
    [InlineData("current_assets_items[1000].amount")]
    [InlineData("prior_year.assets")]
    [InlineData("cash_flow.cash_end")]
    [InlineData("integrity")]
    [InlineData("manual_corrections")]
    [InlineData("amounts_scale")]
    [InlineData("Current_Assets")]
    [InlineData(" current_assets")]
    [InlineData("/current_assets")]
    [InlineData("")]
    public void A_path_outside_the_whitelist_is_refused_on_its_field(string path)
    {
        var (command, errors) = Validate(Request(path, "1"));

        Assert.Null(command);
        var error = Assert.Single(errors);
        Assert.Equal("corrections[0].path", error.Field);
    }

    [Fact]
    public void A_duplicated_path_is_refused()
    {
        var request = Request("equity", "1");
        request.Corrections!.Add(Item("equity", "2"));

        var (command, errors) = Validate(request);

        Assert.Null(command);
        Assert.Equal("corrections[1].path", Assert.Single(errors).Field);
    }

    // ── Values ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("\"123\"")]
    [InlineData("\"NaN\"")]
    [InlineData("\"Infinity\"")]
    [InlineData("\"1.234.567\"")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[1]")]
    public void A_non_numeric_value_is_refused_on_its_field(string json)
    {
        var (command, errors) = Validate(Request("equity", json));

        Assert.Null(command);
        var error = Assert.Single(errors);
        Assert.Equal("corrections[0].value", error.Field);
        Assert.Contains("equity", error.Message);
        Assert.Contains("número", error.Message);
    }

    [Theory]
    [InlineData("1e400")]
    [InlineData("-1e400")]
    [InlineData("1e16")]
    public void A_non_finite_or_out_of_range_value_is_refused(string json)
    {
        var (command, errors) = Validate(Request("equity", json));

        Assert.Null(command);
        Assert.Equal("corrections[0].value", Assert.Single(errors).Field);
    }

    [Fact]
    public void A_missing_value_is_refused()
    {
        var request = new BalanceCorrectionRequest
        {
            Reason      = GoodReason,
            Corrections = [new BalanceCorrectionItemRequest { Path = "equity" }]
        };

        var (command, errors) = Validate(request);

        Assert.Null(command);
        Assert.Equal("corrections[0].value", Assert.Single(errors).Field);
    }

    [Fact]
    public void Null_clears_a_figure_and_negative_zero_is_zero()
    {
        var (nulled, _) = Validate(Request("prepaid_expenses", "null"));
        Assert.Null(Assert.Single(nulled!.Corrections).Value);

        var (zero, _) = Validate(Request("net_profit", "-0"));
        var value = Assert.Single(zero!.Corrections).Value!.Value;
        Assert.False(double.IsNegative(value));
    }

    [Fact]
    public void The_net_profit_reconciliation_inputs_take_a_negative_value_or_null()
    {
        // A tax charge is negative as printed; a figure the reading invented can be cleared.
        var (tax, taxErrors) = Validate(Request("income_tax", "-30000000"));
        Assert.Empty(taxErrors);
        Assert.Equal(-30_000_000d, Assert.Single(tax!.Corrections).Value);

        var (cleared, clearedErrors) = Validate(Request("result_before_tax", "null"));
        Assert.Empty(clearedErrors);
        Assert.Null(Assert.Single(cleared!.Corrections).Value);
    }

    [Theory]
    [InlineData("result_before_tax", "1e400")]
    [InlineData("income_tax", "-1e400")]
    [InlineData("income_tax", "1e16")]
    [InlineData("result_before_tax", "\"90000000\"")]
    public void A_net_profit_reconciliation_input_must_be_a_finite_number_in_range(string path, string json)
    {
        var (command, errors) = Validate(Request(path, json));

        Assert.Null(command);
        Assert.Equal("corrections[0].value", Assert.Single(errors).Field);
    }

    [Fact]
    public void A_negative_figure_is_a_legitimate_value()
    {
        var (command, errors) = Validate(Request("net_profit", "-1500000.75"));

        Assert.Empty(errors);
        Assert.Equal(-1_500_000.75, Assert.Single(command!.Corrections).Value);
    }

    // ── Reason and count ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("corto")]
    [InlineData("    nueve     ")]       // 5 characters once trimmed
    public void A_short_reason_is_refused(string? reason)
    {
        var request = Request("equity", "1");
        request.Reason = reason;

        var (command, errors) = Validate(request);

        Assert.Null(command);
        Assert.Equal("reason", Assert.Single(errors).Field);
    }

    [Fact]
    public void The_reason_is_trimmed_and_bounded_at_1000()
    {
        var request = Request("equity", "1");
        request.Reason = "  " + new string('a', 1000) + "  ";
        var (accepted, _) = Validate(request);
        Assert.Equal(1000, accepted!.Reason.Length);

        request.Reason = new string('a', 1001);
        var (refused, errors) = Validate(request);
        Assert.Null(refused);
        Assert.Equal("reason", Assert.Single(errors).Field);
    }

    [Fact]
    public void No_corrections_and_more_than_30_are_refused()
    {
        var empty = new BalanceCorrectionRequest { Reason = GoodReason, Corrections = [] };
        Assert.Equal("corrections", Assert.Single(Validate(empty).Errors).Field);

        var missing = new BalanceCorrectionRequest { Reason = GoodReason };
        Assert.Equal("corrections", Assert.Single(Validate(missing).Errors).Field);

        var tooMany = new BalanceCorrectionRequest
        {
            Reason      = GoodReason,
            Corrections = Enumerable.Range(0, 31).Select(i => Item($"current_assets_items[{i}].amount", "1")).ToList()
        };
        Assert.Equal("corrections", Assert.Single(Validate(tooMany).Errors).Field);

        var thirty = new BalanceCorrectionRequest
        {
            Reason      = GoodReason,
            Corrections = Enumerable.Range(0, 30).Select(i => Item($"current_assets_items[{i}].amount", "1")).ToList()
        };
        Assert.Empty(Validate(thirty).Errors);
    }

    [Fact]
    public void Every_error_of_one_request_is_reported_together()
    {
        var request = new BalanceCorrectionRequest
        {
            Reason      = "x",
            Corrections = [Item("nope", "1"), Item("equity", "\"1\"")]
        };

        var (_, errors) = Validate(request);

        Assert.Equal(["reason", "corrections[0].path", "corrections[1].value"], errors.Select(e => e.Field));
    }

    // ── Against the stored balance ──────────────────────────────────────────────

    [Fact]
    public void An_item_index_beyond_the_stored_array_is_refused()
    {
        var balance = Balance();
        var errors  = BalanceCorrectionValidator.ValidateAgainstBalance(
            balance, BalanceCorrectionFixtures.Command("current_assets_items[4].amount", 1d));

        var error = Assert.Single(errors);
        Assert.Equal("corrections[0].path", error.Field);
        Assert.Contains("current_assets_items[4].amount", error.Message);
        Assert.Contains("renglón", error.Message);
    }

    [Fact]
    public void An_item_of_an_array_the_balance_does_not_carry_is_refused()
    {
        var balance = Balance();
        balance.Remove("non_current_liabilities_items");

        var errors = BalanceCorrectionValidator.ValidateAgainstBalance(
            balance, BalanceCorrectionFixtures.Command("non_current_liabilities_items[0].amount", 1d));

        Assert.Equal("corrections[0].path", Assert.Single(errors).Field);
    }

    [Fact]
    public void A_value_equal_to_the_stored_one_is_sin_cambios()
    {
        var balance = Balance();

        var same = BalanceCorrectionValidator.ValidateAgainstBalance(
            balance, BalanceCorrectionFixtures.Command("equity", 300_000_000d));
        var error = Assert.Single(same);
        Assert.Equal("corrections[0].value", error.Field);
        Assert.Contains("sin cambios", error.Message);

        var item = BalanceCorrectionValidator.ValidateAgainstBalance(
            balance, BalanceCorrectionFixtures.Command(BalanceCorrectionFixtures.MisreadPath, BalanceCorrectionFixtures.MisreadAmount));
        Assert.Contains("sin cambios", Assert.Single(item).Message);

        var bothNull = BalanceCorrectionValidator.ValidateAgainstBalance(
            balance, BalanceCorrectionFixtures.Command("prepaid_expenses", null));
        Assert.Contains("sin cambios", Assert.Single(bothNull).Message);

        Assert.Empty(BalanceCorrectionValidator.ValidateAgainstBalance(
            balance, BalanceCorrectionFixtures.Command("prepaid_expenses", 0d)));
    }

    [Fact]
    public void A_missing_top_level_key_reads_as_null()
    {
        var balance = Balance();
        balance.Remove("prepaid_expenses");

        Assert.Empty(BalanceCorrectionValidator.ValidateAgainstBalance(
            balance, BalanceCorrectionFixtures.Command("prepaid_expenses", 10d)));
        Assert.Single(BalanceCorrectionValidator.ValidateAgainstBalance(
            balance, BalanceCorrectionFixtures.Command("prepaid_expenses", null)));
    }

    [Fact]
    public void A_balance_read_before_the_tax_lines_existed_can_gain_them()
    {
        // A blob older than prompt 2.18 carries no result_before_tax / income_tax: they read as null.
        var balance = Balance();
        balance.Remove("result_before_tax");
        balance.Remove("income_tax");

        Assert.Empty(BalanceCorrectionValidator.ValidateAgainstBalance(
            balance, BalanceCorrectionFixtures.Command("result_before_tax", 90_000_000d)));
        Assert.Empty(BalanceCorrectionValidator.ValidateAgainstBalance(
            balance, BalanceCorrectionFixtures.Command("income_tax", -30_000_000d)));
    }

    [Fact]
    public void Messages_name_the_path_and_never_quote_a_value_or_the_reason()
    {
        var secretReason = "Motivo con CUIT 30712345678 que no debe salir.";
        var request = new BalanceCorrectionRequest
        {
            Reason      = secretReason,
            Corrections = [Item("equity", "\"987654321\""), Item("net_profit", "1e400")]
        };

        var (_, errors) = Validate(request);

        Assert.Equal(2, errors.Count);
        foreach (var error in errors)
        {
            Assert.DoesNotContain("987654321", error.Message);
            Assert.DoesNotContain("30712345678", error.Message);
        }
        Assert.Contains("equity", errors[0].Message);
        Assert.Contains("net_profit", errors[1].Message);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static (BalanceCorrectionCommand? Command, IReadOnlyList<Api.Web.FinanceIA.Clases.Exceptions.ValidationError> Errors)
        Validate(BalanceCorrectionRequest request) => BalanceCorrectionValidator.ValidateRequest(request);

    private static BalanceCorrectionRequest Request(string path, string valueJson)
        => new() { Reason = GoodReason, Corrections = [Item(path, valueJson)] };

    private static BalanceCorrectionItemRequest Item(string path, string valueJson)
        => new() { Path = path, Value = JsonDocument.Parse(valueJson).RootElement.Clone() };

    private static JsonObject Balance()
        => JsonNode.Parse(BalanceCorrectionFixtures.FinexBalanceJson())!.AsObject();
}
