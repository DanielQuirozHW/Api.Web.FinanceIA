// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-21

using System.Security.Claims;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Clases.Exceptions;
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
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// Which existing rows block a new invitation. Only a LIVE invitation of the SAME tenant (unused,
/// unrevoked, unexpired) and a user of the same tenant do — a revoked invitation used to block too,
/// so a revoked address could never be invited again, and another tenant's invitation blocked and
/// revealed itself. The REAL <see cref="UserInvitationRepository"/> runs over an InMemory
/// <see cref="FinanciaDbContext"/>, so the production pending query is what is exercised.
/// </summary>
public sealed class UserInvitationServiceInviteConflictTests
{
    private const string Tenant      = "TenantA";
    private const string OtherTenant = "TenantB";
    private const string Address     = "invitee@example.com";

    // ── What no longer blocks ────────────────────────────────────────────────────

    public static TheoryData<string> NonBlockingInvitations => new() { "revoked", "other-tenant", "expired", "used" };

    [Theory]
    [MemberData(nameof(NonBlockingInvitations))]
    public async Task DeadOrForeignInvitation_DoesNotBlockANewOne(string kind)
    {
        await using var db = NewContext();
        var existing = NewInvitation();
        switch (kind)
        {
            case "revoked":      existing.RevokedAt = DateTime.UtcNow.AddHours(-1); break;
            case "other-tenant": existing.TenantId  = OtherTenant;                  break;
            case "expired":      existing.ExpiresAt = DateTime.UtcNow.AddMinutes(-1); break;
            case "used":         existing.UsedAt    = DateTime.UtcNow.AddDays(-1);  break;
        }
        await SeedAsync(db, existing);
        var email = new RecordingEmailService();

        InviteUserResponse response = await NewService(db, email).InviteUserAsync(
            Tenant, Guid.NewGuid(), new InviteUserSettingsRequest { Email = Address, Role = Roles.Analyst },
            null, null, CancellationToken.None);

        UserInvitation created = await db.UserInvitations.AsNoTracking()
            .SingleAsync(i => i.InvitationId == response.InvitationId);
        Assert.Equal(Tenant, created.TenantId);
        Assert.Null(created.RevokedAt);
        Assert.Equal([Address], email.Recipients);
    }

    // ── What still blocks ────────────────────────────────────────────────────────

    [Fact]
    public async Task OwnLiveInvitation_IsAConflict_InvitationPending()
    {
        await using var db = NewContext();
        await SeedAsync(db, NewInvitation());
        var email = new RecordingEmailService();

        var ex = await Assert.ThrowsAsync<InvitationConflictException>(() => NewService(db, email).InviteUserAsync(
            Tenant, Guid.NewGuid(), new InviteUserSettingsRequest { Email = "  INVITEE@example.com ", Role = Roles.Analyst },
            null, null, CancellationToken.None));

        Assert.Equal(InvitationConflictException.InvitationPending, ex.ErrorCode);
        Assert.Equal(
            "Ya hay una invitación pendiente para este correo. Reenviala o revocala desde Invitaciones pendientes.",
            ex.Message);
        Assert.Single(await db.UserInvitations.AsNoTracking().ToListAsync());
        Assert.Empty(email.Recipients);
    }

    [Fact]
    public async Task ExistingUserOfTheTenant_IsAConflict_UserAlreadyExists()
    {
        await using var db = NewContext();
        await SeedAsync(db, invitation: null, NewUser(Tenant));

        var ex = await Assert.ThrowsAsync<InvitationConflictException>(() => NewService(db, new RecordingEmailService()).InviteUserAsync(
            Tenant, Guid.NewGuid(), new InviteUserSettingsRequest { Email = Address, Role = Roles.Analyst },
            null, null, CancellationToken.None));

        Assert.Equal(InvitationConflictException.UserAlreadyExists, ex.ErrorCode);
        Assert.Equal("Ese correo ya tiene un usuario en esta organización.", ex.Message);
    }

    // ── SettingsController maps the conflict to 409 ──────────────────────────────

