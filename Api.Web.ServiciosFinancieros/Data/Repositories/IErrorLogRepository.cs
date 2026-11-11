// Project : Api.Web.ServiciosFinancieros
// Created : 2026-04-15
// Modified: 2026-04-15

using Api.Web.ServiciosFinancieros.Models;

namespace Api.Web.ServiciosFinancieros.Data.Repositories;

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
