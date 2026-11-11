// Project : Api.Web.FinanceIA
// Created : 2026-10-23

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Data.Repositories;

// GET /api/dashboard/operations. ApiCallLogs is read for its counters only — never the ErrorMessage text
// (only whether it is NULL, which tells an ARCA "not found" from a failed call, and whether it starts with a
// LUFE "no data" marker our own code wrote), never the request/response hashes.
public sealed partial class DashboardRepository
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<DashboardApiCallOutcomeRow>> GetApiCallOutcomesAsync(
        string tenantId, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
        => await ApiCallOutcomesQuery(tenantId, fromUtc, toUtc).ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<DashboardApiCallDurationRow>> GetApiCallDurationsAsync(
        string tenantId, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
        => await ApiCallDurationsQuery(tenantId, fromUtc, toUtc).ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<(int EntityCount, IReadOnlyList<DashboardFreshnessRow> Sources)> GetFreshnessAsync(
        string tenantId, DateTime cutoff30Utc, DateTime cutoff90Utc, CancellationToken ct)
    {
        var counts = await FreshnessQuery(tenantId, cutoff30Utc, cutoff90Utc)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        // No entity with an Active cupo: GROUP BY over an empty set yields no row at all.
        if (counts is null)
        {
            return (0, [
                new DashboardFreshnessRow(RescoreSource.Arca,   0, 0, 0, 0),
                new DashboardFreshnessRow(RescoreSource.Lufe,   0, 0, 0, 0),
                new DashboardFreshnessRow(RescoreSource.Bcra,   0, 0, 0, 0),
                new DashboardFreshnessRow(RescoreSource.Casfog, 0, 0, 0, 0),
                new DashboardFreshnessRow(RescoreSource.Nosis,  0, 0, 0, 0)
            ]);
        }

        return (counts.Total, [
            new DashboardFreshnessRow(RescoreSource.Arca,   counts.ArcaFresh,   counts.ArcaMid,   counts.ArcaOld,   counts.ArcaNever),
            new DashboardFreshnessRow(RescoreSource.Lufe,   counts.LufeFresh,   counts.LufeMid,   counts.LufeOld,   counts.LufeNever),
            new DashboardFreshnessRow(RescoreSource.Bcra,   counts.BcraFresh,   counts.BcraMid,   counts.BcraOld,   counts.BcraNever),
            new DashboardFreshnessRow(RescoreSource.Casfog, counts.CasfogFresh, counts.CasfogMid, counts.CasfogOld, counts.CasfogNever),
            new DashboardFreshnessRow(RescoreSource.Nosis,  counts.NosisFresh,  counts.NosisMid,  counts.NosisOld,  counts.NosisNever)
        ]);
    }

    // ── Query shapes — internal so the SQL Server translation can be asserted with ToQueryString ──

    /// <summary>
    /// ApiCallLogs of the tenant in [<paramref name="fromUtc"/>, <paramref name="toUtc"/>], aggregated per
    /// Service and outcome. Whether a failed outcome is a vendor's "no data" answer is decided by the
    /// caller (ApiCallNoDataClassifier), not here — so the rule lives in one place, not in SQL. SQL only
    /// flags the rows whose ErrorMessage starts with a LUFE "no data" marker (LufeCallOutcome), so the
    /// text itself never leaves the database.
    /// </summary>
    internal IQueryable<DashboardApiCallOutcomeRow> ApiCallOutcomesQuery(string tenantId, DateTime fromUtc, DateTime toUtc)
        => ApiCallWindow(tenantId, fromUtc, toUtc)
            .GroupBy(l => new
            {
                l.Service,
                l.IsSuccess,
                l.ResponseStatus,
                HasErrorMessage     = l.ErrorMessage != null,
                HasLufeNoDataMarker = l.ErrorMessage != null
                    && (l.ErrorMessage.StartsWith(LufeCallOutcome.EmptyAnswerPrefix)
                        || l.ErrorMessage.StartsWith(LufeCallOutcome.NoLegajoPrefix))
            })
            .Select(g => new DashboardApiCallOutcomeRow(
                g.Key.Service,
                g.Key.IsSuccess,
                g.Key.ResponseStatus,
                g.Key.HasErrorMessage,
                g.Key.HasLufeNoDataMarker,
                g.Count(),
                g.Max(l => l.CalledAt)));

    /// <summary>
    /// Service and DurationMs of the window's rows that carry a duration — two columns, for a p95 that EF
    /// cannot ask SQL Server for.
    /// </summary>
    internal IQueryable<DashboardApiCallDurationRow> ApiCallDurationsQuery(string tenantId, DateTime fromUtc, DateTime toUtc)
        => ApiCallWindow(tenantId, fromUtc, toUtc)
            .Where(l => l.DurationMs != null)
            .Select(l => new DashboardApiCallDurationRow(l.Service, l.DurationMs!.Value));

    /// <summary>
    /// One row of conditional counts over the entities that hold an Active cupo: per source, how many were
    /// refreshed after <paramref name="cutoff30Utc"/>, between the two cutoffs, before
    /// <paramref name="cutoff90Utc"/>, and never. Only the timestamps are read — no snapshot blob.
    /// </summary>
    internal IQueryable<FreshnessCounts> FreshnessQuery(string tenantId, DateTime cutoff30Utc, DateTime cutoff90Utc)
    {
        var activeLines = _dbContext.CreditLines.AsNoTracking().Where(c => c.TenantId == tenantId && c.Status == ActiveLine);

        return _dbContext.Entities
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && activeLines.Any(c => c.EntityId == e.EntityId))
            .GroupBy(e => 1)
            .Select(g => new FreshnessCounts
            {
                Total       = g.Count(),
                ArcaFresh   = g.Count(e => e.FiscalDataUpdatedAt > cutoff30Utc),
                ArcaMid     = g.Count(e => e.FiscalDataUpdatedAt <= cutoff30Utc && e.FiscalDataUpdatedAt >= cutoff90Utc),
                ArcaOld     = g.Count(e => e.FiscalDataUpdatedAt < cutoff90Utc),
                ArcaNever   = g.Count(e => e.FiscalDataUpdatedAt == null),
                LufeFresh   = g.Count(e => e.LufeDataUpdatedAt > cutoff30Utc),
                LufeMid     = g.Count(e => e.LufeDataUpdatedAt <= cutoff30Utc && e.LufeDataUpdatedAt >= cutoff90Utc),
                LufeOld     = g.Count(e => e.LufeDataUpdatedAt < cutoff90Utc),
                LufeNever   = g.Count(e => e.LufeDataUpdatedAt == null),
                BcraFresh   = g.Count(e => e.BcraDataUpdatedAt > cutoff30Utc),
                BcraMid     = g.Count(e => e.BcraDataUpdatedAt <= cutoff30Utc && e.BcraDataUpdatedAt >= cutoff90Utc),
                BcraOld     = g.Count(e => e.BcraDataUpdatedAt < cutoff90Utc),
                BcraNever   = g.Count(e => e.BcraDataUpdatedAt == null),
                CasfogFresh = g.Count(e => e.CasfogDataUpdatedAt > cutoff30Utc),
                CasfogMid   = g.Count(e => e.CasfogDataUpdatedAt <= cutoff30Utc && e.CasfogDataUpdatedAt >= cutoff90Utc),
                CasfogOld   = g.Count(e => e.CasfogDataUpdatedAt < cutoff90Utc),
                CasfogNever = g.Count(e => e.CasfogDataUpdatedAt == null),
                NosisFresh  = g.Count(e => e.NosisDataUpdatedAt > cutoff30Utc),
                NosisMid    = g.Count(e => e.NosisDataUpdatedAt <= cutoff30Utc && e.NosisDataUpdatedAt >= cutoff90Utc),
                NosisOld    = g.Count(e => e.NosisDataUpdatedAt < cutoff90Utc),
                NosisNever  = g.Count(e => e.NosisDataUpdatedAt == null)
            });
    }

    private IQueryable<ApiCallLog> ApiCallWindow(string tenantId, DateTime fromUtc, DateTime toUtc)
        => _dbContext.ApiCallLogs
            .AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.CalledAt >= fromUtc && l.CalledAt <= toUtc);

    /// <summary>The single row <see cref="FreshnessQuery"/> returns. Member-initialised so EF can project into it.</summary>
    internal sealed class FreshnessCounts
    {
        public int Total { get; init; }
        public int ArcaFresh { get; init; }
        public int ArcaMid { get; init; }
        public int ArcaOld { get; init; }
        public int ArcaNever { get; init; }
        public int LufeFresh { get; init; }
        public int LufeMid { get; init; }
        public int LufeOld { get; init; }
        public int LufeNever { get; init; }
        public int BcraFresh { get; init; }
        public int BcraMid { get; init; }
        public int BcraOld { get; init; }
        public int BcraNever { get; init; }
        public int CasfogFresh { get; init; }
        public int CasfogMid { get; init; }
        public int CasfogOld { get; init; }
        public int CasfogNever { get; init; }
        public int NosisFresh { get; init; }
        public int NosisMid { get; init; }
        public int NosisOld { get; init; }
        public int NosisNever { get; init; }
    }
}
