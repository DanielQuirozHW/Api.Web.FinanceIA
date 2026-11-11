// Project : Api.Web.FinanceIA
// Created : 2026-10-19

using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Api.Web.FinanceIA.Clases.PasswordReset;

/// <summary>
/// Default <see cref="IPasswordResetService"/>. Scoped; the worker opens one scope per request.
/// </summary>
/// <remarks>
/// <para>
/// The emailed link is NOT the one Firebase returns. Firebase's link opens its own hosted action
/// page, which would reset the password there and never run the Portal's /recover steps 2–3. So the
/// link is generated (with <c>ActionCodeSettings.Url</c> = the Portal /recover route, which also
/// makes Firebase check that domain is authorised), its <c>oobCode</c> is taken out, and the email
/// carries <c>{portal}/recover?mode=resetPassword&amp;oobCode=…&amp;lang=es-AR</c> — the same query
/// names Firebase uses for a custom action handler, so the Portal reads it as one.
/// </para>
/// <para>
/// Nothing here logs the address or the link. The address appears only as its
/// <see cref="EmailHasher"/> tag; the link is a credential and appears nowhere.
/// </para>
/// </remarks>
public sealed class PasswordResetService : IPasswordResetService
{
    private const string AuditAction = "User.PasswordResetRequested";

    private static readonly string[] SettingKeys =
    [
        TenantSettingKeys.PortalUrl,
        TenantSettingKeys.EmailFromAddress,
        TenantSettingKeys.EmailFromName,
        TenantSettingKeys.EmailPasswordResetTemplateId,
        .. TenantEmailSettings.BrandingKeys
    ];

    private readonly IUserRepository _userRepository;
    private readonly ITenantSettingsRepository _tenantSettingsRepository;
    private readonly IPasswordResetLinkGenerator _linkGenerator;
    private readonly IEmailService _emailService;
    private readonly IAuditService _auditService;
    private readonly PasswordResetOptions _options;
    private readonly ILogger<PasswordResetService> _logger;

    /// <summary>Initializes a new instance of <see cref="PasswordResetService"/>.</summary>
    public PasswordResetService(
        IUserRepository userRepository,
        ITenantSettingsRepository tenantSettingsRepository,
        IPasswordResetLinkGenerator linkGenerator,
        IEmailService emailService,
        IAuditService auditService,
        IOptions<PasswordResetOptions> options,
        ILogger<PasswordResetService> logger)
    {
        _userRepository           = userRepository;
        _tenantSettingsRepository = tenantSettingsRepository;
        _linkGenerator            = linkGenerator;
        _emailService             = emailService;
        _auditService             = auditService;
        _options                  = options.Value;
        _logger                   = logger;
    }

    /// <inheritdoc />
    public async Task<PasswordResetOutcome> ProcessAsync(PasswordResetWorkItem item, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);

        var tenantId  = item.TenantContext.TenantId;
        var emailHash = EmailHasher.Hash(item.Email);

        var outcome = await RunAsync(item, emailHash, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Password reset {Outcome} for tenant {TenantId} email {EmailHash}",
            outcome,
            tenantId,
            emailHash);

