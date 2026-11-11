// Project : Api.Web.FinanceIA
// Created : 2026-11-06

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.AnalysisComparison;

/// <summary>"Cambios respecto al análisis anterior": an analysis against the previous one of the same company.</summary>
public interface IAnalysisComparisonService
{
    /// <summary>
    /// The comparison for the analysis behind <paramref name="resultId"/>; null when that result is not
    /// <paramref name="tenantId"/>'s. Never throws for a malformed blob.
    /// </summary>
    Task<AnalysisComparisonResponse?> CompareWithPreviousAsync(
        Guid resultId, string tenantId, CancellationToken ct);
}
