// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-26

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Entities;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// The invitation email takes the tenant's sender, template and branding from TenantSettings on every
/// path that sends one — invite, resend and the first-admin bootstrap (no inviter) — through the
/// REAL <see cref="TenantSettingsRepository"/> over an InMemory <see cref="FinanciaDbContext"/>.
/// Pinned: no rows means every one of those fields is null (= the global SendGrid configuration, no
/// branding); another tenant's rows are never read; a settings read failure costs nothing but the
/// tenant values — the email still goes.
/// </summary>
public sealed class UserInvitationServiceEmailSettingsTests
{
    private const string Tenant      = "TenantA";
    private const string OtherTenant = "TenantB";
    private const string Invitee     = "invitee@example.com";
    private const string Logo        = "https://cdn.example.com/movil/logo.png";

    // ── Invite ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task InviteUser_NoRows_SendsOnTheGlobalConfiguration_WithoutBranding()
    {
        await using var db = NewContext();
        var inviterId = await SeedInviterAsync(db);
        var fx = new Fixture(db);

        await fx.Sut.InviteUserAsync(
            Tenant, inviterId, new InviteUserSettingsRequest { Email = Invitee, Role = Roles.Analyst },
            null, null, CancellationToken.None);

        var (message, tenantId) = Assert.Single(fx.Email.Sent);
        Assert.Equal(Tenant, tenantId);
        Assert.Equal(Invitee, message.ToEmail);
        Assert.Equal("Ana Pérez", message.InviterName);
        Assert.StartsWith("https://portal.example/register/invite?token=", message.InvitationLink);
        Assert.Null(message.FromEmail);
        Assert.Null(message.FromName);
        Assert.Null(message.TemplateId);
        Assert.Null(message.CompanyName);
        Assert.Null(message.LogoUrl);
    }

    [Fact]
    public async Task InviteUser_TenantRows_SupplySenderTemplateAndBranding()
    {
        await using var db = NewContext();
        var inviterId = await SeedInviterAsync(db);
        await SeedSettingsAsync(db, TenantRows());
        var fx = new Fixture(db);

        await fx.Sut.InviteUserAsync(
            Tenant, inviterId, new InviteUserSettingsRequest { Email = Invitee, Role = Roles.Analyst },
            null, null, CancellationToken.None);

        AssertTenantValues(Assert.Single(fx.Email.Sent).Message);
    }

    [Fact]
    public async Task InviteUser_SettingsReadFailure_StillSends_AndLogsAWarning()
    {
        await using var db = NewContext();
        var inviterId = await SeedInviterAsync(db);
        var fx = new Fixture(db, new ThrowingTenantSettingsRepository());

        await fx.Sut.InviteUserAsync(
            Tenant, inviterId, new InviteUserSettingsRequest { Email = Invitee, Role = Roles.Analyst },
            null, null, CancellationToken.None);

        var message = Assert.Single(fx.Email.Sent).Message;
        Assert.Null(message.TemplateId);
        Assert.Null(message.CompanyName);
        Assert.Contains(fx.Logger.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("TenantSettings read failed for invitation in tenant TenantA"));
    }

    [Theory]
    [InlineData("http://cdn.example.com/logo.png")]
    [InlineData("cdn.example.com/logo.png")]
    public async Task InviteUser_NonHttpsLogo_IsIgnored_WithAWarning(string logoUrl)
    {
        await using var db = NewContext();
        var inviterId = await SeedInviterAsync(db);
        await SeedSettingsAsync(db, Setting(TenantSettingKeys.BrandingLogoUrl, logoUrl));
        var fx = new Fixture(db);

        await fx.Sut.InviteUserAsync(
            Tenant, inviterId, new InviteUserSettingsRequest { Email = Invitee, Role = Roles.Analyst },
            null, null, CancellationToken.None);

        Assert.Null(Assert.Single(fx.Email.Sent).Message.LogoUrl);
        Assert.Contains(fx.Logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("branding.logo_url"));
        Assert.DoesNotContain(fx.Logger.Entries, e => e.Message.Contains(logoUrl));
    }

