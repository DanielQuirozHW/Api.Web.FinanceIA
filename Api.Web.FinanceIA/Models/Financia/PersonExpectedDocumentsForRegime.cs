// Project : Api.Web.FinanceIA
// Created : 2026-10-31

using System.Text.Json.Serialization;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>Where an expected-documents list came from.</summary>
public static class ExpectedDocumentsSources
{
    /// <summary>The tenant's own matrix (TenantSettings <c>person.expected_documents</c>).</summary>
    public const string Tenant = "tenant";

    /// <summary>The code default (<see cref="LegajoExpectedDocuments"/>).</summary>
    public const string Default = "default";
}

/// <summary>
/// The expected documents of ONE regime bucket as resolved for a tenant: what the legajo checklist shows and
/// what the coverage index measures (engine 0.10). Frozen as is in
/// <see cref="PersonEvaluationSources.ExpectedDocuments"/>, so a past coverage can be told apart from a later
/// change to the tenant's matrix.
/// </summary>
public sealed record PersonExpectedDocumentsForRegime
{
    /// <summary>The bucket (<see cref="ExpectedDocumentRegimes"/>), not the legajo's raw regime.</summary>
    public required string Regime { get; init; }

    /// <summary><see cref="ExpectedDocumentsSources.Tenant"/> or <see cref="ExpectedDocumentsSources.Default"/>.</summary>
    public required string Source { get; init; }

    /// <summary>When the tenant's row was last written; null on a default.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? UpdatedAt { get; init; }

    /// <summary>The lines, in display order.</summary>
    public required IReadOnlyList<LegajoExpectedDocument> Lines { get; init; }
}
