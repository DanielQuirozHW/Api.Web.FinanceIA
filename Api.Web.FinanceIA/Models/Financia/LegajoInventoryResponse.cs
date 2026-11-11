// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// GET /api/person-legajos/{id}/inventory (M10 Phase 3b): the legajo's reconciled inventory, grouped by
/// side and kind, with totals computed at read time. PERSONAL DATA: returned to the tenant's users only.
/// </summary>
public sealed record LegajoInventoryResponse
{
    /// <summary>The legajo.</summary>
    public required Guid PersonLegajoId { get; init; }

    /// <summary>Activo groups first, then Pasivo; kinds in LegajoItemKinds order.</summary>
    public required IReadOnlyList<LegajoInventoryGroupResponse> Groups { get; init; }

    /// <summary>Totals over every item.</summary>
    public required LegajoInventoryTotalsResponse Totals { get; init; }

    /// <summary>Items per reconciliation status (every status present, zero included).</summary>
    public required IReadOnlyDictionary<string, int> StatusCounts { get; init; }

    /// <summary>The Bienes Personales document the fiscal values come from (none, or one).</summary>
    public required IReadOnlyList<Guid> BienesPersonalesDocumentIds { get; init; }

    /// <summary>The manifestación the market values come from (none, or one).</summary>
    public required IReadOnlyList<Guid> ManifestacionDocumentIds { get; init; }

    /// <summary>The latest change to any item (UTC), or null when the inventory is empty.</summary>
    public DateTime? LastUpdatedAt { get; init; }

    /// <summary>
    /// The avales y/o coobligaciones otorgados the manifestación of <see cref="ManifestacionDocumentIds"/> declares,
    /// in pesos, on its effective values (mb-1.2). Null when it prints none, or was extracted before mb-1.2 and has
    /// not been reprocessed. Informative only: the evaluation never applies it — the analyst still states
    /// guaranteesGivenAmount, which the Portal can pre-fill from here.
    /// </summary>
    public decimal? GarantiasOtorgadasDeclaradas { get; init; }

    /// <summary>The manifestación <see cref="GarantiasOtorgadasDeclaradas"/> was read from; null exactly when it is.</summary>
    public Guid? GarantiasOtorgadasDocumentId { get; init; }
}
