// Project : Api.Web.FinanceIA
// Created : 2025-01-01
// Modified: 2025-01-01

namespace Api.Web.FinanceIA.Models;

/// <summary>
/// Maps to [dbo].[Credenciales] table in Azure SQL Server.
/// Master tenant registry. Contains Dynamics 365 OAuth
/// credentials per tenant.
/// Managed externally — never modify schema via EF Core.
/// The Conexion column is obsolete and ignored in EF mapping.
/// </summary>
public sealed class TenantCredencial
{
    /// <summary>
    /// Primary key. Maps to [id] column.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Dynamics 365 instance URL.
    /// Maps to [url] column.
    /// Example: https://orgname.crm2.dynamics.com
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Azure AD App Registration Client ID.
    /// Maps to [clientid] column.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Azure AD App Registration Client Secret.
    /// Maps to [clientsecret] column.
    /// Stored encrypted with AES-256 in DB.
    /// Decrypted at runtime by EncryptionService.
    /// Never log or expose this value.
    /// </summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// Azure AD Tenant ID (GUID).
    /// Maps to [tenantid] column.
    /// Format: xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
    /// </summary>
    public string AzureAdTenantId { get; set; } = string.Empty;

    /// <summary>
    /// Internal tenant identifier.
    /// Maps to [cliente] column.
    /// This is the value matched against X-Tenant-ID header.
    /// </summary>
    public string Cliente { get; set; } = string.Empty;
}