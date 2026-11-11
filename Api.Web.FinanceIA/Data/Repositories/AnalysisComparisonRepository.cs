// Project : Api.Web.FinanceIA
// Created : 2026-11-06

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IAnalysisComparisonRepository"/>. Read-only, AsNoTracking,
/// projected: every query names its columns, filters TenantId on both tables (there is no global query
/// filter), and reads BcraDataJson — and, in the previous lookup's WHERE, BalanceJson — only through
/// <see cref="SqlJsonFunctions.JsonValue"/>, so neither blob is ever in a result set.
/// </summary>
public sealed class AnalysisComparisonRepository : IAnalysisComparisonRepository
{
    /// <summary>The statuses an analysis must have to be compared against: decided ones.</summary>
    internal static readonly IReadOnlyList<string> ComparableStatuses =
        [AnalysisStatuses.Approved, AnalysisStatuses.PreApproved, AnalysisStatuses.Rejected];

    private readonly FinanciaDbContext _dbContext;

    public AnalysisComparisonRepository(FinanciaDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<AnalysisComparisonSnapshot?> GetSnapshotByResultIdAsync(
        Guid resultId, string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        return await SnapshotQuery(resultId, tenantId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AnalysisComparisonSnapshot?> GetPreviousSnapshotAsync(
        string tenantId,
        Guid entityId,
        string analysisKind,
        Guid currentAnalysisId,
        DateTime currentCreatedAt,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(analysisKind);

        // First a scalar query — no blob named — that picks the one result; then the blob projection for
        // that result alone. The candidates of a busy company never ship their BalanceJson.
        var previousResultId = await PreviousResultIdQuery(tenantId, entityId, analysisKind, currentAnalysisId, currentCreatedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (previousResultId is not { } id)
            return null;

        return await GetSnapshotByResultIdAsync(id, tenantId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PersonComparisonFacts?> GetPersonFactsAsync(
        Guid analysisId, string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var facts = await PersonFactsQuery(analysisId, tenantId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (facts is null)
            return null;

        facts.FiadorIds = await _dbContext.AnalysisParticipants
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId
                     && p.AnalysisId == analysisId
                     && p.Role == ParticipantRoles.Fiador
                     && p.FiadorId != null)
            .Select(p => p.FiadorId!.Value)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return facts;
    }

    /// <summary>
    /// The previous result's id, best first: scalars only, both tables scoped to <paramref name="tenantId"/>.
    /// Internal (InternalsVisibleTo) so a test can compile it to T-SQL and pin that no blob is selected.
    /// </summary>
    /// <remarks>
    /// What makes an analysis comparable depends on its kind, so the two kinds get separate predicates
    /// rather than one query with a kind switch in it:
    /// <list type="bullet">
    /// <item>A company (Balance) analysis needs a score AND a balance that was actually read: at least one of
    /// assets, equity, net_revenue, net_profit in <c>BalanceJson</c>. Read with <c>JSON_VALUE</c> in the
    /// WHERE clause, so the blob is inspected server-side and never shipped. A decided analysis with nothing
    /// read must not become "the previous".</item>
    /// <item>A persona humana analysis has no score; it needs a stored evaluation — its Solicitante row in
    /// [dbo].[AnalysisParticipants], tenant-scoped.</item>
    /// </list>
    /// </remarks>
    internal IQueryable<Guid?> PreviousResultIdQuery(
        string tenantId, Guid entityId, string analysisKind, Guid currentAnalysisId, DateTime currentCreatedAt)
    {
        var statuses = ComparableStatuses.ToList();

        var candidates = from a in _dbContext.Analyses.AsNoTracking()
                         join r in _dbContext.AnalysisResults.AsNoTracking() on a.AnalysisId equals r.AnalysisId
                         where a.TenantId == tenantId
                            && r.TenantId == tenantId
                            && a.EntityId == entityId
                            && a.AnalysisKind == analysisKind
                            && statuses.Contains(a.Status)
                            && a.CreatedAt < currentCreatedAt
                            && a.AnalysisId != currentAnalysisId
                         select new { a, r };

        candidates = string.Equals(analysisKind, AnalysisKinds.PersonaHumana, StringComparison.Ordinal)
            ? candidates.Where(x => _dbContext.AnalysisParticipants.Any(p =>
                  p.TenantId == tenantId
                  && p.AnalysisId == x.a.AnalysisId
                  && p.Role == ParticipantRoles.Solicitante))
            : candidates.Where(x =>
                  x.r.AiSuggestedScore != null
                  && (SqlJsonFunctions.JsonValue(x.r.BalanceJson, "$.assets") != null
                      || SqlJsonFunctions.JsonValue(x.r.BalanceJson, "$.equity") != null
                      || SqlJsonFunctions.JsonValue(x.r.BalanceJson, "$.net_revenue") != null
                      || SqlJsonFunctions.JsonValue(x.r.BalanceJson, "$.net_profit") != null));

        return candidates
            .OrderByDescending(x => x.a.ReviewedAt ?? x.a.PreApprovedAt ?? x.a.CreatedAt)
            .ThenByDescending(x => x.a.CreatedAt)
            .ThenByDescending(x => x.a.AnalysisId)
            .Select(x => (Guid?)x.r.ResultId);
    }

    /// <summary>
    /// The Solicitante row of one analysis, projected to the columns the comparison reads, tenant-scoped.
    /// Internal for the T-SQL shape test.
    /// </summary>
    internal IQueryable<PersonComparisonFacts> PersonFactsQuery(Guid analysisId, string tenantId)
        => _dbContext.AnalysisParticipants
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId
                     && p.AnalysisId == analysisId
                     && p.Role == ParticipantRoles.Solicitante)
            .OrderBy(p => p.CreatedAt)
            .ThenBy(p => p.ParticipantId)
            .Select(p => new PersonComparisonFacts
            {
                FiltersJson          = p.FiltersJson,
                IndicatorsJson       = p.IndicatorsJson,
                CoverageJson         = p.CoverageJson,
                DocumentVersionsJson = p.DocumentVersionsJson,
                EvaluationVersion    = p.EvaluationVersion,
                AmountAtCharge       = p.AmountAtCharge,
                Currency             = p.Currency,
                InstallmentAmount    = p.InstallmentAmount,
                CuotasPorAnio        = p.CuotasPorAnio,
            });

    /// <summary>
    /// One result joined to its analysis, both scoped to <paramref name="tenantId"/>. Filtered BEFORE the
    /// projection so <see cref="SqlJsonFunctions.JsonValue"/> sits in the top-level select, where the
    /// InMemory provider can client-evaluate it. Internal for the T-SQL shape test.
    /// </summary>
    internal IQueryable<AnalysisComparisonSnapshot> SnapshotQuery(Guid resultId, string tenantId)
        => from r in _dbContext.AnalysisResults.AsNoTracking()
           join a in _dbContext.Analyses.AsNoTracking() on r.AnalysisId equals a.AnalysisId
           where r.ResultId == resultId && r.TenantId == tenantId && a.TenantId == tenantId
           select new AnalysisComparisonSnapshot
           {
               ResultId           = r.ResultId,
               AnalysisId         = a.AnalysisId,
               EntityId           = a.EntityId,
               AnalysisKind       = a.AnalysisKind,
               Status             = a.Status,
               FiscalYear         = a.FiscalYear,
               PromptVersion      = a.PromptVersion,
               CreatedAt          = a.CreatedAt,
               ReviewedAt         = a.ReviewedAt,
               PreApprovedAt      = a.PreApprovedAt,
               AiSuggestedScore   = r.AiSuggestedScore,
               AiSuggestedLevel   = r.AiSuggestedLevel,
               BalanceJson        = r.BalanceJson,
               RatiosJson         = r.RatiosJson,
               ScoreBreakdownJson = r.ScoreBreakdownJson,

               // Paths are case-sensitive in T-SQL and the stored keys are camelCase.
               BcraDeudaEstadoRaw                = SqlJsonFunctions.JsonValue(r.BcraDataJson, "$.deuda.estado"),
               BcraDeudaSituacionMaximaRaw       = SqlJsonFunctions.JsonValue(r.BcraDataJson, "$.deuda.situacionMaxima"),
               BcraDeudaMontoTotalRaw            = SqlJsonFunctions.JsonValue(r.BcraDataJson, "$.deuda.montoTotal"),
               BcraChequesEstadoRaw              = SqlJsonFunctions.JsonValue(r.BcraDataJson, "$.cheques.estado"),
               BcraChequesTotalRechazadosRaw     = SqlJsonFunctions.JsonValue(r.BcraDataJson, "$.cheques.totalRechazados"),
               BcraChequesMontoTotalRechazadoRaw = SqlJsonFunctions.JsonValue(r.BcraDataJson, "$.cheques.montoTotalRechazado"),
           };
}
