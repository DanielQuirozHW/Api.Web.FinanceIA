// Project : Api.Web.FinanceIA
// Created : 2026-05-11

using Api.Web.FinanceIA.Models;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Sends transactional emails via an external provider (e.g. SendGrid).
/// </summary>
public interface IEmailService
{
    /// <summary>
    /// Sends a user invitation email with a registration link through the SendGrid dynamic template
    /// in <see cref="InvitationEmail.TemplateId"/> (or <c>SendGrid:InvitationTemplateId</c>, or the
    /// built-in invitation template). Throws <see cref="InvalidOperationException"/> when the
    /// provider rejects the request. Never logs the recipient or the link.
    /// </summary>
    Task SendInvitationEmailAsync(
        InvitationEmail message,
        string tenantId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Sends a password-reset email through the SendGrid dynamic template in
    /// <see cref="PasswordResetEmail.TemplateId"/> (or <c>SendGrid:PasswordResetTemplateId</c>).
    /// Throws <see cref="InvalidOperationException"/> when no template is configured or the provider
    /// rejects the request. Never logs the recipient or the link.
    /// </summary>
    Task SendPasswordResetEmailAsync(
        PasswordResetEmail message,
        string tenantId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Sends an email-verification email through the SendGrid dynamic template in
    /// <see cref="EmailVerificationEmail.TemplateId"/> (or <c>SendGrid:EmailVerificationTemplateId</c>).
    /// Throws <see cref="InvalidOperationException"/> when no template is configured or the provider
    /// rejects the request. Never logs the recipient or the link.
    /// </summary>
    Task SendEmailVerificationEmailAsync(
        EmailVerificationEmail message,
        string tenantId,
        CancellationToken cancellationToken);
}
