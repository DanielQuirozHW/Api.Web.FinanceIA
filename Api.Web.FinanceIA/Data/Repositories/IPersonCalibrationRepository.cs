// Project : Api.Web.FinanceIA
// Created : 2026-10-30

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// The reads behind the persona humana calibration board (GET /api/dashboard/person-calibration).
/// READ-ONLY, tenant-scoped, AsNoTracking and narrow: every method selects only the columns the board uses.
/// </summary>
public interface IPersonCalibrationRepository
{
    /// <summary>
    /// The PersonaHumana analyses whose CreatedAt falls in <c>[startUtc, endUtc)</c>, oldest first. With
    /// <paramref name="teamId"/>, only those whose requester's CURRENT Users.TeamId is that team.
    /// </summary>
    Task<IReadOnlyList<PersonCalibrationAnalysisRow>> GetAnalysesAsync(
        string tenantId, DateTime startUtc, DateTime endUtc, Guid? teamId, CancellationToken ct);

    /// <summary>
    /// The Solicitante and Fiador rows of the given analyses, as detached <see cref="AnalysisParticipant"/>
    /// instances carrying every column the PersonEvaluationJson readers use — ScoreJson excepted.
    /// </summary>
    Task<IReadOnlyList<AnalysisParticipant>> GetParticipantsAsync(
        string tenantId, IReadOnlyCollection<Guid> analysisIds, CancellationToken ct);

    /// <summary>The tenant's first persona analysis and how many it has to date, whatever the window.</summary>
    Task<PersonCalibrationMilestoneRow> GetPilotMilestoneAsync(string tenantId, CancellationToken ct);

    /// <summary>
    /// The AnalysisRescores rows of the given analyses, newest first, with the persona suggestions read
    /// server-side exactly as AnalysisRescoreRepository.GetHistoryAsync reads them.
    /// </summary>
    Task<IReadOnlyList<PersonCalibrationRescoreRow>> GetRescoresAsync(
        string tenantId, IReadOnlyCollection<Guid> analysisIds, CancellationToken ct);

    /// <summary>The tenant's ScoringConfig rows for the keys of PersonEvaluationDefaults, with UpdatedAt.</summary>
    Task<IReadOnlyList<PersonCalibrationConfigRow>> GetPersonConfigRowsAsync(string tenantId, CancellationToken ct);
}
