// Project : Api.Web.FinanceIA
// Created : 2026-05-09

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// Persistence operations for <see cref="ExportLog"/> records.
/// Append-only — exports are never modified or deleted.
/// </summary>
public interface IExportLogRepository
{
    /// <summary>
    /// Persists a new export log entry.
    /// </summary>
    Task<ExportLog> CreateAsync(ExportLog log, CancellationToken ct);
}
