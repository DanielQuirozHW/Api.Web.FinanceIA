// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-28

using System.Security.Claims;
using System.Text.Json;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Arca;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Lufe;
using Api.Web.FinanceIA.Models.Nosis;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Tests.Clases;
using Api.Web.FinanceIA.Tests.Servicios;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Covers all seven fiadores endpoints on <see cref="FiadoresController"/>: the candidates
/// projection from the persisted LUFE blob, the Active list, the add path (the LUFE path with its
/// candidate-membership rule, the manual path of DDL 074, and the shared duplicate 409), the PATCH
/// edit, the soft remove, the on-demand NOSIS refresh (its gate order, and the rule that a failed
/// lookup writes nothing), and the ARCA prefill lookup — plus the edit markers every response carries
/// and the rule that no CUIT or name reaches a log line or an audit payload.
///
/// <para>
/// Hand-rolled private sealed stubs, no mock library, following the CreditLinesController suites.
/// One file rather than the per-endpoint split those use: the stub set here is shared by all four
/// endpoints, and duplicating ~150 lines of it across two files would buy nothing.
/// </para>
///
/// <para>
/// TENANT SCOPING IS NOT TESTED HERE, deliberately. These stubs return whatever they are handed, so
/// they could only show that the controller passes a tenant id — never that a query honours it.
/// That proof lives in <c>FiadorRepositoryTests</c> and <c>EntityRepositoryDetailTests</c>, against
/// a real database provider.
/// </para>
/// </summary>
public sealed class FiadoresControllerTests
{
    private const string Tenant      = "tenant-1";
    private const string FirebaseUid = "uid-1";

    /// <summary>The tenant's own CUIT, the cuitRepresentada of every ARCA call.</summary>
    private const string TenantArcaCuit = "30716327279";

