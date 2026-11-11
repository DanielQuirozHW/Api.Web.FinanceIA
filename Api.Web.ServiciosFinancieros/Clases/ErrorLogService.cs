// Project : Api.Web.ServiciosFinancieros
// Created : 2026-04-20
// Modified: 2026-04-20

using Api.Web.ServiciosFinancieros.Data.Repositories;
using Api.Web.ServiciosFinancieros.Models;
using Microsoft.Extensions.Logging;

namespace Api.Web.ServiciosFinancieros.Clases;

/// <summary>
/// Persists error log entries to the SQL Server ErrorLogs table via
/// <see cref="IErrorLogRepository"/>. Never throws — any internal failure
/// is logged at Critical level and swallowed so the API response is
/// never affected by a logging failure.
/// </summary>
public sealed class ErrorLogService(
    IErrorLogRepository errorLogRepository,
    ILogger<ErrorLogService> logger) : IErrorLogService
{
    private readonly IErrorLogRepository _errorLogRepository = errorLogRepository;
    private readonly ILogger<ErrorLogService> _logger = logger;

    /// <summary>
    /// Maps <paramref name="entry"/> to an <see cref="ErrorLog"/> entity and
    /// persists it. Swallows all exceptions to guarantee the API response is
    /// never impacted by a logging failure.
    /// </summary>
    /// <param name="entry">The error log entry to persist.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    public async Task LogAsync(ErrorLogEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            var entity = new ErrorLog
            {
                Level            = entry.Level,
                Timestamp        = DateTime.UtcNow,
                Message          = entry.Message,
                ExceptionDetails = entry.ExceptionDetails,
                Source           = entry.Source,
                Url              = entry.Url,
                UserId           = entry.UserId,
                IpAddress        = entry.IpAddress,
                StackTrace       = entry.StackTrace,
                TraceId          = entry.TraceId,
            };

            await _errorLogRepository.AddAsync(entity, cancellationToken).ConfigureAwait(false);

            _logger.LogDebug(
                "Error log persisted for tenant {Source} TraceId {TraceId}",
                entry.Source,
                entry.TraceId);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex,
                "Failed to persist error log. TraceId: {TraceId}",
                entry.TraceId);
        }
    }
}
