// Project : Api.Web.ServiciosFinancieros
// Created : 2026-04-15
// Modified: 2026-04-15

using Api.Web.ServiciosFinancieros.Models;

namespace Api.Web.ServiciosFinancieros.Data.Repositories;

/// <summary>
/// Repository for reading <see cref="TenantCredencial"/> records
/// from [dbo].[Credenciales].
/// </summary>
public interface ICredencialRepository
{
    /// <summary>
    /// Returns the credential record matching the given tenant identifier,
    /// or <see langword="null"/> if not found.
    /// </summary>
    Task<TenantCredencial?> GetByClienteAsync(
        string cliente,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the credential record matching the given primary key,
    /// or <see langword="null"/> if not found.
    /// </summary>
    Task<TenantCredencial?> GetByIdAsync(
        int id,
        CancellationToken cancellationToken);
}
