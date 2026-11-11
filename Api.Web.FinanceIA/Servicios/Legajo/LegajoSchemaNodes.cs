// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using System.Text.Json.Nodes;

namespace Api.Web.FinanceIA.Servicios.Legajo;

// The small immutable vocabulary a legajo extraction schema is written in (M10 Phase 2b). Kept in one
// file because each type is a line or two and none makes sense alone: a schema is a tree of these,
// from which LegajoSchemaJson generates the tool's input_schema, LegajoToolInputReader validates the
// model's answer, and LegajoCorrectionValidator checks an analyst's correction — one source, so the
// three cannot drift.

/// <summary>What a leaf value is, as C# validates it (the JSON schema only carries the JSON type).</summary>
public enum LegajoValueKind
{
    /// <summary>An amount as printed (JSON number, no separators).</summary>
    Money,

    /// <summary>A whole number.</summary>
    Integer,

    /// <summary>true / false.</summary>
    Boolean,

    /// <summary>A date as <c>yyyy-MM-dd</c>.</summary>
    Date,

    /// <summary>One of <see cref="LegajoValueSpec.EnumValues"/>, matched case-insensitively.</summary>
    Enum,

    /// <summary>Free text, trimmed and truncated to <see cref="LegajoValueSpec.MaxLength"/>.</summary>
    Text,

    /// <summary>A percentage as printed (100 = the whole).</summary>
    Percent,

    /// <summary>A CUIT/CUIL: kept as its 11 digits, or null.</summary>
    Cuit,

    /// <summary>A month as <c>yyyy-MM</c> (a DDJJ period, a payment month). Phase 3a.</summary>
    Period
}

/// <summary>A leaf's kind and its constraints.</summary>
/// <param name="Kind">The kind.</param>
/// <param name="EnumValues">The canonical values of an <see cref="LegajoValueKind.Enum"/>.</param>
/// <param name="MaxLength">The truncation length of a <see cref="LegajoValueKind.Text"/>.</param>
public sealed record LegajoValueSpec(LegajoValueKind Kind, IReadOnlyList<string>? EnumValues = null, int MaxLength = 200)
{
    /// <summary>An amount.</summary>
    public static LegajoValueSpec Money { get; } = new(LegajoValueKind.Money);

    /// <summary>A whole number.</summary>
    public static LegajoValueSpec Integer { get; } = new(LegajoValueKind.Integer);

    /// <summary>A boolean.</summary>
    public static LegajoValueSpec Boolean { get; } = new(LegajoValueKind.Boolean);

    /// <summary>A date.</summary>
    public static LegajoValueSpec Date { get; } = new(LegajoValueKind.Date);

    /// <summary>A percentage.</summary>
    public static LegajoValueSpec Percent { get; } = new(LegajoValueKind.Percent);

    /// <summary>A CUIT.</summary>
    public static LegajoValueSpec Cuit { get; } = new(LegajoValueKind.Cuit);

    /// <summary>A month, <c>yyyy-MM</c>.</summary>
    public static LegajoValueSpec Period { get; } = new(LegajoValueKind.Period);

    /// <summary>Free text up to <paramref name="maxLength"/> characters.</summary>
    public static LegajoValueSpec Text(int maxLength) => new(LegajoValueKind.Text, MaxLength: maxLength);

    /// <summary>One of <paramref name="values"/>.</summary>
    public static LegajoValueSpec OneOf(params string[] values) => new(LegajoValueKind.Enum, values);
}

/// <summary>A node of a legajo extraction schema.</summary>
public abstract record LegajoSchemaNode;

/// <summary>
/// A leaf the analyst verifies on its own: <c>{value, page, quote, confidence}</c>. A correction of
/// the leaf's path replaces its <c>value</c>.
/// </summary>
/// <param name="Spec">The value's kind.</param>
public sealed record LegajoSchemaField(LegajoValueSpec Spec) : LegajoSchemaNode;

/// <summary>A plain value inside an item; the item carries the provenance for all of them.</summary>
/// <param name="Spec">The value's kind.</param>
/// <param name="NullableEnum">
/// For an <see cref="LegajoValueKind.Enum"/> only: the value may also be null. Every other kind is nullable anyway; an
/// enum is not by default (a row's <c>rubro</c> or <c>kind</c> is required). Added for mb-1.3 / bp-1.1's
/// <c>investmentClass</c>, which is null when the document gives no hint; false leaves every older schema unchanged.
/// </param>
public sealed record LegajoSchemaValue(LegajoValueSpec Spec, bool NullableEnum = false) : LegajoSchemaNode;

/// <summary>
/// An object. With <paramref name="ItemProvenance"/> it is an array item and also carries
/// <c>page</c>, <c>quote</c> and <c>confidence</c> for the printed row it was read from.
/// </summary>
/// <param name="Properties">The properties, in wire order.</param>
/// <param name="Nullable">Whether the whole object may be null (a section the document may not have).</param>
/// <param name="ItemProvenance">Whether it is an array item with row-level provenance.</param>
public sealed record LegajoSchemaObject(
    IReadOnlyList<LegajoSchemaProperty> Properties, bool Nullable = false, bool ItemProvenance = false)
    : LegajoSchemaNode
{
    /// <summary>The property named <paramref name="name"/>, or null.</summary>
    public LegajoSchemaNode? Find(string name)
        => Properties.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal))?.Node;
}

/// <summary>An array of items.</summary>
/// <param name="Items">The item shape (always with row provenance).</param>
public sealed record LegajoSchemaArray(LegajoSchemaObject Items) : LegajoSchemaNode;

/// <summary>A named property of an object.</summary>
/// <param name="Name">camelCase; the same name in the tool schema, ResultJson and JSON Pointers.</param>
/// <param name="Node">Its shape.</param>
public sealed record LegajoSchemaProperty(string Name, LegajoSchemaNode Node);

/// <summary>
/// A requirement a list of paths cannot express (Phase 3a): "a period row with its period and its sales or
/// its débito", "the categoría or at least one payment". When <paramref name="IsMet"/> is false the
/// extraction lists <paramref name="Path"/> as missing, exactly as an unfilled required field.
/// </summary>
/// <param name="Path">The JSON Pointer reported in the missing queue.</param>
/// <param name="IsMet">Whether a normalized (or effective) extraction tree satisfies the requirement. Pure.</param>
public sealed record LegajoRequirement(string Path, Func<JsonObject, bool> IsMet);
