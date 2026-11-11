// Project : Api.Web.FinanceIA
// Created : 2026-10-23

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Data.Repositories;

// GET /api/dashboard/portfolio. Every read groups or sums in SQL and returns a handful of rows; the cupo
// rule ("occupies cupo unless Cancelada") is CreditLineRules.OccupiesCupoPredicate, the translatable twin
// of CreditLineRules.OccupiesCupo. TenantId is on every table of every query — including the operations,
// whose CreditLineId alone would let another tenant's rows eat into this tenant's utilizado.
public sealed partial class DashboardRepository
{
    private const string ActiveLine    = "Active";
    private const string SuspendedLine = "Suspended";

    // Case-sensitive in T-SQL, and the stored keys are camelCase — the paths AnalysisResultRepository reads.
    private const string BcraEstadoPath          = "$.deuda.estado";
    private const string BcraSituacionMaximaPath = "$.deuda.situacionMaxima";

    /// <inheritdoc />
    public async Task<IReadOnlyList<DashboardLineGroupRow>> GetLineTotalsAsync(string tenantId, CancellationToken ct)
        => await LineTotalsQuery(tenantId).ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<DashboardLineGroupRow>> GetOccupiedByCurrencyAsync(string tenantId, CancellationToken ct)
        => await OccupiedByCurrencyQuery(tenantId).ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<DashboardLineGroupRow>> GetLinesByRiskAsync(string tenantId, CancellationToken ct)
        => await LinesByRiskQuery(tenantId).ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<DashboardLineGroupRow>> GetOccupiedByRiskAsync(string tenantId, CancellationToken ct)
        => await OccupiedByRiskQuery(tenantId).ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetActiveLineCurrenciesAsync(string tenantId, CancellationToken ct)
        => await _dbContext.CreditLines
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.Status == ActiveLine)
            .Select(c => c.Currency)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<DashboardExposureRow>> GetTopExposuresAsync(
        string tenantId, string currency, int take, CancellationToken ct)
        => await TopExposuresQuery(tenantId, currency).Take(take).ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<DashboardLineGroupRow>> GetHighUtilizationCountsAsync(
        string tenantId, decimal thresholdPct, CancellationToken ct)
        => await HighUtilizationCountsQuery(tenantId, thresholdPct).ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<DashboardExposureRow>> GetHighUtilizationAsync(
        string tenantId, string currency, decimal thresholdPct, int take, CancellationToken ct)
        => await HighUtilizationQuery(tenantId, currency, thresholdPct).Take(take).ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<DashboardLineGroupRow>> GetLinesBySectorAsync(string tenantId, CancellationToken ct)
        => await LinesBySectorQuery(tenantId).ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<DashboardLineGroupRow>> GetOccupiedBySectorAsync(string tenantId, CancellationToken ct)
        => await OccupiedBySectorQuery(tenantId).ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<DashboardMaturityGroupRow>> GetMaturityGroupsAsync(
        string tenantId, DateTime validUntilBefore, CancellationToken ct)
        => await MaturityGroupsQuery(tenantId, validUntilBefore).ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<DashboardUpcomingOperationRow>> GetUpcomingMaturitiesAsync(
        string tenantId, DateTime validFrom, int take, CancellationToken ct)
        => await UpcomingMaturitiesQuery(tenantId, validFrom).Take(take).ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<DashboardBcraComparisonRow>> GetBcraComparisonsAsync(string tenantId, CancellationToken ct)
        => await BcraComparisonsQuery(tenantId).ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<DashboardBcraCurrentRow>> GetBcraCurrentAsync(string tenantId, CancellationToken ct)
        => await BcraCurrentQuery(tenantId).ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public Task<IReadOnlyList<PymeReviewSource>> GetLatestApprovedAnalysesOfActiveEntitiesAsync(string tenantId, CancellationToken ct)
        => PymeReviewQueries.ReadAsync(_dbContext, tenantId, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<DashboardUpcomingOperationRow>> GetOccupyingOperationsOfActiveLinesAsync(
        string tenantId, CancellationToken ct)
        => await OccupyingOperationsOfActiveLinesQuery(tenantId).ToListAsync(ct).ConfigureAwait(false);

    // ── Query shapes — internal so the SQL Server translation can be asserted with ToQueryString ──

    /// <summary>
    /// The tenant's cupos in <paramref name="status"/>, LEFT JOINED to the analysis behind each —
    /// COALESCE(LastRaiseAnalysisId, OriginAnalysisId) — and its result, and to the entity. Every joined
    /// table is filtered to the tenant BEFORE the join, so a key of another tenant joins to nothing.
    /// </summary>
    internal IQueryable<DashboardLineFact> LineFacts(string tenantId, string status)
    {
        var lines    = _dbContext.CreditLines.AsNoTracking().Where(c => c.TenantId == tenantId && c.Status == status);
        var analyses = _dbContext.Analyses.AsNoTracking().Where(a => a.TenantId == tenantId);
        var results  = _dbContext.AnalysisResults.AsNoTracking().Where(r => r.TenantId == tenantId);
        var entities = _dbContext.Entities.AsNoTracking().Where(e => e.TenantId == tenantId);

        return from c in lines
               join a in analyses on (c.LastRaiseAnalysisId ?? c.OriginAnalysisId) equals (Guid?)a.AnalysisId into aj
               from a in aj.DefaultIfEmpty()
               join r in results on (c.LastRaiseAnalysisId ?? c.OriginAnalysisId) equals (Guid?)r.AnalysisId into rj
               from r in rj.DefaultIfEmpty()
               join e in entities on c.EntityId equals e.EntityId into ej
               from e in ej.DefaultIfEmpty()
               select new DashboardLineFact
               {
                   CreditLineId     = c.CreditLineId,
                   EntityId         = c.EntityId,
                   Currency         = c.Currency,
                   Amount           = c.Amount,
                   AnalysisId       = c.LastRaiseAnalysisId ?? c.OriginAnalysisId,
                   AnalysisKind     = a == null ? null : a.AnalysisKind,
                   ResultId         = r == null ? null : (Guid?)r.ResultId,
                   AiSuggestedLevel = r == null ? null : r.AiSuggestedLevel,
                   Sector           = e == null ? null : e.Sector
               };
    }

    /// <summary>
    /// The cupo-occupying operations of the tenant's Active cupos, each as its cupo's facts with
    /// <see cref="DashboardLineFact.Amount"/> set to the OPERATION's amount. Grouped by the CUPO's
    /// currency — an operation's currency must equal it (CreditLinesController enforces it).
    /// </summary>
    internal IQueryable<DashboardLineFact> OccupyingOperations(string tenantId)
        => _dbContext.CreditLineOperations
            .AsNoTracking()
            .Where(o => o.TenantId == tenantId)
            .Where(CreditLineRules.OccupiesCupoPredicate)
            .Join(LineFacts(tenantId, ActiveLine), o => o.CreditLineId, f => f.CreditLineId, (o, f) => new DashboardLineFact
            {
                CreditLineId     = f.CreditLineId,
                EntityId         = f.EntityId,
                Currency         = f.Currency,
                Amount           = o.Amount,
                AnalysisId       = f.AnalysisId,
                AnalysisKind     = f.AnalysisKind,
                ResultId         = f.ResultId,
                AiSuggestedLevel = f.AiSuggestedLevel,
                Sector           = f.Sector
            });

    /// <summary>Active and Suspended cupos by currency and status: how many, how many entities, their Amount.</summary>
    internal IQueryable<DashboardLineGroupRow> LineTotalsQuery(string tenantId)
        => _dbContext.CreditLines
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId && (c.Status == ActiveLine || c.Status == SuspendedLine))
            .GroupBy(c => new { c.Currency, c.Status })
            .Select(g => new DashboardLineGroupRow(
                g.Key.Currency, g.Key.Status, null, null, null,
                g.Count(), g.Select(c => c.EntityId).Distinct().Count(), g.Sum(c => c.Amount)));

    /// <summary>Utilizado of the Active cupos by currency.</summary>
    internal IQueryable<DashboardLineGroupRow> OccupiedByCurrencyQuery(string tenantId)
        => OccupyingOperations(tenantId)
            .GroupBy(x => x.Currency)
            .Select(g => new DashboardLineGroupRow(g.Key, null, null, null, null, g.Count(), 0, g.Sum(x => x.Amount)));

    /// <summary>
    /// Active cupos by currency and by the raw kind and level of their analysis. The bucket is decided in
    /// C# from these two columns, so its rule exists once.
    /// </summary>
    internal IQueryable<DashboardLineGroupRow> LinesByRiskQuery(string tenantId)
        => LineFacts(tenantId, ActiveLine)
            .GroupBy(f => new { f.Currency, f.AnalysisKind, f.AiSuggestedLevel })
            .Select(g => new DashboardLineGroupRow(
                g.Key.Currency, null, g.Key.AnalysisKind, g.Key.AiSuggestedLevel, null, g.Count(), 0, g.Sum(f => f.Amount)));

    /// <summary>Utilizado of the Active cupos by currency, analysis kind and level.</summary>
    internal IQueryable<DashboardLineGroupRow> OccupiedByRiskQuery(string tenantId)
        => OccupyingOperations(tenantId)
            .GroupBy(x => new { x.Currency, x.AnalysisKind, x.AiSuggestedLevel })
            .Select(g => new DashboardLineGroupRow(
                g.Key.Currency, null, g.Key.AnalysisKind, g.Key.AiSuggestedLevel, null, g.Count(), 0, g.Sum(x => x.Amount)));

    /// <summary>
    /// The Active cupos of one currency with utilizado as a correlated scalar subquery — the
    /// CreditLineRepository.BuildMainActive shape — ordered by it, highest first. One Active cupo per
    /// entity and currency, so a cupo here is an entity.
    /// </summary>
    internal IQueryable<DashboardExposureRow> TopExposuresQuery(string tenantId, string currency)
    {
        var operations = _dbContext.CreditLineOperations.AsNoTracking().Where(o => o.TenantId == tenantId);

        return LineFacts(tenantId, ActiveLine)
            .Where(f => f.Currency == currency)
            .Select(f => new
            {
                Fact      = f,
                Utilizado = operations
                    .Where(o => o.CreditLineId == f.CreditLineId)
                    .Where(CreditLineRules.OccupiesCupoPredicate)
                    .Sum(o => (decimal?)o.Amount) ?? 0m
            })
            .OrderByDescending(x => x.Utilizado)
            .ThenBy(x => x.Fact.EntityId)
            .Select(x => new DashboardExposureRow(
                x.Fact.CreditLineId, x.Fact.EntityId, x.Fact.Currency, x.Fact.Amount, x.Utilizado,
                x.Fact.AnalysisId, x.Fact.AnalysisKind, x.Fact.ResultId, x.Fact.AiSuggestedLevel));
    }

    /// <summary>
    /// The Active cupos with Amount &gt; 0 whose utilizado — the TopExposuresQuery correlated subquery — is
    /// at least <paramref name="thresholdPct"/>% of it, compared on the exact amounts
    /// (utilizado × 100 ≥ Amount × threshold) so no rounding decides membership. Amount 0 is excluded
    /// outright: 0 × 100 ≥ 0 × 90 would otherwise list every empty, ungranted cupo.
    /// </summary>
    private IQueryable<HighUtilizationCandidate> HighUtilizationSource(string tenantId, decimal thresholdPct)
    {
        var operations = _dbContext.CreditLineOperations.AsNoTracking().Where(o => o.TenantId == tenantId);

        return LineFacts(tenantId, ActiveLine)
            .Where(f => f.Amount > 0m)
            .Select(f => new HighUtilizationCandidate
            {
                Fact      = f,
                Utilizado = operations
                    .Where(o => o.CreditLineId == f.CreditLineId)
                    .Where(CreditLineRules.OccupiesCupoPredicate)
                    .Sum(o => (decimal?)o.Amount) ?? 0m
            })
            .Where(x => x.Utilizado * 100m >= x.Fact.Amount * thresholdPct);
    }

    /// <summary>The highly utilized Active cupos by currency: how many, and their Amount.</summary>
    internal IQueryable<DashboardLineGroupRow> HighUtilizationCountsQuery(string tenantId, decimal thresholdPct)
        => HighUtilizationSource(tenantId, thresholdPct)
            .GroupBy(x => x.Fact.Currency)
            .Select(g => new DashboardLineGroupRow(g.Key, null, null, null, null, g.Count(), 0, g.Sum(x => x.Fact.Amount)));

    /// <summary>
    /// The highly utilized Active cupos of one currency, by the exact utilization ratio descending, then
    /// Amount descending, then EntityId. The ratio sits under a CASE on Amount &gt; 0: SQL Server does not
    /// promise to filter before it evaluates the ORDER BY, so the guard is what rules out a divide by zero.
    /// </summary>
    internal IQueryable<DashboardExposureRow> HighUtilizationQuery(string tenantId, string currency, decimal thresholdPct)
        => HighUtilizationSource(tenantId, thresholdPct)
            .Where(x => x.Fact.Currency == currency)
            .OrderByDescending(x => x.Fact.Amount > 0m ? x.Utilizado / x.Fact.Amount : 0m)
            .ThenByDescending(x => x.Fact.Amount)
            .ThenBy(x => x.Fact.EntityId)
            .Select(x => new DashboardExposureRow(
                x.Fact.CreditLineId, x.Fact.EntityId, x.Fact.Currency, x.Fact.Amount, x.Utilizado,
                x.Fact.AnalysisId, x.Fact.AnalysisKind, x.Fact.ResultId, x.Fact.AiSuggestedLevel));

    /// <summary>
    /// Active cupos by currency and Entities.Sector as stored. Folding case and blanks happens in C#, over
    /// these few groups, so the rule is C#'s Trim and not T-SQL's narrower LTRIM/RTRIM.
    /// </summary>
    internal IQueryable<DashboardLineGroupRow> LinesBySectorQuery(string tenantId)
        => LineFacts(tenantId, ActiveLine)
            .GroupBy(f => new { f.Currency, f.Sector })
            .Select(g => new DashboardLineGroupRow(
                g.Key.Currency, null, null, null, g.Key.Sector, g.Count(), 0, g.Sum(f => f.Amount)));

    /// <summary>Utilizado of the Active cupos by currency and sector as stored.</summary>
    internal IQueryable<DashboardLineGroupRow> OccupiedBySectorQuery(string tenantId)
        => OccupyingOperations(tenantId)
            .GroupBy(x => new { x.Currency, x.Sector })
            .Select(g => new DashboardLineGroupRow(
                g.Key.Currency, null, null, null, g.Key.Sector, g.Count(), 0, g.Sum(x => x.Amount)));

    /// <summary>
    /// The stored-'Vigente' operations of Active cupos with a ValidUntil before
    /// <paramref name="validUntilBefore"/>, grouped by currency and ValidUntil DATE. Which of them are
    /// Vencida and how far away the others are is decided in C# with CreditLineRules.EffectiveStatus on
    /// each date — the cupo screen's own rule, not a SQL rewrite of it.
    /// </summary>
    internal IQueryable<DashboardMaturityGroupRow> MaturityGroupsQuery(string tenantId, DateTime validUntilBefore)
        => VigenteOperationsOfActiveLines(tenantId)
            .Where(x => x.ValidUntil != null && x.ValidUntil < validUntilBefore)
            .GroupBy(x => new { x.Currency, ValidUntil = x.ValidUntil!.Value.Date })
            .Select(g => new DashboardMaturityGroupRow(g.Key.Currency, g.Key.ValidUntil, g.Count(), g.Sum(x => x.Amount)));

    /// <summary>
    /// The stored-'Vigente' operations of Active cupos with ValidUntil at or after
    /// <paramref name="validFrom"/> — the first UTC instant of the day EffectiveStatus still calls Vigente —
    /// soonest first.
    /// </summary>
    internal IQueryable<DashboardUpcomingOperationRow> UpcomingMaturitiesQuery(string tenantId, DateTime validFrom)
        => VigenteOperationsOfActiveLines(tenantId)
            .Where(x => x.ValidUntil != null && x.ValidUntil >= validFrom)
            .OrderBy(x => x.ValidUntil)
            .ThenBy(x => x.OperationId);

    /// <summary>
    /// Every Active cupo with the BCRA estado and maximum situación of its entity's CURRENT snapshot and of
    /// its analysis' frozen one, each pulled out with JSON_VALUE on the same paths the analysis detail reads
    /// — neither nvarchar(max) blob leaves the database.
    /// </summary>
    internal IQueryable<DashboardBcraComparisonRow> BcraComparisonsQuery(string tenantId)
    {
        var lines    = _dbContext.CreditLines.AsNoTracking().Where(c => c.TenantId == tenantId && c.Status == ActiveLine);
        var entities = _dbContext.Entities.AsNoTracking().Where(e => e.TenantId == tenantId);
        var results  = _dbContext.AnalysisResults.AsNoTracking().Where(r => r.TenantId == tenantId);

        return from c in lines
               join e in entities on c.EntityId equals e.EntityId
               join r in results on (c.LastRaiseAnalysisId ?? c.OriginAnalysisId) equals (Guid?)r.AnalysisId into rj
               from r in rj.DefaultIfEmpty()
               select new DashboardBcraComparisonRow(
                   c.EntityId,
                   c.CreditLineId,
                   SqlJsonFunctions.JsonValue(e.BcraDataJson, BcraEstadoPath),
                   SqlJsonFunctions.JsonValue(e.BcraDataJson, BcraSituacionMaximaPath),
                   e.BcraDataUpdatedAt,
                   c.LastRaiseAnalysisId ?? c.OriginAnalysisId,
                   r == null ? null : (Guid?)r.ResultId,
                   r == null ? null : (DateTime?)r.GeneratedAt,
                   r == null ? null : SqlJsonFunctions.JsonValue(r.BcraDataJson, BcraEstadoPath),
                   r == null ? null : SqlJsonFunctions.JsonValue(r.BcraDataJson, BcraSituacionMaximaPath));
    }

    /// <summary>
    /// Every Active cupo with its utilizado — the TopExposuresQuery correlated subquery — and the BCRA
    /// estado, maximum situación and last-query instant of its entity's CURRENT snapshot, pulled out with
    /// JSON_VALUE on the paths BcraComparisonsQuery reads. The entity is LEFT JOINED, so a cupo whose entity
    /// does not resolve in this tenant still counts its money (with no reading) rather than vanishing from
    /// the totals.
    /// </summary>
    internal IQueryable<DashboardBcraCurrentRow> BcraCurrentQuery(string tenantId)
    {
        var lines      = _dbContext.CreditLines.AsNoTracking().Where(c => c.TenantId == tenantId && c.Status == ActiveLine);
        var entities   = _dbContext.Entities.AsNoTracking().Where(e => e.TenantId == tenantId);
        var operations = _dbContext.CreditLineOperations.AsNoTracking().Where(o => o.TenantId == tenantId);

        return from c in lines
               join e in entities on c.EntityId equals e.EntityId into ej
               from e in ej.DefaultIfEmpty()
               select new DashboardBcraCurrentRow(
                   c.EntityId,
                   c.CreditLineId,
                   c.Currency,
                   c.Amount,
                   operations
                       .Where(o => o.CreditLineId == c.CreditLineId)
                       .Where(CreditLineRules.OccupiesCupoPredicate)
                       .Sum(o => (decimal?)o.Amount) ?? 0m,
                   e == null ? null : SqlJsonFunctions.JsonValue(e.BcraDataJson, BcraEstadoPath),
                   e == null ? null : SqlJsonFunctions.JsonValue(e.BcraDataJson, BcraSituacionMaximaPath),
                   e == null ? null : e.BcraDataUpdatedAt);
    }

    /// <summary>
    /// The operations of the tenant's Active cupos that occupy cupo — CreditLineRules.OccupiesCupoPredicate,
    /// so Aprobada, Vigente and Vencida alike — with the cupo's entity and currency. TenantId is on both sides
    /// of the join. Which of them is Vigente or Vencida is decided in C# with CreditLineRules.EffectiveStatus,
    /// the cupo screen's own rule.
    /// </summary>
    internal IQueryable<DashboardUpcomingOperationRow> OccupyingOperationsOfActiveLinesQuery(string tenantId)
    {
        var lines = _dbContext.CreditLines.AsNoTracking().Where(c => c.TenantId == tenantId && c.Status == ActiveLine);

        return _dbContext.CreditLineOperations
            .AsNoTracking()
            .Where(o => o.TenantId == tenantId)
            .Where(CreditLineRules.OccupiesCupoPredicate)
            .Join(lines, o => o.CreditLineId, c => c.CreditLineId, (o, c) => new DashboardUpcomingOperationRow
            {
                OperationId  = o.OperationId,
                CreditLineId = o.CreditLineId,
                EntityId     = c.EntityId,
                Type         = o.Type,
                Currency     = c.Currency,
                Amount       = o.Amount,
                Status       = o.Status,
                ValidUntil   = o.ValidUntil
            });
    }

    /// <summary>The annualReview input — PymeReviewQueries, the query PYME_REVIEW_DUE reads through CreditLineRepository.</summary>
    internal IQueryable<PymeReviewSource> LatestApprovedAnalysesQuery(string tenantId)
        => PymeReviewQueries.LatestApprovedAnalysesOfActiveEntities(_dbContext, tenantId);

    /// <summary>A cupo's facts with its utilizado, member-initialised so EF keeps translating after it.</summary>
    private sealed record HighUtilizationCandidate
    {
        public DashboardLineFact Fact { get; init; } = null!;

        public decimal Utilizado { get; init; }
    }

    private IQueryable<DashboardUpcomingOperationRow> VigenteOperationsOfActiveLines(string tenantId)
    {
        var lines = _dbContext.CreditLines.AsNoTracking().Where(c => c.TenantId == tenantId && c.Status == ActiveLine);

        return _dbContext.CreditLineOperations
            .AsNoTracking()
            .Where(o => o.TenantId == tenantId && o.Status == CreditLineRules.Vigente)
            .Join(lines, o => o.CreditLineId, c => c.CreditLineId, (o, c) => new DashboardUpcomingOperationRow
            {
                OperationId  = o.OperationId,
                CreditLineId = o.CreditLineId,
                EntityId     = c.EntityId,
                Type         = o.Type,
                Currency     = c.Currency,
                Amount       = o.Amount,
                Status       = o.Status,
                ValidUntil   = o.ValidUntil
            });
    }
}
