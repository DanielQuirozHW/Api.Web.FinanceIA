// Project : Api.Web.FinanceIA
// Created : 2026-10-22

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.BalanceCorrections;

namespace Api.Web.FinanceIA.Servicios.AnalysisRuns;

/// <summary>
/// "Reanalizar con datos actualizados": re-scores a balance analysis in place from its persisted
/// extraction, with a live BCRA lookup and the entity's current snapshots — never a new extraction.
/// </summary>
public interface IAnalysisRescoreService
{
    /// <summary>
    /// Re-scores the result <paramref name="resultId"/>. Every outcome but a 200 leaves the result and
    /// the history exactly as they were.
    /// </summary>
    Task<AnalysisRescoreOutcome> RescoreAsync(
        Guid resultId, string tenantId, Guid userId, string? ipAddress, string? requestId, CancellationToken ct);

    /// <summary>
    /// Corrects figures of the result's extracted balance and re-scores it over the corrected balance, in ONE
    /// transaction: the corrected BalanceJson, the re-score, its history row and one [AnalysisEdits] row per
    /// figure — or nothing. Same eligibility, cooldown and gates as <see cref="RescoreAsync"/>; a persona
    /// humana analysis is refused (409 person_analysis_not_correctable); a correction the stored balance
    /// fails (a missing line, an unchanged value) is a 400 with field paths.
    /// </summary>
    Task<AnalysisRescoreOutcome> CorrectBalanceAsync(
        Guid resultId, string tenantId, Guid userId, BalanceCorrectionCommand command,
        string? ipAddress, string? requestId, CancellationToken ct);

    /// <summary>
    /// The re-scores of the result's analysis, newest first; null when the result is not in this tenant.
    /// </summary>
    Task<IReadOnlyList<AnalysisRescoreHistoryItem>?> GetHistoryAsync(
        Guid resultId, string tenantId, CancellationToken ct);
}
