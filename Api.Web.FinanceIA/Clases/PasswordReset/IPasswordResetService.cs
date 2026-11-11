// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Clases.PasswordReset;

/// <summary>
/// Does the work behind one accepted password-reset request: tenant-scoped user lookup, Firebase
/// link generation with the tenant's app, and the SendGrid email. Runs in
/// <see cref="PasswordResetWorker"/>, after the caller already got its 202.
/// </summary>
public interface IPasswordResetService
{
    /// <summary>
    /// Processes <paramref name="item"/> and returns what happened. Never throws for a business or
    /// provider failure — those become <see cref="PasswordResetOutcome.LinkFailed"/> or
    /// <see cref="PasswordResetOutcome.SendFailed"/> with an Error log. Only cancellation propagates.
    /// </summary>
    Task<PasswordResetOutcome> ProcessAsync(PasswordResetWorkItem item, CancellationToken ct);
}
