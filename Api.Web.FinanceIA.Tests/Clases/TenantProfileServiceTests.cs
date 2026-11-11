// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-04

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// Covers <see cref="TenantProfileService"/>: the three-step resolution (explicit row, then the
/// preset for the stored entity type, then the SGR default), malformed-row fallback, the
/// never-throws contract with its <see cref="TenantProfileSource.ReadFailed"/> marker, and
/// per-request memoization. Follows the repo's no-mock-library convention: a hand-rolled
/// private sealed stub.
/// </summary>
public sealed class TenantProfileServiceTests
{
    private const string Tenant = "tenant-1";

    private sealed class StubTenantSettingsRepository : ITenantSettingsRepository
    {
        public Dictionary<string, string> Rows { get; } = new(StringComparer.Ordinal);
        public Exception? ThrowOnCall { get; set; }
        public int CallCount { get; private set; }
        public List<string> RequestedTenants { get; } = [];
        public IReadOnlyCollection<string>? LastRequestedKeys { get; private set; }

        // Write-path capture.
        public Exception? ThrowOnUpsert { get; set; }
        public List<(string TenantId, string Key, string Value)> Writes { get; } = [];

        public Task<string?> GetValueAsync(string tenantId, string settingKey, CancellationToken ct)
            => throw new NotSupportedException("TenantProfileService must use the batched read.");

        public Task<Api.Web.FinanceIA.Models.Financia.TenantSettingEntry?> GetEntryAsync(string tenantId, string settingKey, CancellationToken ct)
            => throw new NotSupportedException("TenantProfileService does not read entries.");

        public Task<IReadOnlyDictionary<string, string>> GetValuesAsync(
            string tenantId, IReadOnlyCollection<string> settingKeys, CancellationToken ct)
        {
            CallCount++;
            RequestedTenants.Add(tenantId);
            LastRequestedKeys = settingKeys;

            if (ThrowOnCall is not null)
                return Task.FromException<IReadOnlyDictionary<string, string>>(ThrowOnCall);

            // Honour the contract: only the requested keys come back, and only those with a row.
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var key in settingKeys)
            {
                if (Rows.TryGetValue(key, out var value))
                    result[key] = value;
            }

            return Task.FromResult<IReadOnlyDictionary<string, string>>(result);
        }

        public Task UpsertAsync(string tenantId, string settingKey, string settingValue, CancellationToken ct)
        {
            if (ThrowOnUpsert is not null)
                return Task.FromException(ThrowOnUpsert);

            Writes.Add((tenantId, settingKey, settingValue));

            // Land the write where the reads see it, so a GetEffectiveAsync after a write observes
            // the new value — which is exactly what the cache-eviction test needs to be able to fail.
            Rows[settingKey] = settingValue;
            return Task.CompletedTask;
        }

