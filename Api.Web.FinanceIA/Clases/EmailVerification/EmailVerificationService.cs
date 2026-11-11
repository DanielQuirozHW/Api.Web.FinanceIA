// Project : Api.Web.FinanceIA
// Created : 2026-10-21

using Api.Web.FinanceIA.Clases.PasswordReset;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Api.Web.FinanceIA.Clases.EmailVerification;

/// <summary>
/// Default <see cref="IEmailVerificationService"/>. Scoped; the worker opens one scope per request.
/// </summary>
/// <remarks>
/// <para>
/// The design is the password reset's (<see cref="PasswordResetService"/>): the emailed link is NOT
/// the one Firebase returns, which would open Firebase's hosted action page. The link is generated
/// with <c>ActionCodeSettings.Url</c> = the Portal base URL (which also makes Firebase check that
/// domain is authorised), its <c>oobCode</c> is taken out, and the email carries
/// <c>{portal}/verify-email?mode=verifyEmail&amp;oobCode=…</c> for the Portal to apply.
/// </para>
/// <para>
/// Nothing here logs the address or the link: the one outcome line carries the UserId and TenantId.
/// </para>
/// </remarks>
public sealed class EmailVerificationService : IEmailVerificationService
{
    private const string AuditAction = "User.EmailVerificationRequested";

    private static readonly string[] SettingKeys =
    [
        TenantSettingKeys.PortalUrl,
        TenantSettingKeys.EmailFromAddress,
        TenantSettingKeys.EmailFromName,
        TenantSettingKeys.EmailEmailVerificationTemplateId,
        .. TenantEmailSettings.BrandingKeys
    ];

    private readonly ITenantSettingsRepository _tenantSettingsRepository;
    private readonly ITenantFirebaseUserAdmin _firebaseUserAdmin;
    private readonly IEmailService _emailService;
    private readonly IAuditService _auditService;
    private readonly EmailVerificationOptions _options;
    private readonly ILogger<EmailVerificationService> _logger;

    /// <summary>Initializes a new instance of <see cref="EmailVerificationService"/>.</summary>
    public EmailVerificationService(
        ITenantSettingsRepository tenantSettingsRepository,
        ITenantFirebaseUserAdmin firebaseUserAdmin,
        IEmailService emailService,
        IAuditService auditService,
        IOptions<EmailVerificationOptions> options,
        ILogger<EmailVerificationService> logger)
    {
        _tenantSettingsRepository = tenantSettingsRepository;
        _firebaseUserAdmin        = firebaseUserAdmin;
        _emailService             = emailService;
        _auditService             = auditService;
        _options                  = options.Value;
        _logger                   = logger;
    }

    /// <inheritdoc />
    public async Task<EmailVerificationOutcome> ProcessAsync(EmailVerificationWorkItem item, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);

        var outcome = await RunAsync(item, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Email verification {Outcome} for tenant {TenantId} user {UserId}",
            outcome,
            item.TenantContext.TenantId,
            item.UserId);

