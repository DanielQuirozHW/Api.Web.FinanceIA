// Project : Api.Web.FinanceIA
// Created : 2026-05-02
// Modified: 2026-05-02

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// EF Core entity that maps to [dbo].[Users].
/// Represents a user account within a tenant.
/// </summary>
public sealed class User
{
    /// <summary>Primary key.</summary>
    public Guid UserId { get; set; }

    /// <summary>Tenant that owns this user record. Matches the slug in Credenciales and TenantFirebaseConfig.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Firebase UID from the identity provider.</summary>
    public string FirebaseUid { get; set; } = string.Empty;

    /// <summary>User email address, stored in lowercase.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Display name shown in the UI.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Application role. Allowed values: Analyst, RiskManager, Admin, Auditor, plus SuperAdmin once
    /// DDL 076 has widened CK_Users_Role — all enumerated in <see cref="Api.Web.FinanceIA.Clases.Roles"/>.
    /// SuperAdmin is granted by the DBA only; an invitation can never carry it (CK_UserInvitations_Role).
    /// Self-registration always assigns Analyst.
    /// </summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>Indicates whether the account is active.</summary>
    public bool IsActive { get; set; }

    /// <summary>UTC timestamp of the most recent successful login.</summary>
    public DateTime? LastLoginAt { get; set; }

    /// <summary>Consecutive failed login attempts since last success.</summary>
    public int FailedLoginCount { get; set; }

    /// <summary>UTC timestamp until which the account is locked. Null when not locked.</summary>
    public DateTime? LockedUntil { get; set; }

    /// <summary>When true the user must change their password on next login.</summary>
    public bool MustChangePassword { get; set; }

    /// <summary>Indicates whether two-factor authentication is enabled.</summary>
    public bool TwoFactorEnabled { get; set; }

    /// <summary>UTC timestamp when the record was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>UserId (as string) of the administrator who created this account, or null for self-registration.</summary>
    public string? CreatedBy { get; set; }

    /// <summary>UTC timestamp of the most recent update to this record.</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>JSON blob storing per-user onboarding/coach-mark flags. Null means no flags set (tours shown).</summary>
    public string? OnboardingFlagsJson { get; set; }

    /// <summary>
    /// Team this user belongs to (DDL 063), or null when they are not assigned to one.
    /// </summary>
    /// <remarks>
    /// The null is meaningful and is the default. It is what the decision notifier reads as "no
    /// manager to notify", falling back to the tenant's active RiskManagers — see
    /// <see cref="Api.Web.FinanceIA.Clases.AnalysisDecisionNotifier"/>.
    /// </remarks>
    public Guid? TeamId { get; set; }
}
