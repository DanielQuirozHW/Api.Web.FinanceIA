// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Models;

/// <summary>
/// Everything <see cref="Clases.IEmailService.SendPasswordResetEmailAsync"/> needs to send one
/// password-reset email. The sender and template are the TENANT's when it has them configured in
/// TenantSettings; null means "use the global SendGrid configuration".
/// </summary>
/// <remarks>
/// <see cref="ToEmail"/> and <see cref="ResetLink"/> are PII and a live credential respectively:
/// neither may be logged.
/// </remarks>
public sealed record PasswordResetEmail
{
    /// <summary>Recipient address.</summary>
    public required string ToEmail { get; init; }

    /// <summary>The Portal /recover link carrying the Firebase oobCode. Never log it.</summary>
    public required string ResetLink { get; init; }

    /// <summary>Product name shown in the email ("Financia").</summary>
    public required string ProductName { get; init; }

    /// <summary>Human-readable link lifetime in Spanish, e.g. "1 hora".</summary>
    public required string ExpiresInText { get; init; }

    /// <summary>Tenant sender address, or null for <c>SendGrid:FromEmail</c>.</summary>
    public string? FromEmail { get; init; }

    /// <summary>Tenant sender display name, or null for <c>SendGrid:FromName</c>.</summary>
    public string? FromName { get; init; }

    /// <summary>Tenant SendGrid dynamic template id, or null for <c>SendGrid:PasswordResetTemplateId</c>.</summary>
    public string? TemplateId { get; init; }

    /// <summary>Tenant company name (<c>branding.company_name</c>), or null to omit <c>companyName</c> from the template data.</summary>
    public string? CompanyName { get; init; }

    /// <summary>Tenant logo URL (<c>branding.logo_url</c>, absolute https), or null to omit <c>logoUrl</c> from the template data.</summary>
    public string? LogoUrl { get; init; }
}
