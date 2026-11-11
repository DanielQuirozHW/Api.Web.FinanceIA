// Project : Api.Web.FinanceIA
// Created : 2026-09-28

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// GET /api/person-legajos/{legajoId}. Deliberately carries neither the person's name nor CUIT, and
/// no JSON snapshot: the Portal already shows the entity, and the legajo view is about documents.
/// </summary>
public sealed record PersonLegajoResponse
{
    /// <summary>The legajo.</summary>
    public required Guid PersonLegajoId { get; init; }

    /// <summary>The entity linked to it, or null.</summary>
    public Guid? EntityId { get; init; }

    /// <summary>Monotributo / ResponsableInscripto / Exento / Desconocido.</summary>
    public required string FiscalRegime { get; init; }

    /// <summary>A–K, or null.</summary>
    public string? MonotributoCategory { get; init; }

    /// <summary>The recorded data-processing authorization, or null when there is none.</summary>
    public LegajoConsentResponse? Consent { get; init; }

    /// <summary>
    /// The checklist for the fiscal regime — the tenant's expected-documents matrix, or the code default for
    /// the regime — with what satisfies each line.
    /// </summary>
    public required IReadOnlyList<LegajoExpectedDocumentResponse> ExpectedDocuments { get; init; }

    /// <summary>
    /// Where <see cref="ExpectedDocuments"/> came from: <c>tenant</c> (the tenant configured this regime) or
    /// <c>default</c> (the code default; the Portal still labels that list as provisional).
    /// </summary>
    public string ExpectedDocumentsSource { get; init; } = ExpectedDocumentsSources.Default;
}