    // ── Resend ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Resend_TenantRows_SupplySenderTemplateAndBranding()
    {
        await using var db = NewContext();
        var inviterId = await SeedInviterAsync(db);
        await SeedSettingsAsync(db, TenantRows());
        var invitation = await SeedInvitationAsync(db, inviterId);
        var fx = new Fixture(db);

        InvitationActionResult result = await fx.Sut.ResendInvitationAsync(
            Tenant, invitation.InvitationId, inviterId, null, null, CancellationToken.None);

        Assert.Equal(InvitationActionResult.Success, result);
        var message = Assert.Single(fx.Email.Sent).Message;
        Assert.Equal("Ana Pérez", message.InviterName);
        AssertTenantValues(message);
    }

    // ── First-admin bootstrap (no inviter) ───────────────────────────────────────

    [Fact]
    public async Task FirstAdmin_NoRows_SendsWithTheFallbackInviter_AndNoBranding()
    {
        await using var db = NewContext();
        var fx = new Fixture(db);

        FirstAdminInviteOutcome outcome = await fx.Sut.InviteFirstAdminAsync(
            Tenant, Invitee, null, null, CancellationToken.None);

        Assert.True(outcome.Invitation!.EmailSent);
        var message = Assert.Single(fx.Email.Sent).Message;
        Assert.Equal("Un administrador", message.InviterName);
        Assert.Null(message.TemplateId);
        Assert.Null(message.CompanyName);
        Assert.Null(message.LogoUrl);
    }

    [Fact]
    public async Task FirstAdmin_TenantRows_SupplySenderTemplateAndBranding()
    {
        await using var db = NewContext();
        await SeedSettingsAsync(db, TenantRows());
        var fx = new Fixture(db);

        FirstAdminInviteOutcome outcome = await fx.Sut.InviteFirstAdminAsync(
            Tenant, Invitee, null, null, CancellationToken.None);

        Assert.True(outcome.Invitation!.EmailSent);
        var message = Assert.Single(fx.Email.Sent).Message;
        Assert.Equal("Un administrador", message.InviterName);
        AssertTenantValues(message);
    }

    [Fact]
    public async Task FirstAdmin_SettingsReadFailure_StillSends()
    {
        await using var db = NewContext();
        var fx = new Fixture(db, new ThrowingTenantSettingsRepository());

        FirstAdminInviteOutcome outcome = await fx.Sut.InviteFirstAdminAsync(
            Tenant, Invitee, null, null, CancellationToken.None);

        Assert.True(outcome.Invitation!.EmailSent);
        Assert.Single(fx.Email.Sent);
        Assert.Contains(fx.Logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("TenantSettings read failed"));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>Every invitation key for TenantA, plus decoys for TenantB that must never be read.</summary>
    private static TenantSettings[] TenantRows() =>
    [
        Setting(TenantSettingKeys.EmailFromAddress, "no-reply@movil.example.com"),
        Setting(TenantSettingKeys.EmailFromName, "Movil SGR"),
        Setting(TenantSettingKeys.EmailInvitationTemplateId, " d-tenant-invite "),
        Setting(TenantSettingKeys.BrandingCompanyName, "Movil SGR"),
        Setting(TenantSettingKeys.BrandingLogoUrl, Logo),
        Setting(TenantSettingKeys.EmailInvitationTemplateId, "d-other-tenant", OtherTenant),
        Setting(TenantSettingKeys.BrandingCompanyName, "Other SGR", OtherTenant)
    ];

    private static void AssertTenantValues(InvitationEmail message)
    {
        Assert.Equal("no-reply@movil.example.com", message.FromEmail);
        Assert.Equal("Movil SGR", message.FromName);
        Assert.Equal("d-tenant-invite", message.TemplateId);
        Assert.Equal("Movil SGR", message.CompanyName);
        Assert.Equal(Logo, message.LogoUrl);
    }

    private static FinanciaDbContext NewContext()
        => new(new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"invitation-email-settings-{Guid.NewGuid()}")
            .Options);

