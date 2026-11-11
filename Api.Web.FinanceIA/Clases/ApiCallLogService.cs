// Project : Api.Web.FinanceIA
// Created : 2026-05-08

using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.Extensions.DependencyInjection;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// EF Core implementation of <see cref="IApiCallLogService"/>.
/// Appends records to [dbo].[ApiCallLogs]. Fire-and-forget safe — all exceptions
/// are caught internally so that logging failures never interrupt the request flow.
/// Uses its own DI scope to avoid DbContext concurrency with the request pipeline.
/// </summary>
public sealed class ApiCallLogService : IApiCallLogService
{
    private readonly IServiceScopeFactory       _scopeFactory;
    private readonly ILogger<ApiCallLogService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="ApiCallLogService"/>.
    /// </summary>
    public ApiCallLogService(IServiceScopeFactory scopeFactory, ILogger<ApiCallLogService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <inheritdoc />
    public async Task LogAsync(
        string   tenantId,
        string   service,
        string   endpoint,
        int?     responseStatus,
        int?     inputTokens,
        int?     outputTokens,
        decimal? estimatedCostUsd,
        bool     isSuccess,
        string?  errorMessage,
        int      durationMs,
        string?  operation     = null,
        string?  model         = null,
        Guid?    userId        = null,
        Guid?    analysisId    = null,
        long?    chatMessageId = null,
        Guid?    documentId    = null,
        CancellationToken ct   = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FinanciaDbContext>();

            var entry = new ApiCallLog
            {
                TenantId         = tenantId,
                Service          = service,
                Endpoint         = endpoint,
                ResponseStatus   = responseStatus,
                InputTokens      = inputTokens,
                OutputTokens     = outputTokens,
                EstimatedCostUsd = estimatedCostUsd,
                IsSuccess        = isSuccess,
                ErrorMessage     = errorMessage,
                DurationMs       = durationMs,
                CalledAt         = DateTime.UtcNow,
                Operation        = operation,
                Model            = model,
                UserId           = userId,
                AnalysisId       = analysisId,
                ChatMessageId    = chatMessageId,
                DocumentId       = documentId
            };

            db.ApiCallLogs.Add(entry);
            db.ChangeTracker.DetectChanges();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "ApiCallLog write failed for tenant {TenantId} service {Service} operation {Operation}; continuing",
                tenantId, service, operation);
        }
    }
}
