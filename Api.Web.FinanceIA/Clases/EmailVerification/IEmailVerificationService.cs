// Project : Api.Web.FinanceIA
// Created : 2026-10-21

namespace Api.Web.FinanceIA.Clases.EmailVerification;

/// <summary>
/// Does the work behind one accepted email-verification request: tenant settings, the Firebase link
/// from the tenant's app, and the SendGrid email. Runs in <see cref="EmailVerificationWorker"/>, after
/// the caller already got its 202.
/// </summary>
public interface IEmailVerificationService
{
    /// <summary>
    /// Processes <paramref name="item"/> and returns what happened. Never throws for a configuration
    /// or provider failure — those become <see cref="EmailVerificationOutcome.TemplateMissing"/>,
    /// <see cref="EmailVerificationOutcome.LinkFailed"/> or <see cref="EmailVerificationOutcome.SendFailed"/>.
    /// Only cancellation propagates.
    /// </summary>
    Task<EmailVerificationOutcome> ProcessAsync(EmailVerificationWorkItem item, CancellationToken ct);
}
