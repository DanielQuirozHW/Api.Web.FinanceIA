// Project : Api.Web.FinanceIA
// Created : 2026-09-04

using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Default implementation of <see cref="ITenantProfileService"/>.
/// Registered Scoped, so a single instance lives for the duration of one request; the private
/// dictionary therefore acts as a per-request read cache with no extra infrastructure, matching
/// <see cref="TenantSettingsService"/> and <see cref="TenantBillingConfigService"/>. Cross-request
/// caching is deliberately avoided: rows are written by the DBA and by
/// <see cref="SetEntityTypeAsync"/>, and nothing could invalidate a longer-lived cache.
///
/// <para>
/// It reads through <see cref="ITenantSettingsRepository"/> directly rather than through
/// <see cref="ITenantSettingsService"/>, because that service's read swallows failures into the
/// same null an absent row produces — which would make <see cref="TenantProfileSource.ReadFailed"/>
/// unobservable. The fault isolation is the same; it just happens here, where the outcome can
/// still be told apart.
/// </para>
/// </summary>
public sealed class TenantProfileService : ITenantProfileService
{
    /// <summary>The four keys the profile is assembled from, fetched in one query.</summary>
    private static readonly string[] ProfileKeys =
    [
        TenantSettingKeys.EntityType,
        TenantSettingKeys.IntegrationLufeEnabled,
        TenantSettingKeys.IntegrationCasfogEnabled,
        TenantSettingKeys.IntegrationNosisEnabled
    ];

    private readonly ITenantSettingsRepository     _repository;
    private readonly ILogger<TenantProfileService> _logger;

    // Per-request memoization keyed by tenant id. The resolution is cached even when it is the
    // default or a degraded read, so an unconfigured tenant is not re-queried within a request
    // and a failing one does not retry the same broken read on every consumer.
    private readonly Dictionary<string, TenantProfileResolution> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of <see cref="TenantProfileService"/>.
    /// </summary>
    public TenantProfileService(
        ITenantSettingsRepository repository,
        ILogger<TenantProfileService> logger)
    {
        _repository = repository;
        _logger     = logger;
    }

    /// <inheritdoc />
    public async Task<TenantProfileResolution> GetEffectiveAsync(string tenantId, CancellationToken ct)
    {
        if (_cache.TryGetValue(tenantId, out var cached))
            return cached;

        IReadOnlyDictionary<string, string> rows;

        try
        {
            rows = await _repository.GetValuesAsync(tenantId, ProfileKeys, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Resolving the profile must never take down the caller — it sits on the
            // authentication bootstrap and, later, on the analysis flow. A failed read still
            // yields a usable profile, but one marked as degraded so a consumer does not switch an
            // integration OFF on the strength of a database blip.
            _logger.LogWarning(
                ex,
                "Failed to read tenant profile settings for tenant {TenantId}; returning the {EntityType} preset marked as degraded.",
                tenantId,
                TenantProfilePresets.Default.EntityType);

            var degraded = new TenantProfileResolution(TenantProfilePresets.Default, TenantProfileSource.ReadFailed);
            _cache[tenantId] = degraded;
            return degraded;
        }

        var resolution = rows.Count == 0
            ? new TenantProfileResolution(TenantProfilePresets.Default, TenantProfileSource.NoConfiguration)
            : new TenantProfileResolution(Overlay(tenantId, rows), TenantProfileSource.Configured);

        _cache[tenantId] = resolution;
        return resolution;
    }

    /// <inheritdoc />
    public async Task<TenantProfileResolution> SetEntityTypeAsync(
        string tenantId,
        string entityType,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        // TenantProfilePresets.Find is the read path's own lookup: trimmed, case-insensitive, null
        // for anything unknown. Validating through it means the endpoint can never store a value
        // that ResolvePreset would later have to fall back on with a Warning.
        var preset = TenantProfilePresets.Find(entityType)
            ?? throw new ArgumentException(
                $"'{TenantSettingKeys.EntityType}' must be " +
                $"'{TenantSettingKeys.EntityTypeSgr}' or '{TenantSettingKeys.EntityTypeNonSgr}'.",
                nameof(entityType));

        // No try/catch on purpose. GetEffectiveAsync above swallows read failures because the
        // profile feeds flows that must fail open; a write must do the opposite and let the caller
        // turn the failure into a 500, rather than report a profile change that did not happen.
        // The preset's own EntityType is stored, not the caller's string, so casing and surrounding
        // whitespace are normalised on the way in.
        await _repository
            .UpsertAsync(tenantId, TenantSettingKeys.EntityType, preset.EntityType, ct)
            .ConfigureAwait(false);

        // Drop the memoized resolution: this instance lives for the whole request, so the
        // GetEffectiveAsync below — and any later consumer in the same request — would otherwise
        // return the pre-write profile.
        _cache.Remove(tenantId);

        _logger.LogInformation(
            "Tenant profile entity type set to {EntityType} for tenant {TenantId}.",
            preset.EntityType,
            tenantId);

        // Re-resolved rather than assembled from the preset: the tenant may carry
        // integration.*.enabled rows that override it, and the caller must see what is actually in
        // force. A read failure here still cannot throw, so it surfaces as ReadFailed — the write
        // itself already succeeded.
        return await GetEffectiveAsync(tenantId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the effective profile: the preset for the stored entity type, with each
    /// <c>integration.*.enabled</c> row the tenant has overriding that preset's flag.
    /// </summary>
    private TenantProfile Overlay(string tenantId, IReadOnlyDictionary<string, string> rows)
    {
        var preset = ResolvePreset(tenantId, rows);

        return new TenantProfile(
            preset.EntityType,
            LufeEnabled:   ResolveFlag(tenantId, rows, TenantSettingKeys.IntegrationLufeEnabled,   preset.LufeEnabled),
            CasfogEnabled: ResolveFlag(tenantId, rows, TenantSettingKeys.IntegrationCasfogEnabled, preset.CasfogEnabled),
            NosisEnabled:  ResolveFlag(tenantId, rows, TenantSettingKeys.IntegrationNosisEnabled,  preset.NosisEnabled));
    }

    private TenantProfile ResolvePreset(string tenantId, IReadOnlyDictionary<string, string> rows)
    {
        if (!rows.TryGetValue(TenantSettingKeys.EntityType, out var storedType))
            return TenantProfilePresets.Default;

        var preset = TenantProfilePresets.Find(storedType);
        if (preset is not null)
            return preset;

        // A typo or a stale value in a DBA-seeded row must not break a request; it resolves to
        // the default preset, loudly enough that someone finds the row. The stored value itself
        // is not logged — it is operator input of unknown shape.
        _logger.LogWarning(
            "Tenant {TenantId} has an unrecognised {SettingKey} value; falling back to the {EntityType} preset.",
            tenantId,
            TenantSettingKeys.EntityType,
            TenantProfilePresets.Default.EntityType);

        return TenantProfilePresets.Default;
    }

    private bool ResolveFlag(
        string tenantId,
        IReadOnlyDictionary<string, string> rows,
        string key,
        bool presetValue)
    {
        if (!rows.TryGetValue(key, out var stored))
            return presetValue;

        if (bool.TryParse(stored.Trim(), out var parsed))
            return parsed;

        _logger.LogWarning(
            "Tenant {TenantId} has a non-boolean {SettingKey} value; falling back to the preset value {PresetValue}.",
            tenantId,
            key,
            presetValue);

        return presetValue;
    }
}
