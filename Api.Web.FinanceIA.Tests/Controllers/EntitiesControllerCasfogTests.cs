// Project : Api.Web.FinanceIA.Tests
// Created : 2026-07-29

using System.Reflection;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Microsoft.Extensions.Configuration;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Covers the CASFOG wiring on EntitiesController: the refresh-casfog endpoint
/// (404 when the entity is absent; 200 + persisted JSON/timestamp on
/// DataFound/NoDataReported; 502 + unchanged snapshot on the transient LookupFailed)
/// and the MapToResponse deserialize-and-attach block (attaches Casfog when present,
/// leaves it null on malformed JSON).
///
/// This is the repo's first controller-instantiation test. Following the established
/// no-mock-library convention, the dependencies are hand-rolled private sealed stubs;
/// the tested paths (refresh + mapping) touch only the tenant accessor, the entity
/// repository, the CASFOG service and the logger, so the remaining constructor
/// dependencies are passed as null! (the constructor performs plain assignment).
/// </summary>
public sealed class EntitiesControllerCasfogTests
{
    private const string Cuit = "30-12345678-9";

    // ── Stubs ─────────────────────────────────────────────────────────────────
    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; }
    }

    private sealed class StubEntityRepository : IEntityRepository
    {
        public Entity? EntityToReturn { get; set; }
        public bool UpdateCalled { get; private set; }
        public Entity? Updated { get; private set; }

        public Task<Entity?> GetByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
            => Task.FromResult(EntityToReturn);

        public Task<Entity> UpdateAsync(Entity entity, CancellationToken ct)
        {
            UpdateCalled = true;
            Updated = entity;
            return Task.FromResult(entity);
        }

        public Task<IReadOnlyList<Entity>> GetAllByTenantAsync(string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<(IReadOnlyList<EntityListProjection> Items, int TotalCount)> GetListPageByTenantAsync(
            string tenantId, int skip, int take, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, EntityNameProjection>> GetNameLookupByIdsAsync(
            IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<EntityDetailProjection?> GetDetailByIdAsync(
            Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<EntitySnapshotProjection?> GetBcraSnapshotAsync(
            Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<EntitySnapshotProjection?> GetCasfogSnapshotAsync(
            Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<EntitySnapshotProjection?> GetLufeSnapshotAsync(
            Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<EntitySnapshotProjection?> GetNosisSnapshotAsync(
            Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<bool> ExistsByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Entity?> GetByCuitAsync(string cuit, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<Entity> CreateAsync(Entity entity, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<EntityNameProjection?> GetNameByIdAsync(
            Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class StubCasfogService : ICasfogService
    {
        public CasfogData Result { get; set; } = new() { Estado = CasfogConsultaEstado.DataFound };
        public int CallCount { get; private set; }

        public Task<CasfogData> GetCentralDeudoresAsync(long cuit, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(Result);
        }
    }

    /// <summary>
    /// Every optional integration entitled (the SGR preset). The entitlement gate (§7.0b) sits in
    /// front of the availability checks these tests exercise, so it must be open here; the gate
    /// itself is covered in EntitiesControllerEntitlementGateTests.
    /// </summary>
    private sealed class StubTenantProfileService : ITenantProfileService
    {
        public Task<TenantProfileResolution> GetEffectiveAsync(string tenantId, CancellationToken ct)
            => Task.FromResult(new TenantProfileResolution(TenantProfilePresets.Sgr, TenantProfileSource.Configured));

        // EntitiesController only ever reads the profile — it gates vendor calls and never writes.
        // Present to satisfy the interface, and loud so a future write from here is not silent.
        public Task<TenantProfileResolution> SetEntityTypeAsync(string tenantId, string entityType, CancellationToken ct)
            => throw new NotSupportedException("EntitiesController must not write the tenant profile.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static TenantContext BuildTenant(string tenantId = "tenant-1") => new()
    {
        TenantId               = tenantId,
        FirebaseProjectId      = "proj",
        FirebaseCredentialJson = "{}",
        RecaptchaSecretKey     = "secret"
    };

    private static EntitiesController BuildSut(
        StubTenantContextAccessor tenant,
        StubEntityRepository      repo,
        StubCasfogService         casfog)
        => new(
            tenant,   // ITenantContextAccessor
            repo,     // IEntityRepository
            null!,    // IArcaPadronService
            null!,    // IArcaConstanciaService
            null!,    // IArcaWsaaService
            null!,    // IApiCallLogService
            null!,    // IAuditService
            null!,    // ILufeService
            null!,    // IBcraService
            casfog,   // ICasfogService
            null!,    // INosisService
            new IntegrationAccessService(new StubTenantProfileService(), new ConfigurationBuilder().Build(), NullLogger<IntegrationAccessService>.Instance), // all entitled
            null!,    // IDocumentRepository
            null!,    // IDocumentStorageService
            null!,    // IUserRepository
            null!,    // ICreditLineRepository
            null!,    // IAnalysisRepository
            null!,    // INoteRepository
            null!,    // IPersonLegajoRepository
            null!,    // INosisReportRepository
            null!,    // INosisManagerReportParser
            null!,    // KeyedAsyncLock
            null!,    // IConfiguration
            NullLogger<EntitiesController>.Instance);

    private static Entity BuildEntity(string? casfogJson = null, DateTime? casfogUpdatedAt = null) => new()
    {
        EntityId            = Guid.NewGuid(),
        TenantId            = "tenant-1",
        CUIT                = Cuit,
        BusinessName        = "Test SA",
        CasfogDataJson      = casfogJson,
        CasfogDataUpdatedAt = casfogUpdatedAt
    };

    private static EntityResponse InvokeMapToResponse(EntitiesController sut, Entity entity)
    {
        var method = typeof(EntitiesController)
            .GetMethod("MapToResponse", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return Assert.IsType<EntityResponse>(method!.Invoke(sut, new object[] { entity }));
    }

    // ── refresh-casfog ─────────────────────────────────────────────────────────
    [Fact]
    public async Task RefreshCasfog_WhenEntityMissing_Returns404()
    {
        var repo   = new StubEntityRepository { EntityToReturn = null };
        var casfog = new StubCasfogService();
        var sut    = BuildSut(new StubTenantContextAccessor { TenantContext = BuildTenant() }, repo, casfog);

        var result = await sut.RefreshCasfogAsync(Guid.NewGuid(), CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(404, obj.StatusCode);
        Assert.Equal(0, casfog.CallCount);      // service not invoked when the entity is absent
        Assert.False(repo.UpdateCalled);
    }

    [Fact]
    public async Task RefreshCasfog_WhenDataFound_PersistsJsonAndTimestampAndReturns200()
    {
        var entity = BuildEntity();
        var repo   = new StubEntityRepository { EntityToReturn = entity };
        var casfog = new StubCasfogService { Result = new CasfogData { Estado = CasfogConsultaEstado.DataFound } };
        var sut    = BuildSut(new StubTenantContextAccessor { TenantContext = BuildTenant() }, repo, casfog);

        var result = await sut.RefreshCasfogAsync(entity.EntityId, CancellationToken.None);

        var ok       = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<EntityResponse>(ok.Value);
        Assert.True(repo.UpdateCalled);
        Assert.False(string.IsNullOrEmpty(entity.CasfogDataJson));
        Assert.NotNull(entity.CasfogDataUpdatedAt);
        Assert.NotNull(response.Casfog);
        Assert.Equal(CasfogConsultaEstado.DataFound, response.Casfog!.Estado);
        Assert.NotNull(response.CasfogDataUpdatedAt);
    }

    [Fact]
    public async Task RefreshCasfog_WhenNoDataReported_PersistsEmptyDataAndReturns200()
    {
        var entity = BuildEntity();
        var repo   = new StubEntityRepository { EntityToReturn = entity };
        var casfog = new StubCasfogService { Result = new CasfogData { Estado = CasfogConsultaEstado.NoDataReported } };
        var sut    = BuildSut(new StubTenantContextAccessor { TenantContext = BuildTenant() }, repo, casfog);

        var result = await sut.RefreshCasfogAsync(entity.EntityId, CancellationToken.None);

        var ok       = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<EntityResponse>(ok.Value);
        Assert.True(repo.UpdateCalled);
        Assert.False(string.IsNullOrEmpty(entity.CasfogDataJson));
        Assert.NotNull(entity.CasfogDataUpdatedAt);
        Assert.NotNull(response.Casfog);
        Assert.Equal(CasfogConsultaEstado.NoDataReported, response.Casfog!.Estado);
    }

    [Fact]
    public async Task RefreshCasfog_WhenLookupFailed_Returns502AndLeavesSnapshotUnchanged()
    {
        var originalJson = "{\"estado\":0}";
        var originalDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entity       = BuildEntity(originalJson, originalDate);
        var repo         = new StubEntityRepository { EntityToReturn = entity };
        var casfog       = new StubCasfogService { Result = new CasfogData { Estado = CasfogConsultaEstado.LookupFailed } };
        var sut          = BuildSut(new StubTenantContextAccessor { TenantContext = BuildTenant() }, repo, casfog);

        var result = await sut.RefreshCasfogAsync(entity.EntityId, CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, obj.StatusCode);
        Assert.False(repo.UpdateCalled);                        // snapshot left untouched
        Assert.Equal(originalJson, entity.CasfogDataJson);
        Assert.Equal(originalDate, entity.CasfogDataUpdatedAt);
    }

    // ── MapToResponse ────────────────────────────────────────────────────────
    [Fact]
    public void MapToResponse_WhenCasfogJsonPresent_AttachesCasfog()
    {
        var updatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var json      = System.Text.Json.JsonSerializer.Serialize(
            new CasfogData { Estado = CasfogConsultaEstado.DataFound });
        var entity = BuildEntity(json, updatedAt);
        var sut    = BuildSut(new StubTenantContextAccessor { TenantContext = BuildTenant() },
                              new StubEntityRepository(), new StubCasfogService());

        var response = InvokeMapToResponse(sut, entity);

        Assert.NotNull(response.Casfog);
        Assert.Equal(CasfogConsultaEstado.DataFound, response.Casfog!.Estado);
        Assert.Equal(updatedAt, response.CasfogDataUpdatedAt);
    }

    [Fact]
    public void MapToResponse_WhenCasfogJsonMalformed_LeavesCasfogNull()
    {
        var entity = BuildEntity("{ not valid json");
        var sut    = BuildSut(new StubTenantContextAccessor { TenantContext = BuildTenant() },
                              new StubEntityRepository(), new StubCasfogService());

        var response = InvokeMapToResponse(sut, entity);

        Assert.Null(response.Casfog);
    }
}
