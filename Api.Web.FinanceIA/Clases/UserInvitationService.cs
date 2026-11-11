// Project : Api.Web.FinanceIA
// Created : 2026-05-11
// Modified: 2026-10-26

using System.Security.Cryptography;
using Api.Web.FinanceIA.Clases.Exceptions;
using Api.Web.FinanceIA.Clases.PasswordReset;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Entities;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Settings;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Orchestrates user invitation workflows using existing repositories
/// and the email service. Validation queries that are not in the repositories
/// use the <see cref="FinanciaDbContext"/> directly.
/// </summary>
public sealed class UserInvitationService : IUserInvitationService
{
    /// <summary>
    /// <c>InvitedByName</c> of a first-admin bootstrap invitation in the invitations list: it has no
    /// inviter (DDL 075). The Portal renders this field as is.
    /// </summary>
    public const string BootstrapInviterLabel = "Configuración inicial";

    /// <summary>Inviter name in the email when the inviter is unknown or absent.</summary>
    private const string FallbackInviterName = "Un administrador";

    private const int InvitationLifetimeDays = 7;

    /// <summary>Sender, template and branding of the invitation email, each optional per tenant.</summary>
    private static readonly string[] EmailSettingKeys =
    [
        TenantSettingKeys.EmailFromAddress,
        TenantSettingKeys.EmailFromName,
        TenantSettingKeys.EmailInvitationTemplateId,
        .. TenantEmailSettings.BrandingKeys
    ];

    private readonly FinanciaDbContext _dbContext;
    private readonly IUserInvitationRepository _invitationRepository;
    private readonly IUserRepository _userRepository;
    private readonly ITenantSettingsRepository _tenantSettingsRepository;
    private readonly IEmailService _emailService;
    private readonly IAuditService _auditService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<UserInvitationService> _logger;

