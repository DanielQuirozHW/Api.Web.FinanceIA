// Project : Api.Web.ServiciosFinancieros
// Created : 2026-04-27
// Modified: 2026-04-27

using Api.Web.ServiciosFinancieros.Models;

namespace Api.Web.ServiciosFinancieros.Servicios;

/// <summary>
/// Resolves the Dynamics 365 identity context for an authenticated Firebase user.
/// </summary>
public interface IUserContextResolver
{
    /// <summary>
    /// Resolves the <see cref="UserContext"/> for the given Firebase UID by querying Firestore.
    /// </summary>
    /// <param name="uid">The Firebase UID of the authenticated user.</param>
    /// <param name="tenantContext">The resolved tenant context used to select the correct Firestore project.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>
    /// A <see cref="UserContext"/> when the user is found in Firestore; <c>null</c> otherwise.
    /// </returns>
    Task<UserContext?> ResolveAsync(
        string uid,
        TenantContext tenantContext,
        CancellationToken cancellationToken);
}