    private static TenantSettings Setting(string key, string value, string tenantId = Tenant) => new()
    {
        SettingId    = Guid.NewGuid(),
        TenantId     = tenantId,
        SettingKey   = key,
        SettingValue = value,
        UpdatedAt    = DateTime.UtcNow
    };

    private static async Task SeedSettingsAsync(FinanciaDbContext db, params TenantSettings[] settings)
    {
        db.TenantSettings.AddRange(settings);
        db.ChangeTracker.DetectChanges(); // AutoDetectChanges is disabled on the context
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task<Guid> SeedInviterAsync(FinanciaDbContext db)
    {
        var inviter = new User
        {
            UserId      = Guid.NewGuid(),
            TenantId    = Tenant,
            FirebaseUid = $"uid-{Guid.NewGuid():N}",
            Email       = "admin@client.example",
            DisplayName = "Ana Pérez",
            Role        = Roles.Admin,
            IsActive    = true,
            CreatedAt   = DateTime.UtcNow.AddDays(-10),
            UpdatedAt   = DateTime.UtcNow.AddDays(-10)
        };
        db.Users.Add(inviter);
        db.ChangeTracker.DetectChanges();
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return inviter.UserId;
    }

    private static async Task<UserInvitation> SeedInvitationAsync(FinanciaDbContext db, Guid inviterId)
    {
        var invitation = new UserInvitation
        {
            InvitationId    = Guid.NewGuid(),
            TenantId        = Tenant,
            Email           = Invitee,
            Role            = Roles.Analyst,
            InvitedByUserId = inviterId,
            Token           = "old-token",
            ExpiresAt       = DateTime.UtcNow.AddDays(1),
            CreatedAt       = DateTime.UtcNow.AddDays(-1)
        };
        db.UserInvitations.Add(invitation);
        db.ChangeTracker.DetectChanges();
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return invitation;
    }

    private sealed class Fixture
    {
        public Fixture(FinanciaDbContext db, ITenantSettingsRepository? settings = null)
        {
            Sut = new UserInvitationService(
                db,
                new UserInvitationRepository(db, NullLogger<UserInvitationRepository>.Instance),
                new UserRepository(db, NullLogger<UserRepository>.Instance),
                settings ?? new TenantSettingsRepository(db, NullLogger<TenantSettingsRepository>.Instance),
                Email,
                new NoOpAuditService(),
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?> { ["AppSettings:FrontendUrl"] = "https://portal.example" })
                    .Build(),
                Logger);
        }

        public UserInvitationService Sut { get; }
        public RecordingEmailService Email { get; } = new();
        public RecordingLogger<UserInvitationService> Logger { get; } = new();
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

    private sealed class RecordingEmailService : IEmailService
    {
        public List<(InvitationEmail Message, string TenantId)> Sent { get; } = [];

        public Task SendInvitationEmailAsync(InvitationEmail message, string tenantId, CancellationToken cancellationToken)
        {
            Sent.Add((message, tenantId));
            return Task.CompletedTask;
        }

        public Task SendPasswordResetEmailAsync(PasswordResetEmail message, string tenantId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task SendEmailVerificationEmailAsync(EmailVerificationEmail message, string tenantId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class NoOpAuditService : IAuditService
    {
        public Task LogAsync(
            string tenantId, Guid? userId, string action, string severity = "Info", string? entityType = null,
            string? entityId = null, string? ipAddress = null, string? requestId = null,
            string? oldValueJson = null, string? newValueJson = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task LogSessionAsync(
            Guid tenantId, Guid? userId, string email, string eventType, string? ipAddress,
            string? userAgent, string? failureReason = null, CancellationToken ct = default)
            => Task.CompletedTask;
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
