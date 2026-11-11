// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using System.Text.Json.Nodes;

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// The model's extraction after C# read it against the schema: the normalized tree (what ResultJson
/// stores — personal data) and the violations that make it unusable.
/// </summary>
/// <param name="Result">The normalized tree, in schema order.</param>
/// <param name="Violations">
/// <c>"{pointer}: {code}"</c> entries, empty when the output is usable. Pointers and codes only —
/// never a value — so they may be stored with a failed run.
/// </param>
public sealed record LegajoExtractionReading(JsonObject Result, IReadOnlyList<string> Violations)
{
    /// <summary>True when the output matched the schema.</summary>
    public bool IsValid => Violations.Count == 0;
}
