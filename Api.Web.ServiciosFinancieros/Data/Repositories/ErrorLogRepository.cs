// Project : Api.Web.ServiciosFinancieros
// Created : 2026-04-15
// Modified: 2026-04-15

using Api.Web.ServiciosFinancieros.Models;

namespace Api.Web.ServiciosFinancieros.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IErrorLogRepository"/>.
/// Write-only repository — persists error entries to [dbo].[ErrorLogs].
/// Never throws: a failure here must not affect the API response.
/// </summary>
public sealed class ErrorLogRepository : IErrorLogRepository
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<ErrorLogRepository> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="ErrorLogRepository"/>.
    /// </summary>
    public ErrorLogRepository(
        AppDbContext dbContext,
        ILogger<ErrorLogRepository> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task AddAsync(
        ErrorLog errorLog,
        CancellationToken cancellationToken)
    {
        try
        {
            _dbContext.ErrorLogs.Add(errorLog);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex,
                "Failed to persist error log. TraceId: {TraceId}",
                errorLog.TraceId);
        }
    }
}
