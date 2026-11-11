// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// The EFFECTIVE values of an extraction: the run's ResultJson with its LegajoCorrections applied in
/// order, the latest winning (M10 Phase 2b). Pure — the run and the corrections are never modified;
/// the result is a fresh tree.
///
/// <para>
/// A correction names a JSON Pointer the schema admits (see <see cref="LegajoCorrectionValidator"/>):
/// a provenance field (its <c>value</c> is replaced; page and quote stay the model's, as the record of
/// what was read), a plain value inside a row, a row's match-key object, or a row index equal to the
/// array's length (a row the model missed, appended). A correction whose path no longer resolves —
/// only possible with hand-edited rows — is skipped, never fatal.
/// </para>
/// </summary>
public static class LegajoEffectiveValues
{
    /// <summary>Applies <paramref name="corrections"/> (in CorrectedAt order) to a copy of <paramref name="resultJson"/>.</summary>
    /// <returns>The effective tree, and the paths that were corrected (in order, distinct).</returns>
    public static (JsonObject Effective, IReadOnlyList<string> CorrectedPaths) Apply(
        LegajoExtractionSchema schema, string resultJson, IEnumerable<LegajoCorrection> corrections)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(corrections);

        var effective = JsonNode.Parse(resultJson) as JsonObject ?? new JsonObject();
        var corrected = new List<string>();

        foreach (var correction in corrections.OrderBy(c => c.CorrectedAt))
        {
            if (!TryApply(schema, effective, correction.FieldPath, Parse(correction.CorrectedValueJson)))
                continue;

            if (!corrected.Contains(correction.FieldPath))
                corrected.Add(correction.FieldPath);
        }

        return (effective, corrected);
    }

    /// <summary>The value a field currently holds in <paramref name="effective"/> (a field's <c>value</c>, or the node itself).</summary>
    public static JsonNode? ValueAt(LegajoExtractionSchema schema, JsonObject effective, string fieldPath)
    {
        var node = LegajoSchemaResolver.Resolve(schema.Root, fieldPath, out _);
        return node is LegajoSchemaField
            ? LegajoJsonPointer.Get(effective, fieldPath + "/value")
            : LegajoJsonPointer.Get(effective, fieldPath);
    }

    /// <summary>Applies one value at one path. False when the path does not resolve in the schema or the data.</summary>
    internal static bool TryApply(LegajoExtractionSchema schema, JsonObject effective, string fieldPath, JsonNode? value)
    {
        var node = LegajoSchemaResolver.Resolve(schema.Root, fieldPath, out _);
        return node switch
        {
            LegajoSchemaField => LegajoJsonPointer.Get(effective, fieldPath, out var found) is JsonObject && found
                                 && LegajoJsonPointer.Set(effective, fieldPath + "/value", value?.DeepClone()),
            LegajoSchemaValue or LegajoSchemaObject => LegajoJsonPointer.Set(effective, fieldPath, value?.DeepClone()),
            _ => false
        };
    }

    private static JsonNode? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
