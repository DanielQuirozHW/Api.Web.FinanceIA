// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-19

using System.Text.Json;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Clases.Exceptions;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// The reCAPTCHA gate of the anonymous POST api/bff/auth/onboarding-token. It used to read
/// <c>Success || Score &lt; threshold</c> — inverted, so a token Google rejected was let through and a
/// genuine one refused. Pinned here in both directions, plus the configured threshold and the 502 a
/// Google outage produces through <see cref="GlobalExceptionHandler"/>. The 422 body is the public
/// contract and is asserted as it was.
/// </summary>
public sealed class BffControllerOnboardingTokenTests
{
    private const string Tenant = "TenantA";

    [Fact]
    public async Task ValidTokenWithHighScore_Returns200_AndIssuesTheToken()
    {
        var issuer = new RecordingIssuer();

        IActionResult result = await NewController(new StubRecaptchaVerifier { Success = true, Score = 0.9 }, issuer)
            .IssueOnboardingTokenAsync(Request(), CancellationToken.None);

        Assert.IsType<OkResult>(result);
        Assert.Equal(1, issuer.CallCount);
    }

    [Theory]
    [InlineData(true, 0.3)]    // valid token, bot-like score
    [InlineData(false, 0.9)]   // Google rejected the token — the case the inverted check let through
    [InlineData(false, 0.1)]
    public async Task FailedRecaptcha_Returns422_AndIssuesNothing(bool success, double score)
    {
        var issuer = new RecordingIssuer();

        IActionResult result = await NewController(new StubRecaptchaVerifier { Success = success, Score = score }, issuer)
            .IssueOnboardingTokenAsync(Request(), CancellationToken.None);

        AssertRecaptchaFailed(result);
        Assert.Equal(0, issuer.CallCount);
    }

    [Fact]
    public async Task Threshold_IsReadFromOnboardingOptions()
    {
        var issuer  = new RecordingIssuer();
        var options = new OnboardingOptions { RecaptchaMinScore = 0.8 };

        IActionResult refused = await NewController(new StubRecaptchaVerifier { Score = 0.7 }, issuer, options)
            .IssueOnboardingTokenAsync(Request(), CancellationToken.None);
        IActionResult accepted = await NewController(new StubRecaptchaVerifier { Score = 0.8 }, issuer, options)
            .IssueOnboardingTokenAsync(Request(), CancellationToken.None);

        AssertRecaptchaFailed(refused);
        Assert.IsType<OkResult>(accepted);
        Assert.Equal(1, issuer.CallCount);
    }

    [Fact]
    public void Threshold_DefaultsToHalf()
        => Assert.Equal(0.5, new OnboardingOptions().RecaptchaMinScore);

    [Fact]
    public async Task GoogleUnreachable_PropagatesRecaptchaException_WhichTheHandlerAnswersWith502()
    {
        var issuer = new RecordingIssuer();

        RecaptchaException ex = await Assert.ThrowsAsync<RecaptchaException>(() =>
            NewController(new StubRecaptchaVerifier { Throws = true }, issuer)
                .IssueOnboardingTokenAsync(Request(), CancellationToken.None));

        Assert.Equal(0, issuer.CallCount);

        var services = new ServiceCollection();
        services.AddSingleton<IErrorLogService, NoOpErrorLogService>();
        var httpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        httpContext.Request.Path  = "/api/bff/auth/onboarding-token";
        httpContext.Response.Body = new MemoryStream();

        Assert.True(await new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance)
            .TryHandleAsync(httpContext, ex, CancellationToken.None));

        Assert.Equal(StatusCodes.Status502BadGateway, httpContext.Response.StatusCode);
        httpContext.Response.Body.Position = 0;
        using JsonDocument body = await JsonDocument.ParseAsync(httpContext.Response.Body);
        Assert.Equal("recaptcha_error", body.RootElement.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task MissingRecaptchaToken_Returns400_WithoutCallingGoogle()
    {
        var recaptcha = new StubRecaptchaVerifier();

        IActionResult result = await NewController(recaptcha, new RecordingIssuer())
            .IssueOnboardingTokenAsync(new AuthTokenRequest { RecaptchaToken = " " }, CancellationToken.None);

        Assert.Equal(400, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        Assert.Equal(0, recaptcha.CallCount);
    }

    private static AuthTokenRequest Request() => new() { RecaptchaToken = "token" };

    private static void AssertRecaptchaFailed(IActionResult result)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(422, objectResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal("recaptcha_failed", problem.Extensions["errorCode"]);
        Assert.Equal("Falló la verificación de reCAPTCHA.", problem.Detail);
        Assert.Equal("https://serviciosfinancieros.api/errors/recaptcha_failed", problem.Type);
    }

    private static BffController NewController(
        StubRecaptchaVerifier recaptcha,
        RecordingIssuer issuer,
        OnboardingOptions? options = null)
    {
        var tenantContext = new TenantContext
        {
            TenantId               = Tenant,
            FirebaseProjectId      = "project",
            FirebaseCredentialJson = "{}",
            RecaptchaSecretKey     = "secret"
        };

        // The onboarding action touches only the accessor, the verifier and the issuer.
        var controller = new BffController(
            new StubTenantContextAccessor { TenantContext = tenantContext },
            null!,    // IFirebaseTokenValidator
            null!,    // ITokenService
            issuer,
            recaptcha,
            null!,    // IUserRepository
            null!,    // IUserInvitationService
            null!,    // ITenantSettingsService
            null!,    // IWebHostEnvironment
            Options.Create(options ?? new OnboardingOptions()),
            NullLogger<BffController>.Instance);

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
        public bool   Throws    { get; set; }
        public int    CallCount { get; private set; }

        public Task<RecaptchaResponse> VerifyAsync(
            string token, TenantContext tenantContext, string? remoteIp, CancellationToken ct)
        {
            CallCount++;
            if (Throws)
            {
                throw new RecaptchaException("Failed to verify reCAPTCHA token.", new HttpRequestException("unreachable"));
            }

            return Task.FromResult(new RecaptchaResponse { Success = Success, Score = Score });
        }
    }

    private sealed class RecordingIssuer : IOnboardingTokenIssuer
    {
        public int CallCount { get; private set; }

        public Task<string> IssueAsync(TenantContext tenantContext, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult("jwt");
        }
    }

    private sealed class NoOpErrorLogService : IErrorLogService
    {
        public Task LogAsync(ErrorLogEntry entry, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
