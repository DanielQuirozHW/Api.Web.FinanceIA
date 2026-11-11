// Project : Api.Web.ServiciosFinancieros
// Created : 2026-04-20
// Modified: 2026-04-20

using Api.Web.ServiciosFinancieros.Models;

namespace Api.Web.ServiciosFinancieros.Clases;

/// <summary>
/// Persists error log entries to the SQL Server ErrorLogs table
/// for operational review without requiring access to Azure Portal.
/// Works in combination with ILogger&lt;T&gt; → Application Insights.
/// </summary>
public interface IErrorLogService
{
    /// <summary>
    /// Persists the given <paramref name="entry"/> to the ErrorLogs table.
    /// Implementations must never throw — a logging failure must not
    /// affect the API response.
    /// </summary>
    /// <param name="entry">The error log entry to persist.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    Task LogAsync(ErrorLogEntry entry, CancellationToken cancellationToken);
}
