// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-23

using System.Security.Claims;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Tests.Clases;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// <see cref="DashboardController"/>: who may see what — the confirmer check read off the DATABASE row,
/// as PATCH .../decision reads it — the tenant guard on the caller, and the parity that matters most:
/// the quorum numbers a dashboard item shows are the ones GET /api/analyses/{id} shows for the same
/// analysis — and that the tenant-wide endpoints answer. The attribute gates themselves are pinned in
/// <see cref="AuthorizationMatrixTests"/>.
/// </summary>
public sealed class DashboardControllerTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; }
    }

    private static TenantContext BuildTenant() => new()
    {
        TenantId               = DashboardFixture.Tenant,
        FirebaseProjectId      = "proj",
        FirebaseCredentialJson = "{}",
        RecaptchaSecretKey     = "secret"
    };

    private static ControllerContext ContextFor(string? firebaseUid)
        => new()
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    firebaseUid is null ? [] : [new Claim("uid", firebaseUid)], "Test"))
            }
        };

    private static DashboardController Sut(DashboardFixture f, string? firebaseUid)
        => new(
            new StubTenantContextAccessor { TenantContext = BuildTenant() },
            new UserRepository(f.Context, NullLogger<UserRepository>.Instance),
            f.Service(),
            NullLogger<DashboardController>.Instance)
        {
            ControllerContext = ContextFor(firebaseUid)
        };

    // ── Role and identity ─────────────────────────────────────────────────────

    [Fact]
    public async Task Approvals_AnalystOnTheDatabaseRow_Is403WithItsErrorCode()
    {
        await using var f = DashboardFixture.Create(nameof(Approvals_AnalystOnTheDatabaseRow_Is403WithItsErrorCode));
        var analyst = f.AddUser(Roles.Analyst);
        await f.SaveAsync();

        var result = await Sut(f, analyst.FirebaseUid).GetApprovalsAsync(Ct);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, problem.StatusCode);
        var details = Assert.IsType<ProblemDetails>(problem.Value);
        Assert.Equal("confirmer_role_required", details.Extensions["errorCode"]);
    }

    [Theory]
    [InlineData(Roles.RiskManager)]
    [InlineData(Roles.Admin)]
    public async Task Approvals_ConfirmerOnTheDatabaseRow_Is200(string role)
    {
        await using var f = DashboardFixture.Create($"{nameof(Approvals_ConfirmerOnTheDatabaseRow_Is200)}-{role}");
        var confirmer = f.AddUser(role);
        await f.SaveAsync();

        var result = await Sut(f, confirmer.FirebaseUid).GetApprovalsAsync(Ct);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.IsType<ApprovalsResponse>(ok.Value);
    }

    [Theory]
    [InlineData(Roles.Analyst)]
    [InlineData(Roles.RiskManager)]
    [InlineData(Roles.Admin)]
    public async Task MyWork_AnyDecider_Is200(string role)
    {
        await using var f = DashboardFixture.Create($"{nameof(MyWork_AnyDecider_Is200)}-{role}");
        var user = f.AddUser(role);
        await f.SaveAsync();

        var result = await Sut(f, user.FirebaseUid).GetMyWorkAsync(Ct);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.IsType<MyWorkResponse>(ok.Value);
    }

    [Fact]
    public async Task BothEndpoints_ACallerWhoseRowIsInAnotherTenant_Is401()
    {
        await using var f = DashboardFixture.Create(nameof(BothEndpoints_ACallerWhoseRowIsInAnotherTenant_Is401));
        var foreign = f.AddUser(Roles.Admin, tenant: DashboardFixture.Other);
        await f.SaveAsync();

        Assert.IsType<UnauthorizedResult>(await Sut(f, foreign.FirebaseUid).GetMyWorkAsync(Ct));
        Assert.IsType<UnauthorizedResult>(await Sut(f, foreign.FirebaseUid).GetApprovalsAsync(Ct));
    }

    [Fact]
    public async Task BothEndpoints_NoUserClaimOrNoUserRow_Is401()
    {
        await using var f = DashboardFixture.Create(nameof(BothEndpoints_NoUserClaimOrNoUserRow_Is401));
        await f.SaveAsync();

        Assert.IsType<UnauthorizedResult>(await Sut(f, null).GetMyWorkAsync(Ct));
        Assert.IsType<UnauthorizedResult>(await Sut(f, "uid-unknown").GetApprovalsAsync(Ct));
    }

    [Fact]
    public async Task TenantWideEndpoints_Answer200_WithoutAUserRow()
    {
        // Tenant-wide: the role gate is the attribute (AuthorizationMatrixTests), and no section is keyed
        // by the caller, so not even a uid claim is needed.
        await using var f = DashboardFixture.Create(nameof(TenantWideEndpoints_Answer200_WithoutAUserRow));
        await f.SaveAsync();

        var sut = Sut(f, null);

        Assert.IsType<PortfolioResponse>(Assert.IsType<OkObjectResult>(await sut.GetPortfolioAsync(Ct)).Value);
        Assert.IsType<OversightResponse>(Assert.IsType<OkObjectResult>(await sut.GetOversightAsync(Ct)).Value);
        Assert.IsType<OperationsResponse>(Assert.IsType<OkObjectResult>(await sut.GetOperationsAsync(Ct)).Value);
    }

    [Fact]
    public async Task TenantWideEndpoints_WithoutATenantContext_Are500()
    {
        await using var f = DashboardFixture.Create(nameof(TenantWideEndpoints_WithoutATenantContext_Are500));
        var sut = new DashboardController(
            new StubTenantContextAccessor(),
            new UserRepository(f.Context, NullLogger<UserRepository>.Instance),
            f.Service(),
            NullLogger<DashboardController>.Instance)
        {
            ControllerContext = ContextFor(null)
        };

        Assert.Equal(500, Assert.IsType<ObjectResult>(await sut.GetPortfolioAsync(Ct)).StatusCode);
        Assert.Equal(500, Assert.IsType<ObjectResult>(await sut.GetOversightAsync(Ct)).StatusCode);
        Assert.Equal(500, Assert.IsType<ObjectResult>(await sut.GetOperationsAsync(Ct)).StatusCode);
    }

    // ── Quorum parity with GET /api/analyses/{id} ─────────────────────────────

    [Fact]
    public async Task QuorumNumbers_EqualWhatGetByIdReports_ForTheSameFixture()
    {
        await using var f = DashboardFixture.Create(nameof(QuorumNumbers_EqualWhatGetByIdReports_ForTheSameFixture));

        var m1 = f.AddUser(Roles.RiskManager);
        var m2 = f.AddUser(Roles.RiskManager);
        var m3 = f.AddUser(Roles.Admin);
        var reader = f.AddUser(Roles.Admin);
        var team   = f.AddTeam(3, [m1.UserId, m2.UserId, m3.UserId]);
        var capped = f.AddTeam(5, [m1.UserId, m2.UserId]);
        var teamAnalyst   = f.AddUser(Roles.Analyst, team.TeamId);
        var cappedAnalyst = f.AddUser(Roles.Analyst, capped.TeamId);
        var loneAnalyst   = f.AddUser(Roles.Analyst);

        var entity = f.AddEntity();
        var (twoOfThree, twoOfThreeResult) = f.AddPreApproved(teamAnalyst.UserId, entityId: entity.EntityId);
        f.AddVote(twoOfThree.AnalysisId, m1.UserId);
        f.AddVote(twoOfThree.AnalysisId, m2.UserId);
        var (cappedTwo, cappedResult) = f.AddPreApproved(cappedAnalyst.UserId, entityId: entity.EntityId);
        f.AddVote(cappedTwo.AnalysisId, m3.UserId);
        var (noQuorum, noQuorumResult) = f.AddPreApproved(loneAnalyst.UserId, entityId: entity.EntityId);
        await f.SaveAsync();

        var approvals = Assert.IsType<ApprovalsResponse>(
            Assert.IsType<OkObjectResult>(await Sut(f, reader.FirebaseUid).GetApprovalsAsync(Ct)).Value);
        var waiting = approvals.WaitingForMe!.Items.ToDictionary(i => i.ResultId);

        var detailController = BuildAnalysisController(f);

        foreach (var resultId in new[] { twoOfThreeResult.ResultId, cappedResult.ResultId, noQuorumResult.ResultId })
        {
            var detail = Assert.IsType<AnalysisResultResponse>(
                Assert.IsType<OkObjectResult>(await detailController.GetById(resultId, Ct)).Value);

            Assert.Equal(detail.ApprovalCount,  waiting[resultId].ApprovalCount);
            Assert.Equal(detail.ApprovalQuorum, waiting[resultId].ApprovalQuorum);
        }

        // The fixture exercises a real quorum, a capped one and none at all.
        Assert.Equal((2, 3), (waiting[twoOfThreeResult.ResultId].ApprovalCount, waiting[twoOfThreeResult.ResultId].ApprovalQuorum));
        Assert.Equal((1, 2), (waiting[cappedResult.ResultId].ApprovalCount, waiting[cappedResult.ResultId].ApprovalQuorum));
        Assert.Null(waiting[noQuorumResult.ResultId].ApprovalQuorum);

        // And the analyst's own my-work agrees with both.
        var myWork = Assert.IsType<MyWorkResponse>(
            Assert.IsType<OkObjectResult>(await Sut(f, teamAnalyst.FirebaseUid).GetMyWorkAsync(Ct)).Value);
        var mine = Assert.Single(myWork.AwaitingSignature!.Items);
        Assert.Equal((2, 3), (mine.ApprovalCount, mine.ApprovalQuorum));
    }

    /// <summary>
    /// AnalysisController wired for GET /{id} over the SAME in-memory database, with the REAL quorum
    /// engine and real repositories — so the detail's tally is computed, not scripted.
    /// </summary>
    private static AnalysisController BuildAnalysisController(DashboardFixture f)
    {
        var ctx = f.Context;
        return new AnalysisController(
            new StubTenantContextAccessor { TenantContext = BuildTenant() },
            new DocumentRepository(ctx, NullLogger<DocumentRepository>.Instance),
            new AnalysisResultRepository(ctx, NullLogger<AnalysisResultRepository>.Instance),
            new AnalysisRepository(ctx, NullLogger<AnalysisRepository>.Instance),
            null!,                                  // claudeService
            new CashFlowReconciliationService(),
            new CashFlowNarrativeService(),
            new AlertRatioReconciliationService(),
            new EntityRepository(ctx, NullLogger<EntityRepository>.Instance),
            new UserRepository(ctx, NullLogger<UserRepository>.Instance),
            null!,                                  // chatRepository
            new CreditLineRepository(ctx, NullLogger<CreditLineRepository>.Instance),
            null!,                                  // operationRepository
            null!,                                  // auditService
            null!,                                  // apiCallLogService
            null!,                                  // excelCashFlowParser
            null!,                                  // billingOptions
            null!,                                  // tenantBillingConfigService
            null!,                                  // userApprovalLimitRepository
            null!,                                  // decisionNotifier
            f.Quorum(),
            new Api.Web.FinanceIA.Tests.Legajo.FakeLegajoDocumentRepository(),
            null!,                                  // analysisPipeline
            null!,                                  // analysisJobQueue
            null!,                                  // analysisRunRegistry
            null!,                                  // analysisRunStateRepository
            new PersonAnalysisRepository(ctx, NullLogger<PersonAnalysisRepository>.Instance),
            NullLogger<AnalysisController>.Instance)
        {
            ControllerContext = ContextFor("uid-reader")
        };
    }
}
