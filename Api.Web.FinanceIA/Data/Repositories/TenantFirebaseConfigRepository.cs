// Project : Api.Web.FinanceIA
// Created : 2026-04-15
// Modified: 2026-04-15

using Api.Web.FinanceIA.Clases.Exceptions;
using Api.Web.FinanceIA.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="ITenantFirebaseConfigRepository"/>.
/// Only returns active configurations (IsActive == true).
/// CredentialJson and RecaptchaSecretKey are stored AES-256 encrypted —
/// decryption is the responsibility of the calling service.
/// </summary>
public sealed class TenantFirebaseConfigRepository : ITenantFirebaseConfigRepository
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<TenantFirebaseConfigRepository> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="TenantFirebaseConfigRepository"/>.
    /// </summary>
    public TenantFirebaseConfigRepository(
        AppDbContext dbContext,
        ILogger<TenantFirebaseConfigRepository> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TenantFirebaseConfig?> GetByClienteAsync(
        string cliente,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cliente);

        try
        {
            var result = await _dbContext.TenantFirebaseConfigs
                .AsNoTracking()
                .Where(x => x.TenantId == cliente && x.IsActive)
                .Select(x => new TenantFirebaseConfig
                {
                    Id = x.Id,
                    TenantId = x.TenantId,
                    ProjectId = x.ProjectId,
                    CredentialJson = x.CredentialJson,
                    RecaptchaSecretKey = x.RecaptchaSecretKey,
                    WebApiKey = x.WebApiKey,
                    IsActive = x.IsActive,
                    CreatedAt = x.CreatedAt,
                    UpdatedAt = x.UpdatedAt
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (result is null)
            {
                _logger.LogWarning(
                    "TenantFirebaseConfig not found for tenant {Cliente}",
                    cliente);
            }

            return result;
        }
        catch (DbUpdateException ex)
        {
            throw new DatabaseException(ex);
        }
    }
}
