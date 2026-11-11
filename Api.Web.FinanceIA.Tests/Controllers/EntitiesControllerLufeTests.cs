// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-05

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Lufe;
using Api.Web.FinanceIA.Servicios;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Covers the partial-snapshot guard on the refresh-lufe endpoint: a Lufe snapshot whose
/// Entidad identity block is null (the LUFE base entidades/{cuit} call failed while the other
/// endpoints succeeded) must never overwrite better stored data. Scenarios:
///   (a) stored good data + partial snapshot  -> preserved, timestamp unchanged, 200;
///   (b) no stored data     + partial snapshot -> persisted (partial beats none), 200;
///   (c) stored good data + complete snapshot  -> overwritten, timestamp advanced, 200.
/// Plus the outcome split: LUFE unreachable (LookupFailed) -> 502 lufe_service_unavailable, and a
/// PyME with no legajo (NoLegajoReported) -> 404 lufe_no_legajo; neither touches a stored snapshot.
///
/// The second half covers the MiPyME category backfill that rides on the same write:
///   (d) blank column + snapshot carries a categoria -> filled and normalised, in ONE update;
///   (e) column already set                          -> never overwritten, snapshot still refreshes;
///   (f) blank column + snapshot has no certificate  -> stays blank, no throw;
///   (g) blank column + partial snapshot             -> nothing written; the guard above still wins.
///
/// Follows the repo's no-mock-library convention (see EntitiesControllerCasfogTests): the tested
/// path touches only the tenant accessor, the entity repository, the Lufe service, the
/// configuration (for the tenant API key) and the logger, so every other constructor dependency
/// is passed as null! (the constructor performs plain assignment).
/// </summary>
public sealed class EntitiesControllerLufeTests
{
    private const string Cuit = "30-12345678-9";
    private const string TenantId = "tenant-1";

