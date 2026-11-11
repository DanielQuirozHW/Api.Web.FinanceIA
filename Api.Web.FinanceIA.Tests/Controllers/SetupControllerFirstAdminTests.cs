// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-25

using System.Reflection;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Clases.Exceptions;
using Api.Web.FinanceIA.Clases.Filters;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Entities;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// POST /api/bff/setup/first-admin — the one way to invite the first administrator of a tenant
/// whose database has no active Admin. The gate (404 off / 401 bad key) is the resource filter;
/// the rest runs the REAL <see cref="UserInvitationService"/> and repositories over an InMemory
/// <see cref="FinanciaDbContext"/>, so it is the production invitation path that is exercised.
/// </summary>
public sealed class SetupControllerFirstAdminTests
{
    private const string Tenant      = "TenantA";
    private const string OtherTenant = "TenantB";
    private const string AdminMail   = "admin@client.example";
    private const string PortalUrl   = "https://portal.example";
    private const string ValidKey    = "0123456789abcdef0123456789abcdef-key";

    // ── Gate: the endpoint is off unless a long-enough key is configured ─────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0123456789abcdef0123456789abcde")] // 31 chars
    public async Task Gate_KeyUnsetOrShort_Answers404_AndNeverRunsTheAction(string? configuredKey)
    {
        (bool nextCalled, IActionResult? result) = await RunGateAsync(configuredKey, suppliedKey: configuredKey);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(result).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong-key")]
    [InlineData(ValidKey + "x")]
    public async Task Gate_MissingOrWrongHeader_Answers401_AndNeverRunsTheAction(string? suppliedKey)
    {
        (bool nextCalled, IActionResult? result) = await RunGateAsync(ValidKey, suppliedKey);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsType<StatusCodeResult>(result).StatusCode);
    }

    [Fact]
    public async Task Gate_ExactKey_LetsTheRequestThrough()
    {
        (bool nextCalled, IActionResult? result) = await RunGateAsync(ValidKey, ValidKey);

        Assert.True(nextCalled);
        Assert.Null(result);
    }

    [Fact]
    public void Gate_KeyOfExactlyTheMinimumLength_TurnsTheEndpointOn()
    {
        Assert.True(SetupKeyGateFilter.IsEnabled(new string('k', SetupOptions.MinimumBootstrapKeyLength)));
        Assert.False(SetupKeyGateFilter.IsEnabled(new string('k', SetupOptions.MinimumBootstrapKeyLength - 1)));
    }

