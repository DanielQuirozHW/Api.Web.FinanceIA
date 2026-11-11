// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-27

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
/// Covers GET /api/entities/{id}/label — the two strings the PyME detail's breadcrumb was fetching
/// the entire entity detail to read.
///
/// <para>
/// The load-bearing test is <see cref="GetLabel_NeverReadsTheFullRowOrTheDetail"/>. The endpoint's
/// whole justification is that a breadcrumb stops paying for FiscalDataJson and LufeDataJson — ~26KB
/// of LUFE on a representative PyME — so it is not enough that the right name comes back: it must
/// come back through <c>GetNameByIdAsync</c> and not through either of the two wider reads. Both of
/// those fail with a named exception on this path.
/// </para>
///
/// <para>
/// The other thing under test is that a 404 stays a 404 and looks exactly like the one every other
/// entity endpoint returns — same status, same Spanish detail — so a client's existing error
/// handling is unaffected by the swap.
/// </para>
///
/// Hand-rolled private sealed stubs (no mock library). Only the repository this path reads is
/// stubbed; the rest of the 18 ctor dependencies are passed as null!. Argument order mirrors the
/// ctor signature.
/// </summary>
public sealed class EntitiesControllerLabelTests
{
    private const string Tenant = "tenant-1";
    private const string Name   = "Metalúrgica del Sur S.A.";
    private const string Cuit   = "30-71234567-1";

