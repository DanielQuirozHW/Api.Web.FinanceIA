// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using System.Globalization;
using System.Text.Json.Nodes;

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// Minimal RFC 6901 JSON Pointers over <see cref="JsonNode"/> trees — the addressing of every legajo
/// extraction field (ResultJson, corrections, check fieldPaths, review queues). Pure.
/// </summary>
public static class LegajoJsonPointer
{
    /// <summary>The unescaped reference tokens of <paramref name="pointer"/>, or null when it is not a pointer ("" is the root).</summary>
    public static IReadOnlyList<string>? Parse(string? pointer)
    {
        if (pointer is null)
            return null;
        if (pointer.Length == 0)
            return [];
        if (pointer[0] != '/')
            return null;

        return pointer[1..]
            .Split('/')
            .Select(t => t.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>Builds a pointer from tokens, escaping <c>~</c> and <c>/</c>.</summary>
    public static string Build(IEnumerable<string> tokens)
        => string.Concat(tokens.Select(t => "/" + t.Replace("~", "~0", StringComparison.Ordinal)
                                                   .Replace("/", "~1", StringComparison.Ordinal)));

    /// <summary>A child pointer: <paramref name="parent"/> + one token.</summary>
    public static string Child(string parent, string token) => parent + Build([token]);

    /// <summary>A child pointer for an array index.</summary>
    public static string Child(string parent, int index) => parent + "/" + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The node at <paramref name="pointer"/>; <paramref name="found"/> is false when a token does not
    /// resolve (a JSON null that exists is found, with a null node).
    /// </summary>
    public static JsonNode? Get(JsonNode? root, string pointer, out bool found)
    {
        found = false;
        var tokens = Parse(pointer);
        if (tokens is null)
            return null;

        var current = root;
        foreach (var token in tokens)
        {
            switch (current)
            {
                case JsonObject obj when obj.TryGetPropertyValue(token, out var child):
                    current = child;
                    break;
                case JsonArray array when TryIndex(token, out var index) && index < array.Count:
                    current = array[index];
                    break;
                default:
                    return null;
            }
        }

        found = true;
        return current;
    }

    /// <summary>The node at <paramref name="pointer"/>, or null.</summary>
    public static JsonNode? Get(JsonNode? root, string pointer) => Get(root, pointer, out _);

    /// <summary>
    /// Replaces the node at <paramref name="pointer"/> with <paramref name="value"/>. The parent must
    /// exist; an array index equal to its count APPENDS. Returns false when the pointer does not resolve.
    /// </summary>
    public static bool Set(JsonNode root, string pointer, JsonNode? value)
    {
        ArgumentNullException.ThrowIfNull(root);

        var tokens = Parse(pointer);
        if (tokens is null || tokens.Count == 0)
            return false;

        var parent = Get(root, Build(tokens.Take(tokens.Count - 1)), out var found);
        if (!found)
            return false;

        var last = tokens[^1];
        switch (parent)
        {
            case JsonObject obj:
                obj[last] = value;
                return true;
            case JsonArray array when TryIndex(last, out var index) && index <= array.Count:
                if (index == array.Count)
                    array.Add(value);
                else
                    array[index] = value;
                return true;
            default:
                return false;
        }
    }

    /// <summary>A non-negative array index token without leading zeros.</summary>
    public static bool TryIndex(string token, out int index)
    {
        index = -1;
        if (token.Length == 0 || (token.Length > 1 && token[0] == '0') || !token.All(char.IsAsciiDigit))
            return false;

        return int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out index);
    }
}
