// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-02

using System.Reflection;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Clases.EmailVerification;
using Api.Web.FinanceIA.Clases.Exceptions;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Entities;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Settings;
using FirebaseAdmin.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// POST api/bff/auth/register-invited is the only way a Users row is created, so its gates are
/// pinned here: the invitation must be live (not used, revoked or expired) and belong to the
/// resolved tenant, the validated Firebase token's email must match it, and the role is the
/// invitation's — never Analyst by default, never from the body.
/// <para>
/// The REAL <see cref="UserInvitationRepository"/> and <see cref="UserRepository"/> run over an
/// InMemory <see cref="FinanciaDbContext"/>, so it is the production token query that rejects an
/// expired/used/revoked/foreign invitation, not a stub that was told to.
/// </para>
/// </summary>
public sealed class InvitationControllerRegisterInvitedTests
{
    private const string Tenant      = "TenantA";
    private const string OtherTenant = "TenantB";
    private const string InvitedMail = "invitee@example.com";
    private const string FirebaseUid = "firebase-uid-1";
    private const string InviteToken = "invite-token-1";

    // ── Invitation gates ─────────────────────────────────────────────────────────

    public static TheoryData<string> DeadInvitations => new() { "expired", "used", "revoked", "other-tenant" };

    [Theory]
    [MemberData(nameof(DeadInvitations))]
    public async Task DeadOrForeignInvitation_IsRejected_AndNoUserIsCreated(string kind)
    {
        await using var ctx = NewContext();
        var invitation = NewInvitation(Roles.Auditor);
        switch (kind)
        {
            case "expired":      invitation.ExpiresAt = DateTime.UtcNow.AddMinutes(-1); break;
            case "used":         invitation.UsedAt    = DateTime.UtcNow.AddDays(-1);    break;
            case "revoked":      invitation.RevokedAt = DateTime.UtcNow.AddDays(-1);    break;
            case "other-tenant": invitation.TenantId  = OtherTenant;                    break;
        }
        await SeedAsync(ctx, invitation);

        IActionResult result = await NewController(ctx).RegisterInvitedAsync(
            new RegisterInvitedRequest(InviteToken, "Invitee"), CancellationToken.None);

        AssertProblem(result, 400, "invitation_invalid");
        Assert.Empty(await ctx.Users.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task UnknownToken_IsRejected()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, NewInvitation(Roles.Auditor));

        IActionResult result = await NewController(ctx).RegisterInvitedAsync(
            new RegisterInvitedRequest("not-the-token", "Invitee"), CancellationToken.None);

        AssertProblem(result, 400, "invitation_invalid");
        Assert.Empty(await ctx.Users.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task EmailMismatch_IsRejected_AndTheInvitationStaysUnused()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, NewInvitation(Roles.Auditor));

        IActionResult result = await NewController(ctx, email: "attacker@example.com").RegisterInvitedAsync(
            new RegisterInvitedRequest(InviteToken, "Invitee"), CancellationToken.None);

        AssertProblem(result, 400, "email_mismatch");
        Assert.Empty(await ctx.Users.AsNoTracking().ToListAsync());
        Assert.Null((await ctx.UserInvitations.AsNoTracking().SingleAsync()).UsedAt);
    }