    // ── Stubs ─────────────────────────────────────────────────────────────────

    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; }
    }

    private sealed class StubEntityRepository : IEntityRepository
    {
        public EntityNameProjection? NameToReturn { get; set; }

        public string? LastTenantId { get; private set; }
        public Guid?   LastEntityId { get; private set; }

        public Task<EntityNameProjection?> GetNameByIdAsync(
            Guid entityId, string tenantId, CancellationToken ct)
        {
            LastEntityId = entityId;
            LastTenantId = tenantId;
            return Task.FromResult(NameToReturn);
        }

        /// <summary>
        /// The full-row read — all five nvarchar(max) snapshots. This endpoint exists precisely so a
        /// breadcrumb stops paying for them, so a call here is a regression and fails with a named
        /// exception.
        /// </summary>
        public Task<Entity?> GetByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new InvalidOperationException(
                "GET /api/entities/{id}/label must read via GetNameByIdAsync.");

        /// <summary>
        /// The detail projection — still two nvarchar(max) columns wide. Filling the breadcrumb
        /// through it is the exact defect this endpoint removes, so it fails loudly too.
        /// </summary>
        public Task<EntityDetailProjection?> GetDetailByIdAsync(
            Guid entityId, string tenantId, CancellationToken ct)
            => throw new InvalidOperationException(
                "GET /api/entities/{id}/label must not read the entity detail projection.");

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
        public Task<IReadOnlyDictionary<Guid, EntityNameProjection>> GetNameLookupByIdsAsync(
            IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct)
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

    private static EntityNameProjection Proj(Guid entityId) => new()
    {
        EntityId     = entityId,
        BusinessName = Name,
        CUIT         = Cuit
    };

    private static EntitiesController BuildSut(
        StubEntityRepository repo,
        bool withTenant = true)
        => new(
            new StubTenantContextAccessor { TenantContext = withTenant ? BuildTenant() : null }, // ITenantContextAccessor
            repo,   // IEntityRepository
            null!,  // IArcaPadronService
            null!,  // IArcaConstanciaService
            null!,  // IArcaWsaaService
            null!,  // IApiCallLogService
            null!,  // IAuditService
            null!,  // ILufeService
            null!,  // IBcraService
            null!,  // ICasfogService
            null!,  // INosisService
            null!,  // IIntegrationAccessService — never resolved by the label endpoint
            null!,  // IDocumentRepository
            null!,  // IDocumentStorageService
            null!,  // IUserRepository
            null!,  // ICreditLineRepository
            null!,  // IAnalysisRepository
            null!,  // INoteRepository
            null!,  // IPersonLegajoRepository
            null!,  // INosisReportRepository
            null!,  // INosisManagerReportParser
            null!,  // KeyedAsyncLock
            null!,  // IConfiguration
            NullLogger<EntitiesController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    // ── The contract ──────────────────────────────────────────────────────────

    /// <summary>
    /// THE contract test. The two fields come back under the SAME names GET /api/entities/{id}
    /// already uses, so the Portal swaps the URL and keeps reading <c>businessName</c>. Both are NOT
    /// NULL in the database, so a 200 always carries both.
    /// </summary>
    [Fact]
    public async Task GetLabel_ReturnsTheBusinessNameAndCuit()
    {
        var id = Guid.NewGuid();
        var repo = new StubEntityRepository { NameToReturn = Proj(id) };

        var response = await BuildSut(repo).GetLabelAsync(id, CancellationToken.None);

        var body = Assert.IsType<EntityLabelResponse>(Assert.IsType<OkObjectResult>(response).Value);
        Assert.Equal(Name, body.BusinessName);
        Assert.Equal(Cuit, body.CUIT);
    }

    /// <summary>
    /// THE test for the change itself. The name must arrive through the narrow read: neither the
    /// full-row load nor the detail projection may be reached, and both throw a named exception if
    /// they are.
    /// </summary>
    [Fact]
    public async Task GetLabel_NeverReadsTheFullRowOrTheDetail()
    {
        var id = Guid.NewGuid();
        var repo = new StubEntityRepository { NameToReturn = Proj(id) };

        var response = await BuildSut(repo).GetLabelAsync(id, CancellationToken.None);

        Assert.IsType<OkObjectResult>(response);
        Assert.Equal(id, repo.LastEntityId);
    }

    /// <summary>The resolved tenant reaches the query, not some other value or none.</summary>
    [Fact]
    public async Task GetLabel_PassesTheResolvedTenantToTheQuery()
    {
        var id = Guid.NewGuid();
        var repo = new StubEntityRepository { NameToReturn = Proj(id) };

        await BuildSut(repo).GetLabelAsync(id, CancellationToken.None);

        Assert.Equal(Tenant, repo.LastTenantId);
        Assert.Equal(id,     repo.LastEntityId);
    }

    // ── Failure modes ─────────────────────────────────────────────────────────

    /// <summary>
    /// An id that does not resolve in this tenant is a 404 with the SAME ProblemDetails every other
    /// entity endpoint returns — status and Spanish detail both — so existing client error handling
    /// is unaffected by the swap.
    /// </summary>
    [Fact]
    public async Task GetLabel_UnknownId_Returns404()
    {
        var repo = new StubEntityRepository { NameToReturn = null };

        var response = await BuildSut(repo).GetLabelAsync(Guid.NewGuid(), CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(response);
        Assert.Equal(404, obj.StatusCode);
        Assert.Equal(
            "No se encontró la entidad.",
            Assert.IsAssignableFrom<ProblemDetails>(obj.Value).Detail);
    }

    /// <summary>
    /// An entity of another tenant is indistinguishable from one that does not exist — the repository
    /// returns null for both, and so must this endpoint. A different response would confirm that the
    /// id exists somewhere in the system.
    /// </summary>
    [Fact]
    public async Task GetLabel_EntityInAnotherTenant_Returns404()
    {
        // The tenant-isolated query found nothing; the controller cannot tell why, and must not try.
        var repo = new StubEntityRepository { NameToReturn = null };

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
        var repo = new StubEntityRepository { NameToReturn = Proj(Guid.NewGuid()) };

        var response = await BuildSut(repo, withTenant: false)
            .GetLabelAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(500, Assert.IsType<ObjectResult>(response).StatusCode);
        Assert.Null(repo.LastTenantId);
    }
}
