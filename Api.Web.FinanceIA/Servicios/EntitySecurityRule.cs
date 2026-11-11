// Project : Api.Web.FinanceIA
// Created : 2026-04-27
// Modified: 2026-04-27

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// Security rules for a single entity. Aggregates field-level rules
/// that must be enforced for every search request against this entity.
/// </summary>
public sealed record EntitySecurityRule
{
    /// <summary>Gets the collection of field-level security rules.</summary>
    public List<FieldSecurityRule> FieldRules { get; init; } = new();
}
