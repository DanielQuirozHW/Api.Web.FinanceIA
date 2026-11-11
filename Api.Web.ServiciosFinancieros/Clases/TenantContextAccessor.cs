// Project : Api.Web.ServiciosFinancieros
// Created : 2026-04-20
// Modified: 2026-04-20

using Api.Web.ServiciosFinancieros.Models;

namespace Api.Web.ServiciosFinancieros.Clases;

/// <summary>
/// Scoped holder for the resolved <see cref="TenantContext"/> of the current HTTP request.
/// Populated by TenantResolutionMiddleware before authentication runs.
/// One instance is created per request via the scoped DI lifetime.
/// </summary>
public sealed class TenantContextAccessor : ITenantContextAccessor
{
    /// <inheritdoc />
    public TenantContext? TenantContext { get; set; }
}
