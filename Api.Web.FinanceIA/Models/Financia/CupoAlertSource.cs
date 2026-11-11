// Project : Api.Web.FinanceIA
// Created : 2026-10-25

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// One Active cupo with exactly what the cupo alerts need to decide and describe it — its amount and
/// currency, the analysis an alert should link to, and its cupo-occupying operations. Read by
/// <c>CreditLineRepository.GetActiveForCupoAlertsAsync</c>; no nvarchar(max) column is involved.
/// </summary>
public sealed record CupoAlertSource
{
    /// <summary>The cupo.</summary>
    public Guid CreditLineId { get; init; }

    /// <summary>The PyME that owns the cupo; becomes the alert's EntityId.</summary>
    public Guid EntityId { get; init; }

    /// <summary>The cupo amount (tope).</summary>
    public decimal Amount { get; init; }

    /// <summary>The cupo currency; every operation under it carries the same one.</summary>
    public string Currency { get; init; } = string.Empty;

    /// <summary>
    /// COALESCE(LastRaiseAnalysisId, OriginAnalysisId): the analysis behind the cupo's current amount.
    /// Null for a never-raised cupo that predates OriginAnalysisId.
    /// </summary>
    public Guid? AnalysisId { get; init; }

    /// <summary>
    /// The operations for which <c>CreditLineRules.OccupiesCupo</c> holds, i.e. every one not 'Cancelada'.
    /// </summary>
    public IReadOnlyList<CreditLineOperation> OccupyingOperations { get; init; } = Array.Empty<CreditLineOperation>();
}
