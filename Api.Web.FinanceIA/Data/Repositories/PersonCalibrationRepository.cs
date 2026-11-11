// Project : Api.Web.FinanceIA
// Created : 2026-10-30

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IPersonCalibrationRepository"/>. Read-only: nothing here writes.
/// </summary>
/// <remarks>
/// Every query carries its own TenantId predicate — FinanciaDbContext has no global query filter — and the
/// subqueries too, so an id from another tenant can never widen a result.
/// </remarks>
public sealed class PersonCalibrationRepository : IPersonCalibrationRepository
{
    private readonly FinanciaDbContext _dbContext;

    /// <summary>
    /// Initializes a new instance of <see cref="PersonCalibrationRepository"/>.
    /// </summary>
    public PersonCalibrationRepository(FinanciaDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PersonCalibrationAnalysisRow>> GetAnalysesAsync(
        string tenantId, DateTime startUtc, DateTime endUtc, Guid? teamId, CancellationToken ct)
        => await AnalysesQuery(tenantId, startUtc, endUtc, teamId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <summary>The query behind <see cref="GetAnalysesAsync"/>; internal so its T-SQL can be pinned.</summary>
    internal IQueryable<PersonCalibrationAnalysisRow> AnalysesQuery(
        string tenantId, DateTime startUtc, DateTime endUtc, Guid? teamId)
    {
        var analyses = _dbContext.Analyses
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId
                     && a.AnalysisKind == AnalysisKinds.PersonaHumana
                     && a.CreatedAt >= startUtc
                     && a.CreatedAt < endUtc);

        if (teamId is { } team)
        {
            // CURRENT membership: the team is not stamped on the analysis, so the requester's row decides.
            var members = _dbContext.Users
                .Where(u => u.TenantId == tenantId && u.TeamId == team)
                .Select(u => u.UserId);

            analyses = analyses.Where(a => members.Contains(a.RequestedBy));
        }

        // The analysis' result id, for the links the board hands the Portal. A correlated subquery rather than a
        // join, so an analysis with no result row still lists; tenant-scoped on its own side like every other
        // table here, and UQ_AnalysisResults_AnalysisId makes the TOP(1) exact. Only the key is selected — none of
        // the result's nvarchar(max) blobs.
        var results = _dbContext.AnalysisResults
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId);

        return analyses
            .OrderBy(a => a.CreatedAt)
            .ThenBy(a => a.AnalysisId)
            .Select(a => new PersonCalibrationAnalysisRow
            {
                AnalysisId             = a.AnalysisId,
                EntityId               = a.EntityId,
                ResultId               = results
                    .Where(r => r.AnalysisId == a.AnalysisId)
                    .Select(r => (Guid?)r.ResultId)
                    .FirstOrDefault(),
                CreatedAt              = a.CreatedAt,
                RequestedBy            = a.RequestedBy,
                Status                 = a.Status,
                RequestedAmount        = a.RequestedAmount,
                PreApprovedBy          = a.PreApprovedBy,
                PreApprovedAt          = a.PreApprovedAt,
                ReviewedBy             = a.ReviewedBy,
                ReviewedAt             = a.ReviewedAt,
                ReviewNotes            = a.ReviewNotes,
                PreApprovalChecksJson  = a.PreApprovalChecksJson,
                ConfirmationChecksJson = a.ConfirmationChecksJson
            });
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AnalysisParticipant>> GetParticipantsAsync(
        string tenantId, IReadOnlyCollection<Guid> analysisIds, CancellationToken ct)
    {
        if (analysisIds.Count == 0)
            return [];

        return await ParticipantsQuery(tenantId, analysisIds.Distinct().ToList())
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>The query behind <see cref="GetParticipantsAsync"/>; internal so its T-SQL can be pinned.</summary>
    internal IQueryable<AnalysisParticipant> ParticipantsQuery(string tenantId, IReadOnlyList<Guid> ids)
    {
        // Detached instances (projected, never tracked) so PersonEvaluationJson's readers take them as-is.
        // ScoreJson is the one blob nothing here reads.
        return _dbContext.AnalysisParticipants
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId
                     && ids.Contains(p.AnalysisId)
                     && (p.Role == ParticipantRoles.Solicitante || p.Role == ParticipantRoles.Fiador))
            .Select(p => new AnalysisParticipant
            {
                ParticipantId         = p.ParticipantId,
                TenantId              = p.TenantId,
                AnalysisId            = p.AnalysisId,
                PersonLegajoId        = p.PersonLegajoId,
                Role                  = p.Role,
                AmountAtCharge        = p.AmountAtCharge,
                Currency              = p.Currency,
                InstallmentAmount     = p.InstallmentAmount,
                DocumentVersionsJson  = p.DocumentVersionsJson,
                FiltersJson           = p.FiltersJson,
                IndicatorsJson        = p.IndicatorsJson,
                CoverageJson          = p.CoverageJson,
                EvaluationVersion     = p.EvaluationVersion,
                CreatedAt             = p.CreatedAt,
                FiadorId              = p.FiadorId,
                GuaranteeType         = p.GuaranteeType,
                GuaranteesGivenAmount = p.GuaranteesGivenAmount
            });
    }

    /// <inheritdoc />
    public async Task<PersonCalibrationMilestoneRow> GetPilotMilestoneAsync(string tenantId, CancellationToken ct)
    {
        var persona = _dbContext.Analyses
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.AnalysisKind == AnalysisKinds.PersonaHumana);

        var count = await persona.CountAsync(ct).ConfigureAwait(false);
        if (count == 0)
            return new PersonCalibrationMilestoneRow { FirstCreatedAt = null, Count = 0 };

        var first = await persona
            .MinAsync(a => (DateTime?)a.CreatedAt, ct)
            .ConfigureAwait(false);

        return new PersonCalibrationMilestoneRow { FirstCreatedAt = first, Count = count };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PersonCalibrationRescoreRow>> GetRescoresAsync(
        string tenantId, IReadOnlyCollection<Guid> analysisIds, CancellationToken ct)
    {
        if (analysisIds.Count == 0)
            return [];

        return await RescoresQuery(tenantId, analysisIds.Distinct().ToList())
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>The query behind <see cref="GetRescoresAsync"/>; internal so its T-SQL can be pinned.</summary>
    internal IQueryable<PersonCalibrationRescoreRow> RescoresQuery(string tenantId, IReadOnlyList<Guid> ids)
    {
        return _dbContext.AnalysisRescores
            .AsNoTracking()
            .Where(h => h.TenantId == tenantId && ids.Contains(h.AnalysisId))
            .OrderByDescending(h => h.RescoredAt)
            .ThenBy(h => h.RescoreId)
            .Select(h => new PersonCalibrationRescoreRow
            {
                RescoreId          = h.RescoreId,
                AnalysisId         = h.AnalysisId,
                ResultId           = h.ResultId,
                RescoredAt         = h.RescoredAt,
                RescoredBy         = h.RescoredBy,
                // The suggestions index of PreviousValuesJson, read server-side exactly as
                // AnalysisRescoreRepository.GetHistoryAsync reads it, so the blob is never selected.
                PreviousSuggestion = SqlJsonFunctions.JsonValue(h.PreviousValuesJson, PersonRescorePreviousValues.PreviousSuggestionPath),
                NewSuggestion      = SqlJsonFunctions.JsonValue(h.PreviousValuesJson, PersonRescorePreviousValues.NewSuggestionPath)
            });
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PersonCalibrationConfigRow>> GetPersonConfigRowsAsync(string tenantId, CancellationToken ct)
    {
        var keys = PersonEvaluationDefaults.Values.Keys.ToList();

        return await _dbContext.ScoringConfigs
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId && keys.Contains(c.ConfigKey))
            .Select(c => new PersonCalibrationConfigRow
            {
                Key       = c.ConfigKey,
                Value     = c.ConfigValue,
                UpdatedAt = c.UpdatedAt
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }
}
