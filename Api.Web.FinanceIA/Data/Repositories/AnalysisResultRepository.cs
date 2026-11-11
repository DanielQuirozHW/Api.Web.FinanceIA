// Project : Api.Web.FinanceIA
// Created : 2026-05-04
// Modified: 2026-05-04

using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IAnalysisResultRepository"/>.
/// Read queries use AsNoTracking. Write operations call
/// ChangeTracker.DetectChanges() manually because AutoDetectChangesEnabled
/// is disabled on <see cref="FinanciaDbContext"/>.
/// </summary>
public sealed class AnalysisResultRepository : IAnalysisResultRepository
{
    private readonly FinanciaDbContext _dbContext;
    private readonly ILogger<AnalysisResultRepository> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="AnalysisResultRepository"/>.
    /// </summary>
    public AnalysisResultRepository(FinanciaDbContext dbContext, ILogger<AnalysisResultRepository> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<AnalysisResult?> GetByIdAsync(Guid resultId, string tenantId, CancellationToken ct)
    {
        return await _dbContext.AnalysisResults
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.ResultId == resultId && x.TenantId == tenantId, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AnalysisResult>> GetAllByTenantAsync(string tenantId, CancellationToken ct)
    {
        return await _dbContext.AnalysisResults
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AnalysisResult?> GetByDocumentIdAsync(Guid documentId, string tenantId, CancellationToken ct)
    {
        var analysisIds = _dbContext.Analyses
            .Where(a => a.DocumentId == documentId && a.TenantId == tenantId)
            .Select(a => a.AnalysisId);

        return await _dbContext.AnalysisResults
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && analysisIds.Contains(x.AnalysisId))
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AnalysisResult> CreateAsync(AnalysisResult result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);

        _dbContext.AnalysisResults.Add(result);
        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "AnalysisResult created: {ResultId} for tenant {TenantId}",
            result.ResultId,
            result.TenantId);

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AnalysisResult>> GetByEntityIdAsync(Guid entityId, string tenantId, CancellationToken ct)
    {
        var documentIds = _dbContext.Documents
            .Where(d => d.EntityId == entityId && d.TenantId == tenantId && !d.IsDeleted)
            .Select(d => d.DocumentId);

        var analysisIds = EntityAnalysisIds(entityId, tenantId, documentIds);

        return await _dbContext.AnalysisResults
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && analysisIds.Contains(x.AnalysisId))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AnalysisResult> UpdateAsync(AnalysisResult result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);

        _dbContext.AnalysisResults.Update(result);
        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "AnalysisResult updated: {ResultId} for tenant {TenantId}",
            result.ResultId,
            result.TenantId);

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, Guid>> GetResultIdsByAnalysisIdsAsync(
        IEnumerable<Guid> analysisIds, string tenantId, CancellationToken ct)
    {
        var ids = analysisIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, Guid>();

        var pairs = await _dbContext.AnalysisResults
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && ids.Contains(x.AnalysisId))
            .Select(x => new { x.AnalysisId, x.ResultId })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // UQ_AnalysisResults_AnalysisId makes this 1:1; last-write-wins is a harmless safety net.
        var map = new Dictionary<Guid, Guid>(pairs.Count);
        foreach (var p in pairs)
            map[p.AnalysisId] = p.ResultId;

        return map;
    }

    // ── List paging ───────────────────────────────────────────────────────────────
    // Same idea as GetResultIdsByAnalysisIdsAsync above — an EF .Select(...) so the emitted SQL
    // names only the columns wanted — applied to the list read, where it matters most: without it
    // the query drags 14 nvarchar(max) columns off disk for every row in the tenant.

    /// <inheritdoc />
    public Task<(IReadOnlyList<AnalysisListProjection> Items, int TotalCount)> GetListPageByTenantAsync(
        string tenantId, int skip, int take, CancellationToken ct)
        => ReadPageAsync(TenantSource(tenantId), skip, take, ct);

    /// <inheritdoc />
    public Task<(IReadOnlyList<AnalysisListProjection> Items, int TotalCount)> GetListPageByEntityAsync(
        Guid entityId, string tenantId, int skip, int take, CancellationToken ct)
        => ReadPageAsync(EntitySource(entityId, tenantId), skip, take, ct);

