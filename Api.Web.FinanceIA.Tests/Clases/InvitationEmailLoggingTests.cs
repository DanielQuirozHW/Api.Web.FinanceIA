// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-19

using System.Net;
using System.Security.Claims;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Clases.PasswordReset;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Entities;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// An invitation used to write the invitee's address to the logs three times (controller, service,
/// SendGrid sender) and a resend once more. Every one of those lines now carries the
/// <see cref="EmailHasher"/> tag instead. The capturing logger checks the rendered message AND every
/// structured argument AND any attached exception, because Application Insights stores all three.
/// What is stored in UserInvitations and sent to SendGrid is asserted unchanged.
/// </summary>
public sealed class InvitationEmailLoggingTests
{
    private const string Tenant  = "TenantA";
    private const string Address = "Invitee.Person@Example.com";
    private const string Stored  = "invitee.person@example.com";

    // ── UserInvitationService ────────────────────────────────────────────────────

    [Fact]
    public async Task InviteUser_LogsTheTag_NeverTheAddress_AndStoresAndSendsTheAddress()
    {
        var fixture = new ServiceFixture();

        await fixture.Sut.InviteUserAsync(
            Tenant, Guid.NewGuid(), new InviteUserSettingsRequest { Email = Address, Role = "Analyst" },
            "203.0.113.7", "req-1", CancellationToken.None);

        fixture.Logger.AssertNeverContains(Address);
        Assert.Contains(fixture.Logger.Entries, e => e.Text.Contains(EmailHasher.Hash(Address)));

        UserInvitation stored = Assert.Single(fixture.Db.UserInvitations);
        Assert.Equal(Stored, stored.Email);
        Assert.Equal(Stored, Assert.Single(fixture.Email.Recipients));
    }

    [Fact]
    public async Task ResendInvitation_DoesNotLogTheAddress()
    {
        var fixture    = new ServiceFixture();
        var invitation = fixture.Invitations.Seed(Stored);

        InvitationActionResult result = await fixture.Sut.ResendInvitationAsync(
            Tenant, invitation.InvitationId, Guid.NewGuid(), null, null, CancellationToken.None);

        Assert.Equal(InvitationActionResult.Success, result);
        fixture.Logger.AssertNeverContains(Address);
        Assert.Equal(Stored, Assert.Single(fixture.Email.Recipients));
    }

    // ── SendGridEmailService ─────────────────────────────────────────────────────

    [Fact]
    public async Task SendGrid_Accepted_LogsTheTag_AndSendsToTheAddress()
    {
        var handler = new StubSendGridHandler(HttpStatusCode.Accepted);
        var logger  = new CapturingLogger<SendGridEmailService>();

        await NewSendGrid(handler, logger).SendInvitationEmailAsync(
            Invitation(), Tenant, CancellationToken.None);

        logger.AssertNeverContains(Address);
        Assert.Contains(logger.Entries, e => e.Text.Contains(EmailHasher.Hash(Stored)));
        Assert.Contains(Stored, handler.LastBody);
    }

    [Fact]
    public async Task SendGrid_Rejected_LogsNoAddressEither()
    {
        var logger = new CapturingLogger<SendGridEmailService>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewSendGrid(new StubSendGridHandler(HttpStatusCode.BadRequest), logger).SendInvitationEmailAsync(
                Invitation(), Tenant, CancellationToken.None));

