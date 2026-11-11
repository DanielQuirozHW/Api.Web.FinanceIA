// Project : Api.Web.FinanceIA
// Created : 2026-06-20

using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// EF Core read-only implementation of <see cref="IAuditLogRepository"/>.
/// All queries use AsNoTracking and DB-level pagination because the audit log
/// table can grow very large.
/// </summary>
public sealed class AuditLogRepository : IAuditLogRepository
{
    private readonly FinanciaDbContext _dbContext;
    private readonly ILogger<AuditLogRepository> _logger;

    public AuditLogRepository(FinanciaDbContext dbContext, ILogger<AuditLogRepository> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyList<AuditLog> Items, int Total)> GetPagedAsync(
        string tenantId, AuditLogFilter filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var query = _dbContext.AuditLogs
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId);

        if (filter.UserId.HasValue)
            query = query.Where(x => x.UserId == filter.UserId.Value);

        if (!string.IsNullOrEmpty(filter.Action))
            query = query.Where(x => x.Action.Contains(filter.Action));

        if (!string.IsNullOrEmpty(filter.Severity))
            query = query.Where(x => x.Severity == filter.Severity);

        if (filter.DateFrom.HasValue)
            query = query.Where(x => x.Timestamp >= filter.DateFrom.Value);

        if (filter.DateTo.HasValue)
            query = query.Where(x => x.Timestamp <= filter.DateTo.Value);

        if (!string.IsNullOrEmpty(filter.Search))
        {
            var search = filter.Search;
            query = query.Where(x =>
                x.Action.Contains(search) ||
                x.Source!.Contains(search) ||
                x.EntityType!.Contains(search) ||
                x.EntityId!.Contains(search));
        }

        var total = await query.CountAsync(ct).ConfigureAwait(false);

        var items = await query
            .OrderByDescending(x => x.Timestamp)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "AuditLog paged query: tenant {TenantId}, page {Page}/{PageSize}, total {Total}",
            tenantId, filter.Page, filter.PageSize, total);

        return (items, total);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetDistinctActionsAsync(
        string tenantId, CancellationToken ct)
    {
        return await _dbContext.AuditLogs
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .Select(x => x.Action)
            .Distinct()
            .OrderBy(a => a)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }
}
