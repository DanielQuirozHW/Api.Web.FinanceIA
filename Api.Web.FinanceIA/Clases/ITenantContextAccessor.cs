// Project : Api.Web.FinanceIA
// Created : 2026-04-20
// Modified: 2026-04-20

using Api.Web.FinanceIA.Models;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Provides scoped access to the resolved TenantContext
/// for the current HTTP request. Set by TenantResolutionMiddleware
/// before authentication runs. Use in controllers only —
/// never inject in services.
/// </summary>
public interface ITenantContextAccessor
{
    /// <summary>
    /// The resolved TenantContext for the current request.
    /// Null if TenantResolutionMiddleware has not yet resolved the tenant
    /// or if tenant resolution failed.
    /// </summary>
    TenantContext? TenantContext { get; set; }
}
