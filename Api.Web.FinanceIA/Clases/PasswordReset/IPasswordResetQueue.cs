// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Clases.PasswordReset;

/// <summary>
/// In-process hand-off between POST api/bff/auth/password-reset and <see cref="PasswordResetWorker"/>.
/// The controller answers 202 as soon as an item is written, so a known and an unknown address take
/// the same time to answer — the slow Firebase and SendGrid calls happen off the request.
/// </summary>
public interface IPasswordResetQueue
{
    /// <summary>Queues one request. False when the queue is full; the caller still answers 202.</summary>
    bool TryEnqueue(PasswordResetWorkItem item);

    /// <summary>Yields queued requests until <paramref name="ct"/> is cancelled. Read by the worker only.</summary>
    IAsyncEnumerable<PasswordResetWorkItem> ReadAllAsync(CancellationToken ct);
}
