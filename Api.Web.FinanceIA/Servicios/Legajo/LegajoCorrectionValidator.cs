// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using System.Text.Json;
using System.Text.Json.Nodes;
using V = Api.Web.FinanceIA.Servicios.Legajo.LegajoExtractionVocabulary;

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// Validates an analyst's correction against the schema and the current effective values (M10 Phase
/// 2b), and normalizes the value exactly as the model's answer is normalized. Pure. The messages are
/// es-AR and quote the path, never a value.
///
/// <para>
/// Correctable: a provenance field (<c>/totals/totalBienes</c>), a plain value of an existing row
/// (<c>/items/3/fiscalValue</c>), a row's match-key object (<c>/items/3/matchKey</c>), or a new row at
/// the index equal to the array's length (<c>/items/4</c>, a row the model missed). NOT correctable:
/// the holder CUIT (never shown, compared only), a row's provenance (page, quote, confidence), a whole
/// existing row or a whole section.
/// </para>
/// </summary>
public static class LegajoCorrectionValidator
{
    /// <summary>LegajoCorrections.FieldPath is nvarchar(300).</summary>
    public const int MaxFieldPathLength = 300;

    private static readonly HashSet<string> ProvenanceKeys = new(StringComparer.Ordinal) { "page", "quote", "confidence" };

    /// <summary>The outcome of a validation.</summary>
    /// <param name="Error">An es-AR message for a 400, or null when valid.</param>
    /// <param name="Value">The normalized value to store (null clears the field).</param>
    /// <param name="Original">The effective value before the correction.</param>
    public sealed record Result(string? Error, JsonNode? Value, JsonNode? Original)
    {
        /// <summary>True when the correction may be stored.</summary>
        public bool IsValid => Error is null;
    }

    /// <summary>Validates <paramref name="value"/> for <paramref name="fieldPath"/>.</summary>
    public static Result Validate(LegajoExtractionSchema schema, JsonObject effective, string? fieldPath, JsonElement value)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(effective);

        static Result Fail(string message) => new(message, null, null);

        if (string.IsNullOrWhiteSpace(fieldPath) || LegajoJsonPointer.Parse(fieldPath) is not { Count: > 0 } tokens)
            return Fail("fieldPath debe ser un JSON Pointer (por ejemplo /totals/totalBienes).");

        if (fieldPath.Length > MaxFieldPathLength)
            return Fail($"fieldPath no puede superar {MaxFieldPathLength} caracteres.");

        if (value.ValueKind == JsonValueKind.Undefined)
            return Fail("value es obligatorio (null borra el campo).");

        if (fieldPath == V.HolderCuitPath || fieldPath.StartsWith(V.HolderCuitPath + "/", StringComparison.Ordinal))
            return Fail("El CUIT del titular no se corrige: se compara con el del legajo.");

        // A provenance member (a field's value/page/quote/confidence, a row's page/quote/confidence) is not
        // a path of its own: the correction names the field, and only its value changes.
        var parentPath = LegajoJsonPointer.Build(tokens.Take(tokens.Count - 1));
        var parentNode = tokens.Count > 1 ? LegajoSchemaResolver.Resolve(schema.Root, parentPath, out _) : null;
        if ((parentNode is LegajoSchemaField && (tokens[^1] == "value" || ProvenanceKeys.Contains(tokens[^1])))
            || (parentNode is LegajoSchemaObject { ItemProvenance: true } && ProvenanceKeys.Contains(tokens[^1])))
        {
            return Fail($"{fieldPath} no es un campo corregible; indique la ruta del campo (sin /value, /page, /quote ni /confidence).");
        }

        var node = LegajoSchemaResolver.Resolve(schema.Root, fieldPath, out var insideItem);
        if (node is null)
            return Fail($"{fieldPath} no existe en el esquema {schema.SchemaVersion} de {schema.DocumentType}.");

