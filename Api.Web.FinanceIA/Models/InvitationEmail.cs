// Project : Api.Web.FinanceIA
// Created : 2026-10-26

namespace Api.Web.FinanceIA.Models;

/// <summary>
/// Everything <see cref="Clases.IEmailService.SendInvitationEmailAsync"/> needs to send one invitation
/// email, first-admin bootstrap included. The sender and template are the TENANT's when it has them
/// configured in TenantSettings; null means "use the global SendGrid configuration".
/// </summary>
/// <remarks>
/// <see cref="ToEmail"/> and <see cref="InvitationLink"/> are PII and a live registration token
/// respectively: neither may be logged.
/// </remarks>
public sealed record InvitationEmail
{
    /// <summary>Recipient address.</summary>
    public required string ToEmail { get; init; }

    /// <summary>The Portal /register/invite link carrying the invitation token. Never log it.</summary>
    public required string InvitationLink { get; init; }

    /// <summary>Inviter display name, or the neutral fallback when there is none (bootstrap).</summary>
    public required string InviterName { get; init; }

    /// <summary>Tenant sender address, or null for <c>SendGrid:FromEmail</c>.</summary>
    public string? FromEmail { get; init; }

    /// <summary>Tenant sender display name, or null for <c>SendGrid:FromName</c>.</summary>
    public string? FromName { get; init; }

    /// <summary>
    /// Tenant SendGrid dynamic template id, or null for <c>SendGrid:InvitationTemplateId</c> (and,
    /// failing that, the historical built-in invitation template).
    /// </summary>
    public string? TemplateId { get; init; }

    /// <summary>Tenant company name (<c>branding.company_name</c>), or null to omit <c>companyName</c> from the template data.</summary>
    public string? CompanyName { get; init; }

    /// <summary>Tenant logo URL (<c>branding.logo_url</c>, absolute https), or null to omit <c>logoUrl</c> from the template data.</summary>
    public string? LogoUrl { get; init; }
}
