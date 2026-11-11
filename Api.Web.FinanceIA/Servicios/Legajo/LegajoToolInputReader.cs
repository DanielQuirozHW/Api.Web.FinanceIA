// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// Reads an extraction tool input against its <see cref="LegajoSchemaObject"/> tree and produces the
/// normalized ResultJson tree. This is the FULL validation the non-strict tool call relies on. Pure.
///
/// <para>
/// A structural problem is a violation and makes the output unusable (the pass is retried as images):
/// a wrong JSON type, an unknown enum value, a malformed date, a non-object where an object belongs.
/// A lenient reading applies where nothing is lost: a missing leaf is null, a missing array is empty,
/// a bare value where a <c>{value, page, quote, confidence}</c> object belongs keeps the value with no
/// provenance and confidence 0 (so it lands in the review queue), a page below 1 is null, a confidence
/// outside 0..1 is clamped, quotes and texts are truncated.
/// </para>
///
/// <para>
/// Violations name the pointer and a code, never the value read.
/// </para>
/// </summary>
public static class LegajoToolInputReader
{
    /// <summary>Longest quote kept.</summary>
    public const int MaxQuoteLength = 300;

    /// <summary>
    /// The largest amount (or percentage) accepted, in absolute value, in the PRINTED unit: 10^15.
    /// Even scaled by millions and summed over thousands of rows it stays far inside decimal's range, so
    /// no check can overflow on a value that passed here. Beyond it: <c>out_of_range</c>.
    /// </summary>
    public const decimal MaxAbsAmount = 1_000_000_000_000_000m;

    /// <summary>Reads <paramref name="input"/> against <paramref name="root"/>.</summary>
    public static LegajoExtractionReading Read(LegajoSchemaObject root, JsonElement input)
    {
        ArgumentNullException.ThrowIfNull(root);

        var violations = new List<string>();
        var result = ReadObject(root, input, string.Empty, violations) ?? new JsonObject();
        return new LegajoExtractionReading(result, violations);
    }