        var violations = new List<string>();
        switch (node)
        {
            case LegajoSchemaField field:
            {
                if (!TargetExists(field, effective, fieldPath))
                    return Fail($"{fieldPath} no existe en esta extracción.");

                var normalized = LegajoToolInputReader.ReadValue(field.Spec, value, fieldPath, violations, strictText: true);
                return violations.Count > 0
                    ? Fail(ValueMessage(fieldPath, field.Spec, violations))
                    : new Result(null, normalized, LegajoJsonPointer.Get(effective, fieldPath + "/value")?.DeepClone());
            }

            case LegajoSchemaValue plain:
            {
                // The row (and any object between it and the value) must exist: rows are added whole.
                if (!TargetExists(plain, effective, fieldPath))
                    return Fail($"{parentPath} no existe en esta extracción.");

                var normalized = LegajoToolInputReader.ReadValue(plain.Spec, value, fieldPath, violations,
                                                                 LegajoToolInputReader.AllowsNull(plain), strictText: true);
                return violations.Count > 0
                    ? Fail(ValueMessage(fieldPath, plain.Spec, violations))
                    : new Result(null, normalized, LegajoJsonPointer.Get(effective, fieldPath)?.DeepClone());
            }

            case LegajoSchemaObject { ItemProvenance: true } item:
                return AppendRow(item, effective, fieldPath, tokens, value);

            case LegajoSchemaObject nested when insideItem:
            {
                // A row's match-key: replaced whole, since it may be null and its members depend on each other.
                if (!TargetExists(nested, effective, fieldPath))
                    return Fail($"{parentPath} no existe en esta extracción.");

                var normalized = value.ValueKind == JsonValueKind.Null && nested.Nullable
                    ? null
                    : LegajoToolInputReader.ReadItem(nested, value, fieldPath, violations, strictText: true);
                return violations.Count > 0
                    ? Fail($"{fieldPath} debe ser un objeto válido: {string.Join("; ", violations)}.")
                    : new Result(null, normalized, LegajoJsonPointer.Get(effective, fieldPath)?.DeepClone());
            }

            default:
                return Fail($"{fieldPath} es una sección, no un campo; corrija sus campos uno por uno.");
        }
    }

    /// <summary>
    /// The structural half of <see cref="Validate"/>: whether the node a correction of
    /// <paramref name="fieldPath"/> writes into exists in the effective values. A provenance field needs
    /// its own <c>{value, page, quote, confidence}</c> object (so none under a null section); a plain value
    /// or a row's match-key needs the object that holds it (rows are added whole, never field by field).
    /// </summary>
    /// <param name="node">The schema node <paramref name="fieldPath"/> resolves to.</param>
    /// <param name="effective">The effective values.</param>
    /// <param name="fieldPath">The JSON Pointer.</param>
    public static bool TargetExists(LegajoSchemaNode node, JsonObject effective, string fieldPath)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(effective);

        if (node is LegajoSchemaField)
            return LegajoJsonPointer.Get(effective, fieldPath) is JsonObject;

        if (node is not (LegajoSchemaValue or LegajoSchemaObject) || LegajoJsonPointer.Parse(fieldPath) is not { Count: > 0 } tokens)
            return false;

        return LegajoJsonPointer.Get(effective, LegajoJsonPointer.Build(tokens.Take(tokens.Count - 1))) is JsonObject;
    }

    /// <summary>
    /// LegajoExtractionFieldResponse.Correctable: a leaf (provenance field or plain value) whose target
    /// exists — the same rule <see cref="Validate"/> applies. A whole object (a row's null match-key) is
    /// never offered, although Validate accepts one: the Portal edits leaves, not objects.
    /// </summary>
    /// <param name="node">The schema node of the emitted field.</param>
    /// <param name="effective">The effective values.</param>
    /// <param name="fieldPath">The field's JSON Pointer.</param>
    public static bool IsCorrectableInView(LegajoSchemaNode node, JsonObject effective, string fieldPath)
        => node is LegajoSchemaField or LegajoSchemaValue && TargetExists(node, effective, fieldPath);

    private static Result AppendRow(
        LegajoSchemaObject item, JsonObject effective, string fieldPath, IReadOnlyList<string> tokens, JsonElement value)
    {
        var arrayPath = LegajoJsonPointer.Build(tokens.Take(tokens.Count - 1));
        if (LegajoJsonPointer.Get(effective, arrayPath) is not JsonArray rows
            || !LegajoJsonPointer.TryIndex(tokens[^1], out var index))
        {
            return new Result($"{arrayPath} no existe en esta extracción.", null, null);
        }

        if (index != rows.Count)
            return new Result(
                $"Una fila existente no se reemplaza entera: corrija sus campos. Para agregar una fila use {LegajoJsonPointer.Child(arrayPath, rows.Count)}.",
                null, null);

        var violations = new List<string>();
        var row = LegajoToolInputReader.ReadItem(item, value, fieldPath, violations, strictText: true);
        if (violations.Count > 0 || row is null)
            return new Result($"La fila debe ser un objeto válido: {string.Join("; ", violations)}.", null, null);

        // Typed in by the analyst: nothing to doubt (page and quote are whatever the analyst gave).
        row["confidence"] = 1m;
        return new Result(null, row, null);
    }

    private static string ValueMessage(string fieldPath, LegajoValueSpec spec, IReadOnlyList<string> violations)
    {
        if (violations.Any(v => v.EndsWith(": out_of_range", StringComparison.Ordinal)))
            return $"{fieldPath} está fuera de rango: su valor absoluto no puede superar 1.000.000.000.000.000 en la unidad impresa.";

        return violations.Any(v => v.EndsWith(": too_long", StringComparison.Ordinal))
            ? $"{fieldPath} supera {spec.MaxLength} caracteres."
            : TypeMessage(fieldPath, spec);
    }

    private static string TypeMessage(string fieldPath, LegajoValueSpec spec) => spec.Kind switch
    {
        LegajoValueKind.Money or LegajoValueKind.Percent => $"{fieldPath} debe ser un número (sin separadores de miles) o null.",
        LegajoValueKind.Integer => $"{fieldPath} debe ser un número entero o null.",
        LegajoValueKind.Boolean => $"{fieldPath} debe ser true, false o null.",
        LegajoValueKind.Date    => $"{fieldPath} debe ser una fecha AAAA-MM-DD o null.",
        LegajoValueKind.Period  => $"{fieldPath} debe ser un período AAAA-MM o null.",
        LegajoValueKind.Enum    => $"{fieldPath} debe ser uno de: {string.Join(", ", spec.EnumValues ?? [])}.",
        _                       => $"{fieldPath} debe ser un texto o null."
    };
}
