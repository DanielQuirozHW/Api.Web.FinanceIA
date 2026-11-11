// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-21

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Clases.EmailVerification;
using Api.Web.FinanceIA.Clases.Exceptions;
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
/// The work behind one accepted email-verification request. The REAL
/// <see cref="TenantSettingsRepository"/> runs over an InMemory <see cref="FinanciaDbContext"/>, so the
/// production settings read supplies the tenant's portal, sender and template.
/// <para>
/// Pinned: with no template anywhere nothing is generated or sent (TemplateMissing — there is no
/// hardcoded template id); the link comes from THIS tenant's Firebase app and lands on the Portal's
/// /verify-email, never Firebase's hosted page; Firebase and SendGrid failures never throw; neither
/// the address nor the link is ever logged or audited.
/// </para>
/// </summary>
public sealed class EmailVerificationServiceTests
{
    private const string Tenant    = "TenantA";
    private const string UserEmail = "analyst@example.com";
    private const string OobCode   = "VERIFY-OOB-456";

    private static readonly Guid UserId = Guid.NewGuid();

    private const string FirebaseLink =
        "https://project-a.firebaseapp.com/__/auth/action?mode=verifyEmail&oobCode=" + OobCode +
        "&apiKey=web-key&continueUrl=https%3A%2F%2Fportal.example.com&lang=en";

    // ── Template ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoTemplateAnywhere_IsTemplateMissing_AndNothingIsGeneratedOrSent()
    {
        await using var ctx = NewContext();
        var fx = new Fixture(ctx, defaultTemplateId: null);

        EmailVerificationOutcome outcome = await fx.Sut.ProcessAsync(Item(), CancellationToken.None);

        Assert.Equal(EmailVerificationOutcome.TemplateMissing, outcome);
        Assert.Empty(fx.Firebase.LinkCalls);
        Assert.Empty(fx.Email.Sent);
        Assert.Empty(fx.Audit.Entries);
        Assert.Contains(fx.Logger.Messages, m => m.Contains("TemplateMissing"));
    }