        return outcome;
    }

    private async Task<EmailVerificationOutcome> RunAsync(EmailVerificationWorkItem item, CancellationToken ct)
    {
        var tenantContext = item.TenantContext;
        var tenantId      = tenantContext.TenantId;

        // 1. Tenant identity: portal URL, sender, template and branding, each falling back to global config.
        var settings = await TenantEmailSettings
            .ReadAsync(_tenantSettingsRepository, tenantId, SettingKeys, "email verification", _logger, ct)
            .ConfigureAwait(false);

        // 2. The template, checked first: without one nothing would be sent, so no link is generated.
        var templateId = settings.GetValueOrDefault(TenantSettingKeys.EmailEmailVerificationTemplateId)
                         ?? (string.IsNullOrWhiteSpace(_options.DefaultTemplateId) ? null : _options.DefaultTemplateId.Trim());
        if (templateId is null)
        {
            _logger.LogWarning(
                "Email verification for tenant {TenantId} has no SendGrid template: set {SettingKey} or SendGrid:EmailVerificationTemplateId",
                tenantId, TenantSettingKeys.EmailEmailVerificationTemplateId);
            return EmailVerificationOutcome.TemplateMissing;
        }

        var portalUrl = ResolvePortalUrl(settings.GetValueOrDefault(TenantSettingKeys.PortalUrl), tenantId);
        if (portalUrl is null)
        {
            _logger.LogError(
                "Email verification for tenant {TenantId} has no usable Portal URL: AppSettings:FrontendUrl is not an absolute URL",
                tenantId);
            return EmailVerificationOutcome.LinkFailed;
        }

        // 3. The Firebase link, from the tenant's own Firebase app.
        string? firebaseLink;
        try
        {
            firebaseLink = await _firebaseUserAdmin
                .GenerateEmailVerificationLinkAsync(item.Email, portalUrl, tenantContext, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                "Email verification link generation failed for tenant {TenantId} user {UserId}: {ExceptionType}",
                tenantId, item.UserId, ex.GetType().Name);
            return EmailVerificationOutcome.LinkFailed;
        }

        if (firebaseLink is null)
        {
            return EmailVerificationOutcome.LinkFailed;
        }

        var oobCode = PasswordResetService.ExtractOobCode(firebaseLink);
        if (oobCode is null)
        {
            _logger.LogError(
                "Email verification link from Firebase carried no oobCode for tenant {TenantId}",
                tenantId);
            return EmailVerificationOutcome.LinkFailed;
        }

        var verifyLink = QueryHelpers.AddQueryString(
            portalUrl + "/" + _options.VerifyPath.TrimStart('/'),
            new Dictionary<string, string?>
            {
                ["mode"]    = "verifyEmail",
                ["oobCode"] = oobCode
            });

        // 4. The email.
        try
        {
            await _emailService.SendEmailVerificationEmailAsync(
                new EmailVerificationEmail
                {
                    ToEmail     = item.Email,
                    VerifyLink  = verifyLink,
                    ProductName = _options.ProductName,
                    FromEmail   = settings.GetValueOrDefault(TenantSettingKeys.EmailFromAddress),
                    FromName    = settings.GetValueOrDefault(TenantSettingKeys.EmailFromName),
                    TemplateId  = templateId,
                    CompanyName = settings.GetValueOrDefault(TenantSettingKeys.BrandingCompanyName),
                    LogoUrl     = TenantEmailSettings.ResolveLogoUrl(settings, tenantId, _logger)
                },
                tenantId,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                "Email verification email failed for tenant {TenantId} user {UserId}: {ExceptionType}",
                tenantId, item.UserId, ex.GetType().Name);
            return EmailVerificationOutcome.SendFailed;
        }

        // 5. Audit. The row identifies the user by UserId, never by address; AuditService never throws.
        await _auditService.LogAsync(
            tenantId,
            item.UserId,
            AuditAction,
            severity: "Info",
            entityType: "User",
            entityId: item.UserId.ToString(),
            ipAddress: item.IpAddress,
            requestId: item.TraceId,
            ct: CancellationToken.None).ConfigureAwait(false);

        return EmailVerificationOutcome.Sent;
    }

    /// <summary>
    /// The Portal base URL (no trailing slash) for the tenant: its <c>portal.url</c> setting when that
    /// passes <see cref="PasswordResetService.IsAllowedPortalUrl"/> — the same rule the reset link
    /// obeys, since both carry a live oobCode — otherwise <see cref="EmailVerificationOptions.DefaultPortalUrl"/>.
    /// Null only when the default itself is not an absolute URL.
    /// </summary>
    private string? ResolvePortalUrl(string? tenantPortalUrl, string tenantId)
    {
        if (tenantPortalUrl is not null)
        {
            if (PasswordResetService.IsAllowedPortalUrl(tenantPortalUrl, _options.AllowedPortalOrigins, out var tenantBase))
            {
                return tenantBase;
            }

            _logger.LogWarning(
                "Ignoring TenantSettings {SettingKey} for tenant {TenantId}: not https or not in Cors:AllowedOrigins",
                TenantSettingKeys.PortalUrl, tenantId);
        }

        return Uri.TryCreate(_options.DefaultPortalUrl?.Trim(), UriKind.Absolute, out var fallback)
            ? fallback.GetLeftPart(UriPartial.Path).TrimEnd('/')
            : null;
    }
}
