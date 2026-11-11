// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-06

using System.Reflection;
using System.Security.Claims;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.AnalysisComparison;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// GET /api/analyses/{id}/previous-comparison: 404 outside the tenant, 200 otherwise, and the declared
/// authorization — every reader, SuperAdmin and Auditor included, on a GET that writes nothing.
/// </summary>
public sealed class AnalysisControllerPreviousComparisonTests
{
    private const string Tenant = "tenant-1";

    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; }
    }

    private sealed class StubComparisonService(AnalysisComparisonResponse? response) : IAnalysisComparisonService
    {
        public (Guid ResultId, string TenantId)? Call { get; private set; }

        public Task<AnalysisComparisonResponse?> CompareWithPreviousAsync(Guid resultId, string tenantId, CancellationToken ct)
        {
            Call = (resultId, tenantId);
            return Task.FromResult(response);
        }
    }

    private static AnalysisController BuildSut(bool withTenant = true)
    {
        var sut = new AnalysisController(
            new StubTenantContextAccessor
            {
                TenantContext = withTenant
                    ? new TenantContext { TenantId = Tenant, FirebaseProjectId = "proj", FirebaseCredentialJson = "{}", RecaptchaSecretKey = "secret" }
                    : null,
            },
            null!,  // documentRepository
            null!,  // analysisResultRepository
            null!,  // analysisRepository
            null!,  // claudeService
            null!,  // cashFlowReconciliationService
            null!,  // cashFlowNarrativeService
            null!,  // alertRatioReconciliationService
            null!,  // entityRepository
            null!,  // userRepository
            null!,  // chatRepository
            null!,  // creditLineRepository
            null!,  // operationRepository
            null!,  // auditService
            null!,  // apiCallLogService
            null!,  // excelCashFlowParser
            null!,  // billingOptions
            null!,  // tenantBillingConfigService
            null!,  // userApprovalLimitRepository
            null!,  // decisionNotifier
            null!,  // approvalQuorumService
            new Api.Web.FinanceIA.Tests.Legajo.FakeLegajoDocumentRepository(),
            null!,  // analysisPipeline
            null!,  // analysisJobQueue
            null!,  // analysisRunRegistry
            null!,  // analysisRunStateRepository
            new Api.Web.FinanceIA.Tests.PersonAnalysis.StubPersonAnalysisRepository(),
            NullLogger<AnalysisController>.Instance);

        sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("uid", "uid-1")], "Test")),
            },
        };
        return sut;
    }

    [Fact]
    public async Task A_result_outside_the_tenant_is_404()
    {
        var service = new StubComparisonService(null);
        var id      = Guid.NewGuid();

        var result = await BuildSut().GetPreviousComparisonAsync(id, service, CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(404, problem.StatusCode);
        Assert.Equal((id, Tenant), service.Call);
    }

    [Fact]
    public async Task Nothing_to_compare_is_still_200()
    {
        var body = AnalysisComparisonResponse.Unavailable(AnalysisComparisonCodes.Reasons.NoPrevious);

        var result = await BuildSut().GetPreviousComparisonAsync(Guid.NewGuid(), new StubComparisonService(body), CancellationToken.None);

        Assert.Same(body, Assert.IsType<OkObjectResult>(result).Value);
    }

    [Fact]
    public async Task Without_a_tenant_the_service_is_never_called()
    {
        var service = new StubComparisonService(null);

        var result = await BuildSut(withTenant: false).GetPreviousComparisonAsync(Guid.NewGuid(), service, CancellationToken.None);

        Assert.Equal(500, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Null(service.Call);
    }

    // ── Authorization (declared metadata — direct calls bypass the middleware) ──

    [Fact]
    public void Declares_every_reader_role()
    {
        const string action = nameof(AnalysisController.GetPreviousComparisonAsync);

        Assert.Equal(Roles.All, AuthorizationTestHelpers.DeclaredRolesOf(typeof(AnalysisController), action));
        foreach (var role in new[] { Roles.Analyst, Roles.RiskManager, Roles.Admin, Roles.Auditor, Roles.SuperAdmin })
            Assert.True(AuthorizationTestHelpers.RoleAllowed(typeof(AnalysisController), action, role), role);
    }

    [Fact]
    public void Is_a_get_on_the_detail_route_family()
    {
        var method = typeof(AnalysisController).GetMethod(nameof(AnalysisController.GetPreviousComparisonAsync))!;

        var get = Assert.Single(method.GetCustomAttributes<HttpGetAttribute>());
        Assert.Equal("{id:guid}/previous-comparison", get.Template);
        Assert.Empty(method.GetCustomAttributes<HttpPostAttribute>());
        Assert.Empty(method.GetCustomAttributes<HttpPutAttribute>());
        Assert.Empty(method.GetCustomAttributes<HttpPatchAttribute>());
        Assert.Empty(method.GetCustomAttributes<HttpDeleteAttribute>());
    }
}
