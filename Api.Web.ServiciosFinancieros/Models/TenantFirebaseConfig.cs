// Project : Api.Web.ServiciosFinancieros
// Created : 2025-01-01
// Modified: 2025-01-01

namespace Api.Web.ServiciosFinancieros.Models;

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