        Assert.NotEmpty(logger.Entries);
        logger.AssertNeverContains(Address);
    }

    // ── SettingsController.InviteUser ────────────────────────────────────────────

    [Fact]
    public async Task InviteUser_Success_LogsTheTag_NeverTheAddress()
    {
        var logger = new CapturingLogger<SettingsController>();

        IActionResult result = await NewController(new StubInvitationService(), logger)
            .InviteUser(new InviteUserSettingsRequest { Email = Address, Role = "Analyst" }, CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        logger.AssertNeverContains(Address);
        Assert.Contains(logger.Entries, e => e.Text.Contains(EmailHasher.Hash(Address)));
    }

    [Fact]
    public async Task InviteUser_Failure_LogsNoAddress()
    {
        var logger = new CapturingLogger<SettingsController>();

        IActionResult result = await NewController(new StubInvitationService { Throws = true }, logger)
            .InviteUser(new InviteUserSettingsRequest { Email = Address, Role = "Analyst" }, CancellationToken.None);

        Assert.Equal(400, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
        logger.AssertNeverContains(Address);
    }

    [Fact]
    public async Task MissingUserIdClaim_LogsClaimTypes_NotTheirValues()
    {
        var logger     = new CapturingLogger<SettingsController>();
        var controller = NewController(new StubInvitationService(), logger, new Claim(ClaimTypes.Email, Address));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            controller.InviteUser(new InviteUserSettingsRequest { Email = "other@example.com", Role = "Analyst" }, CancellationToken.None));

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Text.Contains(ClaimTypes.Email));
        logger.AssertNeverContains(Address);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static InvitationEmail Invitation() => new()
    {
        ToEmail        = Stored,
        InvitationLink = "https://portal/register/invite?token=t",
        InviterName    = "Admin"
    };

    private static SendGridEmailService NewSendGrid(HttpMessageHandler handler, ILogger<SendGridEmailService> logger)
        => new(Config(("SendGrid:ApiKey", "SG.test")), logger, handler);

    private static SettingsController NewController(
        IUserInvitationService invitations,
        ILogger<SettingsController> logger,
        params Claim[] claims)
    {
        var controller = new SettingsController(
            null!,    // ISettingsService
            invitations,
            new StubTenantContextAccessor(),
            null!,    // IUserRepository
            null!,    // IUserApprovalLimitRepository
            null!,    // ITeamRepository
            null!,    // ISgrSettingsService
            logger);

        Claim[] identity = claims.Length > 0
            ? claims
            : [new Claim("userId", Guid.NewGuid().ToString()), new Claim("tenantId", Tenant)];

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(identity, "Internal")) }
        };
        return controller;
    }

    private static IConfiguration Config(params (string Key, string Value)[] settings)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build();

    private sealed class ServiceFixture
    {
        public ServiceFixture()
        {
            Db = new FinanciaDbContext(new DbContextOptionsBuilder<FinanciaDbContext>()
                .UseInMemoryDatabase($"invitation-logging-{Guid.NewGuid()}")
                .Options);
            Invitations = new InMemoryInvitationRepository(Db);
            Settings = new TenantSettingsRepository(Db, NullLogger<TenantSettingsRepository>.Instance);

            Sut = new UserInvitationService(
                Db,
                Invitations,
                new NoInviterUserRepository(),
                Settings,
                Email,
                new NoOpAuditService(),
                Config(("AppSettings:FrontendUrl", "https://portal.example")),
                Logger);
        }

        public FinanciaDbContext Db { get; }
        public InMemoryInvitationRepository Invitations { get; }
        public TenantSettingsRepository Settings { get; }
        public RecordingEmailService Email { get; } = new();
        public CapturingLogger<UserInvitationService> Logger { get; } = new();
        public UserInvitationService Sut { get; }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Text)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            // Rendered message, every structured value (including the template) and the exception.
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

        public void AssertNeverContains(string address)
        {
            Assert.All(Entries, e => Assert.DoesNotContain(address, e.Text, StringComparison.OrdinalIgnoreCase));
        }
    }

    private sealed class StubSendGridHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public string LastBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status);
        }
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

    private sealed class StubInvitationService : IUserInvitationService
    {
        public bool Throws { get; set; }

        public Task<InviteUserResponse> InviteUserAsync(
            string tenantId, Guid invitedByUserId, InviteUserSettingsRequest request,
            string? ipAddress, string? requestId, CancellationToken ct)
        {
            if (Throws)
            {
                throw new InvalidOperationException("Invitation already sent to this email");
            }

            return Task.FromResult(new InviteUserResponse
            {
                InvitationId   = Guid.NewGuid(),
                Email          = request.Email,
                ExpiresAt      = DateTime.UtcNow.AddDays(7),
                InvitationLink = "https://portal/register/invite?token=t"
            });
        }

        public Task<FirstAdminInviteOutcome> InviteFirstAdminAsync(string tenantId, string email, string? ipAddress, string? requestId, CancellationToken ct) => throw new NotImplementedException();
        public Task<UserInvitation?> GetInvitationByTokenAsync(string token, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<InvitationDto>> GetInvitationsAsync(string tenantId, string? status, CancellationToken ct) => throw new NotImplementedException();
        public Task<InvitationActionResult> ResendInvitationAsync(string tenantId, Guid invitationId, Guid resentByUserId, string? ipAddress, string? requestId, CancellationToken ct) => throw new NotImplementedException();
        public Task<InvitationActionResult> RevokeInvitationAsync(string tenantId, Guid invitationId, Guid revokedByUserId, string? ipAddress, string? requestId, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class InMemoryInvitationRepository(FinanciaDbContext db) : IUserInvitationRepository
    {
        public UserInvitation Seed(string email)
        {
            var invitation = new UserInvitation
            {
                InvitationId    = Guid.NewGuid(),
                TenantId        = Tenant,
                Email           = email,
                Role            = "Analyst",
                InvitedByUserId = Guid.NewGuid(),
                Token           = "old-token",
                ExpiresAt       = DateTime.UtcNow.AddDays(1),
                CreatedAt       = DateTime.UtcNow
            };
            db.UserInvitations.Add(invitation);
            db.SaveChanges();
            db.ChangeTracker.Clear();
            return invitation;
        }

        public async Task CreateAsync(UserInvitation invitation, CancellationToken ct)
        {
            db.UserInvitations.Add(invitation);
            await db.SaveChangesAsync(ct);
        }

        public Task<UserInvitation?> GetByIdAsync(Guid invitationId, string tenantId, CancellationToken ct)
            => db.UserInvitations.AsNoTracking()
                .FirstOrDefaultAsync(i => i.InvitationId == invitationId && i.TenantId == tenantId, ct);

        public Task RotateTokenAsync(Guid invitationId, string newToken, DateTime newExpiresAt, CancellationToken ct)
            => Task.CompletedTask;

        public Task<UserInvitation?> GetByTokenAsync(string token, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<UserInvitation>> GetByTenantAsync(string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task MarkAsUsedAsync(Guid invitationId, CancellationToken ct) => throw new NotImplementedException();
        public Task RevokeAsync(Guid invitationId, Guid? revokedByUserId, DateTime revokedAt, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class RecordingEmailService : IEmailService
    {
        public List<string> Recipients { get; } = [];

        public Task SendInvitationEmailAsync(
            InvitationEmail message, string tenantId, CancellationToken cancellationToken)
        {
            Recipients.Add(message.ToEmail);
            return Task.CompletedTask;
        }

        public Task SendPasswordResetEmailAsync(PasswordResetEmail message, string tenantId, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task SendEmailVerificationEmailAsync(EmailVerificationEmail message, string tenantId, CancellationToken cancellationToken)
            => throw new NotImplementedException();
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

    private sealed class NoInviterUserRepository : IUserRepository
    {
        // Inviter lookup: null makes the service fall back to "Un administrador".
        public Task<User?> GetByUserIdAsync(Guid userId, CancellationToken ct) => Task.FromResult<User?>(null);

        public Task<IReadOnlyList<User>> GetByIdsAsync(IEnumerable<Guid> userIds, CancellationToken ct) => throw new NotImplementedException();
        public Task<User?> GetByFirebaseUidAsync(string firebaseUid, CancellationToken ct) => throw new NotImplementedException();
        public Task<User?> GetByEmailAsync(string email, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> ExistsByFirebaseUidAsync(string firebaseUid, CancellationToken ct) => throw new NotImplementedException();
        public Task<User> CreateAsync(User user, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateLastLoginAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateOnboardingFlagsAsync(Guid userId, string? flagsJson, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<User>> GetActiveByTenantAsync(string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateTeamAsync(Guid userId, Guid? teamId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> ClearTeamAsync(Guid teamId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
    }
}
