// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-02

using System.Security.Claims;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Covers POST /api/errorlog/client — the anonymous intake for browser errors reported by the
/// Portal. The endpoint's whole contract is the shape of the row it writes, so every test asserts
/// against the <see cref="ErrorLogEntry"/> captured by a stub <see cref="IErrorLogService"/>:
/// the "financia-portal:{tenant}" Source convention with and without a resolved TenantContext,
/// the optional user id, the length clamps applied to unauthenticated browser input, the digest
/// fold-in, and the 204-always response (a crash report must never itself fail the browser).
///
/// Hand-rolled private sealed stubs, no mock library.
/// </summary>
public sealed class ErrorLogControllerClientTests
{
    private const string Tenant = "SgrOneClickSandboxFB2";

    // ── Stubs ─────────────────────────────────────────────────────────────────
    private sealed class StubErrorLogService : IErrorLogService
    {
        public ErrorLogEntry? Captured { get; private set; }
        public int CallCount { get; private set; }
        public Exception? ThrowOnLog { get; set; }

        public Task LogAsync(ErrorLogEntry entry, CancellationToken cancellationToken)
        {
            CallCount++;
            Captured = entry;

            if (ThrowOnLog is not null)
            {
                throw ThrowOnLog;
            }

            return Task.CompletedTask;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static TenantContext BuildTenantContext(string tenantId) => new()
    {
        TenantId               = tenantId,
        FirebaseProjectId      = "project-1",
        FirebaseCredentialJson = "{}",
        RecaptchaSecretKey     = "secret",
    };

    private static ErrorLogController BuildController(
        StubErrorLogService service,
        string?             tenantId = Tenant,
        ClaimsPrincipal?    user     = null)
    {
        var httpContext = new DefaultHttpContext();

        if (tenantId is not null)
        {
            httpContext.Items["TenantContext"] = BuildTenantContext(tenantId);
        }

        if (user is not null)
        {
            httpContext.User = user;
        }

        var sut = new ErrorLogController(service, NullLogger<ErrorLogController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        return sut;
    }

    private static ClaimsPrincipal PrincipalWith(string claimType, string value)
        => new(new ClaimsIdentity(new[] { new Claim(claimType, value) }, "Test"));

    // ── Source convention ─────────────────────────────────────────────────────
    [Fact]
    public async Task LogClientError_WithTenantContext_PrefixesSourceWithFinanciaPortal()
    {
        var service = new StubErrorLogService();
        var sut     = BuildController(service);

        var result = await sut.LogClientErrorAsync(
            new ClientErrorLogRequest("Boom"), CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal($"financia-portal:{Tenant}", service.Captured!.Source);
    }

    [Fact]
    public async Task LogClientError_WithoutTenantContext_FallsBackToUnknown()
    {
        var service = new StubErrorLogService();
        var sut     = BuildController(service, tenantId: null);

        await sut.LogClientErrorAsync(new ClientErrorLogRequest("Boom"), CancellationToken.None);

        Assert.Equal("financia-portal:unknown", service.Captured!.Source);
    }

    // ── UserId is honoured but never required ─────────────────────────────────
    [Fact]
    public async Task LogClientError_WithUidClaim_RecordsUserId()
    {
        var service = new StubErrorLogService();
        var sut     = BuildController(service, user: PrincipalWith("uid", "firebase-uid-1"));

        await sut.LogClientErrorAsync(new ClientErrorLogRequest("Boom"), CancellationToken.None);

        Assert.Equal("firebase-uid-1", service.Captured!.UserId);
    }

    [Fact]
    public async Task LogClientError_WithSubClaimOnly_FallsBackToSub()
    {
        var service = new StubErrorLogService();
        var sut     = BuildController(service, user: PrincipalWith("sub", "subject-1"));

        await sut.LogClientErrorAsync(new ClientErrorLogRequest("Boom"), CancellationToken.None);

        Assert.Equal("subject-1", service.Captured!.UserId);
    }

    [Fact]
    public async Task LogClientError_WhenAnonymous_LeavesUserIdNull()
    {
        var service = new StubErrorLogService();
        var sut     = BuildController(service);

        await sut.LogClientErrorAsync(new ClientErrorLogRequest("Boom"), CancellationToken.None);

        Assert.Null(service.Captured!.UserId);
    }

    // ── Clamping of unauthenticated browser input ─────────────────────────────
    [Fact]
    public async Task LogClientError_ClampsOverlongFieldsToTheirLimits()
    {
        var service = new StubErrorLogService();
        var sut     = BuildController(service);

        var request = new ClientErrorLogRequest(
            Message         : new string('m', 5000),
            ExceptionDetails: new string('e', 5000),
            Url             : "https://portal.test/" + new string('u', 3000),
            StackTrace      : new string('s', 9000));

        await sut.LogClientErrorAsync(request, CancellationToken.None);

        var captured = service.Captured!;
        Assert.Equal(4000, captured.Message.Length);
        Assert.Equal(4000, captured.ExceptionDetails!.Length);
        Assert.Equal(2048, captured.Url!.Length);
        Assert.Equal(8000, captured.StackTrace!.Length);
    }

    [Fact]
    public async Task LogClientError_WithShortFields_LeavesThemIntact()
    {
        var service = new StubErrorLogService();
        var sut     = BuildController(service);

        var request = new ClientErrorLogRequest(
            Message         : "Render failed",
            ExceptionDetails: "TypeError: x is not a function",
            Url             : "https://portal.test/analisis/42",
            StackTrace      : "at Component (page.tsx:11:3)");

        await sut.LogClientErrorAsync(request, CancellationToken.None);

        var captured = service.Captured!;
        Assert.Equal("Render failed", captured.Message);
        Assert.Equal("TypeError: x is not a function", captured.ExceptionDetails);
        Assert.Equal("https://portal.test/analisis/42", captured.Url);
        Assert.Equal("at Component (page.tsx:11:3)", captured.StackTrace);
        Assert.Equal("Error", captured.Level);
    }

    [Fact]
    public async Task LogClientError_WithBlankOptionalFields_WritesNullNotEmptyString()
    {
        var service = new StubErrorLogService();
        var sut     = BuildController(service);

        var request = new ClientErrorLogRequest(
            Message   : "Boom",
            Url       : "   ",
            StackTrace: string.Empty);

        await sut.LogClientErrorAsync(request, CancellationToken.None);

        Assert.Null(service.Captured!.Url);
        Assert.Null(service.Captured!.StackTrace);
    }

    // ── Digest has no column of its own ───────────────────────────────────────
    [Fact]
    public async Task LogClientError_WithDigest_FoldsItIntoExceptionDetails()
    {
        var service = new StubErrorLogService();
        var sut     = BuildController(service);

        var request = new ClientErrorLogRequest(
            Message         : "Boom",
            ExceptionDetails: "TypeError",
            Digest          : "1234567890");

        await sut.LogClientErrorAsync(request, CancellationToken.None);

        Assert.Equal("TypeError | Digest: 1234567890", service.Captured!.ExceptionDetails);
    }

    [Fact]
    public async Task LogClientError_WithDigestOnly_StillLabelsTheEntry()
    {
        var service = new StubErrorLogService();
        var sut     = BuildController(service);

        var request = new ClientErrorLogRequest(Message: "Boom", Digest: "abc");

        await sut.LogClientErrorAsync(request, CancellationToken.None);

        Assert.Equal("ClientError | Digest: abc", service.Captured!.ExceptionDetails);
    }

    [Fact]
    public async Task LogClientError_WithoutDetailsOrDigest_LabelsTheEntryClientError()
    {
        var service = new StubErrorLogService();
        var sut     = BuildController(service);

        await sut.LogClientErrorAsync(new ClientErrorLogRequest("Boom"), CancellationToken.None);

        Assert.Equal("ClientError", service.Captured!.ExceptionDetails);
    }

    // ── Response contract ─────────────────────────────────────────────────────
    [Fact]
    public async Task LogClientError_WhenPersistenceThrows_StillReturns204()
    {
        var service = new StubErrorLogService { ThrowOnLog = new InvalidOperationException("db down") };
        var sut     = BuildController(service);

        var result = await sut.LogClientErrorAsync(
            new ClientErrorLogRequest("Boom"), CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(1, service.CallCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task LogClientError_WithBlankMessage_Returns400AndPersistsNothing(string message)
    {
        var service = new StubErrorLogService();
        var sut     = BuildController(service);

        var result = await sut.LogClientErrorAsync(
            new ClientErrorLogRequest(message), CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
        Assert.Equal(0, service.CallCount);
    }

    [Fact]
    public async Task LogClientError_RecordsTheIpAddressAndATraceId()
    {
        var service = new StubErrorLogService();
        var sut     = BuildController(service);
        sut.ControllerContext.HttpContext.Connection.RemoteIpAddress =
            System.Net.IPAddress.Parse("203.0.113.7");

        await sut.LogClientErrorAsync(new ClientErrorLogRequest("Boom"), CancellationToken.None);

        Assert.Equal("203.0.113.7", service.Captured!.IpAddress);
        Assert.False(string.IsNullOrWhiteSpace(service.Captured!.TraceId));
    }
}
