// Project : Api.Web.FinanceIA
// Created : 2026-10-21

namespace Api.Web.FinanceIA.Clases.EmailVerification;

/// <summary>
/// In-process hand-off between POST api/bff/auth/email-verification and
/// <see cref="EmailVerificationWorker"/>. The controller answers 202 as soon as an item is written;
/// the Firebase link and the SendGrid call happen off the request.
/// </summary>
public interface IEmailVerificationQueue
{
    /// <summary>Queues one request. False when the queue is full; the caller still answers 202.</summary>
    bool TryEnqueue(EmailVerificationWorkItem item);

    /// <summary>Yields queued requests until <paramref name="ct"/> is cancelled. Read by the worker only.</summary>
    IAsyncEnumerable<EmailVerificationWorkItem> ReadAllAsync(CancellationToken ct);
}
