// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-08

using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Servicios;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

// ── Test class ────────────────────────────────────────────────────────────────

/// <summary>
/// Covers <see cref="CashFlowReconciliationJson.Inject"/>: it adds a "reconciliation" object under
/// the existing "cash_flow" node of an already-serialized BalanceJson, and is fault-isolated — any
/// problem leaves the input string byte-identical and never throws.
/// </summary>
public sealed class CashFlowReconciliationJsonTests
{
    // A minimal but realistic BalanceJson with a cash_flow node and NO reconciliation key yet.
    private const string BalanceWithCashFlow =
        "{\"assets\":1000,\"cash_flow\":{\"present\":true,\"net_change\":30,\"operating\":100," +
        "\"investing\":-40,\"financing\":-30,\"cash_beginning\":200,\"cash_end\":230,\"components\":null}}";

    private static CashFlowReconciliationResult Result(
        ReconciliationState sState, double? sExp, double? sRep,
        ReconciliationState bState, double? bExp, double? bRep) =>
        new(
            new CashFlowCheckResult(CashFlowReconciliationService.SectionsVsNetChange, sState, sExp, sRep),
            new CashFlowCheckResult(CashFlowReconciliationService.BeginningPlusChangeVsEnd, bState, bExp, bRep));

    private static JsonObject Reconciliation(string balanceJson)
    {
        var root = JsonNode.Parse(balanceJson)!.AsObject();
        var cashFlow = root["cash_flow"]!.AsObject();
        return cashFlow["reconciliation"]!.AsObject();
    }

    [Fact]
    public void Inject_WhenBothPassed_WritesBothKeysWithFigures()
    {
        var result = Result(
            ReconciliationState.Passed, 30, 30,
            ReconciliationState.Passed, 230, 230);

        var reconciliation = Reconciliation(CashFlowReconciliationJson.Inject(BalanceWithCashFlow, result)!);

        var sections = reconciliation[CashFlowReconciliationService.SectionsVsNetChange]!.AsObject();
        Assert.Equal("passed", sections["state"]!.GetValue<string>());
        Assert.Equal(30.0, sections["expected"]!.GetValue<double>(), 3);
        Assert.Equal(30.0, sections["reported"]!.GetValue<double>(), 3);

        var beginning = reconciliation[CashFlowReconciliationService.BeginningPlusChangeVsEnd]!.AsObject();
        Assert.Equal("passed", beginning["state"]!.GetValue<string>());
        Assert.Equal(230.0, beginning["expected"]!.GetValue<double>(), 3);
        Assert.Equal(230.0, beginning["reported"]!.GetValue<double>(), 3);
    }

    [Fact]
    public void Inject_WhenFailed_WritesFailedStateWithBothFigures()
    {
        var result = Result(
            ReconciliationState.Failed, 30, 50,
            ReconciliationState.Passed, 230, 230);

        var sections = Reconciliation(CashFlowReconciliationJson.Inject(BalanceWithCashFlow, result)!)
            [CashFlowReconciliationService.SectionsVsNetChange]!.AsObject();

        Assert.Equal("failed", sections["state"]!.GetValue<string>());
        Assert.Equal(30.0, sections["expected"]!.GetValue<double>(), 3);
        Assert.Equal(50.0, sections["reported"]!.GetValue<double>(), 3);
    }

    [Fact]
    public void Inject_WhenNotEvaluated_WritesNullFigures()
    {
        var result = Result(
            ReconciliationState.NotEvaluated, null, null,
            ReconciliationState.NotEvaluated, null, null);

        var sections = Reconciliation(CashFlowReconciliationJson.Inject(BalanceWithCashFlow, result)!)
            [CashFlowReconciliationService.SectionsVsNetChange]!.AsObject();

        Assert.Equal("not_evaluated", sections["state"]!.GetValue<string>());
        Assert.True(sections.ContainsKey("expected"));
        Assert.True(sections.ContainsKey("reported"));
        Assert.Null(sections["expected"]);
        Assert.Null(sections["reported"]);
    }

    [Fact]
    public void Inject_WhenCashFlowPresentFalse_WritesNodeWithBothNotEvaluatedAndAllFiguresNull()
    {
        // A cash_flow node exists but present:false with all figures null → both checks NotEvaluated.
        const string presentFalse =
            "{\"assets\":1000,\"cash_flow\":{\"present\":false,\"net_change\":null,\"operating\":null," +
            "\"investing\":null,\"financing\":null,\"cash_beginning\":null,\"cash_end\":null,\"components\":null}}";

        var result = Result(
            ReconciliationState.NotEvaluated, null, null,
            ReconciliationState.NotEvaluated, null, null);

        var reconciliation = Reconciliation(CashFlowReconciliationJson.Inject(presentFalse, result)!);

        foreach (var key in new[]
                 {
                     CashFlowReconciliationService.SectionsVsNetChange,
                     CashFlowReconciliationService.BeginningPlusChangeVsEnd,
                 })
        {
            var check = reconciliation[key]!.AsObject();
            Assert.Equal("not_evaluated", check["state"]!.GetValue<string>());
            Assert.Null(check["expected"]);
            Assert.Null(check["reported"]);
        }
    }

    [Fact]
    public void Inject_WhenNoCashFlowNode_ReturnsInputByteIdentical()
    {
        const string noCashFlow = "{\"assets\":1000,\"liabilities\":400}";
        var result = Result(
            ReconciliationState.Passed, 1, 1,
            ReconciliationState.Passed, 1, 1);

        var output = CashFlowReconciliationJson.Inject(noCashFlow, result);

        Assert.Same(noCashFlow, output); // untouched — not even re-serialized
    }

    [Fact]
    public void Inject_WhenNull_ReturnsNullWithoutThrowing()
    {
        var result = Result(
            ReconciliationState.NotEvaluated, null, null,
            ReconciliationState.NotEvaluated, null, null);

        Assert.Null(CashFlowReconciliationJson.Inject(null, result));
    }

    [Fact]
    public void Inject_WhenMalformed_ReturnsInputUnchangedWithoutThrowing()
    {
        const string malformed = "{ this is not json";
        var result = Result(
            ReconciliationState.Passed, 1, 1,
            ReconciliationState.Passed, 1, 1);

        var output = CashFlowReconciliationJson.Inject(malformed, result);

        Assert.Same(malformed, output);
    }

    [Fact]
    public void Inject_IsAdditiveOnly_EverythingExceptReconciliationIsUnchanged()
    {
        var result = Result(
            ReconciliationState.Failed, 30, 50,
            ReconciliationState.Passed, 230, 230);

        var output = CashFlowReconciliationJson.Inject(BalanceWithCashFlow, result)!;

        // Remove the one key we added; the rest must be structurally identical to the input.
        var outNode = JsonNode.Parse(output)!.AsObject();
        outNode["cash_flow"]!.AsObject().Remove("reconciliation");

        var inNode = JsonNode.Parse(BalanceWithCashFlow)!;
        Assert.Equal(inNode.ToJsonString(), outNode.ToJsonString());
    }
}
