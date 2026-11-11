// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-19

using System.Reflection;
using System.Text.Json;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Clases.PasswordReset;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// POST api/bff/auth/password-reset is anonymous, so its gates are pinned here: reCAPTCHA (403),
/// the per-email budget (429), input validation (400) — and above all that every request that
/// passes them gets the SAME 202 and body, decided before anything about the address is looked up.
/// <para>
/// The controller never sees whether the address exists: it only queues. The real
/// <see cref="PasswordResetEmailThrottle"/> runs over a real <see cref="MemoryCache"/>, so the 429
/// is the production counter, not a stub that was told to refuse.
/// </para>
/// </summary>
public sealed class PasswordResetControllerTests
{
    private const string Tenant = "TenantA";

    // ── Happy path ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ValidRequest_Returns202_AndQueuesOneNormalisedItemForTheResolvedTenant()
    {
        var queue = new RecordingQueue();
        var controller = NewController(queue: queue);

        IActionResult result = await controller.RequestPasswordResetAsync(
            Request("  Analyst@Example.COM "), CancellationToken.None);

        AssertAccepted(result);
        PasswordResetWorkItem item = Assert.Single(queue.Items);
        Assert.Equal("analyst@example.com", item.Email);
        Assert.Equal(Tenant, item.TenantContext.TenantId);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task ResponseBody_IsIdentical_WhateverTheAddress()
    {
        // The controller cannot distinguish a known from an unknown address — that is the point —
        // so two arbitrary addresses must serialise to byte-identical bodies.
        IActionResult first  = await NewController().RequestPasswordResetAsync(Request("known@example.com"), CancellationToken.None);
        IActionResult second = await NewController().RequestPasswordResetAsync(Request("nobody@example.org"), CancellationToken.None);

        Assert.Equal(
            JsonSerializer.Serialize(Assert.IsType<ObjectResult>(first).Value),
            JsonSerializer.Serialize(Assert.IsType<ObjectResult>(second).Value));
    }

    [Fact]
    public async Task QueueFull_StillAnswersTheSame202()
    {
        var queue = new RecordingQueue { Accepts = false };

        IActionResult result = await NewController(queue: queue).RequestPasswordResetAsync(
            Request("analyst@example.com"), CancellationToken.None);

        AssertAccepted(result);
        Assert.Empty(queue.Items);
    }

    // ── reCAPTCHA ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false, 0.9)]  // Google rejected the token
    [InlineData(true, 0.3)]   // valid token, bot-like score
    public async Task FailedRecaptcha_Returns403_AndNothingIsCountedOrQueued(bool success, double score)
    {
        var queue    = new RecordingQueue();
        var throttle = new RecordingThrottle();

        IActionResult result = await NewController(
                recaptcha: new StubRecaptchaVerifier { Success = success, Score = score },
                queue: queue,
                throttle: throttle)
            .RequestPasswordResetAsync(Request("analyst@example.com"), CancellationToken.None);

        AssertProblem(result, 403, "recaptcha_failed");
        Assert.Empty(queue.Items);
        Assert.Equal(0, throttle.CallCount);
    }

    // ── Per-email limit ──────────────────────────────────────────────────────────

    [Fact]
    public async Task FourthRequestForTheSameAddress_Returns429_WithRetryAfter()
    {
        var queue    = new RecordingQueue();
        var throttle = RealThrottle(limit: 3);

        for (var i = 0; i < 3; i++)
        {
            AssertAccepted(await NewController(queue: queue, throttle: throttle)
                .RequestPasswordResetAsync(Request("analyst@example.com"), CancellationToken.None));
        }

        // Different casing is the same address.
        var controller = NewController(queue: queue, throttle: throttle);
        IActionResult result = await controller.RequestPasswordResetAsync(
            Request("ANALYST@example.com"), CancellationToken.None);

        AssertProblem(result, 429, "rate_limit_exceeded_email");
        Assert.True(int.Parse(controller.Response.Headers.RetryAfter.ToString()) > 0);
        Assert.Equal(3, queue.Items.Count);

        // Another address is untouched by that budget.
        AssertAccepted(await NewController(queue: queue, throttle: throttle)
            .RequestPasswordResetAsync(Request("other@example.com"), CancellationToken.None));
    }

    // ── Input validation ─────────────────────────────────────────────────────────

    public static TheoryData<string> MalformedEmails => new()
    {
        "",
        "   ",
        "not-an-email",
        "Name <analyst@example.com>",
        new string('a', 250) + "@example.com"   // 262 chars, over Users.Email's 256
    };

    [Theory]
    [MemberData(nameof(MalformedEmails))]
    public async Task MalformedEmail_Returns400_BeforeReCaptcha(string email)
    {
        var recaptcha = new StubRecaptchaVerifier();

        IActionResult result = await NewController(recaptcha: recaptcha)
            .RequestPasswordResetAsync(Request(email), CancellationToken.None);

        Assert.Equal(400, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        Assert.Equal(0, recaptcha.CallCount);
    }

    [Fact]
    public async Task MissingRecaptchaToken_Returns400()
    {
        IActionResult result = await NewController().RequestPasswordResetAsync(
            new PasswordResetRequest { Email = "analyst@example.com", RecaptchaToken = "" },
            CancellationToken.None);

        Assert.Equal(400, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
    }

    // ── Endpoint shape ───────────────────────────────────────────────────────────

    [Fact]
    public void Endpoint_IsAnonymousPost_UnderTheAuthPrefixThatRequiresXTenantId()
    {
        // TenantResolutionMiddleware demands X-Tenant-ID for every /api/bff/auth/* path, which is
        // what makes the tenant known on an anonymous call. Moving the route out of that prefix
        // would silently drop the requirement.
        Assert.Equal("api/bff", typeof(PasswordResetController).GetCustomAttribute<RouteAttribute>()!.Template);

        MethodInfo action = typeof(PasswordResetController).GetMethod(nameof(PasswordResetController.RequestPasswordResetAsync))!;
        Assert.Equal("auth/password-reset", action.GetCustomAttribute<HttpPostAttribute>()!.Template);
        Assert.NotNull(action.GetCustomAttribute<AllowAnonymousAttribute>());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static PasswordResetRequest Request(string email)
        => new() { Email = email, RecaptchaToken = "recaptcha-token" };

    private static void AssertAccepted(IActionResult result)
    {
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(202, objectResult.StatusCode);
        var body = Assert.IsType<PasswordResetResponse>(objectResult.Value);
        Assert.StartsWith("Si el correo corresponde a una cuenta activa", body.Message);
    }

    private static void AssertProblem(IActionResult result, int status, string errorCode)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(status, objectResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(errorCode, problem.Extensions["errorCode"]);
    }

    private static PasswordResetEmailThrottle RealThrottle(int limit)
        => new(new MemoryCache(new MemoryCacheOptions()),
               Options.Create(new PasswordResetOptions { PerEmailLimit = limit }));

    private static PasswordResetController NewController(
        StubRecaptchaVerifier? recaptcha = null,
        IPasswordResetEmailThrottle? throttle = null,
        RecordingQueue? queue = null)
    {
        var tenantContext = new TenantContext
        {
            TenantId               = Tenant,
            FirebaseProjectId      = "project",
            FirebaseCredentialJson = "{}",
            RecaptchaSecretKey     = "secret"
        };

        var controller = new PasswordResetController(
            new StubTenantContextAccessor { TenantContext = tenantContext },
            recaptcha ?? new StubRecaptchaVerifier(),
            throttle ?? RealThrottle(limit: 3),
            queue ?? new RecordingQueue(),
            Options.Create(new PasswordResetOptions()),
            NullLogger<PasswordResetController>.Instance);

        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; }
    }

    private sealed class StubRecaptchaVerifier : IRecaptchaVerifier
    {
        public bool   Success   { get; set; } = true;
        public double Score     { get; set; } = 0.9;
        public int    CallCount { get; private set; }

        public Task<RecaptchaResponse> VerifyAsync(
            string token, TenantContext tenantContext, string? remoteIp, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(new RecaptchaResponse { Success = Success, Score = Score });
        }
    }

    private sealed class RecordingThrottle : IPasswordResetEmailThrottle
    {
        public int CallCount { get; private set; }

        public PasswordResetThrottleDecision Register(string tenantId, string email)
        {
            CallCount++;
            return new PasswordResetThrottleDecision(true, 0);
        }
    }

    private sealed class RecordingQueue : IPasswordResetQueue
    {
        public bool Accepts { get; set; } = true;
        public List<PasswordResetWorkItem> Items { get; } = [];

        public bool TryEnqueue(PasswordResetWorkItem item)
        {
            if (!Accepts) return false;
            Items.Add(item);
            return true;
        }

        public IAsyncEnumerable<PasswordResetWorkItem> ReadAllAsync(CancellationToken ct)
            => throw new NotSupportedException();
    }
}