    // ── Stubs ─────────────────────────────────────────────────────────────────
    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; }
    }

    private sealed class StubEntityRepository : IEntityRepository
    {
        // Default: the entity exists and LUFE was never fetched. Null means "no such entity" — the
        // list, add, PATCH and refresh paths all read the LUFE column and 404 on null.
        public EntitySnapshotProjection? LufeSnapshotToReturn { get; set; } =
            new() { EntityId = Guid.NewGuid(), Json = null };
        public bool EntityExists { get; set; } = true;

        public Task<EntitySnapshotProjection?> GetLufeSnapshotAsync(
            Guid entityId, string tenantId, CancellationToken ct)
            => Task.FromResult(LufeSnapshotToReturn);

        public Task<bool> ExistsByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
            => Task.FromResult(EntityExists);

        public Task<Entity?> GetByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
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
        public Task<EntitySnapshotProjection?> GetNosisSnapshotAsync(
            Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<EntityNameProjection?> GetNameByIdAsync(
            Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class StubFiadorRepository : IFiadorRepository
    {
        public List<Fiador>  ActiveToReturn  { get; set; } = [];
        public Fiador?       DuplicateToReturn { get; set; }
        public Fiador?       ByIdToReturn    { get; set; }
        public bool          SetStatusResult { get; set; } = true;

        public bool    CreateCalled     { get; private set; }
        public Fiador? CreatedFiador    { get; private set; }
        public bool    SetStatusCalled  { get; private set; }
        public string? SetStatusExpected { get; private set; }
        public string? SetStatusNew     { get; private set; }

        public Task<IReadOnlyList<Fiador>> GetActiveByEntityAsync(
            Guid entityId, string tenantId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Fiador>>(ActiveToReturn);

        public Task<Fiador?> GetActiveByEntityCuitAsync(
            Guid entityId, string cuit, string tenantId, CancellationToken ct)
            => Task.FromResult(DuplicateToReturn);

        public Task<Fiador?> GetByIdAsync(Guid fiadorId, string tenantId, CancellationToken ct)
            => Task.FromResult(ByIdToReturn);

        public Task CreateAsync(Fiador fiador, CancellationToken ct)
        {
            CreateCalled  = true;
            CreatedFiador = fiador;
            return Task.CompletedTask;
        }

        public Task<bool> SetStatusAsync(
            Guid fiadorId, string tenantId, string expectedStatus, string newStatus, CancellationToken ct)
        {
            SetStatusCalled   = true;
            SetStatusExpected = expectedStatus;
            SetStatusNew      = newStatus;
            return Task.FromResult(SetStatusResult);
        }

        public bool      SetNosisResult    { get; set; } = true;
        public bool      SetNosisCalled    { get; private set; }
        public string?   SetNosisJson      { get; private set; }
        public DateTime? SetNosisFetchedAt { get; private set; }

        public Task<bool> SetNosisSnapshotAsync(
            Guid fiadorId, string tenantId, string nosisDataJson, DateTime fetchedAt, CancellationToken ct)
        {
            SetNosisCalled    = true;
            SetNosisJson      = nosisDataJson;
            SetNosisFetchedAt = fetchedAt;
            return Task.FromResult(SetNosisResult);
        }

        public bool      UpdateDetailsResult     { get; set; } = true;
        public bool      UpdateDetailsCalled     { get; private set; }
        public string?   UpdatedDenominacion     { get; private set; }
        public string?   UpdatedCargo            { get; private set; }
        public decimal?  UpdatedPorcAccionista   { get; private set; }
        public Guid?     UpdatedByUserId         { get; private set; }

        public Task<bool> UpdateDetailsAsync(
            Guid fiadorId, string tenantId, string? denominacion, string? cargo, decimal? porcAccionista,
            DateTime updatedAt, Guid updatedByUserId, CancellationToken ct)
        {
            UpdateDetailsCalled   = true;
            UpdatedDenominacion   = denominacion;
            UpdatedCargo          = cargo;
            UpdatedPorcAccionista = porcAccionista;
            UpdatedByUserId       = updatedByUserId;
            return Task.FromResult(UpdateDetailsResult);
        }

        public Task<bool> HasActiveFiadorWithLegajoAsync(
            Guid entityId, Guid personLegajoId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<bool> AnyActiveWithLegajoAsync(Guid personLegajoId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Records what the controller asked NOSIS for — the CUIT and, critically, the VARIABLE GROUP.
    /// The vendor answers a wrong group with the wrong variables rather than an error, so "the
    /// fiador route asked for the fiador group" is a behaviour worth pinning rather than assuming.
    /// </summary>
    private sealed class StubNosisService : INosisService
    {
        public NosisData ToReturn { get; set; } =
            new() { Estado = NosisConsultaEstado.DataFound, FetchedAt = DateTime.UtcNow };

        public bool                Called    { get; private set; }
        public long                LastCuit  { get; private set; }
        public NosisVariableGroup? LastGrupo { get; private set; }
        public string?             LastTenant { get; private set; }

        public Task<NosisData> GetVariablesAsync(
            string tenantId, long cuit, NosisVariableGroup grupo, CancellationToken ct)
        {
            Called     = true;
            LastTenant = tenantId;
            LastCuit   = cuit;
            LastGrupo  = grupo;
            return Task.FromResult(ToReturn);
        }
    }

    private sealed class StubTenantProfileService : ITenantProfileService
    {
        public bool NosisEnabled { get; set; } = true;
        public bool Throws       { get; set; }

        public Task<TenantProfileResolution> GetEffectiveAsync(string tenantId, CancellationToken ct)
        {
            if (Throws)
                throw new InvalidOperationException("profile read blew up");

            return Task.FromResult(new TenantProfileResolution(
                TenantProfilePresets.Default with { NosisEnabled = NosisEnabled },
                TenantProfileSource.Configured));
        }

        public Task<TenantProfileResolution> SetEntityTypeAsync(
            string tenantId, string entityType, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class StubUserRepository : IUserRepository
    {
        public User?      UserToReturn       { get; set; }
        public List<User> UsersByIdToReturn  { get; set; } = [];
        public int        GetByIdsCallCount  { get; private set; }

        public Task<User?> GetByFirebaseUidAsync(string firebaseUid, CancellationToken ct)
            => Task.FromResult(UserToReturn);

        public Task<User?> GetByUserIdAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<User?> GetByEmailAsync(string email, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> ExistsByFirebaseUidAsync(string firebaseUid, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<User>> GetByIdsAsync(IEnumerable<Guid> userIds, CancellationToken ct)
        {
            GetByIdsCallCount++;
            var ids = userIds.ToHashSet();
            return Task.FromResult<IReadOnlyList<User>>(UsersByIdToReturn.Where(u => ids.Contains(u.UserId)).ToList());
        }

        public Task<User> CreateAsync(User user, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateLastLoginAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateOnboardingFlagsAsync(Guid userId, string? flagsJson, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<User>> GetActiveByTenantAsync(string tenantId, CancellationToken ct) => throw new NotImplementedException();
        // Team assignment (DDL 063) — unused by this suite.
        public Task UpdateTeamAsync(Guid userId, Guid? teamId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> ClearTeamAsync(Guid teamId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class StubAuditService : IAuditService
    {
        public bool    LogCalled    { get; private set; }
        public string? Action       { get; private set; }
        public string? OldValueJson { get; private set; }
        public string? NewValueJson { get; private set; }

        public Task LogAsync(
            string tenantId, Guid? userId, string action, string severity = "Info",
            string? entityType = null, string? entityId = null, string? ipAddress = null,
            string? requestId = null, string? oldValueJson = null, string? newValueJson = null,
            CancellationToken ct = default)
        {
            LogCalled    = true;
            Action       = action;
            OldValueJson = oldValueJson;
            NewValueJson = newValueJson;
            return Task.CompletedTask;
        }

        public Task LogSessionAsync(
            Guid tenantId, Guid? userId, string email, string eventType,
            string? ipAddress, string? userAgent, string? failureReason = null,
            CancellationToken ct = default)
            => Task.CompletedTask;
    }

    /// <summary>Answers ARCA lookups with <see cref="ToReturn"/>, or throws <see cref="ToThrow"/>.</summary>
    private sealed class StubArcaConstanciaService : IArcaConstanciaService
    {
        public ArcaPersonaData? ToReturn { get; set; }
        public Exception?       ToThrow  { get; set; }

        public int     CallCount            { get; private set; }
        public string? LastCuit             { get; private set; }
        public string? LastCuitRepresentada { get; private set; }

        public Task<ArcaPersonaData?> GetPersonaByCuitAsync(
            string cuit, string cuitRepresentada, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastCuit             = cuit;
            LastCuitRepresentada = cuitRepresentada;
            if (ToThrow is not null)
                throw ToThrow;
            return Task.FromResult(ToReturn);
        }
    }

    private sealed class StubApiCallLogService : IApiCallLogService
    {
        public List<(string Service, int? ResponseStatus, bool IsSuccess, string? ErrorMessage)> Calls { get; } = [];

        public Task LogAsync(
            string tenantId, string service, string endpoint, int? responseStatus, int? inputTokens,
            int? outputTokens, decimal? estimatedCostUsd, bool isSuccess, string? errorMessage,
            int durationMs, string? operation = null, string? model = null, Guid? userId = null,
            Guid? analysisId = null, long? chatMessageId = null, Guid? documentId = null, CancellationToken ct = default)
        {
            Calls.Add((service, responseStatus, isSuccess, errorMessage));
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Captures every formatted log message at every level, so a test can assert that no CUIT and no
    /// name reached a log line — the PII rule this controller lives under.
    /// </summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static TenantContext BuildTenant() => new()
    {
        TenantId               = Tenant,
        FirebaseProjectId      = "proj",
        FirebaseCredentialJson = "{}",
        RecaptchaSecretKey     = "secret"
    };

    /// <summary>
    /// Builds the stored blob the way production does — <c>JsonSerializer.Serialize(lufeData)</c>,
    /// exactly as EntitiesController writes LufeDataJson after a refresh — so the fixture exercises
    /// the real round-trip (root PascalCase, Autoridades[] snake_case via JsonPropertyName) rather
    /// than a hand-written string that might not match what is on disk.
    /// </summary>
    private static EntitySnapshotProjection LufeSnapshot(params LufeAutoridad[] autoridades)
        => new()
        {
            EntityId  = Guid.NewGuid(),
            Json      = JsonSerializer.Serialize(new LufeData { Autoridades = autoridades }),
            UpdatedAt = DateTime.UtcNow,
        };

    private static LufeAutoridad Autoridad(
        long cuit, string? denominacion = "Pérez, Juan", string? cargo = "Presidente",
        decimal? porc = 51.00m, int? esAccionista = 1) => new()
    {
        Cuit           = cuit,
        Denominacion   = denominacion,
        Cargo          = cargo,
        PorcAccionista = porc,
        EsAccionista   = esAccionista,
    };

    private static Fiador ActiveFiador(Guid entityId, string cuit) => new()
    {
        FiadorId        = Guid.NewGuid(),
        TenantId        = Tenant,
        EntityId        = entityId,
        CUIT            = cuit,
        Denominacion    = "Pérez, Juan",
        Cargo           = "Presidente",
        PorcAccionista  = 51.00m,
        Origen          = "Lufe",
        Status          = "Active",
        CreatedByUserId = Guid.NewGuid(),
        CreatedAt       = DateTime.UtcNow,
    };

    private static FiadoresController BuildSut(
        StubFiadorRepository       fiadorRepo,
        StubEntityRepository?      entityRepo  = null,
        StubUserRepository?        userRepo    = null,
        StubAuditService?          audit       = null,
        StubNosisService?          nosis       = null,
        StubTenantProfileService?  profile     = null,
        bool                       nosisConfigured = true,
        IArcaConstanciaService?    arca        = null,
        StubApiCallLogService?     apiCallLog  = null,
        RecordingLogger<FiadoresController>? logger = null,
        bool                       arcaConfigured = true)
    {
        entityRepo ??= new StubEntityRepository();
        userRepo   ??= new StubUserRepository { UserToReturn = new User { UserId = Guid.NewGuid(), FirebaseUid = FirebaseUid } };
        audit      ??= new StubAuditService();
        nosis      ??= new StubNosisService();
        profile    ??= new StubTenantProfileService();

        // Nosis:VrFiador is deliberately absent: the shipping state, in which the fiador group falls
        // back to Nosis:VR. IsNosisConfigured checks VR, not VrFiador, for exactly that reason.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nosis:Enabled"] = nosisConfigured ? "true" : "false",
                ["Nosis:ApiKey"]  = nosisConfigured ? "test-key" : "",
                ["Nosis:VR"]      = nosisConfigured ? "42" : "",
                [$"Tenants:{Tenant}:Cuit"] = arcaConfigured ? TenantArcaCuit : null,
            })
            .Build();

        var sut = new FiadoresController(
            new StubTenantContextAccessor { TenantContext = BuildTenant() },
            fiadorRepo,
            entityRepo,
            userRepo,
            audit,
            nosis,
            new IntegrationAccessService(profile, configuration, NullLogger<IntegrationAccessService>.Instance),
            arca ?? new StubArcaConstanciaService(),
            apiCallLog ?? new StubApiCallLogService(),
            null!,   // IPersonLegajoRepository: only the legajo link reads it (FiadoresControllerLegajoTests)
            configuration,
            logger ?? new RecordingLogger<FiadoresController>());

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim("uid", FirebaseUid) }, "Test"))
        };
        sut.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return sut;
    }

    private static List<FiadorCandidateResponse> Candidates(IActionResult result)
        => Assert.IsType<List<FiadorCandidateResponse>>(Assert.IsType<OkObjectResult>(result).Value);

    // ── GET candidates ────────────────────────────────────────────────────────

    /// <summary>
    /// The projection, including the conversion that has no other home: LUFE informs the CUIT as a
    /// JSON number (30716327279) and the column stores the hyphenated string (30-71632727-9). A
    /// candidate that shipped the raw number would be un-addable — the add path's lookups are plain
    /// string equalities against the stored form.
    /// </summary>
    [Fact]
    public async Task GetCandidates_ProjectsAutoridades_AndFormatsCuitFromLong()
    {
        var entityId = Guid.NewGuid();
        var entities = new StubEntityRepository { LufeSnapshotToReturn = LufeSnapshot(Autoridad(30716327279)) };
        var sut      = BuildSut(new StubFiadorRepository(), entities);

        var result = await sut.GetCandidatesAsync(entityId, CancellationToken.None);

        var candidate = Assert.Single(Candidates(result));
        Assert.Equal("30-71632727-9", candidate.Cuit);
        Assert.Equal("Pérez, Juan",   candidate.Denominacion);
        Assert.Equal("Presidente",    candidate.Cargo);
        Assert.Equal(51.00m,          candidate.PorcAccionista);
        Assert.Equal(1,               candidate.EsAccionista);
        Assert.False(candidate.AlreadyAdded);
    }

    /// <summary>
    /// The flag the Portal disables its rows on. Marked per CUIT, so one socio already added does
    /// not grey out the others.
    /// </summary>
    [Fact]
    public async Task GetCandidates_MarksAlreadyAddedPerCuit()
    {
        var entityId = Guid.NewGuid();
        var entities = new StubEntityRepository
        {
            LufeSnapshotToReturn = LufeSnapshot(Autoridad(30716327279), Autoridad(20111111111))
        };
        var fiadores = new StubFiadorRepository
        {
            ActiveToReturn = [ActiveFiador(entityId, "30-71632727-9")]
        };
        var sut = BuildSut(fiadores, entities);

        var result = await sut.GetCandidatesAsync(entityId, CancellationToken.None);

        var candidates = Candidates(result);
        Assert.Equal(2, candidates.Count);
        Assert.True(candidates.Single(c => c.Cuit == "30-71632727-9").AlreadyAdded);
        Assert.False(candidates.Single(c => c.Cuit == "20-11111111-1").AlreadyAdded);
    }

    /// <summary>
    /// LUFE can list the same person twice under two cargos. Only one of them could ever be added,
    /// so a duplicate row would render an add button that can only ever 409.
    /// </summary>
    [Fact]
    public async Task GetCandidates_DeduplicatesRepeatedCuit()
    {
        var entities = new StubEntityRepository
        {
            LufeSnapshotToReturn = LufeSnapshot(
                Autoridad(30716327279, cargo: "Presidente"),
                Autoridad(30716327279, cargo: "Socio Gerente"))
        };
        var sut = BuildSut(new StubFiadorRepository(), entities);

        var result = await sut.GetCandidatesAsync(Guid.NewGuid(), CancellationToken.None);

        var candidate = Assert.Single(Candidates(result));
        // First occurrence wins, so the cargo shown is the one LUFE listed first.
        Assert.Equal("Presidente", candidate.Cargo);
    }

    /// <summary>
    /// FAULT ISOLATION. The blob is a cached copy of a third party's payload; one unparseable row
    /// must degrade to an empty list, never take the entity's fiadores panel down with a 500.
    /// </summary>
    [Fact]
    public async Task GetCandidates_MalformedLufeJson_ReturnsEmptyList_NotAnError()
    {
        var entities = new StubEntityRepository
        {
            LufeSnapshotToReturn = new EntitySnapshotProjection { Json = "{ this is not json" }
        };
        var sut = BuildSut(new StubFiadorRepository(), entities);

        var result = await sut.GetCandidatesAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Empty(Candidates(result));
    }

    /// <summary>
    /// A LUFE payload whose Autoridades is absent parses fine and simply has no candidates — a 200
    /// with an empty list, distinct from the malformed case above but with the same outcome.
    /// </summary>
    [Fact]
    public async Task GetCandidates_LufeJsonWithoutAutoridades_ReturnsEmptyList()
    {
        var entities = new StubEntityRepository
        {
            LufeSnapshotToReturn = new EntitySnapshotProjection { Json = """{"Entidad":{"cuit":30716327279}}""" }
        };
        var sut = BuildSut(new StubFiadorRepository(), entities);

        var result = await sut.GetCandidatesAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Empty(Candidates(result));
    }

    /// <summary>
    /// The entity exists but LUFE has never been fetched for it: 200 with an empty list, NOT a 404.
    /// The 404 is reserved for "no such entity in this tenant", which the repository signals by
    /// returning null rather than an instance with a null Json.
    /// </summary>
    [Fact]
    public async Task GetCandidates_EntityExistsButLufeNeverFetched_ReturnsEmptyList()
    {
        var entities = new StubEntityRepository
        {
            LufeSnapshotToReturn = new EntitySnapshotProjection { Json = null }
        };
        var sut = BuildSut(new StubFiadorRepository(), entities);

        var result = await sut.GetCandidatesAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Empty(Candidates(result));
    }

    [Fact]
    public async Task GetCandidates_UnknownEntity_Returns404()
    {
        var entities = new StubEntityRepository { LufeSnapshotToReturn = null };
        var sut      = BuildSut(new StubFiadorRepository(), entities);

        var result = await sut.GetCandidatesAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    // ── GET list ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The stored fields round-trip, and the NOSIS snapshot is null — it ships null and stays null
    /// until the step-2 query lands.
    /// </summary>
    [Fact]
    public async Task GetFiadores_MapsStoredFields_WithNullNosisSnapshot()
    {
        var entityId = Guid.NewGuid();
        var fiadores = new StubFiadorRepository { ActiveToReturn = [ActiveFiador(entityId, "30-71632727-9")] };
        var sut      = BuildSut(fiadores);

        var result = await sut.GetFiadoresAsync(entityId, CancellationToken.None);

        var listed = Assert.Single(
            Assert.IsType<List<FiadorResponse>>(Assert.IsType<OkObjectResult>(result).Value));
        Assert.Equal("30-71632727-9", listed.CUIT);
        Assert.Equal("Pérez, Juan",   listed.Denominacion);
        Assert.Equal("Lufe",          listed.Origen);
        Assert.Equal("Active",        listed.Status);
        Assert.Null(listed.Nosis);
        Assert.Null(listed.NosisFetchedAt);
    }

    [Fact]
    public async Task GetFiadores_UnknownEntity_Returns404()
    {
        var entities = new StubEntityRepository { LufeSnapshotToReturn = null };
        var sut      = BuildSut(new StubFiadorRepository(), entities);

        var result = await sut.GetFiadoresAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    // ── POST add ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddFiador_CreatesActiveRow_WithOrigenLufeAndNullNosis()
    {
        var entityId = Guid.NewGuid();
        var entities = new StubEntityRepository { LufeSnapshotToReturn = LufeSnapshot(Autoridad(30716327279)) };
        var fiadores = new StubFiadorRepository();
        var audit    = new StubAuditService();
        var sut      = BuildSut(fiadores, entities, audit: audit);

        var result = await sut.AddFiadorAsync(
            entityId, new AddFiadorRequest { Cuit = "30-71632727-9" }, CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.True(fiadores.CreateCalled);

        var created = fiadores.CreatedFiador!;
        Assert.Equal(Tenant,          created.TenantId);
        Assert.Equal(entityId,        created.EntityId);
        Assert.Equal("30-71632727-9", created.CUIT);
        Assert.Equal("Active",        created.Status);
        Assert.Equal("Lufe",          created.Origen);
        Assert.Null(created.NosisDataJson);
        Assert.Null(created.NosisFetchedAt);
        Assert.NotEqual(Guid.Empty, created.CreatedByUserId);

        Assert.True(audit.LogCalled);
        Assert.Equal("Fiador.Added", audit.Action);
    }

    /// <summary>
    /// The blob wins. The request may carry denominación/cargo/porcentaje — the Portal posts the
    /// candidate object back verbatim — but the stored values come from the matched LUFE entry, so
    /// a caller cannot write a false name against a real socio's CUIT while the row is stamped
    /// Origen='Lufe'.
    /// </summary>
    [Fact]
    public async Task AddFiador_TakesFieldsFromBlob_IgnoringRequestOverrides()
    {
        var entities = new StubEntityRepository { LufeSnapshotToReturn = LufeSnapshot(Autoridad(30716327279)) };
        var fiadores = new StubFiadorRepository();
        var sut      = BuildSut(fiadores, entities);

        await sut.AddFiadorAsync(Guid.NewGuid(), new AddFiadorRequest
        {
            Cuit           = "30-71632727-9",
            Denominacion   = "NOMBRE FALSO",
            Cargo          = "CARGO FALSO",
            PorcAccionista = 99.00m,
        }, CancellationToken.None);

        var created = fiadores.CreatedFiador!;
        Assert.Equal("Pérez, Juan", created.Denominacion);
        Assert.Equal("Presidente",  created.Cargo);
        Assert.Equal(51.00m,        created.PorcAccionista);
    }

    /// <summary>
    /// The row is stamped Origen='Lufe', so a CUIT LUFE never reported as a socio of this PyME
    /// would make that provenance a lie. Rejected before anything is persisted.
    /// </summary>
    [Fact]
    public async Task AddFiador_CuitNotAmongAutoridades_Returns400_AndPersistsNothing()
    {
        var entities = new StubEntityRepository { LufeSnapshotToReturn = LufeSnapshot(Autoridad(30716327279)) };
        var fiadores = new StubFiadorRepository();
        var sut      = BuildSut(fiadores, entities);

        var result = await sut.AddFiadorAsync(
            Guid.NewGuid(), new AddFiadorRequest { Cuit = "20-11111111-1" }, CancellationToken.None);

        Assert.Equal(400, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(fiadores.CreateCalled);
    }

    /// <summary>
    /// The one-Active-per-(entity, CUIT) rule, checked in code so the caller gets a clean 409
    /// rather than the filtered unique index surfacing as a 500.
    /// </summary>
    [Fact]
    public async Task AddFiador_WhenCuitAlreadyActive_Returns409_AndPersistsNothing()
    {
        var entityId = Guid.NewGuid();
        var entities = new StubEntityRepository { LufeSnapshotToReturn = LufeSnapshot(Autoridad(30716327279)) };
        var fiadores = new StubFiadorRepository
        {
            DuplicateToReturn = ActiveFiador(entityId, "30-71632727-9")
        };
        var sut = BuildSut(fiadores, entities);

        var result = await sut.AddFiadorAsync(
            entityId, new AddFiadorRequest { Cuit = "30-71632727-9" }, CancellationToken.None);

        Assert.Equal(409, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(fiadores.CreateCalled);
    }

    /// <summary>
    /// A CUIT posted without hyphens still resolves: it is normalised to the stored form before the
    /// candidate match and the duplicate check, both of which are plain string equalities.
    /// </summary>
    [Fact]
    public async Task AddFiador_AcceptsUnformattedCuit_AndStoresItFormatted()
    {
        var entities = new StubEntityRepository { LufeSnapshotToReturn = LufeSnapshot(Autoridad(30716327279)) };
        var fiadores = new StubFiadorRepository();
        var sut      = BuildSut(fiadores, entities);

        var result = await sut.AddFiadorAsync(
            Guid.NewGuid(), new AddFiadorRequest { Cuit = "30716327279" }, CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal("30-71632727-9", fiadores.CreatedFiador!.CUIT);
    }

    [Fact]
    public async Task AddFiador_WhenCuitMissing_Returns400()
    {
        var fiadores = new StubFiadorRepository();
        var sut      = BuildSut(fiadores);

        var result = await sut.AddFiadorAsync(
            Guid.NewGuid(), new AddFiadorRequest { Cuit = "  " }, CancellationToken.None);

        Assert.Equal(400, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(fiadores.CreateCalled);
    }

    [Fact]
    public async Task AddFiador_UnknownEntity_Returns404()
    {
        var entities = new StubEntityRepository { LufeSnapshotToReturn = null };
        var fiadores = new StubFiadorRepository();
        var sut      = BuildSut(fiadores, entities);

        var result = await sut.AddFiadorAsync(
            Guid.NewGuid(), new AddFiadorRequest { Cuit = "30-71632727-9" }, CancellationToken.None);

        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(fiadores.CreateCalled);
    }

    [Fact]
    public async Task AddFiador_WhenUserUnknown_Returns401()
    {
        var fiadores = new StubFiadorRepository();
        var sut      = BuildSut(fiadores, userRepo: new StubUserRepository { UserToReturn = null });

        var result = await sut.AddFiadorAsync(
            Guid.NewGuid(), new AddFiadorRequest { Cuit = "30-71632727-9" }, CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
        Assert.False(fiadores.CreateCalled);
    }

    // ── DELETE soft-remove ────────────────────────────────────────────────────

    /// <summary>Soft, never hard: the transition asked for is Active → Removed.</summary>
    [Fact]
    public async Task RemoveFiador_FlipsStatusToRemoved()
    {
        var entityId = Guid.NewGuid();
        var fiador   = ActiveFiador(entityId, "30-71632727-9");
        var fiadores = new StubFiadorRepository { ByIdToReturn = fiador };
        var audit    = new StubAuditService();
        var sut      = BuildSut(fiadores, audit: audit);

        var result = await sut.RemoveFiadorAsync(entityId, fiador.FiadorId, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.True(fiadores.SetStatusCalled);
        Assert.Equal("Active",  fiadores.SetStatusExpected);
        Assert.Equal("Removed", fiadores.SetStatusNew);
        Assert.True(audit.LogCalled);
        Assert.Equal("Fiador.Removed", audit.Action);
    }

    /// <summary>
    /// Idempotent: re-removing writes nothing and logs nothing, so a real removal's audit entry is
    /// never joined by a second, misleading one.
    /// </summary>
    [Fact]
    public async Task RemoveFiador_WhenAlreadyRemoved_Returns204_WithoutWriting()
    {
        var entityId = Guid.NewGuid();
        var fiador   = ActiveFiador(entityId, "30-71632727-9");
        fiador.Status = "Removed";
        var fiadores = new StubFiadorRepository { ByIdToReturn = fiador };
        var audit    = new StubAuditService();
        var sut      = BuildSut(fiadores, audit: audit);

        var result = await sut.RemoveFiadorAsync(entityId, fiador.FiadorId, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.False(fiadores.SetStatusCalled);
        Assert.False(audit.LogCalled);
    }

    /// <summary>
    /// The route asserts a parent. Honouring a mismatched one would let a caller remove a fiador
    /// through an entity they were not looking at.
    /// </summary>
    [Fact]
    public async Task RemoveFiador_WhenFiadorBelongsToAnotherEntity_Returns404()
    {
        var fiador   = ActiveFiador(Guid.NewGuid(), "30-71632727-9");
        var fiadores = new StubFiadorRepository { ByIdToReturn = fiador };
        var sut      = BuildSut(fiadores);

        var result = await sut.RemoveFiadorAsync(Guid.NewGuid(), fiador.FiadorId, CancellationToken.None);

        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(fiadores.SetStatusCalled);
    }

    [Fact]
    public async Task RemoveFiador_UnknownFiador_Returns404()
    {
        var fiadores = new StubFiadorRepository { ByIdToReturn = null };
        var sut      = BuildSut(fiadores);

        var result = await sut.RemoveFiadorAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(fiadores.SetStatusCalled);
    }

    /// <summary>
    /// The row left 'Active' between the read and the write — a concurrent removal. Refused with a
    /// 409 rather than silently overwriting it, and nothing is written to the audit trail.
    /// </summary>
    [Fact]
    public async Task RemoveFiador_WhenConcurrentlyModified_Returns409()
    {
        var entityId = Guid.NewGuid();
        var fiador   = ActiveFiador(entityId, "30-71632727-9");
        var fiadores = new StubFiadorRepository { ByIdToReturn = fiador, SetStatusResult = false };
        var audit    = new StubAuditService();
        var sut      = BuildSut(fiadores, audit: audit);

        var result = await sut.RemoveFiadorAsync(entityId, fiador.FiadorId, CancellationToken.None);

        Assert.Equal(409, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(audit.LogCalled);
    }

    // ── POST {fiadorId}/refresh-nosis ──────────────────────────────────────────

    private static FiadorResponse Response(IActionResult result)
        => Assert.IsType<FiadorResponse>(Assert.IsType<OkObjectResult>(result).Value);

    /// <summary>
    /// The happy path, and the two things about it that are easy to get wrong: NOSIS is asked for
    /// the FIADOR variable group (a wrong group returns wrong variables, not an error), and it is
    /// asked about the FIADOR's own CUIT in digits-only form — the column stores it hyphenated.
    /// </summary>
    [Fact]
    public async Task RefreshFiadorNosis_WhenLookupSucceeds_AsksForTheFiadorGroupWithTheFiadorsOwnDigits()
    {
        var entityId = Guid.NewGuid();
        var fiador   = ActiveFiador(entityId, "30-71632727-9");
        var fiadores = new StubFiadorRepository { ByIdToReturn = fiador };
        var nosis    = new StubNosisService();
        var sut      = BuildSut(fiadores, nosis: nosis);

        var result = await sut.RefreshFiadorNosisAsync(entityId, fiador.FiadorId, CancellationToken.None);

        Assert.Equal(200, Assert.IsType<OkObjectResult>(result).StatusCode);
        Assert.True(nosis.Called);
        Assert.Equal(NosisVariableGroup.Fiador, nosis.LastGrupo);
        Assert.Equal(30716327279L, nosis.LastCuit);
        Assert.Equal(Tenant, nosis.LastTenant);
    }

    [Fact]
    public async Task RefreshFiadorNosis_WhenLookupSucceeds_PersistsTheSnapshotAndReturnsIt()
    {
        var entityId = Guid.NewGuid();
        var fiador   = ActiveFiador(entityId, "30-71632727-9");
        var fiadores = new StubFiadorRepository { ByIdToReturn = fiador };
        var nosis    = new StubNosisService
        {
            ToReturn = new NosisData
            {
                Estado         = NosisConsultaEstado.DataFound,
                SujetoObligado = "Posible SO",
                EsEmpleado     = true,
                FetchedAt      = DateTime.UtcNow
            }
        };
        var audit = new StubAuditService();
        var sut   = BuildSut(fiadores, audit: audit, nosis: nosis);

        var result = await sut.RefreshFiadorNosisAsync(entityId, fiador.FiadorId, CancellationToken.None);

        Assert.True(fiadores.SetNosisCalled);
        Assert.NotNull(fiadores.SetNosisJson);
        // Round-trips through the same camelCase contract the read path parses.
        Assert.Contains("sujetoObligado", fiadores.SetNosisJson!);

        var response = Response(result);
        Assert.NotNull(response.Nosis);
        Assert.Equal("Posible SO", response.Nosis!.SujetoObligado);
        Assert.True(response.Nosis.EsEmpleado);
        Assert.Equal(fiadores.SetNosisFetchedAt, response.NosisFetchedAt);
        Assert.True(audit.LogCalled);
    }

    /// <summary>
    /// NoDataReported is a real answer — NOSIS was asked and has nothing on this person — so it IS
    /// persisted. Only LookupFailed is withheld. Collapsing the two would turn a vendor outage into
    /// a clean bill of health on a guarantor.
    /// </summary>
    [Fact]
    public async Task RefreshFiadorNosis_WhenNoDataReported_StillPersists()
    {
        var entityId = Guid.NewGuid();
        var fiador   = ActiveFiador(entityId, "30-71632727-9");
        var fiadores = new StubFiadorRepository { ByIdToReturn = fiador };
        var nosis    = new StubNosisService
        {
            ToReturn = new NosisData { Estado = NosisConsultaEstado.NoDataReported, FetchedAt = DateTime.UtcNow }
        };
        var sut = BuildSut(fiadores, nosis: nosis);

        var result = await sut.RefreshFiadorNosisAsync(entityId, fiador.FiadorId, CancellationToken.None);

        Assert.Equal(200, Assert.IsType<OkObjectResult>(result).StatusCode);
        Assert.True(fiadores.SetNosisCalled);
        Assert.Equal(NosisConsultaEstado.NoDataReported, Response(result).Nosis!.Estado);
    }

    /// <summary>
    /// The rule the whole endpoint is shaped around: a failed lookup returns BEFORE any write, so a
    /// transient NOSIS outage can never overwrite a good stored snapshot with an empty one.
    /// </summary>
    [Fact]
    public async Task RefreshFiadorNosis_WhenLookupFailed_Returns502AndWritesNothing()
    {
        var entityId = Guid.NewGuid();
        var fiador   = ActiveFiador(entityId, "30-71632727-9");
        var fiadores = new StubFiadorRepository { ByIdToReturn = fiador };
        var nosis    = new StubNosisService
        {
            ToReturn = new NosisData { Estado = NosisConsultaEstado.LookupFailed, FetchedAt = DateTime.UtcNow }
        };
        var audit = new StubAuditService();
        var sut   = BuildSut(fiadores, audit: audit, nosis: nosis);

        var result = await sut.RefreshFiadorNosisAsync(entityId, fiador.FiadorId, CancellationToken.None);

        Assert.Equal(502, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(fiadores.SetNosisCalled);
        Assert.False(audit.LogCalled);
    }

    [Fact]
    public async Task RefreshFiadorNosis_WhenEntityNotFound_Returns404AndNeverBillsAQuery()
    {
        var entityId = Guid.NewGuid();
        var fiador   = ActiveFiador(entityId, "30-71632727-9");
        var fiadores = new StubFiadorRepository { ByIdToReturn = fiador };
        var nosis    = new StubNosisService();
        var sut      = BuildSut(fiadores, entityRepo: new StubEntityRepository { LufeSnapshotToReturn = null }, nosis: nosis);

        var result = await sut.RefreshFiadorNosisAsync(entityId, fiador.FiadorId, CancellationToken.None);

        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(nosis.Called);
    }

    [Fact]
    public async Task RefreshFiadorNosis_WhenFiadorNotFound_Returns404()
    {
        var fiadores = new StubFiadorRepository { ByIdToReturn = null };
        var nosis    = new StubNosisService();
        var sut      = BuildSut(fiadores, nosis: nosis);

        var result = await sut.RefreshFiadorNosisAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(nosis.Called);
    }

    /// <summary>
    /// Tenant scope alone would let a fiador of PyME A be refreshed through PyME B's route inside
    /// the same tenant. The EntityId check closes that, exactly as the remove path does.
    /// </summary>
    [Fact]
    public async Task RefreshFiadorNosis_WhenFiadorBelongsToAnotherEntity_Returns404()
    {
        var fiador   = ActiveFiador(Guid.NewGuid(), "30-71632727-9");
        var fiadores = new StubFiadorRepository { ByIdToReturn = fiador };
        var nosis    = new StubNosisService();
        var sut      = BuildSut(fiadores, nosis: nosis);

        var result = await sut.RefreshFiadorNosisAsync(Guid.NewGuid(), fiador.FiadorId, CancellationToken.None);

        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(nosis.Called);
    }

    /// <summary>
    /// A removed fiador is a tombstone. Refused BEFORE the lookup: NOSIS bills per query, and the
    /// repository would refuse the write anyway.
    /// </summary>
    [Fact]
    public async Task RefreshFiadorNosis_WhenFiadorIsRemoved_Returns409AndNeverBillsAQuery()
    {
        var entityId = Guid.NewGuid();
        var fiador   = ActiveFiador(entityId, "30-71632727-9");
        fiador.Status = "Removed";
        var fiadores = new StubFiadorRepository { ByIdToReturn = fiador };
        var nosis    = new StubNosisService();
        var sut      = BuildSut(fiadores, nosis: nosis);

        var result = await sut.RefreshFiadorNosisAsync(entityId, fiador.FiadorId, CancellationToken.None);

        Assert.Equal(409, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(nosis.Called);
    }

    /// <summary>
    /// Entitlement (the tenant profile) is checked before availability, and both answer 400 rather
    /// than 502: NosisService collapses "off", "unconfigured" and "down" into one LookupFailed, so a
    /// 502 here would invite a retry that can never succeed.
    /// </summary>
    [Fact]
    public async Task RefreshFiadorNosis_WhenTenantProfileDisablesNosis_Returns400AndNeverBillsAQuery()
    {
        var entityId = Guid.NewGuid();
        var fiador   = ActiveFiador(entityId, "30-71632727-9");
        var fiadores = new StubFiadorRepository { ByIdToReturn = fiador };
        var nosis    = new StubNosisService();
        var sut      = BuildSut(fiadores, nosis: nosis,
            profile: new StubTenantProfileService { NosisEnabled = false });

        var result = await sut.RefreshFiadorNosisAsync(entityId, fiador.FiadorId, CancellationToken.None);

        Assert.Equal(400, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(nosis.Called);
    }

    [Fact]
    public async Task RefreshFiadorNosis_WhenNosisIsUnconfigured_Returns400AndNeverBillsAQuery()
    {
        var entityId = Guid.NewGuid();
        var fiador   = ActiveFiador(entityId, "30-71632727-9");
        var fiadores = new StubFiadorRepository { ByIdToReturn = fiador };
        var nosis    = new StubNosisService();
        var sut      = BuildSut(fiadores, nosis: nosis, nosisConfigured: false);

        var result = await sut.RefreshFiadorNosisAsync(entityId, fiador.FiadorId, CancellationToken.None);

        Assert.Equal(400, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(nosis.Called);
    }

    /// <summary>
    /// The entitlement read FAILS OPEN: a profile service that blows up must not switch a paid
    /// integration off on its own, leaving IsNosisConfigured as the only gate.
    /// </summary>
    [Fact]
    public async Task RefreshFiadorNosis_WhenProfileReadThrows_FailsOpenAndStillQueries()
    {
        var entityId = Guid.NewGuid();
        var fiador   = ActiveFiador(entityId, "30-71632727-9");
        var fiadores = new StubFiadorRepository { ByIdToReturn = fiador };
        var nosis    = new StubNosisService();
        var sut      = BuildSut(fiadores, nosis: nosis,
            profile: new StubTenantProfileService { Throws = true });

        var result = await sut.RefreshFiadorNosisAsync(entityId, fiador.FiadorId, CancellationToken.None);

        Assert.Equal(200, Assert.IsType<OkObjectResult>(result).StatusCode);
        Assert.True(nosis.Called);
    }

    /// <summary>
    /// A stored CUIT that is not a number cannot be sent to NOSIS. Answered 502, matching
    /// EntitiesController.RefreshNosisAsync, and without spending a query.
    /// </summary>
    [Fact]
    public async Task RefreshFiadorNosis_WhenStoredCuitIsNotNumeric_Returns502AndNeverBillsAQuery()
    {
        var entityId = Guid.NewGuid();
        var fiador   = ActiveFiador(entityId, "no-es-un-cuit");
        var fiadores = new StubFiadorRepository { ByIdToReturn = fiador };
        var nosis    = new StubNosisService();
        var sut      = BuildSut(fiadores, nosis: nosis);

        var result = await sut.RefreshFiadorNosisAsync(entityId, fiador.FiadorId, CancellationToken.None);

        Assert.Equal(502, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(nosis.Called);
    }

    /// <summary>
    /// The fiador was removed between the read and the write. A 409, not a silent success — and no
    /// audit row, since nothing was recorded.
    /// </summary>
    [Fact]
    public async Task RefreshFiadorNosis_WhenConcurrentlyRemoved_Returns409()
    {
        var entityId = Guid.NewGuid();
        var fiador   = ActiveFiador(entityId, "30-71632727-9");
        var fiadores = new StubFiadorRepository { ByIdToReturn = fiador, SetNosisResult = false };
        var audit    = new StubAuditService();
        var sut      = BuildSut(fiadores, audit: audit);

        var result = await sut.RefreshFiadorNosisAsync(entityId, fiador.FiadorId, CancellationToken.None);

        Assert.Equal(409, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(audit.LogCalled);
    }

    /// <summary>
    /// Adding a fiador must never SPEND a NOSIS query. The sibling test above already pins that the
    /// stored snapshot is null; this pins the reason it is null — nobody asked — which is the half
    /// that costs money if it regresses.
    /// </summary>
    [Fact]
    public async Task AddFiador_DoesNotQueryNosis()
    {
        var entities = new StubEntityRepository { LufeSnapshotToReturn = LufeSnapshot(Autoridad(30716327279)) };
        var fiadores = new StubFiadorRepository();
        var nosis    = new StubNosisService();
        var sut      = BuildSut(fiadores, entities, nosis: nosis);

        await sut.AddFiadorAsync(
            Guid.NewGuid(), new AddFiadorRequest { Cuit = "30-71632727-9" }, CancellationToken.None);

        Assert.True(fiadores.CreateCalled);
        Assert.False(nosis.Called);
        Assert.Null(fiadores.CreatedFiador?.NosisDataJson);
        Assert.Null(fiadores.CreatedFiador?.NosisFetchedAt);
    }

    // ══ DDL 074: manual add, edit, edit markers, ARCA prefill ══════════════════

    // A real persona CUIT shape with a valid modulo-11 check digit, deliberately NOT among the LUFE
    // fixtures' autoridades — the manual path must accept a CUIT LUFE never reported.
    private const string ManualCuit       = "20-12345678-6";
    private const string ManualCuitDigits = "20123456786";
    private const string BadCheckDigit    = "20-12345678-5";

    private static int? Status(IActionResult result) => Assert.IsAssignableFrom<ObjectResult>(result).StatusCode;

    private static T Body<T>(IActionResult result) => Assert.IsType<T>(Assert.IsAssignableFrom<ObjectResult>(result).Value);

    private static AddFiadorRequest ManualRequest(
        string cuit = ManualCuit, string? denominacion = "Gómez, Laura", string? cargo = "Cónyuge",
        decimal? porc = 12.50m) => new()
    {
        Cuit           = cuit,
        Denominacion   = denominacion,
        Cargo          = cargo,
        PorcAccionista = porc,
        Origen         = "Manual",
    };

    /// <summary>
    /// Deserialised the way MVC binds the body (web defaults), so the merge-patch "…Specified" flags are
    /// exercised exactly as in production — an object initializer would set them for every property.
    /// </summary>
    private static PatchFiadorRequest PatchJson(string json)
        => JsonSerializer.Deserialize<PatchFiadorRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    // ── POST add — manual path ────────────────────────────────────────────────

    [Fact]
    public async Task AddManual_StoresTheRequestValues_StampedManual_WithoutQueryingNosis()
    {
        var entityId = Guid.NewGuid();
        // LUFE is present but does not list the CUIT: allowed on the manual path.
        var entities = new StubEntityRepository { LufeSnapshotToReturn = LufeSnapshot(Autoridad(30716327279)) };
        var fiadores = new StubFiadorRepository();
        var audit    = new StubAuditService();
        var nosis    = new StubNosisService();
        var sut      = BuildSut(fiadores, entities, audit: audit, nosis: nosis);

        var result = await sut.AddFiadorAsync(
            entityId, ManualRequest(cuit: ManualCuitDigits, denominacion: "  Gómez, Laura  ", cargo: " Cónyuge "),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, Status(result));
        var created = fiadores.CreatedFiador!;
        Assert.Equal(ManualCuit,      created.CUIT);          // normalised to the stored hyphenated form
        Assert.Equal("Gómez, Laura",  created.Denominacion);  // trimmed
        Assert.Equal("Cónyuge",       created.Cargo);
        Assert.Equal(12.50m,          created.PorcAccionista);
        Assert.Equal("Manual",        created.Origen);
        Assert.Equal("Active",        created.Status);
        Assert.Null(created.NosisDataJson);
        Assert.Null(created.UpdatedAt);
        Assert.False(nosis.Called);

        var body = Body<FiadorResponse>(result);
        Assert.Equal("Manual", body.Origen);
        Assert.False(body.Editado);
        Assert.Null(body.LufeOriginal);
        Assert.Null(body.UpdatedByName);

        Assert.Equal("Fiador.Added", audit.Action);
        Assert.Equal("""{"Origen":"Manual"}""", audit.NewValueJson);
    }

    /// <summary>
    /// The CUIT LUFE DOES list, added manually: the analyst's values win — correcting LUFE is the point.
    /// </summary>
    [Fact]
    public async Task AddManual_CuitAlsoInLufe_StoresTheAnalystsValues()
    {
        var entities = new StubEntityRepository { LufeSnapshotToReturn = LufeSnapshot(Autoridad(30716327279)) };
        var fiadores = new StubFiadorRepository();
        var sut      = BuildSut(fiadores, entities);

        var result = await sut.AddFiadorAsync(
            Guid.NewGuid(), ManualRequest(cuit: "30-71632727-9", denominacion: "Pérez, Juan Carlos", cargo: null, porc: null),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, Status(result));
        Assert.Equal("Pérez, Juan Carlos", fiadores.CreatedFiador!.Denominacion);
        Assert.Null(fiadores.CreatedFiador.Cargo);
        Assert.Null(fiadores.CreatedFiador.PorcAccionista);
        Assert.Equal("Manual", fiadores.CreatedFiador.Origen);
    }

    [Theory]
    [InlineData(BadCheckDigit)]
    [InlineData("20-1234567-6")]     // 10 digits
    [InlineData("20.12345678.6")]    // a dot is not silently dropped
    public async Task AddManual_InvalidCuit_Returns400_AndPersistsNothing(string cuit)
    {
        var fiadores = new StubFiadorRepository();
        var sut      = BuildSut(fiadores);

        var result = await sut.AddFiadorAsync(Guid.NewGuid(), ManualRequest(cuit: cuit), CancellationToken.None);

        Assert.Equal(400, Status(result));
        Assert.False(fiadores.CreateCalled);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData(" A ")]  // one character once trimmed
    public async Task AddManual_MissingOrShortName_Returns400(string? denominacion)
    {
        var fiadores = new StubFiadorRepository();
        var sut      = BuildSut(fiadores);

        var result = await sut.AddFiadorAsync(
            Guid.NewGuid(), ManualRequest(denominacion: denominacion), CancellationToken.None);

        Assert.Equal(400, Status(result));
        Assert.False(fiadores.CreateCalled);
    }

    [Fact]
    public async Task AddManual_NameOver300OrCargoOver200_Returns400()
    {
        var fiadores = new StubFiadorRepository();
        var sut      = BuildSut(fiadores);

        var longName = await sut.AddFiadorAsync(
            Guid.NewGuid(), ManualRequest(denominacion: new string('a', 301)), CancellationToken.None);
        var longCargo = await sut.AddFiadorAsync(
            Guid.NewGuid(), ManualRequest(cargo: new string('c', 201)), CancellationToken.None);

        Assert.Equal(400, Status(longName));
        Assert.Equal(400, Status(longCargo));
        Assert.False(fiadores.CreateCalled);
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("100.01")]
    [InlineData("12.345")]   // a third decimal is refused, never rounded
    public async Task AddManual_PorcAccionistaOutOfBounds_Returns400(string porc)
    {
        var fiadores = new StubFiadorRepository();
        var sut      = BuildSut(fiadores);

        var result = await sut.AddFiadorAsync(
            Guid.NewGuid(),
            ManualRequest(porc: decimal.Parse(porc, System.Globalization.CultureInfo.InvariantCulture)),
            CancellationToken.None);

        Assert.Equal(400, Status(result));
        Assert.False(fiadores.CreateCalled);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("100")]
    [InlineData("33.33")]
    public async Task AddManual_PorcAccionistaWithinBounds_IsAccepted(string porc)
    {
        var fiadores = new StubFiadorRepository();
        var sut      = BuildSut(fiadores);

        var result = await sut.AddFiadorAsync(
            Guid.NewGuid(),
            ManualRequest(porc: decimal.Parse(porc, System.Globalization.CultureInfo.InvariantCulture)),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, Status(result));
    }

    /// <summary>Same one-Active-per-(entity, CUIT) rule as the LUFE path, whatever the origen.</summary>
    [Fact]
    public async Task AddManual_WhenCuitAlreadyActive_Returns409()
    {
        var entityId = Guid.NewGuid();
        var fiadores = new StubFiadorRepository { DuplicateToReturn = ActiveFiador(entityId, ManualCuit) };
        var sut      = BuildSut(fiadores);

        var result = await sut.AddFiadorAsync(entityId, ManualRequest(), CancellationToken.None);

        Assert.Equal(409, Status(result));
        Assert.False(fiadores.CreateCalled);
    }

    [Fact]
    public async Task AddManual_UnknownEntity_Returns404()
    {
        var fiadores = new StubFiadorRepository();
        var sut      = BuildSut(fiadores, new StubEntityRepository { EntityExists = false });

        var result = await sut.AddFiadorAsync(Guid.NewGuid(), ManualRequest(), CancellationToken.None);

        Assert.Equal(404, Status(result));
        Assert.False(fiadores.CreateCalled);
    }

    [Fact]
    public async Task AddFiador_UnknownOrigen_Returns400()
    {
        var fiadores = new StubFiadorRepository();
        var sut      = BuildSut(fiadores);

        var request = ManualRequest();
        request.Origen = "Nosis";
        var result = await sut.AddFiadorAsync(Guid.NewGuid(), request, CancellationToken.None);

        Assert.Equal(400, Status(result));
        Assert.False(fiadores.CreateCalled);
    }

    /// <summary>
    /// The LUFE path is unchanged — an explicit "lufe" (any case) takes it exactly as an absent origen
    /// does, the blob still wins over the request, and the audit now records the origen.
    /// </summary>
    [Fact]
    public async Task AddLufe_ExplicitOrigen_TakesTheUnchangedPath_AndAuditsTheOrigen()
    {
        var entities = new StubEntityRepository { LufeSnapshotToReturn = LufeSnapshot(Autoridad(30716327279)) };
        var fiadores = new StubFiadorRepository();
        var audit    = new StubAuditService();
        var sut      = BuildSut(fiadores, entities, audit: audit);

        var result = await sut.AddFiadorAsync(Guid.NewGuid(), new AddFiadorRequest
        {
            Cuit         = "30-71632727-9",
            Denominacion = "NOMBRE FALSO",
            Origen       = "lufe",
        }, CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, Status(result));
        Assert.Equal("Lufe",        fiadores.CreatedFiador!.Origen);
        Assert.Equal("Pérez, Juan", fiadores.CreatedFiador.Denominacion);
        Assert.Equal("""{"Origen":"Lufe"}""", audit.NewValueJson);

        var body = Body<FiadorResponse>(result);
        Assert.False(body.Editado);
        Assert.Equal("Pérez, Juan", body.LufeOriginal!.Denominacion);
    }

    // ── PATCH edit ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Patch_LufeFiador_StampsTheEdit_AndShowsWhatLufeSays()
    {
        var entityId = Guid.NewGuid();
        var editor   = new User { UserId = Guid.NewGuid(), FirebaseUid = FirebaseUid, DisplayName = "Ana Analista" };
        var entities = new StubEntityRepository { LufeSnapshotToReturn = LufeSnapshot(Autoridad(30716327279)) };
        var fiador   = ActiveFiador(entityId, "30-71632727-9");
        var fiadores = new StubFiadorRepository { ByIdToReturn = fiador };
        var users    = new StubUserRepository { UserToReturn = editor, UsersByIdToReturn = [editor] };
        var audit    = new StubAuditService();
        var sut      = BuildSut(fiadores, entities, users, audit);

        var result = await sut.PatchFiadorAsync(
            entityId, fiador.FiadorId, PatchJson("""{"denominacion":" Pérez, Juan Carlos "}"""), CancellationToken.None);

        Assert.Equal(200, Status(result));
        Assert.True(fiadores.UpdateDetailsCalled);
        Assert.Equal("Pérez, Juan Carlos", fiadores.UpdatedDenominacion);
        Assert.Equal("Presidente",         fiadores.UpdatedCargo);            // absent → unchanged
        Assert.Equal(51.00m,               fiadores.UpdatedPorcAccionista);   // absent → unchanged
        Assert.Equal(editor.UserId,        fiadores.UpdatedByUserId);

        var body = Body<FiadorResponse>(result);
        Assert.Equal("Lufe",               body.Origen);                      // origen never changes
        Assert.Equal("Pérez, Juan Carlos", body.Denominacion);
        Assert.True(body.Editado);
        Assert.Equal("Pérez, Juan",        body.LufeOriginal!.Denominacion);
        Assert.Equal("Presidente",         body.LufeOriginal.Cargo);
        Assert.Equal(51.00m,               body.LufeOriginal.PorcAccionista);
        Assert.NotNull(body.UpdatedAt);
        Assert.Equal("Ana Analista",       body.UpdatedByName);

        Assert.Equal("Fiador.Edited", audit.Action);
        Assert.Equal("""{"ChangedFields":["denominacion"]}""", audit.NewValueJson);
        Assert.Null(audit.OldValueJson);
    }

    [Fact]
    public async Task Patch_ManualFiador_EditsCargoAndPorcentaje_WithNoLufeOriginal()
    {
        var entityId = Guid.NewGuid();
        // LUFE even lists this CUIT — a Manual fiador still never shows a lufeOriginal.
        var entities = new StubEntityRepository { LufeSnapshotToReturn = LufeSnapshot(Autoridad(30716327279)) };
        var fiador   = ActiveFiador(entityId, "30-71632727-9");
        fiador.Origen = "Manual";
        var fiadores = new StubFiadorRepository { ByIdToReturn = fiador };
        var audit    = new StubAuditService();
        var users    = new StubUserRepository { UserToReturn = new User { UserId = Guid.NewGuid(), FirebaseUid = FirebaseUid } };
        var sut      = BuildSut(fiadores, entities, users, audit);

        var result = await sut.PatchFiadorAsync(
            entityId, fiador.FiadorId, PatchJson("""{"cargo":"Tercero","porcAccionista":0}"""), CancellationToken.None);

        Assert.Equal(200, Status(result));
        Assert.Equal("Tercero", fiadores.UpdatedCargo);
        Assert.Equal(0m,        fiadores.UpdatedPorcAccionista);

        var body = Body<FiadorResponse>(result);
        Assert.Equal("Manual", body.Origen);
        Assert.True(body.Editado);
        Assert.Null(body.LufeOriginal);
        // The editor no longer resolves (UsersByIdToReturn is empty): the "Usuario" fallback.
        Assert.Equal("Usuario", body.UpdatedByName);
        Assert.Equal("""{"ChangedFields":["cargo","porcAccionista"]}""", audit.NewValueJson);
    }

    /// <summary>Merge-patch: an explicit null clears, an absent field is left alone.</summary>
    [Fact]
    public async Task Patch_ExplicitNullClearsCargo_AbsentPorcentajeIsKept()
    {
        var entityId = Guid.NewGuid();
        var fiador   = ActiveFiador(entityId, "30-71632727-9");
        var fiadores = new StubFiadorRepository { ByIdToReturn = fiador };
        var sut      = BuildSut(fiadores);

        var result = await sut.PatchFiadorAsync(
            entityId, fiador.FiadorId, PatchJson("""{"cargo":null}"""), CancellationToken.None);

        Assert.Equal(200, Status(result));
        Assert.Null(fiadores.UpdatedCargo);
        Assert.Equal(51.00m,        fiadores.UpdatedPorcAccionista);
        Assert.Equal("Pérez, Juan", fiadores.UpdatedDenominacion);
    }

    [Fact]
    public async Task Patch_DifferentCuit_Returns400_AndWritesNothing()
    {
        var entityId = Guid.NewGuid();
        var fiador   = ActiveFiador(entityId, "30-71632727-9");
        var fiadores = new StubFiadorRepository { ByIdToReturn = fiador };
        var sut      = BuildSut(fiadores);

        var result = await sut.PatchFiadorAsync(
            entityId, fiador.FiadorId, PatchJson($$"""{"cuit":"{{ManualCuit}}","denominacion":"Otro Nombre"}"""),
            CancellationToken.None);

        Assert.Equal(400, Status(result));
        Assert.False(fiadores.UpdateDetailsCalled);
    }

    /// <summary>The stored CUIT posted back without hyphens is not a change.</summary>
    [Fact]
    public async Task Patch_SameCuitInAnotherFormat_IsAccepted()
    {
        var entityId = Guid.NewGuid();
        var fiador   = ActiveFiador(entityId, "30-71632727-9");
        var fiadores = new StubFiadorRepository { ByIdToReturn = fiador };
        var sut      = BuildSut(fiadores);

        var result = await sut.PatchFiadorAsync(
            entityId, fiador.FiadorId, PatchJson("""{"cuit":"30716327279","denominacion":"Otro Nombre"}"""),
            CancellationToken.None);

        Assert.Equal(200, Status(result));
        Assert.True(fiadores.UpdateDetailsCalled);
    }

    [Fact]
    public async Task Patch_RemovedFiador_Returns409_AndWritesNothing()
    {
        var entityId = Guid.NewGuid();
        var fiador   = ActiveFiador(entityId, "30-71632727-9");
        fiador.Status = "Removed";
        var fiadores = new StubFiadorRepository { ByIdToReturn = fiador };
        var sut      = BuildSut(fiadores);

        var result = await sut.PatchFiadorAsync(
            entityId, fiador.FiadorId, PatchJson("""{"denominacion":"Otro Nombre"}"""), CancellationToken.None);

        Assert.Equal(409, Status(result));
        Assert.False(fiadores.UpdateDetailsCalled);
    }

    /// <summary>Removed between the read and the conditional write: refused, never audited.</summary>
    [Fact]
    public async Task Patch_WhenConcurrentlyRemoved_Returns409_AndAuditsNothing()
    {
        var entityId = Guid.NewGuid();
        var fiador   = ActiveFiador(entityId, "30-71632727-9");
        var fiadores = new StubFiadorRepository { ByIdToReturn = fiador, UpdateDetailsResult = false };
        var audit    = new StubAuditService();
        var sut      = BuildSut(fiadores, audit: audit);

        var result = await sut.PatchFiadorAsync(
            entityId, fiador.FiadorId, PatchJson("""{"denominacion":"Otro Nombre"}"""), CancellationToken.None);

        Assert.Equal(409, Status(result));
        Assert.False(audit.LogCalled);
    }

    [Fact]
    public async Task Patch_FiadorOfAnotherEntity_Returns404()
    {
        var fiador   = ActiveFiador(Guid.NewGuid(), "30-71632727-9");
        var fiadores = new StubFiadorRepository { ByIdToReturn = fiador };
        var sut      = BuildSut(fiadores);

        var result = await sut.PatchFiadorAsync(
            Guid.NewGuid(), fiador.FiadorId, PatchJson("""{"denominacion":"Otro Nombre"}"""), CancellationToken.None);

        Assert.Equal(404, Status(result));
        Assert.False(fiadores.UpdateDetailsCalled);
    }

    [Fact]
    public async Task Patch_UnknownEntity_Returns404()
    {
        var fiadores = new StubFiadorRepository { ByIdToReturn = ActiveFiador(Guid.NewGuid(), "30-71632727-9") };
        var sut      = BuildSut(fiadores, new StubEntityRepository { LufeSnapshotToReturn = null });

        var result = await sut.PatchFiadorAsync(
            Guid.NewGuid(), Guid.NewGuid(), PatchJson("""{"denominacion":"Otro Nombre"}"""), CancellationToken.None);

        Assert.Equal(404, Status(result));
    }

    /// <summary>Repeating the stored values is a 200 that writes nothing: no stamp, no audit row.</summary>
    [Fact]
    public async Task Patch_WithNoActualChange_WritesNothing()
    {
        var entityId = Guid.NewGuid();
        var fiador   = ActiveFiador(entityId, "30-71632727-9");
        var fiadores = new StubFiadorRepository { ByIdToReturn = fiador };
        var audit    = new StubAuditService();
        var sut      = BuildSut(fiadores, audit: audit);

        var result = await sut.PatchFiadorAsync(
            entityId, fiador.FiadorId, PatchJson("""{"denominacion":"Pérez, Juan","porcAccionista":51.00}"""),
            CancellationToken.None);

        Assert.Equal(200, Status(result));
        Assert.False(fiadores.UpdateDetailsCalled);
        Assert.False(audit.LogCalled);
        Assert.Null(Body<FiadorResponse>(result).UpdatedAt);
    }

    [Theory]
    [InlineData("""{}""")]                                // nothing to edit
    [InlineData("""{"cuit":"30716327279"}""")]            // only the non-editable CUIT
    [InlineData("""{"denominacion":null}""")]             // the name cannot be cleared
    [InlineData("""{"denominacion":"  "}""")]
    [InlineData("""{"porcAccionista":101}""")]
    [InlineData("""{"porcAccionista":1.234}""")]
    public async Task Patch_InvalidBody_Returns400_AndWritesNothing(string json)
    {
        var entityId = Guid.NewGuid();
        var fiador   = ActiveFiador(entityId, "30-71632727-9");
        var fiadores = new StubFiadorRepository { ByIdToReturn = fiador };
        var sut      = BuildSut(fiadores);

        var result = await sut.PatchFiadorAsync(entityId, fiador.FiadorId, PatchJson(json), CancellationToken.None);

        Assert.Equal(400, Status(result));
        Assert.False(fiadores.UpdateDetailsCalled);
    }

    // ── Edit markers on the list ──────────────────────────────────────────────

    [Fact]
    public async Task GetFiadores_UntouchedLufeFiador_IsNotEditado_AndCarriesLufeOriginal_WithoutAUserLookup()
    {
        var entityId = Guid.NewGuid();
        var entities = new StubEntityRepository { LufeSnapshotToReturn = LufeSnapshot(Autoridad(30716327279)) };
        var fiadores = new StubFiadorRepository { ActiveToReturn = [ActiveFiador(entityId, "30-71632727-9")] };
        var users    = new StubUserRepository { UserToReturn = new User { UserId = Guid.NewGuid() } };
        var sut      = BuildSut(fiadores, entities, users);

        var listed = Assert.Single(Body<List<FiadorResponse>>(
            await sut.GetFiadoresAsync(entityId, CancellationToken.None)));

        Assert.False(listed.Editado);
        Assert.Equal("Pérez, Juan", listed.LufeOriginal!.Denominacion);
        Assert.Null(listed.UpdatedAt);
        Assert.Null(listed.UpdatedByName);
        Assert.Equal(0, users.GetByIdsCallCount);   // nobody edited anything: no extra query
    }

    /// <summary>
    /// LUFE now reports something else for the CUIT (a refresh after the add): the fiador is flagged even
    /// though nobody edited it — the analyst should see the disagreement.
    /// </summary>
    [Fact]
    public async Task GetFiadores_LufeNowDiffers_IsEditado()
    {
        var entityId = Guid.NewGuid();
        var entities = new StubEntityRepository
        {
            LufeSnapshotToReturn = LufeSnapshot(Autoridad(30716327279, cargo: "Socio Gerente"))
        };
        var fiadores = new StubFiadorRepository { ActiveToReturn = [ActiveFiador(entityId, "30-71632727-9")] };
        var sut      = BuildSut(fiadores, entities);

        var listed = Assert.Single(Body<List<FiadorResponse>>(
            await sut.GetFiadoresAsync(entityId, CancellationToken.None)));

        Assert.True(listed.Editado);
        Assert.Equal("Socio Gerente", listed.LufeOriginal!.Cargo);
        Assert.Null(listed.UpdatedAt);
    }

    /// <summary>
    /// Whitespace and a LUFE percentage with more decimals than the decimal(5,2) column holds are not
    /// differences.
    /// </summary>
    [Fact]
    public async Task GetFiadores_LufeDiffersOnlyInWhitespaceOrBeyondTwoDecimals_IsNotEditado()
    {
        var entityId = Guid.NewGuid();
        var entities = new StubEntityRepository
        {
            LufeSnapshotToReturn = LufeSnapshot(Autoridad(30716327279, denominacion: " Pérez, Juan ", porc: 51.001m))
        };
        var fiadores = new StubFiadorRepository { ActiveToReturn = [ActiveFiador(entityId, "30-71632727-9")] };
        var sut      = BuildSut(fiadores, entities);

        var listed = Assert.Single(Body<List<FiadorResponse>>(
            await sut.GetFiadoresAsync(entityId, CancellationToken.None)));

        Assert.False(listed.Editado);
    }

    [Fact]
    public async Task GetFiadores_LufeNoLongerListsTheCuit_HasNoLufeOriginal_AndIsNotEditado()
    {
        var entityId = Guid.NewGuid();
        var entities = new StubEntityRepository { LufeSnapshotToReturn = LufeSnapshot(Autoridad(20111111111)) };
        var fiadores = new StubFiadorRepository { ActiveToReturn = [ActiveFiador(entityId, "30-71632727-9")] };
        var sut      = BuildSut(fiadores, entities);

        var listed = Assert.Single(Body<List<FiadorResponse>>(
            await sut.GetFiadoresAsync(entityId, CancellationToken.None)));

        Assert.Null(listed.LufeOriginal);
        Assert.False(listed.Editado);
    }

    [Fact]
    public async Task GetFiadores_EditedFiador_IsEditado_WithTheEditorsName()
    {
        var entityId = Guid.NewGuid();
        var editor   = new User { UserId = Guid.NewGuid(), DisplayName = "Ana Analista" };
        var fiador   = ActiveFiador(entityId, ManualCuit);
        fiador.Origen          = "Manual";
        fiador.UpdatedAt       = DateTime.UtcNow;
        fiador.UpdatedByUserId = editor.UserId;
        var fiadores = new StubFiadorRepository { ActiveToReturn = [fiador] };
        var users    = new StubUserRepository { UserToReturn = new User { UserId = Guid.NewGuid() }, UsersByIdToReturn = [editor] };
        var sut      = BuildSut(fiadores, userRepo: users);

        var listed = Assert.Single(Body<List<FiadorResponse>>(
            await sut.GetFiadoresAsync(entityId, CancellationToken.None)));

        Assert.True(listed.Editado);
        Assert.Null(listed.LufeOriginal);
        Assert.Equal(fiador.UpdatedAt, listed.UpdatedAt);
        Assert.Equal("Ana Analista", listed.UpdatedByName);
        Assert.Equal(1, users.GetByIdsCallCount);
    }

    // ── GET arca-lookup ───────────────────────────────────────────────────────

    private static ArcaPersonaData ArcaPersona(
        string cuit, string? tipoPersona, string? nombre = null, string? apellido = null, string? razonSocial = null)
        => new(
            Cuit: cuit, TipoPersona: tipoPersona, EstadoClave: "ACTIVO", Nombre: nombre, Apellido: apellido,
            RazonSocial: razonSocial, TipoDocumento: null, NumeroDocumento: null, FechaNacimiento: null,
            MesCierre: null, IdActividadPrincipal: null, DescripcionActividadPrincipal: null,
            PeriodoActividadPrincipal: null, FormaJuridica: null, DomicilioFiscal: null, DomicilioLegal: null);

    [Fact]
    public async Task ArcaLookup_PersonaHumana_ReturnsTheComposedName_AndCreatesNothing()
    {
        var arca       = new StubArcaConstanciaService { ToReturn = ArcaPersona(ManualCuitDigits, "FISICA", "Laura", "Gómez") };
        var apiCallLog = new StubApiCallLogService();
        var fiadores   = new StubFiadorRepository();
        var audit      = new StubAuditService();
        var sut        = BuildSut(fiadores, audit: audit, arca: arca, apiCallLog: apiCallLog);

        var result = await sut.ArcaLookupAsync(Guid.NewGuid(), ManualCuit, CancellationToken.None);

        var body = Body<FiadorArcaLookupResponse>(result);
        Assert.Equal(ManualCuit,         body.Cuit);
        Assert.Equal("Gómez, Laura",     body.Denominacion);
        Assert.Equal(PersonTypes.Humana, body.TipoPersona);

        Assert.Equal(ManualCuitDigits, arca.LastCuit);              // ARCA wants the digits
        Assert.Equal(TenantArcaCuit,   arca.LastCuitRepresentada);
        Assert.False(fiadores.CreateCalled);
        Assert.False(audit.LogCalled);
        var call = Assert.Single(apiCallLog.Calls);
        Assert.Equal("ARCA", call.Service);
        Assert.True(call.IsSuccess);
    }

    [Fact]
    public async Task ArcaLookup_PersonaJuridica_ReturnsTheRazonSocial()
    {
        var arca = new StubArcaConstanciaService { ToReturn = ArcaPersona("30716327279", "JURIDICA", razonSocial: "Acme SA") };
        var sut  = BuildSut(new StubFiadorRepository(), arca: arca);

        var body = Body<FiadorArcaLookupResponse>(
            await sut.ArcaLookupAsync(Guid.NewGuid(), "30716327279", CancellationToken.None));

        Assert.Equal("30-71632727-9",      body.Cuit);
        Assert.Equal("Acme SA",            body.Denominacion);
        Assert.Equal(PersonTypes.Juridica, body.TipoPersona);
    }

    /// <summary>
    /// The prefill is normalized like the PyME autofill: quotes wrapping the whole razón social are
    /// stripped (inner quotes stay), and a name that is nothing but quotes comes back as null.
    /// </summary>
    [Theory]
    [InlineData("\"Acme SA\"",                  "Acme SA")]
    [InlineData("  \u201CAcme  SA\u201D ",        "Acme SA")]
    [InlineData("Acme \"La Nueva\" SA",          "Acme \"La Nueva\" SA")]
    [InlineData("\"\"",                          null)]
    public async Task ArcaLookup_NormalizesTheRazonSocial(string razonSocial, string? expected)
    {
        var arca = new StubArcaConstanciaService { ToReturn = ArcaPersona("30716327279", "JURIDICA", razonSocial: razonSocial) };
        var sut  = BuildSut(new StubFiadorRepository(), arca: arca);

        var body = Body<FiadorArcaLookupResponse>(
            await sut.ArcaLookupAsync(Guid.NewGuid(), "30716327279", CancellationToken.None));

        Assert.Equal(expected, body.Denominacion);
    }

    [Fact]
    public async Task ArcaLookup_WhenArcaDoesNotKnowTheCuit_Returns404()
    {
        var arca       = new StubArcaConstanciaService { ToReturn = null };
        var apiCallLog = new StubApiCallLogService();
        var sut        = BuildSut(new StubFiadorRepository(), arca: arca, apiCallLog: apiCallLog);

        var result = await sut.ArcaLookupAsync(Guid.NewGuid(), ManualCuit, CancellationToken.None);

        Assert.Equal(404, Status(result));
        Assert.Contains("ARCA", Body<ProblemDetails>(result).Detail);   // told apart from the entity 404
        var call = Assert.Single(apiCallLog.Calls);
        Assert.Equal((false, null), (call.IsSuccess, call.ErrorMessage));
        // The writer shape ArcaCallOutcome rests on: ARCA answered "no persona", so the operations
        // dashboard reads this row as no data, not as an integration failure.
        Assert.True(ApiCallLogRowMarkers.IsNoData(call.Service, call.IsSuccess, call.ResponseStatus, call.ErrorMessage));
    }

    /// <summary>
    /// Production: ARCA answers an unknown CUIT with the "No existe persona con ese Id" SOAP Fault over
    /// HTTP 500. Through the real service it is the same 404 and the same not-found row as
    /// personaNoRegistrada — not a 502.
    /// </summary>
    [Fact]
    public async Task ArcaLookup_WhenArcaAnswersTheNotFoundFaultOverHttp500_Returns404_AndANoDataRow()
    {
        var arca       = ArcaSoapFakes.Service(
            System.Net.HttpStatusCode.InternalServerError, ArcaSoapFakes.FaultBody(ArcaSoapFakes.NotFoundFaultString));
        var apiCallLog = new StubApiCallLogService();
        var sut        = BuildSut(new StubFiadorRepository(), arca: arca, apiCallLog: apiCallLog);

        var result = await sut.ArcaLookupAsync(Guid.NewGuid(), ManualCuit, CancellationToken.None);

        Assert.Equal(404, Status(result));
        Assert.Equal("ARCA no registra ninguna persona con el CUIT ingresado.", Body<ProblemDetails>(result).Detail);
        var call = Assert.Single(apiCallLog.Calls);
        Assert.Equal(("ARCA", (int?)502, false, (string?)null), call);
        Assert.True(ApiCallLogRowMarkers.IsNoData(call.Service, call.IsSuccess, call.ResponseStatus, call.ErrorMessage));
    }

    public static TheoryData<System.Net.HttpStatusCode, string> ArcaFailureAnswers() => new()
    {
        { System.Net.HttpStatusCode.InternalServerError, ArcaSoapFakes.FaultBody("El token ha expirado") },
        { System.Net.HttpStatusCode.InternalServerError, ArcaSoapFakes.FaultBody("La CUIT representada no se encuentra autorizada") },
        { System.Net.HttpStatusCode.InternalServerError, ArcaSoapFakes.FaultBody("Computador no autorizado a acceder al servicio") },
        { System.Net.HttpStatusCode.ServiceUnavailable,  "Service Unavailable" },
        { System.Net.HttpStatusCode.OK,                  ArcaSoapFakes.FaultBody("El token ha expirado") },
    };

    [Theory]
    [MemberData(nameof(ArcaFailureAnswers))]
    public async Task ArcaLookup_WhenArcaAnswersAnyOtherFaultOrA5xx_Returns502_AndAFailureRow(
        System.Net.HttpStatusCode status, string body)
    {
        var apiCallLog = new StubApiCallLogService();
        var sut        = BuildSut(new StubFiadorRepository(), arca: ArcaSoapFakes.Service(status, body), apiCallLog: apiCallLog);

        var result = await sut.ArcaLookupAsync(Guid.NewGuid(), ManualCuit, CancellationToken.None);

        Assert.Equal(502, Status(result));
        var call = Assert.Single(apiCallLog.Calls);
        Assert.Equal((false, nameof(HttpRequestException)), (call.IsSuccess, call.ErrorMessage));
        Assert.False(ApiCallLogRowMarkers.IsNoData(call.Service, call.IsSuccess, call.ResponseStatus, call.ErrorMessage));
    }

    public static TheoryData<Exception> ArcaFailures() => new()
    {
        new HttpRequestException("503 Service Unavailable"),
        new InvalidOperationException("WSAA ticket could not be obtained"),
        new TaskCanceledException("HttpClient timeout"),   // a timeout, not the caller cancelling
    };

    /// <summary>
    /// FAULT ISOLATION: whatever ARCA throws, the prefill is a 502 with a Spanish detail — never a 500 —
    /// and neither the log line nor the ApiCallLogs row carries the CUIT.
    /// </summary>
    [Theory]
    [MemberData(nameof(ArcaFailures))]
    public async Task ArcaLookup_WhenArcaFails_Returns502_WithoutLoggingTheCuit(Exception failure)
    {
        var arca       = new StubArcaConstanciaService { ToThrow = failure };
        var apiCallLog = new StubApiCallLogService();
        var logger     = new RecordingLogger<FiadoresController>();
        var sut        = BuildSut(new StubFiadorRepository(), arca: arca, apiCallLog: apiCallLog, logger: logger);

        var result = await sut.ArcaLookupAsync(Guid.NewGuid(), ManualCuit, CancellationToken.None);

        Assert.Equal(502, Status(result));
        Assert.Equal("El servicio de ARCA no está disponible temporalmente.", Body<ProblemDetails>(result).Detail);
        var call = Assert.Single(apiCallLog.Calls);
        Assert.False(call.IsSuccess);
        Assert.Equal(failure.GetType().Name, call.ErrorMessage);
        Assert.False(ApiCallLogRowMarkers.IsNoData(call.Service, call.IsSuccess, call.ResponseStatus, call.ErrorMessage));
        Assert.DoesNotContain(logger.Messages, m => m.Contains(ManualCuit) || m.Contains(ManualCuitDigits));
    }

    [Fact]
    public async Task ArcaLookup_WhenTheCallerCancels_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var arca = new StubArcaConstanciaService { ToThrow = new OperationCanceledException(cts.Token) };
        var sut  = BuildSut(new StubFiadorRepository(), arca: arca);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.ArcaLookupAsync(Guid.NewGuid(), ManualCuit, cts.Token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(BadCheckDigit)]
    public async Task ArcaLookup_InvalidCuit_Returns400_WithoutCallingArca(string? cuit)
    {
        var arca = new StubArcaConstanciaService();
        var sut  = BuildSut(new StubFiadorRepository(), arca: arca);

        var result = await sut.ArcaLookupAsync(Guid.NewGuid(), cuit, CancellationToken.None);

        Assert.Equal(400, Status(result));
        Assert.Equal(0, arca.CallCount);
    }

    [Fact]
    public async Task ArcaLookup_UnknownEntity_Returns404_WithoutCallingArca()
    {
        var arca = new StubArcaConstanciaService();
        var sut  = BuildSut(new StubFiadorRepository(), new StubEntityRepository { EntityExists = false }, arca: arca);

        var result = await sut.ArcaLookupAsync(Guid.NewGuid(), ManualCuit, CancellationToken.None);

        Assert.Equal(404, Status(result));
        Assert.Equal(0, arca.CallCount);
    }

    [Fact]
    public async Task ArcaLookup_TenantWithoutArcaCuit_Returns502_WithoutCallingArca()
    {
        var arca = new StubArcaConstanciaService();
        var sut  = BuildSut(new StubFiadorRepository(), arca: arca, arcaConfigured: false);

        var result = await sut.ArcaLookupAsync(Guid.NewGuid(), ManualCuit, CancellationToken.None);

        Assert.Equal(502, Status(result));
        Assert.Equal(0, arca.CallCount);
    }

    // ── PII ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// The person's CUIT and name never reach a log line or an audit payload — not on a manual add, not
    /// on an edit (old or new value), not on the list.
    /// </summary>
    [Fact]
    public async Task ManualAddEditAndList_NeverPutTheCuitOrTheNameInLogsOrAudit()
    {
        var entityId = Guid.NewGuid();
        var logger   = new RecordingLogger<FiadoresController>();
        var audits   = new List<string?>();

        var addAudit = new StubAuditService();
        var addRepo  = new StubFiadorRepository();
        await BuildSut(addRepo, audit: addAudit, logger: logger).AddFiadorAsync(
            entityId, ManualRequest(denominacion: "Gómez, Laura"), CancellationToken.None);
        audits.Add(addAudit.NewValueJson);

        var stored = addRepo.CreatedFiador!;
        var patchAudit = new StubAuditService();
        var patchRepo  = new StubFiadorRepository { ByIdToReturn = stored, ActiveToReturn = [stored] };
        var patchSut   = BuildSut(patchRepo, audit: patchAudit, logger: logger);
        await patchSut.PatchFiadorAsync(
            entityId, stored.FiadorId, PatchJson("""{"denominacion":"Gómez, Laura Inés","cargo":"Socia"}"""),
            CancellationToken.None);
        audits.Add(patchAudit.NewValueJson);
        audits.Add(patchAudit.OldValueJson);
        await patchSut.GetFiadoresAsync(entityId, CancellationToken.None);

        Assert.NotEmpty(logger.Messages);
        string[] pii = [ManualCuit, ManualCuitDigits, "Gómez", "Laura", "Cónyuge", "Socia"];
        foreach (var value in pii)
        {
            Assert.DoesNotContain(logger.Messages, m => m.Contains(value, StringComparison.Ordinal));
            Assert.DoesNotContain(audits, a => a is not null && a.Contains(value, StringComparison.Ordinal));
        }
    }
}
