// Project : Api.Web.FinanceIA
// Created : 2026-07-20

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="ICreditLineRepository"/>.
/// All reads use AsNoTracking; writes load a tracked entity and call DetectChanges before SaveChangesAsync.
/// </summary>
public sealed class CreditLineRepository : ICreditLineRepository
{
    private readonly FinanciaDbContext             _dbContext;
    private readonly ILogger<CreditLineRepository> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="CreditLineRepository"/>.
    /// </summary>
    public CreditLineRepository(FinanciaDbContext dbContext, ILogger<CreditLineRepository> logger)
    {
        _dbContext = dbContext;
        _logger    = logger;
    }

    /// <summary>
    /// The single source of truth for which cupo statuses are NON-TERMINAL, i.e. still occupy the
    /// one-live-cupo-per-(entity, currency) slot: they block creating a new cupo for the same
    /// (entity, currency). 'Superseded' is terminal and does not. Read by
    /// <see cref="GetBlockingByEntityCurrencyAsync"/> and <see cref="GetIdsByOriginAnalysisAsync"/>.
    /// Note: rejecting the origin analysis is gated on ACTIVE cupos only (a merely 'Suspended' cupo
    /// lends nothing further) — see <see cref="GetActiveIdsByOriginAnalysisAsync"/> — so this list no
    /// longer governs that guard. EF Core translates the Contains(...) into a SQL IN clause.
    /// </summary>
    private static readonly string[] NonTerminalStatuses = { "Active", "Suspended" };