        return outcome;
    }

    private async Task<PasswordResetOutcome> RunAsync(
        PasswordResetWorkItem item,
        string emailHash,
        CancellationToken ct)
    {
        var tenantContext = item.TenantContext;
        var tenantId      = tenantContext.TenantId;

        // 1. The user, in THIS tenant. UQ_Users_Email makes the address unique across every tenant,
        //    so the global lookup followed by the tenant check below is exactly a tenant-scoped
        //    query: an address registered under another tenant reads as unknown here.
        User? user;
        try
        {
            user = await _userRepository.GetByEmailAsync(item.Email, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                "Password reset user lookup failed for tenant {TenantId} email {EmailHash}: {ExceptionType}",
                tenantId, emailHash, ex.GetType().Name);
            return PasswordResetOutcome.LinkFailed;
        }

        if (user is null || !string.Equals(user.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
        {
            return PasswordResetOutcome.UnknownEmail;
        }

        if (!user.IsActive)
        {
            return PasswordResetOutcome.InactiveUser;
        }

        // 2. Tenant identity: portal URL, sender, template and branding, each falling back to global config.
        var settings = await TenantEmailSettings
            .ReadAsync(_tenantSettingsRepository, tenantId, SettingKeys, "password reset", _logger, ct)
            .ConfigureAwait(false);

        var portalUrl = ResolvePortalUrl(settings.GetValueOrDefault(TenantSettingKeys.PortalUrl), tenantId);
        if (portalUrl is null)
        {
            _logger.LogError(
                "Password reset for tenant {TenantId} has no usable Portal URL: AppSettings:FrontendUrl is not an absolute URL",
                tenantId);
            return PasswordResetOutcome.LinkFailed;
        }

        var recoverUrl = portalUrl + "/" + _options.RecoverPath.TrimStart('/');

        // 3. The Firebase link, from the tenant's own Firebase app.
        string? firebaseLink;
        try
        {
            firebaseLink = await _linkGenerator
                .GenerateAsync(item.Email, recoverUrl, tenantContext, ct)
                .ConfigureAwait(false);
        }
        // FirebaseServiceException (an SDK failure, already logged with its Firebase reason by the
        // generator) and anything else, e.g. the registry failing to build the tenant's app. Logged
        // here too, by type, so the outcome is traceable to this request's email tag.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                "Password reset link generation failed for tenant {TenantId} email {EmailHash}: {ExceptionType}",
                tenantId, emailHash, ex.GetType().Name);
            return PasswordResetOutcome.LinkFailed;
        }

        if (firebaseLink is null)
        {
            return PasswordResetOutcome.FirebaseUserMissing;
        }

        var oobCode = ExtractOobCode(firebaseLink);
        if (oobCode is null)
        {
            _logger.LogError(
                "Password reset link from Firebase carried no oobCode for tenant {TenantId}",
                tenantId);
            return PasswordResetOutcome.LinkFailed;
        }

        var resetLink = QueryHelpers.AddQueryString(recoverUrl, new Dictionary<string, string?>
        {
            ["mode"]    = "resetPassword",
            ["oobCode"] = oobCode,
            ["lang"]    = "es-AR"
        });

        // 4. The email.
        try
        {
            await _emailService.SendPasswordResetEmailAsync(
                new PasswordResetEmail
                {
                    ToEmail       = item.Email,
                    ResetLink     = resetLink,
                    ProductName   = _options.ProductName,
                    ExpiresInText = _options.LinkExpiryText,
                    FromEmail     = settings.GetValueOrDefault(TenantSettingKeys.EmailFromAddress),
                    FromName      = settings.GetValueOrDefault(TenantSettingKeys.EmailFromName),
                    TemplateId    = settings.GetValueOrDefault(TenantSettingKeys.EmailPasswordResetTemplateId),
                    CompanyName   = settings.GetValueOrDefault(TenantSettingKeys.BrandingCompanyName),
                    LogoUrl       = TenantEmailSettings.ResolveLogoUrl(settings, tenantId, _logger)
                },
                tenantId,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                "Password reset email failed for tenant {TenantId} email {EmailHash}: {ExceptionType}",
                tenantId, emailHash, ex.GetType().Name);
            return PasswordResetOutcome.SendFailed;
        }

        // 5. Audit. The row identifies the user by UserId, never by address; AuditService never throws.
        await _auditService.LogAsync(
            tenantId,
            user.UserId,
            AuditAction,
            severity: "Info",
            entityType: "User",
            entityId: user.UserId.ToString(),
            ipAddress: item.IpAddress,
            requestId: item.TraceId,
            ct: CancellationToken.None).ConfigureAwait(false);

        return PasswordResetOutcome.Sent;
    }

    /// <summary>
    /// The Portal base URL (no trailing slash) for the tenant: its <c>portal.url</c> setting when that
    /// passes <see cref="IsAllowedPortalUrl"/>, otherwise <see cref="PasswordResetOptions.DefaultPortalUrl"/>.
    /// Null only when the default itself is not an absolute URL.
    /// </summary>
    private string? ResolvePortalUrl(string? tenantPortalUrl, string tenantId)
    {
        if (tenantPortalUrl is not null)
        {
            if (IsAllowedPortalUrl(tenantPortalUrl, _options.AllowedPortalOrigins, out var tenantBase))
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

    /// <summary>
    /// True when <paramref name="candidate"/> is an absolute https URL (http only on a loopback
    /// host), with no query or fragment, whose origin is one of <paramref name="allowedOrigins"/>.
    /// The emailed link carries a live oobCode, so a tenant row must never be able to send it to a
    /// host we do not serve.
    /// </summary>
    internal static bool IsAllowedPortalUrl(
        string candidate,
        IReadOnlyCollection<string> allowedOrigins,
        out string normalised)
    {
        normalised = string.Empty;

        if (!Uri.TryCreate(candidate.Trim(), UriKind.Absolute, out var uri))
            return false;

        var isSecure = uri.Scheme == Uri.UriSchemeHttps
                       || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
        if (!isSecure || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.UserInfo))
            return false;

        var origin = uri.GetLeftPart(UriPartial.Authority);
        var isListed = allowedOrigins.Any(allowed =>
            Uri.TryCreate(allowed.Trim(), UriKind.Absolute, out var allowedUri)
            && string.Equals(allowedUri.GetLeftPart(UriPartial.Authority), origin, StringComparison.OrdinalIgnoreCase));
        if (!isListed)
            return false;

        normalised = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return true;
    }

    /// <summary>The <c>oobCode</c> query value of a Firebase action link, or null when absent.</summary>
    internal static string? ExtractOobCode(string firebaseLink)
    {
        if (!Uri.TryCreate(firebaseLink, UriKind.Absolute, out var uri))
            return null;

        var query = QueryHelpers.ParseQuery(uri.Query);
        return query.TryGetValue("oobCode", out var values) && !string.IsNullOrWhiteSpace(values.ToString())
            ? values.ToString()
            : null;
    }
}
