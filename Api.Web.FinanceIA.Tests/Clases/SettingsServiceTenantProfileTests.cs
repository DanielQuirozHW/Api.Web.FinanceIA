// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-09

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// Behaviour of the tenant-profile half of <see cref="SettingsService"/>: what the endpoint reads
/// back, what it audits, and — the one that matters most — that a rejected value leaves nothing
/// written.
///
/// <para>
/// <see cref="ITenantProfileService"/> is stubbed here on purpose. Its own tests cover resolution,
/// normalisation and the cache eviction; what is under test here is the orchestration around it.
/// </para>
/// </summary>
public sealed class SettingsServiceTenantProfileTests
{
    private const string Tenant = "tenant-1";
    private static readonly Guid User = Guid.NewGuid();

    // ── Stubs ───────────────────────────────────────────────────────────────────

    private sealed class StubTenantProfileService : ITenantProfileService
    {
        public TenantProfileResolution Current { get; set; } =
            new(TenantProfilePresets.Sgr, TenantProfileSource.NoConfiguration);

        public Exception? ThrowOnWrite { get; set; }
        public List<string> Writes { get; } = [];

        public Task<TenantProfileResolution> GetEffectiveAsync(string tenantId, CancellationToken ct)
            => Task.FromResult(Current);

        public Task<TenantProfileResolution> SetEntityTypeAsync(string tenantId, string entityType, CancellationToken ct)
        {
            if (ThrowOnWrite is not null)
                return Task.FromException<TenantProfileResolution>(ThrowOnWrite);

            var preset = TenantProfilePresets.Find(entityType)
                ?? throw new ArgumentException("unknown entity type", nameof(entityType));

            Writes.Add(preset.EntityType);
            Current = new TenantProfileResolution(preset, TenantProfileSource.Configured);
            return Task.FromResult(Current);
        }
    }

    private sealed record AuditCall(
        string TenantId, Guid? UserId, string Action,
        string? EntityType, string? EntityId, string? OldValueJson, string? NewValueJson);

    private sealed class StubAuditService : IAuditService
    {
        public List<AuditCall> Calls { get; } = [];

        public Task LogAsync(
            string tenantId, Guid? userId, string action, string severity = "Info",
            string? entityType = null, string? entityId = null, string? ipAddress = null,
            string? requestId = null, string? oldValueJson = null, string? newValueJson = null,
            CancellationToken ct = default)
        {
            Calls.Add(new AuditCall(tenantId, userId, action, entityType, entityId, oldValueJson, newValueJson));
            return Task.CompletedTask;
        }

        // Session logging is a different concern and never reached from the profile path.
        public Task LogSessionAsync(
            Guid tenantId, Guid? userId, string email, string eventType,
            string? ipAddress, string? userAgent, string? failureReason = null,
            CancellationToken ct = default)
            => throw new NotSupportedException("The tenant-profile path must not write session logs.");
    }