    [Theory]
    [InlineData(InvitationConflictException.InvitationPending)]
    [InlineData(InvitationConflictException.UserAlreadyExists)]
    public async Task Controller_AnswersAConflictWith409_AndItsErrorCode(string errorCode)
    {
        var conflict = errorCode == InvitationConflictException.InvitationPending
            ? InvitationConflictException.ForPendingInvitation()
            : InvitationConflictException.ForExistingUser();

        IActionResult result = await NewController(new ThrowingInvitationService(conflict))
            .InviteUser(new InviteUserSettingsRequest { Email = Address, Role = Roles.Analyst }, CancellationToken.None);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, objectResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(errorCode, problem.Extensions["errorCode"]);
        Assert.Equal(conflict.Message, problem.Detail);
    }

    [Fact]
    public async Task Controller_KeepsOtherInvalidOperationFailuresOn400()
    {
        IActionResult result = await NewController(new ThrowingInvitationService(new InvalidOperationException("boom")))
            .InviteUser(new InviteUserSettingsRequest { Email = Address, Role = Roles.Analyst }, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static FinanciaDbContext NewContext()
        => new(new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"invite-conflict-{Guid.NewGuid()}")
            .Options);

    private static UserInvitationService NewService(FinanciaDbContext db, RecordingEmailService email)
        => new(
            db,
            new UserInvitationRepository(db, NullLogger<UserInvitationRepository>.Instance),
            new UserRepository(db, NullLogger<UserRepository>.Instance),
            new TenantSettingsRepository(db, NullLogger<TenantSettingsRepository>.Instance),
            email,
            new NoOpAuditService(),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["AppSettings:FrontendUrl"] = "https://portal.example" })
                .Build(),
            NullLogger<UserInvitationService>.Instance);

    private static UserInvitation NewInvitation() => new()
    {
        InvitationId    = Guid.NewGuid(),
        TenantId        = Tenant,
        Email           = Address,
        Role            = Roles.Analyst,
        InvitedByUserId = Guid.NewGuid(),
        Token           = $"token-{Guid.NewGuid():N}",
        ExpiresAt       = DateTime.UtcNow.AddDays(3),
        CreatedAt       = DateTime.UtcNow.AddDays(-1)
    };

    private static User NewUser(string tenantId) => new()
    {
        UserId      = Guid.NewGuid(),
        TenantId    = tenantId,
        FirebaseUid = "uid-existing",
        Email       = Address,
        DisplayName = "Existing",
        Role        = Roles.Analyst,
        IsActive    = true,
        CreatedAt   = DateTime.UtcNow.AddDays(-10),
        UpdatedAt   = DateTime.UtcNow.AddDays(-10)
    };

    private static async Task SeedAsync(FinanciaDbContext db, UserInvitation? invitation, params User[] users)
    {
        if (invitation is not null)
        {
            db.UserInvitations.Add(invitation);
        }

        db.Users.AddRange(users);
        db.ChangeTracker.DetectChanges(); // AutoDetectChanges is disabled on the context
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static SettingsController NewController(IUserInvitationService invitations)
    {
        var controller = new SettingsController(
            null!,    // ISettingsService
            invitations,
            new StubTenantContextAccessor(),
            null!,    // IUserRepository
            null!,    // IUserApprovalLimitRepository
            null!,    // ITeamRepository
            null!,    // ISgrSettingsService
            NullLogger<SettingsController>.Instance);

        Claim[] claims = [new Claim("userId", Guid.NewGuid().ToString()), new Claim("tenantId", Tenant)];
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Internal")) }
        };
        return controller;
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

    private sealed class ThrowingInvitationService(Exception toThrow) : IUserInvitationService
    {
        public Task<InviteUserResponse> InviteUserAsync(
            string tenantId, Guid invitedByUserId, InviteUserSettingsRequest request,
            string? ipAddress, string? requestId, CancellationToken ct)
            => throw toThrow;

        public Task<FirstAdminInviteOutcome> InviteFirstAdminAsync(string tenantId, string email, string? ipAddress, string? requestId, CancellationToken ct) => throw new NotImplementedException();
        public Task<UserInvitation?> GetInvitationByTokenAsync(string token, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<InvitationDto>> GetInvitationsAsync(string tenantId, string? status, CancellationToken ct) => throw new NotImplementedException();
        public Task<InvitationActionResult> ResendInvitationAsync(string tenantId, Guid invitationId, Guid resentByUserId, string? ipAddress, string? requestId, CancellationToken ct) => throw new NotImplementedException();
        public Task<InvitationActionResult> RevokeInvitationAsync(string tenantId, Guid invitationId, Guid revokedByUserId, string? ipAddress, string? requestId, CancellationToken ct) => throw new NotImplementedException();
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
}
