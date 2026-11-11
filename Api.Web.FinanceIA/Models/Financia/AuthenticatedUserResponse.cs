// Project : Api.Web.FinanceIA
// Created : 2026-05-03
// Modified: 2026-10-26

using System.Text.Json.Serialization;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Unified response shape returned by /me and /register-invited.
/// Contains the full identity context needed by the frontend to bootstrap the session.
/// The internal JWT is also returned as an httpOnly cookie named <c>bff_session</c>.
/// </summary>
public sealed record AuthenticatedUserResponse
{
    /// <summary>Unique identifier of the user record in FinanciaDbContext.</summary>
    public Guid UserId { get; init; }

    /// <summary>Tenant slug stored in FinanciaDbContext.Users.TenantId.</summary>
    public string TenantId { get; init; } = string.Empty;

    /// <summary>Tenant string slug from the X-Tenant-ID header (TenantContext.TenantId).</summary>
    public string TenantCode { get; init; } = string.Empty;

    /// <summary>User email address, stored and returned in lowercase.</summary>
    public string Email { get; init; } = string.Empty;

    /// <summary>Display name shown in the UI.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Application role assigned to the user.</summary>
    public string Role { get; init; } = string.Empty;

    /// <summary>Firebase UID from the identity provider.</summary>
    public string FirebaseUid { get; init; } = string.Empty;

    /// <summary>
    /// Credit-line display mode for the tenant: <c>"sgr"</c> (default) or <c>"simple"</c>.
    /// Presentation only — the frontend collapses the cupo/operation levels in "simple"
    /// mode; it never changes server-side validation. Named specifically (not a generic
    /// "mode") because more tenant settings will hang off this response over time.
    /// </summary>
    public string CreditLineMode { get; init; } = TenantSettingKeys.CreditLineModeDefault;

    /// <summary>
    /// Display name of the client company (<c>branding.company_name</c>), e.g. <c>"Movil SGR"</c>, or
    /// null when the tenant has none configured. Always written, as <c>null</c> when unset — the
    /// Portal types it <c>string | null</c> and supplies its own fallback.
    /// </summary>
    public string? TenantDisplayName { get; init; }

    /// <summary>
    /// Set by /register-invited only: true when the BFF marked the Firebase email verified (the
    /// invite token proved mailbox control), false when that Firebase call failed. When true the
    /// Portal reloads its Firebase user instead of sending a verification mail. Null — and omitted
    /// from the JSON — on /me, which does not read Firebase.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? EmailVerified { get; init; }
}
