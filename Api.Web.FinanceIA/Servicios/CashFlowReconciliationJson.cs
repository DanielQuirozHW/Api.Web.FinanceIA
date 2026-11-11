// Project : Api.Web.FinanceIA
// Created : 2026-08-08

using System.Text.Json.Nodes;

namespace Api.Web.FinanceIA.Servicios;

// ── Helper ──────────────────────────────────────────────────────────────────────

/// <summary>
/// Injects a structured cash-flow reconciliation outcome into an already-serialized BalanceJson
/// string, under the existing <c>cash_flow</c> node, so the Portal can warn on the cash-flow card
/// without parsing prose or recomputing tolerances client-side.
///
/// Fault-isolated by design: any problem (null/blank/unparseable input, no <c>cash_flow</c> node)
/// returns the input string UNCHANGED and never throws — it must never break persistence.
/// </summary>
public static class CashFlowReconciliationJson
{
    /// <summary>
    /// Returns <paramref name="balanceJson"/> with a <c>reconciliation</c> object added under the
    /// existing <c>cash_flow</c> node. When <c>cash_flow</c> is absent, or the input is
    /// null/blank/unparseable, the input is returned UNCHANGED (byte-identical). Never throws.
    /// The re-serialization uses default <see cref="System.Text.Json.JsonSerializer"/> options so the
    /// untouched parts of the blob stay byte-consistent with how ClaudeService originally wrote it.
    /// </summary>
    public static string? Inject(string? balanceJson, CashFlowReconciliationResult reconciliation)
    {
        if (string.IsNullOrWhiteSpace(balanceJson))
            return balanceJson;

        try
        {
            if (JsonNode.Parse(balanceJson) is not JsonObject root)
                return balanceJson;

            if (root["cash_flow"] is not JsonObject cashFlow)
                return balanceJson; // no statement node → leave the string exactly as it was

            cashFlow["reconciliation"] = BuildNode(reconciliation);
            return root.ToJsonString();
        }
        catch (Exception)
        {
            // Fault-isolated: a malformed blob must never prevent persistence of the analysis.
            return balanceJson;
        }
    }

    private static JsonObject BuildNode(CashFlowReconciliationResult r) => new()
    {
        [CashFlowReconciliationService.SectionsVsNetChange]      = CheckNode(r.SectionsVsNetChange),
        [CashFlowReconciliationService.BeginningPlusChangeVsEnd] = CheckNode(r.BeginningPlusChangeVsEnd),
    };

    private static JsonObject CheckNode(CashFlowCheckResult c) => new()
    {
        ["state"]    = StateToken(c.State),
        ["expected"] = c.Expected is { } e ? JsonValue.Create(e) : null,
        ["reported"] = c.Reported is { } r ? JsonValue.Create(r) : null,
    };

    private static string StateToken(ReconciliationState state) => state switch
    {
        ReconciliationState.Passed => "passed",
        ReconciliationState.Failed => "failed",
        _                          => "not_evaluated",
    };
}