    // ── Stubs ─────────────────────────────────────────────────────────────────
    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; }
    }

    private sealed class StubEntityRepository : IEntityRepository
    {
        public Entity? EntityToReturn { get; set; }
        public bool UpdateCalled => UpdateCallCount > 0;
        public int UpdateCallCount { get; private set; }
        public Entity? Updated { get; private set; }

        public Task<Entity?> GetByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
            => Task.FromResult(EntityToReturn);

        public Task<Entity> UpdateAsync(Entity entity, CancellationToken ct)
        {
            UpdateCallCount++;
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

    private sealed class StubLufeService : ILufeService
    {
        public LufeData? Result { get; set; }

        /// <summary>Overrides the estado derived from <see cref="Result"/> (null = LookupFailed).</summary>
        public LufeConsultaEstado? Estado { get; set; }

        public int GetLufeDataCallCount { get; private set; }

        public Task<LufeLookupResult> GetLufeDataAsync(string tenantId, long cuit, string apiKey, CancellationToken ct)
        {
            GetLufeDataCallCount++;
            var estado = Estado
                ?? (Result is null ? LufeConsultaEstado.LookupFailed : LufeConsultaEstado.LegajoFound);
            return Task.FromResult(new LufeLookupResult(estado, Result));
        }

        public Task<string?> GetMipymeCategoriaAsync(string tenantId, long cuit, string apiKey, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<LufeDocumentDownload?> DownloadDocumentAsync(string absoluteUrl, string apiKey, CancellationToken ct)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Every optional integration entitled (the SGR preset). The entitlement gate (§7.0b) sits in
    /// front of the key check these tests exercise, so it must be open here; the gate itself is
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
    private static TenantContext BuildTenant() => new()
    {
        TenantId               = TenantId,
        FirebaseProjectId      = "proj",
        FirebaseCredentialJson = "{}",
        RecaptchaSecretKey     = "secret"
    };

    private static IConfiguration BuildConfig(string? lufeApiKey = "test-key")
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"Tenants:{TenantId}:LufeApiKey"] = lufeApiKey
            })
            .Build();

    private static EntitiesController BuildSut(
        StubTenantContextAccessor tenant,
        StubEntityRepository      repo,
        StubLufeService           lufe,
        IConfiguration            config)
        => new(
            tenant,   // ITenantContextAccessor
            repo,     // IEntityRepository
            null!,    // IArcaPadronService
            null!,    // IArcaConstanciaService
            null!,    // IArcaWsaaService
            null!,    // IApiCallLogService
            null!,    // IAuditService
            lufe,     // ILufeService
            null!,    // IBcraService
            null!,    // ICasfogService
            null!,    // INosisService
            new IntegrationAccessService(new StubTenantProfileService(), config, NullLogger<IntegrationAccessService>.Instance), // all entitled
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
            config,   // IConfiguration
            NullLogger<EntitiesController>.Instance);

    private static Entity BuildEntity(
        string?   lufeJson       = null,
        DateTime? lufeUpdatedAt  = null,
        string?   mipymeCategory = null) => new()
    {
        EntityId          = Guid.NewGuid(),
        TenantId          = TenantId,
        CUIT              = Cuit,
        BusinessName      = "Test SA",
        MipymeCategory    = mipymeCategory,
        LufeDataJson      = lufeJson,
        LufeDataUpdatedAt = lufeUpdatedAt
    };

    // A complete snapshot carries the Entidad identity block (with the MiPyME certificate nested).
    // Pass categoria: null to model an entidad with no MiPyME certificate on file at all — LUFE
    // omits the block entirely for a company that is not registered as a PyME.
    private static LufeData CompleteSnapshot(string nombre, string? categoria = "Micro") => new()
    {
        Entidad = new LufeEntidad
        {
            Cuit            = 30123456789,
            Nombre          = nombre,
            CertificadoPyme = categoria is null
                                ? null
                                : new LufeCertificadoPyme { Categoria = categoria }
        },
        Autoridades = new[] { new LufeAutoridad { Cuit = 1, Denominacion = "Dir", Cargo = "Presidente" } },
        Indicadores = new LufeIndicador { Periodo = 202401 }
    };

    // A partial-outage snapshot: base entidades/{cuit} failed (Entidad null) but the other
    // endpoints returned data.
    private static LufeData PartialSnapshot() => new()
    {
        Entidad     = null,
        Autoridades = new[] { new LufeAutoridad { Cuit = 1, Denominacion = "Dir", Cargo = "Presidente" } },
        Indicadores = new LufeIndicador { Periodo = 202401 }
    };

    private static string Serialize(LufeData data) => System.Text.Json.JsonSerializer.Serialize(data);

    // ── (a) stored good data + partial snapshot -> preserved, timestamp unchanged ───────────────
    [Fact]
    public async Task RefreshLufe_WhenPartialSnapshotAndStoredDataExists_PreservesDataAndTimestamp()
    {
        var storedJson   = Serialize(CompleteSnapshot("Stored SA"));
        var originalDate  = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entity        = BuildEntity(storedJson, originalDate);
        var repo          = new StubEntityRepository { EntityToReturn = entity };
        var lufe          = new StubLufeService { Result = PartialSnapshot() };
        var sut = BuildSut(new StubTenantContextAccessor { TenantContext = BuildTenant() }, repo, lufe, BuildConfig());

        var result = await sut.RefreshLufeAsync(entity.EntityId, CancellationToken.None);

        var ok       = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<EntityResponse>(ok.Value);

        Assert.False(repo.UpdateCalled);                       // nothing persisted
        Assert.Equal(storedJson, entity.LufeDataJson);         // stored data untouched
        Assert.Equal(originalDate, entity.LufeDataUpdatedAt);  // timestamp unchanged -> Portal "nothing landed"
        Assert.Equal(originalDate, response.LufeDataUpdatedAt);
        Assert.NotNull(response.Lufe);
        Assert.NotNull(response.Lufe!.Entidad);                // identity + certificate still present
        Assert.Equal("Stored SA", response.Lufe.Entidad!.Nombre);
        Assert.Equal("Micro", response.Lufe.Entidad.CertificadoPyme!.Categoria);
    }

    // ── (b) no stored data + partial snapshot -> persisted (partial beats none) ──────────────────
    [Fact]
    public async Task RefreshLufe_WhenPartialSnapshotAndNoStoredData_PersistsPartialSnapshot()
    {
        var entity = BuildEntity(lufeJson: null, lufeUpdatedAt: null);
        var repo   = new StubEntityRepository { EntityToReturn = entity };
        var lufe   = new StubLufeService { Result = PartialSnapshot() };
        var sut = BuildSut(new StubTenantContextAccessor { TenantContext = BuildTenant() }, repo, lufe, BuildConfig());

        var result = await sut.RefreshLufeAsync(entity.EntityId, CancellationToken.None);

        var ok       = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<EntityResponse>(ok.Value);

        Assert.True(repo.UpdateCalled);                        // persisted
        Assert.False(string.IsNullOrEmpty(entity.LufeDataJson));
        Assert.NotNull(entity.LufeDataUpdatedAt);              // timestamp advanced
        Assert.NotNull(response.Lufe);
        Assert.Null(response.Lufe!.Entidad);                   // partial: no identity block, as fetched
        Assert.NotNull(response.Lufe.Autoridades);
    }

    // ── (c) stored good data + complete snapshot -> overwritten as usual ─────────────────────────
    [Fact]
    public async Task RefreshLufe_WhenCompleteSnapshot_OverwritesAndAdvancesTimestamp()
    {
        var originalDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entity       = BuildEntity(Serialize(CompleteSnapshot("Stored SA")), originalDate);
        var repo         = new StubEntityRepository { EntityToReturn = entity };
        var lufe         = new StubLufeService { Result = CompleteSnapshot("Fresh SA") };
        var sut = BuildSut(new StubTenantContextAccessor { TenantContext = BuildTenant() }, repo, lufe, BuildConfig());

        var result = await sut.RefreshLufeAsync(entity.EntityId, CancellationToken.None);

        var ok       = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<EntityResponse>(ok.Value);

        Assert.True(repo.UpdateCalled);
        Assert.NotNull(entity.LufeDataUpdatedAt);
        Assert.True(entity.LufeDataUpdatedAt > originalDate);  // timestamp advanced
        Assert.NotNull(response.Lufe);
        Assert.Equal("Fresh SA", response.Lufe!.Entidad!.Nombre);  // fresh identity persisted
    }

    // ── LookupFailed -> 502 lufe_service_unavailable, snapshot untouched ─────────────────────────
    [Fact]
    public async Task RefreshLufe_WhenLookupFailed_Returns502WithErrorCodeAndLeavesSnapshotUnchanged()
    {
        var storedJson   = Serialize(CompleteSnapshot("Stored SA"));
        var originalDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entity       = BuildEntity(storedJson, originalDate);
        var repo         = new StubEntityRepository { EntityToReturn = entity };
        var lufe         = new StubLufeService { Estado = LufeConsultaEstado.LookupFailed, Result = null };
        var sut = BuildSut(new StubTenantContextAccessor { TenantContext = BuildTenant() }, repo, lufe, BuildConfig());

        var result = await sut.RefreshLufeAsync(entity.EntityId, CancellationToken.None);

        var obj     = Assert.IsType<ObjectResult>(result);
        var problem = Assert.IsType<ProblemDetails>(obj.Value);
        Assert.Equal(502, obj.StatusCode);
        Assert.Equal("lufe_service_unavailable", problem.Extensions["errorCode"]);
        Assert.Equal(
            "El servicio de LUFE no está disponible en este momento. Intentá nuevamente en unos minutos.",
            problem.Detail);
        Assert.False(repo.UpdateCalled);
        Assert.Equal(storedJson, entity.LufeDataJson);
        Assert.Equal(originalDate, entity.LufeDataUpdatedAt);
    }

    // ── NoLegajoReported -> 404 lufe_no_legajo, a stored snapshot is NOT overwritten ────────────
    [Fact]
    public async Task RefreshLufe_WhenNoLegajo_Returns404WithErrorCodeAndLeavesSnapshotUnchanged()
    {
        var storedJson   = Serialize(CompleteSnapshot("Stored SA"));
        var originalDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entity       = BuildEntity(storedJson, originalDate);
        var repo         = new StubEntityRepository { EntityToReturn = entity };
        var lufe         = new StubLufeService { Estado = LufeConsultaEstado.NoLegajoReported, Result = null };
        var sut = BuildSut(new StubTenantContextAccessor { TenantContext = BuildTenant() }, repo, lufe, BuildConfig());

        var result = await sut.RefreshLufeAsync(entity.EntityId, CancellationToken.None);

        var obj     = Assert.IsType<ObjectResult>(result);
        var problem = Assert.IsType<ProblemDetails>(obj.Value);
        Assert.Equal(404, obj.StatusCode);
        Assert.Equal("lufe_no_legajo", problem.Extensions["errorCode"]);
        Assert.Equal("La PyME no tiene legajo en LUFE o no autorizó compartirlo con esta entidad.", problem.Detail);
        Assert.False(repo.UpdateCalled);                       // nothing written
        Assert.Equal(storedJson, entity.LufeDataJson);         // good snapshot survives
        Assert.Equal(originalDate, entity.LufeDataUpdatedAt);
    }

    // ── NoLegajoReported with nothing stored -> still 404, still nothing written ────────────────
    [Fact]
    public async Task RefreshLufe_WhenNoLegajoAndNothingStored_Returns404AndWritesNothing()
    {
        var entity = BuildEntity(lufeJson: null, lufeUpdatedAt: null);
        var repo   = new StubEntityRepository { EntityToReturn = entity };
        var lufe   = new StubLufeService { Estado = LufeConsultaEstado.NoLegajoReported, Result = null };
        var sut = BuildSut(new StubTenantContextAccessor { TenantContext = BuildTenant() }, repo, lufe, BuildConfig());

        var result = await sut.RefreshLufeAsync(entity.EntityId, CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(404, obj.StatusCode);
        Assert.False(repo.UpdateCalled);
        Assert.Null(entity.LufeDataJson);
        Assert.Null(entity.LufeDataUpdatedAt);
    }

    // ── MiPyME backfill ─────────────────────────────────────────────────────────────────────────
    // The category prefill on POST /api/entities/lookup-arca runs under a 4 s LUFE timeout, so a
    // slow LUFE leaves Entities.MipymeCategory null and nothing used to fill it afterwards. These
    // four cases pin the backfill: it fills a BLANK column from the snapshot being persisted, and
    // it is subordinate to both the no-clobber rule and the partial-snapshot guard.

    // ── (d) blank stored category + snapshot carries one -> filled, normalised, in ONE write ─────
    [Fact]
    public async Task RefreshLufe_WhenCategoryIsBlank_BackfillsItFromTheSnapshot()
    {
        var entity = BuildEntity(mipymeCategory: null);
        var repo   = new StubEntityRepository { EntityToReturn = entity };
        // "Pequeña" as LUFE spells it — the shared NormalizeMipymeCategoria maps it to the
        // ASCII "Pequenia" the CK_Entities_MipymeCategory check constraint accepts.
        var lufe   = new StubLufeService { Result = CompleteSnapshot("Fresh SA", "Pequeña") };
        var sut = BuildSut(new StubTenantContextAccessor { TenantContext = BuildTenant() }, repo, lufe, BuildConfig());

        var result = await sut.RefreshLufeAsync(entity.EntityId, CancellationToken.None);

        var ok       = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<EntityResponse>(ok.Value);

        Assert.Equal("Pequenia", entity.MipymeCategory);
        Assert.Equal("Pequenia", response.MipymeCategory);   // the response carries the filled value
        Assert.Equal(1, repo.UpdateCallCount);               // folded into the snapshot write, not a second one
        Assert.Same(entity, repo.Updated);
        Assert.NotNull(entity.LufeDataUpdatedAt);            // the snapshot still landed as before
    }

    // ── (e) a category already set is never overwritten, even when LUFE disagrees ────────────────
    [Fact]
    public async Task RefreshLufe_WhenCategoryAlreadySet_DoesNotOverwriteIt()
    {
        var originalDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entity = BuildEntity(
            Serialize(CompleteSnapshot("Stored SA")), originalDate, mipymeCategory: "Mediana");
        var repo = new StubEntityRepository { EntityToReturn = entity };
        var lufe = new StubLufeService { Result = CompleteSnapshot("Fresh SA", "Micro") };
        var sut = BuildSut(new StubTenantContextAccessor { TenantContext = BuildTenant() }, repo, lufe, BuildConfig());

        var result = await sut.RefreshLufeAsync(entity.EntityId, CancellationToken.None);

        var ok       = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<EntityResponse>(ok.Value);

        // A manual edit survives the refresh, matching the rule RefreshArcaAsync states.
        Assert.Equal("Mediana", entity.MipymeCategory);
        Assert.Equal("Mediana", response.MipymeCategory);

        // The snapshot itself still refreshes — only the column is held back.
        Assert.Equal(1, repo.UpdateCallCount);
        Assert.True(entity.LufeDataUpdatedAt > originalDate);
        Assert.Equal("Fresh SA", response.Lufe!.Entidad!.Nombre);
    }

    // ── (f) blank category + snapshot has no CertificadoPyme -> stays blank, no throw ────────────
    [Fact]
    public async Task RefreshLufe_WhenSnapshotHasNoCertificadoPyme_LeavesTheCategoryBlank()
    {
        var entity = BuildEntity(mipymeCategory: null);
        var repo   = new StubEntityRepository { EntityToReturn = entity };
        var lufe   = new StubLufeService { Result = CompleteSnapshot("Fresh SA", categoria: null) };
        var sut = BuildSut(new StubTenantContextAccessor { TenantContext = BuildTenant() }, repo, lufe, BuildConfig());

        var result = await sut.RefreshLufeAsync(entity.EntityId, CancellationToken.None);

        var ok       = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<EntityResponse>(ok.Value);

        Assert.Null(entity.MipymeCategory);                  // nothing to backfill, and no null-deref
        Assert.Null(response.MipymeCategory);
        Assert.Equal(1, repo.UpdateCallCount);               // the snapshot still persists normally
        Assert.NotNull(entity.LufeDataUpdatedAt);
    }

    // ── (g) the backfill is subordinate to the partial-snapshot guard ────────────────────────────
    [Fact]
    public async Task RefreshLufe_WhenPartialSnapshotAndBlankCategory_WritesNothingAtAll()
    {
        var storedJson   = Serialize(CompleteSnapshot("Stored SA"));
        var originalDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entity       = BuildEntity(storedJson, originalDate, mipymeCategory: null);
        var repo         = new StubEntityRepository { EntityToReturn = entity };
        var lufe         = new StubLufeService { Result = PartialSnapshot() };
        var sut = BuildSut(new StubTenantContextAccessor { TenantContext = BuildTenant() }, repo, lufe, BuildConfig());

        var result = await sut.RefreshLufeAsync(entity.EntityId, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);

        // A snapshot with no Entidad block has no CertificadoPyme either, so there is nothing to
        // backfill — and the backfill must not become a reason to write when the guard said not to.
        Assert.Equal(0, repo.UpdateCallCount);
        Assert.Null(entity.MipymeCategory);
        Assert.Equal(storedJson, entity.LufeDataJson);
        Assert.Equal(originalDate, entity.LufeDataUpdatedAt);
    }

    // ── V3: a FAILED monthly-sales sub-call never wipes a good stored series ───────────────────

    private static List<LufeVentaCompraMes> Series(params string[] periodos)
        => periodos.Select((p, i) => new LufeVentaCompraMes { Periodo = p, Ventas = 1000m + i }).ToList();

    private static LufeData FreshWithFailedSales(string nombre)
    {
        var fresh = CompleteSnapshot(nombre);
        fresh.VentasComprasMensual       = null;
        fresh.VentasComprasMensualEstado = LufeVentasComprasEstados.LookupFailed;   // as LufeService marks it
        return fresh;
    }

    [Fact]
    public async Task RefreshLufe_WhenSalesCallFailed_CarriesTheStoredSeriesForward_DatedByTheStoredSnapshot()
    {
        var storedSnapshot = CompleteSnapshot("Stored SA");
        storedSnapshot.VentasComprasMensual = Series("202507", "202508");
        var storedAt = new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);

        var entity = BuildEntity(Serialize(storedSnapshot), storedAt);
        var repo   = new StubEntityRepository { EntityToReturn = entity };
        var lufe   = new StubLufeService { Result = FreshWithFailedSales("Fresh SA") };
        var sut = BuildSut(new StubTenantContextAccessor { TenantContext = BuildTenant() }, repo, lufe, BuildConfig());

        var result = await sut.RefreshLufeAsync(entity.EntityId, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.True(repo.UpdateCalled);
        Assert.True(entity.LufeDataUpdatedAt > storedAt);           // the rest of the snapshot is fresh

        var saved = System.Text.Json.JsonSerializer.Deserialize<LufeData>(entity.LufeDataJson!)!;
        Assert.Equal("Fresh SA", saved.Entidad!.Nombre);
        Assert.Equal(new[] { "202507", "202508" }, saved.VentasComprasMensual!.Select(m => m.Periodo));
        Assert.Equal(LufeVentasComprasEstados.CarriedForward, saved.VentasComprasMensualEstado);
        Assert.Equal(storedAt, saved.VentasComprasMensualAsOf!.Value.ToUniversalTime());
    }

    [Fact]
    public async Task RefreshLufe_WhenSalesCallFailsTwice_TheCarriedSeriesKeepsItsOriginalDate()
    {
        var originalAsOf = new DateTime(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc);
        var storedSnapshot = CompleteSnapshot("Stored SA");
        storedSnapshot.VentasComprasMensual       = Series("202505", "202506");
        storedSnapshot.VentasComprasMensualEstado = LufeVentasComprasEstados.CarriedForward;
        storedSnapshot.VentasComprasMensualAsOf   = originalAsOf;

        // Stored a month later by the first carrying refresh.
        var entity = BuildEntity(Serialize(storedSnapshot), new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
        var repo   = new StubEntityRepository { EntityToReturn = entity };
        var lufe   = new StubLufeService { Result = FreshWithFailedSales("Fresh SA") };
        var sut = BuildSut(new StubTenantContextAccessor { TenantContext = BuildTenant() }, repo, lufe, BuildConfig());

        await sut.RefreshLufeAsync(entity.EntityId, CancellationToken.None);

        var saved = System.Text.Json.JsonSerializer.Deserialize<LufeData>(entity.LufeDataJson!)!;
        Assert.Equal(LufeVentasComprasEstados.CarriedForward, saved.VentasComprasMensualEstado);
        Assert.Equal(originalAsOf, saved.VentasComprasMensualAsOf!.Value.ToUniversalTime());
        Assert.Equal(2, saved.VentasComprasMensual!.Count);
    }

    [Fact]
    public async Task RefreshLufe_WhenSalesCallFailedAndNothingStored_MarksLookupFailed()
    {
        var entity = BuildEntity(Serialize(CompleteSnapshot("Stored SA")), new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
        var repo   = new StubEntityRepository { EntityToReturn = entity };
        var lufe   = new StubLufeService { Result = FreshWithFailedSales("Fresh SA") };
        var sut = BuildSut(new StubTenantContextAccessor { TenantContext = BuildTenant() }, repo, lufe, BuildConfig());

        await sut.RefreshLufeAsync(entity.EntityId, CancellationToken.None);

        var saved = System.Text.Json.JsonSerializer.Deserialize<LufeData>(entity.LufeDataJson!)!;
        Assert.Null(saved.VentasComprasMensual);
        Assert.Equal(LufeVentasComprasEstados.LookupFailed, saved.VentasComprasMensualEstado);
        Assert.Null(saved.VentasComprasMensualAsOf);
    }

    [Fact]
    public async Task RefreshLufe_WhenSalesCallAnsweredEmpty_StoresNullWithNoMark_EvenOverAStoredSeries()
    {
        // A genuine empty answer is LUFE's answer: it replaces the stored series, exactly as before.
        var storedSnapshot = CompleteSnapshot("Stored SA");
        storedSnapshot.VentasComprasMensual = Series("202507");
        var entity = BuildEntity(Serialize(storedSnapshot), new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
        var repo   = new StubEntityRepository { EntityToReturn = entity };
        var lufe   = new StubLufeService { Result = CompleteSnapshot("Fresh SA") };   // series null, unmarked
        var sut = BuildSut(new StubTenantContextAccessor { TenantContext = BuildTenant() }, repo, lufe, BuildConfig());

        await sut.RefreshLufeAsync(entity.EntityId, CancellationToken.None);

        Assert.DoesNotContain("VentasComprasMensualEstado", entity.LufeDataJson, StringComparison.Ordinal);
        Assert.DoesNotContain("VentasComprasMensualAsOf", entity.LufeDataJson, StringComparison.Ordinal);
        var saved = System.Text.Json.JsonSerializer.Deserialize<LufeData>(entity.LufeDataJson!)!;
        Assert.Null(saved.VentasComprasMensual);
        Assert.Null(saved.VentasComprasMensualEstado);
    }
}
