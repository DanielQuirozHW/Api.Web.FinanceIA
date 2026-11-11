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
using Api.Web.FinanceIA.Models.Lufe;
using Api.Web.FinanceIA.Models.Nosis;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.Legajo;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Covers persona humana on entity creation (M10 phase 1, DDL 069): POST /api/entities and the
/// POST /api/entities/lookup-arca answer the Portal pre-fills it from.
///
/// <para>
/// Why these exist. A persona humana is created TOGETHER with its <see cref="PersonLegajo"/> — one
/// row per (tenant, CUIL), written in the same SaveChanges as the entity — and several rules ride on
/// that write that nothing else would notice breaking: the legajo carries the SAME hyphenated CUIT as
/// the entity (the key the DDL 069 backfill uses too), an existing legajo is linked rather than
/// duplicated, a request without informative fiscal data never downgrades a regime the legajo already
/// knows, the entity's regime is made to agree with its legajo's, and the one BCRA answer the create
/// already fetches is copied onto the legajo instead of paying for a second query. The per-tenant CUIT
/// uniqueness (the old index was global) and the races against it are what turns a lost write into the
/// same 409 the pre-check gives, instead of a 500.
/// </para>
///
/// <para>
/// The harness runs the REAL <see cref="EntityRepository"/> and <see cref="PersonLegajoRepository"/>
/// over one InMemory <see cref="FinanciaDbContext"/> per test, and every assertion about persisted
/// state reads the store back through a FRESH context — so a write that only happened on a tracked
/// object, or that a fault-isolated block swallowed, cannot pass. The races swap in hand-rolled
/// decorators (or a SaveChanges interceptor naming a real index) for the one write that must lose.
/// Everything else is the create-path stub set of <c>EntitiesControllerCreateTests</c>; members off
/// the tested paths throw, so an unexpected call is loud. Synthetic CUITs and names only.
/// </para>
/// </summary>
public sealed class EntitiesControllerPersonaHumanaCreateTests
{
    private const string Tenant       = "tenant-1";
    private const string OtherTenant  = "tenant-2";
    private const string Uid          = "firebase-uid-1";
    private const string HumanaCuit   = "20-11111111-2";
    private const string JuridicaCuit = "30-12345678-9";
    private const string AmbiguousCuit = "50-12345678-1";

    private const string InvalidPersonTypeMessage = "El tipo de persona debe ser 'Juridica' o 'Humana'.";
    private const string SeededSnapshot           = "{\"seeded\":true}";

