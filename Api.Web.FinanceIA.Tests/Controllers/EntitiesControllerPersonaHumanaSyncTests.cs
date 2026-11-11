// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-27

using System.Security.Claims;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Arca;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.Legajo;
using Api.Web.FinanceIA.Tests.Clases;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Covers how an EXISTING entity and its persona-humana legajo (DDL 069, [dbo].[PersonLegajos]) are
/// kept in sync after creation: PUT /api/entities/{id} when the CUIT changes, refresh-arca,
/// refresh-bcra, and the read paths that answer with the monotributo category living on the legajo
/// (GET cuit/{cuit}, refresh-casfog).
///
/// <para>
/// Why these exist. An entity and its legajo are two rows describing ONE person, and every one of
/// these actions writes (or reads) both. The failure modes are quiet: a PUT that changes the CUIT but
/// keeps pointing at the OLD person's legajo attaches one CUIL's ARCA and BCRA history to another
/// person; a partial ARCA answer that "refreshes" a monotributista to Desconocido silently loses the
/// category the analyst sees; a refresh-bcra that calls BCRA twice doubles a vendor call for every
/// persona humana; a legajo lookup that forgets the tenant links one tenant's person to another
/// tenant's entity. None of that throws — each one returns 200 with plausible data.
/// </para>
///
/// <para>
/// Harness: the REAL <see cref="EntityRepository"/> and <see cref="PersonLegajoRepository"/> over ONE
/// shared InMemory <see cref="FinanciaDbContext"/> per test, because the atomic entity+legajo save and
/// the AsNoTracking read / Update write pattern are part of what is under test. Each repository is
/// wrapped in a thin counting spy that delegates to it, so a test can also pin that an action did NO
/// legajo work, and can make a save lose a uniqueness race the way SQL Server would (InMemory enforces
/// no unique index). Vendors and the user lookup are hand-rolled private stubs, per the repo's
/// no-mock-library convention; members outside the tested paths throw NotImplementedException so an
/// unexpected call is loud. All CUITs and names are synthetic.
/// </para>
/// </summary>
public sealed class EntitiesControllerPersonaHumanaSyncTests
{
    private const string Tenant      = "tenant-1";
    private const string OtherTenant = "tenant-2";
    private const string Uid         = "firebase-uid-1";

    private const string CuitHumanaA  = "20-11111111-2";
    private const string CuitHumanaB  = "27-22222222-3";
    private const string CuitJuridica = "30-12345678-9";
    private const string CuitAmbiguo  = "50-12345678-1";

    private const string NombreHumana  = "PERSONA DE PRUEBA";
    private const string NombreEmpresa = "EMPRESA DE PRUEBA SA";

    private static readonly Guid CurrentUserId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTime SeedTime  = new(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);

    // ── Repository spies (delegate to the REAL repositories) ──────────────────

    /// <summary>
    /// Delegates to the real <see cref="EntityRepository"/>. Counts updates and, when
    /// <see cref="LoseRaceOnUpdate"/> is set, throws what the real repository throws when SQL Server
    /// rejects the write on a per-tenant CUIT index — without writing anything.
    /// </summary>
    private sealed class EntityRepositorySpy : IEntityRepository
    {
        private readonly EntityRepository _inner;

        public EntityRepositorySpy(EntityRepository inner)
        {
            _inner = inner;
        }

        public int UpdateCalls { get; private set; }

        public DuplicateCuitScope? LoseRaceOnUpdate { get; set; }

        public Task<Entity?> GetByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
            => _inner.GetByIdAsync(entityId, tenantId, ct);

        public Task<Entity?> GetByCuitAsync(string cuit, string tenantId, CancellationToken ct)
            => _inner.GetByCuitAsync(cuit, tenantId, ct);

        public Task<Entity> UpdateAsync(Entity entity, CancellationToken ct)
        {
            UpdateCalls++;

            if (LoseRaceOnUpdate is { } scope) throw new DuplicateCuitException(scope);

            return _inner.UpdateAsync(entity, ct);
        }

