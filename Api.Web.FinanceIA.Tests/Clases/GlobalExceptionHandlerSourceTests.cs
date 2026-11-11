// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-02

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Clases.Exceptions;
using Api.Web.FinanceIA.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// Pins the product-wide ErrorLogs [Source] convention on the backend side:
/// <see cref="GlobalExceptionHandler"/> must write "financia-bff:{tenant}", so a row can be told
/// apart from one posted by the Portal through /api/errorlog/client ("financia-portal:{tenant}").
/// The "unknown" fallback for an unresolved tenant is part of the contract and is asserted too.
/// </summary>
public sealed class GlobalExceptionHandlerSourceTests
{
    private const string Tenant = "SgrOneClickSandboxFB2";

    private sealed class StubErrorLogService : IErrorLogService
    {
        public ErrorLogEntry? Captured { get; private set; }

        public Task LogAsync(ErrorLogEntry entry, CancellationToken cancellationToken)
        {
            Captured = entry;
            return Task.CompletedTask;
        }
    }

    private static TenantContext BuildTenantContext(string tenantId) => new()
    {
        TenantId               = tenantId,
        FirebaseProjectId      = "project-1",
        FirebaseCredentialJson = "{}",
        RecaptchaSecretKey     = "secret",
    };

    private static DefaultHttpContext BuildHttpContext(StubErrorLogService service, string? tenantId)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IErrorLogService>(service);

        var httpContext = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
        };

        httpContext.Request.Path  = "/api/analyses";
        httpContext.Response.Body = new MemoryStream();

        if (tenantId is not null)
        {
            httpContext.Items["TenantContext"] = BuildTenantContext(tenantId);
        }

        return httpContext;
    }

    private static GlobalExceptionHandler BuildSut()
        => new(NullLogger<GlobalExceptionHandler>.Instance);

    [Fact]
    public async Task TryHandle_WithTenantContext_PrefixesSourceWithFinanciaBff()
    {
        var service     = new StubErrorLogService();
        var httpContext = BuildHttpContext(service, Tenant);

        var handled = await BuildSut().TryHandleAsync(
            httpContext, new InvalidOperationException("boom"), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal($"financia-bff:{Tenant}", service.Captured!.Source);
    }

    [Fact]
    public async Task TryHandle_WithoutTenantContext_FallsBackToUnknown()
    {
        var service     = new StubErrorLogService();
        var httpContext = BuildHttpContext(service, tenantId: null);

        await BuildSut().TryHandleAsync(
            httpContext, new InvalidOperationException("boom"), CancellationToken.None);

        Assert.Equal("financia-bff:unknown", service.Captured!.Source);
    }

    [Fact]
    public async Task TryHandle_ForAppException_PrefixesSourceAndKeepsErrorCodeDetails()
    {
        var service     = new StubErrorLogService();
        var httpContext = BuildHttpContext(service, Tenant);

        await BuildSut().TryHandleAsync(
            httpContext, new TenantNotFoundException(Tenant), CancellationToken.None);

        var captured = service.Captured!;
        Assert.Equal($"financia-bff:{Tenant}", captured.Source);
        Assert.Equal("TenantNotFoundException | ErrorCode: tenant_not_found", captured.ExceptionDetails);
        Assert.Equal(StatusCodes.Status404NotFound, httpContext.Response.StatusCode);
    }

    [Fact]
    public async Task TryHandle_PrefixIsAppliedOnlyToSourceNotToOtherFields()
    {
        var service     = new StubErrorLogService();
        var httpContext = BuildHttpContext(service, Tenant);

        await BuildSut().TryHandleAsync(
            httpContext, new InvalidOperationException("boom"), CancellationToken.None);

        // The Url stays the request path: the prefix discriminates the origin of the row,
        // it is not a general-purpose namespace applied across the entry.
        Assert.Equal("/api/analyses", service.Captured!.Url);
    }
}
