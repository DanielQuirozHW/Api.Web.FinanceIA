// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-03

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// Covers <see cref="TenantSettingsService"/>: in-code defaults when a tenant has no
/// row, known-value pass-through, malformed-value fallback, and per-request memoization
/// (the Scoped instance queries the repository at most once per (tenant, key)).
/// Follows the repo's no-mock-library convention: a hand-rolled private sealed stub.
/// </summary>
public sealed class TenantSettingsServiceTests
{
    private const string Tenant = "tenant-1";

    private sealed class StubTenantSettingsRepository : ITenantSettingsRepository
    {
        public string? ValueToReturn { get; set; }
        public Exception? ThrowOnCall { get; set; }
        public int CallCount { get; private set; }
        public string? LastKey { get; private set; }

        public Task<string?> GetValueAsync(string tenantId, string settingKey, CancellationToken ct)
        {
            CallCount++;
            LastKey = settingKey;
            if (ThrowOnCall is not null)
                return Task.FromException<string?>(ThrowOnCall);
            return Task.FromResult(ValueToReturn);
        }

        // Not exercised here — TenantSettingsService never calls the batched read; it belongs to
        // TenantProfileService, which has its own stub. Present only to satisfy the interface.
        public Task<Api.Web.FinanceIA.Models.Financia.TenantSettingEntry?> GetEntryAsync(string tenantId, string settingKey, CancellationToken ct)
            => throw new NotSupportedException("TenantSettingsService does not read entries.");

        public Task<IReadOnlyDictionary<string, string>> GetValuesAsync(
            string tenantId, IReadOnlyCollection<string> settingKeys, CancellationToken ct)
            => throw new NotSupportedException("TenantSettingsService must not use the batched read.");

        // Not exercised here either — TenantSettingsService reads only; the write path belongs to
        // TenantProfileService. Present only to satisfy the interface.
        public Task UpsertAsync(string tenantId, string settingKey, string settingValue, CancellationToken ct)
            => throw new NotSupportedException("TenantSettingsService must not write.");

        public Task SetValuesAsync(string tenantId, IReadOnlyDictionary<string, string?> values, CancellationToken ct)
            => throw new NotSupportedException("TenantSettingsService must not write.");
    }

    private static TenantSettingsService BuildSut(StubTenantSettingsRepository repo)
        => new(repo, NullLogger<TenantSettingsService>.Instance);

    [Fact]
    public async Task GetCreditLineModeAsync_WhenNoRow_ReturnsSgrDefault()
    {
        var repo = new StubTenantSettingsRepository { ValueToReturn = null };
        var sut  = BuildSut(repo);

        var mode = await sut.GetCreditLineModeAsync(Tenant, CancellationToken.None);

        Assert.Equal(TenantSettingKeys.CreditLineModeSgr, mode);
    }

    [Fact]
    public async Task GetCreditLineModeAsync_WhenSimpleStored_ReturnsSimple()
    {
        var repo = new StubTenantSettingsRepository { ValueToReturn = "simple" };
        var sut  = BuildSut(repo);

        var mode = await sut.GetCreditLineModeAsync(Tenant, CancellationToken.None);

        Assert.Equal(TenantSettingKeys.CreditLineModeSimple, mode);
    }

    [Fact]
    public async Task GetCreditLineModeAsync_WhenMixedCaseAndPadding_NormalizesToKnownMode()
    {
        var repo = new StubTenantSettingsRepository { ValueToReturn = "  SIMPLE  " };
        var sut  = BuildSut(repo);

        var mode = await sut.GetCreditLineModeAsync(Tenant, CancellationToken.None);

        Assert.Equal(TenantSettingKeys.CreditLineModeSimple, mode);
    }

    [Fact]
    public async Task GetCreditLineModeAsync_WhenUnknownValue_FallsBackToDefault()
    {
        var repo = new StubTenantSettingsRepository { ValueToReturn = "garbage" };
        var sut  = BuildSut(repo);

        var mode = await sut.GetCreditLineModeAsync(Tenant, CancellationToken.None);

        Assert.Equal(TenantSettingKeys.CreditLineModeDefault, mode);
    }

    [Fact]
    public async Task GetCreditLineModeAsync_WhenRepositoryThrows_ReturnsDefaultAndDoesNotPropagate()
    {
        // Reproduces the /auth/me incident: a second command on the shared, MARS-off
        // connection makes the repository throw. Reading a presentation-only setting must
        // never take down the caller, so the service swallows it and returns the default.
        var repo = new StubTenantSettingsRepository
        {
            ThrowOnCall = new InvalidOperationException(
                "The connection does not support MultipleActiveResultSets.")
        };
        var sut = BuildSut(repo);

        var mode = await sut.GetCreditLineModeAsync(Tenant, CancellationToken.None);

        Assert.Equal(TenantSettingKeys.CreditLineModeDefault, mode);
    }

    [Fact]
    public async Task GetStringAsync_WhenNoRow_ReturnsProvidedDefault()
    {
        var repo = new StubTenantSettingsRepository { ValueToReturn = null };
        var sut  = BuildSut(repo);

        var value = await sut.GetStringAsync(Tenant, "some.key", "fallback", CancellationToken.None);

        Assert.Equal("fallback", value);
    }

    [Fact]
    public async Task GetCreditLineModeAsync_CalledTwice_QueriesRepositoryOnce()
    {
        var repo = new StubTenantSettingsRepository { ValueToReturn = "simple" };
        var sut  = BuildSut(repo);

        _ = await sut.GetCreditLineModeAsync(Tenant, CancellationToken.None);
        _ = await sut.GetCreditLineModeAsync(Tenant, CancellationToken.None);

        Assert.Equal(1, repo.CallCount);   // per-request memoization
    }

    [Fact]
    public async Task GetTenantDisplayNameAsync_WhenStored_ReturnsItTrimmed_FromTheBrandingKey()
    {
        var repo = new StubTenantSettingsRepository { ValueToReturn = "  Movil SGR " };
        var sut  = BuildSut(repo);

        var name = await sut.GetTenantDisplayNameAsync(Tenant, CancellationToken.None);

        Assert.Equal("Movil SGR", name);
        Assert.Equal(TenantSettingKeys.BrandingCompanyName, repo.LastKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetTenantDisplayNameAsync_WhenNoRowOrBlank_ReturnsNull(string? stored)
    {
        var repo = new StubTenantSettingsRepository { ValueToReturn = stored };
        var sut  = BuildSut(repo);

        Assert.Null(await sut.GetTenantDisplayNameAsync(Tenant, CancellationToken.None));
    }

    [Fact]
    public async Task GetTenantDisplayNameAsync_WhenRepositoryThrows_ReturnsNullAndDoesNotPropagate()
    {
        var repo = new StubTenantSettingsRepository { ThrowOnCall = new InvalidOperationException("db down") };
        var sut  = BuildSut(repo);

        Assert.Null(await sut.GetTenantDisplayNameAsync(Tenant, CancellationToken.None));
    }
}