    [Fact]
    public async Task BlankTenantTemplateAndBlankDefault_AreTemplateMissing()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, Setting(TenantSettingKeys.EmailEmailVerificationTemplateId, "   "));
        var fx = new Fixture(ctx, defaultTemplateId: " ");

        Assert.Equal(EmailVerificationOutcome.TemplateMissing, await fx.Sut.ProcessAsync(Item(), CancellationToken.None));
        Assert.Empty(fx.Email.Sent);
    }

    // ── Sent ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sent_LinkFromTheTenantsApp_ToThePortal_OneEmail_OneAuditRow()
    {
        await using var ctx = NewContext();
        var fx = new Fixture(ctx);

        EmailVerificationOutcome outcome = await fx.Sut.ProcessAsync(Item(), CancellationToken.None);

        Assert.Equal(EmailVerificationOutcome.Sent, outcome);

        // Link generated once, with THIS tenant's context and the Portal base URL as continue URL.
        var call = Assert.Single(fx.Firebase.LinkCalls);
        Assert.Equal(UserEmail, call.Email);
        Assert.Equal(Tenant, call.TenantId);
        Assert.Equal("https://portal.example.com", call.ContinueUrl);

        // One email, straight to the Portal with the oobCode — not Firebase's hosted page.
        var (message, tenantId) = Assert.Single(fx.Email.Sent);
        Assert.Equal(Tenant, tenantId);
        Assert.Equal(UserEmail, message.ToEmail);
        Assert.Equal("https://portal.example.com/verify-email?mode=verifyEmail&oobCode=" + OobCode, message.VerifyLink);
        Assert.Equal("Financia", message.ProductName);
        Assert.Equal("d-default-verify", message.TemplateId);
        Assert.Null(message.FromEmail);
        Assert.Null(message.FromName);

        var audit = Assert.Single(fx.Audit.Entries);
        Assert.Equal("User.EmailVerificationRequested", audit.Action);
        Assert.Equal(UserId, audit.UserId);
        Assert.Equal(UserId.ToString(), audit.EntityId);
        Assert.All(audit.AllText, v => Assert.DoesNotContain(UserEmail, v ?? string.Empty));
    }

    [Fact]
    public async Task TenantSettings_BeatTheGlobalDefaults()
    {
        await using var ctx = NewContext();
        await SeedAsync(
            ctx,
            Setting(TenantSettingKeys.EmailEmailVerificationTemplateId, "d-tenant-verify"),
            Setting(TenantSettingKeys.EmailFromAddress, "no-reply@tenant-a.example.com"),
            Setting(TenantSettingKeys.EmailFromName, "Tenant A"),
            Setting(TenantSettingKeys.PortalUrl, "https://tenant-a.example.com/"),
            Setting(TenantSettingKeys.EmailEmailVerificationTemplateId, "d-other-tenant", tenantId: "TenantB"));
        var fx = new Fixture(ctx);

        await fx.Sut.ProcessAsync(Item(), CancellationToken.None);

        var (message, _) = Assert.Single(fx.Email.Sent);
        Assert.Equal("d-tenant-verify", message.TemplateId);
        Assert.Equal("no-reply@tenant-a.example.com", message.FromEmail);
        Assert.Equal("Tenant A", message.FromName);
        Assert.StartsWith("https://tenant-a.example.com/verify-email?", message.VerifyLink);
        Assert.Equal("https://tenant-a.example.com", Assert.Single(fx.Firebase.LinkCalls).ContinueUrl);
    }

    [Fact]
    public async Task TenantSettings_SupplyCompanyNameAndLogo()
    {
        await using var ctx = NewContext();
        await SeedAsync(
            ctx,
            Setting(TenantSettingKeys.BrandingCompanyName, "Movil SGR"),
            Setting(TenantSettingKeys.BrandingLogoUrl, " https://cdn.example.com/movil/logo.png "),
            Setting(TenantSettingKeys.BrandingCompanyName, "Other SGR", tenantId: "TenantB"));
        var fx = new Fixture(ctx);

        Assert.Equal(EmailVerificationOutcome.Sent, await fx.Sut.ProcessAsync(Item(), CancellationToken.None));

        var (message, _) = Assert.Single(fx.Email.Sent);
        Assert.Equal("Movil SGR", message.CompanyName);
        Assert.Equal("https://cdn.example.com/movil/logo.png", message.LogoUrl);
    }

    [Fact]
    public async Task NoBrandingRows_SendNoBranding()
    {
        await using var ctx = NewContext();
        var fx = new Fixture(ctx);

        Assert.Equal(EmailVerificationOutcome.Sent, await fx.Sut.ProcessAsync(Item(), CancellationToken.None));

        var (message, _) = Assert.Single(fx.Email.Sent);
        Assert.Null(message.CompanyName);
        Assert.Null(message.LogoUrl);
    }

    [Theory]
    [InlineData("http://cdn.example.com/logo.png")]
    [InlineData("logo.png")]
    public async Task NonHttpsLogo_IsIgnored_WithAWarning_AndTheEmailStillGoes(string logoUrl)
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, Setting(TenantSettingKeys.BrandingLogoUrl, logoUrl));
        var fx = new Fixture(ctx);

        Assert.Equal(EmailVerificationOutcome.Sent, await fx.Sut.ProcessAsync(Item(), CancellationToken.None));

        Assert.Null(Assert.Single(fx.Email.Sent).Message.LogoUrl);
        Assert.Contains(fx.Logger.Messages, m => m.Contains("branding.logo_url"));
    }

    [Fact]
    public async Task TenantSettingsReadFailure_StillSends_OnTheGlobalDefaults()
    {
        await using var ctx = NewContext();
        var fx = new Fixture(ctx, settings: new ThrowingTenantSettingsRepository());

        Assert.Equal(EmailVerificationOutcome.Sent, await fx.Sut.ProcessAsync(Item(), CancellationToken.None));

        var (message, _) = Assert.Single(fx.Email.Sent);
        Assert.Equal("d-default-verify", message.TemplateId);
        Assert.Null(message.CompanyName);
        Assert.Null(message.LogoUrl);
        Assert.Contains(fx.Logger.Messages, m => m.Contains("TenantSettings read failed for email verification in tenant TenantA"));
    }

    [Fact]
    public async Task TenantTemplateAlone_IsEnough_WithoutAGlobalDefault()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, Setting(TenantSettingKeys.EmailEmailVerificationTemplateId, "d-tenant-verify"));
        var fx = new Fixture(ctx, defaultTemplateId: null);

        Assert.Equal(EmailVerificationOutcome.Sent, await fx.Sut.ProcessAsync(Item(), CancellationToken.None));
    }

    [Fact]
    public async Task UnlistedTenantPortalUrl_IsIgnored_ForTheDefault()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, Setting(TenantSettingKeys.PortalUrl, "https://evil.example.net"));
        var fx = new Fixture(ctx);

        await fx.Sut.ProcessAsync(Item(), CancellationToken.None);

        Assert.StartsWith("https://portal.example.com/verify-email?", Assert.Single(fx.Email.Sent).Message.VerifyLink);
    }

    // ── Failures never throw ─────────────────────────────────────────────────────

    [Fact]
    public async Task NoFirebaseAccount_IsLinkFailed_AndNothingIsSent()
    {
        await using var ctx = NewContext();
        var fx = new Fixture(ctx);
        fx.Firebase.LinkResult = null;

        Assert.Equal(EmailVerificationOutcome.LinkFailed, await fx.Sut.ProcessAsync(Item(), CancellationToken.None));
        Assert.Empty(fx.Email.Sent);
        Assert.Empty(fx.Audit.Entries);
    }

    [Fact]
    public async Task FirebaseThrows_IsLinkFailed_LoggedAtErrorWithoutTheAddress()
    {
        await using var ctx = NewContext();
        var fx = new Fixture(ctx);
        fx.Firebase.LinkThrow = new FirebaseServiceException($"Firebase failed for {UserEmail}");

        Assert.Equal(EmailVerificationOutcome.LinkFailed, await fx.Sut.ProcessAsync(Item(), CancellationToken.None));
        Assert.Contains(fx.Logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Empty(fx.Email.Sent);
        fx.AssertNoSecretLogged();
    }

    [Fact]
    public async Task LinkWithoutOobCode_IsLinkFailed()
    {
        await using var ctx = NewContext();
        var fx = new Fixture(ctx);
        fx.Firebase.LinkResult = "https://project-a.firebaseapp.com/__/auth/action?mode=verifyEmail";

        Assert.Equal(EmailVerificationOutcome.LinkFailed, await fx.Sut.ProcessAsync(Item(), CancellationToken.None));
        Assert.Empty(fx.Email.Sent);
    }

    [Fact]
    public async Task SendGridThrows_IsSendFailed_AndNotAudited()
    {
        await using var ctx = NewContext();
        var fx = new Fixture(ctx);
        fx.Email.Throw = new InvalidOperationException("Failed to send email: BadRequest");

        Assert.Equal(EmailVerificationOutcome.SendFailed, await fx.Sut.ProcessAsync(Item(), CancellationToken.None));
        Assert.Empty(fx.Audit.Entries);
        Assert.Contains(fx.Logger.Entries, e => e.Level == LogLevel.Error);
    }

    // ── Logs ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sent_LogsTheOutcomeWithUserAndTenant_NeverTheAddressOrTheLink()
    {
        await using var ctx = NewContext();
        var fx = new Fixture(ctx);

        await fx.Sut.ProcessAsync(Item(), CancellationToken.None);

        var line = Assert.Single(fx.Logger.Messages, m => m.Contains("Sent"));
        Assert.Contains(UserId.ToString(), line);
        Assert.Contains(Tenant, line);
        fx.AssertNoSecretLogged();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static EmailVerificationWorkItem Item() => new(
        new TenantContext
        {
            TenantId               = Tenant,
            FirebaseProjectId      = "project-a",
            FirebaseCredentialJson = "{}",
            RecaptchaSecretKey     = "secret"
        },
        UserId,
        UserEmail,
        "203.0.113.7",
        "trace-1");

    private static FinanciaDbContext NewContext()
        => new(new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"email-verification-{Guid.NewGuid()}")
            .Options);

    private static TenantSettings Setting(string key, string value, string tenantId = Tenant) => new()
    {
        SettingId    = Guid.NewGuid(),
        TenantId     = tenantId,
        SettingKey   = key,
        SettingValue = value,
        UpdatedAt    = DateTime.UtcNow
    };

    private static async Task SeedAsync(FinanciaDbContext ctx, params TenantSettings[] settings)
    {
        ctx.TenantSettings.AddRange(settings);
        ctx.ChangeTracker.DetectChanges(); // AutoDetectChanges is disabled on the context
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();
    }

    private sealed class Fixture
    {
        public Fixture(
            FinanciaDbContext ctx,
            string? defaultTemplateId = "d-default-verify",
            ITenantSettingsRepository? settings = null)
        {
            Sut = new EmailVerificationService(
                settings ?? new TenantSettingsRepository(ctx, NullLogger<TenantSettingsRepository>.Instance),
                Firebase,
                Email,
                Audit,
                Options.Create(new EmailVerificationOptions
                {
                    DefaultPortalUrl     = "https://portal.example.com/",
                    AllowedPortalOrigins = ["https://portal.example.com/", "https://tenant-a.example.com"],
                    DefaultTemplateId    = defaultTemplateId
                }),
                Logger);
        }

        public EmailVerificationService Sut { get; }
        public RecordingFirebaseUserAdmin Firebase { get; } = new();
        public RecordingEmailService Email { get; } = new();
        public RecordingAuditService Audit { get; } = new();
        public RecordingLogger<EmailVerificationService> Logger { get; } = new();

        public void AssertNoSecretLogged()
        {
            Assert.All(Logger.Messages, m =>
            {
                Assert.DoesNotContain(UserEmail, m, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(OobCode, m);
                Assert.DoesNotContain("verify-email?", m);
            });
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

    private sealed class RecordingFirebaseUserAdmin : ITenantFirebaseUserAdmin
    {
        public string? LinkResult { get; set; } = FirebaseLink;
        public Exception? LinkThrow { get; set; }
        public List<(string Email, string ContinueUrl, string TenantId)> LinkCalls { get; } = [];

        public Task<bool?> GetEmailVerifiedAsync(string firebaseUid, TenantContext tenantContext, CancellationToken cancellationToken)
            => throw new NotSupportedException("The worker must not re-read the verified flag.");

        public Task MarkEmailVerifiedAsync(string firebaseUid, TenantContext tenantContext, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<string?> GenerateEmailVerificationLinkAsync(
            string email, string continueUrl, TenantContext tenantContext, CancellationToken cancellationToken)
        {
            LinkCalls.Add((email, continueUrl, tenantContext.TenantId));
            if (LinkThrow is not null) throw LinkThrow;
            return Task.FromResult(LinkResult);
        }
    }

    private sealed class RecordingEmailService : IEmailService
    {
        public Exception? Throw { get; set; }
        public List<(EmailVerificationEmail Message, string TenantId)> Sent { get; } = [];

        public Task SendInvitationEmailAsync(
            InvitationEmail message, string tenantId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task SendPasswordResetEmailAsync(PasswordResetEmail message, string tenantId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task SendEmailVerificationEmailAsync(
            EmailVerificationEmail message, string tenantId, CancellationToken cancellationToken)
        {
            if (Throw is not null) throw Throw;
            Sent.Add((message, tenantId));
            return Task.CompletedTask;
        }
    }

    private sealed record AuditEntry(string Action, Guid? UserId, string? EntityId, string?[] AllText);

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
                action, userId, entityId,
                [tenantId, action, severity, entityType, entityId, ipAddress, requestId, oldValueJson, newValueJson]));
            return Task.CompletedTask;
        }

        public Task LogSessionAsync(
            Guid tenantId, Guid? userId, string email, string eventType, string? ipAddress,
            string? userAgent, string? failureReason = null, CancellationToken ct = default)
            => throw new NotSupportedException("Email verification must not write SessionLogs (they store the raw email).");
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
        {
            // Rendered message plus every structured value and the exception.
            var parts = new List<string> { formatter(state, exception) };
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                parts.AddRange(values.Select(v => v.Value?.ToString() ?? string.Empty));
            }

            if (exception is not null)
            {
                parts.Add(exception.ToString());
            }

            Entries.Add((logLevel, string.Join(" | ", parts)));
        }
    }
}