    /// <summary>
    /// Reads one leaf value of kind <paramref name="spec"/>. Null or absent is null unless
    /// <paramref name="allowNull"/> is false; a value of the wrong JSON type is a violation (and null).
    /// Text longer than its <see cref="LegajoValueSpec.MaxLength"/> is truncated when the model wrote it,
    /// but is a <c>too_long</c> violation under <paramref name="strictText"/> — an analyst's correction is
    /// never silently cut. Shared with the correction validator.
    /// </summary>
    public static JsonNode? ReadValue(
        LegajoValueSpec spec, JsonElement element, string path, List<string> violations, bool allowNull = true,
        bool strictText = false)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            if (!allowNull)
                violations.Add($"{path}: required");
            return null;
        }

        switch (spec.Kind)
        {
            case LegajoValueKind.Money:
            case LegajoValueKind.Percent:
                if (element.ValueKind != JsonValueKind.Number)
                {
                    violations.Add($"{path}: expected_number");
                    return null;
                }

                // A number beyond decimal's own range does not even parse: out of range as well.
                if (!element.TryGetDecimal(out var amount) || Math.Abs(amount) > MaxAbsAmount)
                {
                    violations.Add($"{path}: out_of_range");
                    return null;
                }

                return JsonValue.Create(amount);

            case LegajoValueKind.Integer:
                if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var number)
                    && number == decimal.Truncate(number) && number is >= int.MinValue and <= int.MaxValue)
                {
                    return JsonValue.Create((int)number);
                }
                violations.Add($"{path}: expected_integer");
                return null;

            case LegajoValueKind.Boolean:
                if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    return JsonValue.Create(element.GetBoolean());
                violations.Add($"{path}: expected_boolean");
                return null;

            case LegajoValueKind.Date:
                if (element.ValueKind == JsonValueKind.String
                    && DateOnly.TryParseExact(element.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                              DateTimeStyles.None, out var date)
                    && date.Year is >= 1900 and <= 2100)
                {
                    return JsonValue.Create(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                }
                violations.Add($"{path}: expected_date");
                return null;

            case LegajoValueKind.Enum:
                if (element.ValueKind == JsonValueKind.String)
                {
                    var raw = element.GetString()!.Trim();
                    var canonical = spec.EnumValues?.FirstOrDefault(v => string.Equals(v, raw, StringComparison.OrdinalIgnoreCase));
                    if (canonical is not null)
                        return JsonValue.Create(canonical);
                }
                violations.Add($"{path}: unknown_enum_value");
                return null;

            case LegajoValueKind.Text:
                if (element.ValueKind == JsonValueKind.String)
                {
                    if (strictText && IsTooLong(element.GetString(), spec.MaxLength))
                    {
                        violations.Add($"{path}: too_long");
                        return null;
                    }
                    return Truncate(element.GetString(), spec.MaxLength) is { } text ? JsonValue.Create(text) : null;
                }
                violations.Add($"{path}: expected_string");
                return null;

            case LegajoValueKind.Period:
                if (element.ValueKind == JsonValueKind.String
                    && LegajoJsonValues.ParsePeriod(element.GetString()) is { } month)
                {
                    return JsonValue.Create(month.ToString("yyyy-MM", CultureInfo.InvariantCulture));
                }
                violations.Add($"{path}: expected_period");
                return null;

            case LegajoValueKind.Cuit:
                // 11 digits or null; the check digit and the comparison with the legajo are C#'s, later.
                var cuitText = element.ValueKind switch
                {
                    JsonValueKind.String => element.GetString(),
                    JsonValueKind.Number => element.GetRawText(),
                    _ => null
                };
                if (cuitText is null)
                {
                    violations.Add($"{path}: expected_string");
                    return null;
                }
                return LegajoCuit.Digits(cuitText) is { } digits ? JsonValue.Create(digits) : null;

            default:
                violations.Add($"{path}: unsupported_kind");
                return null;
        }
    }

    /// <summary>
    /// Reads an item object (for appending one by correction). Shared with the correction validator, which
    /// passes <paramref name="strictText"/> so an over-long text (a quote included) is rejected, not cut.
    /// </summary>
    public static JsonObject? ReadItem(LegajoSchemaObject item, JsonElement element, string path, List<string> violations,
                                       bool strictText = false)
        => ReadObject(item, element, path, violations, strictText);

    private static JsonObject? ReadObject(LegajoSchemaObject node, JsonElement element, string path, List<string> violations,
                                          bool strictText = false)
    {
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            if (!node.Nullable)
                violations.Add($"{PathOrRoot(path)}: required");
            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            violations.Add($"{PathOrRoot(path)}: expected_object");
            return null;
        }

        var result = new JsonObject();
        foreach (var property in node.Properties)
        {
            var childPath = LegajoJsonPointer.Child(path, property.Name);
            var child = element.TryGetProperty(property.Name, out var value) ? value : default;
            result[property.Name] = ReadNode(property.Node, child, childPath, violations, strictText);
        }

        if (node.ItemProvenance)
            AddProvenance(result, element, path, violations, strictText);

        return result;
    }

    private static JsonNode? ReadNode(LegajoSchemaNode node, JsonElement element, string path, List<string> violations,
                                      bool strictText)
        => node switch
        {
            LegajoSchemaObject obj  => ReadObject(obj, element, path, violations, strictText),
            LegajoSchemaArray array => ReadArray(array, element, path, violations, strictText),
            LegajoSchemaField field => ReadField(field, element, path, violations, strictText),
            LegajoSchemaValue value => ReadValue(value.Spec, element, path, violations, AllowsNull(value), strictText),
            _ => throw new InvalidOperationException($"Unknown schema node {node.GetType().Name}.")
        };

    private static JsonArray ReadArray(LegajoSchemaArray node, JsonElement element, string path, List<string> violations,
                                       bool strictText)
    {
        var result = new JsonArray();
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return result;

        if (element.ValueKind != JsonValueKind.Array)
        {
            violations.Add($"{path}: expected_array");
            return result;
        }

        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            result.Add(ReadObject(node.Items, item, LegajoJsonPointer.Child(path, index), violations, strictText));
            index++;
        }

        return result;
    }

    private static JsonObject ReadField(LegajoSchemaField node, JsonElement element, string path, List<string> violations,
                                        bool strictText)
    {
        var field = new JsonObject();

        if (element.ValueKind == JsonValueKind.Object)
        {
            var value = element.TryGetProperty("value", out var v) ? v : default;
            field["value"] = ReadValue(node.Spec, value, LegajoJsonPointer.Child(path, "value"), violations,
                                       strictText: strictText);
            AddProvenance(field, element, path, violations, strictText);
            if (field["value"] is null)
            {
                // No value, no provenance: a page or a quote for a figure that was not read means nothing.
                field["page"]  = null;
                field["quote"] = null;
            }
            return field;
        }

        // A bare value where the provenance object belongs: keep the value, with nothing to verify it by.
        field["value"]      = ReadValue(node.Spec, element, path, violations, strictText: strictText);
        field["page"]       = null;
        field["quote"]      = null;
        field["confidence"] = 0m;
        return field;
    }

    private static void AddProvenance(JsonObject target, JsonElement source, string path, List<string> violations,
                                      bool strictText)
    {
        int? page = source.TryGetProperty("page", out var p) && p.ValueKind == JsonValueKind.Number
                    && p.TryGetInt32(out var pageNumber) && pageNumber >= 1
            ? pageNumber
            : null;

        var quote = source.TryGetProperty("quote", out var q) && q.ValueKind == JsonValueKind.String
            ? Truncate(q.GetString(), MaxQuoteLength)
            : null;
        if (strictText && q.ValueKind == JsonValueKind.String && IsTooLong(q.GetString(), MaxQuoteLength))
            violations.Add($"{LegajoJsonPointer.Child(path, "quote")}: too_long");

        var confidence = source.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number
                         && c.TryGetDecimal(out var reported)
            ? Math.Round(Math.Clamp(reported, 0m, 1m), 4)
            : 0m;

        target["page"]       = page;
        target["quote"]      = quote;
        target["confidence"] = confidence;
    }

    /// <summary>
    /// Whether a plain item value may be null. An item's enum (rubro, location, section, kind, the
    /// match-key kind) may not: the row means nothing without it, unless the schema marks it
    /// <see cref="LegajoSchemaValue.NullableEnum"/> (investmentClass). The generated schema says the same.
    /// </summary>
    public static bool AllowsNull(LegajoSchemaValue value) => value.Spec.Kind != LegajoValueKind.Enum || value.NullableEnum;

    private static string PathOrRoot(string path) => path.Length == 0 ? "/" : path;

    private static bool IsTooLong(string? value, int max)
        => !string.IsNullOrWhiteSpace(value) && value.Trim().Length > max;

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        return trimmed.Length > max ? trimmed[..max] : trimmed;
    }
}