    /// <inheritdoc />
    public async Task<IReadOnlyList<CreditLine>> GetNonTerminalByEntityAsync(
        Guid entityId, string tenantId, CancellationToken ct)
    {
        // Non-terminal ('Active' or 'Suspended') so a suspended cupo stays visible on the entity's
        // card (its Reactivate action lives there). OrderBy(Status) first — 'Active' sorts before
        // 'Suspended' — puts live money at the top; ThenBy(Currency) keeps the list stable.
        return await _dbContext.CreditLines
            .AsNoTracking()
            .Where(c => c.EntityId == entityId && c.TenantId == tenantId
                     && NonTerminalStatuses.Contains(c.Status))
            .OrderBy(c => c.Status)
            .ThenBy(c => c.Currency)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<CreditLine?> GetActiveByEntityCurrencyAsync(
        Guid entityId, string currency, string tenantId, CancellationToken ct)
    {
        return await _dbContext.CreditLines
            .AsNoTracking()
            .FirstOrDefaultAsync(
                c => c.EntityId == entityId
                  && c.Currency == currency
                  && c.TenantId == tenantId
                  && c.Status   == "Active",
                ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<CreditLine?> GetByIdAsync(Guid creditLineId, string tenantId, CancellationToken ct)
    {
        return await _dbContext.CreditLines
            .AsNoTracking()
            .FirstOrDefaultAsync(
                c => c.CreditLineId == creditLineId && c.TenantId == tenantId, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task CreateAsync(CreditLine line, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(line);

        _dbContext.CreditLines.Add(line);
        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Created credit line {CreditLineId} for entity {EntityId} currency {Currency} tenant {TenantId}",
            line.CreditLineId, line.EntityId, line.Currency, line.TenantId);
    }

    /// <inheritdoc />
    public async Task<CreditLine?> RaiseAsync(
        Guid creditLineId, string tenantId, decimal newAmount, Guid analysisId,
        DateTime? expectedLastRaisedAt, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // Single atomic conditional UPDATE. The WHERE includes the baseline the caller validated
        // the freshness rule against (LastRaisedAt == expectedLastRaisedAt), so two concurrent
        // raises cannot both win: the second matches 0 rows because its expected baseline is
        // stale, and returns null -> 409. This closes the read-then-write gap a tracked
        // load + SaveChanges would leave open, without an explicit transaction. EF Core null-safe
        // equality handles the never-raised case (expected == null -> LastRaisedAt IS NULL).
        var affected = await _dbContext.CreditLines
            .Where(c => c.CreditLineId == creditLineId
                     && c.TenantId     == tenantId
                     && c.Status       == "Active"
                     && c.LastRaisedAt == expectedLastRaisedAt)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.Amount,              newAmount)
                .SetProperty(c => c.LastRaisedAt,        now)
                .SetProperty(c => c.LastRaiseAnalysisId, (Guid?)analysisId),
                ct)
            .ConfigureAwait(false);

        if (affected == 0)
            return null;

        _logger.LogInformation(
            "Raised credit line {CreditLineId} to {NewAmount} (analysis {AnalysisId}) tenant {TenantId}",
            creditLineId, newAmount, analysisId, tenantId);

        // Return the authoritative persisted snapshot for the response mapping.
        return await _dbContext.CreditLines
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.CreditLineId == creditLineId && c.TenantId == tenantId, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<Guid>> GetEntityIdsWithNonTerminalLineAsync(
        IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct)
    {
        var ids = entityIds.Distinct().ToList();
        if (ids.Count == 0) return new HashSet<Guid>();

        var matches = await _dbContext.CreditLines
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId
                     && NonTerminalStatuses.Contains(c.Status)
                     && ids.Contains(c.EntityId))
            .Select(c => c.EntityId)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return matches.ToHashSet();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, MainCreditLineProjection>> GetMainActiveByEntityIdsAsync(
        IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct)
    {
        var ids = entityIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, MainCreditLineProjection>();

        // ONE query for the whole page, however many rows it has — see BuildMainActive.
        var candidates = await MainActiveByEntityIdsQuery(ids, tenantId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // The most-recent-per-entity pick happens here rather than in SQL, over a set bounded by
        // UX_CreditLines_ActivePerEntityCurrency: at most one Active cupo per (entity, currency), so
        // a handful of rows per entity, not a portfolio. Same shape as
        // AnalysisRepository.GetLatestStatusByEntityIdsAsync's latest-per-entity reduction.
        // Currency then CreditLineId break a CreatedAt tie so the chosen cupo is deterministic and a
        // page never reorders between two identical requests.
        return candidates
            .GroupBy(c => c.EntityId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(c => c.CreatedAt)
                      .ThenBy(c => c.Currency, StringComparer.Ordinal)
                      .ThenBy(c => c.CreditLineId)
                      .First());
    }

    /// <summary>
    /// The main-cupo projection as an unexecuted query — ONE statement for a whole page of entities.
    ///
    /// <para>
    /// 'Active' only: the strict live-money predicate, the same one
    /// <see cref="GetActiveByEntityCurrencyAsync"/> and <see cref="GetActiveIdsByOriginAnalysisAsync"/>
    /// use. NOT <see cref="NonTerminalStatuses"/> — a Suspended cupo has no live tope to draw a
    /// utilisation bar for.
    /// </para>
    ///
    /// <para>
    /// Utilizado is a CORRELATED SCALAR SUBQUERY, so SQL Server sums the operations and not one of
    /// them is materialised here — the whole performance claim on a page of up to 100 entities. Its
    /// predicate is <c>CreditLineRules.OccupiesCupo</c> ("stored status is not 'Cancelada'")
    /// rewritten as LINQ (its translatable twin is <c>CreditLineRules.OccupiesCupoPredicate</c>), because a C# method cannot be translated to SQL. The two
    /// must not drift: an 'Aprobada' operation reserves cupo from creation and an effective-'Vencida'
    /// one still consumes it, so only an explicit cancellation frees saldo.
    /// </para>
    ///
    /// <para>
    /// <c>SUM</c> over an empty set is NULL in SQL rather than 0, hence the cast to <c>decimal?</c>
    /// and the coalesce. TenantId is filtered on BOTH sides: omitting it from the subquery would let
    /// another tenant's operations eat into this tenant's disponible.
    /// </para>
    ///
    /// <para>
    /// Split out from the public method so the test seam below can assert the SQL this exact query
    /// emits, rather than a re-declared copy of it.
    /// </para>
    /// </summary>
    private IQueryable<MainCreditLineProjection> BuildMainActive(
        IReadOnlyList<Guid> entityIds, string tenantId)
        => _dbContext.CreditLines
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId
                     && c.Status   == "Active"
                     && entityIds.Contains(c.EntityId))
            .Select(c => new MainCreditLineProjection
            {
                EntityId     = c.EntityId,
                CreditLineId = c.CreditLineId,
                Amount       = c.Amount,
                Currency     = c.Currency,
                CreatedAt    = c.CreatedAt,
                Utilizado    = _dbContext.CreditLineOperations
                    .Where(o => o.CreditLineId == c.CreditLineId
                             && o.TenantId     == tenantId
                             && o.Status       != "Cancelada")
                    .Sum(o => (decimal?)o.Amount) ?? 0m
            });

    // ── Test seam ─────────────────────────────────────────────────────────────────
    // Internal (InternalsVisibleTo: Api.Web.FinanceIA.Tests) so a test can call ToQueryString() and
    // assert the SQL this exact query emits — that the operations SUM really is a subquery SQL Server
    // evaluates, not a second round-trip or client-side evaluation. The in-memory provider used by
    // the other tests here runs LINQ-to-objects and would happily execute an untranslatable
    // projection, so it cannot prove this. Mirrors EntityRepository.TenantListPageQuery.

    internal IQueryable<MainCreditLineProjection> MainActiveByEntityIdsQuery(
        IReadOnlyList<Guid> entityIds, string tenantId)
        => BuildMainActive(entityIds, tenantId);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> GetIdsByOriginAnalysisAsync(
        Guid analysisId, string tenantId, CancellationToken ct)
    {
        // Id-only projection: enough to know the analysis has dependent cupos AND to name them
        // in the caller's 409, without materialising entities. Restricted to non-terminal statuses
        // so "live dependent" means the same thing here as in GetBlockingByEntityCurrencyAsync.
        return await _dbContext.CreditLines
            .AsNoTracking()
            .Where(c => c.OriginAnalysisId == analysisId
                     && c.TenantId == tenantId
                     && NonTerminalStatuses.Contains(c.Status))
            .Select(c => c.CreditLineId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> GetActiveIdsByOriginAnalysisAsync(
        Guid analysisId, string tenantId, CancellationToken ct)
    {
        // Id-only projection of the ACTIVE cupos this analysis originated: the operative subset that
        // blocks rejecting the origin analysis and that the 409 names for the analyst to suspend.
        // 'Suspended' (non-terminal but dormant) and 'Superseded' (terminal) are both excluded here —
        // contrast GetIdsByOriginAnalysisAsync, which returns the whole non-terminal set.
        return await _dbContext.CreditLines
            .AsNoTracking()
            .Where(c => c.OriginAnalysisId == analysisId
                     && c.TenantId == tenantId
                     && c.Status == "Active")
            .Select(c => c.CreditLineId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CreditLine>> GetByOriginAnalysisAsync(
        Guid analysisId, string tenantId, CancellationToken ct)
    {
        // Provenance: full rows, ALL statuses (incl. terminal 'Superseded') — no status filter, on
        // purpose. Contrast with GetIdsByOriginAnalysisAsync (non-terminal, id-only) for the reject
        // guard. Ordered Currency then Status for a stable, readable list.
        return await _dbContext.CreditLines
            .AsNoTracking()
            .Where(c => c.OriginAnalysisId == analysisId && c.TenantId == tenantId)
            .OrderBy(c => c.Currency)
            .ThenBy(c => c.Status)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<CreditLine?> GetBlockingByEntityCurrencyAsync(
        Guid entityId, string currency, string tenantId, CancellationToken ct)
    {
        // 'Active' sorts before 'Suspended', so OrderBy(Status) returns the Active row first should
        // both ever coexist — the more restrictive case wins the message.
        return await _dbContext.CreditLines
            .AsNoTracking()
            .Where(c => c.EntityId == entityId
                     && c.Currency == currency
                     && c.TenantId == tenantId
                     && NonTerminalStatuses.Contains(c.Status))
            .OrderBy(c => c.Status)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> SetStatusAsync(
        Guid creditLineId, string tenantId, string expectedStatus, string newStatus, CancellationToken ct)
    {
        // Single atomic conditional UPDATE guarded on the expected current status, mirroring RaiseAsync:
        // a concurrent transition that already moved the row off expectedStatus matches 0 rows, so the
        // caller 409s instead of clobbering it. Only the Status column is written.
        var affected = await _dbContext.CreditLines
            .Where(c => c.CreditLineId == creditLineId
                     && c.TenantId     == tenantId
                     && c.Status       == expectedStatus)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.Status, newStatus),
                ct)
            .ConfigureAwait(false);

        if (affected == 0)
            return false;

        _logger.LogInformation(
            "Credit line {CreditLineId} status {OldStatus} -> {NewStatus} tenant {TenantId}",
            creditLineId, expectedStatus, newStatus, tenantId);

        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CupoAlertSource>> GetActiveForCupoAlertsAsync(string tenantId, CancellationToken ct)
    {
        var lines = await ActiveLinesOf(tenantId)
            .Select(c => new
            {
                c.CreditLineId,
                c.EntityId,
                c.Amount,
                c.Currency,
                AnalysisId = c.LastRaiseAnalysisId ?? c.OriginAnalysisId
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (lines.Count == 0)
            return Array.Empty<CupoAlertSource>();

        var operations = await BuildCupoAlertOperations(tenantId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var operationsByLine = operations
            .GroupBy(o => o.CreditLineId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<CreditLineOperation>)g.Select(o => o.ToOperation()).ToList());

        return lines
            .Select(l => new CupoAlertSource
            {
                CreditLineId        = l.CreditLineId,
                EntityId            = l.EntityId,
                Amount              = l.Amount,
                Currency            = l.Currency,
                AnalysisId          = l.AnalysisId,
                OccupyingOperations = operationsByLine.GetValueOrDefault(l.CreditLineId)
                                      ?? Array.Empty<CreditLineOperation>()
            })
            .ToList();
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PymeReviewSource>> GetLatestApprovedAnalysesOfActiveEntitiesAsync(
        string tenantId, CancellationToken ct)
        => PymeReviewQueries.ReadAsync(_dbContext, tenantId, ct);

    private IQueryable<CreditLine> ActiveLinesOf(string tenantId)
        => _dbContext.CreditLines
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.Status == "Active");

    /// <summary>
    /// The cupo-occupying operations of the tenant's Active cupos, as an unexecuted query.
    /// <c>CreditLineRules.OccupiesCupoPredicate</c>, not a re-typed "!= Cancelada": the cupo card, the
    /// dashboard and the cupo alerts have to agree on what occupies cupo, and that twin is the one
    /// parity-tested against <c>CreditLineRules.OccupiesCupo</c>. Joined to the Active cupos rather than
    /// filtered by an IN list of ids, so it stays one statement however many cupos the tenant has, and
    /// TenantId is on both sides of the join.
    /// </summary>
    private IQueryable<CupoAlertOperationRow> BuildCupoAlertOperations(string tenantId)
        => _dbContext.CreditLineOperations
            .AsNoTracking()
            .Where(o => o.TenantId == tenantId)
            .Where(CreditLineRules.OccupiesCupoPredicate)
            .Join(ActiveLinesOf(tenantId), o => o.CreditLineId, c => c.CreditLineId, (o, c) => new CupoAlertOperationRow
            {
                OperationId         = o.OperationId,
                CreditLineId        = o.CreditLineId,
                Amount              = o.Amount,
                Currency            = o.Currency,
                Status              = o.Status,
                ValidUntil          = o.ValidUntil,
                CreatedAt           = o.CreatedAt,
                VigenteConfirmadaEl = o.VigenteConfirmadaEl
            });

    // Test seam (InternalsVisibleTo), the MainActiveByEntityIdsQuery idiom: lets a test assert the SQL
    // this exact query emits, which the in-memory provider cannot prove.
    internal IQueryable<CupoAlertOperationRow> CupoAlertOperationsQuery(string tenantId)
        => BuildCupoAlertOperations(tenantId);

    // Test seam, as above, for the PYME_REVIEW_DUE read.
    internal IQueryable<PymeReviewSource> LatestApprovedAnalysesQuery(string tenantId)
        => PymeReviewQueries.LatestApprovedAnalysesOfActiveEntities(_dbContext, tenantId);
}
