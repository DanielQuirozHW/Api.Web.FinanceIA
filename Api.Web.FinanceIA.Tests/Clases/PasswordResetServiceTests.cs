// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-19

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Clases.Exceptions;
using Api.Web.FinanceIA.Clases.PasswordReset;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// The work behind one accepted password-reset request. The REAL <see cref="UserRepository"/> and
/// <see cref="TenantSettingsRepository"/> run over an InMemory <see cref="FinanciaDbContext"/>, so
/// it is the production lookup that decides "unknown / inactive / other tenant", and the production
/// settings read that supplies the tenant's portal, sender and template.
/// <para>
/// Pinned: nothing is generated or sent unless the address is an ACTIVE user of THIS tenant; the
/// link comes from THIS tenant's Firebase app and lands on the Portal's /recover; Firebase and
/// SendGrid failures never throw and are logged at Error; neither the address nor the link is
/// ever logged or audited.
/// </para>
/// </summary>
public sealed class PasswordResetServiceTests
{
    private const string Tenant      = "TenantA";
    private const string OtherTenant = "TenantB";
    private const string UserEmail   = "analyst@example.com";
    private const string OobCode     = "OOB-CODE-123";

    private const string FirebaseLink =
        "https://project-a.firebaseapp.com/__/auth/action?mode=resetPassword&oobCode=" + OobCode +
        "&apiKey=web-key&continueUrl=https%3A%2F%2Fportal.example.com%2Frecover&lang=en";

    // ── Nothing sent ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task UnknownEmail_GeneratesAndSendsNothing()
    {
        await using var ctx = NewContext();
        var fx = new Fixture(ctx);

        PasswordResetOutcome outcome = await fx.Sut.ProcessAsync(Item("nobody@example.com"), CancellationToken.None);

        Assert.Equal(PasswordResetOutcome.UnknownEmail, outcome);
        fx.AssertNothingHappened();
    }

