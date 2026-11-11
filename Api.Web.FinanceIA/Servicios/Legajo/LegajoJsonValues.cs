// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// Typed reads of leaf values in a ResultJson tree, whether the node was built in C# (a boxed decimal,
/// int, bool, string) or parsed from JSON (an element-backed value). Pure.
/// </summary>
public static class LegajoJsonValues
{
    /// <summary>The node as a decimal, or null when it is not a number.</summary>
    public static decimal? Decimal(JsonNode? node)
    {
        if (node is not JsonValue value)
            return null;

        if (value.TryGetValue<decimal>(out var d)) return d;
        if (value.TryGetValue<int>(out var i)) return i;
        if (value.TryGetValue<long>(out var l)) return l;
        if (value.TryGetValue<double>(out var f)) return (decimal)f;
        if (value.TryGetValue<JsonElement>(out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetDecimal(out var ed))
            return ed;

        return null;
    }

    /// <summary>The node as a whole number, or null.</summary>
    public static int? Integer(JsonNode? node)
        => Decimal(node) is { } d && d == decimal.Truncate(d) && d is >= int.MinValue and <= int.MaxValue ? (int)d : null;

    /// <summary>The node as a string, or null.</summary>
    public static string? String(JsonNode? node)
    {
        if (node is not JsonValue value)
            return null;

        if (value.TryGetValue<string>(out var s)) return s;
        if (value.TryGetValue<JsonElement>(out var e) && e.ValueKind == JsonValueKind.String) return e.GetString();

        return null;
    }

    /// <summary>The <c>value</c> of the provenance field at <paramref name="fieldPath"/>, as a decimal.</summary>
    public static decimal? FieldDecimal(JsonNode root, string fieldPath)
        => Decimal(LegajoJsonPointer.Get(root, fieldPath + "/value"));

    /// <summary>The <c>value</c> of the provenance field at <paramref name="fieldPath"/>, as a string.</summary>
    public static string? FieldString(JsonNode root, string fieldPath)
        => String(LegajoJsonPointer.Get(root, fieldPath + "/value"));

    /// <summary>
    /// A <c>yyyy-MM</c> period (years 1900–2100) as the first day of that month, or null. The one parser of
    /// the Period kind: the reader, the corrections and the checks agree on what a period is.
    /// </summary>
    public static DateOnly? ParsePeriod(string? text)
        => DateOnly.TryParseExact(text, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month)
           && month.Year is >= 1900 and <= 2100
            ? month
            : null;

    /// <summary>The node as a <c>yyyy-MM-dd</c> date, or null.</summary>
    public static DateOnly? Date(JsonNode? node)
        => DateOnly.TryParseExact(String(node), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    /// <summary>The node as a period (the first day of its month), or null.</summary>
    public static DateOnly? Period(JsonNode? node) => ParsePeriod(String(node));

    /// <summary>The array at <paramref name="path"/>, or an empty one.</summary>
    public static IReadOnlyList<JsonNode?> ArrayAt(JsonNode root, string path)
        => LegajoJsonPointer.Get(root, path) is JsonArray array ? array.ToList() : [];
}
