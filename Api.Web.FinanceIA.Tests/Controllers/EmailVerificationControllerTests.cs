// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-21

using System.Security.Claims;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Clases.EmailVerification;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// POST api/bff/auth/email-verification, for the signed-in user only. The REAL
/// <see cref="UserRepository"/> runs over an InMemory <see cref="FinanciaDbContext"/> and the REAL
/// <see cref="EmailVerificationThrottle"/> keeps the budget, so it is production code that scopes the
/// user to the tenant and answers the fourth request with 429.
/// </summary>
public sealed class EmailVerificationControllerTests
{
    private const string Tenant      = "TenantA";
    private const string OtherTenant = "TenantB";
    private const string UserEmail   = "analyst@example.com";
    private const string FirebaseUid = "firebase-uid-1";

    [Fact]
    public async Task Unverified_Queues_AndAnswers202()
    {
        await using var ctx = NewContext();
        User user = await SeedAsync(ctx);
        var fx = new Fixture(ctx) { Verified = false };

        IActionResult result = await fx.Controller(user.UserId).RequestEmailVerificationAsync(CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status202Accepted, objectResult.StatusCode);
        Assert.False(Assert.IsType<EmailVerificationResponse>(objectResult.Value).AlreadyVerified);

        var item = Assert.Single(fx.Queue.Items);
        Assert.Equal(user.UserId, item.UserId);
        Assert.Equal(UserEmail, item.Email);
        Assert.Equal(Tenant, item.TenantContext.TenantId);
        Assert.Equal(FirebaseUid, fx.Firebase.LastUid);
    }

