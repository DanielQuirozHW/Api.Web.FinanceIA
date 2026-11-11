// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-27

using System.Security.Claims;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Covers GET /api/analyses/{id}/label — the one string the analysis detail's breadcrumb was
/// fetching the entire analysis to read.
///
/// <para>
/// The load-bearing test is <see cref="GetLabel_NeverReadsTheFullRowOrTheDetail"/>. The breadcrumb
/// hook runs in the Portal's shell, OUTSIDE the page transition, so it is a SECOND read on every
/// navigation to an analysis detail — and the detail is the heaviest read in the app, still seven
/// <c>nvarchar(max)</c> columns wide after #143 and #147. It is therefore not enough that the right
/// name comes back: it must come back through <c>GetLabelByIdAsync</c> and not through either wider
/// read, both of which fail with a named exception on this path.
/// </para>
///
/// <para>
/// The 404 is the other thing pinned here, and it is deliberately the SAME ProblemDetails
/// /context and /ratios return — status and Spanish detail both — so a client's existing error
/// handling is unaffected by the swap. Note the analysis endpoints use a different string from the
/// entity ones; getting that wrong would be invisible to a status-code-only assertion.
/// </para>
///
/// Hand-rolled private sealed stubs (no mock library). Only the repository this path reads is
/// stubbed; the rest of the ctor dependencies are passed as null! (the constructor performs plain
/// assignment).
/// </summary>
public sealed class AnalysisControllerLabelTests
{
    private const string Tenant      = "tenant-1";
    private const string FirebaseUid = "uid-1";
    private const string Name        = "Metalúrgica del Sur S.A.";