    [Fact]
    public async Task TokenWithoutEmail_IsRejectedAsAMismatch()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, NewInvitation(Roles.Auditor));

        IActionResult result = await NewController(ctx, email: null).RegisterInvitedAsync(
            new RegisterInvitedRequest(InviteToken, "Invitee"), CancellationToken.None);

        AssertProblem(result, 400, "email_mismatch");
        Assert.Empty(await ctx.Users.AsNoTracking().ToListAsync());
    }

    // ── Happy path: the invitation decides the role ──────────────────────────────

    [Theory]
    [InlineData(Roles.Auditor)]
    [InlineData(Roles.RiskManager)]
    [InlineData(Roles.Admin)]
    public async Task ValidInvitation_CreatesTheUserWithTheInvitationsRole_AndConsumesIt(string role)
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, NewInvitation(role));
        var tokens = new StubTokenService();

        IActionResult result = await NewController(ctx, tokens: tokens).RegisterInvitedAsync(
            new RegisterInvitedRequest(InviteToken, "  Invitee  "), CancellationToken.None);

        var ok   = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<AuthenticatedUserResponse>(ok.Value);
        Assert.Equal(role, body.Role);
        Assert.Null(body.TenantDisplayName);

        User user = await ctx.Users.AsNoTracking().SingleAsync();
        Assert.Equal(role, user.Role);
        Assert.Equal(Tenant, user.TenantId);
        Assert.Equal(FirebaseUid, user.FirebaseUid);
        Assert.Equal(InvitedMail, user.Email);
        Assert.Equal("Invitee", user.DisplayName);

        Assert.NotNull((await ctx.UserInvitations.AsNoTracking().SingleAsync()).UsedAt);
        Assert.Equal(role, tokens.IssuedRole);
    }

    [Fact]
    public async Task FirstAdminBootstrapInvitation_WithNoInviter_RegistersAnAdmin_EndToEnd()
    {
        await using var ctx = NewContext();
        var email = new LinkCapturingEmailService();
        var service = new UserInvitationService(
            ctx,
            new UserInvitationRepository(ctx, NullLogger<UserInvitationRepository>.Instance),
            new UserRepository(ctx, NullLogger<UserRepository>.Instance),
            new TenantSettingsRepository(ctx, NullLogger<TenantSettingsRepository>.Instance),
            email,
            new StubAuditService(),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["AppSettings:FrontendUrl"] = "https://portal.example" })
                .Build(),
            NullLogger<UserInvitationService>.Instance);

        FirstAdminInviteOutcome outcome = await service.InviteFirstAdminAsync(
            Tenant, InvitedMail, null, null, CancellationToken.None);
        ctx.ChangeTracker.Clear();

        string token = outcome.Invitation!.InviteUrl.Split("token=")[1];
        Assert.Equal(outcome.Invitation.InviteUrl, email.LastLink);
        Assert.Null((await ctx.UserInvitations.AsNoTracking().SingleAsync()).InvitedByUserId);

        var tokens = new StubTokenService();
        IActionResult result = await NewController(ctx, tokens: tokens).RegisterInvitedAsync(
            new RegisterInvitedRequest(token, "First Admin"), CancellationToken.None);

        var body = Assert.IsType<AuthenticatedUserResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(Roles.Admin, body.Role);
        User user = await ctx.Users.AsNoTracking().SingleAsync();
        Assert.Equal(Roles.Admin, user.Role);
        Assert.True(user.IsActive);
        Assert.Equal(Tenant, user.TenantId);
        Assert.Equal(Roles.Admin, tokens.IssuedRole);
        Assert.NotNull((await ctx.UserInvitations.AsNoTracking().SingleAsync()).UsedAt);

        // The tenant now has an active Admin: the bootstrap is closed.
        FirstAdminInviteOutcome again = await service.InviteFirstAdminAsync(
            Tenant, "someone-else@example.com", null, null, CancellationToken.None);
        Assert.True(again.AdminAlreadyExists);
    }

    [Fact]
    public async Task EmailMatch_IsCaseInsensitive()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, NewInvitation(Roles.Auditor));

        IActionResult result = await NewController(ctx, email: "Invitee@EXAMPLE.com").RegisterInvitedAsync(
            new RegisterInvitedRequest(InviteToken, "Invitee"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(InvitedMail, (await ctx.Users.AsNoTracking().SingleAsync()).Email);
    }

    // ── Invited users are verified: the invite token proved mailbox control ─────

    [Fact]
    public async Task ValidInvitation_MarksTheFirebaseEmailVerified_AndSaysSo()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, NewInvitation(Roles.Auditor));
        var firebase = new StubFirebaseUserAdmin();

        IActionResult result = await NewController(ctx, firebase: firebase).RegisterInvitedAsync(
            new RegisterInvitedRequest(InviteToken, "Invitee"), CancellationToken.None);

        var body = Assert.IsType<AuthenticatedUserResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.True(body.EmailVerified);
        Assert.Equal([FirebaseUid], firebase.MarkedVerified);
        Assert.Equal(Tenant, firebase.LastTenantId);
    }

    [Fact]
    public async Task FirebaseFailure_DoesNotFailTheRegistration_AndLogsNoAddress()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, NewInvitation(Roles.Auditor));
        var logger   = new CapturingLogger<InvitationController>();
        var firebase = new StubFirebaseUserAdmin
        {
            Throw = new FirebaseServiceException($"Firebase failed for {InvitedMail}")
        };

        IActionResult result = await NewController(ctx, firebase: firebase, logger: logger).RegisterInvitedAsync(
            new RegisterInvitedRequest(InviteToken, "Invitee"), CancellationToken.None);

        var body = Assert.IsType<AuthenticatedUserResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.False(body.EmailVerified);
        User user = await ctx.Users.AsNoTracking().SingleAsync();
        Assert.NotNull((await ctx.UserInvitations.AsNoTracking().SingleAsync()).UsedAt);

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(user.UserId.ToString(), warning.Text);
        Assert.Contains(Tenant, warning.Text);
        Assert.All(logger.Entries, e => Assert.DoesNotContain(InvitedMail, e.Text, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RejectedRegistration_NeverTouchesFirebase()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, NewInvitation(Roles.Auditor));
        var firebase = new StubFirebaseUserAdmin();

        await NewController(ctx, email: "attacker@example.com", firebase: firebase).RegisterInvitedAsync(
            new RegisterInvitedRequest(InviteToken, "Invitee"), CancellationToken.None);

        Assert.Empty(firebase.MarkedVerified);
    }

    // ── Request and identity guards ──────────────────────────────────────────────

    [Fact]
    public async Task MissingBearer_IsUnauthorized()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, NewInvitation(Roles.Auditor));

        IActionResult result = await NewController(ctx, bearer: null).RegisterInvitedAsync(
            new RegisterInvitedRequest(InviteToken, "Invitee"), CancellationToken.None);

        AssertProblem(result, 401, errorCode: null);
        Assert.Empty(await ctx.Users.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData("", "Invitee")]
    [InlineData("   ", "Invitee")]
    [InlineData(InviteToken, "")]
    [InlineData(InviteToken, "   ")]
    public async Task BlankTokenOrName_IsABadRequest_NotA500(string token, string name)
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, NewInvitation(Roles.Auditor));

        IActionResult result = await NewController(ctx).RegisterInvitedAsync(
            new RegisterInvitedRequest(token, name), CancellationToken.None);

        AssertProblem(result, 400, "invalid_request");
        Assert.Empty(await ctx.Users.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task OverlongName_IsABadRequest()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, NewInvitation(Roles.Auditor));

        IActionResult result = await NewController(ctx).RegisterInvitedAsync(
            new RegisterInvitedRequest(InviteToken, new string('x', 201)), CancellationToken.None);

        AssertProblem(result, 400, "invalid_request");
    }

    [Fact]
    public async Task AlreadyRegisteredUid_IsAConflict_AndTheInvitationStaysUnused()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, NewInvitation(Roles.Admin), ExistingUser(FirebaseUid, "someone-else@example.com"));

        IActionResult result = await NewController(ctx).RegisterInvitedAsync(
            new RegisterInvitedRequest(InviteToken, "Invitee"), CancellationToken.None);

        AssertProblem(result, 409, "user_already_registered");
        Assert.Single(await ctx.Users.AsNoTracking().ToListAsync());
        Assert.Null((await ctx.UserInvitations.AsNoTracking().SingleAsync()).UsedAt);
    }

    [Fact]
    public async Task AlreadyRegisteredEmail_IsAConflict_AndTheInvitationStaysUnused()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, NewInvitation(Roles.Admin), ExistingUser("another-uid", InvitedMail));

        IActionResult result = await NewController(ctx).RegisterInvitedAsync(
            new RegisterInvitedRequest(InviteToken, "Invitee"), CancellationToken.None);

        AssertProblem(result, 409, "user_already_registered");
        Assert.Single(await ctx.Users.AsNoTracking().ToListAsync());
        Assert.Null((await ctx.UserInvitations.AsNoTracking().SingleAsync()).UsedAt);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static void AssertProblem(IActionResult result, int status, string? errorCode)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(status, objectResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        if (errorCode is not null)
        {
            Assert.Equal(errorCode, problem.Extensions["errorCode"]);
        }
    }

    private static FinanciaDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"register-invited-{Guid.NewGuid()}")
            .Options;
        return new FinanciaDbContext(options);
    }

    private static UserInvitation NewInvitation(string role) => new()
    {
        InvitationId    = Guid.NewGuid(),
        TenantId        = Tenant,
        Email           = InvitedMail,
        Role            = role,
        InvitedByUserId = Guid.NewGuid(),
        Token           = InviteToken,
        ExpiresAt       = DateTime.UtcNow.AddDays(3),
        CreatedAt       = DateTime.UtcNow.AddDays(-1)
    };

    private static User ExistingUser(string firebaseUid, string email) => new()
    {
        UserId      = Guid.NewGuid(),
        TenantId    = Tenant,
        FirebaseUid = firebaseUid,
        Email       = email,
        DisplayName = "Existing",
        Role        = Roles.Analyst,
        IsActive    = true,
        CreatedAt   = DateTime.UtcNow.AddDays(-10),
        UpdatedAt   = DateTime.UtcNow.AddDays(-10)
    };

    private static async Task SeedAsync(FinanciaDbContext ctx, UserInvitation invitation, params User[] users)
    {
        ctx.UserInvitations.Add(invitation);
        ctx.Users.AddRange(users);
        ctx.ChangeTracker.DetectChanges(); // AutoDetectChanges is disabled on the context
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();
    }

    [Fact]
    public async Task ValidInvitation_ReturnsTheTenantDisplayName_LikeMe()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, NewInvitation(Roles.Analyst));

        IActionResult result = await NewController(ctx, tenantDisplayName: "Movil SGR").RegisterInvitedAsync(
            new RegisterInvitedRequest(InviteToken, "Invitee"), CancellationToken.None);

        var body = Assert.IsType<AuthenticatedUserResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("Movil SGR", body.TenantDisplayName);
    }

    private static InvitationController NewController(
        FinanciaDbContext ctx,
        string? email = InvitedMail,
        string? bearer = "firebase-id-token",
        StubTokenService? tokens = null,
        StubFirebaseUserAdmin? firebase = null,
        ILogger<InvitationController>? logger = null,
        string? tenantDisplayName = null)
    {
        var tenantContext = new TenantContext
        {
            TenantId               = Tenant,
            FirebaseProjectId      = "project",
            FirebaseCredentialJson = "{}",
            RecaptchaSecretKey     = "secret"
        };

        var controller = new InvitationController(
            new StubTenantContextAccessor { TenantContext = tenantContext },
            new StubFirebaseTokenValidator(FirebaseUid, email),
            tokens ?? new StubTokenService(),
            new UserRepository(ctx, NullLogger<UserRepository>.Instance),
            new UserInvitationRepository(ctx, NullLogger<UserInvitationRepository>.Instance),
            new StubAuditService(),
            new StubTenantSettingsService { TenantDisplayName = tenantDisplayName },
            firebase ?? new StubFirebaseUserAdmin(),
            null!, // IWebHostEnvironment — not read by RegisterInvitedAsync
            logger ?? NullLogger<InvitationController>.Instance);

        var httpContext = new DefaultHttpContext();
        if (bearer is not null)
        {
            httpContext.Request.Headers.Authorization = $"Bearer {bearer}";
        }

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    /// <summary>
    /// <see cref="FirebaseToken"/> only has an internal constructor taking the internal
    /// <c>FirebaseToken.Args</c>; <c>Uid</c> is <c>Args.Subject</c> and the email rides in
    /// <c>Args.Claims</c>, exactly as the Admin SDK fills them from a verified ID token.
    /// </summary>
    private static FirebaseToken NewFirebaseToken(string uid, string? email)
    {
        const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        Type argsType = typeof(FirebaseToken).GetNestedType("Args", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("FirebaseToken.Args not found — FirebaseAdmin internals changed.");
        object args = Activator.CreateInstance(argsType, nonPublic: true)!;

        var claims = new Dictionary<string, object> { ["sub"] = uid };
        if (email is not null)
        {
            claims["email"] = email;
        }

        argsType.GetProperty("Subject", any)!.SetValue(args, uid);
        argsType.GetProperty("Claims", any)!.SetValue(args, (IReadOnlyDictionary<string, object>)claims);

        ConstructorInfo ctor = typeof(FirebaseToken).GetConstructor(any, [argsType])
            ?? throw new InvalidOperationException("FirebaseToken(Args) not found — FirebaseAdmin internals changed.");
        return (FirebaseToken)ctor.Invoke([args]);
    }

    private sealed class StubFirebaseTokenValidator(string uid, string? email) : IFirebaseTokenValidator
    {
        public Task<FirebaseToken> ValidateAsync(
            string idToken, TenantContext tenantContext, CancellationToken cancellationToken)
            => Task.FromResult(NewFirebaseToken(uid, email));
    }

    private sealed class StubFirebaseUserAdmin : ITenantFirebaseUserAdmin
    {
        public Exception? Throw { get; init; }
        public List<string> MarkedVerified { get; } = [];
        public string? LastTenantId { get; private set; }

        public Task<bool?> GetEmailVerifiedAsync(
            string firebaseUid, TenantContext tenantContext, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task MarkEmailVerifiedAsync(
            string firebaseUid, TenantContext tenantContext, CancellationToken cancellationToken)
        {
            if (Throw is not null) throw Throw;
            MarkedVerified.Add(firebaseUid);
            LastTenantId = tenantContext.TenantId;
            return Task.CompletedTask;
        }

        public Task<string?> GenerateEmailVerificationLinkAsync(
            string email, string continueUrl, TenantContext tenantContext, CancellationToken cancellationToken)
            => throw new NotSupportedException();
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
            // Rendered message, every structured value and the exception: nothing may carry the address.
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

    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; }
    }

    private sealed class StubTokenService : ITokenService
    {
        public string? IssuedRole { get; private set; }

        public Task<string> IssueAsync(
            string firebaseUid, TenantContext tenantContext, Guid userId, string role,
            CancellationToken cancellationToken)
        {
            IssuedRole = role;
            return Task.FromResult("internal-jwt");
        }
    }

    private sealed class LinkCapturingEmailService : IEmailService
    {
        public string? LastLink { get; private set; }

        public Task SendInvitationEmailAsync(
            InvitationEmail message, string tenantId, CancellationToken cancellationToken)
        {
            LastLink = message.InvitationLink;
            return Task.CompletedTask;
        }

        public Task SendPasswordResetEmailAsync(PasswordResetEmail message, string tenantId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task SendEmailVerificationEmailAsync(EmailVerificationEmail message, string tenantId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class StubAuditService : IAuditService
    {
        public Task LogAsync(
            string tenantId, Guid? userId, string action, string severity = "Info",
            string? entityType = null, string? entityId = null, string? ipAddress = null,
            string? requestId = null, string? oldValueJson = null, string? newValueJson = null,
            CancellationToken ct = default)
            => Task.CompletedTask;

        public Task LogSessionAsync(
            Guid tenantId, Guid? userId, string email, string eventType,
            string? ipAddress, string? userAgent, string? failureReason = null,
            CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class StubTenantSettingsService : ITenantSettingsService
    {
        public Task<string> GetStringAsync(string tenantId, string key, string defaultValue, CancellationToken ct)
            => Task.FromResult(defaultValue);

        public Task<string> GetCreditLineModeAsync(string tenantId, CancellationToken ct)
            => Task.FromResult("classic");

        public string? TenantDisplayName { get; init; }

        public Task<string?> GetTenantDisplayNameAsync(string tenantId, CancellationToken ct)
            => Task.FromResult(TenantDisplayName);
    }
}
