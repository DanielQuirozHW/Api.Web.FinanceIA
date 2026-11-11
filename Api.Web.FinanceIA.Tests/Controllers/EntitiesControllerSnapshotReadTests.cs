// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-25

using System.Text.Json;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Nosis;
using Api.Web.FinanceIA.Servicios;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Covers the three on-demand snapshot reads — GET {id}/bcra, {id}/casfog and {id}/nosis — that
/// GetById stopped shipping payloads for.
///
/// <para>
/// The load-bearing tests are the <c>NeverCallsTheExternalService</c> ones. These endpoints exist
/// precisely because they are NOT the refresh-* endpoints: they read the stored column and return
/// it, with no gateway call, no write and no side effect. That distinction is what makes them safe
/// to call every time a user opens a tab, and it matters most for NOSIS, which is billed per query
/// — a read that silently re-queried the source would cost money on every tab click. The service
/// stubs here throw on any call, so a regression that reintroduces a gateway call fails loudly.
/// </para>
///
/// <para>
/// The second thing under test is the 404-vs-null split: a missing entity is a 404, while an entity
/// that exists but has never had that source fetched is a 200 carrying a null payload. Collapsing
/// those two into one response would make "never fetched" indistinguishable from "wrong id".
/// </para>
/// </summary>
public sealed class EntitiesControllerSnapshotReadTests
{
    private const string Tenant = "tenant-1";

