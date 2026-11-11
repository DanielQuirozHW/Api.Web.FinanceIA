// Project : Api.Web.FinanceIA
// Created : 2026-10-21

namespace Api.Web.FinanceIA.Models;

/// <summary>
/// Everything <see cref="Clases.IEmailService.SendEmailVerificationEmailAsync"/> needs to send one
/// email-verification email. The sender is the TENANT's when it has one configured in
/// TenantSettings; null means "use the global SendGrid configuration".
/// </summary>
/// <remarks>
/// <see cref="ToEmail"/> and <see cref="VerifyLink"/> are PII and a live credential respectively:
/// neither may be logged.
/// </remarks>
public sealed record EmailVerificationEmail
{
    /// <summary>Recipient address.</summary>
    public required string ToEmail { get; init; }

    /// <summary>The Portal /verify-email link carrying the Firebase oobCode. Never log it.</summary>
    public required string VerifyLink { get; init; }

    /// <summary>Product name shown in the email ("Financia").</summary>
    public required string ProductName { get; init; }

    /// <summary>Tenant sender address, or null for <c>SendGrid:FromEmail</c>.</summary>
    public string? FromEmail { get; init; }

    /// <summary>Tenant sender display name, or null for <c>SendGrid:FromName</c>.</summary>
    public string? FromName { get; init; }

    /// <summary>
    /// SendGrid dynamic template id — the tenant's, else <c>SendGrid:EmailVerificationTemplateId</c>.
    /// Null only falls back to that same configuration key once more; there is no hardcoded id.
    /// </summary>
    public string? TemplateId { get; init; }

    /// <summary>Tenant company name (<c>branding.company_name</c>), or null to omit <c>companyName</c> from the template data.</summary>
    public string? CompanyName { get; init; }

    /// <summary>Tenant logo URL (<c>branding.logo_url</c>, absolute https), or null to omit <c>logoUrl</c> from the template data.</summary>
    public string? LogoUrl { get; init; }
}