        public Task SetValuesAsync(string tenantId, IReadOnlyDictionary<string, string?> values, CancellationToken ct)
            => throw new NotSupportedException("TenantProfileService writes one key with UpsertAsync.");
    }

    private static TenantProfileService BuildSut(StubTenantSettingsRepository repo)
        => new(repo, NullLogger<TenantProfileService>.Instance);

    // ── Defaults ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetEffectiveAsync_WhenNoRows_ReturnsSgrPresetMarkedNoConfiguration()
    {
        var sut = BuildSut(new StubTenantSettingsRepository());

        var resolution = await sut.GetEffectiveAsync(Tenant, CancellationToken.None);

        Assert.Same(TenantProfilePresets.Sgr, resolution.Profile);
        Assert.Equal(TenantProfileSource.NoConfiguration, resolution.Source);
        Assert.False(resolution.IsDegraded);
    }

    [Fact]
    public async Task GetEffectiveAsync_NeverReturnsNull()
    {
        var sut = BuildSut(new StubTenantSettingsRepository());

        var resolution = await sut.GetEffectiveAsync(Tenant, CancellationToken.None);

        Assert.NotNull(resolution);
        Assert.NotNull(resolution.Profile);
    }

    [Fact]
    public async Task GetEffectiveAsync_RequestsExactlyTheFourProfileKeys()
    {
        var repo = new StubTenantSettingsRepository();
        var sut  = BuildSut(repo);

        _ = await sut.GetEffectiveAsync(Tenant, CancellationToken.None);

        Assert.NotNull(repo.LastRequestedKeys);
        Assert.Equal(
            new[]
            {
                TenantSettingKeys.EntityType,
                TenantSettingKeys.IntegrationLufeEnabled,
                TenantSettingKeys.IntegrationCasfogEnabled,
                TenantSettingKeys.IntegrationNosisEnabled
            }.Order(StringComparer.Ordinal),
            repo.LastRequestedKeys.Order(StringComparer.Ordinal));
    }

    // ── Entity type selects the preset ──────────────────────────────────────────

    [Fact]
    public async Task GetEffectiveAsync_WhenEntityTypeIsNonSgr_ReturnsNonSgrPresetMarkedConfigured()
    {
        var repo = new StubTenantSettingsRepository();
        repo.Rows[TenantSettingKeys.EntityType] = TenantSettingKeys.EntityTypeNonSgr;

        var resolution = await BuildSut(repo).GetEffectiveAsync(Tenant, CancellationToken.None);

        Assert.Equal(TenantSettingKeys.EntityTypeNonSgr, resolution.Profile.EntityType);
        Assert.True(resolution.Profile.LufeEnabled);
        Assert.False(resolution.Profile.CasfogEnabled);   // meaningless to a non-SGR lender
        Assert.False(resolution.Profile.NosisEnabled);    // billed per query, so off by default
        Assert.Equal(TenantProfileSource.Configured, resolution.Source);
    }

    [Fact]
    public async Task GetEffectiveAsync_WhenEntityTypeIsSgrExplicitly_ReturnsSgrPresetMarkedConfigured()
    {
        var repo = new StubTenantSettingsRepository();
        repo.Rows[TenantSettingKeys.EntityType] = TenantSettingKeys.EntityTypeSgr;

        var resolution = await BuildSut(repo).GetEffectiveAsync(Tenant, CancellationToken.None);

        Assert.Equal(TenantProfilePresets.Sgr, resolution.Profile);
        Assert.Equal(TenantProfileSource.Configured, resolution.Source);
    }

    [Theory]
    [InlineData("  NON_SGR  ")]
    [InlineData("Non_Sgr")]
    public async Task GetEffectiveAsync_NormalisesEntityTypeCaseAndPadding(string stored)
    {
        var repo = new StubTenantSettingsRepository();
        repo.Rows[TenantSettingKeys.EntityType] = stored;

        var resolution = await BuildSut(repo).GetEffectiveAsync(Tenant, CancellationToken.None);

        Assert.Equal(TenantSettingKeys.EntityTypeNonSgr, resolution.Profile.EntityType);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetEffectiveAsync_WhenEntityTypeIsUnknown_FallsBackToSgrPreset(string stored)
    {
        var repo = new StubTenantSettingsRepository();
        repo.Rows[TenantSettingKeys.EntityType] = stored;

        var resolution = await BuildSut(repo).GetEffectiveAsync(Tenant, CancellationToken.None);

        // A bad row is still a row: the source is Configured, but the values are the default's.
        Assert.Equal(TenantProfilePresets.Sgr, resolution.Profile);
        Assert.Equal(TenantProfileSource.Configured, resolution.Source);
    }

    // ── Explicit integration rows override the preset ───────────────────────────

    [Fact]
    public async Task GetEffectiveAsync_ExplicitRowOverridesThePresetForThatKeyOnly()
    {
        var repo = new StubTenantSettingsRepository();
        repo.Rows[TenantSettingKeys.EntityType]               = TenantSettingKeys.EntityTypeNonSgr;
        repo.Rows[TenantSettingKeys.IntegrationCasfogEnabled] = TenantSettingKeys.BoolTrue;

        var resolution = await BuildSut(repo).GetEffectiveAsync(Tenant, CancellationToken.None);

        Assert.Equal(TenantSettingKeys.EntityTypeNonSgr, resolution.Profile.EntityType);
        Assert.True(resolution.Profile.CasfogEnabled);    // overridden back on
        Assert.True(resolution.Profile.LufeEnabled);      // untouched preset value
        Assert.False(resolution.Profile.NosisEnabled);    // the OTHER flag the preset turns off
                                                          // stays off — the override is per key
    }

    [Fact]
    public async Task GetEffectiveAsync_IntegrationRowWithoutEntityTypeOverlaysTheDefaultPreset()
    {
        var repo = new StubTenantSettingsRepository();
        repo.Rows[TenantSettingKeys.IntegrationNosisEnabled] = TenantSettingKeys.BoolFalse;

        var resolution = await BuildSut(repo).GetEffectiveAsync(Tenant, CancellationToken.None);

        Assert.Equal(TenantSettingKeys.EntityTypeSgr, resolution.Profile.EntityType);
        Assert.False(resolution.Profile.NosisEnabled);
        Assert.True(resolution.Profile.LufeEnabled);
        Assert.True(resolution.Profile.CasfogEnabled);
        Assert.Equal(TenantProfileSource.Configured, resolution.Source);
    }

    [Theory]
    [InlineData(" TRUE ", true)]
    [InlineData("False", false)]
    public async Task GetEffectiveAsync_ParsesBooleanRowsLeniently(string stored, bool expected)
    {
        var repo = new StubTenantSettingsRepository();
        repo.Rows[TenantSettingKeys.IntegrationLufeEnabled] = stored;

        var resolution = await BuildSut(repo).GetEffectiveAsync(Tenant, CancellationToken.None);

        Assert.Equal(expected, resolution.Profile.LufeEnabled);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("")]
    public async Task GetEffectiveAsync_WhenBooleanRowIsMalformed_FallsBackToPresetValue(string stored)
    {
        var repo = new StubTenantSettingsRepository();
        repo.Rows[TenantSettingKeys.EntityType]               = TenantSettingKeys.EntityTypeNonSgr;
        repo.Rows[TenantSettingKeys.IntegrationCasfogEnabled] = stored;

        var resolution = await BuildSut(repo).GetEffectiveAsync(Tenant, CancellationToken.None);

        Assert.False(resolution.Profile.CasfogEnabled);   // the non-SGR preset value, not "true"
    }

    // ── Fault isolation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetEffectiveAsync_WhenRepositoryThrows_ReturnsDefaultMarkedReadFailedAndDoesNotPropagate()
    {
        var repo = new StubTenantSettingsRepository
        {
            ThrowOnCall = new InvalidOperationException(
                "The connection does not support MultipleActiveResultSets.")
        };

        var resolution = await BuildSut(repo).GetEffectiveAsync(Tenant, CancellationToken.None);

        Assert.Same(TenantProfilePresets.Default, resolution.Profile);
        Assert.Equal(TenantProfileSource.ReadFailed, resolution.Source);
        Assert.True(resolution.IsDegraded);
    }

    [Fact]
    public async Task GetEffectiveAsync_NoRowsAndFailedRead_ShareProfileButNotSource()
    {
        var healthy = await BuildSut(new StubTenantSettingsRepository())
            .GetEffectiveAsync(Tenant, CancellationToken.None);

        var failed = await BuildSut(new StubTenantSettingsRepository { ThrowOnCall = new TimeoutException() })
            .GetEffectiveAsync(Tenant, CancellationToken.None);

        Assert.Equal(healthy.Profile, failed.Profile);
        Assert.NotEqual(healthy.Source, failed.Source);
        Assert.False(healthy.IsDegraded);
        Assert.True(failed.IsDegraded);
    }

    // ── Per-request memoization ─────────────────────────────────────────────────

    [Fact]
    public async Task GetEffectiveAsync_CalledTwice_QueriesRepositoryOnce()
    {
        var repo = new StubTenantSettingsRepository();
        repo.Rows[TenantSettingKeys.EntityType] = TenantSettingKeys.EntityTypeNonSgr;
        var sut = BuildSut(repo);

        var first  = await sut.GetEffectiveAsync(Tenant, CancellationToken.None);
        var second = await sut.GetEffectiveAsync(Tenant, CancellationToken.None);

        Assert.Equal(1, repo.CallCount);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task GetEffectiveAsync_WhenNoRows_CalledTwice_QueriesRepositoryOnce()
    {
        var repo = new StubTenantSettingsRepository();
        var sut  = BuildSut(repo);

        _ = await sut.GetEffectiveAsync(Tenant, CancellationToken.None);
        _ = await sut.GetEffectiveAsync(Tenant, CancellationToken.None);

        Assert.Equal(1, repo.CallCount);
    }

    [Fact]
    public async Task GetEffectiveAsync_WhenRepositoryThrows_CalledTwice_QueriesRepositoryOnce()
    {
        var repo = new StubTenantSettingsRepository { ThrowOnCall = new TimeoutException() };
        var sut  = BuildSut(repo);

        _ = await sut.GetEffectiveAsync(Tenant, CancellationToken.None);
        _ = await sut.GetEffectiveAsync(Tenant, CancellationToken.None);

        Assert.Equal(1, repo.CallCount);   // a broken read is not retried within the request
    }

    [Fact]
    public async Task GetEffectiveAsync_ForDifferentTenants_QueriesRepositoryPerTenant()
    {
        var repo = new StubTenantSettingsRepository();
        var sut  = BuildSut(repo);

        _ = await sut.GetEffectiveAsync("tenant-a", CancellationToken.None);
        _ = await sut.GetEffectiveAsync("tenant-b", CancellationToken.None);

        Assert.Equal(2, repo.CallCount);
        Assert.Equal(["tenant-a", "tenant-b"], repo.RequestedTenants);
    }

    // ── SetEntityTypeAsync — the write ──────────────────────────────────────────

    [Theory]
    [InlineData(TenantSettingKeys.EntityTypeSgr)]
    [InlineData(TenantSettingKeys.EntityTypeNonSgr)]
    public async Task SetEntityTypeAsync_StoresTheEntityTypeRow(string entityType)
    {
        var repo = new StubTenantSettingsRepository();

        await BuildSut(repo).SetEntityTypeAsync(Tenant, entityType, CancellationToken.None);

        var write = Assert.Single(repo.Writes);
        Assert.Equal(Tenant, write.TenantId);
        Assert.Equal(TenantSettingKeys.EntityType, write.Key);
        Assert.Equal(entityType, write.Value);
    }

    /// <summary>
    /// Only the entity type is written. A tenant that has overridden an integration keeps that row,
    /// and every key it has NOT overridden keeps following the preset — materialising all four
    /// would pin the tenant against future preset changes in code.
    /// </summary>
    [Fact]
    public async Task SetEntityTypeAsync_WritesTheEntityTypeKeyOnly()
    {
        var repo = new StubTenantSettingsRepository();

        await BuildSut(repo).SetEntityTypeAsync(Tenant, TenantSettingKeys.EntityTypeNonSgr, CancellationToken.None);

        Assert.All(repo.Writes, w => Assert.Equal(TenantSettingKeys.EntityType, w.Key));
    }

    /// <summary>
    /// The stored value is the preset's own EntityType, not the caller's string, so a value that
    /// only differs by casing or padding cannot land in the database and force the read path into
    /// its unrecognised-value fallback.
    /// </summary>
    [Theory]
    [InlineData("  non_sgr  ")]
    [InlineData("NON_SGR")]
    [InlineData("Non_Sgr")]
    public async Task SetEntityTypeAsync_NormalisesTheStoredValue(string input)
    {
        var repo = new StubTenantSettingsRepository();

        await BuildSut(repo).SetEntityTypeAsync(Tenant, input, CancellationToken.None);

        Assert.Equal(TenantSettingKeys.EntityTypeNonSgr, Assert.Single(repo.Writes).Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("banco")]
    [InlineData("sgr_")]
    [InlineData("non-sgr")]   // hyphen, not underscore
    public async Task SetEntityTypeAsync_UnknownValue_ThrowsAndWritesNothing(string entityType)
    {
        var repo = new StubTenantSettingsRepository();

        await Assert.ThrowsAsync<ArgumentException>(
            () => BuildSut(repo).SetEntityTypeAsync(Tenant, entityType, CancellationToken.None));

        // Nothing is written: validation happens before the upsert, so a rejected value cannot
        // leave a half-applied change behind.
        Assert.Empty(repo.Writes);
    }

    [Fact]
    public async Task SetEntityTypeAsync_NullValue_ThrowsAndWritesNothing()
    {
        var repo = new StubTenantSettingsRepository();

        await Assert.ThrowsAsync<ArgumentException>(
            () => BuildSut(repo).SetEntityTypeAsync(Tenant, null!, CancellationToken.None));

        Assert.Empty(repo.Writes);
    }

    /// <summary>
    /// The OPPOSITE failure contract to the read. A failed read resolves to the preset marked
    /// ReadFailed so callers can fail open; a failed write must surface, because an operator told
    /// the profile was saved must not be told that by a write that failed.
    /// </summary>
    [Fact]
    public async Task SetEntityTypeAsync_WhenTheWriteFails_Propagates()
    {
        var repo = new StubTenantSettingsRepository
        {
            ThrowOnUpsert = new InvalidOperationException("boom")
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => BuildSut(repo).SetEntityTypeAsync(Tenant, TenantSettingKeys.EntityTypeNonSgr, CancellationToken.None));
    }

    /// <summary>Read and write disagree on failure, deliberately. Pinned side by side.</summary>
    [Fact]
    public async Task GetEffectiveAsync_StillSwallowsAFailedRead_WhileTheWriteThrows()
    {
        var repo = new StubTenantSettingsRepository { ThrowOnCall = new InvalidOperationException("boom") };

        var resolution = await BuildSut(repo).GetEffectiveAsync(Tenant, CancellationToken.None);

        Assert.Equal(TenantProfileSource.ReadFailed, resolution.Source);
        Assert.True(resolution.IsDegraded);
    }

    // ── The memo ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The regression this guards: the service memoises per request, so without the cache eviction
    /// in SetEntityTypeAsync a read after a write in the SAME request returns the pre-write profile.
    /// </summary>
    [Fact]
    public async Task SetEntityTypeAsync_AfterAPriorRead_ReturnsTheNewProfileNotTheMemoizedOne()
    {
        var repo = new StubTenantSettingsRepository();
        var sut  = BuildSut(repo);

        // Warm the memo with the default (SGR) resolution.
        var before = await sut.GetEffectiveAsync(Tenant, CancellationToken.None);
        Assert.Equal(TenantSettingKeys.EntityTypeSgr, before.Profile.EntityType);

        var after = await sut.SetEntityTypeAsync(Tenant, TenantSettingKeys.EntityTypeNonSgr, CancellationToken.None);

        Assert.Equal(TenantSettingKeys.EntityTypeNonSgr, after.Profile.EntityType);
        Assert.False(after.Profile.CasfogEnabled);
        Assert.False(after.Profile.NosisEnabled);
    }

    /// <summary>And a consumer reading LATER in the same request must see it too.</summary>
    [Fact]
    public async Task GetEffectiveAsync_AfterAWrite_SeesTheNewProfile()
    {
        var repo = new StubTenantSettingsRepository();
        var sut  = BuildSut(repo);

        await sut.GetEffectiveAsync(Tenant, CancellationToken.None);
        await sut.SetEntityTypeAsync(Tenant, TenantSettingKeys.EntityTypeNonSgr, CancellationToken.None);

        var later = await sut.GetEffectiveAsync(Tenant, CancellationToken.None);

        Assert.Equal(TenantSettingKeys.EntityTypeNonSgr, later.Profile.EntityType);
    }

    /// <summary>
    /// The write returns the EFFECTIVE profile, not the bare preset: an explicit
    /// integration.*.enabled row still wins over the preset the new entity type selects.
    /// </summary>
    [Fact]
    public async Task SetEntityTypeAsync_ReturnsTheOverlaidProfile_NotThePreset()
    {
        var repo = new StubTenantSettingsRepository();
        repo.Rows[TenantSettingKeys.IntegrationNosisEnabled] = TenantSettingKeys.BoolTrue;

        var resolution = await BuildSut(repo)
            .SetEntityTypeAsync(Tenant, TenantSettingKeys.EntityTypeNonSgr, CancellationToken.None);

        Assert.Equal(TenantSettingKeys.EntityTypeNonSgr, resolution.Profile.EntityType);
        Assert.False(resolution.Profile.CasfogEnabled);   // preset value, not overridden
        Assert.True(resolution.Profile.NosisEnabled);     // the explicit row wins over the preset
    }
}
