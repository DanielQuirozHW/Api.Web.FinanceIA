// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-03

using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.BalanceCorrections;
using Xunit;

namespace Api.Web.FinanceIA.Tests.BalanceCorrections;

/// <summary>
/// <c>origin</c> of a balance correction (10-F4a): a closed vocabulary on the request, written on every
/// manual_corrections entry, read back per entry by <see cref="ManualCorrectionsReader"/>; and the dry run
/// the suggestion verifies with, which writes nothing.
/// </summary>
public sealed class BalanceCorrectionOriginTests
{
    private static readonly DateTime At = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Analyst = Guid.NewGuid();

    [Theory]
    [InlineData(null, BalanceCorrectionOrigins.Manual)]
    [InlineData("manual", BalanceCorrectionOrigins.Manual)]
    [InlineData("ai_suggestion", BalanceCorrectionOrigins.AiSuggestion)]
    public void A_known_or_absent_origin_is_accepted(string? origin, string expected)
    {
        var (command, errors) = BalanceCorrectionValidator.ValidateRequest(Request(origin));

        Assert.Empty(errors);
        Assert.Equal(expected, command!.Origin);
    }

    [Theory]
    [InlineData("AI_SUGGESTION")]
    [InlineData("")]
    [InlineData(" manual")]
    [InlineData("ia")]
    public void Any_other_origin_is_a_validation_error_on_origin(string origin)
    {
        var (command, errors) = BalanceCorrectionValidator.ValidateRequest(Request(origin));

        Assert.Null(command);
        var error = Assert.Single(errors);
        Assert.Equal("origin", error.Field);
    }

    [Fact]
    public void The_applier_writes_the_origin_on_each_entry_and_the_reader_reads_it_back()
    {
        var command = BalanceCorrectionFixtures.FinexCommand() with { Origin = BalanceCorrectionOrigins.AiSuggestion };

        var applied = BalanceCorrectionApplier.Apply(
            BalanceCorrectionFixtures.FinexBalanceJson(), command, Analyst, At, new BalanceIntegrityService());

        var entry = Assert.Single(JsonNode.Parse(applied.BalanceJson!)!["manual_corrections"]!.AsArray())!;
        Assert.Equal(BalanceCorrectionOrigins.AiSuggestion, entry["origin"]!.GetValue<string>());

        var summary = ManualCorrectionsReader.Read(applied.BalanceJson)!;
        var read    = Assert.Single(summary.Entries);
        Assert.Equal(BalanceCorrectionFixtures.MisreadPath, read.Path);
        Assert.Equal(At, read.At);
        Assert.Equal(Analyst, read.By);
        Assert.Equal(BalanceCorrectionOrigins.AiSuggestion, summary.OriginOf(BalanceCorrectionFixtures.MisreadPath, Analyst, At));
        Assert.Equal(BalanceCorrectionOrigins.Manual, summary.OriginOf(BalanceCorrectionFixtures.MisreadPath, Guid.NewGuid(), At));
        Assert.Equal(BalanceCorrectionOrigins.Manual, summary.OriginOf(BalanceCorrectionFixtures.MisreadPath, Analyst, At.AddTicks(1)));
    }

    [Fact]
    public void An_entry_without_or_with_an_unknown_origin_reads_as_manual()
    {
        var root = JsonNode.Parse(BalanceCorrectionFixtures.FinexBalanceJson())!.AsObject();
        root[BalanceCorrectionApplier.ManualCorrectionsKey] = new JsonArray
        {
            new JsonObject { ["path"] = "equity", ["by"] = Analyst.ToString(), ["at"] = At.ToString("O") },
            new JsonObject { ["path"] = "assets", ["by"] = Analyst.ToString(), ["at"] = At.ToString("O"), ["origin"] = "robot" },
        };

        var summary = ManualCorrectionsReader.Read(root.ToJsonString())!;

        Assert.All(summary.Entries, e => Assert.Equal(BalanceCorrectionOrigins.Manual, e.Origin));
    }

    [Fact]
    public void The_dry_run_reports_the_corrected_integrity_and_writes_no_trail()
    {
        var json = BalanceCorrectionFixtures.FinexBalanceJson();

        var baseline = BalanceCorrectionApplier.DryRun(json, [], new BalanceIntegrityService());
        var fixedRun = BalanceCorrectionApplier.DryRun(json, BalanceCorrectionFixtures.FinexCommand().Corrections, new BalanceIntegrityService());

        Assert.False(baseline.Report!.Passed);
        Assert.True(fixedRun.Report!.Passed);
        Assert.Null(fixedRun.Report.ManuallyCorrected);   // a dry run marks nothing
        Assert.DoesNotContain(BalanceCorrectionApplier.ManualCorrectionsKey, json);
    }

    [Fact]
    public void The_dry_run_refuses_an_unchanged_value_and_an_unreadable_balance()
    {
        var unchanged = BalanceCorrectionApplier.DryRun(
            BalanceCorrectionFixtures.FinexBalanceJson(),
            BalanceCorrectionFixtures.Command("equity", 300_000_000d).Corrections,
            new BalanceIntegrityService());

        Assert.Null(unchanged.Report);
        Assert.Single(unchanged.Errors);
        Assert.True(BalanceCorrectionApplier.DryRun("[]", [], new BalanceIntegrityService()).Unreadable);
    }

    private static BalanceCorrectionRequest Request(string? origin) => new()
    {
        Reason      = BalanceCorrectionFixtures.Reason,
        Corrections = [new BalanceCorrectionItemRequest { Path = "equity", Value = JsonDocument.Parse("1").RootElement.Clone() }],
        Origin      = origin,
    };
}