    // ── Stubs ─────────────────────────────────────────────────────────────────

    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; }
    }

    private sealed class StubAnalysisResultRepository : IAnalysisResultRepository
    {
        public AnalysisLabelProjection? LabelToReturn { get; set; }

        public string? LastTenantId { get; private set; }
        public Guid?   LastResultId { get; private set; }

        public Task<AnalysisLabelProjection?> GetLabelByIdAsync(
            Guid resultId, string tenantId, CancellationToken ct)
        {
            LastResultId = resultId;
            LastTenantId = tenantId;
            return Task.FromResult(LabelToReturn);
        }

        /// <summary>
        /// The full-row read. This endpoint exists precisely so the breadcrumb stops paying for the
        /// whole row, so a call here is a regression and fails with a named exception.
        /// </summary>
        public Task<AnalysisResult?> GetByIdAsync(Guid resultId, string tenantId, CancellationToken ct)
            => throw new InvalidOperationException(
                "GET /api/analyses/{id}/label must read via GetLabelByIdAsync.");

        /// <summary>
        /// The detail projection — still seven nvarchar(max) columns wide, and only the first of the
        /// three round-trips the detail path needs to reach a name. Filling the breadcrumb through it
        /// is the exact defect this endpoint removes, so it fails loudly too.
        /// </summary>
        public Task<AnalysisResultDetailProjection?> GetDetailByIdAsync(
            Guid resultId, string tenantId, CancellationToken ct)
            => throw new InvalidOperationException(
                "GET /api/analyses/{id}/label must not read the analysis detail projection.");

        public Task<AnalysisRatiosProjection?> GetRatiosByIdAsync(
            Guid resultId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<AnalysisContextProjection?> GetContextSnapshotByIdAsync(
            Guid resultId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<AnalysisBcraProjection?> GetBcraSnapshotByIdAsync(
            Guid resultId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<AnalysisResult>> GetAllByTenantAsync(string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<AnalysisResult?> GetByDocumentIdAsync(Guid documentId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<AnalysisResult> CreateAsync(AnalysisResult result, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<AnalysisResult>> GetByEntityIdAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<AnalysisResult> UpdateAsync(AnalysisResult result, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, Guid>> GetResultIdsByAnalysisIdsAsync(
            IEnumerable<Guid> analysisIds, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<(IReadOnlyList<AnalysisListProjection> Items, int TotalCount)> GetListPageByTenantAsync(
            string tenantId, int skip, int take, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<(IReadOnlyList<AnalysisListProjection> Items, int TotalCount)> GetListPageByEntityAsync(
            Guid entityId, string tenantId, int skip, int take, CancellationToken ct)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// The entity repository must not be touched either. The name arrives inside the label query's
    /// own join; a second lookup here would be the resolve-then-fetch chain coming back, and its
    /// GetByIdAsync is a full-row load with all five source snapshots on it.
    /// </summary>
    private sealed class ExplodingEntityRepository : IEntityRepository
    {
        private static InvalidOperationException Boom()
            => new("GET /api/analyses/{id}/label resolves the name inside its own query.");

        public Task<Entity?> GetByIdAsync(Guid entityId, string tenantId, CancellationToken ct) => throw Boom();
        public Task<EntityNameProjection?> GetNameByIdAsync(Guid entityId, string tenantId, CancellationToken ct) => throw Boom();
        public Task<IReadOnlyDictionary<Guid, EntityNameProjection>> GetNameLookupByIdsAsync(
            IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct) => throw Boom();
        public Task<EntityDetailProjection?> GetDetailByIdAsync(Guid entityId, string tenantId, CancellationToken ct) => throw Boom();

        public Task<bool> ExistsByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<Entity>> GetAllByTenantAsync(string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Entity?> GetByCuitAsync(string cuit, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Entity> CreateAsync(Entity entity, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Entity> UpdateAsync(Entity entity, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<(IReadOnlyList<EntityListProjection> Items, int TotalCount)> GetListPageByTenantAsync(
            string tenantId, int skip, int take, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<EntitySnapshotProjection?> GetBcraSnapshotAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<EntitySnapshotProjection?> GetCasfogSnapshotAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<EntitySnapshotProjection?> GetLufeSnapshotAsync(
            Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<EntitySnapshotProjection?> GetNosisSnapshotAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static TenantContext BuildTenant() => new()
    {
        TenantId               = Tenant,
        FirebaseProjectId      = "proj",
        FirebaseCredentialJson = "{}",
        RecaptchaSecretKey     = "secret"
    };

    private static AnalysisLabelProjection Proj(Guid resultId, Guid entityId) => new()
    {
        ResultId   = resultId,
        EntityId   = entityId,
        EntityName = Name
    };

    private static AnalysisController BuildSut(
        StubAnalysisResultRepository resultRepo,
        bool withTenant = true)
    {
        var sut = new AnalysisController(
            new StubTenantContextAccessor { TenantContext = withTenant ? BuildTenant() : null },
            null!,                            // documentRepository
            resultRepo,                       // analysisResultRepository
            null!,                            // analysisRepository
            null!,                            // claudeService
            null!,                            // cashFlowReconciliationService
            null!,                            // cashFlowNarrativeService
            null!,                            // alertRatioReconciliationService
            new ExplodingEntityRepository(),  // entityRepository — must never be called
            null!,                            // userRepository
            null!,                            // chatRepository
            null!,                            // creditLineRepository
            null!,                            // operationRepository
            null!,                            // auditService
            null!,                            // apiCallLogService
            null!,                            // excelCashFlowParser
            null!,            // billingOptions
            null!,            // tenantBillingConfigService
            null!,            // userApprovalLimitRepository
            null!,            // decisionNotifier
            null!,            // approvalQuorumService — this suite never reaches a quorum vote
            new Api.Web.FinanceIA.Tests.Legajo.FakeLegajoDocumentRepository(),
            null!,                            // analysisPipeline
            null!,                            // analysisJobQueue
            null!,                            // analysisRunRegistry
            null!,                            // analysisRunStateRepository
            new Api.Web.FinanceIA.Tests.PersonAnalysis.StubPersonAnalysisRepository(),
            NullLogger<AnalysisController>.Instance);

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim("uid", FirebaseUid) }, "Test"))
        };
        sut.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return sut;
    }

    // ── The contract ──────────────────────────────────────────────────────────

    /// <summary>
    /// THE contract test. Both fields come back under the SAME names GET /api/analyses/{id} already
    /// uses, so the Portal swaps the URL and keeps reading <c>entityName</c>. <c>entityId</c> rides
    /// along because it is the join key — free to select, and what lets the crumb link to the PyME.
    /// </summary>
    [Fact]
    public async Task GetLabel_ReturnsTheEntityNameAndId()
    {
        var resultId = Guid.NewGuid();
        var entityId = Guid.NewGuid();
        var repo = new StubAnalysisResultRepository { LabelToReturn = Proj(resultId, entityId) };

        var response = await BuildSut(repo).GetLabelAsync(resultId, CancellationToken.None);

        var body = Assert.IsType<AnalysisLabelResponse>(Assert.IsType<OkObjectResult>(response).Value);
        Assert.Equal(Name,     body.EntityName);
        Assert.Equal(entityId, body.EntityId);
    }

    /// <summary>
    /// THE test for the change itself. The name must arrive through the joined projection: neither
    /// the full-row read, nor the detail projection, nor a second trip to the entity repository may
    /// be reached. All three throw a named exception if they are.
    /// </summary>
    [Fact]
    public async Task GetLabel_NeverReadsTheFullRowOrTheDetail()
    {
        var resultId = Guid.NewGuid();
        var repo = new StubAnalysisResultRepository
        {
            LabelToReturn = Proj(resultId, Guid.NewGuid())
        };

        var response = await BuildSut(repo).GetLabelAsync(resultId, CancellationToken.None);

        Assert.IsType<OkObjectResult>(response);
        Assert.Equal(resultId, repo.LastResultId);
    }

    /// <summary>The resolved tenant reaches the query, not some other value or none.</summary>
    [Fact]
    public async Task GetLabel_PassesTheResolvedTenantToTheQuery()
    {
        var resultId = Guid.NewGuid();
        var repo = new StubAnalysisResultRepository
        {
            LabelToReturn = Proj(resultId, Guid.NewGuid())
        };

        await BuildSut(repo).GetLabelAsync(resultId, CancellationToken.None);

        Assert.Equal(Tenant,   repo.LastTenantId);
        Assert.Equal(resultId, repo.LastResultId);
    }

    // ── Failure modes ─────────────────────────────────────────────────────────

    /// <summary>
    /// A result id that does not resolve in this tenant is a 404 with the SAME ProblemDetails
    /// /context and /ratios return. The detail string is asserted, not just the status: the analysis
    /// endpoints use a different one from the entity endpoints and a status-only check would not
    /// notice the swap.
    /// </summary>
    [Fact]
    public async Task GetLabel_UnknownId_Returns404()
    {
        var repo = new StubAnalysisResultRepository { LabelToReturn = null };

        var response = await BuildSut(repo).GetLabelAsync(Guid.NewGuid(), CancellationToken.None);

        var problem = Assert.IsType<ProblemDetails>(Assert.IsType<ObjectResult>(response).Value);
        Assert.Equal(404, problem.Status);
        Assert.Equal("No se encontró el resultado del análisis.", problem.Detail);
    }

    /// <summary>
    /// A result of another tenant — or one whose analysis or entity row belongs to another — is
    /// indistinguishable from one that does not exist: the query returns null for all of them, and so
    /// must this endpoint. Anything else would confirm that the id exists somewhere in the system.
    /// </summary>
    [Fact]
    public async Task GetLabel_ResultInAnotherTenant_Returns404()
    {
        // The tenant-isolated query found nothing; the controller cannot tell why, and must not try.
        var repo = new StubAnalysisResultRepository { LabelToReturn = null };

        var response = await BuildSut(repo).GetLabelAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(404, Assert.IsType<ObjectResult>(response).StatusCode);
    }

    /// <summary>
    /// Without a resolved tenant there is nothing to isolate on, so the endpoint fails with a 500 and
    /// never reaches the query — asserted by the capture staying null rather than by the response.
    /// </summary>
    [Fact]
    public async Task GetLabel_WithoutTenantContext_Returns500AndNeverQueries()
    {
        var repo = new StubAnalysisResultRepository
        {
            LabelToReturn = Proj(Guid.NewGuid(), Guid.NewGuid())
        };

        var response = await BuildSut(repo, withTenant: false)
            .GetLabelAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(500, Assert.IsType<ObjectResult>(response).StatusCode);
        Assert.Null(repo.LastTenantId);
    }
}
