// Project : Api.Web.FinanceIA
// Created : 2026-10-31

namespace Api.Web.FinanceIA.Models.Settings;

/// <summary>
/// One line of a tenant's expected-documents matrix, as PUT /api/bff/settings/person-expected-documents takes
/// it and as the TenantSettings row <c>person.expected_documents</c> stores it. Every member is nullable so a
/// malformed line reaches the validator — and gets an es-AR message with its path — instead of failing the
/// model binder.
/// </summary>
public sealed record PersonExpectedDocumentLineDto
{
    /// <summary>A legajo document type other than Otro.</summary>
    public string? DocumentType { get; init; }

    /// <summary><c>required</c> or <c>optional</c>.</summary>
    public string? Requirement { get; init; }

    /// <summary>Other types that satisfy the line; null or empty when there are none.</summary>
    public List<string?>? Alternatives { get; init; }

    /// <summary>
    /// The line's own recency cut, an integer 1–60; null inherits the class key. Decimal so that 6.5 is a
    /// validation message, not a binder error.
    /// </summary>
    public decimal? MaxAgeMonths { get; init; }
}

/// <summary>The stored value of TenantSettings <c>person.expected_documents</c>.</summary>
public sealed record PersonExpectedDocumentsMatrixDto
{
    /// <summary>Lines by regime bucket (Monotributo, ResponsableInscripto, ExentoOrDesconocido).</summary>
    public Dictionary<string, List<PersonExpectedDocumentLineDto?>?>? Regimes { get; init; }
}

/// <summary>
/// Request body of PUT /api/bff/settings/person-expected-documents: <c>{ "regimes": { … } }</c> stores the
/// matrix (a regime left out keeps its code default), <c>{ "reset": true }</c> deletes the tenant's row so
/// every regime goes back to the code default.
/// </summary>
public sealed record UpdatePersonExpectedDocumentsRequest
{
    /// <summary>True deletes the tenant's matrix. Not allowed together with <see cref="Regimes"/>.</summary>
    public bool? Reset { get; init; }

    /// <summary>Lines by regime bucket.</summary>
    public Dictionary<string, List<PersonExpectedDocumentLineDto?>?>? Regimes { get; init; }
}

/// <summary>GET (and PUT) /api/bff/settings/person-expected-documents.</summary>
public sealed record PersonExpectedDocumentsSettingsDto
{
    /// <summary>The matrix in force, one entry per regime bucket.</summary>
    public required IReadOnlyList<PersonExpectedDocumentsRegimeDto> Regimes { get; init; }

    /// <summary>The code defaults, in the same shape (every source is <c>default</c>).</summary>
    public required IReadOnlyList<PersonExpectedDocumentsRegimeDto> Defaults { get; init; }

    /// <summary>The types a line may name, as its type or as an alternative.</summary>
    public required IReadOnlyList<string> DocumentTypes { get; init; }

    /// <summary>When the tenant's row was last written; null when the tenant has none.</summary>
    public DateTime? UpdatedAt { get; init; }
}

/// <summary>One regime bucket of <see cref="PersonExpectedDocumentsSettingsDto"/>.</summary>
public sealed record PersonExpectedDocumentsRegimeDto
{
    /// <summary>Monotributo, ResponsableInscripto or ExentoOrDesconocido.</summary>
    public required string Regime { get; init; }

    /// <summary><c>tenant</c> or <c>default</c>.</summary>
    public required string Source { get; init; }

    /// <summary>The lines; a type absent from them is not expected in the regime ("No se pide").</summary>
    public required IReadOnlyList<PersonExpectedDocumentLineViewDto> Lines { get; init; }
}

/// <summary>One line of <see cref="PersonExpectedDocumentsRegimeDto"/>.</summary>
public sealed record PersonExpectedDocumentLineViewDto
{
    /// <summary>The type the line is named after.</summary>
    public required string DocumentType { get; init; }

    /// <summary><c>required</c> or <c>optional</c>.</summary>
    public required string Requirement { get; init; }

    /// <summary>Other types that satisfy the line.</summary>
    public required IReadOnlyList<string> Alternatives { get; init; }

    /// <summary>The line's own recency cut; null inherits the class key.</summary>
    public int? MaxAgeMonths { get; init; }

    /// <summary>The cut the coverage applies: <see cref="MaxAgeMonths"/>, else the tenant's class key; null when timeless.</summary>
    public int? EffectiveMaxAgeMonths { get; init; }

    /// <summary><c>timeless</c>, <c>annual</c> (pf_ddjj_anual_antiguedad_max_meses) or <c>pointInTime</c> (pf_doc_antiguedad_max_meses).</summary>
    public required string AgeClass { get; init; }
}
