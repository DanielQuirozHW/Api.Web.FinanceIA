// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using System.Text.Json.Nodes;

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// Generates a tool input_schema from a <see cref="LegajoSchemaObject"/> tree. Pure and deterministic
/// (the output is fingerprinted).
///
/// <para>
/// The schema is written in the strict-tool-use dialect — every object closes
/// <c>additionalProperties</c> and requires every property, no numeric or length keywords — although
/// the extraction sends it WITHOUT <c>strict: true</c> (see LegajoExtractionService). Keeping the shape
/// strict-compatible makes switching strict on a one-line change once a live run proves the grammar
/// compiles. The provenance leaves share <c>$defs</c> per value kind to keep the schema, and so every
/// request's input tokens, small.
/// </para>
/// </summary>
public static class LegajoSchemaJson
{
    /// <summary>The generated schema as compact JSON.</summary>
    public static string Generate(LegajoSchemaObject root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var defs = new JsonObject();
        var schema = ObjectSchema(root, defs);
        if (defs.Count > 0)
            schema["$defs"] = defs;

        return schema.ToJsonString();
    }

    private static JsonObject ObjectSchema(LegajoSchemaObject node, JsonObject defs)
    {
        var properties = new JsonObject();
        var required   = new JsonArray();

        foreach (var property in node.Properties)
        {
            properties[property.Name] = NodeSchema(property.Node, defs);
            required.Add(property.Name);
        }

        if (node.ItemProvenance)
        {
            properties["page"]       = new JsonObject { ["type"] = new JsonArray("integer", "null") };
            properties["quote"]      = new JsonObject { ["type"] = new JsonArray("string", "null") };
            properties["confidence"] = new JsonObject { ["type"] = "number" };
            required.Add("page");
            required.Add("quote");
            required.Add("confidence");
        }

        return new JsonObject
        {
            ["type"]                 = node.Nullable ? new JsonArray("object", "null") : "object",
            ["additionalProperties"] = false,
            ["required"]             = required,
            ["properties"]           = properties
        };
    }

    private static JsonNode NodeSchema(LegajoSchemaNode node, JsonObject defs) => node switch
    {
        LegajoSchemaObject obj   => ObjectSchema(obj, defs),
        LegajoSchemaArray array  => new JsonObject { ["type"] = "array", ["items"] = ObjectSchema(array.Items, defs) },
        LegajoSchemaValue value  => ValueSchema(value.Spec, nullable: LegajoToolInputReader.AllowsNull(value)),
        LegajoSchemaField field  => FieldSchema(field.Spec, defs),
        _ => throw new InvalidOperationException($"Unknown schema node {node.GetType().Name}.")
    };

    // A provenance leaf. Non-enum kinds share one $defs entry each; an enum carries its own list inline.
    private static JsonNode FieldSchema(LegajoValueSpec spec, JsonObject defs)
    {
        if (spec.Kind == LegajoValueKind.Enum)
            return FieldObject(ValueSchema(spec, nullable: true));

        var name = "field_" + spec.Kind.ToString().ToLowerInvariant();
        if (!defs.ContainsKey(name))
            defs[name] = FieldObject(ValueSchema(spec, nullable: true));

        return new JsonObject { ["$ref"] = "#/$defs/" + name };
    }

    private static JsonObject FieldObject(JsonNode valueSchema) => new()
    {
        ["type"]                 = "object",
        ["additionalProperties"] = false,
        ["required"]             = new JsonArray("value", "page", "quote", "confidence"),
        ["properties"]           = new JsonObject
        {
            ["value"]      = valueSchema,
            ["page"]       = new JsonObject { ["type"] = new JsonArray("integer", "null") },
            ["quote"]      = new JsonObject { ["type"] = new JsonArray("string", "null") },
            ["confidence"] = new JsonObject { ["type"] = "number" }
        }
    };

    private static JsonObject ValueSchema(LegajoValueSpec spec, bool nullable)
    {
        JsonNode Type(string jsonType) => nullable ? new JsonArray(jsonType, "null") : jsonType;

        return spec.Kind switch
        {
            LegajoValueKind.Money or LegajoValueKind.Percent => new JsonObject { ["type"] = Type("number") },
            LegajoValueKind.Integer => new JsonObject { ["type"] = Type("integer") },
            LegajoValueKind.Boolean => new JsonObject { ["type"] = Type("boolean") },
            LegajoValueKind.Date    => new JsonObject { ["type"] = Type("string"), ["description"] = "YYYY-MM-DD" },
            LegajoValueKind.Cuit    => new JsonObject { ["type"] = Type("string"), ["description"] = "11 digits, no hyphens" },
            LegajoValueKind.Period  => new JsonObject { ["type"] = Type("string"), ["description"] = "YYYY-MM" },
            LegajoValueKind.Text    => new JsonObject { ["type"] = Type("string") },
            LegajoValueKind.Enum    => EnumSchema(spec, nullable),
            _ => throw new InvalidOperationException($"Unknown value kind {spec.Kind}.")
        };
    }

    private static JsonObject EnumSchema(LegajoValueSpec spec, bool nullable)
    {
        var values = new JsonArray();
        foreach (var value in spec.EnumValues ?? [])
            values.Add(value);
        if (nullable)
            values.Add(null);

        return new JsonObject
        {
            ["type"] = nullable ? new JsonArray("string", "null") : "string",
            ["enum"] = values
        };
    }
}
