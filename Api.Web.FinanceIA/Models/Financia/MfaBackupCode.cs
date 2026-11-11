// Project : Api.Web.FinanceIA
// Created : 2026-09-03

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Maps to [dbo].[MfaBackupCodes] in the FinancIA BUSINESS database (FinanciaDbContext,
/// ConnectionStrings:FinanciaDefault). One row per single-use MFA recovery code: the recovery
/// path for a user who enrolled a TOTP factor in Firebase Identity Platform and then lost the
/// authenticator.
/// Managed externally — never modify schema via EF Core.
/// </summary>
/// <remarks>
/// <para>
/// It lives beside <see cref="User"/> in the business database, not in the shared "Default"
/// auth/config database, because a backup code is FinancIA BUSINESS data belonging to one
/// FinancIA user — the very row it now points at through <see cref="UserId"/>
/// (FK_MfaBackupCodes_Users). That foreign key is only possible because both tables are in the
/// same database; the earlier design keyed the codes by the global Firebase uid precisely
/// because it could not have one.
/// </para>
/// <para>
/// <see cref="TenantId"/> is denormalised from the owning <see cref="User"/> row — never from the
/// request's X-Tenant-ID header — so it can never disagree with the code's owner, and every query
/// stays tenant-scoped as every FinanciaDbContext query must be. There is no Firebase uid column:
/// [dbo].[Users] already carries a unique one (UQ_Users_FirebaseUid), so a uid resolves to a
/// UserId in one indexed lookup, and a second copy here could only drift.
/// </para>
/// <para>
/// Firebase Identity Platform owns the TOTP factor itself but exposes NO backup-code API, so
/// issuing, hashing and validating them is this BFF's job.
/// </para>
/// <para>
/// <see cref="CodeHash"/> holds a salted PBKDF2 hash and NEVER the plaintext. The plaintext
/// exists exactly once, in the response to the generation call, and is unrecoverable
/// afterwards — losing it means generating a fresh set.
/// </para>
/// </remarks>
public sealed class MfaBackupCode
{
    /// <summary>
    /// Primary key. Maps to [BackupCodeId] column.
    /// </summary>
    public int BackupCodeId { get; set; }

    /// <summary>
    /// Tenant that owns this code. Maps to [TenantId] column.
    /// An NVARCHAR(100) Firebase tenant SLUG (per DDL 005), never a GUID, always copied from the
    /// owning <see cref="User"/> row rather than read from the request header.
    /// </summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// The FinancIA user the code belongs to. Maps to [UserId] column.
    /// FK -> [dbo].[Users].[UserId]. Always resolved from the authenticated principal's uid (or,
    /// for redemption, from the email whose password Identity Platform just verified) — never
    /// taken from a request body.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Salted PBKDF2-HMAC-SHA256 hash of the normalised code.
    /// Maps to [CodeHash] column.
    /// Format: "pbkdf2-sha256${iterations}${base64 salt}${base64 hash}".
    /// NEVER the plaintext code, and never logged.
    /// </summary>
    public string CodeHash { get; set; } = string.Empty;

    /// <summary>
    /// When the code was issued, in UTC. Maps to [CreatedAt] column.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the code was spent, in UTC, or null while it is still available.
    /// Maps to [UsedAt] column.
    /// This column is what makes a code single-use: every lookup filters on
    /// <c>UsedAt IS NULL</c>, and validation stamps it in the same request that accepts it.
    /// </summary>
    public DateTime? UsedAt { get; set; }

    /// <summary>
    /// Client IP that spent the code, or null while it is still available.
    /// Maps to [UsedIp] column.
    /// Recorded for the security audit trail — 45 chars to hold an IPv6 literal.
    /// </summary>
    public string? UsedIp { get; set; }
}