    private static SettingsService BuildSut(StubTenantProfileService profile, StubAuditService audit)
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"settings-profile-{Guid.NewGuid()}")
            .Options;

        return new SettingsService(
            new FinanciaDbContext(options), audit, profile,
            NullLogger<SettingsService>.Instance);
    }

    private static UpdateTenantProfileRequest Request(string entityType) => new() { EntityType = entityType };

    // ── Read ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetTenantProfileAsync_ProjectsTheResolutionIncludingItsSource()
    {
        var profile = new StubTenantProfileService
        {
            Current = new TenantProfileResolution(TenantProfilePresets.NonSgr, TenantProfileSource.Configured)
        };

        var dto = await BuildSut(profile, new StubAuditService())
            .GetTenantProfileAsync(Tenant, CancellationToken.None);

        Assert.Equal(TenantSettingKeys.EntityTypeNonSgr, dto.EntityType);
        Assert.Equal(nameof(TenantProfileSource.Configured), dto.Source);
        Assert.True(dto.LufeEnabled);
        Assert.False(dto.CasfogEnabled);
        Assert.False(dto.NosisEnabled);
    }

    /// <summary>
    /// A degraded read must reach the client AS degraded, not be laundered into a plain answer: the
    /// profile it carries is a fallback, and a portal presenting it as settled configuration would
    /// be lying about what the tenant is entitled to.
    /// </summary>
    [Fact]
    public async Task GetTenantProfileAsync_SurfacesReadFailed()
    {
        var profile = new StubTenantProfileService
        {
            Current = new TenantProfileResolution(TenantProfilePresets.Default, TenantProfileSource.ReadFailed)
        };

        var dto = await BuildSut(profile, new StubAuditService())
            .GetTenantProfileAsync(Tenant, CancellationToken.None);

        Assert.Equal(nameof(TenantProfileSource.ReadFailed), dto.Source);
    }

    // ── Write ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateTenantProfileAsync_WritesAndReturnsTheNewProfile()
    {
        var profile = new StubTenantProfileService();

        var dto = await BuildSut(profile, new StubAuditService())
            .UpdateTenantProfileAsync(User, Tenant, Request(TenantSettingKeys.EntityTypeNonSgr),
                "10.0.0.1", "trace-1", CancellationToken.None);

        Assert.Equal(TenantSettingKeys.EntityTypeNonSgr, Assert.Single(profile.Writes));
        Assert.Equal(TenantSettingKeys.EntityTypeNonSgr, dto.EntityType);
        Assert.Equal(nameof(TenantProfileSource.Configured), dto.Source);
    }

    /// <summary>
    /// The audit must carry what it changed FROM as well as TO — the old value is gone after the
    /// write, so capturing it afterwards would record the new one twice.
    /// </summary>
    [Fact]
    public async Task UpdateTenantProfileAsync_AuditsBothTheOldAndTheNewProfile()
    {
        var profile = new StubTenantProfileService();   // starts on the SGR default
        var audit   = new StubAuditService();

        await BuildSut(profile, audit).UpdateTenantProfileAsync(
            User, Tenant, Request(TenantSettingKeys.EntityTypeNonSgr),
            "10.0.0.1", "trace-1", CancellationToken.None);

        var call = Assert.Single(audit.Calls);
        Assert.Equal(Tenant, call.TenantId);
        Assert.Equal(User, call.UserId);
        Assert.Equal("Settings.UpdateTenantProfile", call.Action);
        Assert.Equal("TenantSettings", call.EntityType);
        Assert.Equal(TenantSettingKeys.EntityType, call.EntityId);

        Assert.Contains(TenantSettingKeys.EntityTypeSgr, call.OldValueJson);
        Assert.Contains(TenantSettingKeys.EntityTypeNonSgr, call.NewValueJson);
    }

    [Fact]
    public async Task UpdateTenantProfileAsync_UnknownValue_ThrowsAndAuditsNothing()
    {
        var profile = new StubTenantProfileService();
        var audit   = new StubAuditService();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            BuildSut(profile, audit).UpdateTenantProfileAsync(
                User, Tenant, Request("banco"), null, null, CancellationToken.None));

        Assert.Empty(profile.Writes);
        Assert.Empty(audit.Calls);
    }

    /// <summary>
    /// A failed write must propagate rather than fail open, and must not leave an audit row saying
    /// a change happened. Contrast the read, which never throws.
    /// </summary>
    [Fact]
    public async Task UpdateTenantProfileAsync_WhenTheWriteFails_PropagatesAndAuditsNothing()
    {
        var profile = new StubTenantProfileService { ThrowOnWrite = new InvalidOperationException("boom") };
        var audit   = new StubAuditService();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BuildSut(profile, audit).UpdateTenantProfileAsync(
                User, Tenant, Request(TenantSettingKeys.EntityTypeNonSgr), null, null, CancellationToken.None));

        Assert.Empty(audit.Calls);
    }
}