    // ── Stubs ─────────────────────────────────────────────────────────────────

    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; }
    }

    /// <summary>
    /// Captures Warning-level log calls. The snapshot endpoints warn in exactly one place —
    /// <c>ParseSnapshot</c>'s catch — so an empty list against valid JSON, and exactly one entry
    /// against garbage, is direct evidence about what was parsed.
    /// </summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }

    /// <summary>
    /// Returns the configured snapshot for whichever source is asked for, and records which reads
    /// happened. <c>GetByIdAsync</c> throws: a snapshot read that fell back to the full-row load
    /// would defeat the whole change, so it must not even be reachable from these endpoints.
    /// </summary>
    private sealed class StubEntityRepository : IEntityRepository
    {
        public EntitySnapshotProjection? BcraToReturn   { get; set; }
        public EntitySnapshotProjection? CasfogToReturn { get; set; }
        public EntitySnapshotProjection? NosisToReturn  { get; set; }

        public string? LastTenantId { get; private set; }
        public Guid?   LastEntityId { get; private set; }

        public Task<EntitySnapshotProjection?> GetBcraSnapshotAsync(
            Guid entityId, string tenantId, CancellationToken ct)
        {
            LastEntityId = entityId; LastTenantId = tenantId;
            return Task.FromResult(BcraToReturn);
        }

        public Task<EntitySnapshotProjection?> GetCasfogSnapshotAsync(
            Guid entityId, string tenantId, CancellationToken ct)
        {
            LastEntityId = entityId; LastTenantId = tenantId;
            return Task.FromResult(CasfogToReturn);
        }

        public Task<EntitySnapshotProjection?> GetLufeSnapshotAsync(

            Guid entityId, string tenantId, CancellationToken ct)

            => throw new NotImplementedException();


        public Task<EntitySnapshotProjection?> GetNosisSnapshotAsync(
            Guid entityId, string tenantId, CancellationToken ct)
        {
            LastEntityId = entityId; LastTenantId = tenantId;
            return Task.FromResult(NosisToReturn);
        }

        public Task<Entity?> GetByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException("A snapshot read must never load the full row.");
        public Task<EntityDetailProjection?> GetDetailByIdAsync(
            Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<Entity>> GetAllByTenantAsync(string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<bool> ExistsByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
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

        public Task<EntityNameProjection?> GetNameByIdAsync(
            Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// The three gateways, wired in but rigged to blow up. Any call from a READ endpoint is a bug,
    /// so failing with a named exception beats passing null! and getting an opaque NullReference.
    /// </summary>
    private sealed class ExplodingBcraService : IBcraService
    {
        private static Exception Boom()
            => new InvalidOperationException("A read endpoint must never consult BCRA.");

        public Task<BcraData?> GetBcraDataForAnalysisAsync(string cuit, CancellationToken ct) => throw Boom();
        public Task<BcraDeudaData> GetDeudaAsync(string cuit, CancellationToken ct) => throw Boom();
        public Task<BcraChequesData> GetChequesRechazadosAsync(string cuit, CancellationToken ct) => throw Boom();
        public Task<BcraMacroData?> GetMacroAsync(CancellationToken ct) => throw Boom();
        public Task<BcraCambiarioData?> GetCambiarioAsync(CancellationToken ct) => throw Boom();
    }

    private sealed class ExplodingCasfogService : ICasfogService
    {
        public Task<CasfogData> GetCentralDeudoresAsync(long cuit, CancellationToken ct)
            => throw new InvalidOperationException("A read endpoint must never consult CASFOG.");
    }

    private sealed class ExplodingNosisService : INosisService
    {
        public Task<NosisData> GetVariablesAsync(
            string tenantId, long cuit, NosisVariableGroup grupo, CancellationToken ct)
            => throw new InvalidOperationException("A read endpoint must never consult NOSIS.");
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static TenantContext BuildTenant() => new()
    {
        TenantId               = Tenant,
        FirebaseProjectId      = "proj",
        FirebaseCredentialJson = "{}",
        RecaptchaSecretKey     = "secret"
    };

    private static EntitiesController BuildSut(
        StubEntityRepository repo,
        RecordingLogger<EntitiesController> logger,
        bool withTenant = true)
        => new(
            new StubTenantContextAccessor { TenantContext = withTenant ? BuildTenant() : null }, // ITenantContextAccessor
            repo,                        // IEntityRepository
            null!,                       // IArcaPadronService
            null!,                       // IArcaConstanciaService
            null!,                       // IArcaWsaaService
            null!,                       // IApiCallLogService
            null!,                       // IAuditService
            null!,                       // ILufeService
            new ExplodingBcraService(),  // IBcraService   — must never be called
            new ExplodingCasfogService(),// ICasfogService — must never be called
            new ExplodingNosisService(), // INosisService  — must never be called
            null!,                       // IIntegrationAccessService — a read endpoint never resolves it
            null!,                       // IDocumentRepository
            null!,                       // IDocumentStorageService
            null!,                       // IUserRepository
            null!,                       // ICreditLineRepository
            null!,                       // IAnalysisRepository
            null!,                       // INoteRepository
            null!,                       // IPersonLegajoRepository
            new NoNosisReportRepository(),// INosisReportRepository — GET nosis reads the (absent) report
            null!,                       // INosisManagerReportParser
            null!,                       // KeyedAsyncLock
            null!,                       // IConfiguration
            logger)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    /// <summary>An entity that never had a NOSIS Manager report imported.</summary>
    private sealed class NoNosisReportRepository : INosisReportRepository
    {
        public Task<NosisReportSubject?> GetSubjectAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new InvalidOperationException("A read endpoint never resolves the import subject.");

        public Task<NosisReportSnapshot?> GetAsync(Guid entityId, string tenantId, CancellationToken ct)
            => Task.FromResult<NosisReportSnapshot?>(new NosisReportSnapshot { EntityId = entityId });

        public Task<(string? Sha256, string? ParserVersion)> GetStoredFingerprintAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new InvalidOperationException("A read endpoint never writes.");

        public Task<bool> SaveAsync(Guid entityId, string tenantId, string json, DateTime reportDate, DateTime importedAt, CancellationToken ct)
            => throw new InvalidOperationException("A read endpoint never writes.");

        public Task<bool> ClearAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new InvalidOperationException("A read endpoint never writes.");
    }

    private static DateTime Utc(int day) => new(2026, 8, day, 12, 0, 0, DateTimeKind.Utc);

    private static EntitySnapshotProjection Snap(Guid id, string? json, DateTime? updatedAt) => new()
    {
        EntityId  = id,
        Json      = json,
        UpdatedAt = updatedAt
    };

    // ── The stored snapshot comes back ────────────────────────────────────────

    /// <summary>The BCRA tab gets the stored snapshot and its refresh timestamp.</summary>
    [Fact]
    public async Task GetBcra_ReturnsTheStoredSnapshotAndItsTimestamp()
    {
        var id = Guid.NewGuid();
        var logger = new RecordingLogger<EntitiesController>();
        var repo = new StubEntityRepository { BcraToReturn = Snap(id, "{}", Utc(3)) };

        var response = await BuildSut(repo, logger).GetBcraAsync(id, CancellationToken.None);
        var body = Assert.IsType<EntityBcraResponse>(Assert.IsType<OkObjectResult>(response).Value);

        Assert.NotNull(body.Bcra);
        Assert.Equal(Utc(3), body.BcraDataUpdatedAt);
        Assert.Empty(logger.Warnings);
    }

    /// <summary>The CASFOG tab gets the stored snapshot, round-tripped through the real shape.</summary>
    [Fact]
    public async Task GetCasfog_ReturnsTheStoredSnapshotAndItsTimestamp()
    {
        var id = Guid.NewGuid();
        var stored = new CasfogData { Estado = CasfogConsultaEstado.DataFound, FetchedAt = Utc(4) };
        var logger = new RecordingLogger<EntitiesController>();
        var repo = new StubEntityRepository
        {
            CasfogToReturn = Snap(id, JsonSerializer.Serialize(stored), Utc(4))
        };

        var response = await BuildSut(repo, logger).GetCasfogAsync(id, CancellationToken.None);
        var body = Assert.IsType<EntityCasfogResponse>(Assert.IsType<OkObjectResult>(response).Value);

        Assert.NotNull(body.Casfog);
        Assert.Equal(CasfogConsultaEstado.DataFound, body.Casfog.Estado);
        Assert.Equal(Utc(4), body.CasfogDataUpdatedAt);
        Assert.Empty(logger.Warnings);
    }

    /// <summary>The NOSIS tab gets the stored snapshot, fields intact through the real shape.</summary>
    [Fact]
    public async Task GetNosis_ReturnsTheStoredSnapshotAndItsTimestamp()
    {
        var id = Guid.NewGuid();
        var stored = new NosisData
        {
            Estado      = NosisConsultaEstado.DataFound,
            RazonSocial = "Metalúrgica del Sur S.A."
        };
        var logger = new RecordingLogger<EntitiesController>();
        var repo = new StubEntityRepository
        {
            NosisToReturn = Snap(id, JsonSerializer.Serialize(stored), Utc(5))
        };

        var response = await BuildSut(repo, logger).GetNosisAsync(id, CancellationToken.None);
        var body = Assert.IsType<EntityNosisResponse>(Assert.IsType<OkObjectResult>(response).Value);

        Assert.NotNull(body.Nosis);
        Assert.Equal(NosisConsultaEstado.DataFound, body.Nosis.Estado);
        Assert.Equal("Metalúrgica del Sur S.A.", body.Nosis.RazonSocial);
        Assert.Equal(Utc(5), body.NosisDataUpdatedAt);
        Assert.Empty(logger.Warnings);
    }

    // ── Never fetched → 200 with a null payload ───────────────────────────────

    /// <summary>
    /// An entity that exists but has never had this source fetched is a 200 carrying null, not a
    /// 404 and not an error. For NOSIS this is the normal state of a newly created entity, which is
    /// never enriched on create because it is billed per query.
    /// </summary>
    [Theory]
    [InlineData("bcra")]
    [InlineData("casfog")]
    [InlineData("nosis")]
    public async Task GetSnapshot_SourceNeverFetched_Returns200WithNullPayload(string source)
    {
        var id = Guid.NewGuid();
        var empty = Snap(id, null, null);
        var logger = new RecordingLogger<EntitiesController>();
        var repo = new StubEntityRepository
        {
            BcraToReturn   = empty,
            CasfogToReturn = empty,
            NosisToReturn  = empty
        };
        var sut = BuildSut(repo, logger);

        switch (source)
        {
            case "bcra":
                var b = Assert.IsType<EntityBcraResponse>(
                    Assert.IsType<OkObjectResult>(await sut.GetBcraAsync(id, CancellationToken.None)).Value);
                Assert.Null(b.Bcra);
                Assert.Null(b.BcraDataUpdatedAt);
                break;
            case "casfog":
                var c = Assert.IsType<EntityCasfogResponse>(
                    Assert.IsType<OkObjectResult>(await sut.GetCasfogAsync(id, CancellationToken.None)).Value);
                Assert.Null(c.Casfog);
                Assert.Null(c.CasfogDataUpdatedAt);
                break;
            default:
                var n = Assert.IsType<EntityNosisResponse>(
                    Assert.IsType<OkObjectResult>(await sut.GetNosisAsync(id, CancellationToken.None)).Value);
                Assert.Null(n.Nosis);
                Assert.Null(n.NosisDataUpdatedAt);
                break;
        }

        // Nothing to parse means nothing to warn about.
        Assert.Empty(logger.Warnings);
    }

    // ── Malformed stored JSON degrades, it does not 500 ───────────────────────

    /// <summary>
    /// A stored blob that will not parse is a null payload plus one warning — never a 500. The blob
    /// is a cached copy of a third party's response; one bad row must not take the tab down.
    /// </summary>
    [Fact]
    public async Task GetNosis_MalformedStoredJson_ReturnsNullPayloadAndWarnsOnce()
    {
        var id = Guid.NewGuid();
        var logger = new RecordingLogger<EntitiesController>();
        var repo = new StubEntityRepository { NosisToReturn = Snap(id, "{not json", Utc(5)) };

        var response = await BuildSut(repo, logger).GetNosisAsync(id, CancellationToken.None);
        var body = Assert.IsType<EntityNosisResponse>(Assert.IsType<OkObjectResult>(response).Value);

        Assert.Null(body.Nosis);
        // The timestamp still ships: the row genuinely was refreshed, the copy is just unreadable.
        Assert.Equal(Utc(5), body.NosisDataUpdatedAt);
        Assert.Single(logger.Warnings);
    }

    // ── 404 and tenant isolation ──────────────────────────────────────────────

    /// <summary>A missing entity is a 404 on all three reads, distinct from a null payload.</summary>
    [Theory]
    [InlineData("bcra")]
    [InlineData("casfog")]
    [InlineData("nosis")]
    public async Task GetSnapshot_UnknownEntity_Returns404(string source)
    {
        var repo = new StubEntityRepository();   // every ToReturn stays null → no such entity
        var sut = BuildSut(repo, new RecordingLogger<EntitiesController>());
        var id = Guid.NewGuid();

        var response = source switch
        {
            "bcra"   => await sut.GetBcraAsync(id, CancellationToken.None),
            "casfog" => await sut.GetCasfogAsync(id, CancellationToken.None),
            _        => await sut.GetNosisAsync(id, CancellationToken.None)
        };

        var problem = Assert.IsType<ObjectResult>(response);
        Assert.Equal(404, problem.StatusCode);
    }

    /// <summary>
    /// The tenant from the resolved context is what reaches the repository, so the query filters on
    /// it and one tenant can never read another's snapshot by guessing an entity id.
    /// </summary>
    [Fact]
    public async Task GetSnapshot_PassesTheResolvedTenantToTheQuery()
    {
        var id = Guid.NewGuid();
        var repo = new StubEntityRepository { BcraToReturn = Snap(id, "{}", Utc(3)) };

        await BuildSut(repo, new RecordingLogger<EntitiesController>())
            .GetBcraAsync(id, CancellationToken.None);

        Assert.Equal(Tenant, repo.LastTenantId);
        Assert.Equal(id, repo.LastEntityId);
    }

    /// <summary>An unresolved tenant context is a 500, and the read is never attempted.</summary>
    [Theory]
    [InlineData("bcra")]
    [InlineData("casfog")]
    [InlineData("nosis")]
    public async Task GetSnapshot_WithoutTenantContext_Returns500AndNeverQueries(string source)
    {
        var repo = new StubEntityRepository();
        var sut = BuildSut(repo, new RecordingLogger<EntitiesController>(), withTenant: false);
        var id = Guid.NewGuid();

        var response = source switch
        {
            "bcra"   => await sut.GetBcraAsync(id, CancellationToken.None),
            "casfog" => await sut.GetCasfogAsync(id, CancellationToken.None),
            _        => await sut.GetNosisAsync(id, CancellationToken.None)
        };

        var problem = Assert.IsType<ObjectResult>(response);
        Assert.Equal(500, problem.StatusCode);
        Assert.Null(repo.LastTenantId);   // the query was not reached either
    }

    // ── The load-bearing property: these are READS ────────────────────────────

    /// <summary>
    /// THE test for this change. All three gateways are wired in and throw on any call, so a read
    /// that consulted the external source — the job of the refresh-* endpoints, not these — fails
    /// here instead of quietly costing money in production on every tab click.
    /// </summary>
    [Theory]
    [InlineData("bcra")]
    [InlineData("casfog")]
    [InlineData("nosis")]
    public async Task GetSnapshot_NeverCallsTheExternalService(string source)
    {
        var id = Guid.NewGuid();
        var stored = Snap(id, "{}", Utc(6));
        var repo = new StubEntityRepository
        {
            BcraToReturn   = stored,
            CasfogToReturn = stored,
            NosisToReturn  = stored
        };
        var sut = BuildSut(repo, new RecordingLogger<EntitiesController>());

        var response = source switch
        {
            "bcra"   => await sut.GetBcraAsync(id, CancellationToken.None),
            "casfog" => await sut.GetCasfogAsync(id, CancellationToken.None),
            _        => await sut.GetNosisAsync(id, CancellationToken.None)
        };

        // Reaching a 200 at all proves no gateway was touched: every one of them throws.
        Assert.IsType<OkObjectResult>(response);
    }
}
