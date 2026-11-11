// Project : Api.Web.FinanceIA
// Created : 2026-10-21

namespace Api.Web.FinanceIA.Clases.EmailVerification;

/// <summary>
/// Drains <see cref="IEmailVerificationQueue"/> one request at a time, each in its own DI scope (the
/// service needs the scoped repositories and DbContext, which the original request scope has already
/// disposed). A failure in one request never stops the loop; what reaches this class is logged by
/// exception TYPE only, since the work item holds an email address.
/// </summary>
public sealed class EmailVerificationWorker : BackgroundService
{
    private readonly IEmailVerificationQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EmailVerificationWorker> _logger;

    /// <summary>Initializes a new instance of <see cref="EmailVerificationWorker"/>.</summary>
    public EmailVerificationWorker(
        IEmailVerificationQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<EmailVerificationWorker> logger)
    {
        _queue        = queue;
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>Processes one request in its own scope. Internal so tests can drive it without the loop.</summary>
    internal async Task ProcessOneAsync(EmailVerificationWorkItem item, CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<IEmailVerificationService>();

            await service.ProcessAsync(item, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                "Background email verification for tenant {TenantId} user {UserId} threw {ExceptionType}",
                item.TenantContext.TenantId, item.UserId, ex.GetType().Name);
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