    /// <summary>The tenant's results, unordered and unpaged. The filter the COUNT also runs on.</summary>
    private IQueryable<AnalysisResult> TenantSource(string tenantId)
        => _dbContext.AnalysisResults
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId);

    /// <summary>
    /// One entity's results: the chain <see cref="GetByEntityIdAsync"/> resolves, unchanged.
    /// documentIds and analysisIds are never enumerated — they stay <see cref="IQueryable{T}"/> so EF
    /// folds all three into one statement with two nested IN subqueries rather than three
    /// round-trips. Soft-deleted documents are excluded here, as they are there.
    /// </summary>
    private IQueryable<AnalysisResult> EntitySource(Guid entityId, string tenantId)
    {
        var documentIds = _dbContext.Documents
            .Where(d => d.EntityId == entityId && d.TenantId == tenantId && !d.IsDeleted)
            .Select(d => d.DocumentId);

        var analysisIds = EntityAnalysisIds(entityId, tenantId, documentIds);

        return _dbContext.AnalysisResults
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && analysisIds.Contains(x.AnalysisId));
    }

    /// <summary>
    /// The analyses of one entity. A BALANCE analysis is reached through its live document, exactly as
    /// before DDL 072, so an analysis of a soft-deleted balance stays hidden. A PERSONA HUMANA analysis
    /// has no document (DocumentId NULL) and is reached by its own EntityId instead — without this arm
    /// it would be invisible in the entity's list.
    /// </summary>
    private IQueryable<Guid> EntityAnalysisIds(Guid entityId, string tenantId, IQueryable<Guid> documentIds)
        => _dbContext.Analyses
            .Where(a => a.TenantId == tenantId
                && ((a.DocumentId != null && documentIds.Contains(a.DocumentId.Value))
                    || (a.AnalysisKind == AnalysisKinds.PersonaHumana && a.EntityId == entityId)))
            .Select(a => a.AnalysisId);

    /// <summary>
    /// Counts and pages one filtered set. Both callers share this so the COUNT and the page can
    /// never drift onto different filters.
    /// </summary>
    private static async Task<(IReadOnlyList<AnalysisListProjection> Items, int TotalCount)> ReadPageAsync(
        IQueryable<AnalysisResult> source, int skip, int take, CancellationToken ct)
    {
        var total = await source.CountAsync(ct).ConfigureAwait(false);

        var items = await BuildListPage(source, skip, take)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return (items, total);
    }

    /// <summary>
    /// The projection, the ordering and the paging — the whole reason this class grew a list method.
    /// Naming only these eight columns is what stops SQL Server reading 14 nvarchar(max) columns off
    /// disk per row (EnrichmentStatus and RescoredAt are short scalars, for the score-reliability verdict).
    /// </summary>
    private static IQueryable<AnalysisListProjection> BuildListPage(
        IQueryable<AnalysisResult> source, int skip, int take)
        => source
            .Select(x => new AnalysisListProjection
            {
                ResultId           = x.ResultId,
                AnalysisId         = x.AnalysisId,
                GeneratedAt        = x.GeneratedAt,
                AiSuggestedScore   = x.AiSuggestedScore,
                AiSuggestedLevel   = x.AiSuggestedLevel,
                EnrichmentStatus   = x.EnrichmentStatus,
                RescoredAt         = x.RescoredAt,
                ScoreBreakdownJson = x.ScoreBreakdownJson
            })
            .OrderByDescending(p => p.GeneratedAt)
            .ThenBy(p => p.ResultId)   // unique tie-break — see the interface doc
            .Skip(skip)
            .Take(take);

    // ── Detail and on-demand context snapshots ────────────────────────────────────
    // The same argument as the list paging above, applied to GET /api/analyses/{id}. GetByIdAsync
    // reads all fourteen nvarchar(max) columns, five of which the detail page does not render on
    // load — the four frozen "Contexto" snapshots (Lufe, Fiscal, Casfog, Nosis) plus BcraDataJson.
    // BuildDetail omits all five (and three more columns nothing on this path reads); BuildContext
    // below serves them when they are actually asked for.
    //
    // BcraDataJson is the fifth as of this change. #143 kept it because "the Score renders it on
    // load", which is true of exactly two scalars off it — deuda.estado and deuda.situacionMaxima.
    // The rest, ~40KB of a ~73KB payload on a heavy row, is Contexto's BCRA tab. So the blob joins
    // BuildContext and BuildDetail extracts the two scalars with JSON_VALUE instead, naming the
    // column only as an argument. SQL Server still reads the LOB to parse it; what this saves is
    // the transfer and the client-side deserialisation of ~3000 cheque objects.
    //
    // GetByIdAsync is untouched — the decision, chat and PDF-export paths need the whole row.

    /// <inheritdoc />
    public async Task<AnalysisResultDetailProjection?> GetDetailByIdAsync(
        Guid resultId, string tenantId, CancellationToken ct)
    {
        return await BuildDetail(ResultByIdSource(resultId, tenantId), _dbContext.AnalysisRescores)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AnalysisContextProjection?> GetContextSnapshotByIdAsync(
        Guid resultId, string tenantId, CancellationToken ct)
    {
        return await BuildContext(ResultByIdSource(resultId, tenantId))
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AnalysisBcraProjection?> GetBcraSnapshotByIdAsync(
        Guid resultId, string tenantId, CancellationToken ct)
    {
        return await BuildBcra(ResultByIdSource(resultId, tenantId))
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AnalysisRatiosProjection?> GetRatiosByIdAsync(
        Guid resultId, string tenantId, CancellationToken ct)
    {
        return await BuildRatios(ResultByIdSource(resultId, tenantId))
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AnalysisLabelProjection?> GetLabelByIdAsync(
        Guid resultId, string tenantId, CancellationToken ct)
    {
        return await BuildLabel(ResultByIdSource(resultId, tenantId), tenantId)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// One result, tenant-scoped. The filter both the detail and the context read compose on, so
    /// tenant isolation is declared once and cannot drift between them.
    /// </summary>
    private IQueryable<AnalysisResult> ResultByIdSource(Guid resultId, string tenantId)
        => _dbContext.AnalysisResults
            .AsNoTracking()
            .Where(x => x.ResultId == resultId && x.TenantId == tenantId);

    /// <summary>
    /// The detail projection. Naming these columns — and NOT LufeDataJson, FiscalDataJson,
    /// CasfogDataJson, NosisDataJson or BcraDataJson — is what stops SQL Server shipping five
    /// <c>nvarchar(max)</c> columns for sections the page does not render on load. MacroDataJson,
    /// ValidationNotes, AiInputHash and AiOutputHash are absent for a different reason: nothing on
    /// this path reads them.
    ///
    /// <para>
    /// BcraDataJson appears here only INSIDE <see cref="SqlJsonFunctions.JsonValue"/>, which
    /// translates to the T-SQL built-in <c>JSON_VALUE</c>. SQL Server resolves the path and returns
    /// a few characters, so the blob is never in the result set. That distinction is what
    /// <c>DetailQuery_ExtractsTheTwoScoreFieldsAndNeverProjectsTheBlob</c> asserts — a plain
    /// substring check on the emitted SQL cannot tell the two apart, since the column name is
    /// present either way.
    /// </para>
    /// </summary>
    private static IQueryable<AnalysisResultDetailProjection> BuildDetail(
        IQueryable<AnalysisResult> source, IQueryable<AnalysisRescore> rescores)
        => source
            .Select(x => new AnalysisResultDetailProjection
            {
                ResultId           = x.ResultId,
                AnalysisId         = x.AnalysisId,
                TenantId           = x.TenantId,
                GeneratedAt        = x.GeneratedAt,
                BalanceJson        = x.BalanceJson,
                RatiosJson         = x.RatiosJson,
                ScoreBreakdownJson = x.ScoreBreakdownJson,
                ExecutiveSummary   = x.ExecutiveSummary,
                ScoringNarrative   = x.ScoringNarrative,
                RiskAlerts         = x.RiskAlerts,
                CatalogueAlerts    = x.CatalogueAlerts,
                Anomalies          = x.Anomalies,
                AiSuggestedScore   = x.AiSuggestedScore,
                AiSuggestedLevel   = x.AiSuggestedLevel,
                AiInputTokens      = x.AiInputTokens,
                AiOutputTokens     = x.AiOutputTokens,
                ValidationStatus   = x.ValidationStatus,
                EnrichmentStatus   = x.EnrichmentStatus,
                EnrichmentNotes    = x.EnrichmentNotes,

                // The scalars read off BcraDataJson — the two the Score section shows and the deuda's
                // fetch date the BCRA source is dated by — and the only reason that column is
                // mentioned in this query at all. Paths are case-sensitive in T-SQL and the stored
                // keys are camelCase (explicit [JsonPropertyName] attributes), so '$.Deuda.Estado'
                // would silently return NULL for every row.
                BcraDeudaEstadoRaw          = SqlJsonFunctions.JsonValue(x.BcraDataJson, "$.deuda.estado"),
                BcraDeudaSituacionMaximaRaw = SqlJsonFunctions.JsonValue(x.BcraDataJson, "$.deuda.situacionMaxima"),
                BcraDeudaFetchedAtRaw       = SqlJsonFunctions.JsonValue(x.BcraDataJson, "$.deuda.fetchedAt"),

                // Re-score (DDL 073). The latest history row's SourcesJson is a correlated subquery
                // that seeks on IX_AnalysisRescores_TenantId_AnalysisId_RescoredAt; it is a short
                // array of dates, never a payload, and NULL for a result that was never re-scored.
                RescoredAt               = x.RescoredAt,
                RescoredBy               = x.RescoredBy,
                RescoreCount             = x.RescoreCount,
                LatestRescoreSourcesJson = rescores
                    .Where(h => h.TenantId == x.TenantId && h.AnalysisId == x.AnalysisId)
                    .OrderByDescending(h => h.RescoredAt)
                    .Select(h => h.SourcesJson)
                    .FirstOrDefault()
            });

    /// <summary>
    /// The context projection: the key and the five stored source blobs, nothing else. One query
    /// rather than five single-column ones, because the Contexto section renders them together —
    /// which is why this does not follow the one-blob-per-read shape the ENTITY snapshots use, where
    /// the user opens one tab at a time.
    ///
    /// <para>
    /// BcraDataJson is the odd one out and joined the set after the other four: it is not a frozen
    /// copy of an entity snapshot but BCRA data fetched at analysis time, and unlike the other four
    /// the detail still needs two scalars out of it — which it takes with JSON_VALUE rather than by
    /// reading the column. Here it is read whole, because the BCRA tab renders all of it.
    /// </para>
    /// </summary>
    private static IQueryable<AnalysisContextProjection> BuildContext(IQueryable<AnalysisResult> source)
        => source
            .Select(x => new AnalysisContextProjection
            {
                ResultId       = x.ResultId,
                LufeDataJson   = x.LufeDataJson,
                FiscalDataJson = x.FiscalDataJson,
                CasfogDataJson = x.CasfogDataJson,
                NosisDataJson  = x.NosisDataJson,
                BcraDataJson   = x.BcraDataJson
            });

    /// <summary>
    /// The BCRA projection: the key and one blob, nothing else. Naming only this column is what
    /// stops the cheque table paying for the four frozen snapshots beside it —
    /// <see cref="BuildContext"/> reads all five, LufeDataJson among them at 26KB on a
    /// representative row, because the Contexto section renders them together. This request renders
    /// one table, so it reads one column.
    ///
    /// <para>
    /// Read WHOLE, as a projected column rather than through <c>JSON_VALUE</c>: the caller needs the
    /// per-cheque array itself, not a scalar out of it. That is the opposite of
    /// <see cref="BuildDetail"/>, which names the same column only as a JSON_VALUE argument.
    /// </para>
    /// </summary>
    private static IQueryable<AnalysisBcraProjection> BuildBcra(IQueryable<AnalysisResult> source)
        => source
            .Select(x => new AnalysisBcraProjection
            {
                ResultId     = x.ResultId,
                BcraDataJson = x.BcraDataJson
            });

    /// <summary>
    /// The ratios projection: the key and the two blobs the PyME detail's ratios panel renders,
    /// nothing else. Naming these three columns is what stops that panel paying for the seven
    /// nvarchar(max) columns the detail read still names — BalanceJson among them — none of which
    /// the panel shows. One query rather than two, because the ratio table and its score badges
    /// render together.
    /// </summary>
    private static IQueryable<AnalysisRatiosProjection> BuildRatios(IQueryable<AnalysisResult> source)
        => source
            .Select(x => new AnalysisRatiosProjection
            {
                ResultId           = x.ResultId,
                RatiosJson         = x.RatiosJson,
                ScoreBreakdownJson = x.ScoreBreakdownJson
            });

    /// <summary>
    /// The label projection: one string, reached across two joins. ResultId → AnalysisId → EntityId →
    /// BusinessName, in a single statement that names exactly five columns — three of them keys — and
    /// not one <c>nvarchar(max)</c> on any of the three tables.
    ///
    /// <para>
    /// An instance method rather than a <c>static</c> one like its siblings above, because it is the
    /// only projection here that reads from a table other than AnalysisResults and so needs
    /// <c>_dbContext</c> for the two joined sets. There are no navigation properties anywhere in this
    /// model, so the joins are explicit over the scalar FKs.
    /// </para>
    ///
    /// <para>
    /// The INNER JOINs cannot lose a row: <c>AnalysisResults.AnalysisId</c> and
    /// <c>Analyses.EntityId</c> are both NOT NULL and both carry a foreign key
    /// (<c>FK_AnalysisResults_AnalysisId</c>, <c>FK_Analyses_EntityId</c>). A null return therefore
    /// means the RESULT does not exist in this tenant, never that its name went missing.
    /// </para>
    ///
    /// <para>
    /// The tenant predicate is repeated on all three tables rather than relying on the one
    /// <see cref="ResultByIdSource"/> already applies. Transitively the analysis and the entity must
    /// belong to the same tenant as the result, so the two extra predicates change no row today —
    /// they are the same defence <c>EntitySource</c> applies across its three-table chain, and the
    /// reason a cross-tenant row could never leak a name through this path. Both are index-backed
    /// (<c>IX_Entities_TenantId</c>) and neither costs a scan: the joins are primary-key seeks.
    /// </para>
    /// </summary>
    private IQueryable<AnalysisLabelProjection> BuildLabel(
        IQueryable<AnalysisResult> source, string tenantId)
        => from r in source
           join a in _dbContext.Analyses.Where(a => a.TenantId == tenantId)
                on r.AnalysisId equals a.AnalysisId
           join e in _dbContext.Entities.Where(e => e.TenantId == tenantId)
                on a.EntityId equals e.EntityId
           select new AnalysisLabelProjection
           {
               ResultId   = r.ResultId,
               EntityId   = a.EntityId,
               EntityName = e.BusinessName
           };

    // ── Test seams ────────────────────────────────────────────────────────────────
    // Internal (InternalsVisibleTo: Api.Web.FinanceIA.Tests) so a test can call ToQueryString() and
    // assert the SQL these exact queries emit. They compose the SAME source builders and the SAME
    // BuildListPage the public methods above run, so the assertion is against the production query
    // rather than a re-declared copy of it that could drift.

    internal IQueryable<AnalysisListProjection> TenantListPageQuery(string tenantId, int skip, int take)
        => BuildListPage(TenantSource(tenantId), skip, take);

    internal IQueryable<AnalysisListProjection> EntityListPageQuery(Guid entityId, string tenantId, int skip, int take)
        => BuildListPage(EntitySource(entityId, tenantId), skip, take);

    // Same reasoning for the detail and context reads: these compose the SAME ResultByIdSource and
    // the SAME Build* the public methods run, so a shape test asserts the production SQL.

    internal IQueryable<AnalysisResultDetailProjection> DetailQuery(Guid resultId, string tenantId)
        => BuildDetail(ResultByIdSource(resultId, tenantId), _dbContext.AnalysisRescores);

    internal IQueryable<AnalysisContextProjection> ContextQuery(Guid resultId, string tenantId)
        => BuildContext(ResultByIdSource(resultId, tenantId));

    internal IQueryable<AnalysisBcraProjection> BcraQuery(Guid resultId, string tenantId)
        => BuildBcra(ResultByIdSource(resultId, tenantId));

    internal IQueryable<AnalysisRatiosProjection> RatiosQuery(Guid resultId, string tenantId)
        => BuildRatios(ResultByIdSource(resultId, tenantId));

    internal IQueryable<AnalysisLabelProjection> LabelQuery(Guid resultId, string tenantId)
        => BuildLabel(ResultByIdSource(resultId, tenantId), tenantId);
}
