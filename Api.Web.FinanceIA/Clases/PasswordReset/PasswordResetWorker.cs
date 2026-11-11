// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Clases.PasswordReset;

/// <summary>
/// Drains <see cref="IPasswordResetQueue"/> one request at a time, each in its own DI scope (the
/// service needs the scoped repositories and DbContext, which the original request scope has already
/// disposed). A failure in one request never stops the loop; what reaches this class is logged by
/// exception TYPE only, since the work item holds an email address.
/// </summary>
public sealed class PasswordResetWorker : BackgroundService
{
    private readonly IPasswordResetQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PasswordResetWorker> _logger;

    /// <summary>Initializes a new instance of <see cref="PasswordResetWorker"/>.</summary>
    public PasswordResetWorker(
        IPasswordResetQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<PasswordResetWorker> logger)
    {
        _queue        = queue;
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>Processes one request in its own scope. Internal so tests can drive it without the loop.</summary>
    internal async Task ProcessOneAsync(PasswordResetWorkItem item, CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<IPasswordResetService>();

            await service.ProcessAsync(item, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                "Background password reset for tenant {TenantId} threw {ExceptionType}",
                item.TenantContext.TenantId, ex.GetType().Name);
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var item in _queue.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await ProcessOneAsync(item, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown. Anything still queued is dropped; the user asks again.
        }
    }
}
