// Project : Api.Web.FinanceIA
// Created : 2025-01-01
// Modified: 2025-01-01

namespace Api.Web.FinanceIA.Models;

/// <summary>
/// Maps to [dbo].[TenantFirebaseConfig] table in Azure SQL Server.
/// Contains Firebase project configuration per tenant.
/// CredentialJson and RecaptchaSecretKey are stored encrypted
/// with AES-256 in the database and decrypted at runtime.
/// Managed externally — never modify schema via EF Core.
/// </summary>
public sealed class TenantFirebaseConfig
{
    /// <summary>
    /// Primary key. Maps to [Id] column.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Internal tenant identifier.
    /// Maps to [Cliente] column.
    /// Matches Credenciales.cliente and X-Tenant-ID header value.
    /// </summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Firebase project identifier.
    /// Maps to [ProjectId] column.
    /// Example: my-project-12345
    /// Used to initialize the named FirebaseApp instance.
    /// </summary>
    public string ProjectId { get; set; } = string.Empty;

    /// <summary>
    /// Firebase service account JSON content.
    /// Maps to [CredentialJson] column.
    /// Stored encrypted with AES-256 in DB.
    /// Decrypted at runtime by EncryptionService.
    /// Never log or expose this value.
    /// </summary>
    public string CredentialJson { get; set; } = string.Empty;

    /// <summary>
    /// reCAPTCHA V3 secret key for this tenant.
    /// Maps to [RecaptchaSecretKey] column.
    /// Stored encrypted with AES-256 in DB.
    /// Decrypted at runtime by EncryptionService.
    /// Never log or expose this value.
    /// </summary>
    public string RecaptchaSecretKey { get; set; } = string.Empty;

    /// <summary>
    /// Firebase Web API key for this tenant's project.
    /// Maps to [WebApiKey] column. Nullable — a tenant without one simply cannot
    /// use the backup-code login endpoint, which fails closed rather than degrading.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT encrypted, unlike CredentialJson and RecaptchaSecretKey above.
    /// A Firebase Web API key is public by design — it is compiled into every browser
    /// bundle (the Portal ships it as NEXT_PUBLIC_FIREBASE_API_KEY) and identifies the
    /// project rather than authorising anything on its own. Encrypting it would imply a
    /// secrecy it does not have and cost a decrypt on every tenant resolution.
    /// Used only to call Identity Platform's accounts:signInWithPassword, which is how
    /// the BFF re-proves a user's first factor — see IIdentityPlatformSignInService.
    /// </remarks>
    public string? WebApiKey { get; set; }

    /// <summary>
    /// Soft delete flag.
    /// Maps to [IsActive] column.
    /// Only active tenants (IsActive = true) are resolved
    /// by TenantResolutionMiddleware.
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// Record creation timestamp in UTC.
    /// Maps to [CreatedAt] column.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Record last update timestamp in UTC.
    /// Maps to [UpdatedAt] column.
    /// </summary>
    public DateTime UpdatedAt { get; set; }
}