    public UserInvitationService(
        FinanciaDbContext dbContext,
        IUserInvitationRepository invitationRepository,
        IUserRepository userRepository,
        ITenantSettingsRepository tenantSettingsRepository,
        IEmailService emailService,
        IAuditService auditService,
        IConfiguration configuration,
        ILogger<UserInvitationService> logger)
    {
        _dbContext = dbContext;
        _invitationRepository = invitationRepository;
        _userRepository = userRepository;
        _tenantSettingsRepository = tenantSettingsRepository;
        _emailService = emailService;
        _auditService = auditService;
        _configuration = configuration;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<InviteUserResponse> InviteUserAsync(
        string tenantId,
        Guid invitedByUserId,
        InviteUserSettingsRequest request,
        string? ipAddress,
        string? requestId,
        CancellationToken ct)
    {
        var normalizedEmail = NormalizeEmail(request.Email);

        await EnsureNoUserWithEmailAsync(tenantId, normalizedEmail, ct).ConfigureAwait(false);

        // Only a LIVE invitation of THIS tenant blocks: a revoked one must not, or the address could
        // never be invited again; and another tenant's must not, or it would block this tenant and
        // reveal that the other tenant invited the address.
        var now = DateTime.UtcNow;
        var pendingInvitation = await _dbContext.UserInvitations
            .AsNoTracking()
            .AnyAsync(
                i => i.TenantId == tenantId
                     && i.Email == normalizedEmail
                     && i.UsedAt == null
                     && i.RevokedAt == null
                     && i.ExpiresAt > now,
                ct)
            .ConfigureAwait(false);

        if (pendingInvitation)
            throw InvitationConflictException.ForPendingInvitation();

        var invitation = await CreateInvitationAsync(
            tenantId, normalizedEmail, request.Role, invitedByUserId, now, ct).ConfigureAwait(false);

        var invitationLink = BuildInvitationLink(GetFrontendUrl(), invitation.Token);

        var inviterName = await ResolveInviterNameAsync(invitedByUserId, ct).ConfigureAwait(false);

        var invitationEmail = await BuildInvitationEmailAsync(
            tenantId, normalizedEmail, invitationLink, inviterName, ct).ConfigureAwait(false);

        // Send invitation email
        await _emailService.SendInvitationEmailAsync(invitationEmail, tenantId, ct).ConfigureAwait(false);

        // Fire-and-forget audit
        await _auditService.LogAsync(
            tenantId: tenantId,
            userId: invitedByUserId,
            action: "User.Invite",
            entityType: "UserInvitation",
            entityId: invitation.InvitationId.ToString(),
            ipAddress: ipAddress,
            requestId: requestId,
            ct: ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Invitation sent to {EmailHash} for role {Role}, TenantId: {TenantId}",
            EmailHasher.Hash(normalizedEmail),
            request.Role,
            tenantId);

        return new InviteUserResponse
        {
            InvitationId = invitation.InvitationId,
            Email = normalizedEmail,
            ExpiresAt = invitation.ExpiresAt,
            InvitationLink = invitationLink
        };
    }

    /// <inheritdoc />
    public async Task<FirstAdminInviteOutcome> InviteFirstAdminAsync(
        string tenantId,
        string email,
        string? ipAddress,
        string? requestId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        var normalizedEmail = NormalizeEmail(email);

        // Read before anything is written: a missing FrontendUrl must not leave an orphan invitation.
        var frontendUrl = GetFrontendUrl();

        // Only an ACTIVE Admin closes the bootstrap — a deactivated one cannot invite anybody.
        var hasActiveAdmin = await _dbContext.Users
            .AsNoTracking()
            .AnyAsync(u => u.TenantId == tenantId && u.Role == Roles.Admin && u.IsActive, ct)
            .ConfigureAwait(false);

        if (hasActiveAdmin)
            return FirstAdminInviteOutcome.AdminExists;

        await EnsureNoUserWithEmailAsync(tenantId, normalizedEmail, ct).ConfigureAwait(false);

        // A live invitation an admin sent blocks, exactly as in InviteUserAsync. One a previous
        // bootstrap call created (no inviter) is replaced, so a lost link can be reissued.
        var now = DateTime.UtcNow;
        var liveInvitations = await _dbContext.UserInvitations
            .AsNoTracking()
            .Where(i => i.TenantId == tenantId
                        && i.Email == normalizedEmail
                        && i.UsedAt == null
                        && i.RevokedAt == null
                        && i.ExpiresAt > now)
            .Select(i => new { i.InvitationId, i.InvitedByUserId })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (liveInvitations.Any(i => i.InvitedByUserId is not null))
            throw InvitationConflictException.ForPendingInvitation();

        foreach (var previous in liveInvitations)
        {
            await _invitationRepository
                .RevokeAsync(previous.InvitationId, revokedByUserId: null, now, ct)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "First-admin invitation {InvitationId} replaced by a new one, TenantId: {TenantId}",
                previous.InvitationId,
                tenantId);
        }

        var invitation = await CreateInvitationAsync(
            tenantId, normalizedEmail, Roles.Admin, invitedByUserId: null, now, ct).ConfigureAwait(false);

        var invitationLink = BuildInvitationLink(frontendUrl, invitation.Token);

        // The invitation exists whatever happens here: the caller gets the link back either way.
        // BuildInvitationEmailAsync never throws (its settings read fails open), so a TenantSettings
        // failure degrades to the global sender and template instead of costing the email.
        var invitationEmail = await BuildInvitationEmailAsync(
            tenantId, normalizedEmail, invitationLink, FallbackInviterName, ct).ConfigureAwait(false);

        bool emailSent;
        try
        {
            await _emailService.SendInvitationEmailAsync(invitationEmail, tenantId, ct).ConfigureAwait(false);
            emailSent = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Type only: a provider message could echo the address or the link.
            _logger.LogWarning(
                "First-admin invitation email failed; invitation {InvitationId} kept. " +
                "TenantId: {TenantId} ExceptionType: {ExceptionType}",
                invitation.InvitationId,
                tenantId,
                ex.GetType().Name);
            emailSent = false;
        }

        await _auditService.LogAsync(
            tenantId: tenantId,
            userId: null,
            action: "Setup.FirstAdminInvited",
            entityType: "UserInvitation",
            entityId: invitation.InvitationId.ToString(),
            ipAddress: ipAddress,
            requestId: requestId,
            ct: ct).ConfigureAwait(false);

        _logger.LogInformation(
            "First-admin invitation {InvitationId} created (email sent: {EmailSent}), TenantId: {TenantId}",
            invitation.InvitationId,
            emailSent,
            tenantId);

        return new FirstAdminInviteOutcome
        {
            AdminAlreadyExists = false,
            Invitation = new FirstAdminInvitationResponse
            {
                InvitationId = invitation.InvitationId,
                Email        = normalizedEmail,
                ExpiresAt    = invitation.ExpiresAt,
                InviteUrl    = invitationLink,
                EmailSent    = emailSent
            }
        };
    }

