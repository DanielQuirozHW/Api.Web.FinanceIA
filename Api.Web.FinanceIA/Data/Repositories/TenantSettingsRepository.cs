// Project : Api.Web.FinanceIA
// Created : 2026-08-03

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="ITenantSettingsRepository"/>.
/// Reads use AsNoTracking; the write path tracks deliberately. Tenant-isolated by
/// (TenantId, SettingKey).
/// </summary>
public sealed class TenantSettingsRepository : ITenantSettingsRepository
{
    private readonly FinanciaDbContext                 _dbContext;
    private readonly ILogger<TenantSettingsRepository> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="TenantSettingsRepository"/>.
    /// </summary>
    public TenantSettingsRepository(FinanciaDbContext dbContext, ILogger<TenantSettingsRepository> logger)
    {
        _dbContext = dbContext;
        _logger    = logger;
    }

    /// <inheritdoc />
    public async Task<string?> GetValueAsync(string tenantId, string settingKey, CancellationToken ct)
    {
        return await _dbContext.TenantSettings
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.SettingKey == settingKey)
            .Select(s => s.SettingValue)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TenantSettingEntry?> GetEntryAsync(string tenantId, string settingKey, CancellationToken ct)
    {
        return await _dbContext.TenantSettings
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.SettingKey == settingKey)
            .Select(s => new TenantSettingEntry(s.SettingValue, s.UpdatedAt))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, string>> GetValuesAsync(
        string tenantId,
        IReadOnlyCollection<string> settingKeys,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settingKeys);

        if (settingKeys.Count == 0)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        // Contains() on the key list translates to a single IN (...) predicate, so the four
        // profile keys cost one round trip instead of four. The TenantId predicate is the only
        // tenant isolation there is — no global query filter exists in this project.
        var rows = await _dbContext.TenantSettings
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && settingKeys.Contains(s.SettingKey))
            .Select(s => new { s.SettingKey, s.SettingValue })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // UQ_TenantSettings_Tenant_Key guarantees one row per key, so a plain indexer assignment
        // cannot lose anything; it is still an assignment rather than Add so a duplicate could
        // never throw here.
        var result = new Dictionary<string, string>(rows.Count, StringComparer.Ordinal);
        foreach (var row in rows)
            result[row.SettingKey] = row.SettingValue;

        return result;
    }

    /// <inheritdoc />
    public async Task UpsertAsync(string tenantId, string settingKey, string settingValue, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(settingKey);
        ArgumentNullException.ThrowIfNull(settingValue);

        // TRACKED on purpose: the two reads above are AsNoTracking, so neither can be reused here
        // to obtain an entity EF will write back. Same reasoning as
        // TenantBillingConfigRepository.UpsertAsync.
        var existing = await _dbContext.TenantSettings
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.SettingKey == settingKey, ct)
            .ConfigureAwait(false);

        var isInsert = existing is null;
        var now      = DateTime.UtcNow;

        if (existing is null)
        {
            // SettingId is left unset: the property is ValueGeneratedOnAdd, so EF omits the column
            // and the store's DF_TenantSettings_SettingId (NEWSEQUENTIALID()) assigns it.
            // Description is left null — only the DBA seed writes prose there.
            _dbContext.TenantSettings.Add(new TenantSettings
            {
                TenantId     = tenantId,
                SettingKey   = settingKey,
                SettingValue = settingValue,
                UpdatedAt    = now
            });
        }
        else
        {
            existing.SettingValue = settingValue;

            // Assigned explicitly. DF_TenantSettings_UpdatedAt fires on INSERT only, so an UPDATE
            // that left this alone would keep the row's original timestamp.
            existing.UpdatedAt = now;

            // Description is deliberately untouched: it holds the operator's Spanish prose from the
            // DBA seed, and an edit through this path must not blank it.
        }

        // FinanciaDbContext disables AutoDetectChangesEnabled, so a mutated tracked entity is NOT
        // picked up on its own: without this the UPDATE branch above saves nothing and reports
        // success. Same call, for the same reason, as TenantBillingConfigRepository.UpsertAsync.
        _dbContext.ChangeTracker.DetectChanges();

        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Tenant setting {SettingKey} {Operation} for tenant {TenantId}.",
            settingKey,
            isInsert ? "inserted" : "updated",
            tenantId);
    }

    /// <inheritdoc />
    public async Task SetValuesAsync(string tenantId, IReadOnlyDictionary<string, string?> values, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count == 0)
            return;

        var keys = values.Keys.ToList();

        // TRACKED on purpose, as in UpsertAsync: these are the entities EF writes back or deletes.
        var existing = await _dbContext.TenantSettings
            .Where(s => s.TenantId == tenantId && keys.Contains(s.SettingKey))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var byKey = existing.ToDictionary(s => s.SettingKey, StringComparer.Ordinal);
        var now   = DateTime.UtcNow;

        foreach (var (key, value) in values)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            byKey.TryGetValue(key, out var row);

            if (value is null)
            {
                if (row is not null)
                    _dbContext.TenantSettings.Remove(row);
            }
            else if (row is null)
            {
                // SettingId and Description left unset — see UpsertAsync.
                _dbContext.TenantSettings.Add(new TenantSettings
                {
                    TenantId     = tenantId,
                    SettingKey   = key,
                    SettingValue = value,
                    UpdatedAt    = now
                });
            }
            else
            {
                row.SettingValue = value;
                row.UpdatedAt    = now;
            }
        }

        // AutoDetectChangesEnabled is off on FinanciaDbContext — see UpsertAsync.
        _dbContext.ChangeTracker.DetectChanges();

        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Tenant settings {SettingKeys} set for tenant {TenantId} ({ClearedCount} cleared).",
            string.Join(",", keys),
            tenantId,
            values.Count(v => v.Value is null));
    }
}
