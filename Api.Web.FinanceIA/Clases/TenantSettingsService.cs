// Project : Api.Web.FinanceIA
// Created : 2026-08-03

using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Default implementation of <see cref="ITenantSettingsService"/>.
/// Registered Scoped, so a single instance lives for the duration of one request;
/// the private dictionary therefore acts as a per-request read cache with no extra
/// infrastructure. Cross-request caching is deliberately avoided: rows are written by
/// the DBA via SQL and by <see cref="ITenantProfileService"/>, and nothing could
/// invalidate a longer-lived cache, so it would risk serving stale settings.
///
/// <para>
/// This service reads only. It does not serve <c>entity.type</c> — that key is resolved
/// by <see cref="ITenantProfileService"/>, which owns both its read and its write — so a
/// profile change cannot leave a stale entry in the dictionary below.
/// </para>
/// </summary>
public sealed class TenantSettingsService : ITenantSettingsService
{
    private readonly ITenantSettingsRepository        _repository;
    private readonly ILogger<TenantSettingsService>   _logger;

    // Per-request memoization keyed by "{tenantId}::{settingKey}". Value may be null
    // (row absent), which is cached too so an absent setting is not re-queried.
    private readonly Dictionary<string, string?> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of <see cref="TenantSettingsService"/>.
    /// </summary>
    public TenantSettingsService(
        ITenantSettingsRepository repository,
        ILogger<TenantSettingsService> logger)
    {
        _repository = repository;
        _logger     = logger;
    }

    /// <inheritdoc />
    public async Task<string> GetStringAsync(string tenantId, string key, string defaultValue, CancellationToken ct)
    {
        var raw = await GetRawAsync(tenantId, key, ct).ConfigureAwait(false);
        return raw ?? defaultValue;
    }

    /// <inheritdoc />
    public async Task<string> GetCreditLineModeAsync(string tenantId, CancellationToken ct)
    {
        var raw = await GetRawAsync(tenantId, TenantSettingKeys.CreditLineMode, ct).ConfigureAwait(false);

        // Accept only known modes; anything else (null, empty, typo, stale value) falls
        // back to the default so a bad DBA-seeded row cannot break the bootstrap path.
        var normalized = raw?.Trim().ToLowerInvariant();
        return normalized switch
        {
            TenantSettingKeys.CreditLineModeSgr    => TenantSettingKeys.CreditLineModeSgr,
            TenantSettingKeys.CreditLineModeSimple => TenantSettingKeys.CreditLineModeSimple,
            _                                      => TenantSettingKeys.CreditLineModeDefault,
        };
    }

    /// <inheritdoc />
    public async Task<string?> GetTenantDisplayNameAsync(string tenantId, CancellationToken ct)
    {
        var raw = await GetRawAsync(tenantId, TenantSettingKeys.BrandingCompanyName, ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    private async Task<string?> GetRawAsync(string tenantId, string key, CancellationToken ct)
    {
        var cacheKey = string.Concat(tenantId, "::", key);
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        // A tenant setting is a presentation-only preference; failing to read it must NEVER
        // break the caller (e.g. authentication in /auth/me). Any exception is logged as a
        // warning and resolves to null — the same sentinel as an absent row — so every GetXxx
        // method falls back to its own in-code default. Guarding here (the single choke point
        // all reads funnel through) means every current and future setting inherits this.
        string? value;
        try
        {
            value = await _repository.GetValueAsync(tenantId, key, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to read tenant setting {SettingKey} for tenant {TenantId}; falling back to the in-code default.",
                key,
                tenantId);
            value = null;
        }

        _cache[cacheKey] = value;
        return value;
    }
}
