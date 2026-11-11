// Project : Api.Web.FinanceIA
// Created : 2026-09-28

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>One line of a legajo's checklist.</summary>
public sealed record LegajoExpectedDocumentResponse
{
    /// <summary>The type the line is named after.</summary>
    public required string DocumentType { get; init; }

    /// <summary>False for an optional document.</summary>
    public required bool Required { get; init; }

    /// <summary>Other types that satisfy the line.</summary>
    public required IReadOnlyList<string> Alternatives { get; init; }

    /// <summary>
    /// The legajo's non-deleted documents of that type (or an alternative), excluding Blocked ones —
    /// a document of another person satisfies nothing.
    /// </summary>
    public required IReadOnlyList<Guid> SatisfiedByDocumentIds { get; init; }
}