    [Fact]
    public async Task InactiveUser_GeneratesAndSendsNothing()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, NewUser(isActive: false));
        var fx = new Fixture(ctx);

        PasswordResetOutcome outcome = await fx.Sut.ProcessAsync(Item(UserEmail), CancellationToken.None);

        Assert.Equal(PasswordResetOutcome.InactiveUser, outcome);
        fx.AssertNothingHappened();
    }

    [Fact]
    public async Task UserOfAnotherTenant_ReadsAsUnknown()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, NewUser(tenantId: OtherTenant));
        var fx = new Fixture(ctx);

        PasswordResetOutcome outcome = await fx.Sut.ProcessAsync(Item(UserEmail), CancellationToken.None);

        Assert.Equal(PasswordResetOutcome.UnknownEmail, outcome);
        fx.AssertNothingHappened();
    }

    [Fact]
    public async Task NoFirebaseAccount_SendsNothing()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, NewUser());
        var fx = new Fixture(ctx);
        fx.Links.Result = null;

        PasswordResetOutcome outcome = await fx.Sut.ProcessAsync(Item(UserEmail), CancellationToken.None);

        Assert.Equal(PasswordResetOutcome.FirebaseUserMissing, outcome);
        Assert.Empty(fx.Email.Sent);
        Assert.Empty(fx.Audit.Entries);
    }

    // ── Known active user ────────────────────────────────────────────────────────

    [Fact]
    public async Task KnownActiveUser_LinkFromTheTenantsApp_OneEmail_OneAuditRow()
    {
        await using var ctx = NewContext();
        User user = NewUser();
        await SeedAsync(ctx, user);
        var fx = new Fixture(ctx);

        PasswordResetOutcome outcome = await fx.Sut.ProcessAsync(Item(UserEmail), CancellationToken.None);

        Assert.Equal(PasswordResetOutcome.Sent, outcome);

        // Link generated once, with THIS tenant's context and the Portal /recover as continue URL.
        var call = Assert.Single(fx.Links.Calls);
        Assert.Equal(UserEmail, call.Email);
        Assert.Equal(Tenant, call.TenantContext.TenantId);
        Assert.Equal("https://portal.example.com/recover", call.ContinueUrl);

        // One email, straight to the Portal with the oobCode — not Firebase's hosted page.
        var (message, tenantId) = Assert.Single(fx.Email.Sent);
        Assert.Equal(Tenant, tenantId);
        Assert.Equal(UserEmail, message.ToEmail);
        Assert.Equal(
            "https://portal.example.com/recover?mode=resetPassword&oobCode=" + OobCode + "&lang=es-AR",
            message.ResetLink);
        Assert.Equal("Financia", message.ProductName);
        Assert.Equal("1 hora", message.ExpiresInText);
        Assert.Null(message.FromEmail);   // no tenant rows → global SendGrid config
        Assert.Null(message.TemplateId);

        // One audit row, identifying the user by id only.
        var entry = Assert.Single(fx.Audit.Entries);
        Assert.Equal("User.PasswordResetRequested", entry.Action);
        Assert.Equal(Tenant, entry.TenantId);
        Assert.Equal(user.UserId, entry.UserId);
        Assert.Equal("User", entry.EntityType);
        Assert.Equal(user.UserId.ToString(), entry.EntityId);
        Assert.Equal("198.51.100.4", entry.IpAddress);
        Assert.DoesNotContain(entry.AllText, t => t?.Contains("example.com", StringComparison.OrdinalIgnoreCase) == true);

        // Logs carry neither the address nor the link.
        Assert.NotEmpty(fx.Logger.Messages);
        Assert.DoesNotContain(fx.Logger.Messages, m => m.Contains(UserEmail, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fx.Logger.Messages, m => m.Contains(OobCode, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TenantSettings_SupplyPortalSenderAndTemplate()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, NewUser(),
            Setting(TenantSettingKeys.PortalUrl, "https://tenant-a.example.com/"),
            Setting(TenantSettingKeys.EmailFromAddress, "no-reply@tenant-a.example.com"),
            Setting(TenantSettingKeys.EmailFromName, "Tenant A"),
            Setting(TenantSettingKeys.EmailPasswordResetTemplateId, "d-tenant-a"),
            Setting(TenantSettingKeys.PortalUrl, "https://evil.example.net", OtherTenant));
        var fx = new Fixture(ctx);

        Assert.Equal(PasswordResetOutcome.Sent, await fx.Sut.ProcessAsync(Item(UserEmail), CancellationToken.None));

        Assert.Equal("https://tenant-a.example.com/recover", Assert.Single(fx.Links.Calls).ContinueUrl);
        var (message, _) = Assert.Single(fx.Email.Sent);
        Assert.StartsWith("https://tenant-a.example.com/recover?mode=resetPassword&oobCode=", message.ResetLink);
        Assert.Equal("no-reply@tenant-a.example.com", message.FromEmail);
        Assert.Equal("Tenant A", message.FromName);
        Assert.Equal("d-tenant-a", message.TemplateId);
    }

    // ── Branding ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TenantSettings_SupplyCompanyNameAndLogo()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, NewUser(),
            Setting(TenantSettingKeys.BrandingCompanyName, "  Movil SGR "),
            Setting(TenantSettingKeys.BrandingLogoUrl, "https://cdn.example.com/movil/logo.png"),
            Setting(TenantSettingKeys.BrandingCompanyName, "Other SGR", OtherTenant));
        var fx = new Fixture(ctx);

        Assert.Equal(PasswordResetOutcome.Sent, await fx.Sut.ProcessAsync(Item(UserEmail), CancellationToken.None));

        var (message, _) = Assert.Single(fx.Email.Sent);
        Assert.Equal("Movil SGR", message.CompanyName);
        Assert.Equal("https://cdn.example.com/movil/logo.png", message.LogoUrl);
    }

    [Fact]
    public async Task NoBrandingRows_SendNoBranding()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, NewUser(), Setting(TenantSettingKeys.BrandingCompanyName, "   "));
        var fx = new Fixture(ctx);

        Assert.Equal(PasswordResetOutcome.Sent, await fx.Sut.ProcessAsync(Item(UserEmail), CancellationToken.None));

        var (message, _) = Assert.Single(fx.Email.Sent);
        Assert.Null(message.CompanyName);
        Assert.Null(message.LogoUrl);
    }

    [Theory]
    [InlineData("http://cdn.example.com/logo.png")]     // not https
    [InlineData("/assets/logo.png")]                    // relative
    [InlineData("ftp://cdn.example.com/logo.png")]
    [InlineData("not a url")]
    public async Task NonHttpsLogo_IsIgnored_WithAWarning_AndTheEmailStillGoes(string logoUrl)
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, NewUser(), Setting(TenantSettingKeys.BrandingLogoUrl, logoUrl));
        var fx = new Fixture(ctx);

        Assert.Equal(PasswordResetOutcome.Sent, await fx.Sut.ProcessAsync(Item(UserEmail), CancellationToken.None));

        Assert.Null(Assert.Single(fx.Email.Sent).Message.LogoUrl);
        Assert.Contains(fx.Logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("branding.logo_url"));
    }

    [Fact]
    public async Task TenantSettingsReadFailure_StillSends_OnTheGlobalDefaults()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, NewUser());
        var fx = new Fixture(ctx, new ThrowingTenantSettingsRepository());

        Assert.Equal(PasswordResetOutcome.Sent, await fx.Sut.ProcessAsync(Item(UserEmail), CancellationToken.None));

        var (message, _) = Assert.Single(fx.Email.Sent);
        Assert.Null(message.FromEmail);
        Assert.Null(message.TemplateId);
        Assert.Null(message.CompanyName);
        Assert.Null(message.LogoUrl);
        Assert.Contains(fx.Logger.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("TenantSettings read failed for password reset in tenant TenantA"));
    }

    [Theory]
    [InlineData("https://evil.example.net")]            // origin not in Cors:AllowedOrigins
    [InlineData("http://tenant-a.example.com")]          // allowed host, but not https
    [InlineData("https://tenant-a.example.com/?x=1")]    // query string would ride the link
    [InlineData("not a url")]
    public async Task DisallowedTenantPortalUrl_FallsBackToFrontendUrl_WithAWarning(string portalUrl)
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, NewUser(), Setting(TenantSettingKeys.PortalUrl, portalUrl));
        var fx = new Fixture(ctx);

        Assert.Equal(PasswordResetOutcome.Sent, await fx.Sut.ProcessAsync(Item(UserEmail), CancellationToken.None));

        Assert.Equal("https://portal.example.com/recover", Assert.Single(fx.Links.Calls).ContinueUrl);
        Assert.Contains(fx.Logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("portal.url"));
    }

    // ── Provider failures: never thrown, always logged ───────────────────────────

    [Fact]
    public async Task FirebaseFailure_SendsNothing_DoesNotThrow_LogsError()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, NewUser());
        var fx = new Fixture(ctx);
        fx.Links.Throw = new FirebaseServiceException("boom");

        PasswordResetOutcome outcome = await fx.Sut.ProcessAsync(Item(UserEmail), CancellationToken.None);

        Assert.Equal(PasswordResetOutcome.LinkFailed, outcome);
        Assert.Empty(fx.Email.Sent);
        Assert.Empty(fx.Audit.Entries);
        Assert.Contains(fx.Logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task SendGridFailure_DoesNotThrow_LogsError_AndWritesNoAudit()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, NewUser());
        var fx = new Fixture(ctx);
        fx.Email.Throw = new InvalidOperationException("Failed to send email: Unauthorized");

        PasswordResetOutcome outcome = await fx.Sut.ProcessAsync(Item(UserEmail), CancellationToken.None);

        Assert.Equal(PasswordResetOutcome.SendFailed, outcome);
        Assert.Empty(fx.Audit.Entries);
        Assert.Contains(fx.Logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task FirebaseLinkWithoutOobCode_IsAFailure_NotABrokenEmail()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, NewUser());
        var fx = new Fixture(ctx);
        fx.Links.Result = "https://project-a.firebaseapp.com/__/auth/action?mode=resetPassword";

        Assert.Equal(PasswordResetOutcome.LinkFailed, await fx.Sut.ProcessAsync(Item(UserEmail), CancellationToken.None));
        Assert.Empty(fx.Email.Sent);
    }

    // ── Pure helpers ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://portal.example.com", true, "https://portal.example.com")]
    [InlineData("https://PORTAL.example.com/app/", true, "https://portal.example.com/app")]
    [InlineData("http://localhost:3000", true, "http://localhost:3000")]
    [InlineData("https://portal.example.com.evil.net", false, "")]
    [InlineData("https://user@portal.example.com", false, "")]
    [InlineData("https://portal.example.com/#frag", false, "")]
    [InlineData("ftp://portal.example.com", false, "")]
    public void IsAllowedPortalUrl_ChecksSchemeAndOrigin(string candidate, bool expected, string expectedBase)
    {
        string[] allowed = ["https://portal.example.com/", "http://localhost:3000"];

        Assert.Equal(expected, PasswordResetService.IsAllowedPortalUrl(candidate, allowed, out var normalised));
        Assert.Equal(expectedBase, normalised);
    }

    [Fact]
    public void ExtractOobCode_ReadsTheQueryValue()
    {
        Assert.Equal(OobCode, PasswordResetService.ExtractOobCode(FirebaseLink));
        Assert.Null(PasswordResetService.ExtractOobCode("https://x.example.com/?mode=resetPassword"));
        Assert.Null(PasswordResetService.ExtractOobCode("not a url"));
    }

    // ── Fixture ──────────────────────────────────────────────────────────────────

    private static PasswordResetWorkItem Item(string email) => new(
        new TenantContext
        {
            TenantId               = Tenant,
            FirebaseProjectId      = "project-a",
            FirebaseCredentialJson = "{}",
            RecaptchaSecretKey     = "secret"
        },
        email,
        "198.51.100.4",
        "trace-1");

    private static FinanciaDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"password-reset-{Guid.NewGuid()}")
            .Options;
        return new FinanciaDbContext(options);
    }

    private static User NewUser(string tenantId = Tenant, bool isActive = true) => new()
    {
        UserId      = Guid.NewGuid(),
        TenantId    = tenantId,
        FirebaseUid = "firebase-uid-1",
        Email       = UserEmail,
        DisplayName = "Analyst",
        Role        = Roles.Analyst,
        IsActive    = isActive,
        CreatedAt   = DateTime.UtcNow.AddDays(-10),
        UpdatedAt   = DateTime.UtcNow.AddDays(-10)
    };

    private static TenantSettings Setting(string key, string value, string tenantId = Tenant) => new()
    {
        SettingId    = Guid.NewGuid(),
        TenantId     = tenantId,
        SettingKey   = key,
        SettingValue = value,
        UpdatedAt    = DateTime.UtcNow
    };

    private static async Task SeedAsync(FinanciaDbContext ctx, User user, params TenantSettings[] settings)
    {
        ctx.Users.Add(user);
        ctx.TenantSettings.AddRange(settings);
        ctx.ChangeTracker.DetectChanges(); // AutoDetectChanges is disabled on the context
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();
    }

    private sealed class Fixture
    {
        public Fixture(FinanciaDbContext ctx, ITenantSettingsRepository? settings = null)
        {
            Sut = new PasswordResetService(
                new UserRepository(ctx, NullLogger<UserRepository>.Instance),
                settings ?? new TenantSettingsRepository(ctx, NullLogger<TenantSettingsRepository>.Instance),
                Links,
                Email,
                Audit,
                Options.Create(new PasswordResetOptions
                {
                    DefaultPortalUrl     = "https://portal.example.com/",
                    AllowedPortalOrigins = ["https://portal.example.com/", "https://tenant-a.example.com"]
                }),
                Logger);
        }

        public PasswordResetService Sut { get; }
        public RecordingLinkGenerator Links { get; } = new();
        public RecordingEmailService Email { get; } = new();
        public RecordingAuditService Audit { get; } = new();
        public RecordingLogger<PasswordResetService> Logger { get; } = new();

        public void AssertNothingHappened()
        {
            Assert.Empty(Links.Calls);
            Assert.Empty(Email.Sent);
            Assert.Empty(Audit.Entries);
        }
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

    private sealed class RecordingLinkGenerator : IPasswordResetLinkGenerator
    {
        public string? Result { get; set; } = FirebaseLink;
        public Exception? Throw { get; set; }
        public List<(string Email, string ContinueUrl, TenantContext TenantContext)> Calls { get; } = [];

        public Task<string?> GenerateAsync(
            string email, string continueUrl, TenantContext tenantContext, CancellationToken cancellationToken)
        {
            Calls.Add((email, continueUrl, tenantContext));
            if (Throw is not null) throw Throw;
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingEmailService : IEmailService
    {
        public Exception? Throw { get; set; }
        public List<(PasswordResetEmail Message, string TenantId)> Sent { get; } = [];

        public Task SendInvitationEmailAsync(
            InvitationEmail message, string tenantId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task SendPasswordResetEmailAsync(
            PasswordResetEmail message, string tenantId, CancellationToken cancellationToken)
        {
            if (Throw is not null) throw Throw;
            Sent.Add((message, tenantId));
            return Task.CompletedTask;
        }

        public Task SendEmailVerificationEmailAsync(
            EmailVerificationEmail message, string tenantId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed record AuditEntry(
        string TenantId, Guid? UserId, string Action, string? EntityType, string? EntityId,
        string? IpAddress, string?[] AllText);

    private sealed class RecordingAuditService : IAuditService
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task LogAsync(
            string tenantId, Guid? userId, string action, string severity = "Info",
            string? entityType = null, string? entityId = null, string? ipAddress = null,
            string? requestId = null, string? oldValueJson = null, string? newValueJson = null,
            CancellationToken ct = default)
        {
            Entries.Add(new AuditEntry(
                tenantId, userId, action, entityType, entityId, ipAddress,
                [tenantId, action, severity, entityType, entityId, ipAddress, requestId, oldValueJson, newValueJson]));
            return Task.CompletedTask;
        }

        public Task LogSessionAsync(
            Guid tenantId, Guid? userId, string email, string eventType, string? ipAddress,
            string? userAgent, string? failureReason = null, CancellationToken ct = default)
            => throw new NotSupportedException("A password reset must not write SessionLogs (they store the raw email).");
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IEnumerable<string> Messages => Entries.Select(e => e.Message);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