    private static readonly DateTime Seeded = new(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>One root for the class; each harness still gets its own database name.</summary>
    private static readonly InMemoryDatabaseRoot DatabaseRoot = new();

    // ── Infrastructure stubs ──────────────────────────────────────────────────
    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; }
    }

    /// <summary>A fixed author, so the legajo's CreatedBy can be asserted against it.</summary>
    private sealed class StubUserRepository : IUserRepository
    {
        public Guid UserId { get; } = Guid.NewGuid();

        public Task<User?> GetByFirebaseUidAsync(string firebaseUid, CancellationToken ct)
            => Task.FromResult<User?>(new User
            {
                UserId      = UserId,
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
        // Team assignment (DDL 063) — unused by this suite.
        public Task UpdateTeamAsync(Guid userId, Guid? teamId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> ClearTeamAsync(Guid teamId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
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
        public Task LogAsync(string tenantId, string service, string endpoint, int? responseStatus,
            int? inputTokens, int? outputTokens, decimal? estimatedCostUsd, bool isSuccess, string? errorMessage,
            int durationMs, string? operation = null, string? model = null, Guid? userId = null,
            Guid? analysisId = null, long? chatMessageId = null, Guid? documentId = null, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    /// <summary>
    /// Every optional integration entitled (the SGR preset), so the fan-out runs as it does in
    /// production; the LUFE block still skips on the missing per-tenant key.
    /// </summary>
    private sealed class StubTenantProfileService : ITenantProfileService
    {
        public Task<TenantProfileResolution> GetEffectiveAsync(string tenantId, CancellationToken ct)
            => Task.FromResult(new TenantProfileResolution(TenantProfilePresets.Sgr, TenantProfileSource.Configured));

        // EntitiesController only ever reads the profile — it gates vendor calls and never writes.
        public Task<TenantProfileResolution> SetEntityTypeAsync(string tenantId, string entityType, CancellationToken ct)
            => throw new NotSupportedException("EntitiesController must not write the tenant profile.");
    }

    // ── Vendor stubs (all counting) ───────────────────────────────────────────

    /// <summary>
    /// Records every CUIT it is asked for, so "exactly ONE BCRA call" is a count and not a guess, and
    /// can be made to throw — the RST the real /centraldedeudores intermittently answers with.
    /// </summary>
    private sealed class StubBcraService : IBcraService
    {
        public List<string> QueriedCuits { get; } = [];
        public BcraData? Result { get; set; } = new();
        public Exception? ThrowOnCall { get; set; }

        public int CallCount => QueriedCuits.Count;

        public Task<BcraData?> GetBcraDataForAnalysisAsync(string cuit, CancellationToken ct)
        {
            QueriedCuits.Add(cuit);
            if (ThrowOnCall is not null) throw ThrowOnCall;
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

    /// <summary>
    /// Returns DataFound on purpose, as in EntitiesControllerCreateTests: an eager NOSIS lookup on
    /// create — for a Juridica or for a persona humana's legajo — would be billed per query.
    /// </summary>
    private sealed class StubNosisService : INosisService
    {
        public int CallCount { get; private set; }

        public Task<NosisData> GetVariablesAsync(
            string tenantId, long cuit, NosisVariableGroup grupo, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(new NosisData { Estado = NosisConsultaEstado.DataFound, ScoreVigente = 612 });
        }
    }

    /// <summary>Not reached: the harness configures no Tenants:{tenantId}:LufeApiKey.</summary>
    private sealed class StubLufeService : ILufeService
    {
        public int CallCount { get; private set; }

        public Task<LufeLookupResult> GetLufeDataAsync(string tenantId, long cuit, string apiKey, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(new LufeLookupResult(LufeConsultaEstado.LookupFailed, null));
        }

        public Task<string?> GetMipymeCategoriaAsync(string tenantId, long cuit, string apiKey, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult<string?>(null);
        }

        public Task<LufeDocumentDownload?> DownloadDocumentAsync(string absoluteUrl, string apiKey, CancellationToken ct)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Answers with <see cref="Result"/>, re-stamped with the CUIT it was actually asked for (the
    /// hyphen-less form the controller sends), the way getPersona_v2 echoes idPersona.
    /// </summary>
    private sealed class StubArcaConstanciaService : IArcaConstanciaService
    {
        public ArcaPersonaData? Result { get; set; }
        public List<string> ReceivedCuits { get; } = [];

        public int CallCount => ReceivedCuits.Count;

        public Task<ArcaPersonaData?> GetPersonaByCuitAsync(
            string cuit, string cuitRepresentada, CancellationToken cancellationToken = default)
        {
            ReceivedCuits.Add(cuit);
            return Task.FromResult<ArcaPersonaData?>(Result is null ? null : Result with { Cuit = cuit });
        }
    }

    /// <summary>
    /// Captures every entry the controller logs. Warnings and Errors as in
    /// EntitiesControllerEntitlementGateTests; <see cref="Messages"/> as well, so a persona humana's
    /// CUIL — personal data — can be proven absent from every line the create writes.
    /// </summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];
        public List<string> Errors   { get; } = [];
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var text = formatter(state, exception);
            Messages.Add(text);
            if (logLevel == LogLevel.Warning) Warnings.Add(text);
            if (logLevel >= LogLevel.Error)   Errors.Add(text);
        }
    }

    // ── Race doubles ──────────────────────────────────────────────────────────

    /// <summary>
    /// The real <see cref="PersonLegajoRepository"/>, except that the FIRST
    /// <see cref="SaveEntityWithLegajoAsync"/> loses a race: it runs <see cref="OnLostRace"/> (the
    /// other request's committed write, if any) and throws <see cref="DuplicateCuitException"/> with
    /// the given scope without touching the context — exactly what the real repository leaves behind
    /// after it detaches the failed entries.
    /// </summary>
    private sealed class RacingPersonLegajoRepository : IPersonLegajoRepository
    {
        private readonly IPersonLegajoRepository _inner;
        private readonly DuplicateCuitScope _lostRaceScope;

        public RacingPersonLegajoRepository(IPersonLegajoRepository inner, DuplicateCuitScope lostRaceScope)
        {
            _inner         = inner;
            _lostRaceScope = lostRaceScope;
        }

        public Func<Task>? OnLostRace { get; set; }
        public int GetByCuitCalls { get; private set; }
        public List<(bool EntityIsNew, bool LegajoIsNew)> SaveCalls { get; } = [];

        public Task<PersonLegajo?> GetByCuitAsync(string cuit, string tenantId, CancellationToken ct)
        {
            GetByCuitCalls++;
            return _inner.GetByCuitAsync(cuit, tenantId, ct);
        }

        public async Task SaveEntityWithLegajoAsync(
            Entity entity, bool entityIsNew, PersonLegajo legajo, bool legajoIsNew, CancellationToken ct)
        {
            SaveCalls.Add((entityIsNew, legajoIsNew));

            if (SaveCalls.Count == 1)
            {
                if (OnLostRace is not null) await OnLostRace();
                throw new DuplicateCuitException(_lostRaceScope);
            }

            await _inner.SaveEntityWithLegajoAsync(entity, entityIsNew, legajo, legajoIsNew, ct);
        }

        public Task<PersonLegajo?> GetByIdAsync(Guid personLegajoId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<string?> GetMonotributoCategoryAsync(Guid personLegajoId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task SaveFiadorLegajoLinkAsync(Fiador fiador, PersonLegajo legajo, bool legajoIsNew, CancellationToken ct)
            => throw new NotImplementedException();
        public Task SaveLegajoSnapshotsAsync(PersonLegajo legajo, CancellationToken ct)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// The real <see cref="EntityRepository"/> for the pre-check read and the enrichment updates, but
    /// a <see cref="CreateAsync"/> that loses the race on UQ_Entities_TenantId_CUIT.
    /// </summary>
    private sealed class RacingEntityRepository : IEntityRepository
    {
        private readonly IEntityRepository _inner;

        public RacingEntityRepository(IEntityRepository inner) => _inner = inner;

        public int CreateCalls { get; private set; }

        public Task<Entity?> GetByCuitAsync(string cuit, string tenantId, CancellationToken ct)
            => _inner.GetByCuitAsync(cuit, tenantId, ct);

        public Task<Entity> CreateAsync(Entity entity, CancellationToken ct)
        {
            CreateCalls++;
            throw new DuplicateCuitException(DuplicateCuitScope.Entity);
        }

        public Task<Entity> UpdateAsync(Entity entity, CancellationToken ct)
            => _inner.UpdateAsync(entity, ct);

        public Task<Entity?> GetByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<bool> ExistsByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<Entity>> GetAllByTenantAsync(string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<(IReadOnlyList<EntityListProjection> Items, int TotalCount)> GetListPageByTenantAsync(
            string tenantId, int skip, int take, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, EntityNameProjection>> GetNameLookupByIdsAsync(
            IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<EntityNameProjection?> GetNameByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<EntityDetailProjection?> GetDetailByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<EntitySnapshotProjection?> GetBcraSnapshotAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<EntitySnapshotProjection?> GetCasfogSnapshotAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<EntitySnapshotProjection?> GetNosisSnapshotAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<EntitySnapshotProjection?> GetLufeSnapshotAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Fails the FIRST SaveChanges of the controller's context the way SQL Server does on a unique
    /// index — a <see cref="DbUpdateException"/> whose inner message names the index and, like the
    /// real message, carries the duplicate key. Lets the REAL repositories classify the index.
    /// </summary>
    private sealed class UniqueViolationInterceptor : SaveChangesInterceptor
    {
        private readonly string _indexName;

        public UniqueViolationInterceptor(string indexName) => _indexName = indexName;

        public int Throws { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Throws == 0)
            {
                Throws++;
                throw new DbUpdateException(
                    "An error occurred while saving the entity changes. See the inner exception for details.",
                    new InvalidOperationException(
                        $"Cannot insert duplicate key row in object 'dbo.X' with unique index '{_indexName}'. " +
                        $"The duplicate key value is ({Tenant}, {HumanaCuit})."));
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    // ── Harness ───────────────────────────────────────────────────────────────
    private sealed record Harness(
        EntitiesController                  Sut,
        string                              DatabaseName,
        StubUserRepository                  Users,
        StubBcraService                     Bcra,
        StubCasfogService                   Casfog,
        StubNosisService                    Nosis,
        StubLufeService                     Lufe,
        StubArcaConstanciaService           Arca,
        RecordingLogger<EntitiesController> Logger,
        RacingPersonLegajoRepository?       LegajoRace,
        RacingEntityRepository?             EntityRace);

    private static FinanciaDbContext NewContext(string databaseName, IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase(databaseName, DatabaseRoot);

        if (interceptor is not null)
        {
            builder.AddInterceptors(interceptor);
        }

        return new FinanciaDbContext(builder.Options);
    }

    /// <param name="legajoSaveLosesRaceAs">Wraps the legajo repository so its first save loses with this scope.</param>
    /// <param name="entityCreateLosesRace">Wraps the entity repository so CreateAsync loses the race.</param>
    /// <param name="failingIndexName">Makes the controller context's first SaveChanges fail on this index.</param>
    private static Harness BuildHarness(
        DuplicateCuitScope? legajoSaveLosesRaceAs = null,
        bool entityCreateLosesRace = false,
        string? failingIndexName = null)
    {
        var databaseName = $"persona-humana-create-{Guid.NewGuid()}";
        var interceptor  = failingIndexName is null ? null : new UniqueViolationInterceptor(failingIndexName);

        // ONE context behind both real repositories, exactly as the request scope wires them.
        var dbContext = NewContext(databaseName, interceptor);

        IEntityRepository entityRepository =
            new EntityRepository(dbContext, NullLogger<EntityRepository>.Instance);
        IPersonLegajoRepository legajoRepository =
            new PersonLegajoRepository(dbContext, NullLogger<PersonLegajoRepository>.Instance);

        RacingEntityRepository? entityRace = null;
        if (entityCreateLosesRace)
        {
            entityRace       = new RacingEntityRepository(entityRepository);
            entityRepository = entityRace;
        }

        RacingPersonLegajoRepository? legajoRace = null;
        if (legajoSaveLosesRaceAs is { } scope)
        {
            legajoRace       = new RacingPersonLegajoRepository(legajoRepository, scope);
            legajoRepository = legajoRace;
        }

        var users  = new StubUserRepository();
        var bcra   = new StubBcraService();
        var casfog = new StubCasfogService();
        var nosis  = new StubNosisService();
        var lufe   = new StubLufeService();
        var arca   = new StubArcaConstanciaService();
        var logger = new RecordingLogger<EntitiesController>();

        // No LUFE key (the LUFE block skips, as in EntitiesControllerCreateTests); NOSIS fully
        // configured so that nothing but the code holds an eager lookup back; the tenant CUIT the
        // ARCA lookup sends as cuitRepresentada.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"Tenants:{Tenant}:Cuit"] = "30-99999999-7",
                ["Nosis:Enabled"]          = "true",
                ["Nosis:ApiKey"]           = "test-key",
                ["Nosis:VR"]               = "42"
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
            entityRepository,               // IEntityRepository — real (or racing)
            null!,                          // IArcaPadronService
            arca,                           // IArcaConstanciaService
            null!,                          // IArcaWsaaService
            new StubApiCallLogService(),    // IApiCallLogService
            new StubAuditService(),         // IAuditService
            lufe,                           // ILufeService
            bcra,                           // IBcraService
            casfog,                         // ICasfogService
            nosis,                          // INosisService
            new IntegrationAccessService(new StubTenantProfileService(), config, NullLogger<IntegrationAccessService>.Instance), // all entitled
            null!,                          // IDocumentRepository
            null!,                          // IDocumentStorageService
            users,                          // IUserRepository
            null!,                          // ICreditLineRepository
            null!,                          // IAnalysisRepository
            null!,                          // INoteRepository
            legajoRepository,               // IPersonLegajoRepository — real (or racing)
            null!,                          // INosisReportRepository
            null!,                          // INosisManagerReportParser
            null!,                          // KeyedAsyncLock
            config,                         // IConfiguration
            logger);

        // CreateAsync reads the Firebase uid off the principal and 401s without it.
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("uid", Uid) }, "Test"));
        sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };

        return new Harness(sut, databaseName, users, bcra, casfog, nosis, lufe, arca, logger, legajoRace, entityRace);
    }

    // ── Data helpers ──────────────────────────────────────────────────────────

    /// <summary>Writes rows through a SEPARATE context — another request, or an earlier one.</summary>
    private static async Task SeedAsync(Harness h, params object[] rows)
    {
        await using var ctx = NewContext(h.DatabaseName);
        ctx.AddRange(rows);
        await ctx.SaveChangesAsync();
    }

    private static async Task<List<PersonLegajo>> AllLegajosAsync(Harness h)
    {
        await using var ctx = NewContext(h.DatabaseName);
        return await ctx.PersonLegajos.AsNoTracking().ToListAsync();
    }

    private static async Task<List<Entity>> AllEntitiesAsync(Harness h)
    {
        await using var ctx = NewContext(h.DatabaseName);
        return await ctx.Entities.AsNoTracking().ToListAsync();
    }

    private static async Task<Entity> SingleEntityAsync(Harness h, string tenantId, string cuit)
        => Assert.Single(await AllEntitiesAsync(h), e => e.TenantId == tenantId && e.CUIT == cuit);

    private static async Task<PersonLegajo> SingleLegajoAsync(Harness h, string tenantId, string cuit)
        => Assert.Single(await AllLegajosAsync(h), l => l.TenantId == tenantId && l.CUIT == cuit);

    private static PersonLegajo SeedLegajo(
        string tenantId, string cuit, string fiscalRegime, string? monotributoCategory) => new()
    {
        PersonLegajoId      = Guid.NewGuid(),
        TenantId            = tenantId,
        CUIT                = cuit,
        FullName            = "PERSONA DE PRUEBA",
        FiscalRegime        = fiscalRegime,
        MonotributoCategory = monotributoCategory,
        ArcaSnapshotJson    = SeededSnapshot,
        ArcaDataUpdatedAt   = Seeded,
        CreatedBy           = null,   // as the DDL 069 backfill writes it
        CreatedAt           = Seeded,
        UpdatedAt           = Seeded
    };

    private static Entity SeedEntity(string tenantId, string cuit, string personType, Guid? personLegajoId = null) => new()
    {
        EntityId       = Guid.NewGuid(),
        TenantId       = tenantId,
        CUIT           = cuit,
        BusinessName   = personType == PersonTypes.Humana ? "PERSONA DE PRUEBA" : "EMPRESA DE PRUEBA SA",
        PersonType     = personType,
        PersonLegajoId = personLegajoId,
        CreatedAt      = Seeded,
        UpdatedAt      = Seeded
    };

    // ── Request helpers ───────────────────────────────────────────────────────

    private static ArcaFiscalData Fiscal(string? fiscalRegime, string? monotributoCategoria = null) => new(
        FechaContratoSocial:  null,
        EsSucesion:           false,
        MesCierre:            null,
        Actividades:          [],
        Impuestos:            [],
        Regimenes:            [],
        FiscalRegime:         fiscalRegime,
        MonotributoCategoria: monotributoCategoria);

    private static CreateEntityRequest CreateRequest(
        string cuit = HumanaCuit,
        ArcaFiscalData? fiscal = null,
        string? personType = null,
        string businessName = "  PERSONA DE PRUEBA  ") => new()
    {
        CUIT         = cuit,
        BusinessName = businessName,
        IsMonitored  = true,
        FiscalData   = fiscal,
        PersonType   = personType
    };

    private static ArcaPersonaData Persona(
        string? tipoPersona,
        string? apellido,
        string? nombre,
        string? razonSocial,
        ArcaFiscalData? fiscal = null) => new(
        Cuit:                          "overwritten-by-the-stub",
        TipoPersona:                   tipoPersona,
        EstadoClave:                   "ACTIVO",
        Nombre:                        nombre,
        Apellido:                      apellido,
        RazonSocial:                   razonSocial,
        TipoDocumento:                 null,
        NumeroDocumento:               null,
        FechaNacimiento:               null,
        MesCierre:                     12,
        IdActividadPrincipal:          "620100",
        DescripcionActividadPrincipal: null,
        PeriodoActividadPrincipal:     null,
        FormaJuridica:                 null,
        DomicilioFiscal:               null,
        DomicilioLegal:                null,
        FiscalData:                    fiscal);

    // ── Assertion helpers ─────────────────────────────────────────────────────

    private static EntityResponse AssertCreated(IActionResult result)
    {
        var created = Assert.IsType<CreatedAtActionResult>(result);
        Assert.Equal(201, created.StatusCode);
        return Assert.IsType<EntityResponse>(created.Value);
    }

    private static void AssertCuitConflict(IActionResult result, string cuit)
    {
        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(409, obj.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(obj.Value);
        Assert.Equal($"Ya existe una entidad con el CUIT '{cuit}' para este tenant.", problem.Detail);
    }

    private static ArcaLookupResponse AssertLookupOk(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<ArcaLookupResponse>(ok.Value);
    }

    private static void AssertNothingAlarming(Harness h)
    {
        Assert.Empty(h.Logger.Warnings);
        Assert.Empty(h.Logger.Errors);
    }

    /// <summary>The CUIL of a persona humana is personal data (D8): never in a log line, in either spelling.</summary>
    private static void AssertNeverLogged(Harness h, string cuit)
    {
        var digits = cuit.Replace("-", string.Empty);
        Assert.DoesNotContain(h.Logger.Messages, m => m.Contains(cuit, StringComparison.Ordinal));
        Assert.DoesNotContain(h.Logger.Messages, m => m.Contains(digits, StringComparison.Ordinal));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Create — a persona humana gets a NEW legajo
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Create_Humana_CreatesAndLinksALegajo_WithTheEntitysHyphenatedCuit()
    {
        var h      = BuildHarness();
        var before = DateTime.UtcNow;

        var result = await h.Sut.CreateAsync(
            CreateRequest(fiscal: Fiscal(FiscalRegimes.Monotributo, "h")), CancellationToken.None);

        var response = AssertCreated(result);

        var entity = await SingleEntityAsync(h, Tenant, HumanaCuit);
        Assert.Equal(PersonTypes.Humana, entity.PersonType);
        Assert.Equal(FiscalRegimes.Monotributo, entity.FiscalRegime);
        Assert.Equal("PERSONA DE PRUEBA", entity.BusinessName);
        Assert.NotNull(entity.FiscalDataJson);

        // Exactly one legajo in the whole store, keyed by the SAME hyphenated CUIT as Entities.CUIT —
        // a hyphen-less copy would be a second person to UX_PersonLegajos_TenantId_CUIT.
        var legajo = Assert.Single(await AllLegajosAsync(h));
        Assert.Equal(Tenant, legajo.TenantId);
        Assert.Equal(HumanaCuit, legajo.CUIT);
        Assert.Equal("PERSONA DE PRUEBA", legajo.FullName);                  // trimmed BusinessName
        Assert.Equal(FiscalRegimes.Monotributo, legajo.FiscalRegime);
        Assert.Equal("H", legajo.MonotributoCategory);                       // "h" normalised
        Assert.Equal(entity.FiscalDataJson, legajo.ArcaSnapshotJson);        // the same serialised string
        Assert.NotNull(legajo.ArcaDataUpdatedAt);
        Assert.InRange(legajo.ArcaDataUpdatedAt!.Value, before, DateTime.UtcNow);
        Assert.Equal(h.Users.UserId, legajo.CreatedBy);
        Assert.Null(legajo.NosisDataJson);                                   // NOSIS is never eager
        Assert.Equal(legajo.PersonLegajoId, entity.PersonLegajoId);

        Assert.Equal(entity.EntityId, response.EntityId);
        Assert.Equal(PersonTypes.Humana, response.PersonType);
        Assert.Equal(FiscalRegimes.Monotributo, response.FiscalRegime);
        Assert.Equal("H", response.MonotributoCategory);
        Assert.Equal(legajo.PersonLegajoId, response.PersonLegajoId);

        AssertNothingAlarming(h);
    }

    [Fact]
    public async Task Create_Humana_WithoutFiscalData_StoresDesconocido_AndNoArcaSnapshot()
    {
        var h = BuildHarness();

        var response = AssertCreated(await h.Sut.CreateAsync(CreateRequest(fiscal: null), CancellationToken.None));

        var entity = await SingleEntityAsync(h, Tenant, HumanaCuit);
        var legajo = await SingleLegajoAsync(h, Tenant, HumanaCuit);

        // The legajo column is NOT NULL, so "undetermined" is Desconocido on both rows, never null.
        Assert.Equal(FiscalRegimes.Desconocido, entity.FiscalRegime);
        Assert.Equal(FiscalRegimes.Desconocido, legajo.FiscalRegime);
        Assert.Null(entity.FiscalDataJson);
        Assert.Null(legajo.MonotributoCategory);
        Assert.Null(legajo.ArcaSnapshotJson);
        Assert.Null(legajo.ArcaDataUpdatedAt);
        Assert.Equal(legajo.PersonLegajoId, entity.PersonLegajoId);

        Assert.Equal(FiscalRegimes.Desconocido, response.FiscalRegime);
        Assert.Null(response.MonotributoCategory);
        Assert.Equal(legajo.PersonLegajoId, response.PersonLegajoId);
    }

    [Fact]
    public async Task Create_Humana_WithAnUnknownRegime_StoresDesconocido_ButKeepsTheSnapshot()
    {
        // A regime outside the CHECK list would fail CK_*_FiscalRegime; it degrades to Desconocido.
        var h = BuildHarness();

        AssertCreated(await h.Sut.CreateAsync(
            CreateRequest(fiscal: Fiscal("RegimenInventado", "b")), CancellationToken.None));

        var entity = await SingleEntityAsync(h, Tenant, HumanaCuit);
        var legajo = await SingleLegajoAsync(h, Tenant, HumanaCuit);
        Assert.Equal(FiscalRegimes.Desconocido, entity.FiscalRegime);
        Assert.Equal(FiscalRegimes.Desconocido, legajo.FiscalRegime);
        Assert.Equal(entity.FiscalDataJson, legajo.ArcaSnapshotJson);
        Assert.NotNull(legajo.ArcaDataUpdatedAt);
    }

    /// <summary>
    /// A new legajo keeps a category only when it is a valid letter A–K AND the regime does not rule
    /// it out: a known non-monotributo regime clears it, while Desconocido — monotributo not ruled
    /// out — keeps it. The response reads the same value off the legajo.
    /// </summary>
    [Theory]
    [InlineData(FiscalRegimes.Monotributo,          "k",  "K")]
    [InlineData(FiscalRegimes.Monotributo,          "Z",  null)]   // outside A–K
    [InlineData(FiscalRegimes.Monotributo,          "AB", null)]   // not a single letter
    [InlineData(FiscalRegimes.ResponsableInscripto, "b",  null)]
    [InlineData(FiscalRegimes.Exento,               "b",  null)]
    [InlineData(FiscalRegimes.Desconocido,          "b",  "B")]
    public async Task Create_Humana_NewLegajo_KeepsOnlyAValidCategoryTheRegimeAllows(
        string sentRegime, string sentCategory, string? expectedCategory)
    {
        var h = BuildHarness();

        var response = AssertCreated(await h.Sut.CreateAsync(
            CreateRequest(fiscal: Fiscal(sentRegime, sentCategory)), CancellationToken.None));

        var entity = await SingleEntityAsync(h, Tenant, HumanaCuit);
        var legajo = await SingleLegajoAsync(h, Tenant, HumanaCuit);
        Assert.Equal(sentRegime, legajo.FiscalRegime);
        Assert.Equal(sentRegime, entity.FiscalRegime);
        Assert.Equal(expectedCategory, legajo.MonotributoCategory);
        Assert.Equal(expectedCategory, response.MonotributoCategory);
    }

    [Fact]
    public async Task Create_Humana_NeverLogsTheCuil()
    {
        // D8: for a persona humana the CUIT is a CUIL — the "Entity created" line drops it.
        var h = BuildHarness();

        AssertCreated(await h.Sut.CreateAsync(
            CreateRequest(fiscal: Fiscal(FiscalRegimes.Monotributo, "h")), CancellationToken.None));

        Assert.NotEmpty(h.Logger.Messages);
        AssertNeverLogged(h, HumanaCuit);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Create — the tenant ALREADY has the person's legajo
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A second entity for a person the tenant already knows — typically without fresh ARCA data.
    /// "Informative" excludes Desconocido, so the legajo's Monotributo/'C' must survive, and the
    /// ENTITY takes the legajo's regime so the two rows describing one person agree.
    /// </summary>
    [Theory]
    [InlineData(false, null)]                          // no fiscal data at all
    [InlineData(true,  null)]                          // an older Portal: fiscal data without a regime
    [InlineData(true,  FiscalRegimes.Desconocido)]     // ARCA could not determine it
    [InlineData(true,  "RegimenInventado")]            // unknown → Desconocido → still no downgrade
    public async Task Create_Humana_WithAnExistingLegajo_LinksIt_AndAnUninformativeRegimeNeverDowngradesIt(
        bool sendsFiscalData, string? sentRegime)
    {
        var h        = BuildHarness();
        var existing = SeedLegajo(Tenant, HumanaCuit, FiscalRegimes.Monotributo, "C");
        await SeedAsync(h, existing);

        var fiscal   = sendsFiscalData ? Fiscal(sentRegime) : null;
        var response = AssertCreated(await h.Sut.CreateAsync(
            CreateRequest(fiscal: fiscal, businessName: "OTRO NOMBRE DE PRUEBA"), CancellationToken.None));

        var legajo = Assert.Single(await AllLegajosAsync(h));                // linked, not duplicated
        var entity = await SingleEntityAsync(h, Tenant, HumanaCuit);

        Assert.Equal(existing.PersonLegajoId, legajo.PersonLegajoId);
        Assert.Equal(existing.PersonLegajoId, entity.PersonLegajoId);
        Assert.Equal(FiscalRegimes.Monotributo, legajo.FiscalRegime);        // no downgrade
        Assert.Equal("C", legajo.MonotributoCategory);
        Assert.Equal(FiscalRegimes.Monotributo, entity.FiscalRegime);        // entity agrees with legajo
        Assert.Equal("PERSONA DE PRUEBA", legajo.FullName);                  // the person's, left alone
        Assert.Null(legajo.CreatedBy);                                       // authorship not rewritten

        if (sendsFiscalData)
        {
            Assert.Equal(entity.FiscalDataJson, legajo.ArcaSnapshotJson);    // a snapshot IS informative
            Assert.NotEqual(Seeded, legajo.ArcaDataUpdatedAt);
        }
        else
        {
            Assert.Equal(SeededSnapshot, legajo.ArcaSnapshotJson);
            Assert.Equal(Seeded, legajo.ArcaDataUpdatedAt);
        }

        Assert.Equal(FiscalRegimes.Monotributo, response.FiscalRegime);
        Assert.Equal("C", response.MonotributoCategory);
        Assert.Equal(existing.PersonLegajoId, response.PersonLegajoId);
        AssertNothingAlarming(h);
    }

    /// <summary>
    /// The informative half of the merge: a known regime replaces the legajo's, a sent category is
    /// taken, and a known regime other than Monotributo clears the category — a category belongs to
    /// a monotributista only. The entity ends on the legajo's regime in every row.
    /// </summary>
    [Theory]
    [InlineData(FiscalRegimes.Monotributo,  "C",  FiscalRegimes.ResponsableInscripto, null, FiscalRegimes.ResponsableInscripto, null)]
    [InlineData(FiscalRegimes.Monotributo,  "C",  FiscalRegimes.Exento,               "d",  FiscalRegimes.Exento,               null)]
    [InlineData(FiscalRegimes.Monotributo,  "C",  FiscalRegimes.Monotributo,          "e",  FiscalRegimes.Monotributo,          "E")]
    [InlineData(FiscalRegimes.Monotributo,  "C",  FiscalRegimes.Monotributo,          null, FiscalRegimes.Monotributo,          "C")]
    [InlineData(FiscalRegimes.Desconocido,  null, FiscalRegimes.Monotributo,          "h",  FiscalRegimes.Monotributo,          "H")]
    public async Task Create_Humana_WithAnExistingLegajo_MergesAnInformativeRegimeAndCategory(
        string seededRegime,
        string? seededCategory,
        string sentRegime,
        string? sentCategory,
        string expectedRegime,
        string? expectedCategory)
    {
        var h        = BuildHarness();
        var existing = SeedLegajo(Tenant, HumanaCuit, seededRegime, seededCategory);
        await SeedAsync(h, existing);

        var response = AssertCreated(await h.Sut.CreateAsync(
            CreateRequest(fiscal: Fiscal(sentRegime, sentCategory)), CancellationToken.None));

        var legajo = Assert.Single(await AllLegajosAsync(h));
        var entity = await SingleEntityAsync(h, Tenant, HumanaCuit);

        Assert.Equal(existing.PersonLegajoId, legajo.PersonLegajoId);
        Assert.Equal(expectedRegime, legajo.FiscalRegime);
        Assert.Equal(expectedCategory, legajo.MonotributoCategory);
        Assert.Equal(expectedRegime, entity.FiscalRegime);
        Assert.Equal(existing.PersonLegajoId, entity.PersonLegajoId);

        Assert.Equal(expectedRegime, response.FiscalRegime);
        Assert.Equal(expectedCategory, response.MonotributoCategory);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Create — ARCA's activity start and key status (M10, persona humana ARCA gaps)
    // ═══════════════════════════════════════════════════════════════════════════

    private static ArcaFiscalData FiscalWithPeriods(params string[] impuestoPeriodos)
        => Fiscal(FiscalRegimes.Monotributo, "C") with
        {
            Impuestos               = impuestoPeriodos.Select(p => new ArcaImpuesto("20", "MONOTRIBUTO", "AC", null, p)).ToList(),
            EstadoClave             = "ACTIVO",
            EstadoClaveConsultadoAt = Seeded
        };

    [Fact]
    public async Task Create_Humana_NewLegajo_TakesTheActivityStartFromTheEchoedSnapshot()
    {
        var h = BuildHarness();

        AssertCreated(await h.Sut.CreateAsync(
            CreateRequest(fiscal: FiscalWithPeriods("201905", "201712")), CancellationToken.None));

        var legajo = Assert.Single(await AllLegajosAsync(h));
        Assert.Equal(new DateTime(2017, 12, 1), legajo.ActivityStartDate);

        // The key status rides in the snapshot exactly as lookup-arca stamped it.
        var snapshot = System.Text.Json.JsonSerializer.Deserialize<ArcaFiscalData>(legajo.ArcaSnapshotJson!)!;
        Assert.Equal("ACTIVO", snapshot.EstadoClave);
        Assert.Equal(Seeded, snapshot.EstadoClaveConsultadoAt);
        AssertNothingAlarming(h);
    }

    [Fact]
    public async Task Create_Humana_WithoutPeriods_LeavesTheActivityStartEmpty()
    {
        var h = BuildHarness();

        AssertCreated(await h.Sut.CreateAsync(CreateRequest(fiscal: FiscalWithPeriods()), CancellationToken.None));

        Assert.Null(Assert.Single(await AllLegajosAsync(h)).ActivityStartDate);
    }

    [Fact]
    public async Task Create_Humana_WithAnExistingLegajo_TakesTheEchoedActivityStart()
    {
        var h        = BuildHarness();
        var existing = SeedLegajo(Tenant, HumanaCuit, FiscalRegimes.Monotributo, "C");
        existing.ActivityStartDate = new DateTime(2020, 1, 1);
        await SeedAsync(h, existing);

        AssertCreated(await h.Sut.CreateAsync(
            CreateRequest(fiscal: FiscalWithPeriods("201603")), CancellationToken.None));

        Assert.Equal(new DateTime(2016, 3, 1), Assert.Single(await AllLegajosAsync(h)).ActivityStartDate);
    }

    [Fact]
    public async Task Create_Humana_WithAnExistingLegajo_AndNoSnapshot_KeepsItsActivityStart()
    {
        var h        = BuildHarness();
        var existing = SeedLegajo(Tenant, HumanaCuit, FiscalRegimes.Monotributo, "C");
        existing.ActivityStartDate = new DateTime(2020, 1, 1);
        await SeedAsync(h, existing);

        AssertCreated(await h.Sut.CreateAsync(CreateRequest(fiscal: null), CancellationToken.None));

        Assert.Equal(new DateTime(2020, 1, 1), Assert.Single(await AllLegajosAsync(h)).ActivityStartDate);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Create — BCRA: one call, the same snapshot on entity and legajo
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Create_Humana_CopiesTheOneBcraSnapshotOntoTheLegajo_WithoutASecondCall()
    {
        var h      = BuildHarness();
        var before = DateTime.UtcNow;

        AssertCreated(await h.Sut.CreateAsync(
            CreateRequest(fiscal: Fiscal(FiscalRegimes.Monotributo, "h")), CancellationToken.None));

        // Queried ONCE, by the CUIL as the entity stores it.
        Assert.Equal(new[] { HumanaCuit }, h.Bcra.QueriedCuits);

        var entity = await SingleEntityAsync(h, Tenant, HumanaCuit);
        var legajo = await SingleLegajoAsync(h, Tenant, HumanaCuit);

        Assert.False(string.IsNullOrEmpty(entity.BcraDataJson));
        Assert.Contains("\"deuda\"", entity.BcraDataJson);
        Assert.Equal(entity.BcraDataJson, legajo.BcraDataJson);
        Assert.NotNull(entity.BcraDataUpdatedAt);
        Assert.InRange(entity.BcraDataUpdatedAt!.Value, before, DateTime.UtcNow);
        Assert.Equal(entity.BcraDataUpdatedAt, legajo.BcraDataUpdatedAt);

        // The rest of the fan-out is the Juridica one: CASFOG eager, ARCA and NOSIS never, LUFE keyless.
        Assert.Equal(1, h.Casfog.CallCount);
        Assert.False(string.IsNullOrEmpty(entity.CasfogDataJson));
        Assert.Equal(0, h.Nosis.CallCount);
        Assert.Equal(0, h.Arca.CallCount);
        Assert.Equal(0, h.Lufe.CallCount);
        Assert.Null(entity.NosisDataJson);
        AssertNothingAlarming(h);
    }

    // D1 (score credibility 1b-1): the new entity has nothing to lose, but a legajo that predates it
    // (a fiador's, say) does — it is never overwritten with a failed Central de Deudores.

    private static string StoredLegajoBcraJson() => System.Text.Json.JsonSerializer.Serialize(new BcraData
    {
        Deuda   = new BcraDeudaData { Estado = BcraDeudaConsultaEstado.DebtFound, SituacionMaxima = 1, MontoTotal = 555m },
        Cheques = new BcraChequesData { Estado = BcraChequesConsultaEstado.ChequesFound, MontoTotalRechazado = 900m }
    });

    [Fact]
    public async Task Create_Humana_WithAnExistingLegajo_DeudaFailed_TheLegajoKeepsItsStoredBcra()
    {
        var h        = BuildHarness();
        var stored   = StoredLegajoBcraJson();
        var existing = SeedLegajo(Tenant, HumanaCuit, FiscalRegimes.Monotributo, "C");
        existing.BcraDataJson      = stored;
        existing.BcraDataUpdatedAt = Seeded;
        await SeedAsync(h, existing);
        h.Bcra.Result = new BcraData
        {
            Deuda = new BcraDeudaData { Estado = BcraDeudaConsultaEstado.LookupFailed },
            Macro = new BcraMacroData { InflacionMensual = 2.0 }
        };

        AssertCreated(await h.Sut.CreateAsync(
            CreateRequest(fiscal: Fiscal(FiscalRegimes.Monotributo, "C")), CancellationToken.None));

        var entity = await SingleEntityAsync(h, Tenant, HumanaCuit);
        var legajo = await SingleLegajoAsync(h, Tenant, HumanaCuit);

        // The entity stores what came back (as before); the legajo keeps the person's good snapshot.
        Assert.Equal(BcraDeudaConsultaEstado.LookupFailed, PersonSnapshotWriter.ReadBcra(entity.BcraDataJson)!.Deuda!.Estado);
        Assert.Equal(stored, legajo.BcraDataJson);
        Assert.Equal(Seeded, legajo.BcraDataUpdatedAt);
    }

    [Fact]
    public async Task Create_Humana_WithAnExistingLegajo_ChequesFailed_KeepsTheStoredCheques()
    {
        var h        = BuildHarness();
        var existing = SeedLegajo(Tenant, HumanaCuit, FiscalRegimes.Monotributo, "C");
        existing.BcraDataJson      = StoredLegajoBcraJson();
        existing.BcraDataUpdatedAt = Seeded;
        await SeedAsync(h, existing);
        h.Bcra.Result = new BcraData
        {
            Deuda   = new BcraDeudaData { Estado = BcraDeudaConsultaEstado.NoDebtReported },
            Cheques = new BcraChequesData { Estado = BcraChequesConsultaEstado.LookupFailed }
        };

        AssertCreated(await h.Sut.CreateAsync(
            CreateRequest(fiscal: Fiscal(FiscalRegimes.Monotributo, "C")), CancellationToken.None));

        var entity = await SingleEntityAsync(h, Tenant, HumanaCuit);
        var legajo = await SingleLegajoAsync(h, Tenant, HumanaCuit);
        var saved  = PersonSnapshotWriter.ReadBcra(legajo.BcraDataJson)!;

        Assert.Equal(BcraDeudaConsultaEstado.NoDebtReported, saved.Deuda!.Estado);     // fresh deuda
        Assert.Equal(BcraChequesConsultaEstado.ChequesFound, saved.Cheques!.Estado);  // stored cheques
        Assert.Equal(entity.BcraDataJson, legajo.BcraDataJson);
        Assert.True(legajo.BcraDataUpdatedAt > Seeded);
    }

    [Fact]
    public async Task Create_Humana_WhenBcraThrows_StillCreatesTheEntityAndItsLegajo()
    {
        var h = BuildHarness();
        h.Bcra.ThrowOnCall = new HttpRequestException("connection reset by peer");

        var response = AssertCreated(await h.Sut.CreateAsync(CreateRequest(), CancellationToken.None));

        var entity = await SingleEntityAsync(h, Tenant, HumanaCuit);
        var legajo = await SingleLegajoAsync(h, Tenant, HumanaCuit);

        // The legajo is part of the entity, not an enrichment: it was written before BCRA ran.
        Assert.Equal(legajo.PersonLegajoId, entity.PersonLegajoId);
        Assert.Equal(legajo.PersonLegajoId, response.PersonLegajoId);
        Assert.Null(entity.BcraDataJson);
        Assert.Null(entity.BcraDataUpdatedAt);
        Assert.Null(legajo.BcraDataJson);
        Assert.Null(legajo.BcraDataUpdatedAt);

        Assert.Equal(1, h.Bcra.CallCount);
        Assert.Equal(1, h.Casfog.CallCount);                                 // the next block still runs
        var warning = Assert.Single(h.Logger.Warnings);
        Assert.Contains("BCRA enrichment failed", warning);
        AssertNeverLogged(h, HumanaCuit);
    }

    [Fact]
    public async Task Create_Humana_WhenBcraHasNoData_LeavesBothBcraSnapshotsEmpty()
    {
        var h = BuildHarness();
        h.Bcra.Result = null;

        AssertCreated(await h.Sut.CreateAsync(CreateRequest(), CancellationToken.None));

        var entity = await SingleEntityAsync(h, Tenant, HumanaCuit);
        var legajo = await SingleLegajoAsync(h, Tenant, HumanaCuit);
        Assert.Null(entity.BcraDataJson);
        Assert.Null(legajo.BcraDataJson);
        Assert.Null(legajo.BcraDataUpdatedAt);
        Assert.Equal(1, h.Bcra.CallCount);
        AssertNothingAlarming(h);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Create — the requested person type
    // ═══════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("Fisica")]
    [InlineData("X")]
    [InlineData("Humano")]
    public async Task Create_WithAnUnknownPersonType_Returns400OnPersonType_AndWritesNothing(string personType)
    {
        var h = BuildHarness();

        var result = await h.Sut.CreateAsync(CreateRequest(personType: personType), CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, obj.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(obj.Value);
        Assert.Equal("validation_error", problem.Extensions["errorCode"]);

        var errors = Assert.IsAssignableFrom<System.Collections.IEnumerable>(problem.Extensions["errors"]);
        var error  = Assert.Single(errors.Cast<object>());
        Assert.Equal("PersonType", error.GetType().GetProperty("field")!.GetValue(error));
        Assert.Equal(InvalidPersonTypeMessage, error.GetType().GetProperty("message")!.GetValue(error));

        Assert.Empty(await AllEntitiesAsync(h));
        Assert.Empty(await AllLegajosAsync(h));
        Assert.Equal(0, h.Bcra.CallCount);
        Assert.Equal(0, h.Casfog.CallCount);
    }

    [Theory]
    [InlineData("humana")]
    [InlineData("HUMANA")]
    [InlineData(" Humana ")]
    public async Task Create_WithAnAmbiguousPrefix_TakesTheRequestedHumanaInAnyCase_AndStoresItCanonical(string personType)
    {
        // 50- is not decided by the prefix, so the request breaks the tie — and nothing disagrees.
        var h = BuildHarness();

        var response = AssertCreated(await h.Sut.CreateAsync(
            CreateRequest(cuit: AmbiguousCuit, personType: personType), CancellationToken.None));

        var entity = await SingleEntityAsync(h, Tenant, AmbiguousCuit);
        var legajo = Assert.Single(await AllLegajosAsync(h));
        Assert.Equal(PersonTypes.Humana, entity.PersonType);                 // canonical spelling
        Assert.Equal(AmbiguousCuit, legajo.CUIT);
        Assert.Equal(legajo.PersonLegajoId, entity.PersonLegajoId);
        Assert.Equal(PersonTypes.Humana, response.PersonType);
        AssertNothingAlarming(h);
    }

    [Theory]
    [InlineData("juridica")]
    [InlineData("   ")]           // blank is "not sent", not a 400
    [InlineData(null)]
    public async Task Create_WithAnAmbiguousPrefix_AndNoHumanaRequested_IsJuridica_WithoutALegajo(string? personType)
    {
        var h = BuildHarness();

        var response = AssertCreated(await h.Sut.CreateAsync(
            CreateRequest(cuit: AmbiguousCuit, personType: personType, businessName: "EMPRESA DE PRUEBA SA"),
            CancellationToken.None));

        var entity = await SingleEntityAsync(h, Tenant, AmbiguousCuit);
        Assert.Equal(PersonTypes.Juridica, entity.PersonType);
        Assert.Null(entity.PersonLegajoId);
        Assert.Empty(await AllLegajosAsync(h));
        Assert.Equal(PersonTypes.Juridica, response.PersonType);
        AssertNothingAlarming(h);
    }

    /// <summary>
    /// An unambiguous prefix is authoritative: the requested type is advisory, a disagreement is
    /// logged ONCE, and the log line names the two types and never the CUIT.
    /// </summary>
    [Theory]
    [InlineData(JuridicaCuit, PersonTypes.Humana,   PersonTypes.Juridica)]
    [InlineData(HumanaCuit,   PersonTypes.Juridica, PersonTypes.Humana)]
    public async Task Create_WhenTheRequestedTypeContradictsTheCuitPrefix_ThePrefixWins_AndOneWarningIsLogged(
        string cuit, string requested, string expected)
    {
        var h = BuildHarness();

        var response = AssertCreated(await h.Sut.CreateAsync(
            CreateRequest(cuit: cuit, personType: requested), CancellationToken.None));

        var entity = await SingleEntityAsync(h, Tenant, cuit);
        Assert.Equal(expected, entity.PersonType);
        Assert.Equal(expected, response.PersonType);

        if (expected == PersonTypes.Humana)
        {
            var legajo = Assert.Single(await AllLegajosAsync(h));
            Assert.Equal(legajo.PersonLegajoId, entity.PersonLegajoId);
        }
        else
        {
            Assert.Null(entity.PersonLegajoId);
            Assert.Empty(await AllLegajosAsync(h));
        }

        var warning = Assert.Single(h.Logger.Warnings);
        Assert.Contains(requested, warning);
        Assert.Contains(expected, warning);
        Assert.DoesNotContain(cuit, warning);
        Assert.DoesNotContain(cuit.Replace("-", string.Empty), warning);
        Assert.Empty(h.Logger.Errors);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Create — a Juridica is unchanged
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Create_Juridica_WritesNoLegajo_AndKeepsTheFanOut()
    {
        var h = BuildHarness();

        var response = AssertCreated(await h.Sut.CreateAsync(
            CreateRequest(cuit: JuridicaCuit, businessName: "EMPRESA DE PRUEBA SA"), CancellationToken.None));

        var entity = await SingleEntityAsync(h, Tenant, JuridicaCuit);
        Assert.Equal(PersonTypes.Juridica, entity.PersonType);
        Assert.Null(entity.PersonLegajoId);
        Assert.Null(entity.FiscalRegime);                                    // no fiscal data → undetermined
        Assert.Empty(await AllLegajosAsync(h));

        Assert.Equal(1, h.Bcra.CallCount);
        Assert.Equal(1, h.Casfog.CallCount);
        Assert.Equal(0, h.Nosis.CallCount);
        Assert.Equal(0, h.Arca.CallCount);
        Assert.False(string.IsNullOrEmpty(entity.BcraDataJson));
        Assert.False(string.IsNullOrEmpty(entity.CasfogDataJson));
        Assert.Null(entity.NosisDataJson);

        Assert.Equal(PersonTypes.Juridica, response.PersonType);
        Assert.Null(response.FiscalRegime);
        Assert.Null(response.MonotributoCategory);
        Assert.Null(response.PersonLegajoId);
        AssertNothingAlarming(h);
    }

    [Theory]
    [InlineData(FiscalRegimes.ResponsableInscripto, FiscalRegimes.ResponsableInscripto)]
    [InlineData(FiscalRegimes.Exento,               FiscalRegimes.Exento)]
    [InlineData(null,                               null)]                       // older Portal: no regime
    [InlineData("RegimenInventado",                 FiscalRegimes.Desconocido)]
    public async Task Create_Juridica_StoresTheSentRegime_OrNullWithoutOne(string? sentRegime, string? expected)
    {
        var h = BuildHarness();

        var response = AssertCreated(await h.Sut.CreateAsync(
            CreateRequest(cuit: JuridicaCuit, fiscal: Fiscal(sentRegime), businessName: "EMPRESA DE PRUEBA SA"),
            CancellationToken.None));

        var entity = await SingleEntityAsync(h, Tenant, JuridicaCuit);
        Assert.Equal(expected, entity.FiscalRegime);
        Assert.Equal(expected, response.FiscalRegime);
        Assert.Null(entity.PersonLegajoId);
        Assert.Empty(await AllLegajosAsync(h));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Create — CUIT uniqueness is PER TENANT
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Create_Humana_WhenTheCuitExistsOnlyInAnotherTenant_Returns201_WithThisTenantsOwnLegajo()
    {
        // The business DB is shared: the other tenant's entity AND legajo for the same person are in
        // the same tables, and neither may be found, linked or touched from this tenant.
        var h           = BuildHarness();
        var otherLegajo = SeedLegajo(OtherTenant, HumanaCuit, FiscalRegimes.Monotributo, "C");
        var otherEntity = SeedEntity(OtherTenant, HumanaCuit, PersonTypes.Humana, otherLegajo.PersonLegajoId);
        await SeedAsync(h, otherLegajo, otherEntity);

        var response = AssertCreated(await h.Sut.CreateAsync(CreateRequest(fiscal: null), CancellationToken.None));

        var mine   = await SingleEntityAsync(h, Tenant, HumanaCuit);
        var legajo = await SingleLegajoAsync(h, Tenant, HumanaCuit);
        Assert.NotEqual(otherLegajo.PersonLegajoId, legajo.PersonLegajoId);
        Assert.Equal(legajo.PersonLegajoId, mine.PersonLegajoId);
        Assert.Equal(FiscalRegimes.Desconocido, legajo.FiscalRegime);        // nothing merged in from tenant-2
        Assert.Equal(legajo.PersonLegajoId, response.PersonLegajoId);

        var untouched = await SingleLegajoAsync(h, OtherTenant, HumanaCuit);
        Assert.Equal(FiscalRegimes.Monotributo, untouched.FiscalRegime);
        Assert.Equal("C", untouched.MonotributoCategory);
        Assert.Equal(Seeded, untouched.UpdatedAt);
        Assert.Equal(2, (await AllEntitiesAsync(h)).Count);
    }

    [Fact]
    public async Task Create_Juridica_WhenTheCuitExistsOnlyInAnotherTenant_Returns201()
    {
        var h = BuildHarness();
        await SeedAsync(h, SeedEntity(OtherTenant, JuridicaCuit, PersonTypes.Juridica));

        AssertCreated(await h.Sut.CreateAsync(
            CreateRequest(cuit: JuridicaCuit, businessName: "EMPRESA DE PRUEBA SA"), CancellationToken.None));

        Assert.Equal(PersonTypes.Juridica, (await SingleEntityAsync(h, Tenant, JuridicaCuit)).PersonType);
        Assert.Equal(2, (await AllEntitiesAsync(h)).Count);
    }

    [Fact]
    public async Task Create_WhenTheCuitAlreadyExistsInThisTenant_Returns409_AndWritesNothing()
    {
        var h = BuildHarness();
        await SeedAsync(h, SeedEntity(Tenant, HumanaCuit, PersonTypes.Humana));

        var result = await h.Sut.CreateAsync(
            CreateRequest(fiscal: Fiscal(FiscalRegimes.Monotributo, "h")), CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(409, obj.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(obj.Value);
        Assert.Equal("Ya existe una entidad con el CUIT '20-11111111-2' para este tenant.", problem.Detail);

        Assert.Single(await AllEntitiesAsync(h));
        Assert.Empty(await AllLegajosAsync(h));
        Assert.Equal(0, h.Bcra.CallCount);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Create — races the pre-check cannot close
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Another request creates the same person's legajo between this create's read and its write.
    /// The write loses on UX_PersonLegajos_TenantId_CUIT; the controller re-reads the winner, merges
    /// into it, links to it and retries ONCE — 201, one legajo, no 500.
    /// </summary>
    [Fact]
    public async Task Create_Humana_WhenAnotherRequestCreatesTheLegajoFirst_RelinksToTheWinnerAndRetriesOnce()
    {
        var h      = BuildHarness(legajoSaveLosesRaceAs: DuplicateCuitScope.PersonLegajo);
        var winner = SeedLegajo(Tenant, HumanaCuit, FiscalRegimes.Desconocido, null);
        h.LegajoRace!.OnLostRace = () => SeedAsync(h, winner);

        var response = AssertCreated(await h.Sut.CreateAsync(
            CreateRequest(fiscal: Fiscal(FiscalRegimes.Monotributo, "h")), CancellationToken.None));

        var legajo = Assert.Single(await AllLegajosAsync(h));                // the winner, and only it
        var entity = await SingleEntityAsync(h, Tenant, HumanaCuit);

        Assert.Equal(winner.PersonLegajoId, legajo.PersonLegajoId);
        Assert.Equal(winner.PersonLegajoId, entity.PersonLegajoId);
        Assert.Equal(winner.PersonLegajoId, response.PersonLegajoId);

        // This request's informative data was merged into the winner, not lost with the failed row.
        Assert.Equal(FiscalRegimes.Monotributo, legajo.FiscalRegime);
        Assert.Equal("H", legajo.MonotributoCategory);
        Assert.Equal(entity.FiscalDataJson, legajo.ArcaSnapshotJson);
        Assert.Equal(FiscalRegimes.Monotributo, entity.FiscalRegime);
        Assert.Equal("PERSONA DE PRUEBA", legajo.FullName);
        Assert.Null(legajo.CreatedBy);                                       // still the winner's author
        Assert.Equal("H", response.MonotributoCategory);

        // One re-read, one retry: the two entity-inserting saves are the lost one and the relinked one.
        Assert.Equal(2, h.LegajoRace.GetByCuitCalls);
        Assert.Equal((true, true),  h.LegajoRace.SaveCalls[0]);
        Assert.Equal((true, false), h.LegajoRace.SaveCalls[1]);
        Assert.Equal(2, h.LegajoRace.SaveCalls.Count(c => c.EntityIsNew));

        // The BCRA copy lands on the winner too.
        Assert.Equal(1, h.Bcra.CallCount);
        Assert.Equal(entity.BcraDataJson, legajo.BcraDataJson);
        Assert.NotNull(legajo.BcraDataJson);
        AssertNothingAlarming(h);
    }

    [Fact]
    public async Task Create_Humana_WhenTheLegajoRaceLeavesNoWinnerToReRead_Returns409()
    {
        // "Impossible without deletes" — and if it happens anyway, the answer is the same 409.
        var h = BuildHarness(legajoSaveLosesRaceAs: DuplicateCuitScope.PersonLegajo);

        var result = await h.Sut.CreateAsync(CreateRequest(), CancellationToken.None);

        AssertCuitConflict(result, HumanaCuit);
        Assert.Equal(2, h.LegajoRace!.GetByCuitCalls);
        Assert.Single(h.LegajoRace.SaveCalls);                               // no blind retry
        Assert.Empty(await AllEntitiesAsync(h));
        Assert.Empty(await AllLegajosAsync(h));
        Assert.Equal(0, h.Bcra.CallCount);
    }

    [Fact]
    public async Task Create_Humana_WhenTheEntityWriteLosesTheRace_Returns409WithThePreCheckText()
    {
        var h = BuildHarness(legajoSaveLosesRaceAs: DuplicateCuitScope.Entity);

        var result = await h.Sut.CreateAsync(
            CreateRequest(fiscal: Fiscal(FiscalRegimes.Monotributo, "h")), CancellationToken.None);

        AssertCuitConflict(result, HumanaCuit);
        Assert.Equal(1, h.LegajoRace!.GetByCuitCalls);                       // an Entity loss is not retried
        Assert.Single(h.LegajoRace.SaveCalls);
        Assert.Empty(await AllEntitiesAsync(h));
        Assert.Empty(await AllLegajosAsync(h));
        Assert.Equal(0, h.Bcra.CallCount);                                   // no enrichment for a 409
        Assert.Equal(0, h.Casfog.CallCount);
    }

    [Fact]
    public async Task Create_Juridica_WhenTheEntityWriteLosesTheRace_Returns409WithThePreCheckText()
    {
        // Before DDL 069 this was a 500 on UQ_Entities_CUIT; EntityRepository.CreateAsync now maps it.
        var h = BuildHarness(entityCreateLosesRace: true);

        var result = await h.Sut.CreateAsync(
            CreateRequest(cuit: JuridicaCuit, businessName: "EMPRESA DE PRUEBA SA"), CancellationToken.None);

        AssertCuitConflict(result, JuridicaCuit);
        Assert.Equal(1, h.EntityRace!.CreateCalls);
        Assert.Empty(await AllEntitiesAsync(h));
        Assert.Equal(0, h.Bcra.CallCount);
        Assert.Equal(0, h.Casfog.CallCount);
    }

    /// <summary>
    /// The same races end to end through the REAL repositories: SQL Server reports whichever
    /// Entities index it checks first, and both mean "this person already has an entity here". A
    /// legajo-index loss with nothing to re-read ends in the same 409. The provider message — which
    /// carries the duplicate key, i.e. the CUIL — reaches neither the response nor a log line.
    /// </summary>
    [Theory]
    [InlineData("UQ_Entities_TenantId_CUIT")]
    [InlineData("UX_Entities_PersonLegajoId")]
    [InlineData("UX_PersonLegajos_TenantId_CUIT")]
    public async Task Create_Humana_WhenSqlReportsAPerTenantCuitIndex_Returns409_AndWritesNothing(string indexName)
    {
        var h = BuildHarness(failingIndexName: indexName);

        var result = await h.Sut.CreateAsync(
            CreateRequest(fiscal: Fiscal(FiscalRegimes.Monotributo, "h")), CancellationToken.None);

        AssertCuitConflict(result, HumanaCuit);
        Assert.Empty(await AllEntitiesAsync(h));
        Assert.Empty(await AllLegajosAsync(h));
        Assert.Equal(0, h.Bcra.CallCount);
        AssertNeverLogged(h, HumanaCuit);
    }

    [Fact]
    public async Task Create_Juridica_WhenSqlReportsTheEntitiesCuitIndex_Returns409()
    {
        var h = BuildHarness(failingIndexName: "UQ_Entities_TenantId_CUIT");

        var result = await h.Sut.CreateAsync(
            CreateRequest(cuit: JuridicaCuit, businessName: "EMPRESA DE PRUEBA SA"), CancellationToken.None);

        AssertCuitConflict(result, JuridicaCuit);
        Assert.Empty(await AllEntitiesAsync(h));
    }

    [Fact]
    public async Task Create_Humana_WhenTheSaveFailsOnAnyOtherIndex_TheFailurePropagates()
    {
        // Only the three CUIT rules are a conflict; anything else is a real failure, not a 409.
        var h = BuildHarness(failingIndexName: "IX_Something_Unrelated");

        await Assert.ThrowsAsync<DbUpdateException>(
            () => h.Sut.CreateAsync(CreateRequest(), CancellationToken.None));

        Assert.Empty(await AllEntitiesAsync(h));
        Assert.Empty(await AllLegajosAsync(h));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // lookup-arca — personType and the persona humana's name
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task LookupArca_PersonaHumana_ResolvesHumana_AndComposesApellidoNombre()
    {
        var h = BuildHarness();
        h.Arca.Result = Persona("FISICA", apellido: "PRUEBA", nombre: "PERSONA", razonSocial: null,
            fiscal: Fiscal(FiscalRegimes.Monotributo, "H"));

        var response = AssertLookupOk(await h.Sut.LookupEntityFromArca(
            new ArcaLookupByCuitRequest { Cuit = HumanaCuit }, CancellationToken.None));

        Assert.Equal(PersonTypes.Humana, response.PersonType);
        Assert.Equal("PRUEBA, PERSONA", response.RazonSocial);
        Assert.Equal("FISICA", response.TipoPersona);                        // ARCA's raw text, untouched
        Assert.Equal(HumanaCuit, response.Cuit);
        Assert.NotNull(response.Fiscal);
        Assert.Equal(FiscalRegimes.Monotributo, response.Fiscal!.FiscalRegime);
        Assert.Equal("H", response.Fiscal.MonotributoCategoria);

        Assert.Equal(new[] { "20111111112" }, h.Arca.ReceivedCuits);
        Assert.Empty(await AllEntitiesAsync(h));                             // a lookup creates nothing
        AssertNothingAlarming(h);
    }

    /// <summary>
    /// Create does not call ARCA, so the key status has to ride inside `fiscal`, stamped with the time
    /// it was read, for the snapshot the client echoes back to carry it.
    /// </summary>
    [Fact]
    public async Task LookupArca_StampsTheKeyStatusIntoTheFiscalSnapshot()
    {
        var h = BuildHarness();
        h.Arca.Result = Persona("FISICA", apellido: "PRUEBA", nombre: "PERSONA", razonSocial: null,
            fiscal: Fiscal(FiscalRegimes.Monotributo, "H"));

        var before   = DateTime.UtcNow;
        var response = AssertLookupOk(await h.Sut.LookupEntityFromArca(
            new ArcaLookupByCuitRequest { Cuit = HumanaCuit }, CancellationToken.None));

        Assert.Equal("ACTIVO", response.EstadoClave);
        Assert.Equal("ACTIVO", response.Fiscal!.EstadoClave);
        Assert.InRange(response.Fiscal.EstadoClaveConsultadoAt!.Value, before, DateTime.UtcNow);
    }

    [Fact]
    public async Task LookupArca_WithoutAFiscalSection_StillAnswersWithTheTopLevelStatus()
    {
        var h = BuildHarness();
        h.Arca.Result = Persona("FISICA", apellido: "PRUEBA", nombre: "PERSONA", razonSocial: null, fiscal: null);

        var response = AssertLookupOk(await h.Sut.LookupEntityFromArca(
            new ArcaLookupByCuitRequest { Cuit = HumanaCuit }, CancellationToken.None));

        Assert.Equal("ACTIVO", response.EstadoClave);
        Assert.Null(response.Fiscal);
    }

    /// <summary>
    /// "APELLIDO, NOMBRE" from the parts ARCA actually sent — trimmed, blanks treated as missing — so
    /// a missing part never leaves a stray ", "; then ARCA's razonSocial; then "DESCONOCIDO".
    /// </summary>
    [Theory]
    [InlineData("PRUEBA",     null,        null,                "PRUEBA")]
    [InlineData("PRUEBA",     "   ",       null,                "PRUEBA")]
    [InlineData(null,         "PERSONA",   null,                "PERSONA")]
    [InlineData("  PRUEBA  ", " PERSONA ", null,                "PRUEBA, PERSONA")]
    [InlineData(null,         null,        "PERSONA DE PRUEBA", "PERSONA DE PRUEBA")]
    [InlineData(null,         null,        null,                "DESCONOCIDO")]
    [InlineData("",           " ",         "  ",                "DESCONOCIDO")]
    public async Task LookupArca_PersonaHumana_BuildsTheNameFromThePartsArcaSent_WithoutAStraySeparator(
        string? apellido, string? nombre, string? razonSocial, string expected)
    {
        var h = BuildHarness();
        h.Arca.Result = Persona("FISICA", apellido, nombre, razonSocial);

        var response = AssertLookupOk(await h.Sut.LookupEntityFromArca(
            new ArcaLookupByCuitRequest { Cuit = HumanaCuit }, CancellationToken.None));

        Assert.Equal(PersonTypes.Humana, response.PersonType);
        Assert.Equal(expected, response.RazonSocial);
        Assert.False(response.RazonSocial.StartsWith(',') || response.RazonSocial.TrimEnd().EndsWith(','));
    }

    [Fact]
    public async Task LookupArca_Company_IsJuridica_AndKeepsArcasRazonSocial()
    {
        var h = BuildHarness();
        h.Arca.Result = Persona("JURIDICA", apellido: null, nombre: null, razonSocial: "EMPRESA DE PRUEBA SA",
            fiscal: Fiscal(FiscalRegimes.ResponsableInscripto));

        var response = AssertLookupOk(await h.Sut.LookupEntityFromArca(
            new ArcaLookupByCuitRequest { Cuit = JuridicaCuit }, CancellationToken.None));

        Assert.Equal(PersonTypes.Juridica, response.PersonType);
        Assert.Equal("EMPRESA DE PRUEBA SA", response.RazonSocial);
        Assert.Equal("JURIDICA", response.TipoPersona);
        Assert.Equal(FiscalRegimes.ResponsableInscripto, response.Fiscal!.FiscalRegime);
        Assert.Null(response.Fiscal.MonotributoCategoria);
        AssertNothingAlarming(h);
    }

    /// <summary>
    /// 50- is not decided by the prefix, so ARCA's tipoPersona (any case) decides; neither is a
    /// disagreement. Unrecognised or absent, the type falls back to the column default, Juridica.
    /// </summary>
    [Theory]
    [InlineData("FISICA",   PersonTypes.Humana,   "PRUEBA, PERSONA")]
    [InlineData("fisica",   PersonTypes.Humana,   "PRUEBA, PERSONA")]
    [InlineData("JURIDICA", PersonTypes.Juridica, "EMPRESA DE PRUEBA SA")]
    [InlineData("OTRA",     PersonTypes.Juridica, "EMPRESA DE PRUEBA SA")]
    [InlineData(null,       PersonTypes.Juridica, "EMPRESA DE PRUEBA SA")]
    public async Task LookupArca_WithAnAmbiguousPrefix_FollowsArcasTipoPersona(
        string? tipoPersona, string expectedType, string expectedRazonSocial)
    {
        var h = BuildHarness();
        h.Arca.Result = Persona(tipoPersona, apellido: "PRUEBA", nombre: "PERSONA", razonSocial: "EMPRESA DE PRUEBA SA");

        var response = AssertLookupOk(await h.Sut.LookupEntityFromArca(
            new ArcaLookupByCuitRequest { Cuit = AmbiguousCuit }, CancellationToken.None));

        Assert.Equal(expectedType, response.PersonType);
        Assert.Equal(expectedRazonSocial, response.RazonSocial);
        Assert.Equal(tipoPersona, response.TipoPersona);                     // raw, case preserved
        AssertNothingAlarming(h);
    }

    /// <summary>
    /// An unambiguous prefix wins over ARCA too, and the disagreement is logged exactly once —
    /// naming the two types, never the CUIT. The name follows the RESOLVED type.
    /// </summary>
    [Theory]
    [InlineData(HumanaCuit,   "JURIDICA", PersonTypes.Humana,   "PRUEBA, PERSONA")]
    [InlineData(JuridicaCuit, "FISICA",   PersonTypes.Juridica, "EMPRESA DE PRUEBA SA")]
    public async Task LookupArca_WhenArcaContradictsAnUnambiguousPrefix_ThePrefixWins_AndOneWarningIsLogged(
        string cuit, string tipoPersona, string expectedType, string expectedRazonSocial)
    {
        var h = BuildHarness();
        h.Arca.Result = Persona(tipoPersona, apellido: "PRUEBA", nombre: "PERSONA", razonSocial: "EMPRESA DE PRUEBA SA");

        var response = AssertLookupOk(await h.Sut.LookupEntityFromArca(
            new ArcaLookupByCuitRequest { Cuit = cuit }, CancellationToken.None));

        Assert.Equal(expectedType, response.PersonType);
        Assert.Equal(expectedRazonSocial, response.RazonSocial);
        Assert.Equal(tipoPersona, response.TipoPersona);

        var warning = Assert.Single(h.Logger.Warnings);
        Assert.Contains("ARCA", warning);
        Assert.DoesNotContain(cuit, warning);
        Assert.DoesNotContain(cuit.Replace("-", string.Empty), warning);
        Assert.Empty(h.Logger.Errors);
    }

    [Fact]
    public async Task LookupArca_WhenTheCuitIsAnEntityOnlyInAnotherTenant_StillAnswers()
    {
        // The "already exists" pre-check of the lookup is per tenant as well.
        var h = BuildHarness();
        await SeedAsync(h, SeedEntity(OtherTenant, HumanaCuit, PersonTypes.Humana));
        h.Arca.Result = Persona("FISICA", apellido: "PRUEBA", nombre: "PERSONA", razonSocial: null);

        var response = AssertLookupOk(await h.Sut.LookupEntityFromArca(
            new ArcaLookupByCuitRequest { Cuit = HumanaCuit }, CancellationToken.None));

        Assert.Equal(PersonTypes.Humana, response.PersonType);
        Assert.Equal(1, h.Arca.CallCount);
    }

    [Fact]
    public async Task LookupArca_WhenTheCuitIsAlreadyAnEntityOfThisTenant_Returns409WithoutCallingArca()
    {
        var h = BuildHarness();
        await SeedAsync(h, SeedEntity(Tenant, HumanaCuit, PersonTypes.Humana));
        h.Arca.Result = Persona("FISICA", apellido: "PRUEBA", nombre: "PERSONA", razonSocial: null);

        var result = await h.Sut.LookupEntityFromArca(
            new ArcaLookupByCuitRequest { Cuit = HumanaCuit }, CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(409, obj.StatusCode);
        Assert.Equal(0, h.Arca.CallCount);
    }
}
