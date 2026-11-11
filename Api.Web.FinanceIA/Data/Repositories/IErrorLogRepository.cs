// Project : Api.Web.FinanceIA
// Created : 2026-04-15
// Modified: 2026-04-15

using Api.Web.FinanceIA.Models;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// Repository for writing <see cref="ErrorLog"/> records
/// to [dbo].[ErrorLogs].
/// </summary>
public interface IErrorLogRepository
{
    /// <summary>
    /// Persists an error log entry to the database.
    /// Implementations must never throw — logging failure must not
    /// affect the API response.
    /// </summary>
    Task AddAsync(
        ErrorLog errorLog,
        CancellationToken cancellationToken);
}
