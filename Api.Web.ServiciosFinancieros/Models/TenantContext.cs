// Project : Api.Web.ServiciosFinancieros
// Created : 2025-01-01
// Modified: 2025-01-01

namespace Api.Web.ServiciosFinancieros.Models;

/// <summary>
/// Immutable runtime context for the current tenant.
/// Resolved once per request by TenantResolutionMiddleware
/// from the X-Tenant-ID header. Available to all controllers
/// and services via ITenantContextAccessor.
/// All sensitive fields are decrypted in-memory at resolution time.
/// </summary>
public sealed record TenantContext
{
    /// <summary>
    /// Tenant identifier. Value from X-Tenant-ID header.
    /// Maps to Credenciales.cliente in SQL Server.
    /// </summary>
    public required string TenantId { get; init; }

    /// <summary>
    /// Dynamics 365 instance URL.
    /// Maps to Credenciales.url.
    /// Example: https://orgname.crm2.dynamics.com
    /// </summary>
    public required string DynamicsUrl { get; init; }

    /// <summary>
    /// Azure AD App Registration Client ID for Dynamics 365 OAuth.
    /// Maps to Credenciales.clientid.
    /// </summary>
    public required string DynamicsClientId { get; init; }

    /// <summary>
    /// Azure AD App Registration Client Secret for Dynamics 365 OAuth.
    /// Maps to Credenciales.clientsecret.
    /// Decrypted from AES-256 at resolution time.
    /// Never log or persist this value.
    /// </summary>
    public required string DynamicsClientSecret { get; init; }

    /// <summary>
    /// Azure AD Tenant ID (GUID) for Dynamics 365 OAuth.
    /// Maps to Credenciales.tenantid.
    /// Format: xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
    /// </summary>
    public required string AzureAdTenantId { get; init; }

    /// <summary>
    /// Firebase project identifier.
    /// Maps to TenantFirebaseConfig.ProjectId.
    /// Used to resolve the correct FirebaseApp instance.
    /// </summary>
    public required string FirebaseProjectId { get; init; }

    /// <summary>
    /// Firebase service account JSON content.
    /// Maps to TenantFirebaseConfig.CredentialJson.
    /// Decrypted from AES-256 at resolution time.
    /// Never log or persist this value.
    /// </summary>
    public required string FirebaseCredentialJson { get; init; }

    /// <summary>
    /// reCAPTCHA V3 secret key for this tenant.
    /// Maps to TenantFirebaseConfig.RecaptchaSecretKey.
    /// Decrypted from AES-256 at resolution time.
    /// Never log or persist this value.
    /// </summary>
    public required string RecaptchaSecretKey { get; init; }
}