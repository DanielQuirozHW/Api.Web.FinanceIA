// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-24

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// <see cref="IntegrationAccessService"/>: the entitlement half fails open on a degraded or unreadable
/// profile and never on cancellation; the availability half reads exactly the settings the vendor
/// services read; <see cref="IntegrationAccessService.ResolveUsableAsync"/> ANDs the two.
/// </summary>
public sealed class IntegrationAccessServiceTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string Tenant = DashboardFixture.Tenant;

    private static readonly TenantProfile AllOn = new(TenantSettingKeys.EntityTypeSgr, true, true, true);

    private static Dictionary<string, string?> ConfiguredWithout(params string[] keys)
    {
        var settings = DashboardFixture.FullyConfigured();
        foreach (var key in keys)
            settings.Remove(key);
        return settings;
    }

    // ── Entitlement ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(TenantProfileSource.Configured)]
    [InlineData(TenantProfileSource.NoConfiguration)]
    public async Task ARealProfile_IsReturnedAsIs(TenantProfileSource source)
    {
        var logger = new RecordingLogger<IntegrationAccessService>();
        var sut    = DashboardFixture.IntegrationAccess(new TenantProfileResolution(TenantProfilePresets.NonSgr, source), logger: logger);

        var profile = await sut.ResolveEntitlementsAsync(Tenant, Ct);

        Assert.Same(TenantProfilePresets.NonSgr, profile);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task ADegradedProfile_FailsOpen_WithInformationOnly()
    {
        var logger = new RecordingLogger<IntegrationAccessService>();
        var sut    = DashboardFixture.IntegrationAccess(
            new TenantProfileResolution(TenantProfilePresets.NonSgr, TenantProfileSource.ReadFailed), logger: logger);

        var profile = await sut.ResolveEntitlementsAsync(Tenant, Ct);

        Assert.Equal((true, true, true), (profile.LufeEnabled, profile.CasfogEnabled, profile.NosisEnabled));
        // TenantProfileService already warned about the failed read; this records the decision only.
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains(Tenant, entry.Message);
    }

    [Fact]
    public async Task AResolverThatThrows_FailsOpen_AndWarnsOnceWithTheTenant()
    {
        var logger = new RecordingLogger<IntegrationAccessService>();
        var sut    = DashboardFixture.IntegrationAccess(throwOnRead: new InvalidOperationException("boom"), logger: logger);

        var profile = await sut.ResolveEntitlementsAsync(Tenant, Ct);

        Assert.Equal((true, true, true), (profile.LufeEnabled, profile.CasfogEnabled, profile.NosisEnabled));
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(Tenant, entry.Message);
    }

    [Fact]
    public async Task Cancellation_IsNotADegradedProfile_AndPropagates()
    {
        var logger = new RecordingLogger<IntegrationAccessService>();
        var sut    = DashboardFixture.IntegrationAccess(throwOnRead: new OperationCanceledException(), logger: logger);

        await Assert.ThrowsAsync<OperationCanceledException>(() => sut.ResolveEntitlementsAsync(Tenant, Ct));
        Assert.Empty(logger.Entries);
    }

    // ── Availability ─────────────────────────────────────────────────────────────

    [Fact]
    public void AFullyConfiguredInstallation_IsAvailableForEveryOptionalIntegration()
    {
        var sut = DashboardFixture.IntegrationAccess();

        Assert.True(sut.IsNosisConfigured());
        Assert.True(sut.IsCasfogConfigured());
        Assert.Equal("lufe-key", sut.GetLufeApiKey(Tenant));
        Assert.Null(sut.GetLufeApiKey(DashboardFixture.Other));
    }

    [Theory]
    [InlineData("Nosis:Enabled")]
    [InlineData("Nosis:ApiKey")]
    [InlineData("Nosis:VR")]
    public void Nosis_MissingAnyOfItsThreeSettings_IsNotConfigured(string missingKey)
    {
        Assert.False(DashboardFixture.IntegrationAccess(settings: ConfiguredWithout(missingKey)).IsNosisConfigured());
    }

    [Fact]
    public void Nosis_SwitchedOff_IsNotConfigured_EvenWithAKeyAndAVr()
    {
        var settings = DashboardFixture.FullyConfigured();
        settings["Nosis:Enabled"] = "false";

        Assert.False(DashboardFixture.IntegrationAccess(settings: settings).IsNosisConfigured());
    }

    [Theory]
    [InlineData("Casfog:Email")]
    [InlineData("Casfog:Password")]
    public void Casfog_MissingEitherCredential_IsNotConfigured(string missingKey)
    {
        Assert.False(DashboardFixture.IntegrationAccess(settings: ConfiguredWithout(missingKey)).IsCasfogConfigured());
    }

    [Fact]
    public void Casfog_WithABlankCredential_IsNotConfigured()
    {
        var settings = DashboardFixture.FullyConfigured();
        settings["Casfog:Password"] = "   ";

        Assert.False(DashboardFixture.IntegrationAccess(settings: settings).IsCasfogConfigured());
    }

    // ── Usable = entitled AND available ──────────────────────────────────────────

    [Fact]
    public async Task Usable_IsEntitlementAndAvailability()
    {
        var entitledToLufeOnly = new TenantProfileResolution(
            AllOn with { CasfogEnabled = false, NosisEnabled = false }, TenantProfileSource.Configured);

        Assert.Equal(new OptionalIntegrationAccess(true, false, false),
            await DashboardFixture.IntegrationAccess(entitledToLufeOnly).ResolveUsableAsync(Tenant, Ct));

        // Entitled to everything, but no LUFE key and no NOSIS switch.
        var unavailable = ConfiguredWithout($"Tenants:{Tenant}:LufeApiKey", "Nosis:Enabled");
        Assert.Equal(new OptionalIntegrationAccess(false, true, false),
            await DashboardFixture.IntegrationAccess(settings: unavailable).ResolveUsableAsync(Tenant, Ct));
    }

    [Fact]
    public async Task Usable_OnADegradedProfile_LeavesAvailabilityAsTheOnlyGate()
    {
        var degraded = new TenantProfileResolution(TenantProfilePresets.NonSgr, TenantProfileSource.ReadFailed);

        Assert.Equal(new OptionalIntegrationAccess(true, true, true),
            await DashboardFixture.IntegrationAccess(degraded).ResolveUsableAsync(Tenant, Ct));
        Assert.Equal(new OptionalIntegrationAccess(true, true, false),
            await DashboardFixture.IntegrationAccess(degraded, settings: ConfiguredWithout("Nosis:VR")).ResolveUsableAsync(Tenant, Ct));
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
