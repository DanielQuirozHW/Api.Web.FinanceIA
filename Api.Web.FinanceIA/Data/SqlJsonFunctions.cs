// Project : Api.Web.FinanceIA
// Created : 2026-08-26
// Modified: 2026-08-26

using System.Text.Json;

namespace Api.Web.FinanceIA.Data;

/// <summary>
/// LINQ seams for SQL Server's built-in JSON scalar functions, so a projection can pull a single
/// field out of an <c>nvarchar(max)</c> JSON column WITHOUT selecting the whole blob into the result
/// set. Mapped in <see cref="FinanciaDbContext.OnModelCreating"/> via
/// <c>HasDbFunction(...).HasName("JSON_VALUE").IsBuiltIn()</c>.
///
/// <para>
/// EF Core 9 has no <c>EF.Functions.JsonValue</c> — that arrived in EF Core 10 — and the columns
/// here are plain <c>string</c> properties, not <c>.ToJson()</c> owned entities, so EF has no other
/// route into their contents. This is the whole reason the type exists. It is the first
/// <c>HasDbFunction</c> in the project; anything else that needs a JSON scalar belongs here too
/// rather than in a second, competing helper.
/// </para>
/// </summary>
public static class SqlJsonFunctions
{
    /// <summary>
    /// Extracts one SCALAR value from a JSON document. Translated to the T-SQL built-in
    /// <c>JSON_VALUE(json, path)</c>; SQL Server is the source of truth for its behaviour.
    ///
    /// <para>
    /// The C# body below is NOT a placeholder that throws. On a non-relational provider — the
    /// InMemory contexts the behavioural tests run on — the mapping is not registered and EF
    /// client-evaluates this method in the top-level projection instead, so the body has to agree
    /// with T-SQL or those tests would assert against fiction. It mirrors SQL Server's default LAX
    /// path mode: a missing path, a JSON <c>null</c>, or a path resolving to an object or an array
    /// all yield <c>null</c> rather than an error.
    /// </para>
    ///
    /// <para>
    /// Only the dotted-object subset of JSONPath is supported — <c>$.a.b.c</c> — which is all the
    /// callers need. Array indexes and wildcards are deliberately NOT implemented: SQL Server would
    /// happily translate them, so a silent client/server divergence is a worse outcome than the
    /// <see cref="ArgumentException"/> this throws for a path it does not understand.
    /// </para>
    ///
    /// <para>
    /// Returns the value's TEXT, exactly as <c>JSON_VALUE</c> does — numbers included, since it
    /// returns <c>nvarchar(4000)</c>. Callers parse. Numbers use the raw document text so
    /// <c>2.50</c> and <c>1e3</c> survive unrounded, matching SQL Server.
    /// </para>
    /// </summary>
    /// <param name="json">The JSON document. Null in, null out.</param>
    /// <param name="path">A dotted path rooted at <c>$</c>, e.g. <c>$.deuda.estado</c>.</param>
    /// <returns>The scalar's text, or null when it is absent, JSON null, or not a scalar.</returns>
    /// <exception cref="ArgumentException">
    /// The path is not a supported dotted path. A programming error, not a data condition — it
    /// cannot depend on the row, so it fails on the first call rather than silently disagreeing
    /// with what SQL Server would have returned.
    /// </exception>
    public static string? JsonValue(string? json, string path)
    {
        var segments = ParsePath(path);

        if (string.IsNullOrEmpty(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);

            var current = doc.RootElement;
            foreach (var segment in segments)
            {
                if (current.ValueKind != JsonValueKind.Object) return null;
                if (!current.TryGetProperty(segment, out var next)) return null;
                current = next;
            }

            return current.ValueKind switch
            {
                // GetRawText, not GetDouble/ToString: JSON_VALUE hands back the document's own
                // text, so trailing zeros and exponent notation must survive.
                JsonValueKind.Number => current.GetRawText(),
                JsonValueKind.String => current.GetString(),
                JsonValueKind.True   => "true",
                JsonValueKind.False  => "false",
                // Null, Object, Array, Undefined — all null in lax mode.
                _ => null
            };
        }
        catch (JsonException)
        {
            // SQL Server would raise here, but the JSON columns this runs against all carry an
            // ISJSON check constraint, so unparseable input cannot reach the translated path. Only
            // the client-evaluated one can see it, and a null is the safe answer for a caller whose
            // whole contract is "null when the field is not there".
            return null;
        }
    }

    /// <summary>
    /// Splits <c>$.a.b</c> into <c>["a", "b"]</c>. Rejects anything richer so an unsupported path
    /// can never be silently evaluated one way here and another way in T-SQL.
    /// </summary>
    private static string[] ParsePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith("$.", StringComparison.Ordinal))
            throw new ArgumentException(
                $"Only dotted paths rooted at '$' are supported, e.g. '$.deuda.estado'. Got: '{path}'.",
                nameof(path));

        var segments = path[2..].Split('.');

        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment.Contains('[') || segment.Contains('*'))
                throw new ArgumentException(
                    $"Array indexes and wildcards are not supported. Got: '{path}'.",
                    nameof(path));
        }

        return segments;
    }
}
