// Project : Api.Web.FinanceIA
// Created : 2026-05-11

using Api.Web.FinanceIA.Clases.PasswordReset;
using Api.Web.FinanceIA.Models;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Sends transactional emails via SendGrid.
/// Requires SendGrid:ApiKey, SendGrid:FromEmail and SendGrid:FromName
/// configuration entries.
/// </summary>
public sealed class SendGridEmailService : IEmailService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<SendGridEmailService> _logger;
    private readonly HttpMessageHandler? _sendGridHandler;

    public SendGridEmailService(
        IConfiguration configuration,
        ILogger<SendGridEmailService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Test seam: routes SendGrid calls through <paramref name="sendGridHandler"/> instead of the
    /// network. Internal, so DI only ever sees the public constructor.
    /// </summary>
    internal SendGridEmailService(
        IConfiguration configuration,
        ILogger<SendGridEmailService> logger,
        HttpMessageHandler sendGridHandler)
        : this(configuration, logger)
    {
        _sendGridHandler = sendGridHandler;
    }

    /// <inheritdoc />
    public async Task SendInvitationEmailAsync(
        InvitationEmail message,
        string tenantId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var apiKey = _configuration["SendGrid:ApiKey"]
            ?? throw new InvalidOperationException("SendGrid:ApiKey not configured");

        // Unlike reset and verification, the invitation keeps its historical built-in template as the
        // last fallback: it predates per-tenant templates and every deployed client relies on it.
        var templateId = string.IsNullOrWhiteSpace(message.TemplateId)
            ? _configuration["SendGrid:InvitationTemplateId"] ?? "d-5f0dd166b22c41d98278e6ac28711be2"
            : message.TemplateId;

        var client = CreateClient(apiKey);
        var from = ResolveFrom(message.FromEmail, message.FromName, defaultName: "FinanceIA");
        var to = new EmailAddress(message.ToEmail);

        var dynamicTemplateData = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["inviterName"]    = message.InviterName,
            ["invitationLink"] = message.InvitationLink
        };
        AddBranding(dynamicTemplateData, message.CompanyName, message.LogoUrl);
        var msg = MailHelper.CreateSingleTemplateEmail(from, to, templateId, dynamicTemplateData);

        var response = await client.SendEmailAsync(msg, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode != System.Net.HttpStatusCode.Accepted)
        {
            _logger.LogError(
                "Failed to send invitation email. Status: {StatusCode}, TenantId: {TenantId}",
                response.StatusCode,
                tenantId);
            throw new InvalidOperationException($"Failed to send email: {response.StatusCode}");
        }

        // The address is PII: log its EmailHasher tag, never the address.
        _logger.LogInformation(
            "Invitation email sent to {EmailHash}, TenantId: {TenantId}",
            EmailHasher.Hash(message.ToEmail),
            tenantId);
    }

    /// <inheritdoc />
    public async Task SendPasswordResetEmailAsync(
        PasswordResetEmail message,
        string tenantId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var apiKey = _configuration["SendGrid:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("SendGrid:ApiKey not configured");
        }

        // No hardcoded fallback, unlike the invitation: the reset template is new, and sending an
        // unrelated template id would deliver the wrong email. Unset means "not deployed yet".
        var templateId = string.IsNullOrWhiteSpace(message.TemplateId)
            ? _configuration["SendGrid:PasswordResetTemplateId"]
            : message.TemplateId;
        if (string.IsNullOrWhiteSpace(templateId))
        {
            throw new InvalidOperationException("SendGrid:PasswordResetTemplateId not configured");
        }

        var client = CreateClient(apiKey);
        var from = ResolveFrom(message.FromEmail, message.FromName, defaultName: "Financia");
        var to = new EmailAddress(message.ToEmail);

        var dynamicTemplateData = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["productName"]   = message.ProductName,
            ["resetLink"]     = message.ResetLink,
            ["expiresInText"] = message.ExpiresInText
        };
        AddBranding(dynamicTemplateData, message.CompanyName, message.LogoUrl);
        var msg = MailHelper.CreateSingleTemplateEmail(from, to, templateId, dynamicTemplateData);

        var response = await client.SendEmailAsync(msg, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode != System.Net.HttpStatusCode.Accepted)
        {
            _logger.LogError(
                "Failed to send password reset email. Status: {StatusCode}, TenantId: {TenantId}",
                response.StatusCode,
                tenantId);
            throw new InvalidOperationException($"Failed to send email: {response.StatusCode}");
        }

        // Recipient and link deliberately absent: the address is PII and the link is a credential.
        _logger.LogInformation(
            "Password reset email accepted by SendGrid, TenantId: {TenantId}",
            tenantId);
    }

    /// <inheritdoc />
    public async Task SendEmailVerificationEmailAsync(
        EmailVerificationEmail message,
        string tenantId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var apiKey = _configuration["SendGrid:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("SendGrid:ApiKey not configured");
        }

        // No hardcoded fallback, as for the reset template: sending an unrelated template id would
        // deliver the wrong email. EmailVerificationService already ends as TemplateMissing before a
        // link exists; this check is the backstop.
        var templateId = string.IsNullOrWhiteSpace(message.TemplateId)
            ? _configuration["SendGrid:EmailVerificationTemplateId"]
            : message.TemplateId;
        if (string.IsNullOrWhiteSpace(templateId))
        {
            throw new InvalidOperationException("SendGrid:EmailVerificationTemplateId not configured");
        }

        var client = CreateClient(apiKey);
        var from = ResolveFrom(message.FromEmail, message.FromName, defaultName: "Financia");
        var to = new EmailAddress(message.ToEmail);

        var dynamicTemplateData = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["productName"] = message.ProductName,
            ["verifyLink"]  = message.VerifyLink
        };
        AddBranding(dynamicTemplateData, message.CompanyName, message.LogoUrl);
        var msg = MailHelper.CreateSingleTemplateEmail(from, to, templateId, dynamicTemplateData);

        var response = await client.SendEmailAsync(msg, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode != System.Net.HttpStatusCode.Accepted)
        {
            _logger.LogError(
                "Failed to send email verification email. Status: {StatusCode}, TenantId: {TenantId}",
                response.StatusCode,
                tenantId);
            throw new InvalidOperationException($"Failed to send email: {response.StatusCode}");
        }

        // Recipient and link deliberately absent: the address is PII and the link is a credential.
        _logger.LogInformation(
            "Email verification email accepted by SendGrid, TenantId: {TenantId}",
            tenantId);
    }

    /// <summary>
    /// The tenant's sender when it has one, else <c>SendGrid:FromEmail</c> / <c>SendGrid:FromName</c>,
    /// else the built-in address and <paramref name="defaultName"/>. The invitation passes
    /// <c>"FinanceIA"</c> and the others <c>"Financia"</c>, as each always has.
    /// </summary>
    private EmailAddress ResolveFrom(string? fromEmail, string? fromName, string defaultName)
        => new(
            string.IsNullOrWhiteSpace(fromEmail)
                ? _configuration["SendGrid:FromEmail"] ?? "noreply@financia.app"
                : fromEmail,
            string.IsNullOrWhiteSpace(fromName)
                ? _configuration["SendGrid:FromName"] ?? defaultName
                : fromName);

    /// <summary>
    /// Adds <c>companyName</c> and <c>logoUrl</c> to the template data only when the tenant has them.
    /// An unset value is OMITTED, never sent as "" or null, so a template's
    /// <c>{{#if companyName}}…{{else}}Financia{{/if}}</c> falls through, and a tenant with no branding
    /// rows sends exactly the payload it always did.
    /// </summary>
    private static void AddBranding(Dictionary<string, string> data, string? companyName, string? logoUrl)
    {
        if (!string.IsNullOrWhiteSpace(companyName))
        {
            data["companyName"] = companyName;
        }

        if (!string.IsNullOrWhiteSpace(logoUrl))
        {
            data["logoUrl"] = logoUrl;
        }
    }

    private SendGridClient CreateClient(string apiKey)
        => _sendGridHandler is null
            ? new SendGridClient(apiKey)
            : new SendGridClient(new HttpClient(_sendGridHandler, disposeHandler: false), apiKey);
}
