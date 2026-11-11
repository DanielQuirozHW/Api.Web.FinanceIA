// Project : Api.Web.FinanceIA
// Created : 2026-10-19

using System.Text.Json;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.AnalysisRuns;

/// <summary>
/// JSON helpers shared by <see cref="AnalysisPipeline"/>, which writes an analysis, and
/// AnalysisController, which reads it back. Moved here verbatim from the controller when the long part
/// of POST /api/analyses became a service, so both sides keep reading the persisted blobs with the
/// SAME options — the reason they were one copy in the first place.
/// </summary>
internal static class AnalysisPayloadJson
{
    internal static readonly JsonSerializerOptions BcraJsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Defensively extracts the cash_flow section from a persisted BalanceJson blob. Returns null
    /// when the blob is absent, malformed, not a JSON OBJECT, or has no cash_flow key at all
    /// (analyses predating the feature) — never throws, so the chat endpoint and reconciliation
    /// both degrade gracefully.
    /// </summary>
    internal static CashFlowData? TryExtractCashFlow(string? balanceJson)
    {
        if (string.IsNullOrWhiteSpace(balanceJson)) return null;

        try
        {
            using var doc = JsonDocument.Parse(balanceJson);

            // Guarded here rather than left to the catch below: TryGetProperty on a NON-OBJECT root
            // — the literal `null`, a bare scalar, a top-level array, all of which parse cleanly —
            // throws InvalidOperationException, which is NOT a JsonException and would escape,
            // breaking the "never throws" contract above. That matters at the one call site with no
            // try/catch of its own: BuildChatSystemPrompt, reached from ChatAsync AFTER the
            // analyst's turn is already persisted, where a throw strands a user message with no
            // assistant reply and surfaces as a 500. Same guard, same reason, as
            // BuildBcraChatSummary.
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

            if (doc.RootElement.TryGetProperty("cash_flow", out var cashFlowElement) &&
                cashFlowElement.ValueKind == JsonValueKind.Object)
            {
                return cashFlowElement.Deserialize<CashFlowData>(BcraJsonOpts);
            }
        }
        catch (JsonException)
        {
            // Malformed BalanceJson — degrade to "No disponible" / no reconciliation.
        }

        return null;
    }

    // Options for the LUFE/Fiscal snapshot strings. These blobs are copied verbatim from
    // Entity.LufeDataJson / Entity.FiscalDataJson, so they must be read with the same
    // options EntitiesController uses when serialising/deserialising them — including
    // AllowReadingFromString so numeric fields stored as strings still parse.
    internal static readonly JsonSerializerOptions SnapshotJsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling              = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    internal static Models.Lufe.LufeData? TryDeserializeLufeData(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Models.Lufe.LufeData>(json, SnapshotJsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static Models.Financia.CasfogData? TryDeserializeCasfogData(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Models.Financia.CasfogData>(json, SnapshotJsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal sealed class ClaudeAlertJson
    {
        [System.Text.Json.Serialization.JsonPropertyName("level")]
        public string? Level { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("code")]
        public string? Code { get; set; }

        // Short es-AR title emitted by the model (prompt v2.7+). Declared so the
        // reconciliation round-trip (deserialise → mutate Description → re-serialise)
        // preserves it, and so persistence can prefer it over HumanizeCode. Older
        // analyses persisted before v2.7 have no "title" key → null → HumanizeCode fallback.
        [System.Text.Json.Serialization.JsonPropertyName("title")]
        public string? Title { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("description")]
        public string? Description { get; set; }
    }
}
