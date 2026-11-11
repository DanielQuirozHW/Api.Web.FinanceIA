// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-26

using System.Security.Claims;
using System.Text.Json;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Entities;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// <c>GET /api/bff/auth/me</c> carries the tenant's company name as <c>tenantDisplayName</c>, read
/// from <c>branding.company_name</c> of the RESOLVED tenant through the real
/// <see cref="TenantSettingsService"/> and <see cref="TenantSettingsRepository"/>. Absent, blank, or
/// unreadable, it is <c>null</c> — written, not omitted — and the rest of the profile is unchanged.
/// </summary>
public sealed class BffControllerMeTests
{
    private const string Tenant      = "TenantA";
    private const string OtherTenant = "TenantB";
    private const string Uid         = "firebase-uid-1";

    [Fact]
    public async Task WithTheKey_ReturnsTheCompanyName()
    {
        await using var db = NewContext();
        await SeedAsync(db,
            Setting(TenantSettingKeys.BrandingCompanyName, " Movil SGR "),
            Setting(TenantSettingKeys.BrandingCompanyName, "Other SGR", OtherTenant));

        var response = await MeAsync(new TenantSettingsRepository(db, NullLogger<TenantSettingsRepository>.Instance));

        Assert.Equal("Movil SGR", response.TenantDisplayName);
        Assert.Equal(TenantSettingKeys.CreditLineModeDefault, response.CreditLineMode);
        Assert.Equal(Tenant, response.TenantCode);
    }

    [Fact]
    public async Task WithoutTheKey_ReturnsNull_WrittenAsNullInTheJson()
    {
        await using var db = NewContext();
        await SeedAsync(db, Setting(TenantSettingKeys.BrandingCompanyName, "Other SGR", OtherTenant));

        var response = await MeAsync(new TenantSettingsRepository(db, NullLogger<TenantSettingsRepository>.Instance));

        Assert.Null(response.TenantDisplayName);
        Assert.Equal("analyst@example.com", response.Email);
        Assert.Contains(
            "\"tenantDisplayName\":null",
            JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Fact]
    public async Task SettingsReadFailure_ReturnsNull_AndTheProfile()
    {
        var response = await MeAsync(new ThrowingTenantSettingsRepository());

        Assert.Null(response.TenantDisplayName);
        Assert.Equal(TenantSettingKeys.CreditLineModeDefault, response.CreditLineMode);
        Assert.Equal("Analyst", response.DisplayName);
    }

    private static async Task<AuthenticatedUserResponse> MeAsync(ITenantSettingsRepository settingsRepository)
    {
        var controller = new BffController(
            new StubTenantContextAccessor(),
            null!,    // IFirebaseTokenValidator
            null!,    // ITokenService
            null!,    // IOnboardingTokenIssuer
            null!,    // IRecaptchaVerifier
            new StubUserRepository(),
            null!,    // IUserInvitationService
            new TenantSettingsService(settingsRepository, NullLogger<TenantSettingsService>.Instance),
            null!,    // IWebHostEnvironment
            Options.Create(new OnboardingOptions()),
            NullLogger<BffController>.Instance);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("uid", Uid)], "Internal"))
            }
        };

        IActionResult result = await controller.MeAsync(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<AuthenticatedUserResponse>(ok.Value);
    }

    private static FinanciaDbContext NewContext()
        => new(new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"bff-me-{Guid.NewGuid()}")
            .Options);

    private static TenantSettings Setting(string key, string value, string tenantId = Tenant) => new()
    {
        SettingId    = Guid.NewGuid(),
        TenantId     = tenantId,
        SettingKey   = key,
        SettingValue = value,
        UpdatedAt    = DateTime.UtcNow
    };

    private static async Task SeedAsync(FinanciaDbContext db, params TenantSettings[] settings)
    {
        db.TenantSettings.AddRange(settings);
        db.ChangeTracker.DetectChanges(); // AutoDetectChanges is disabled on the context
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; } = new()
        {
            TenantId               = Tenant,
            FirebaseProjectId      = "project",
            FirebaseCredentialJson = "{}",
            RecaptchaSecretKey     = "secret"
        };
    }

    private sealed class ThrowingTenantSettingsRepository : ITenantSettingsRepository
    {
        public Task<string?> GetValueAsync(string tenantId, string settingKey, CancellationToken ct)
            => throw new InvalidOperationException("db down");

        public Task<Api.Web.FinanceIA.Models.Financia.TenantSettingEntry?> GetEntryAsync(string tenantId, string settingKey, CancellationToken ct)
            => throw new InvalidOperationException("db down");

        public Task<IReadOnlyDictionary<string, string>> GetValuesAsync(
            string tenantId, IReadOnlyCollection<string> settingKeys, CancellationToken ct)
            => throw new InvalidOperationException("db down");

        public Task UpsertAsync(string tenantId, string settingKey, string settingValue, CancellationToken ct)
            => throw new NotSupportedException();

        public Task SetValuesAsync(string tenantId, IReadOnlyDictionary<string, string?> values, CancellationToken ct)
            => throw new NotSupportedException();
    }

    /// <summary>The one active user /me looks up; the last-login update is a no-op.</summary>
    private sealed class StubUserRepository : IUserRepository
    {
        private readonly User _user = new()
        {
            UserId      = Guid.NewGuid(),
            TenantId    = Tenant,
            FirebaseUid = Uid,
            Email       = "analyst@example.com",
            DisplayName = "Analyst",
            Role        = Roles.Analyst,
            IsActive    = true,
            CreatedAt   = DateTime.UtcNow.AddDays(-10),
            UpdatedAt   = DateTime.UtcNow.AddDays(-10)
        };

        public Task<User?> GetByFirebaseUidAsync(string firebaseUid, CancellationToken ct)
            => Task.FromResult<User?>(firebaseUid == Uid ? _user : null);

        public Task UpdateLastLoginAsync(Guid userId, CancellationToken ct) => Task.CompletedTask;

        public Task<User?> GetByUserIdAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<User>> GetByIdsAsync(IEnumerable<Guid> userIds, CancellationToken ct) => throw new NotImplementedException();
        public Task<User?> GetByEmailAsync(string email, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> ExistsByFirebaseUidAsync(string firebaseUid, CancellationToken ct) => throw new NotImplementedException();
        public Task<User> CreateAsync(User user, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateOnboardingFlagsAsync(Guid userId, string? flagsJson, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<User>> GetActiveByTenantAsync(string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateTeamAsync(Guid userId, Guid? teamId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> ClearTeamAsync(Guid teamId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
    }
}