    [Fact]
    public async Task AlreadyVerified_Answers200_AndQueuesNothing()
    {
        await using var ctx = NewContext();
        User user = await SeedAsync(ctx);
        var fx = new Fixture(ctx) { Verified = true };

        IActionResult result = await fx.Controller(user.UserId).RequestEmailVerificationAsync(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.True(Assert.IsType<EmailVerificationResponse>(ok.Value).AlreadyVerified);
        Assert.Empty(fx.Queue.Items);
        Assert.Contains(fx.Logger.Entries, e => e.Contains("AlreadyVerified"));
    }

    [Fact]
    public async Task FourthRequestInTheWindow_Is429_WithRetryAfter_AndTooManyRequests()
    {
        await using var ctx = NewContext();
        User user = await SeedAsync(ctx);
        var fx = new Fixture(ctx) { Verified = false };

        for (var i = 0; i < 3; i++)
        {
            await fx.Controller(user.UserId).RequestEmailVerificationAsync(CancellationToken.None);
        }

        var controller = fx.Controller(user.UserId);
        IActionResult result = await controller.RequestEmailVerificationAsync(CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, objectResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal("too_many_requests", problem.Extensions["errorCode"]);
        Assert.True(int.Parse(controller.Response.Headers.RetryAfter.ToString()) > 0);
        Assert.Equal(3, fx.Queue.Items.Count);
    }

    [Fact]
    public async Task UserOfAnotherTenant_Is404_AndFirebaseIsNotAsked()
    {
        await using var ctx = NewContext();
        User user = await SeedAsync(ctx, tenantId: OtherTenant);
        var fx = new Fixture(ctx) { Verified = false };

        IActionResult result = await fx.Controller(user.UserId).RequestEmailVerificationAsync(CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        Assert.Null(fx.Firebase.LastUid);
        Assert.Empty(fx.Queue.Items);
    }

    [Fact]
    public async Task InactiveUser_Is403()
    {
        await using var ctx = NewContext();
        User user = await SeedAsync(ctx, isActive: false);
        var fx = new Fixture(ctx) { Verified = false };

        IActionResult result = await fx.Controller(user.UserId).RequestEmailVerificationAsync(CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        Assert.Empty(fx.Queue.Items);
    }

    [Fact]
    public async Task NoUserIdClaim_Is401()
    {
        await using var ctx = NewContext();
        var fx = new Fixture(ctx);

        IActionResult result = await fx.Controller(userId: null).RequestEmailVerificationAsync(CancellationToken.None);

        Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public async Task Logs_NeverCarryTheAddress()
    {
        await using var ctx = NewContext();
        User user = await SeedAsync(ctx);
        var fx = new Fixture(ctx) { Verified = false };

        for (var i = 0; i < 4; i++)
        {
            await fx.Controller(user.UserId).RequestEmailVerificationAsync(CancellationToken.None);
        }

        Assert.NotEmpty(fx.Logger.Entries);
        Assert.All(fx.Logger.Entries, e => Assert.DoesNotContain(UserEmail, e, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Endpoint_RequiresTheInternalScheme()
    {
        var attribute = Assert.Single(
            typeof(EmailVerificationController).GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .Cast<AuthorizeAttribute>());
        Assert.Equal("Internal", attribute.AuthenticationSchemes);
        Assert.Empty(typeof(EmailVerificationController)
            .GetMethod(nameof(EmailVerificationController.RequestEmailVerificationAsync))!
            .GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static FinanciaDbContext NewContext()
        => new(new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"email-verification-ctl-{Guid.NewGuid()}")
            .Options);

    private static async Task<User> SeedAsync(FinanciaDbContext ctx, string tenantId = Tenant, bool isActive = true)
    {
        var user = new User
        {
            UserId      = Guid.NewGuid(),
            TenantId    = tenantId,
            FirebaseUid = FirebaseUid,
            Email       = UserEmail,
            DisplayName = "Analyst",
            Role        = Roles.Analyst,
            IsActive    = isActive,
            CreatedAt   = DateTime.UtcNow.AddDays(-10),
            UpdatedAt   = DateTime.UtcNow.AddDays(-10)
        };
        ctx.Users.Add(user);
        ctx.ChangeTracker.DetectChanges(); // AutoDetectChanges is disabled on the context
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();
        return user;
    }

    private sealed class Fixture(FinanciaDbContext ctx)
    {
        private readonly EmailVerificationThrottle _throttle = new(
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new EmailVerificationOptions()));

        public bool? Verified
        {
            get => Firebase.Verified;
            init => Firebase.Verified = value;
        }

        public StubFirebaseUserAdmin Firebase { get; } = new();
        public RecordingQueue Queue { get; } = new();
        public CapturingLogger Logger { get; } = new();

        public EmailVerificationController Controller(Guid? userId)
        {
            var controller = new EmailVerificationController(
                new StubTenantContextAccessor(),
                new UserRepository(ctx, NullLogger<UserRepository>.Instance),
                Firebase,
                _throttle,
                Queue,
                Logger);

            Claim[] claims = userId is null
                ? [new Claim("tenantId", Tenant)]
                : [new Claim("userId", userId.Value.ToString()), new Claim("tenantId", Tenant)];

            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Internal"))
                }
            };
            return controller;
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

    private sealed class StubFirebaseUserAdmin : ITenantFirebaseUserAdmin
    {
        public bool? Verified { get; set; }
        public string? LastUid { get; private set; }

        public Task<bool?> GetEmailVerifiedAsync(string firebaseUid, TenantContext tenantContext, CancellationToken cancellationToken)
        {
            LastUid = firebaseUid;
            return Task.FromResult(Verified);
        }

        public Task MarkEmailVerifiedAsync(string firebaseUid, TenantContext tenantContext, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<string?> GenerateEmailVerificationLinkAsync(
            string email, string continueUrl, TenantContext tenantContext, CancellationToken cancellationToken)
            => throw new NotSupportedException("The controller must not generate the link; the worker does.");
    }

    private sealed class RecordingQueue : IEmailVerificationQueue
    {
        public List<EmailVerificationWorkItem> Items { get; } = [];

        public bool TryEnqueue(EmailVerificationWorkItem item)
        {
            Items.Add(item);
            return true;
        }

        public IAsyncEnumerable<EmailVerificationWorkItem> ReadAllAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class CapturingLogger : ILogger<EmailVerificationController>
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var parts = new List<string> { formatter(state, exception) };
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                parts.AddRange(values.Select(v => v.Value?.ToString() ?? string.Empty));
            }

            Entries.Add(string.Join(" | ", parts));
        }
    }
}
