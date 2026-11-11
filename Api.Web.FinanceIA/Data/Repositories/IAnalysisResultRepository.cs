// Project : Api.Web.FinanceIA
// Created : 2026-05-04
// Modified: 2026-05-04

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// Persistence operations for <see cref="AnalysisResult"/> records
/// in the per-tenant business database.
/// </summary>
public interface IAnalysisResultRepository
{
    /// <summary>
    /// Returns the analysis result with the specified id belonging to the given tenant,
    /// or null if not found.
    /// </summary>
    Task<AnalysisResult?> GetByIdAsync(Guid resultId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Returns all analysis results belonging to the given tenant.
    /// </summary>
    Task<IReadOnlyList<AnalysisResult>> GetAllByTenantAsync(string tenantId, CancellationToken ct);

    /// <summary>
    /// Returns the analysis result associated with the given document within the given tenant,
    /// or null if no result exists yet.
    /// Resolves the document-to-result path via the Analyses table.
    /// </summary>
    Task<AnalysisResult?> GetByDocumentIdAsync(Guid documentId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Persists a new analysis result record and returns the saved entity.
    /// </summary>
    Task<AnalysisResult> CreateAsync(AnalysisResult result, CancellationToken ct);

    /// <summary>
    /// Returns all analysis results associated with documents belonging to the
    /// given entity within the given tenant.
    /// </summary>
    Task<IReadOnlyList<AnalysisResult>> GetByEntityIdAsync(Guid entityId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Persists changes to an existing analysis result record.
    /// </summary>
    Task<AnalysisResult> UpdateAsync(AnalysisResult result, CancellationToken ct);

    /// <summary>
    /// Maps each of <paramref name="analysisIds"/> to the id of its <see cref="AnalysisResult"/>,
    /// tenant-isolated. AnalysisIds with no result row are absent from the map. A single set-based
    /// query — used to resolve navigable ResultIds without N+1 (the detail route is keyed by
    /// ResultId, not AnalysisId). The DB unique index UQ_AnalysisResults_AnalysisId guarantees at
    /// most one result per analysis, so the mapping is 1:1.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, Guid>> GetResultIdsByAnalysisIdsAsync(
        IEnumerable<Guid> analysisIds, string tenantId, CancellationToken ct);

    /// <summary>
    /// Returns ONE PAGE of the tenant's analyses as <see cref="AnalysisListProjection"/>, newest
    /// first, plus the total row count of the unpaged set.
    /// <para>
    /// Deliberately not <see cref="GetAllByTenantAsync"/> + in-memory <c>Skip/Take</c>: the ordering
    /// and the paging run in SQL (<c>ORDER BY … OFFSET … FETCH</c>) and the SELECT names only the
    /// columns the list renders, so the 14 heavy <c>nvarchar(max)</c> columns are never read off
    /// disk. <c>TotalCount</c> is a separate <c>COUNT(*)</c> over the same filter — cheap, and it
    /// touches no blob either.
    /// </para>
    /// <para>
    /// Ordering is <c>GeneratedAt DESC, ResultId ASC</c>. The ResultId tie-break is load-bearing, not
    /// decoration: <c>GeneratedAt</c> is a non-unique <c>datetime2</c>, and OFFSET/FETCH over a
    /// non-total order lets rows sharing a timestamp appear on two pages or on neither.
    /// </para>
    /// </summary>
    /// <param name="skip">Rows to skip — <c>(page - 1) * pageSize</c>, already clamped by the caller.</param>
    /// <param name="take">Rows to return — the clamped pageSize.</param>
    Task<(IReadOnlyList<AnalysisListProjection> Items, int TotalCount)> GetListPageByTenantAsync(
        string tenantId, int skip, int take, CancellationToken ct);

    /// <summary>
    /// The <see cref="GetListPageByTenantAsync"/> page, narrowed to one entity's analyses.
    /// <para>
    /// Resolves the same Documents → Analyses → AnalysisResults chain as
    /// <see cref="GetByEntityIdAsync"/> (entity → its non-deleted documents → their analyses → their
    /// results), composed as nested <c>IQueryable</c>s so it stays a single SQL statement with two
    /// <c>IN</c> subqueries rather than three round-trips.
    /// </para>
    /// </summary>
    Task<(IReadOnlyList<AnalysisListProjection> Items, int TotalCount)> GetListPageByEntityAsync(
        Guid entityId, string tenantId, int skip, int take, CancellationToken ct);

    /// <summary>
    /// Returns the result behind GET /api/analyses/{id} as
    /// <see cref="AnalysisResultDetailProjection"/>, tenant-isolated, or null if there is no such
    /// result in this tenant.
    /// <para>
    /// Deliberately not <see cref="GetByIdAsync"/>: that one is a full-row load, so SQL Server reads
    /// all fourteen <c>nvarchar(max)</c> columns off disk. Five of them belong to the "Contexto"
    /// section, which renders only when the user opens it — LufeDataJson, FiscalDataJson,
    /// CasfogDataJson, NosisDataJson and BcraDataJson. This SELECT omits all five;
    /// <see cref="GetContextSnapshotByIdAsync"/> serves them when they are actually asked for.
    /// </para>
    /// <para>
    /// <c>BcraDataJson</c> is the partial exception. The Score section at the top of the page reads
    /// two scalars off it — <c>deuda.estado</c> and <c>deuda.situacionMaxima</c> — so this query
    /// extracts those with <c>JSON_VALUE</c> into
    /// <see cref="AnalysisResultDetailProjection.BcraDeudaEstadoRaw"/> and
    /// <see cref="AnalysisResultDetailProjection.BcraDeudaSituacionMaximaRaw"/>. The column is named
    /// only as an argument, never projected, so the ~40KB blob does not reach the app. SQL Server
    /// still reads the LOB to parse it — this saves the transfer and the deserialisation, not the
    /// disk read.
    /// </para>
    /// <para>
    /// Three further columns are absent because nothing on the detail path reads them at all:
    /// <c>MacroDataJson</c> (read only by the chat prompt builder, which loads its own full row),
    /// <c>ValidationNotes</c>, and the <c>AiInputHash</c>/<c>AiOutputHash</c> audit pair. None of them
    /// appears on <see cref="AnalysisResultResponse"/>, so the payload is unchanged.
    /// </para>
    /// <para>
    /// <see cref="GetByIdAsync"/> stays for every other caller: <c>DecideAsync</c> mutates the result
    /// and hands it to <see cref="UpdateAsync"/>, which marks every property modified; the chat
    /// endpoints build a prompt from columns this projection does not carry; and the PDF export
    /// renders the whole row. Those need the full entity and must not be given a projection.
    /// </para>
    /// </summary>
    Task<AnalysisResultDetailProjection?> GetDetailByIdAsync(Guid resultId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Returns the five STORED source payloads of one analysis result, tenant-isolated, or null if
    /// there is no such result in this tenant. A read of six columns; it consults no external source
    /// and writes nothing.
    /// <para>
    /// A non-null result whose five blobs are all null means the analysis exists but stored none of
    /// them — a 200 carrying nulls, not a 404. These are point-in-time copies taken at analysis run
    /// time and never refreshed, so there is nothing to re-fetch and no timestamp to return; their
    /// capture time is the result's own <c>GeneratedAt</c>.
    /// </para>
    /// <para>
    /// <c>BcraDataJson</c> joined the set after the other four and is read WHOLE here — the Contexto
    /// BCRA tab renders all of it. <see cref="GetDetailByIdAsync"/> takes only two scalars out of the
    /// same column, which is why that one read does not ship it.
    /// </para>
    /// </summary>
    Task<AnalysisContextProjection?> GetContextSnapshotByIdAsync(Guid resultId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Returns the STORED BCRA blob of one analysis as <see cref="AnalysisBcraProjection"/>,
    /// tenant-isolated, or null if there is no such result in this tenant. A read of two columns; it
    /// consults no external source and writes nothing.
    /// <para>
    /// It exists for GET /api/analyses/{id}/bcra/cheques, which serves the per-cheque table that
    /// <c>/context</c> stopped shipping. Deliberately not
    /// <see cref="GetContextSnapshotByIdAsync"/>: that one names five blobs because the Contexto
    /// section renders them together, so serving one table through it would read LufeDataJson —26KB
    /// on a representative row— plus the Fiscal, CASFOG and NOSIS snapshots for a request that
    /// renders none of them. This is the same one-blob-per-read shape
    /// <see cref="IEntityRepository.GetBcraSnapshotAsync"/> uses, and for the same reason: the user
    /// opens one tab at a time.
    /// </para>
    /// <para>
    /// A non-null result whose <see cref="AnalysisBcraProjection.BcraDataJson"/> is null means the
    /// analysis exists but froze no BCRA snapshot — a 200 carrying an empty list, not a 404. That is
    /// the ordinary state for an entity that had no CUIT at analysis time.
    /// </para>
    /// <para>
    /// <see cref="GetContextSnapshotByIdAsync"/> is unchanged and still reads the same column: the
    /// Contexto tab renders every part of that blob EXCEPT the per-cheque array, so it must not be
    /// narrowed to this.
    /// </para>
    /// </summary>
    Task<AnalysisBcraProjection?> GetBcraSnapshotByIdAsync(Guid resultId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Returns the computed ratios and the score breakdown of one analysis as
    /// <see cref="AnalysisRatiosProjection"/>, tenant-isolated, or null if there is no such result in
    /// this tenant. A read of three columns.
    /// <para>
    /// Deliberately not <see cref="GetByIdAsync"/> or <see cref="GetDetailByIdAsync"/>. The PyME
    /// (entity) detail renders a ratios panel for the entity's latest analysis and needs exactly two
    /// columns to do it — <c>RatiosJson</c> and <c>ScoreBreakdownJson</c>. <c>GetByIdAsync</c> is a
    /// full-row load; <c>GetDetailByIdAsync</c>, even after #143 dropped the four frozen Contexto
    /// snapshots and the BCRA split dropped a fifth, still names seven <c>nvarchar(max)</c> columns —
    /// so serving that panel from the detail read means BalanceJson, RiskAlerts, CatalogueAlerts,
    /// Anomalies, ExecutiveSummary and ScoringNarrative coming off disk for a small table that
    /// renders none of them.
    /// </para>
    /// <para>
    /// A non-null result whose two blobs are null means the analysis exists but was never scored — a
    /// 200 carrying nulls, not a 404. That is the ordinary state for analyses predating the scoring
    /// engine.
    /// </para>
    /// <para>
    /// <see cref="GetDetailByIdAsync"/> is unchanged and still carries both columns: GET
    /// /api/analyses/{id} renders far more than the ratios panel and must not be narrowed to it.
    /// </para>
    /// </summary>
    Task<AnalysisRatiosProjection?> GetRatiosByIdAsync(Guid resultId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Returns the analysed entity's name and id for one result as
    /// <see cref="AnalysisLabelProjection"/>, tenant-isolated, or null if there is no such result in
    /// this tenant — which the caller turns into a 404.
    /// <para>
    /// One statement across three tables: ResultId → AnalysisId → EntityId → BusinessName. It exists
    /// for GET /api/analyses/{id}/label, which fills a breadcrumb. The Portal was calling GET
    /// /api/analyses/{id} — the heaviest read in the app — to take one string off it.
    /// </para>
    /// <para>
    /// Deliberately not <see cref="GetDetailByIdAsync"/> (seven <c>nvarchar(max)</c> columns) and
    /// deliberately not the resolve-then-fetch chain the detail path runs: that one calls
    /// <c>IAnalysisRepository.GetByIdAsync</c>, an unprojected load of the Analyses row whose
    /// <c>ReviewNotes</c> is itself <c>nvarchar(max)</c>, followed by
    /// <see cref="IEntityRepository.GetByIdAsync"/>, an unprojected load of the Entities row with all
    /// five source snapshots on it. Three round-trips, two of them naming a blob, for a label. This
    /// SELECT names five columns, three of which are keys, and no blob on any of the three tables.
    /// </para>
    /// <para>
    /// The two joins are INNER and cannot lose a row — <c>AnalysisResults.AnalysisId</c> and
    /// <c>Analyses.EntityId</c> are NOT NULL and both carry a foreign key — so a null return
    /// unambiguously means "no such result in this tenant" and never "the name went missing".
    /// <see cref="AnalysisLabelProjection.EntityName"/> is therefore never null on a non-null result:
    /// <c>Entities.BusinessName</c> is NOT NULL.
    /// </para>
    /// </summary>
    Task<AnalysisLabelProjection?> GetLabelByIdAsync(Guid resultId, string tenantId, CancellationToken ct);
}
