// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-03

using System.Security.Claims;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Covers <see cref="AnalysisRunsController"/>: the days bounds (400 ProblemDetails with an errorCode),
/// the default window, the tenant and the caller's trigger permission handed to the service as
/// parameters, and the authorization attributes.
/// </summary>
public sealed class AnalysisRunsControllerTests
{
    private const string Tenant = "SgrOneClickSandboxFB2";

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(31)]
    public async Task DaysOutsideTheBounds_Is400_WithItsErrorCode(int days)
    {
        var service = new RecordingService();

        var result = await BuildSut(service).GetRunsAsync(days, CancellationToken.None);

        var bad     = Assert.IsType<BadRequestObjectResult>(result);
        var problem = Assert.IsType<ProblemDetails>(bad.Value);
        Assert.Equal(400, problem.Status);
        Assert.Equal("invalid_days", problem.Extensions["errorCode"]);
        Assert.Null(service.LastCall);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(30)]
    public async Task DaysInsideTheBounds_ReachTheService_WithTheResolvedTenant(int days)
    {
        var service = new RecordingService();

        var result = await BuildSut(service).GetRunsAsync(days, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal((Tenant, days), (service.LastCall!.Value.TenantId, service.LastCall.Value.Days));
    }

    [Fact]
    public void TheDefaultWindow_IsSevenDays()
    {
        var parameter = typeof(AnalysisRunsController)
            .GetMethod(nameof(AnalysisRunsController.GetRunsAsync))!
            .GetParameters()
            .Single(p => p.Name == "days");

        Assert.Equal(7, parameter.DefaultValue);
    }

    [Theory]
    [InlineData(Roles.Analyst, true)]
    [InlineData(Roles.Admin, true)]
    [InlineData(Roles.Auditor, false)]
    [InlineData(Roles.SuperAdmin, false)]
    public async Task TheCallersRole_DecidesWhetherRetryIsOffered(string role, bool canTrigger)
    {
        var service = new RecordingService();

        await BuildSut(service, role).GetRunsAsync(7, CancellationToken.None);

        Assert.Equal(canTrigger, service.LastCall!.Value.CallerCanTrigger);
    }

    [Fact]
    public async Task WithoutATenantContext_Is500ProblemDetails()
    {
        var service = new RecordingService();
        var sut     = BuildSut(service, tenantContext: null);

        var result = await sut.GetRunsAsync(7, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, objectResult.StatusCode);
        Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Null(service.LastCall);
    }

    [Fact]
    public void TheAction_IsOpenToTheRolesThatReadAnalyses_OnTheInternalScheme()
    {
        var controllerAuth = typeof(AnalysisRunsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), false).Cast<AuthorizeAttribute>().Single();
        var actionAuth = typeof(AnalysisRunsController)
            .GetMethod(nameof(AnalysisRunsController.GetRunsAsync))!
            .GetCustomAttributes(typeof(AuthorizeAttribute), false).Cast<AuthorizeAttribute>().Single();
        var route = typeof(AnalysisRunsController)
            .GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>().Single();

        Assert.Equal("Internal", controllerAuth.AuthenticationSchemes);
        Assert.Equal(Roles.All, actionAuth.Roles);
        Assert.Equal("api/analyses/runs", route.Template);
    }

    private static AnalysisRunsController BuildSut(RecordingService service, string role = Roles.Analyst)
        => BuildSut(service, new TenantContext
        {
            TenantId               = Tenant,
            FirebaseProjectId      = "project",
            FirebaseCredentialJson = "{}",
            RecaptchaSecretKey     = "secret"
        }, role);

    private static AnalysisRunsController BuildSut(
        RecordingService service, TenantContext? tenantContext, string role = Roles.Analyst)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "Internal");

        return new AnalysisRunsController(
            new StubTenantContextAccessor { TenantContext = tenantContext },
            service,
            NullLogger<AnalysisRunsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
            }
        };
    }

    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; }
    }

    private sealed class RecordingService : IAnalysisRunListService
    {
        public (string TenantId, int Days, bool CallerCanTrigger)? LastCall { get; private set; }

        public Task<AnalysisRunsResponse> GetRunsAsync(
            string tenantId, int days, bool callerCanTrigger, DateTime nowUtc, CancellationToken ct)
        {
            LastCall = (tenantId, days, callerCanTrigger);
            return Task.FromResult(new AnalysisRunsResponse
            {
                WindowDays  = days,
                GeneratedAt = nowUtc,
                Running     = [],
                Failed      = [],
                Completed   = []
            });
        }
    }
}