        public Task<bool> ExistsByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Entity> CreateAsync(Entity entity, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<Entity>> GetAllByTenantAsync(string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<(IReadOnlyList<EntityListProjection> Items, int TotalCount)> GetListPageByTenantAsync(
            string tenantId, int skip, int take, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, EntityNameProjection>> GetNameLookupByIdsAsync(
            IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<EntityDetailProjection?> GetDetailByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<EntitySnapshotProjection?> GetBcraSnapshotAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<EntitySnapshotProjection?> GetCasfogSnapshotAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<EntitySnapshotProjection?> GetLufeSnapshotAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<EntitySnapshotProjection?> GetNosisSnapshotAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<EntityNameProjection?> GetNameByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Delegates every member to the real <see cref="PersonLegajoRepository"/> and counts the calls.
    /// When <see cref="LoseRaceOnSave"/> is set, <see cref="SaveEntityWithLegajoAsync"/> plays the
    /// OTHER request of a race instead of saving: it writes <see cref="ConcurrentWinner"/> (if any)
    /// first, then throws what the real repository throws once SQL Server has rejected the write.
    /// </summary>
    private sealed class LegajoRepositorySpy : IPersonLegajoRepository
    {
        private readonly PersonLegajoRepository _inner;
        private readonly FinanciaDbContext      _db;

        public LegajoRepositorySpy(PersonLegajoRepository inner, FinanciaDbContext db)
        {
            _inner = inner;
            _db    = db;
        }

        public int GetByCuitCalls    { get; private set; }
        public int GetByIdCalls      { get; private set; }
        public int GetCategoryCalls  { get; private set; }
        public int SaveCalls         { get; private set; }
        public bool? LastEntityIsNew { get; private set; }
        public bool? LastLegajoIsNew { get; private set; }

        public DuplicateCuitScope? LoseRaceOnSave    { get; set; }
        public PersonLegajo?       ConcurrentWinner  { get; set; }

        public int TotalCalls => GetByCuitCalls + GetByIdCalls + GetCategoryCalls + SaveCalls;

        public Task<PersonLegajo?> GetByCuitAsync(string cuit, string tenantId, CancellationToken ct)
        {
            GetByCuitCalls++;
            return _inner.GetByCuitAsync(cuit, tenantId, ct);
        }

        public Task<PersonLegajo?> GetByIdAsync(Guid personLegajoId, string tenantId, CancellationToken ct)
        {
            GetByIdCalls++;
            return _inner.GetByIdAsync(personLegajoId, tenantId, ct);
        }

        public Task<string?> GetMonotributoCategoryAsync(Guid personLegajoId, string tenantId, CancellationToken ct)
        {
            GetCategoryCalls++;
            return _inner.GetMonotributoCategoryAsync(personLegajoId, tenantId, ct);
        }

        public async Task SaveEntityWithLegajoAsync(
            Entity entity, bool entityIsNew, PersonLegajo legajo, bool legajoIsNew, CancellationToken ct)
        {
            SaveCalls++;
            LastEntityIsNew = entityIsNew;
            LastLegajoIsNew = legajoIsNew;

            if (LoseRaceOnSave is { } scope)
            {
                if (ConcurrentWinner is not null)
                {
                    _db.PersonLegajos.Add(ConcurrentWinner);
                    _db.ChangeTracker.DetectChanges();
                    await _db.SaveChangesAsync(ct);
                    _db.ChangeTracker.Clear();
                }

                throw new DuplicateCuitException(scope);
            }

            await _inner.SaveEntityWithLegajoAsync(entity, entityIsNew, legajo, legajoIsNew, ct);
        }

        // The entity routes never write a fiador link or a legajo-only snapshot.
        public Task SaveFiadorLegajoLinkAsync(Fiador fiador, PersonLegajo legajo, bool legajoIsNew, CancellationToken ct)
            => throw new NotImplementedException();
        public Task SaveLegajoSnapshotsAsync(PersonLegajo legajo, CancellationToken ct)
            => throw new NotImplementedException();
    }

    // ── Vendor stubs ──────────────────────────────────────────────────────────

    private sealed class StubArcaConstanciaService : IArcaConstanciaService
    {
        public ArcaPersonaData? Persona   { get; set; }
        public Exception?       ToThrow   { get; set; }
        public int              CallCount { get; private set; }
        public string?          LastCuit  { get; private set; }

        public Task<ArcaPersonaData?> GetPersonaByCuitAsync(
            string cuit, string cuitRepresentada, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastCuit = cuit;
            if (ToThrow is not null)
                throw ToThrow;
            return Task.FromResult(Persona);
        }
    }

    private sealed class StubBcraService : IBcraService
    {
        public BcraData? Result    { get; set; }
        public int       CallCount { get; private set; }

        public Task<BcraData?> GetBcraDataForAnalysisAsync(string cuit, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(Result);
        }

        public Task<BcraDeudaData> GetDeudaAsync(string cuit, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<BcraChequesData> GetChequesRechazadosAsync(string cuit, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<BcraMacroData?> GetMacroAsync(CancellationToken ct)
            => throw new NotImplementedException();
        public Task<BcraCambiarioData?> GetCambiarioAsync(CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class StubCasfogService : ICasfogService
    {
        public int CallCount { get; private set; }

        public Task<CasfogData> GetCentralDeudoresAsync(long cuit, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(new CasfogData { Estado = CasfogConsultaEstado.DataFound });
        }
    }

    /// <summary>Every optional integration entitled (the SGR preset), so refresh-casfog runs.</summary>
    private sealed class StubTenantProfileService : ITenantProfileService
    {
        public Task<TenantProfileResolution> GetEffectiveAsync(string tenantId, CancellationToken ct)
            => Task.FromResult(new TenantProfileResolution(TenantProfilePresets.Sgr, TenantProfileSource.Configured));

        public Task<TenantProfileResolution> SetEntityTypeAsync(string tenantId, string entityType, CancellationToken ct)
            => throw new NotSupportedException("EntitiesController must not write the tenant profile.");
    }

    // ── Infrastructure stubs ──────────────────────────────────────────────────

    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; }
    }

    /// <summary>Always the same analyst, so a new legajo's CreatedBy can be asserted exactly.</summary>
    private sealed class StubUserRepository : IUserRepository
    {
        public Task<User?> GetByFirebaseUidAsync(string firebaseUid, CancellationToken ct)
            => Task.FromResult<User?>(new User
            {
                UserId      = CurrentUserId,
                TenantId    = Tenant,
                FirebaseUid = firebaseUid,
                Email       = "analyst@example.com"
            });

        public Task<User?> GetByUserIdAsync(Guid userId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<User?> GetByEmailAsync(string email, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<bool> ExistsByFirebaseUidAsync(string firebaseUid, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<User>> GetByIdsAsync(IEnumerable<Guid> userIds, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<User> CreateAsync(User user, CancellationToken ct)
            => throw new NotImplementedException();
        public Task UpdateLastLoginAsync(Guid userId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task UpdateOnboardingFlagsAsync(Guid userId, string? flagsJson, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<User>> GetActiveByTenantAsync(string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task UpdateTeamAsync(Guid userId, Guid? teamId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<int> ClearTeamAsync(Guid teamId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class StubAuditService : IAuditService
    {
        public Task LogAsync(string tenantId, Guid? userId, string action, string severity = "Info",
            string? entityType = null, string? entityId = null, string? ipAddress = null,
            string? requestId = null, string? oldValueJson = null, string? newValueJson = null,
            CancellationToken ct = default)
            => Task.CompletedTask;

        public Task LogSessionAsync(Guid tenantId, Guid? userId, string email, string eventType,
            string? ipAddress, string? userAgent, string? failureReason = null,
            CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private sealed class StubApiCallLogService : IApiCallLogService
    {
        public List<(string Service, int? ResponseStatus, bool IsSuccess, string? ErrorMessage)> Calls { get; } = [];

        public Task LogAsync(string tenantId, string service, string endpoint, int? responseStatus,
            int? inputTokens, int? outputTokens, decimal? estimatedCostUsd, bool isSuccess, string? errorMessage,
            int durationMs, string? operation = null, string? model = null, Guid? userId = null,
            Guid? analysisId = null, long? chatMessageId = null, Guid? documentId = null, CancellationToken ct = default)
        {
            Calls.Add((service, responseStatus, isSuccess, errorMessage));
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Captures every entry with its level. Copied from EntitiesControllerEntitlementGateTests (the
    /// type is private per file) and widened to keep all levels, so a test can pin that a CUIL never
    /// reaches the log at ANY level, not only that the Warning count is right.
    /// </summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IEnumerable<string> Warnings => Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message);
        public IEnumerable<string> Errors   => Entries.Where(e => e.Level >= LogLevel.Error).Select(e => e.Message);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private sealed record Harness(
        EntitiesController                  Sut,
        FinanciaDbContext                   Db,
        EntityRepositorySpy                 Entities,
        LegajoRepositorySpy                 Legajos,
        StubArcaConstanciaService           Arca,
        StubBcraService                     Bcra,
        StubCasfogService                   Casfog,
        StubApiCallLogService               ApiCallLog,
        RecordingLogger<EntitiesController> Logger) : IDisposable
    {
        public void Dispose() => Db.Dispose();
    }

    private static Harness BuildHarness()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"persona-humana-sync-{Guid.NewGuid()}")
            .Options;
        var db = new FinanciaDbContext(options);

        // ONE context behind both repositories: the entity+legajo save is one SaveChanges on it.
        var entities = new EntityRepositorySpy(new EntityRepository(db, NullLogger<EntityRepository>.Instance));
        var legajos  = new LegajoRepositorySpy(
            new PersonLegajoRepository(db, NullLogger<PersonLegajoRepository>.Instance), db);

        var arca   = new StubArcaConstanciaService();
        var bcra   = new StubBcraService();
        var casfog     = new StubCasfogService();
        var apiCallLog = new StubApiCallLogService();
        var logger     = new RecordingLogger<EntitiesController>();

        // refresh-arca needs the tenant's own CUIT (the "representada") or it answers 400.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"Tenants:{Tenant}:Cuit"] = "30-99999999-7"
            })
            .Build();

        var sut = new EntitiesController(
            new StubTenantContextAccessor
            {
                TenantContext = new TenantContext
                {
                    TenantId               = Tenant,
                    FirebaseProjectId      = "proj",
                    FirebaseCredentialJson = "{}",
                    RecaptchaSecretKey     = "secret"
                }
            },
            entities,                       // IEntityRepository — real, behind a spy
            null!,                          // IArcaPadronService
            arca,                           // IArcaConstanciaService
            null!,                          // IArcaWsaaService
            apiCallLog,                     // IApiCallLogService
            new StubAuditService(),         // IAuditService
            null!,                          // ILufeService
            bcra,                           // IBcraService
            casfog,                         // ICasfogService
            null!,                          // INosisService
            new IntegrationAccessService(new StubTenantProfileService(), config, NullLogger<IntegrationAccessService>.Instance), // refresh-casfog only
            null!,                          // IDocumentRepository
            null!,                          // IDocumentStorageService
            new StubUserRepository(),       // IUserRepository
            null!,                          // ICreditLineRepository
            null!,                          // IAnalysisRepository
            null!,                          // INoteRepository
            legajos,                        // IPersonLegajoRepository — real, behind a spy
            null!,                          // INosisReportRepository
            null!,                          // INosisManagerReportParser
            null!,                          // KeyedAsyncLock
            config,                         // IConfiguration
            logger);

        // The PUT reads the Firebase uid off the principal and 401s without it.
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("uid", Uid) }, "Test"));
        sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };

        return new Harness(sut, db, entities, legajos, arca, bcra, casfog, apiCallLog, logger);
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static PersonLegajo Legajo(
        string cuit,
        string fiscalRegime = FiscalRegimes.Desconocido,
        string? category = null,
        string tenantId = Tenant,
        string fullName = NombreHumana) => new()
    {
        PersonLegajoId      = Guid.NewGuid(),
        TenantId            = tenantId,
        CUIT                = cuit,
        FullName            = fullName,
        FiscalRegime        = fiscalRegime,
        MonotributoCategory = category,
        ArcaSnapshotJson    = """{"seeded":"arca"}""",
        ArcaDataUpdatedAt   = SeedTime,
        BcraDataJson        = """{"seeded":"bcra"}""",
        BcraDataUpdatedAt   = SeedTime,
        CreatedBy           = null,
        CreatedAt           = SeedTime,
        UpdatedAt           = SeedTime
    };

    private static Entity Humana(string cuit, PersonLegajo? legajo, string? fiscalRegime = null, string tenantId = Tenant)
        => new()
        {
            EntityId       = Guid.NewGuid(),
            TenantId       = tenantId,
            CUIT           = cuit,
            BusinessName   = NombreHumana,
            PersonType     = PersonTypes.Humana,
            FiscalRegime   = fiscalRegime ?? legajo?.FiscalRegime ?? FiscalRegimes.Desconocido,
            PersonLegajoId = legajo?.PersonLegajoId,
            CreatedAt      = SeedTime,
            UpdatedAt      = SeedTime
        };

    private static Entity Juridica(string cuit, string? fiscalRegime = null, string tenantId = Tenant) => new()
    {
        EntityId     = Guid.NewGuid(),
        TenantId     = tenantId,
        CUIT         = cuit,
        BusinessName = NombreEmpresa,
        PersonType   = PersonTypes.Juridica,
        FiscalRegime = fiscalRegime,
        CreatedAt    = SeedTime,
        UpdatedAt    = SeedTime
    };

    /// <summary>
    /// Seeds and then CLEARS the tracker, so the controller starts from what a request really sees:
    /// nothing tracked, rows read AsNoTracking and written back with Update().
    /// </summary>
    private static async Task SeedAsync(Harness h, IEnumerable<Entity> entities, IEnumerable<PersonLegajo> legajos)
    {
        h.Db.PersonLegajos.AddRange(legajos);
        h.Db.Entities.AddRange(entities);
        h.Db.ChangeTracker.DetectChanges();   // AutoDetectChanges is disabled on the context
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
    }

    private static Task<Entity> ReloadEntityAsync(Harness h, Guid entityId)
        => h.Db.Entities.AsNoTracking().SingleAsync(x => x.EntityId == entityId);

    private static Task<PersonLegajo> ReloadLegajoAsync(Harness h, Guid legajoId)
        => h.Db.PersonLegajos.AsNoTracking().SingleAsync(x => x.PersonLegajoId == legajoId);

    private static Task<List<PersonLegajo>> LegajosForAsync(Harness h, string tenantId, string cuit)
        => h.Db.PersonLegajos.AsNoTracking().Where(x => x.TenantId == tenantId && x.CUIT == cuit).ToListAsync();

    private static UpdateEntityRequest PutRequest(string cuit, string businessName = NombreHumana) => new()
    {
        CUIT         = cuit,
        BusinessName = businessName,
        IsMonitored  = true
    };

    private static ArcaFiscalData Fiscal(string? regime, string? category) => new(
        FechaContratoSocial:  null,
        EsSucesion:           false,
        MesCierre:            12,
        Actividades:          [],
        Impuestos:            [],
        Regimenes:            [],
        DomicilioFiscal:      null,
        FiscalRegime:         regime,
        MonotributoCategoria: category);

    private static ArcaPersonaData Persona(
        string cuit, string tipoPersona, ArcaFiscalData fiscal, bool isIncomplete = false) => new(
        Cuit: cuit.Replace("-", ""), TipoPersona: tipoPersona, EstadoClave: "ACTIVO",
        Nombre: "PERSONA", Apellido: "DE PRUEBA", RazonSocial: null,
        TipoDocumento: null, NumeroDocumento: null, FechaNacimiento: null,
        MesCierre: 12, IdActividadPrincipal: "620100", DescripcionActividadPrincipal: null,
        PeriodoActividadPrincipal: null, FormaJuridica: null, DomicilioFiscal: null, DomicilioLegal: null,
        FiscalData: fiscal, IsIncomplete: isIncomplete);

    private static EntityResponse AssertOk(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<EntityResponse>(ok.Value);
    }

    private static void AssertProblem(IActionResult result, int status, string detail)
    {
        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(status, obj.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(obj.Value);
        Assert.Equal(detail, problem.Detail);
    }

    private static string ConflictDetail(string cuit) => $"Ya existe una entidad con el CUIT '{cuit}' para este tenant.";

    /// <summary>The seeded legajo's own row, byte for byte: nothing the action did may have reached it.</summary>
    private static void AssertLegajoUnchanged(PersonLegajo seeded, PersonLegajo reloaded)
    {
        Assert.Equal(seeded.TenantId,            reloaded.TenantId);
        Assert.Equal(seeded.CUIT,                reloaded.CUIT);
        Assert.Equal(seeded.FullName,            reloaded.FullName);
        Assert.Equal(seeded.FiscalRegime,        reloaded.FiscalRegime);
        Assert.Equal(seeded.MonotributoCategory, reloaded.MonotributoCategory);
        Assert.Equal(seeded.ArcaSnapshotJson,    reloaded.ArcaSnapshotJson);
        Assert.Equal(seeded.ArcaDataUpdatedAt,   reloaded.ArcaDataUpdatedAt);
        Assert.Equal(seeded.BcraDataJson,        reloaded.BcraDataJson);
        Assert.Equal(seeded.BcraDataUpdatedAt,   reloaded.BcraDataUpdatedAt);
        Assert.Equal(seeded.CreatedBy,           reloaded.CreatedBy);
        Assert.Equal(seeded.UpdatedAt,           reloaded.UpdatedAt);
    }

    private static void AssertNoCuilLogged(Harness h, string cuit)
    {
        var digits = cuit.Replace("-", "");
        Assert.DoesNotContain(h.Logger.Entries, e => e.Message.Contains(cuit, StringComparison.Ordinal));
        Assert.DoesNotContain(h.Logger.Entries, e => e.Message.Contains(digits, StringComparison.Ordinal));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // PUT /api/entities/{id} — the legajo follows the CUIT
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The Portal saves an entity by round-tripping everything it read, CUIT included, so almost
    /// every PUT carries an UNCHANGED CUIT. That must stay the plain one-row update it always was: no
    /// legajo lookup, no legajo write, the link untouched — and the response still carries the
    /// category, via the one-column read.
    /// </summary>
    [Fact]
    public async Task Update_UnchangedCuitOnLinkedHumana_DoesNoLegajoWorkAndKeepsTheLink()
    {
        using var h = BuildHarness();
        var legajoA = Legajo(CuitHumanaA, FiscalRegimes.Monotributo, "C");
        var entity  = Humana(CuitHumanaA, legajoA);
        await SeedAsync(h, [entity], [legajoA]);

        var result = await h.Sut.UpdateAsync(
            entity.EntityId, PutRequest(CuitHumanaA, "PERSONA DE PRUEBA EDITADA"), CancellationToken.None);

        var response = AssertOk(result);
        Assert.Equal(legajoA.PersonLegajoId, response.PersonLegajoId);
        Assert.Equal("C", response.MonotributoCategory);

        var reloaded = await ReloadEntityAsync(h, entity.EntityId);
        Assert.Equal("PERSONA DE PRUEBA EDITADA", reloaded.BusinessName);
        Assert.Equal(legajoA.PersonLegajoId, reloaded.PersonLegajoId);
        Assert.Equal(PersonTypes.Humana, reloaded.PersonType);
        Assert.Equal(FiscalRegimes.Monotributo, reloaded.FiscalRegime);

        Assert.Equal(1, await h.Db.PersonLegajos.CountAsync());
        AssertLegajoUnchanged(legajoA, await ReloadLegajoAsync(h, legajoA.PersonLegajoId));

        Assert.Equal(1, h.Entities.UpdateCalls);
        Assert.Equal(0, h.Legajos.GetByCuitCalls);
        Assert.Equal(0, h.Legajos.SaveCalls);
        Assert.Equal(1, h.Legajos.GetCategoryCalls);   // the response's narrow read, nothing else
    }

    /// <summary>
    /// A changed CUIT is a different person. The entity must get THAT person's legajo — here a new
    /// one, since the tenant has none — and nothing may be copied into it: the entity's snapshots and
    /// the old legajo's regime/category belong to the OLD CUIL. The old legajo is kept, untouched: it is
    /// still the old person's record.
    /// </summary>
    [Fact]
    public async Task Update_HumanaCuitChangedToAnUnknownPerson_CreatesAndLinksAFreshLegajo()
    {
        using var h = BuildHarness();
        var legajoA = Legajo(CuitHumanaA, FiscalRegimes.Monotributo, "C");
        var entity  = Humana(CuitHumanaA, legajoA);
        entity.FiscalDataJson = """{"old":"cuil"}""";
        await SeedAsync(h, [entity], [legajoA]);

        var result = await h.Sut.UpdateAsync(
            entity.EntityId, PutRequest(CuitHumanaB, "  OTRA PERSONA DE PRUEBA  "), CancellationToken.None);

        var response = AssertOk(result);

        var legajoB = Assert.Single(await LegajosForAsync(h, Tenant, CuitHumanaB));
        Assert.NotEqual(legajoA.PersonLegajoId, legajoB.PersonLegajoId);
        Assert.Equal(CuitHumanaB, legajoB.CUIT);                         // hyphenated, like Entities.CUIT
        Assert.Equal("OTRA PERSONA DE PRUEBA", legajoB.FullName);        // the new, trimmed BusinessName
        Assert.Equal(FiscalRegimes.Desconocido, legajoB.FiscalRegime);
        Assert.Null(legajoB.MonotributoCategory);
        Assert.Null(legajoB.ArcaSnapshotJson);
        Assert.Null(legajoB.ArcaDataUpdatedAt);
        Assert.Null(legajoB.BcraDataJson);
        Assert.Null(legajoB.BcraDataUpdatedAt);
        Assert.Null(legajoB.NosisDataJson);
        Assert.Equal(CurrentUserId, legajoB.CreatedBy);

        var reloaded = await ReloadEntityAsync(h, entity.EntityId);
        Assert.Equal(CuitHumanaB, reloaded.CUIT);
        Assert.Equal(legajoB.PersonLegajoId, reloaded.PersonLegajoId);
        Assert.Equal(PersonTypes.Humana, reloaded.PersonType);
        Assert.Equal(FiscalRegimes.Desconocido, reloaded.FiscalRegime);   // the new legajo's, not the old one's

        AssertLegajoUnchanged(legajoA, await ReloadLegajoAsync(h, legajoA.PersonLegajoId));
        Assert.Equal(2, await h.Db.PersonLegajos.CountAsync());

        Assert.Equal(legajoB.PersonLegajoId, response.PersonLegajoId);
        Assert.Equal(FiscalRegimes.Desconocido, response.FiscalRegime);
        Assert.Null(response.MonotributoCategory);

        // One atomic write of both rows: a new legajo, an existing entity.
        Assert.Equal(1, h.Legajos.SaveCalls);
        Assert.False(h.Legajos.LastEntityIsNew);
        Assert.True(h.Legajos.LastLegajoIsNew);
    }

    /// <summary>
    /// The tenant already has a legajo for the new CUIT (from another entity, since deleted, or a
    /// backfill). It is linked — never duplicated, which UX_PersonLegajos_TenantId_CUIT would reject
    /// in SQL anyway — and the entity takes ITS regime, so the two rows describing one person agree.
    /// The existing legajo itself is not rewritten.
    /// </summary>
    [Fact]
    public async Task Update_CuitChangedToAPersonWithAnExistingLegajo_LinksItWithoutDuplicating()
    {
        using var h = BuildHarness();
        var legajoA = Legajo(CuitHumanaA);
        var legajoC = Legajo(CuitHumanaB, FiscalRegimes.Monotributo, "E", fullName: "OTRA PERSONA DE PRUEBA");
        var entity  = Humana(CuitHumanaA, legajoA);
        await SeedAsync(h, [entity], [legajoA, legajoC]);

        var result = await h.Sut.UpdateAsync(entity.EntityId, PutRequest(CuitHumanaB), CancellationToken.None);

        var response = AssertOk(result);

        var only = Assert.Single(await LegajosForAsync(h, Tenant, CuitHumanaB));
        Assert.Equal(legajoC.PersonLegajoId, only.PersonLegajoId);
        AssertLegajoUnchanged(legajoC, only);

        var reloaded = await ReloadEntityAsync(h, entity.EntityId);
        Assert.Equal(legajoC.PersonLegajoId, reloaded.PersonLegajoId);
        Assert.Equal(FiscalRegimes.Monotributo, reloaded.FiscalRegime);

        Assert.Equal(legajoC.PersonLegajoId, response.PersonLegajoId);
        Assert.Equal("E", response.MonotributoCategory);

        Assert.Equal(0, h.Legajos.SaveCalls);        // linking an existing legajo writes the entity only
        Assert.Equal(1, h.Entities.UpdateCalls);
    }

    /// <summary>
    /// A company re-keyed to a natural person's CUIL (a 20- prefix) is a persona humana from now on:
    /// the unambiguous prefix re-derives the type, and a Humana always has a legajo.
    /// </summary>
    [Fact]
    public async Task Update_JuridicaCuitChangedToAHumanaPrefix_BecomesHumanaAndLinksANewLegajo()
    {
        using var h = BuildHarness();
        var entity = Juridica(CuitJuridica, FiscalRegimes.ResponsableInscripto);
        await SeedAsync(h, [entity], []);

        var result = await h.Sut.UpdateAsync(entity.EntityId, PutRequest(CuitHumanaA), CancellationToken.None);

        var response = AssertOk(result);

        var legajo   = Assert.Single(await LegajosForAsync(h, Tenant, CuitHumanaA));
        var reloaded = await ReloadEntityAsync(h, entity.EntityId);
        Assert.Equal(PersonTypes.Humana, reloaded.PersonType);
        Assert.Equal(legajo.PersonLegajoId, reloaded.PersonLegajoId);
        Assert.Equal(FiscalRegimes.Desconocido, reloaded.FiscalRegime);
        Assert.Equal(NombreHumana, legajo.FullName);
        Assert.Equal(CurrentUserId, legajo.CreatedBy);

        Assert.Equal(PersonTypes.Humana, response.PersonType);
        Assert.Equal(legajo.PersonLegajoId, response.PersonLegajoId);
    }

    /// <summary>
    /// The other direction: a CUIL corrected to a company CUIT (30-) makes the entity a Juridica,
    /// which may not point at a legajo (CK_Entities_PersonLegajo_Humana). The regime is left as it
    /// was — the plan's rule — and the old legajo stays, as the old person's record.
    /// </summary>
    [Fact]
    public async Task Update_HumanaCuitChangedToAJuridicaPrefix_BecomesJuridicaUnlinkedAndKeepsTheRegime()
    {
        using var h = BuildHarness();
        var legajoA = Legajo(CuitHumanaA, FiscalRegimes.Monotributo, "C");
        var entity  = Humana(CuitHumanaA, legajoA);
        await SeedAsync(h, [entity], [legajoA]);

        var result = await h.Sut.UpdateAsync(
            entity.EntityId, PutRequest(CuitJuridica, NombreEmpresa), CancellationToken.None);

        var response = AssertOk(result);

        var reloaded = await ReloadEntityAsync(h, entity.EntityId);
        Assert.Equal(PersonTypes.Juridica, reloaded.PersonType);
        Assert.Null(reloaded.PersonLegajoId);
        Assert.Equal(FiscalRegimes.Monotributo, reloaded.FiscalRegime);

        Assert.Equal(PersonTypes.Juridica, response.PersonType);
        Assert.Null(response.PersonLegajoId);
        Assert.Null(response.MonotributoCategory);

        AssertLegajoUnchanged(legajoA, await ReloadLegajoAsync(h, legajoA.PersonLegajoId));
        Assert.Empty(await LegajosForAsync(h, Tenant, CuitJuridica));
        Assert.Equal(0, h.Legajos.TotalCalls);   // a Juridica needs no legajo lookup at all
    }

    /// <summary>
    /// A prefix that names neither (50-) does not get to decide: the stored type stands. The entity
    /// is still a Humana with a new CUIT, so it still gets that CUIT's legajo.
    /// </summary>
    [Fact]
    public async Task Update_HumanaCuitChangedToAnAmbiguousPrefix_StaysHumanaAndRelinks()
    {
        using var h = BuildHarness();
        var legajoA = Legajo(CuitHumanaA, FiscalRegimes.Monotributo, "C");
        var entity  = Humana(CuitHumanaA, legajoA);
        await SeedAsync(h, [entity], [legajoA]);

        var result = await h.Sut.UpdateAsync(entity.EntityId, PutRequest(CuitAmbiguo), CancellationToken.None);

        AssertOk(result);

        var legajo   = Assert.Single(await LegajosForAsync(h, Tenant, CuitAmbiguo));
        var reloaded = await ReloadEntityAsync(h, entity.EntityId);
        Assert.Equal(PersonTypes.Humana, reloaded.PersonType);
        Assert.Equal(legajo.PersonLegajoId, reloaded.PersonLegajoId);
        AssertLegajoUnchanged(legajoA, await ReloadLegajoAsync(h, legajoA.PersonLegajoId));
    }

    [Fact]
    public async Task Update_CuitOfAnotherEntityInTheTenant_Returns409AndWritesNothing()
    {
        using var h = BuildHarness();
        var legajoA = Legajo(CuitHumanaA);
        var legajoB = Legajo(CuitHumanaB);
        var entity  = Humana(CuitHumanaA, legajoA);
        var owner   = Humana(CuitHumanaB, legajoB);
        await SeedAsync(h, [entity, owner], [legajoA, legajoB]);

        var result = await h.Sut.UpdateAsync(entity.EntityId, PutRequest(CuitHumanaB), CancellationToken.None);

        AssertProblem(result, 409, ConflictDetail(CuitHumanaB));

        var reloaded = await ReloadEntityAsync(h, entity.EntityId);
        Assert.Equal(CuitHumanaA, reloaded.CUIT);
        Assert.Equal(legajoA.PersonLegajoId, reloaded.PersonLegajoId);
        Assert.Equal(2, await h.Db.PersonLegajos.CountAsync());
        Assert.Equal(0, h.Entities.UpdateCalls);
        Assert.Equal(0, h.Legajos.TotalCalls);
    }

    /// <summary>
    /// CUIT uniqueness is per tenant (DDL 069), and so is the legajo. Another tenant owning an entity
    /// AND a legajo for the target CUIT is neither a conflict nor a legajo to link: this tenant gets
    /// its own, and the other tenant's rows are not touched.
    /// </summary>
    [Fact]
    public async Task Update_CuitUsedOnlyInAnotherTenant_Returns200AndNeverLinksThatTenantsLegajo()
    {
        using var h = BuildHarness();
        var legajoA       = Legajo(CuitHumanaA);
        var foreignLegajo = Legajo(CuitHumanaB, FiscalRegimes.Monotributo, "H", tenantId: OtherTenant);
        var entity        = Humana(CuitHumanaA, legajoA);
        var foreignEntity = Humana(CuitHumanaB, foreignLegajo, tenantId: OtherTenant);
        await SeedAsync(h, [entity, foreignEntity], [legajoA, foreignLegajo]);

        var result = await h.Sut.UpdateAsync(entity.EntityId, PutRequest(CuitHumanaB), CancellationToken.None);

        var response = AssertOk(result);

        var ours     = Assert.Single(await LegajosForAsync(h, Tenant, CuitHumanaB));
        var reloaded = await ReloadEntityAsync(h, entity.EntityId);
        Assert.NotEqual(foreignLegajo.PersonLegajoId, ours.PersonLegajoId);
        Assert.Equal(ours.PersonLegajoId, reloaded.PersonLegajoId);
        Assert.Equal(FiscalRegimes.Desconocido, reloaded.FiscalRegime);   // not the other tenant's Monotributo
        Assert.Null(response.MonotributoCategory);                        // not the other tenant's "H"

        AssertLegajoUnchanged(foreignLegajo, await ReloadLegajoAsync(h, foreignLegajo.PersonLegajoId));
        var foreignReloaded = await ReloadEntityAsync(h, foreignEntity.EntityId);
        Assert.Equal(foreignLegajo.PersonLegajoId, foreignReloaded.PersonLegajoId);
    }

    // ── PUT races (the index decides, the controller answers 409 — never 500) ──

    /// <summary>
    /// Two requests can both pass the pre-check read. When the entity+legajo save then loses on
    /// UQ_Entities_TenantId_CUIT or UX_Entities_PersonLegajoId, the answer is the pre-check's own
    /// 409 text, and nothing is written.
    /// </summary>
    [Fact]
    public async Task Update_LegajoSaveLosesTheEntityRace_Returns409()
    {
        using var h = BuildHarness();
        var legajoA = Legajo(CuitHumanaA);
        var entity  = Humana(CuitHumanaA, legajoA);
        await SeedAsync(h, [entity], [legajoA]);
        h.Legajos.LoseRaceOnSave = DuplicateCuitScope.Entity;

        var result = await h.Sut.UpdateAsync(entity.EntityId, PutRequest(CuitHumanaB), CancellationToken.None);

        AssertProblem(result, 409, ConflictDetail(CuitHumanaB));

        var reloaded = await ReloadEntityAsync(h, entity.EntityId);
        Assert.Equal(CuitHumanaA, reloaded.CUIT);
        Assert.Equal(legajoA.PersonLegajoId, reloaded.PersonLegajoId);
        Assert.Empty(await LegajosForAsync(h, Tenant, CuitHumanaB));
        Assert.Equal(1, h.Legajos.SaveCalls);
    }

    /// <summary>The same race on the path with no legajo — EntityRepository.UpdateAsync — is a 409 too.</summary>
    [Fact]
    public async Task Update_JuridicaUpdateLosesTheEntityRace_Returns409()
    {
        using var h = BuildHarness();
        var entity = Juridica(CuitJuridica);
        await SeedAsync(h, [entity], []);
        h.Entities.LoseRaceOnUpdate = DuplicateCuitScope.Entity;

        const string newCuit = "30-87654321-0";
        var result = await h.Sut.UpdateAsync(entity.EntityId, PutRequest(newCuit, NombreEmpresa), CancellationToken.None);

        AssertProblem(result, 409, ConflictDetail(newCuit));

        var reloaded = await ReloadEntityAsync(h, entity.EntityId);
        Assert.Equal(CuitJuridica, reloaded.CUIT);
        Assert.Equal(1, h.Entities.UpdateCalls);
    }

    /// <summary>
    /// Losing on UX_PersonLegajos_TenantId_CUIT is recoverable: another request created the new
    /// person's legajo first. The PUT re-reads it and links to the WINNER instead of failing, so the
    /// tenant still ends with exactly one legajo for that CUIL.
    /// </summary>
    [Fact]
    public async Task Update_LegajoSaveLosesTheLegajoRace_RelinksToTheWinner()
    {
        using var h = BuildHarness();
        var legajoA = Legajo(CuitHumanaA);
        var entity  = Humana(CuitHumanaA, legajoA);
        await SeedAsync(h, [entity], [legajoA]);

        var winner = Legajo(CuitHumanaB, FiscalRegimes.Monotributo, "F", fullName: "OTRA PERSONA DE PRUEBA");
        h.Legajos.LoseRaceOnSave   = DuplicateCuitScope.PersonLegajo;
        h.Legajos.ConcurrentWinner = winner;

        var result = await h.Sut.UpdateAsync(entity.EntityId, PutRequest(CuitHumanaB), CancellationToken.None);

        var response = AssertOk(result);

        var only = Assert.Single(await LegajosForAsync(h, Tenant, CuitHumanaB));
        Assert.Equal(winner.PersonLegajoId, only.PersonLegajoId);

        var reloaded = await ReloadEntityAsync(h, entity.EntityId);
        Assert.Equal(CuitHumanaB, reloaded.CUIT);
        Assert.Equal(winner.PersonLegajoId, reloaded.PersonLegajoId);
        Assert.Equal(FiscalRegimes.Monotributo, reloaded.FiscalRegime);

        Assert.Equal(winner.PersonLegajoId, response.PersonLegajoId);
        Assert.Equal("F", response.MonotributoCategory);
        Assert.Equal(2, h.Legajos.GetByCuitCalls);   // the pre-save lookup and the re-read
    }

    /// <summary>
    /// A legajo race whose winner cannot be re-read (only possible with a delete in between) has
    /// nothing to link to: it degrades to the 409, never to a 500 or a dangling link.
    /// </summary>
    [Fact]
    public async Task Update_LegajoRaceWithNoWinnerToReread_Returns409()
    {
        using var h = BuildHarness();
        var legajoA = Legajo(CuitHumanaA);
        var entity  = Humana(CuitHumanaA, legajoA);
        await SeedAsync(h, [entity], [legajoA]);
        h.Legajos.LoseRaceOnSave = DuplicateCuitScope.PersonLegajo;

        var result = await h.Sut.UpdateAsync(entity.EntityId, PutRequest(CuitHumanaB), CancellationToken.None);

        AssertProblem(result, 409, ConflictDetail(CuitHumanaB));

        var reloaded = await ReloadEntityAsync(h, entity.EntityId);
        Assert.Equal(CuitHumanaA, reloaded.CUIT);
        Assert.Equal(legajoA.PersonLegajoId, reloaded.PersonLegajoId);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // POST /api/entities/{id}/refresh-arca
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The ARCA ApiCallLogs writer shape <see cref="ArcaCallOutcome"/> rests on: a "no persona" answer is
    /// written with no error text, so the operations dashboard reads it as no data; a failed call carries
    /// its error text and stays a failure. Both rows carry the writer's hard-coded 502.
    /// </summary>
    [Fact]
    public async Task RefreshArca_ArcaKnowsNoPersona_LogsARowTheDashboardReadsAsNoData()
    {
        using var h = BuildHarness();
        var legajoA = Legajo(CuitHumanaA);
        var entity  = Humana(CuitHumanaA, legajoA);
        await SeedAsync(h, [entity], [legajoA]);
        h.Arca.Persona = null;

        await h.Sut.RefreshArcaAsync(entity.EntityId, CancellationToken.None);

        var call = Assert.Single(h.ApiCallLog.Calls);
        Assert.Equal(("ARCA", (int?)502, false, (string?)null), call);
        Assert.True(ApiCallLogRowMarkers.IsNoData(call.Service, call.IsSuccess, call.ResponseStatus, call.ErrorMessage));
    }

    [Fact]
    public async Task RefreshArca_ArcaUnreachable_LogsARowTheDashboardReadsAsAFailure()
    {
        using var h = BuildHarness();
        var legajoA = Legajo(CuitHumanaA);
        var entity  = Humana(CuitHumanaA, legajoA);
        await SeedAsync(h, [entity], [legajoA]);
        h.Arca.ToThrow = new HttpRequestException("503 Service Unavailable");

        await h.Sut.RefreshArcaAsync(entity.EntityId, CancellationToken.None);

        var call = Assert.Single(h.ApiCallLog.Calls);
        Assert.Equal(("ARCA", (int?)502, false), (call.Service, call.ResponseStatus, call.IsSuccess));
        Assert.NotNull(call.ErrorMessage);
        Assert.False(ApiCallLogRowMarkers.IsNoData(call.Service, call.IsSuccess, call.ResponseStatus, call.ErrorMessage));
    }

    /// <summary>
    /// ARCA is authoritative for the regime and the category, and the legajo is the person's record:
    /// the refresh lands on both, in the same save, with the SAME serialized snapshot the entity keeps.
    /// </summary>
    [Fact]
    public async Task RefreshArca_LinkedHumanaMonotributo_SyncsEntityAndLegajo()
    {
        using var h = BuildHarness();
        var legajoA = Legajo(CuitHumanaA);
        var entity  = Humana(CuitHumanaA, legajoA);
        await SeedAsync(h, [entity], [legajoA]);
        h.Arca.Persona = Persona(CuitHumanaA, "FISICA", Fiscal(FiscalRegimes.Monotributo, "D"));

        var before = DateTime.UtcNow;
        var result = await h.Sut.RefreshArcaAsync(entity.EntityId, CancellationToken.None);

        var response = AssertOk(result);

        var reloadedEntity = await ReloadEntityAsync(h, entity.EntityId);
        var reloadedLegajo = await ReloadLegajoAsync(h, legajoA.PersonLegajoId);

        Assert.Equal(FiscalRegimes.Monotributo, reloadedEntity.FiscalRegime);
        Assert.Equal(FiscalRegimes.Monotributo, reloadedLegajo.FiscalRegime);
        Assert.Equal("D", reloadedLegajo.MonotributoCategory);

        Assert.NotNull(reloadedEntity.FiscalDataJson);
        Assert.Equal(reloadedEntity.FiscalDataJson, reloadedLegajo.ArcaSnapshotJson);
        Assert.NotNull(reloadedLegajo.ArcaDataUpdatedAt);
        Assert.InRange(reloadedLegajo.ArcaDataUpdatedAt!.Value, before, DateTime.UtcNow);
        Assert.Equal(reloadedEntity.FiscalDataUpdatedAt, reloadedLegajo.ArcaDataUpdatedAt);
        Assert.True(reloadedLegajo.UpdatedAt >= before);

        Assert.Equal(PersonTypes.Humana, reloadedEntity.PersonType);
        Assert.Equal("D", response.MonotributoCategory);
        Assert.Equal(FiscalRegimes.Monotributo, response.FiscalRegime);
        Assert.Equal(legajoA.PersonLegajoId, response.PersonLegajoId);

        Assert.Equal(1, h.Arca.CallCount);
        Assert.Equal("20111111112", h.Arca.LastCuit);   // ARCA takes the CUIT without hyphens
        Assert.Equal(1, h.Legajos.SaveCalls);           // entity + legajo in ONE save
        Assert.False(h.Legajos.LastLegajoIsNew);
        Assert.Empty(h.Logger.Warnings);                // ARCA agrees with the stored type
        Assert.Empty(h.Logger.Errors);
    }

    /// <summary>
    /// The person type is decided at create (and by a CUIT change on PUT), never by a refresh. ARCA
    /// disagreeing is surfaced as ONE Warning for a human to look at — naming the two types, never the
    /// CUIL or the name, and no other log line of the refresh carries them either.
    /// </summary>
    [Fact]
    public async Task RefreshArca_ArcaSaysJuridicaForAHumana_KeepsThePersonTypeAndWarnsOnceWithoutPii()
    {
        using var h = BuildHarness();
        var legajoA = Legajo(CuitHumanaA);
        var entity  = Humana(CuitHumanaA, legajoA);
        await SeedAsync(h, [entity], [legajoA]);
        h.Arca.Persona = Persona(CuitHumanaA, "JURIDICA", Fiscal(FiscalRegimes.Monotributo, "D"));

        var result = await h.Sut.RefreshArcaAsync(entity.EntityId, CancellationToken.None);

        var response = AssertOk(result);
        Assert.Equal(PersonTypes.Humana, response.PersonType);
        Assert.Equal("D", response.MonotributoCategory);

        var reloaded = await ReloadEntityAsync(h, entity.EntityId);
        Assert.Equal(PersonTypes.Humana, reloaded.PersonType);
        Assert.Equal(legajoA.PersonLegajoId, reloaded.PersonLegajoId);

        var warning = Assert.Single(h.Logger.Warnings);
        Assert.Contains(PersonTypes.Juridica, warning, StringComparison.Ordinal);
        Assert.Contains(PersonTypes.Humana, warning, StringComparison.Ordinal);
        Assert.DoesNotContain(NombreHumana, warning, StringComparison.Ordinal);
        Assert.DoesNotContain("DE PRUEBA", warning, StringComparison.Ordinal);
        AssertNoCuilLogged(h, CuitHumanaA);
        Assert.Empty(h.Logger.Errors);
    }

    /// <summary>A category belongs to a monotributista only: a known other regime clears it on the legajo.</summary>
    [Fact]
    public async Task RefreshArca_ResponsableInscripto_ClearsTheLegajoCategory()
    {
        using var h = BuildHarness();
        var legajoA = Legajo(CuitHumanaA, FiscalRegimes.Monotributo, "C");
        var entity  = Humana(CuitHumanaA, legajoA);
        await SeedAsync(h, [entity], [legajoA]);
        h.Arca.Persona = Persona(CuitHumanaA, "FISICA", Fiscal(FiscalRegimes.ResponsableInscripto, null));

        var result = await h.Sut.RefreshArcaAsync(entity.EntityId, CancellationToken.None);

        var response       = AssertOk(result);
        var reloadedEntity = await ReloadEntityAsync(h, entity.EntityId);
        var reloadedLegajo = await ReloadLegajoAsync(h, legajoA.PersonLegajoId);

        Assert.Equal(FiscalRegimes.ResponsableInscripto, reloadedEntity.FiscalRegime);
        Assert.Equal(FiscalRegimes.ResponsableInscripto, reloadedLegajo.FiscalRegime);
        Assert.Null(reloadedLegajo.MonotributoCategory);
        Assert.Null(response.MonotributoCategory);
    }

    /// <summary>
    /// A monotributista whose refresh carries no category letter (ARCA sent none, or one the parser
    /// could not place) keeps the category already on file rather than losing it.
    /// </summary>
    [Fact]
    public async Task RefreshArca_MonotributoWithoutACategory_KeepsTheStoredCategory()
    {
        using var h = BuildHarness();
        var legajoA = Legajo(CuitHumanaA, FiscalRegimes.Monotributo, "C");
        var entity  = Humana(CuitHumanaA, legajoA);
        await SeedAsync(h, [entity], [legajoA]);
        h.Arca.Persona = Persona(CuitHumanaA, "FISICA", Fiscal(FiscalRegimes.Monotributo, null));

        var result = await h.Sut.RefreshArcaAsync(entity.EntityId, CancellationToken.None);

        var response = AssertOk(result);
        var reloaded = await ReloadLegajoAsync(h, legajoA.PersonLegajoId);
        Assert.Equal(FiscalRegimes.Monotributo, reloaded.FiscalRegime);
        Assert.Equal("C", reloaded.MonotributoCategory);
        Assert.Equal("C", response.MonotributoCategory);
    }

    /// <summary>
    /// The no-downgrade rule. A PARTIAL answer (an error section beside datosGenerales) that cannot
    /// determine the regime must not overwrite a known Monotributo 'C' with Desconocido and no
    /// category — the snapshot is still refreshed, since what ARCA did send is the latest word. A
    /// COMPLETE answer that says Desconocido is authoritative and does downgrade.
    /// </summary>
    [Theory]
    [InlineData(true,  FiscalRegimes.Monotributo, "C")]    // partial: keep what is known
    [InlineData(false, FiscalRegimes.Desconocido, null)]   // complete: ARCA's word stands
    public async Task RefreshArca_DesconocidoOnAMonotributista_DowngradesOnlyOnACompleteAnswer(
        bool isIncomplete, string expectedRegime, string? expectedCategory)
    {
        using var h = BuildHarness();
        var legajoA = Legajo(CuitHumanaA, FiscalRegimes.Monotributo, "C");
        var entity  = Humana(CuitHumanaA, legajoA);
        await SeedAsync(h, [entity], [legajoA]);
        h.Arca.Persona = Persona(CuitHumanaA, "FISICA", Fiscal(FiscalRegimes.Desconocido, null), isIncomplete);

        var result = await h.Sut.RefreshArcaAsync(entity.EntityId, CancellationToken.None);

        var response       = AssertOk(result);
        var reloadedEntity = await ReloadEntityAsync(h, entity.EntityId);
        var reloadedLegajo = await ReloadLegajoAsync(h, legajoA.PersonLegajoId);

        Assert.Equal(expectedRegime, reloadedEntity.FiscalRegime);
        Assert.Equal(expectedRegime, reloadedLegajo.FiscalRegime);
        Assert.Equal(expectedCategory, reloadedLegajo.MonotributoCategory);
        Assert.Equal(expectedCategory, response.MonotributoCategory);

        // The snapshot is written either way — replaced, not merged, and stamped.
        Assert.NotEqual(legajoA.ArcaSnapshotJson, reloadedLegajo.ArcaSnapshotJson);
        Assert.Equal(reloadedEntity.FiscalDataJson, reloadedLegajo.ArcaSnapshotJson);
        Assert.NotEqual(SeedTime, reloadedLegajo.ArcaDataUpdatedAt);
        Assert.Equal(reloadedEntity.FiscalDataUpdatedAt, reloadedLegajo.ArcaDataUpdatedAt);
    }

    /// <summary>A company gets its regime from ARCA too, and has no legajo to read or write.</summary>
    [Fact]
    public async Task RefreshArca_Juridica_SetsTheRegimeAndTouchesNoLegajo()
    {
        using var h = BuildHarness();
        var entity = Juridica(CuitJuridica);
        await SeedAsync(h, [entity], []);
        h.Arca.Persona = Persona(CuitJuridica, "JURIDICA", Fiscal(FiscalRegimes.ResponsableInscripto, null));

        var result = await h.Sut.RefreshArcaAsync(entity.EntityId, CancellationToken.None);

        var response = AssertOk(result);
        var reloaded = await ReloadEntityAsync(h, entity.EntityId);
        Assert.Equal(FiscalRegimes.ResponsableInscripto, reloaded.FiscalRegime);
        Assert.Equal(PersonTypes.Juridica, reloaded.PersonType);
        Assert.Null(reloaded.PersonLegajoId);
        Assert.Equal(FiscalRegimes.ResponsableInscripto, response.FiscalRegime);
        Assert.Null(response.MonotributoCategory);

        Assert.Equal(0, h.Legajos.TotalCalls);
        Assert.Equal(1, h.Entities.UpdateCalls);
        Assert.Equal(0, await h.Db.PersonLegajos.CountAsync());
        Assert.Empty(h.Logger.Warnings);
    }

    // ── refresh-arca: key status and activity start (M10, persona humana ARCA gaps) ──

    private static ArcaFiscalData FiscalWithPeriods(DateTime? fechaInicio, params string[] impuestoPeriodos)
        => Fiscal(FiscalRegimes.Monotributo, "D") with
        {
            Impuestos            = impuestoPeriodos.Select(p => new ArcaImpuesto("20", "MONOTRIBUTO", "AC", null, p)).ToList(),
            FechaInicioActividad = fechaInicio
        };

    private static ArcaFiscalData ReadSnapshot(string? json)
        => System.Text.Json.JsonSerializer.Deserialize<ArcaFiscalData>(json!)!;

    /// <summary>
    /// Both gaps close on the refresh: the snapshot keeps ARCA's estadoClave and the instant it was
    /// read, and the legajo's ActivityStartDate is the earliest registration ARCA reports.
    /// </summary>
    [Fact]
    public async Task RefreshArca_StoresTheKeyStatusAndTheActivityStart()
    {
        using var h = BuildHarness();
        var legajoA = Legajo(CuitHumanaA);
        var entity  = Humana(CuitHumanaA, legajoA);
        await SeedAsync(h, [entity], [legajoA]);
        h.Arca.Persona = Persona(CuitHumanaA, "FISICA", FiscalWithPeriods(new DateTime(2014, 6, 1), "201803", "201511"));

        var before = DateTime.UtcNow;
        AssertOk(await h.Sut.RefreshArcaAsync(entity.EntityId, CancellationToken.None));

        var reloadedLegajo = await ReloadLegajoAsync(h, legajoA.PersonLegajoId);
        var snapshot       = ReadSnapshot(reloadedLegajo.ArcaSnapshotJson);

        Assert.Equal(new DateTime(2014, 6, 1), reloadedLegajo.ActivityStartDate);
        Assert.Equal("ACTIVO", snapshot.EstadoClave);
        Assert.NotNull(snapshot.EstadoClaveConsultadoAt);
        Assert.InRange(snapshot.EstadoClaveConsultadoAt!.Value, before, DateTime.UtcNow);
        Assert.Equal((await ReloadEntityAsync(h, entity.EntityId)).FiscalDataJson, reloadedLegajo.ArcaSnapshotJson);
        AssertNoCuilLogged(h, CuitHumanaA);
    }

    [Fact]
    public async Task RefreshArca_ActivityStart_FallsBackToTheListPeriods()
    {
        using var h = BuildHarness();
        var legajoA = Legajo(CuitHumanaA);
        var entity  = Humana(CuitHumanaA, legajoA);
        await SeedAsync(h, [entity], [legajoA]);
        h.Arca.Persona = Persona(CuitHumanaA, "FISICA", FiscalWithPeriods(null, "201803", "201511"));

        AssertOk(await h.Sut.RefreshArcaAsync(entity.EntityId, CancellationToken.None));

        Assert.Equal(new DateTime(2015, 11, 1), (await ReloadLegajoAsync(h, legajoA.PersonLegajoId)).ActivityStartDate);
    }

    [Fact]
    public async Task RefreshArca_NoPeriods_KeepsTheStoredActivityStart()
    {
        using var h = BuildHarness();
        var legajoA = Legajo(CuitHumanaA);
        legajoA.ActivityStartDate = new DateTime(2010, 1, 1);
        var entity  = Humana(CuitHumanaA, legajoA);
        await SeedAsync(h, [entity], [legajoA]);
        h.Arca.Persona = Persona(CuitHumanaA, "FISICA", FiscalWithPeriods(null));

        AssertOk(await h.Sut.RefreshArcaAsync(entity.EntityId, CancellationToken.None));

        Assert.Equal(new DateTime(2010, 1, 1), (await ReloadLegajoAsync(h, legajoA.PersonLegajoId)).ActivityStartDate);
    }

    /// <summary>
    /// ARCA is the column's only writer, so a complete answer replaces the date either way; a partial one
    /// (a section errored, so periods may be missing) may only move it EARLIER.
    /// </summary>
    [Theory]
    [InlineData(false, 2020, 2020)]
    [InlineData(true,  2020, 2010)]
    [InlineData(true,  2005, 2005)]
    public async Task RefreshArca_ActivityStart_APartialAnswerNeverMovesItLater(bool isIncomplete, int arcaYear, int expectedYear)
    {
        using var h = BuildHarness();
        var legajoA = Legajo(CuitHumanaA);
        legajoA.ActivityStartDate = new DateTime(2010, 1, 1);
        var entity  = Humana(CuitHumanaA, legajoA);
        await SeedAsync(h, [entity], [legajoA]);
        h.Arca.Persona = Persona(CuitHumanaA, "FISICA", FiscalWithPeriods(new DateTime(arcaYear, 1, 1)), isIncomplete);

        AssertOk(await h.Sut.RefreshArcaAsync(entity.EntityId, CancellationToken.None));

        Assert.Equal(new DateTime(expectedYear, 1, 1), (await ReloadLegajoAsync(h, legajoA.PersonLegajoId)).ActivityStartDate);
    }

    /// <summary>
    /// ARCA answering with datosGenerales and a key status but no fiscal section — the likely shape for an
    /// inactive key — stores the key status alone: lists, regime, category, activity start and both
    /// snapshot stamps stay as they were, and the refresh answers 200.
    /// </summary>
    [Fact]
    public async Task RefreshArca_KeyStatusOnly_MergesItIntoTheSnapshotsAndTouchesNothingElse()
    {
        using var h = BuildHarness();
        var seededSnapshot = System.Text.Json.JsonSerializer.Serialize(
            FiscalWithPeriods(new DateTime(2015, 1, 1), "201501") with { EstadoClave = "ACTIVO", EstadoClaveConsultadoAt = SeedTime });
        var legajoA = Legajo(CuitHumanaA, FiscalRegimes.Monotributo, "D");
        legajoA.ArcaSnapshotJson  = seededSnapshot;
        legajoA.ActivityStartDate = new DateTime(2015, 1, 1);
        var entity = Humana(CuitHumanaA, legajoA);
        entity.FiscalDataJson      = seededSnapshot;
        entity.FiscalDataUpdatedAt = SeedTime;
        await SeedAsync(h, [entity], [legajoA]);
        h.Arca.Persona = Persona(CuitHumanaA, "FISICA", Fiscal(null, null)) with
        {
            FiscalData  = null,
            EstadoClave = " INACTIVO "
        };

        var before = DateTime.UtcNow;
        var result = await h.Sut.RefreshArcaAsync(entity.EntityId, CancellationToken.None);

        AssertOk(result);
        var reloadedLegajo = await ReloadLegajoAsync(h, legajoA.PersonLegajoId);
        var reloadedEntity = await ReloadEntityAsync(h, entity.EntityId);

        foreach (var json in new[] { reloadedLegajo.ArcaSnapshotJson, reloadedEntity.FiscalDataJson })
        {
            var snapshot = ReadSnapshot(json);
            Assert.Equal("INACTIVO", snapshot.EstadoClave);
            Assert.InRange(snapshot.EstadoClaveConsultadoAt!.Value, before, DateTime.UtcNow);
            Assert.Equal("201501", Assert.Single(snapshot.Impuestos).Periodo);   // the lists stand
            Assert.Equal(FiscalRegimes.Monotributo, snapshot.FiscalRegime);
        }

        Assert.Equal(FiscalRegimes.Monotributo, reloadedLegajo.FiscalRegime);
        Assert.Equal("D", reloadedLegajo.MonotributoCategory);
        Assert.Equal(new DateTime(2015, 1, 1), reloadedLegajo.ActivityStartDate);
        Assert.Equal(SeedTime, reloadedLegajo.ArcaDataUpdatedAt);      // older lists never look freshly read
        Assert.Equal(SeedTime, reloadedEntity.FiscalDataUpdatedAt);
        Assert.Equal(FiscalRegimes.Monotributo, reloadedEntity.FiscalRegime);

        var warning = Assert.Single(h.Logger.Warnings);
        Assert.Contains(legajoA.PersonLegajoId.ToString(), warning, StringComparison.Ordinal);
        Assert.DoesNotContain("INACTIVO", warning, StringComparison.Ordinal);
        AssertNoCuilLogged(h, CuitHumanaA);
    }

    [Fact]
    public async Task RefreshArca_KeyStatusOnly_WithNoSnapshotYet_StoresAMinimalOne()
    {
        using var h = BuildHarness();
        var legajoA = Legajo(CuitHumanaA);
        legajoA.ArcaSnapshotJson  = null;
        legajoA.ArcaDataUpdatedAt = null;
        var entity = Humana(CuitHumanaA, legajoA);
        await SeedAsync(h, [entity], [legajoA]);
        h.Arca.Persona = Persona(CuitHumanaA, "FISICA", Fiscal(null, null)) with
        {
            FiscalData  = null,
            EstadoClave = "INACTIVO"
        };

        AssertOk(await h.Sut.RefreshArcaAsync(entity.EntityId, CancellationToken.None));

        var reloadedLegajo = await ReloadLegajoAsync(h, legajoA.PersonLegajoId);
        var snapshot       = ReadSnapshot(reloadedLegajo.ArcaSnapshotJson);
        Assert.Equal("INACTIVO", snapshot.EstadoClave);
        Assert.Empty(snapshot.Impuestos);
        Assert.Null(reloadedLegajo.ArcaDataUpdatedAt);
        Assert.Null(reloadedLegajo.ActivityStartDate);
    }

    [Fact]
    public async Task RefreshArca_NoFiscalSectionAndNoKeyStatus_IsStill502_AndChangesNothing()
    {
        using var h = BuildHarness();
        var legajoA = Legajo(CuitHumanaA);
        var entity  = Humana(CuitHumanaA, legajoA);
        await SeedAsync(h, [entity], [legajoA]);
        h.Arca.Persona = Persona(CuitHumanaA, "FISICA", Fiscal(null, null)) with
        {
            FiscalData  = null,
            EstadoClave = null
        };

        var result = await h.Sut.RefreshArcaAsync(entity.EntityId, CancellationToken.None);

        AssertProblem(result, 502, "ARCA no devolvió datos fiscales para este CUIT.");
        AssertLegajoUnchanged(legajoA, await ReloadLegajoAsync(h, legajoA.PersonLegajoId));
        Assert.Equal(0, h.Legajos.SaveCalls);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // POST /api/entities/{id}/refresh-bcra
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A persona humana's BCRA record is queried by CUIL, so it is the PERSON's: the legajo keeps the
    /// same payload and timestamp as the entity — from the ONE call, copied, never fetched twice.
    /// </summary>
    [Fact]
    public async Task RefreshBcra_LinkedHumana_CopiesTheOneSnapshotOntoTheLegajo()
    {
        using var h = BuildHarness();
        var legajoA = Legajo(CuitHumanaA, FiscalRegimes.Monotributo, "C");
        var entity  = Humana(CuitHumanaA, legajoA);
        await SeedAsync(h, [entity], [legajoA]);
        h.Bcra.Result = new BcraData { Deuda = new BcraDeudaData { SituacionMaxima = 2, MontoTotal = 1500m } };

        var before = DateTime.UtcNow;
        var result = await h.Sut.RefreshBcraAsync(entity.EntityId, CancellationToken.None);

        var response = AssertOk(result);
        Assert.Equal(1, h.Bcra.CallCount);

        var reloadedEntity = await ReloadEntityAsync(h, entity.EntityId);
        var reloadedLegajo = await ReloadLegajoAsync(h, legajoA.PersonLegajoId);

        Assert.NotNull(reloadedEntity.BcraDataJson);
        Assert.Contains("\"situacionMaxima\":2", reloadedEntity.BcraDataJson, StringComparison.Ordinal);
        Assert.Equal(reloadedEntity.BcraDataJson, reloadedLegajo.BcraDataJson);
        Assert.NotNull(reloadedEntity.BcraDataUpdatedAt);
        Assert.InRange(reloadedEntity.BcraDataUpdatedAt!.Value, before, DateTime.UtcNow);
        Assert.Equal(reloadedEntity.BcraDataUpdatedAt, reloadedLegajo.BcraDataUpdatedAt);
        Assert.True(reloadedLegajo.UpdatedAt >= before);

        // Only the BCRA pair moves on the legajo.
        Assert.Equal(legajoA.ArcaSnapshotJson, reloadedLegajo.ArcaSnapshotJson);
        Assert.Equal(FiscalRegimes.Monotributo, reloadedLegajo.FiscalRegime);
        Assert.Equal("C", reloadedLegajo.MonotributoCategory);

        Assert.Equal(1, h.Legajos.SaveCalls);
        Assert.Equal(0, h.Entities.UpdateCalls);   // the entity went out in the legajo save, not separately
        Assert.Equal("C", response.MonotributoCategory);
    }

    [Fact]
    public async Task RefreshBcra_Juridica_DoesNoLegajoWork()
    {
        using var h = BuildHarness();
        var entity = Juridica(CuitJuridica);
        await SeedAsync(h, [entity], []);
        h.Bcra.Result = new BcraData { Deuda = new BcraDeudaData { SituacionMaxima = 1 } };

        var result = await h.Sut.RefreshBcraAsync(entity.EntityId, CancellationToken.None);

        AssertOk(result);
        var reloaded = await ReloadEntityAsync(h, entity.EntityId);
        Assert.NotNull(reloaded.BcraDataJson);
        Assert.NotNull(reloaded.BcraDataUpdatedAt);

        Assert.Equal(1, h.Bcra.CallCount);
        Assert.Equal(1, h.Entities.UpdateCalls);
        Assert.Equal(0, h.Legajos.TotalCalls);
        Assert.Equal(0, await h.Db.PersonLegajos.CountAsync());
    }

    // ── V8: refresh-bcra never stores a failed Central de Deudores ───────────

    private static readonly DateTime StoredChequesFetchedAt = new(2026, 1, 10, 9, 0, 0, DateTimeKind.Utc);

    private static string StoredGoodBcraJson() => System.Text.Json.JsonSerializer.Serialize(new BcraData
    {
        Deuda   = new BcraDeudaData { Estado = BcraDeudaConsultaEstado.DebtFound, SituacionMaxima = 1, MontoTotal = 999m },
        Cheques = new BcraChequesData
        {
            Estado              = BcraChequesConsultaEstado.ChequesFound,
            TotalRechazados     = 1,
            MontoTotalRechazado = 7000m,
            FetchedAt           = StoredChequesFetchedAt
        }
    });

    private static string ErrorCode(IActionResult result, int status)
    {
        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(status, obj.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(obj.Value);
        return Assert.IsType<string>(problem.Extensions["errorCode"]);
    }

    [Fact]
    public async Task RefreshBcra_DeudaLookupFailed_502_BcraDeudaUnavailable_AndNothingIsWritten()
    {
        using var h = BuildHarness();
        var stored  = StoredGoodBcraJson();
        var legajoA = Legajo(CuitHumanaA);
        legajoA.BcraDataJson = stored;
        var entity  = Humana(CuitHumanaA, legajoA);
        entity.BcraDataJson      = stored;
        entity.BcraDataUpdatedAt = SeedTime;
        await SeedAsync(h, [entity], [legajoA]);
        h.Bcra.Result = new BcraData
        {
            Deuda     = new BcraDeudaData { Estado = BcraDeudaConsultaEstado.LookupFailed },
            Cambiario = new BcraCambiarioData { TipoCambioOficial = 1000 },
            Cheques   = new BcraChequesData { Estado = BcraChequesConsultaEstado.NoChequesReported }
        };

        var result = await h.Sut.RefreshBcraAsync(entity.EntityId, CancellationToken.None);

        Assert.Equal("bcra_deuda_unavailable", ErrorCode(result, 502));
        var problem = Assert.IsType<ProblemDetails>(Assert.IsType<ObjectResult>(result).Value);
        Assert.Equal(
            "No se pudo consultar la Central de Deudores del BCRA. No se actualizó la información guardada; intentá nuevamente en unos minutos.",
            problem.Detail);

        var reloadedEntity = await ReloadEntityAsync(h, entity.EntityId);
        var reloadedLegajo = await ReloadLegajoAsync(h, legajoA.PersonLegajoId);
        Assert.Equal(stored, reloadedEntity.BcraDataJson);
        Assert.Equal(SeedTime, reloadedEntity.BcraDataUpdatedAt);
        AssertLegajoUnchanged(legajoA, reloadedLegajo);
        Assert.Equal(0, h.Entities.UpdateCalls);
        Assert.Equal(0, h.Legajos.SaveCalls);
    }

    [Fact]
    public async Task RefreshBcra_AllSourcesFailed_502_BcraUnavailable_AndNothingIsWritten()
    {
        using var h = BuildHarness();
        var entity = Juridica(CuitJuridica);
        entity.BcraDataJson = StoredGoodBcraJson();
        await SeedAsync(h, [entity], []);
        h.Bcra.Result = null;

        var result = await h.Sut.RefreshBcraAsync(entity.EntityId, CancellationToken.None);

        Assert.Equal("bcra_unavailable", ErrorCode(result, 502));
        Assert.Equal(entity.BcraDataJson, (await ReloadEntityAsync(h, entity.EntityId)).BcraDataJson);
        Assert.Equal(0, h.Entities.UpdateCalls);
    }

    [Fact]
    public async Task RefreshBcra_ChequesLookupFailed_KeepsTheStoredCheques_OnEntityAndLegajo()
    {
        using var h = BuildHarness();
        var stored  = StoredGoodBcraJson();
        var legajoA = Legajo(CuitHumanaA);
        legajoA.BcraDataJson = stored;
        var entity  = Humana(CuitHumanaA, legajoA);
        entity.BcraDataJson = stored;
        await SeedAsync(h, [entity], [legajoA]);
        h.Bcra.Result = new BcraData
        {
            Deuda   = new BcraDeudaData { Estado = BcraDeudaConsultaEstado.DebtFound, SituacionMaxima = 2, MontoTotal = 3131m },
            Cheques = new BcraChequesData { Estado = BcraChequesConsultaEstado.LookupFailed, FetchedAt = DateTime.UtcNow }
        };

        AssertOk(await h.Sut.RefreshBcraAsync(entity.EntityId, CancellationToken.None));

        var reloadedEntity = await ReloadEntityAsync(h, entity.EntityId);
        var saved = PersonSnapshotWriter.ReadBcra(reloadedEntity.BcraDataJson)!;
        Assert.Equal(3131m, saved.Deuda!.MontoTotal);
        Assert.Equal(BcraChequesConsultaEstado.ChequesFound, saved.Cheques!.Estado);
        Assert.Equal(7000m, saved.Cheques.MontoTotalRechazado);
        Assert.Equal(StoredChequesFetchedAt, saved.Cheques.FetchedAt.ToUniversalTime());
        Assert.Equal(reloadedEntity.BcraDataJson, (await ReloadLegajoAsync(h, legajoA.PersonLegajoId)).BcraDataJson);
    }

    [Fact]
    public async Task RefreshBcra_ChequesLookupFailed_WithNoRealStoredCheques_StoresTheFailedBlock()
    {
        using var h = BuildHarness();
        var entity = Juridica(CuitJuridica);
        await SeedAsync(h, [entity], []);
        h.Bcra.Result = new BcraData
        {
            Deuda   = new BcraDeudaData { Estado = BcraDeudaConsultaEstado.NoDebtReported },
            Cheques = new BcraChequesData { Estado = BcraChequesConsultaEstado.LookupFailed }
        };

        AssertOk(await h.Sut.RefreshBcraAsync(entity.EntityId, CancellationToken.None));

        var saved = PersonSnapshotWriter.ReadBcra((await ReloadEntityAsync(h, entity.EntityId)).BcraDataJson)!;
        Assert.Equal(BcraChequesConsultaEstado.LookupFailed, saved.Cheques!.Estado);
    }

    /// <summary>
    /// Belt and braces: a link whose legajo is not found for the tenant (the FK makes this impossible
    /// in SQL) is logged and treated as no legajo — the entity is still refreshed, not a 500.
    /// </summary>
    [Fact]
    public async Task RefreshBcra_DanglingLegajoLink_RefreshesTheEntityOnlyAndWarns()
    {
        using var h = BuildHarness();
        var entity = Humana(CuitHumanaA, legajo: null, fiscalRegime: FiscalRegimes.Desconocido);
        entity.PersonLegajoId = Guid.NewGuid();   // points at nothing
        await SeedAsync(h, [entity], []);
        h.Bcra.Result = new BcraData { Deuda = new BcraDeudaData { SituacionMaxima = 1 } };

        var result = await h.Sut.RefreshBcraAsync(entity.EntityId, CancellationToken.None);

        var response = AssertOk(result);
        Assert.Null(response.MonotributoCategory);

        var reloaded = await ReloadEntityAsync(h, entity.EntityId);
        Assert.NotNull(reloaded.BcraDataJson);
        Assert.Equal(1, h.Entities.UpdateCalls);
        Assert.Equal(0, h.Legajos.SaveCalls);
        Assert.Single(h.Logger.Warnings);
        AssertNoCuilLogged(h, CuitHumanaA);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Read paths — the category lives on the legajo
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// GET cuit/{cuit} holds the Entity but not its legajo, so BuildEntityResponseAsync fetches the
    /// category with the one-column, tenant-scoped read — not the whole legajo with its three
    /// nvarchar(max) snapshots.
    /// </summary>
    [Fact]
    public async Task GetByCuit_LinkedHumana_ReturnsTheLegajoCategoryThroughTheNarrowRead()
    {
        using var h = BuildHarness();
        var legajoA = Legajo(CuitHumanaA, FiscalRegimes.Monotributo, "G");
        var entity  = Humana(CuitHumanaA, legajoA);
        await SeedAsync(h, [entity], [legajoA]);

        var result = await h.Sut.GetByCuitAsync(CuitHumanaA, CancellationToken.None);

        var response = AssertOk(result);
        Assert.Equal(entity.EntityId, response.EntityId);
        Assert.Equal(PersonTypes.Humana, response.PersonType);
        Assert.Equal(FiscalRegimes.Monotributo, response.FiscalRegime);
        Assert.Equal(legajoA.PersonLegajoId, response.PersonLegajoId);
        Assert.Equal("G", response.MonotributoCategory);

        Assert.Equal(1, h.Legajos.GetCategoryCalls);
        Assert.Equal(0, h.Legajos.GetByIdCalls);     // never the full legajo row
        Assert.Equal(0, h.Legajos.GetByCuitCalls);
    }

    [Fact]
    public async Task GetByCuit_Juridica_ReadsNoLegajo()
    {
        using var h = BuildHarness();
        var entity = Juridica(CuitJuridica, FiscalRegimes.ResponsableInscripto);
        await SeedAsync(h, [entity], []);

        var result = await h.Sut.GetByCuitAsync(CuitJuridica, CancellationToken.None);

        var response = AssertOk(result);
        Assert.Equal(PersonTypes.Juridica, response.PersonType);
        Assert.Null(response.PersonLegajoId);
        Assert.Null(response.MonotributoCategory);
        Assert.Equal(0, h.Legajos.TotalCalls);
    }

    /// <summary>
    /// The narrow read filters by the entity's tenant too. A link to another tenant's legajo — which
    /// no code path writes, and the repository refuses to save — must not leak that legajo's data.
    /// </summary>
    [Fact]
    public async Task GetByCuit_LinkToAnotherTenantsLegajo_LeaksNoCategory()
    {
        using var h = BuildHarness();
        var foreignLegajo = Legajo(CuitHumanaA, FiscalRegimes.Monotributo, "K", tenantId: OtherTenant);
        var entity        = Humana(CuitHumanaA, foreignLegajo);
        await SeedAsync(h, [entity], [foreignLegajo]);

        var result = await h.Sut.GetByCuitAsync(CuitHumanaA, CancellationToken.None);

        var response = AssertOk(result);
        Assert.Null(response.MonotributoCategory);
        Assert.Equal(1, h.Legajos.GetCategoryCalls);
    }

    /// <summary>
    /// Every action answering with a full entity goes through BuildEntityResponseAsync, so the payload
    /// is the same whichever produced it. refresh-casfog holds no legajo: the category arrives through
    /// the narrow read, and the refresh writes nothing to the legajo.
    /// </summary>
    [Fact]
    public async Task RefreshCasfog_LinkedHumana_ResponseCarriesTheLegajoCategory()
    {
        using var h = BuildHarness();
        var legajoA = Legajo(CuitHumanaA, FiscalRegimes.Monotributo, "B");
        var entity  = Humana(CuitHumanaA, legajoA);
        await SeedAsync(h, [entity], [legajoA]);

        var result = await h.Sut.RefreshCasfogAsync(entity.EntityId, CancellationToken.None);

        var response = AssertOk(result);
        Assert.Equal(1, h.Casfog.CallCount);
        Assert.Equal("B", response.MonotributoCategory);
        Assert.Equal(legajoA.PersonLegajoId, response.PersonLegajoId);

        Assert.Equal(1, h.Legajos.GetCategoryCalls);
        Assert.Equal(0, h.Legajos.SaveCalls);
        AssertLegajoUnchanged(legajoA, await ReloadLegajoAsync(h, legajoA.PersonLegajoId));
    }
}
