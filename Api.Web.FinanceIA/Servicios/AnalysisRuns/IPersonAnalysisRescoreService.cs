// Project : Api.Web.FinanceIA
// Created : 2026-10-30

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.AnalysisRuns;

/// <summary>
/// The persona humana branch of POST /api/analyses/{id}/rescore: re-evaluates an OPEN persona analysis in
/// place with the current legajos, BCRA snapshots and pf_ parameters, keeping the previous evaluation in
/// [AnalysisRescores]. Reached only through <see cref="IAnalysisRescoreService"/>, which has already
/// applied the eligibility rules and the cooldown both kinds share.
/// </summary>
public interface IPersonAnalysisRescoreService
{
    /// <summary>
    /// Re-evaluates <paramref name="analysis"/> (whose result is <paramref name="result"/>) at
    /// <paramref name="now"/>. Every outcome but a 200 leaves the analysis and the history exactly as they
    /// were.
    /// </summary>
    Task<AnalysisRescoreOutcome> RescoreAsync(
        Analysis analysis, AnalysisResult result, string tenantId, Guid userId, DateTime now,
        string? ipAddress, string? requestId, CancellationToken ct);
}
