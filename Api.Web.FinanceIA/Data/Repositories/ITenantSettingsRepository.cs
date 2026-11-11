// Project : Api.Web.FinanceIA
// Created : 2026-08-03

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// Read and write access to per-tenant settings ([dbo].[TenantSettings]). Tenant-isolated.
/// Rows are still seeded by the DBA via SQL at onboarding; <see cref="UpsertAsync"/> is the
/// narrow write path the tenant-profile endpoint needs, not a general settings editor.
/// </summary>
public interface ITenantSettingsRepository
{
    /// <summary>
    /// Returns the raw <c>SettingValue</c> for a (tenant, key), or null when the
    /// tenant has no row for that key. Callers apply the in-code default.
    /// </summary>
    Task<string?> GetValueAsync(string tenantId, string settingKey, CancellationToken ct);

    /// <summary>
    /// Returns the raw <c>SettingValue</c> of a (tenant, key) together with the row's <c>UpdatedAt</c>, or
    /// null when the tenant has no row for that key. For a setting whose screen shows when it was last saved.
    /// </summary>
    Task<TenantSettingEntry?> GetEntryAsync(string tenantId, string settingKey, CancellationToken ct);

    /// <summary>
    /// Returns the raw <c>SettingValue</c> of every one of <paramref name="settingKeys"/> the
    /// tenant has a row for, keyed by <c>SettingKey</c>, in one query. Keys without a row are
    /// simply absent from the result; an empty dictionary means the tenant has none of them.
    /// Callers apply the in-code default per key.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> GetValuesAsync(
        string tenantId,
        IReadOnlyCollection<string> settingKeys,
        CancellationToken ct);

    /// <summary>
    /// Sets the value of one (tenant, key), inserting the row the first time and updating it
    /// afterwards. <c>UQ_TenantSettings_Tenant_Key</c> allows exactly one row per pair, so a
    /// blind INSERT would be rejected on the second write.
    ///
    /// <para>
    /// PROPAGATES failures, unlike every read on this interface. A caller told a setting was
    /// saved must not be told that by a write that failed.
    /// </para>
    ///
    /// <para>
    /// <c>Description</c> is deliberately NOT written: it carries operator prose from the DBA
    /// seed (see docs/guides/tenant-profile.md) and an update must not blank it.
    /// </para>
    /// </summary>
    Task UpsertAsync(string tenantId, string settingKey, string settingValue, CancellationToken ct);

    /// <summary>
    /// Sets several (tenant, key) values in ONE SaveChanges — so they change together or not at all: a
    /// non-null value is inserted or updated as <see cref="UpsertAsync"/> does, a null value deletes the
    /// row (a no-op when there is none). Used where settings only make sense as a set, e.g. an exchange
    /// rate and the date it is stated at.
    ///
    /// <para>
    /// PROPAGATES failures, like <see cref="UpsertAsync"/>. <c>Description</c> is not written.
    /// </para>
    /// </summary>
    Task SetValuesAsync(string tenantId, IReadOnlyDictionary<string, string?> values, CancellationToken ct);
}
