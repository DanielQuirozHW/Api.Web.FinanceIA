// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-25

using System.Reflection;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Nosis;
using Api.Web.FinanceIA.Servicios;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Covers the NOSIS wiring on EntitiesController: the refresh-nosis endpoint (404 when the entity is
/// absent; 400 when NOSIS is switched off or unconfigured; 200 + persisted JSON/timestamp on
/// DataFound/NoDataReported; 502 + unchanged snapshot on the transient LookupFailed) and the
/// MapToResponse deserialize-and-attach block.
///
/// Modelled on EntitiesControllerCasfogTests, with two deliberate differences:
///
/// 1. A real IConfiguration is passed rather than null!, because the endpoint's 400 guard reads
///    Nosis:Enabled / ApiKey / VR. Everything else the tested paths do not touch stays null!.
/// 2. The stub captures the tenantId. INosisService takes one (ICasfogService does not) because it
///    writes an api-call log row, and a wrong tenant there would be invisible in production.
///
/// The 400-vs-502 split is the point of several of these tests: NosisService collapses "disabled",
/// "no credentials" and "NOSIS is down" into a single LookupFailed, and the endpoint must not let the
/// first two reach the caller as a retryable 502.
/// </summary>
public sealed class EntitiesControllerNosisTests
{
    private const string Cuit   = "30-12345678-9";
    private const string Tenant = "tenant-1";

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

    private sealed class StubNosisService : INosisService
    {
        public NosisData Result { get; set; } = new() { Estado = NosisConsultaEstado.DataFound };
        public int CallCount { get; private set; }
        public string? LastTenantId { get; private set; }
        public long LastCuit { get; private set; }
        public NosisVariableGroup? LastGrupo { get; private set; }

        public Task<NosisData> GetVariablesAsync(
            string tenantId, long cuit, NosisVariableGroup grupo, CancellationToken ct)
        {
            CallCount++;
            LastTenantId = tenantId;
            LastCuit     = cuit;
            LastGrupo    = grupo;
            return Task.FromResult(Result);
        }
    }

    /// <summary>
    /// Every optional integration entitled (the SGR preset). The entitlement gate (§7.0b) sits in
    /// front of the Nosis:* availability guard these tests exercise, so it must be open here: the
    /// 400s asserted below come from the availability guard, not the profile. The gate itself is
    /// covered in EntitiesControllerEntitlementGateTests.
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
    private static TenantContext BuildTenant(string tenantId = Tenant) => new()
    {
        TenantId               = tenantId,
        FirebaseProjectId      = "proj",
        FirebaseCredentialJson = "{}",
        RecaptchaSecretKey     = "secret"
    };

    private static IConfiguration BuildConfig(
        bool enabled = true, string apiKey = "test-key", string vr = "42")
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nosis:Enabled"] = enabled ? "true" : "false",
                ["Nosis:ApiKey"]  = apiKey,
                ["Nosis:VR"]      = vr
            })
            .Build();

    private static EntitiesController BuildSut(
        StubTenantContextAccessor tenant,
        StubEntityRepository      repo,
        StubNosisService          nosis,
        IConfiguration?           config = null)
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
            null!,    // ICasfogService
            nosis,    // INosisService
            new IntegrationAccessService(new StubTenantProfileService(), config ?? BuildConfig(), NullLogger<IntegrationAccessService>.Instance), // all entitled
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
            config ?? BuildConfig(),   // IConfiguration — read by the refresh-nosis 400 guard
            NullLogger<EntitiesController>.Instance);

    private static Entity BuildEntity(
        string? nosisJson = null, DateTime? nosisUpdatedAt = null, string cuit = Cuit) => new()
    {
        EntityId           = Guid.NewGuid(),
        TenantId           = Tenant,
        CUIT               = cuit,
        BusinessName       = "Test SA",
        NosisDataJson      = nosisJson,
        NosisDataUpdatedAt = nosisUpdatedAt
    };

    private static EntityResponse InvokeMapToResponse(EntitiesController sut, Entity entity)
    {
        var method = typeof(EntitiesController)
            .GetMethod("MapToResponse", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return Assert.IsType<EntityResponse>(method!.Invoke(sut, new object[] { entity }));
    }

    // ── refresh-nosis: guards ─────────────────────────────────────────────────
    [Fact]
    public async Task RefreshNosis_WhenEntityMissing_Returns404()
    {
        var repo  = new StubEntityRepository { EntityToReturn = null };
        var nosis = new StubNosisService();
        var sut   = BuildSut(new StubTenantContextAccessor { TenantContext = BuildTenant() }, repo, nosis);

        var result = await sut.RefreshNosisAsync(Guid.NewGuid(), CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(404, obj.StatusCode);
        Assert.Equal(0, nosis.CallCount);       // service not invoked when the entity is absent
        Assert.False(repo.UpdateCalled);
    }

    [Fact]
    public async Task RefreshNosis_WhenDisabled_Returns400AndNeverCallsTheService()
    {
        // The shipped default is Nosis:Enabled=false. A 502 here would tell the portal to retry
        // something that cannot succeed until an operator provisions the key.
        var entity = BuildEntity();
        var repo   = new StubEntityRepository { EntityToReturn = entity };
        var nosis  = new StubNosisService();
        var sut    = BuildSut(new StubTenantContextAccessor { TenantContext = BuildTenant() },
                              repo, nosis, BuildConfig(enabled: false));

        var result = await sut.RefreshNosisAsync(entity.EntityId, CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, obj.StatusCode);
        Assert.Equal(0, nosis.CallCount);
        Assert.False(repo.UpdateCalled);
    }

    [Theory]
    [InlineData("", "42")]
    [InlineData("test-key", "")]
    [InlineData("", "")]
    public async Task RefreshNosis_WhenEnabledButUnconfigured_Returns400(string apiKey, string vr)
    {
        var entity = BuildEntity();
        var repo   = new StubEntityRepository { EntityToReturn = entity };
        var nosis  = new StubNosisService();
        var sut    = BuildSut(new StubTenantContextAccessor { TenantContext = BuildTenant() },
                              repo, nosis, BuildConfig(enabled: true, apiKey: apiKey, vr: vr));

        var result = await sut.RefreshNosisAsync(entity.EntityId, CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, obj.StatusCode);
        Assert.Equal(0, nosis.CallCount);
    }

    [Fact]
    public async Task RefreshNosis_WhenStoredCuitIsMalformed_Returns502AndNeverCallsTheService()
    {
        var entity = BuildEntity(cuit: "no-es-un-cuit");
        var repo   = new StubEntityRepository { EntityToReturn = entity };
        var nosis  = new StubNosisService();
        var sut    = BuildSut(new StubTenantContextAccessor { TenantContext = BuildTenant() }, repo, nosis);

        var result = await sut.RefreshNosisAsync(entity.EntityId, CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, obj.StatusCode);
        Assert.Equal(0, nosis.CallCount);
        Assert.False(repo.UpdateCalled);
    }

    // ── refresh-nosis: happy paths ────────────────────────────────────────────
    [Fact]
    public async Task RefreshNosis_WhenDataFound_PersistsJsonAndTimestampAndReturns200()
    {
        var entity = BuildEntity();
        var repo   = new StubEntityRepository { EntityToReturn = entity };
        var nosis  = new StubNosisService
        {
            Result = new NosisData { Estado = NosisConsultaEstado.DataFound, ScoreVigente = 612 }
        };
        var sut = BuildSut(new StubTenantContextAccessor { TenantContext = BuildTenant() }, repo, nosis);

        var result = await sut.RefreshNosisAsync(entity.EntityId, CancellationToken.None);

        var ok       = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<EntityResponse>(ok.Value);
        Assert.True(repo.UpdateCalled);
        Assert.False(string.IsNullOrEmpty(entity.NosisDataJson));
        Assert.NotNull(entity.NosisDataUpdatedAt);
        Assert.NotNull(response.Nosis);
        Assert.Equal(NosisConsultaEstado.DataFound, response.Nosis!.Estado);
        Assert.Equal(612, response.Nosis.ScoreVigente);
        Assert.NotNull(response.NosisDataUpdatedAt);
    }

    [Fact]
    public async Task RefreshNosis_WhenNoDataReported_PersistsEmptyDataAndReturns200()
    {
        // "NOSIS has nothing on file for this CUIT" is a real answer and worth storing — it is not
        // a failure, and re-asking would just burn a vendor call.
        var entity = BuildEntity();
        var repo   = new StubEntityRepository { EntityToReturn = entity };
        var nosis  = new StubNosisService
        {
            Result = new NosisData { Estado = NosisConsultaEstado.NoDataReported }
        };
        var sut = BuildSut(new StubTenantContextAccessor { TenantContext = BuildTenant() }, repo, nosis);

        var result = await sut.RefreshNosisAsync(entity.EntityId, CancellationToken.None);

        var ok       = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<EntityResponse>(ok.Value);
        Assert.True(repo.UpdateCalled);
        Assert.False(string.IsNullOrEmpty(entity.NosisDataJson));
        Assert.NotNull(entity.NosisDataUpdatedAt);
        Assert.NotNull(response.Nosis);
        Assert.Equal(NosisConsultaEstado.NoDataReported, response.Nosis!.Estado);
    }

    [Fact]
    public async Task RefreshNosis_PassesTheResolvedTenantAndDigitsOnlyCuitToTheService()
    {
        // The tenant lands on the api-call log row; a wrong one there would be invisible in prod.
        var entity = BuildEntity();
        var repo   = new StubEntityRepository { EntityToReturn = entity };
        var nosis  = new StubNosisService();
        var sut    = BuildSut(new StubTenantContextAccessor { TenantContext = BuildTenant("tenant-9") },
                              repo, nosis);

        await sut.RefreshNosisAsync(entity.EntityId, CancellationToken.None);

        Assert.Equal(1, nosis.CallCount);
        Assert.Equal("tenant-9", nosis.LastTenantId);
        Assert.Equal(30123456789L, nosis.LastCuit);   // dashes stripped

        // The PyME's own snapshot comes from the PRINCIPAL variable group. NOSIS answers a wrong
        // group with the wrong variables rather than an error, so this is pinned rather than assumed
        // — the fiador route asks for the other group, and nothing else distinguishes the two calls.
        Assert.Equal(NosisVariableGroup.Principal, nosis.LastGrupo);
    }

    // ── refresh-nosis: the transient failure must not clobber ─────────────────
    [Fact]
    public async Task RefreshNosis_WhenLookupFailed_Returns502AndLeavesSnapshotUnchanged()
    {
        var originalJson = "{\"estado\":0,\"scoreVigente\":612}";
        var originalDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entity       = BuildEntity(originalJson, originalDate);
        var repo         = new StubEntityRepository { EntityToReturn = entity };
        var nosis        = new StubNosisService
        {
            Result = new NosisData { Estado = NosisConsultaEstado.LookupFailed }
        };
        var sut = BuildSut(new StubTenantContextAccessor { TenantContext = BuildTenant() }, repo, nosis);

        var result = await sut.RefreshNosisAsync(entity.EntityId, CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, obj.StatusCode);
        Assert.False(repo.UpdateCalled);                        // snapshot left untouched
        Assert.Equal(originalJson, entity.NosisDataJson);
        Assert.Equal(originalDate, entity.NosisDataUpdatedAt);
    }

    // ── MapToResponse ─────────────────────────────────────────────────────────
    [Fact]
    public void MapToResponse_WhenNosisJsonPresent_AttachesNosis()
    {
        var updatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var json      = System.Text.Json.JsonSerializer.Serialize(
            new NosisData { Estado = NosisConsultaEstado.DataFound, RazonSocial = "PYME EJEMPLO SRL" });
        var entity = BuildEntity(json, updatedAt);
        var sut    = BuildSut(new StubTenantContextAccessor { TenantContext = BuildTenant() },
                              new StubEntityRepository(), new StubNosisService());

        var response = InvokeMapToResponse(sut, entity);

        Assert.NotNull(response.Nosis);
        Assert.Equal(NosisConsultaEstado.DataFound, response.Nosis!.Estado);
        Assert.Equal("PYME EJEMPLO SRL", response.Nosis.RazonSocial);
        Assert.Equal(updatedAt, response.NosisDataUpdatedAt);
    }

    [Fact]
    public void MapToResponse_WhenNosisJsonMalformed_LeavesNosisNull()
    {
        var entity = BuildEntity("{ not valid json");
        var sut    = BuildSut(new StubTenantContextAccessor { TenantContext = BuildTenant() },
                              new StubEntityRepository(), new StubNosisService());

        var response = InvokeMapToResponse(sut, entity);

        Assert.Null(response.Nosis);
    }
}
