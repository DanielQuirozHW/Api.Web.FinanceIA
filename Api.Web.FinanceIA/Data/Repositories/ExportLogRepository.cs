// Project : Api.Web.FinanceIA
// Created : 2026-05-09

using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IExportLogRepository"/>.
/// Append-only — each export generates one immutable row.
/// Write operations call ChangeTracker.DetectChanges() manually because
/// AutoDetectChangesEnabled is disabled on <see cref="FinanciaDbContext"/>.
/// </summary>
public sealed class ExportLogRepository : IExportLogRepository
{
    private readonly FinanciaDbContext             _db;
    private readonly ILogger<ExportLogRepository>  _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="ExportLogRepository"/>.
    /// </summary>
    public ExportLogRepository(FinanciaDbContext db, ILogger<ExportLogRepository> logger)
    {
        _db     = db;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ExportLog> CreateAsync(ExportLog log, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(log);

        _db.ExportLogs.Add(log);
        _db.ChangeTracker.DetectChanges();
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "ExportLog created: {ExportLogId} for analysis {AnalysisId} tenant {TenantId} format {Format}",
            log.ExportLogId,
            log.AnalysisId,
            log.TenantId,
            log.Format);

        return log;
    }
}
