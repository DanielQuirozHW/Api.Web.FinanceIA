// Project : Api.Web.FinanceIA
// Created : 2026-04-15
// Modified: 2026-04-15

using Api.Web.FinanceIA.Models;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// Repository for reading <see cref="TenantFirebaseConfig"/> records
/// from [dbo].[TenantFirebaseConfig].
/// </summary>
public interface ITenantFirebaseConfigRepository
{
    /// <summary>
    /// Returns the active Firebase configuration for the given tenant identifier,
    /// or <see langword="null"/> if not found or inactive.
    /// </summary>
    Task<TenantFirebaseConfig?> GetByClienteAsync(
        string cliente,
        CancellationToken cancellationToken);
}