    [Fact]
    public void Endpoint_IsAnonymous_GatedBeforeModelBinding_AndOnTheDocumentedRoute()
    {
        MethodInfo action = typeof(SetupController).GetMethod(nameof(SetupController.InviteFirstAdminAsync))!;

        Assert.NotNull(typeof(SetupController).GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Equal("api/bff/setup", typeof(SetupController).GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.Equal("first-admin", action.GetCustomAttribute<HttpPostAttribute>()!.Template);

        var gate = action.GetCustomAttribute<SetupKeyGateAttribute>();
        Assert.NotNull(gate);
        Assert.Equal(typeof(SetupKeyGateFilter), gate.ImplementationType);
        // A RESOURCE filter runs before model binding: a malformed body cannot reveal the route.
        Assert.True(typeof(IAsyncResourceFilter).IsAssignableFrom(gate.ImplementationType));
    }

    // ── Tenant and body ──────────────────────────────────────────────────────────

    [Fact]
    public async Task NoTenantHeader_Answers400_EvenWhenATenantWasResolvedFromAJwt()
    {
        await using var db = NewContext();

        IActionResult result = await NewController(db, new RecordingEmailService(), tenantHeader: null)
            .InviteFirstAdminAsync(new FirstAdminSetupRequest { Email = AdminMail }, CancellationToken.None);

        AssertProblem(result, 400, "tenant_resolution_error");
        Assert.Empty(await db.UserInvitations.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    [InlineData("Admin <admin@client.example>")]
    [InlineData(" admin@client.example ")] // rejected exactly like the Settings invite
    public async Task MissingOrInvalidEmail_Answers400_AndCreatesNothing(string? email)
    {
        await using var db = NewContext();

        IActionResult result = await NewController(db, new RecordingEmailService())
            .InviteFirstAdminAsync(new FirstAdminSetupRequest { Email = email }, CancellationToken.None);

        Assert.Equal(400, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        Assert.Empty(await db.UserInvitations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task EmptyBody_Answers400()
    {
        await using var db = NewContext();

        IActionResult result = await NewController(db, new RecordingEmailService())
            .InviteFirstAdminAsync(null, CancellationToken.None);

        Assert.Equal(400, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
    }

    // ── Who blocks the bootstrap ─────────────────────────────────────────────────

    [Fact]
    public async Task ActiveAdmin_Answers409_AdminAlreadyExists_AndCreatesNothing()
    {
        await using var db = NewContext();
        await SeedAsync(db, users: [NewUser(Tenant, Roles.Admin, isActive: true)]);
        var email = new RecordingEmailService();

        IActionResult result = await NewController(db, email)
            .InviteFirstAdminAsync(new FirstAdminSetupRequest { Email = AdminMail }, CancellationToken.None);

        var problem = AssertProblem(result, 409, "admin_already_exists");
        Assert.Equal("La organización ya tiene un administrador.", problem.Detail);
        Assert.Empty(await db.UserInvitations.AsNoTracking().ToListAsync());
        Assert.Empty(email.Sent);
    }

    public static TheoryData<string, bool> NonBlockingUsers => new()
    {
        { Roles.Admin,       false },
        { Roles.Analyst,     true  },
        { Roles.RiskManager, true  },
        { Roles.Auditor,     true  },
    };

    [Theory]
    [MemberData(nameof(NonBlockingUsers))]
    public async Task InactiveAdminOrOtherRoles_DoNotBlock(string role, bool isActive)
    {
        await using var db = NewContext();
        await SeedAsync(db, users: [NewUser(Tenant, role, isActive)]);

        IActionResult result = await NewController(db, new RecordingEmailService())
            .InviteFirstAdminAsync(new FirstAdminSetupRequest { Email = AdminMail }, CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public async Task ActiveAdminOfAnotherTenant_DoesNotBlock_AndTheInvitationLandsInThisTenantOnly()
    {
        await using var db = NewContext();
        await SeedAsync(db, users: [NewUser(OtherTenant, Roles.Admin, isActive: true)]);

        IActionResult result = await NewController(db, new RecordingEmailService())
            .InviteFirstAdminAsync(new FirstAdminSetupRequest { Email = AdminMail }, CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        UserInvitation created = await db.UserInvitations.AsNoTracking().SingleAsync();
        Assert.Equal(Tenant, created.TenantId);
    }

    [Fact]
    public async Task ExistingUserWithThatEmail_Answers409_UserAlreadyExists()
    {
        await using var db = NewContext();
        var user = NewUser(Tenant, Roles.Analyst, isActive: true);
        user.Email = AdminMail;
        await SeedAsync(db, users: [user]);

        IActionResult result = await NewController(db, new RecordingEmailService())
            .InviteFirstAdminAsync(new FirstAdminSetupRequest { Email = AdminMail }, CancellationToken.None);

        AssertProblem(result, 409, InvitationConflictException.UserAlreadyExists);
        Assert.Empty(await db.UserInvitations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task LiveInvitationSentByAnAdmin_Answers409_InvitationPending_AndIsLeftAlone()
    {
        await using var db = NewContext();
        var adminSent = NewInvitation(invitedBy: Guid.NewGuid());
        await SeedAsync(db, invitations: [adminSent]);

        IActionResult result = await NewController(db, new RecordingEmailService())
            .InviteFirstAdminAsync(new FirstAdminSetupRequest { Email = AdminMail }, CancellationToken.None);

        AssertProblem(result, 409, InvitationConflictException.InvitationPending);
        UserInvitation untouched = await db.UserInvitations.AsNoTracking().SingleAsync();
        Assert.Null(untouched.RevokedAt);
    }

    // ── Happy path ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task EmptyTenant_Answers201_WithAnAdminInvitationWithoutInviter_AndSendsTheSameEmail()
    {
        await using var db = NewContext();
        var email = new RecordingEmailService();
        var audit = new RecordingAuditService();

        IActionResult result = await NewController(db, email, audit: audit)
            .InviteFirstAdminAsync(new FirstAdminSetupRequest { Email = "Admin@Client.EXAMPLE" }, CancellationToken.None);

        var created = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status201Created, created.StatusCode);
        var body = Assert.IsType<FirstAdminInvitationResponse>(created.Value);

        UserInvitation row = await db.UserInvitations.AsNoTracking().SingleAsync();
        Assert.Equal(body.InvitationId, row.InvitationId);
        Assert.Equal(Tenant, row.TenantId);
        Assert.Equal(AdminMail, row.Email);
        Assert.Equal(Roles.Admin, row.Role);
        Assert.Null(row.InvitedByUserId);
        Assert.Null(row.UsedAt);
        Assert.Null(row.RevokedAt);
        // Same token format and lifetime as the normal invite: 32 bytes base64url, 7 days.
        Assert.Matches("^[A-Za-z0-9_-]{43}$", row.Token);
        Assert.InRange(row.ExpiresAt - row.CreatedAt, TimeSpan.FromDays(7) - TimeSpan.FromSeconds(1), TimeSpan.FromDays(7));

        Assert.Equal(AdminMail, body.Email);
        Assert.Equal(row.ExpiresAt, body.ExpiresAt);
        Assert.True(body.EmailSent);
        Assert.Equal($"{PortalUrl}/register/invite?token={row.Token}", body.InviteUrl);

        var sent = Assert.Single(email.Sent);
        Assert.Equal(AdminMail, sent.To);
        Assert.Equal(body.InviteUrl, sent.Link);       // the response hands over exactly the email's link
        Assert.Equal("Un administrador", sent.InviterName);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal("Setup.FirstAdminInvited", entry.Action);
        Assert.Null(entry.UserId);
        Assert.Equal(Tenant, entry.TenantId);
        Assert.Equal(row.InvitationId.ToString(), entry.EntityId);
        Assert.DoesNotContain(row.Token, entry.ToString());
    }

    [Fact]
    public async Task EmailFailure_StillAnswers201_WithEmailSentFalse_AndKeepsTheInvitation()
    {
        await using var db = NewContext();

        IActionResult result = await NewController(db, new RecordingEmailService { Throw = true })
            .InviteFirstAdminAsync(new FirstAdminSetupRequest { Email = AdminMail }, CancellationToken.None);

        var created = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status201Created, created.StatusCode);
        var body = Assert.IsType<FirstAdminInvitationResponse>(created.Value);
        Assert.False(body.EmailSent);

        UserInvitation row = await db.UserInvitations.AsNoTracking().SingleAsync();
        Assert.Equal(body.InvitationId, row.InvitationId);
        Assert.EndsWith(row.Token, body.InviteUrl);
    }

    [Fact]
    public async Task SecondCallForTheSameEmail_RevokesTheEarlierBootstrapInvitation_AndIssuesANewToken()
    {
        await using var db = NewContext();
        var controller = NewController(db, new RecordingEmailService());

        var first  = await InviteAsync(controller);
        var second = await InviteAsync(NewController(db, new RecordingEmailService()));

        Assert.NotEqual(first.InvitationId, second.InvitationId);
        Assert.NotEqual(first.InviteUrl, second.InviteUrl);

        List<UserInvitation> rows = await db.UserInvitations.AsNoTracking().ToListAsync();
        UserInvitation old = rows.Single(i => i.InvitationId == first.InvitationId);
        UserInvitation now = rows.Single(i => i.InvitationId == second.InvitationId);
        Assert.NotNull(old.RevokedAt);
        Assert.Null(old.RevokedByUserId);
        Assert.Null(now.RevokedAt);
    }

    [Fact]
    public async Task Logs_NeverCarryTheTokenTheLinkOrTheAddress()
    {
        await using var db = NewContext();
        var logger = new CapturingLogger<UserInvitationService>();

        var body = await InviteAsync(NewController(db, new RecordingEmailService { Throw = true }, serviceLogger: logger));

        UserInvitation row = await db.UserInvitations.AsNoTracking().SingleAsync();
        Assert.NotEmpty(logger.Entries);
        Assert.All(logger.Entries, entry =>
        {
            Assert.DoesNotContain(row.Token, entry);
            Assert.DoesNotContain(AdminMail, entry, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Contains(logger.Entries, e => e.Contains(body.InvitationId.ToString()));
    }

    // ── A NULL inviter downstream: the list, resend and revoke ───────────────────

    [Fact]
    public async Task InvitationsList_ShowsABootstrapInvitationWithNullIdAndTheNeutralLabel()
    {
        await using var db = NewContext();
        var admin = NewUser(Tenant, Roles.Admin, isActive: false);
        admin.DisplayName = "Ana Admin";
        await SeedAsync(
            db,
            invitations: [NewInvitation(invitedBy: null), NewInvitation(invitedBy: admin.UserId, email: "other@client.example")],
            users: [admin]);

        IReadOnlyList<InvitationDto> list = await NewService(db, new RecordingEmailService())
            .GetInvitationsAsync(Tenant, status: null, CancellationToken.None);

        InvitationDto bootstrap = list.Single(i => i.Email == AdminMail);
        Assert.Null(bootstrap.InvitedByUserId);
        Assert.Equal(UserInvitationService.BootstrapInviterLabel, bootstrap.InvitedByName);
        Assert.Equal("Configuración inicial", bootstrap.InvitedByName);

        InvitationDto normal = list.Single(i => i.Email == "other@client.example");
        Assert.Equal(admin.UserId, normal.InvitedByUserId);
        Assert.Equal("Ana Admin", normal.InvitedByName);
    }

    [Fact]
    public async Task Resend_WorksWithANullInviter()
    {
        await using var db = NewContext();
        var invitation = NewInvitation(invitedBy: null);
        await SeedAsync(db, invitations: [invitation]);
        var email = new RecordingEmailService();

        InvitationActionResult result = await NewService(db, email).ResendInvitationAsync(
            Tenant, invitation.InvitationId, Guid.NewGuid(), null, null, CancellationToken.None);

        Assert.Equal(InvitationActionResult.Success, result);
        var sent = Assert.Single(email.Sent);
        Assert.Equal("Un administrador", sent.InviterName);
        UserInvitation row = await db.UserInvitations.AsNoTracking().SingleAsync();
        Assert.NotEqual(invitation.Token, row.Token);
        Assert.Null(row.InvitedByUserId);
    }

    [Fact]
    public async Task Revoke_WorksWithANullInviter()
    {
        await using var db = NewContext();
        var invitation = NewInvitation(invitedBy: null);
        await SeedAsync(db, invitations: [invitation]);
        var revoker = Guid.NewGuid();

        InvitationActionResult result = await NewService(db, new RecordingEmailService()).RevokeInvitationAsync(
            Tenant, invitation.InvitationId, revoker, null, null, CancellationToken.None);

        Assert.Equal(InvitationActionResult.Success, result);
        UserInvitation row = await db.UserInvitations.AsNoTracking().SingleAsync();
        Assert.NotNull(row.RevokedAt);
        Assert.Equal(revoker, row.RevokedByUserId);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static async Task<(bool NextCalled, IActionResult? Result)> RunGateAsync(string? configuredKey, string? suppliedKey)
    {
        var filter = new SetupKeyGateFilter(
            Options.Create(new SetupOptions { BootstrapKey = configuredKey }),
            new StubTenantContextAccessor(),
            NullLogger<SetupKeyGateFilter>.Instance);

        var httpContext = new DefaultHttpContext();
        if (suppliedKey is not null)
        {
            httpContext.Request.Headers[SetupKeyGateFilter.HeaderName] = suppliedKey;
        }

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var context = new ResourceExecutingContext(actionContext, new List<IFilterMetadata>(), new List<IValueProviderFactory>());

        bool nextCalled = false;
        await filter.OnResourceExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ResourceExecutedContext(actionContext, new List<IFilterMetadata>()));
        });

        return (nextCalled, context.Result);
    }

    private static async Task<FirstAdminInvitationResponse> InviteAsync(SetupController controller)
    {
        IActionResult result = await controller.InviteFirstAdminAsync(
            new FirstAdminSetupRequest { Email = AdminMail }, CancellationToken.None);

        var created = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status201Created, created.StatusCode);
        return Assert.IsType<FirstAdminInvitationResponse>(created.Value);
    }

    private static ProblemDetails AssertProblem(IActionResult result, int status, string errorCode)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(status, objectResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(errorCode, problem.Extensions["errorCode"]);
        return problem;
    }

    private static FinanciaDbContext NewContext()
        => new(new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"first-admin-{Guid.NewGuid()}")
            .Options);

    private static UserInvitationService NewService(
        FinanciaDbContext db,
        RecordingEmailService email,
        RecordingAuditService? audit = null,
        CapturingLogger<UserInvitationService>? logger = null)
        => new(
            db,
            new UserInvitationRepository(db, NullLogger<UserInvitationRepository>.Instance),
            new UserRepository(db, NullLogger<UserRepository>.Instance),
            new TenantSettingsRepository(db, NullLogger<TenantSettingsRepository>.Instance),
            email,
            audit ?? new RecordingAuditService(),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["AppSettings:FrontendUrl"] = PortalUrl })
                .Build(),
            (Microsoft.Extensions.Logging.ILogger<UserInvitationService>?)logger ?? NullLogger<UserInvitationService>.Instance);

    private static SetupController NewController(
        FinanciaDbContext db,
        RecordingEmailService email,
        string? tenantHeader = Tenant,
        RecordingAuditService? audit = null,
        CapturingLogger<UserInvitationService>? serviceLogger = null)
    {
        var controller = new SetupController(
            NewService(db, email, audit, serviceLogger),
            new StubTenantContextAccessor(),
            NullLogger<SetupController>.Instance);

        var httpContext = new DefaultHttpContext();
        if (tenantHeader is not null)
        {
            httpContext.Request.Headers["X-Tenant-ID"] = tenantHeader;
        }

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static UserInvitation NewInvitation(Guid? invitedBy, string email = AdminMail) => new()
    {
        InvitationId    = Guid.NewGuid(),
        TenantId        = Tenant,
        Email           = email,
        Role            = Roles.Admin,
        InvitedByUserId = invitedBy,
        Token           = $"token-{Guid.NewGuid():N}",
        ExpiresAt       = DateTime.UtcNow.AddDays(3),
        CreatedAt       = DateTime.UtcNow.AddDays(-1)
    };

    private static User NewUser(string tenantId, string role, bool isActive) => new()
    {
        UserId      = Guid.NewGuid(),
        TenantId    = tenantId,
        FirebaseUid = $"uid-{Guid.NewGuid():N}",
        Email       = $"{Guid.NewGuid():N}@client.example",
        DisplayName = "Existing",
        Role        = role,
        IsActive    = isActive,
        CreatedAt   = DateTime.UtcNow.AddDays(-10),
        UpdatedAt   = DateTime.UtcNow.AddDays(-10)
    };

    private static async Task SeedAsync(
        FinanciaDbContext db,
        UserInvitation[]? invitations = null,
        User[]? users = null)
    {
        db.UserInvitations.AddRange(invitations ?? []);
        db.Users.AddRange(users ?? []);
        db.ChangeTracker.DetectChanges(); // AutoDetectChanges is disabled on the context
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    /// <summary>The resolved tenant is always TenantA; the header decides whether the action trusts it.</summary>
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

    private sealed class RecordingEmailService : IEmailService
    {
        public bool Throw { get; init; }
        public List<(string To, string Link, string InviterName)> Sent { get; } = [];
        public List<InvitationEmail> Messages { get; } = [];

        public Task SendInvitationEmailAsync(
            InvitationEmail message, string tenantId, CancellationToken cancellationToken)
        {
            if (Throw)
            {
                throw new InvalidOperationException("Failed to send email: Unauthorized");
            }

            Sent.Add((message.ToEmail, message.InvitationLink, message.InviterName));
            Messages.Add(message);
            return Task.CompletedTask;
        }

        public Task SendPasswordResetEmailAsync(PasswordResetEmail message, string tenantId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task SendEmailVerificationEmailAsync(EmailVerificationEmail message, string tenantId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed record AuditEntry(string TenantId, Guid? UserId, string Action, string? EntityType, string? EntityId, string? NewValueJson);

    private sealed class RecordingAuditService : IAuditService
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task LogAsync(
            string tenantId, Guid? userId, string action, string severity = "Info", string? entityType = null,
            string? entityId = null, string? ipAddress = null, string? requestId = null,
            string? oldValueJson = null, string? newValueJson = null, CancellationToken ct = default)
        {
            Entries.Add(new AuditEntry(tenantId, userId, action, entityType, entityId, newValueJson));
            return Task.CompletedTask;
        }

        public Task LogSessionAsync(
            Guid tenantId, Guid? userId, string email, string eventType, string? ipAddress,
            string? userAgent, string? failureReason = null, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class CapturingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // Rendered message, every structured value and the exception: nothing may carry a secret.
            var parts = new List<string> { formatter(state, exception) };
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                parts.AddRange(values.Select(v => v.Value?.ToString() ?? string.Empty));
            }

            if (exception is not null)
            {
                parts.Add(exception.ToString());
            }

            Entries.Add(string.Join(" | ", parts));
        }
    }
}