    /// <inheritdoc />
    public async Task<UserInvitation?> GetInvitationByTokenAsync(
        string token,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        return await _dbContext.UserInvitations
            .AsNoTracking()
            .FirstOrDefaultAsync(
                i => i.Token == token
                     && i.UsedAt == null
                     && i.RevokedAt == null
                     && i.ExpiresAt > now,
                ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InvitationDto>> GetInvitationsAsync(
        string tenantId,
        string? status,
        CancellationToken ct)
    {
        var invitations = await _invitationRepository
            .GetByTenantAsync(tenantId, ct)
            .ConfigureAwait(false);

        if (invitations.Count == 0)
            return [];

        // Resolve inviter display names in one batch (reuses IUserRepository.GetByIdsAsync).
        // A bootstrap invitation has no inviter (DDL 075) and is labelled instead.
        var inviterIds = invitations
            .Where(i => i.InvitedByUserId is not null)
            .Select(i => i.InvitedByUserId!.Value)
            .Distinct();

        var inviters = await _userRepository
            .GetByIdsAsync(inviterIds, ct)
            .ConfigureAwait(false);

        var nameById = inviters.ToDictionary(u => u.UserId, u => u.DisplayName);

        var now = DateTime.UtcNow;

        IEnumerable<InvitationDto> items = invitations
            .Select(i => new InvitationDto
            {
                InvitationId    = i.InvitationId,
                Email           = i.Email,
                Role            = i.Role,
                Status          = InvitationStatusResolver.Resolve(i, now),
                InvitedByUserId = i.InvitedByUserId,
                InvitedByName   = i.InvitedByUserId is Guid inviterId
                    ? (nameById.TryGetValue(inviterId, out var name) ? name : null)
                    : BootstrapInviterLabel,
                CreatedAt       = i.CreatedAt,
                ExpiresAt       = i.ExpiresAt,
                UsedAt          = i.UsedAt,
                RevokedAt       = i.RevokedAt
            });

        // Optional status filter (case-insensitive).
        if (!string.IsNullOrWhiteSpace(status))
        {
            var wanted = status.Trim();
            items = items.Where(i => string.Equals(i.Status, wanted, StringComparison.OrdinalIgnoreCase));
        }

        // Pending first (soonest expiry first), then the rest by CreatedAt desc.
        return items
            .OrderBy(i => i.Status == InvitationStatusResolver.Pending ? 0 : 1)
            .ThenBy(i => i.Status == InvitationStatusResolver.Pending ? i.ExpiresAt : DateTime.MaxValue)
            .ThenByDescending(i => i.CreatedAt)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<InvitationActionResult> ResendInvitationAsync(
        string tenantId,
        Guid invitationId,
        Guid resentByUserId,
        string? ipAddress,
        string? requestId,
        CancellationToken ct)
    {
        var invitation = await _invitationRepository
            .GetByIdAsync(invitationId, tenantId, ct)
            .ConfigureAwait(false);

        if (invitation is null)
            return InvitationActionResult.NotFound;

        var now = DateTime.UtcNow;
        var status = InvitationStatusResolver.Resolve(invitation, now);

        // Cannot resend an invitation that is already accepted or revoked.
        if (status is InvitationStatusResolver.Accepted or InvitationStatusResolver.Revoked)
            return InvitationActionResult.InvalidState;

        // Rotate token + extend expiry, mirroring the Settings invite path (base64url, +7d).
        var newToken = GenerateSecureToken();
        var newExpiresAt = now.AddDays(InvitationLifetimeDays);

        await _invitationRepository
            .RotateTokenAsync(invitationId, newToken, newExpiresAt, ct)
            .ConfigureAwait(false);

        // Rebuild the link and re-send via the same email service used on the initial invite.
        var invitationLink = BuildInvitationLink(GetFrontendUrl(), newToken);

        // Resolve inviter display name — fault-isolated, never blocks the resend.
        var inviterName = await ResolveInviterNameAsync(invitation.InvitedByUserId, ct).ConfigureAwait(false);

        var invitationEmail = await BuildInvitationEmailAsync(
            tenantId, invitation.Email, invitationLink, inviterName, ct).ConfigureAwait(false);

        await _emailService.SendInvitationEmailAsync(invitationEmail, tenantId, ct).ConfigureAwait(false);

        // Fire-and-forget audit (lifecycle event; never blocks the response).
        _ = _auditService.LogAsync(
            tenantId: tenantId,
            userId: resentByUserId,
            action: "UserInvitation.Resent",
            severity: "Info",
            entityType: "UserInvitation",
            entityId: invitationId.ToString(),
            ipAddress: ipAddress,
            requestId: requestId,
            ct: CancellationToken.None);

        _logger.LogInformation(
            "Invitation {InvitationId} resent for TenantId: {TenantId}",
            invitationId,
            tenantId);

        return InvitationActionResult.Success;
    }

    /// <inheritdoc />
    public async Task<InvitationActionResult> RevokeInvitationAsync(
        string tenantId,
        Guid invitationId,
        Guid revokedByUserId,
        string? ipAddress,
        string? requestId,
        CancellationToken ct)
    {
        var invitation = await _invitationRepository
            .GetByIdAsync(invitationId, tenantId, ct)
            .ConfigureAwait(false);

        if (invitation is null)
            return InvitationActionResult.NotFound;

        var now = DateTime.UtcNow;
        var status = InvitationStatusResolver.Resolve(invitation, now);

        // Cannot revoke an accepted invitation — the user already exists.
        if (status == InvitationStatusResolver.Accepted)
            return InvitationActionResult.InvalidState;

        // Idempotent: already revoked -> success, no-op.
        if (status == InvitationStatusResolver.Revoked)
            return InvitationActionResult.Success;

        await _invitationRepository
            .RevokeAsync(invitationId, revokedByUserId, now, ct)
            .ConfigureAwait(false);

        // Fire-and-forget audit (lifecycle event; never blocks the response).
        _ = _auditService.LogAsync(
            tenantId: tenantId,
            userId: revokedByUserId,
            action: "UserInvitation.Revoked",
            severity: "Info",
            entityType: "UserInvitation",
            entityId: invitationId.ToString(),
            ipAddress: ipAddress,
            requestId: requestId,
            ct: CancellationToken.None);

        _logger.LogInformation(
            "Invitation {InvitationId} revoked by {UserId} for TenantId: {TenantId}",
            invitationId,
            revokedByUserId,
            tenantId);

        return InvitationActionResult.Success;
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    private static string BuildInvitationLink(string frontendUrl, string token)
        => $"{frontendUrl}/register/invite?token={token}";

    private string GetFrontendUrl()
        => _configuration["AppSettings:FrontendUrl"]
           ?? throw new InvalidOperationException("AppSettings:FrontendUrl not configured");

    /// <summary>Throws the user-exists conflict when the address already has a user in the tenant.</summary>
    private async Task EnsureNoUserWithEmailAsync(string tenantId, string normalizedEmail, CancellationToken ct)
    {
        var existingUser = await _dbContext.Users
            .AsNoTracking()
            .AnyAsync(u => u.Email == normalizedEmail && u.TenantId == tenantId, ct)
            .ConfigureAwait(false);

        if (existingUser)
            throw InvitationConflictException.ForExistingUser();
    }

    /// <summary>
    /// Inserts a pending invitation with a fresh token and the standard expiry. A null
    /// <paramref name="invitedByUserId"/> marks a first-admin bootstrap invitation.
    /// </summary>
    private async Task<UserInvitation> CreateInvitationAsync(
        string tenantId,
        string normalizedEmail,
        string role,
        Guid? invitedByUserId,
        DateTime now,
        CancellationToken ct)
    {
        var invitation = new UserInvitation
        {
            InvitationId = Guid.NewGuid(),
            TenantId = tenantId,
            Email = normalizedEmail,
            Role = role,
            InvitedByUserId = invitedByUserId,
            Token = GenerateSecureToken(),
            ExpiresAt = now.AddDays(InvitationLifetimeDays),
            UsedAt = null,
            CreatedAt = now
        };

        await _invitationRepository.CreateAsync(invitation, ct).ConfigureAwait(false);

        return invitation;
    }

    /// <summary>
    /// The inviter's display name for the email. Fault-isolated: never blocks the invitation, and a
    /// missing inviter (bootstrap) or an unresolvable one falls back to a neutral name.
    /// </summary>
    private async Task<string> ResolveInviterNameAsync(Guid? invitedByUserId, CancellationToken ct)
    {
        if (invitedByUserId is not Guid inviterId)
            return FallbackInviterName;

        try
        {
            var inviter = await _userRepository.GetByUserIdAsync(inviterId, ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(inviter?.DisplayName)
                ? FallbackInviterName
                : inviter.DisplayName;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not resolve inviter DisplayName for UserId {UserId}. Using fallback.",
                inviterId);
            return FallbackInviterName;
        }
    }

    /// <summary>
    /// The invitation email with the tenant's sender, template and branding where it has them
    /// configured, null (= the global SendGrid configuration, no branding) where it does not.
    /// Never throws: the settings read fails open, like every TenantSettings read.
    /// </summary>
    private async Task<InvitationEmail> BuildInvitationEmailAsync(
        string tenantId,
        string toEmail,
        string invitationLink,
        string inviterName,
        CancellationToken ct)
    {
        var settings = await TenantEmailSettings
            .ReadAsync(_tenantSettingsRepository, tenantId, EmailSettingKeys, "invitation", _logger, ct)
            .ConfigureAwait(false);

        return new InvitationEmail
        {
            ToEmail        = toEmail,
            InvitationLink = invitationLink,
            InviterName    = inviterName,
            FromEmail      = settings.GetValueOrDefault(TenantSettingKeys.EmailFromAddress),
            FromName       = settings.GetValueOrDefault(TenantSettingKeys.EmailFromName),
            TemplateId     = settings.GetValueOrDefault(TenantSettingKeys.EmailInvitationTemplateId),
            CompanyName    = settings.GetValueOrDefault(TenantSettingKeys.BrandingCompanyName),
            LogoUrl        = TenantEmailSettings.ResolveLogoUrl(settings, tenantId, _logger)
        };
    }

    private static string GenerateSecureToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }
}
