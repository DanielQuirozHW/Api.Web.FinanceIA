// Project : Api.Web.FinanceIA
// Created : 2026-04-15
// Modified: 2026-04-15

using Api.Web.FinanceIA.Clases.Exceptions;
using Api.Web.FinanceIA.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="ICredencialRepository"/>.
/// All queries are read-only (AsNoTracking) and project only
/// the required columns to avoid loading sensitive data unnecessarily.
/// </summary>
public sealed class CredencialRepository : ICredencialRepository
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<CredencialRepository> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="CredencialRepository"/>.
    /// </summary>
    public CredencialRepository(
        AppDbContext dbContext,
        ILogger<CredencialRepository> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TenantCredencial?> GetByClienteAsync(
        string cliente,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cliente);

        try
        {
            var result = await _dbContext.Credenciales
                .AsNoTracking()
                .Where(x => x.Cliente == cliente)
                .Select(x => new TenantCredencial
                {
                    Id = x.Id,
                    Url = x.Url,
                    ClientId = x.ClientId,
                    ClientSecret = x.ClientSecret,
                    AzureAdTenantId = x.AzureAdTenantId,
                    Cliente = x.Cliente
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (result is null)
            {
                _logger.LogWarning(
                    "Credencial not found for tenant {Cliente}",
                    cliente);
            }

            return result;
        }
        catch (DbUpdateException ex)
        {
            throw new DatabaseException(ex);
        }
    }

    /// <inheritdoc />
    public async Task<TenantCredencial?> GetByIdAsync(
        int id,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _dbContext.Credenciales
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

            if (result is null)
            {
                _logger.LogWarning(
                    "Credencial not found for id {Id}",
                    id);
            }

            return result;
        }
        catch (DbUpdateException ex)
        {
            throw new DatabaseException(ex);
        }
    }
}
