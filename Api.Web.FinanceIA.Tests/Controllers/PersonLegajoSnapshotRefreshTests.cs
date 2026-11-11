// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-29

using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Arca;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.Legajo;
using Api.Web.FinanceIA.Tests.Legajo;
using Api.Web.FinanceIA.Tests.PersonAnalysis;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// POST api/person-legajos/{legajoId}/refresh-bcra and /refresh-arca (M10 Phase 5): the BCRA/ARCA
/// refresh of a legajo, which is the only refresh route a FIADOR's legajo has (it hangs off no entity).
///
/// <para>
/// What is pinned: the snapshot lands on the legajo (and, for an entity's legajo, on the entity too,
/// with one payload — as the entity routes do); an unrelated, removed-fiador-only or foreign legajo is a
/// 404 before any vendor call; vendor failures write nothing; ARCA calls leave an ApiCallLogs row; logs
/// carry ids only.
/// </para>
///
/// <para>
/// Harness: <see cref="PersonLegajosController"/> over the REAL <see cref="PersonLegajoSnapshotRefreshService"/>
/// and the REAL repositories on ONE InMemory <see cref="FinanciaDbContext"/>; BCRA and ARCA are
/// hand-rolled stubs. Every CUIT and name is synthetic.
/// </para>
/// </summary>
public sealed class PersonLegajoSnapshotRefreshTests
{
    private const string Tenant      = LegajoTestData.Tenant;
    private const string OtherTenant = LegajoTestData.OtherTenant;
    private const string FiadorCuit  = LegajoTestData.LegajoCuit;
    private const string ClientCuit  = "27-11223344-5";
    private const string PymeCuit    = "30-55555555-1";
    private static readonly DateTime SeedTime = new(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);

    // ── Stubs ─────────────────────────────────────────────────────────────────

    private sealed class StubBcraService : IBcraService
    {
        public BcraData? Result { get; set; }
        public List<string> Cuits { get; } = [];

        public Task<BcraData?> GetBcraDataForAnalysisAsync(string cuit, CancellationToken ct)
        {
            Cuits.Add(cuit);
            return Task.FromResult(Result);
        }

        public Task<BcraDeudaData> GetDeudaAsync(string cuit, CancellationToken ct) => throw new NotImplementedException();
        public Task<BcraChequesData> GetChequesRechazadosAsync(string cuit, CancellationToken ct) => throw new NotImplementedException();
        public Task<BcraMacroData?> GetMacroAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<BcraCambiarioData?> GetCambiarioAsync(CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class StubArcaConstanciaService : IArcaConstanciaService
    {
        public ArcaPersonaData? Persona { get; set; }
        public Exception? ToThrow { get; set; }
        public List<string> Cuits { get; } = [];

        public Task<ArcaPersonaData?> GetPersonaByCuitAsync(
            string cuit, string cuitRepresentada, CancellationToken cancellationToken = default)
        {
            Cuits.Add(cuit);
            if (ToThrow is not null)
                throw ToThrow;
            return Task.FromResult(Persona);
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private sealed record Harness(
        PersonLegajosController   Sut,
        FinanciaDbContext         Db,
        StubBcraService           Bcra,
        StubArcaConstanciaService Arca,
        RecordingApiCallLogService ApiCallLog,
        RecordingLogger<PersonLegajoSnapshotRefreshService> Logger) : IDisposable
    {
        public void Dispose() => Db.Dispose();
    }

    private static Harness BuildHarness(bool tenantCuitConfigured = true)
    {
        var db = new FinanciaDbContext(new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"legajo-refresh-{Guid.NewGuid()}")
            .Options);

        var legajoDocuments = new LegajoDocumentRepository(db, NullLogger<LegajoDocumentRepository>.Instance);
        var bcra       = new StubBcraService();
        var arca       = new StubArcaConstanciaService();
        var apiCallLog = new RecordingApiCallLogService();
        var logger     = new RecordingLogger<PersonLegajoSnapshotRefreshService>();

        var service = new PersonLegajoSnapshotRefreshService(
            legajoDocuments,
            new PersonLegajoRepository(db, NullLogger<PersonLegajoRepository>.Instance),
            new EntityRepository(db, NullLogger<EntityRepository>.Instance),
            new FiadorRepository(db, NullLogger<FiadorRepository>.Instance),
            bcra,
            arca,
            apiCallLog,
            logger);

        var settings = new Dictionary<string, string?>();
        if (tenantCuitConfigured)
            settings[$"Tenants:{Tenant}:Cuit"] = "30-99999999-7";
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var sut = new PersonLegajosController(
            new StubTenantContextAccessor(),
            legajoDocuments,
            null!,   // IDocumentRepository: document reads only
            null!,   // IUserRepository: consent writes only
            null!,   // IAuditService: consent writes only
            null!,   // ILegajoPipelineQueue: consent writes only
            null!,   // ILegajoIntegrityService: the checklist read only
            service,
            new StubPersonExpectedDocumentsService(),
            config,
            Microsoft.Extensions.Options.Options.Create(new Api.Web.FinanceIA.Models.LegajoOptions()),
            NullLogger<PersonLegajosController>.Instance);

        return new Harness(sut, db, bcra, arca, apiCallLog, logger);
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static PersonLegajo NewLegajo(string cuit, string tenantId = Tenant) => new()
    {
        PersonLegajoId    = Guid.NewGuid(),
        TenantId          = tenantId,
        CUIT              = cuit,
        FullName          = "PERSONA SINTETICA",
        FiscalRegime      = FiscalRegimes.Desconocido,
        ArcaSnapshotJson  = """{"Actividades":[],"Impuestos":[],"Regimenes":[]}""",
        ArcaDataUpdatedAt = SeedTime,
        CreatedAt         = SeedTime,
        UpdatedAt         = SeedTime
    };

    private static Entity NewEntity(string cuit, PersonLegajo? legajo = null, string tenantId = Tenant) => new()
    {
        EntityId       = Guid.NewGuid(),
        TenantId       = tenantId,
        CUIT           = cuit,
        BusinessName   = "ENTIDAD SINTETICA",
        PersonType     = legajo is null ? PersonTypes.Juridica : PersonTypes.Humana,
        FiscalRegime   = legajo?.FiscalRegime,
        PersonLegajoId = legajo?.PersonLegajoId,
        CreatedAt      = SeedTime,
        UpdatedAt      = SeedTime
    };

    private static Fiador NewFiador(Entity pyme, PersonLegajo legajo, string status = "Active") => new()
    {
        FiadorId        = Guid.NewGuid(),
        TenantId        = pyme.TenantId,
        EntityId        = pyme.EntityId,
        CUIT            = legajo.CUIT,
        Denominacion    = "PERSONA SINTETICA",
        Origen          = "Manual",
        Status          = status,
        PersonLegajoId  = legajo.PersonLegajoId,
        CreatedByUserId = Guid.NewGuid(),
        CreatedAt       = SeedTime
    };

    /// <summary>A PyME (company) with one fiador whose legajo is linked: the legajo hangs off no entity.</summary>
    private static async Task<(Entity Pyme, PersonLegajo Legajo)> SeedFiadorLegajoAsync(Harness h, string status = "Active")
    {
        var legajo = NewLegajo(FiadorCuit);
        var pyme   = NewEntity(PymeCuit);
        await SeedAsync(h, [legajo], [pyme], [NewFiador(pyme, legajo, status)]);
        return (pyme, legajo);
    }

    private static async Task SeedAsync(Harness h, IEnumerable<PersonLegajo> legajos, IEnumerable<Entity> entities,
        IEnumerable<Fiador>? fiadores = null)
    {
        h.Db.PersonLegajos.AddRange(legajos);
        h.Db.Entities.AddRange(entities);
        h.Db.Fiadores.AddRange(fiadores ?? []);
        h.Db.ChangeTracker.DetectChanges();   // AutoDetectChanges is disabled on the context
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
    }

    private static Task<PersonLegajo> ReloadLegajoAsync(Harness h, Guid id)
        => h.Db.PersonLegajos.AsNoTracking().SingleAsync(l => l.PersonLegajoId == id);

    private static Task<Entity> ReloadEntityAsync(Harness h, Guid id)
        => h.Db.Entities.AsNoTracking().SingleAsync(e => e.EntityId == id);

    private static BcraData SomeBcra() => new() { Deuda = new BcraDeudaData { SituacionMaxima = 2, MontoTotal = 1500m } };

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

    private static ArcaPersonaData Persona(ArcaFiscalData? fiscal, string? estadoClave = "ACTIVO") => new(
        Cuit: LegajoTestData.LegajoCuitDigits, TipoPersona: "FISICA", EstadoClave: estadoClave,
        Nombre: "PERSONA", Apellido: "SINTETICA", RazonSocial: null,
        TipoDocumento: null, NumeroDocumento: null, FechaNacimiento: null,
        MesCierre: 12, IdActividadPrincipal: "620100", DescripcionActividadPrincipal: null,
        PeriodoActividadPrincipal: null, FormaJuridica: null, DomicilioFiscal: null, DomicilioLegal: null,
        FiscalData: fiscal);

    private static LegajoSnapshotRefreshResponse AssertOk(IActionResult result)
        => Assert.IsType<LegajoSnapshotRefreshResponse>(Assert.IsType<OkObjectResult>(result).Value);

    private static ProblemDetails AssertProblem(IActionResult result, int status)
    {
        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(status, obj.StatusCode);
        return Assert.IsType<ProblemDetails>(obj.Value);
    }

    // ── refresh-bcra ──────────────────────────────────────────────────────────

    [Fact]
    public async Task RefreshBcra_FiadorsLegajo_WritesTheLegajoSnapshot_AndNotThePymes()
    {
        using var h = BuildHarness();
        var (pyme, legajo) = await SeedFiadorLegajoAsync(h);
        h.Bcra.Result = SomeBcra();

        var body = AssertOk(await h.Sut.RefreshBcraAsync(legajo.PersonLegajoId, CancellationToken.None));

        Assert.Equal(legajo.PersonLegajoId, body.PersonLegajoId);
        Assert.Null(body.EntityId);
        Assert.Equal([FiadorCuit], h.Bcra.Cuits);   // the FIADOR's CUIT, as stored

        var reloaded = await ReloadLegajoAsync(h, legajo.PersonLegajoId);
        Assert.Contains("1500", reloaded.BcraDataJson);
        Assert.NotNull(reloaded.BcraDataUpdatedAt);
        Assert.Equal(reloaded.BcraDataUpdatedAt, body.BcraDataUpdatedAt);

        // The PyME's own snapshot is the company's: the fiador's BCRA never lands on it.
        Assert.Null((await ReloadEntityAsync(h, pyme.EntityId)).BcraDataJson);
    }

    [Fact]
    public async Task RefreshBcra_EntitysLegajo_UpdatesTheEntityToo_WithOnePayload()
    {
        using var h = BuildHarness();
        var legajo = NewLegajo(ClientCuit);
        var client = NewEntity(ClientCuit, legajo);
        await SeedAsync(h, [legajo], [client]);
        h.Bcra.Result = SomeBcra();

        var body = AssertOk(await h.Sut.RefreshBcraAsync(legajo.PersonLegajoId, CancellationToken.None));

        Assert.Equal(client.EntityId, body.EntityId);
        Assert.Single(h.Bcra.Cuits);

        var entity   = await ReloadEntityAsync(h, client.EntityId);
        var reloaded = await ReloadLegajoAsync(h, legajo.PersonLegajoId);
        Assert.NotNull(entity.BcraDataJson);
        Assert.Equal(entity.BcraDataJson, reloaded.BcraDataJson);
        Assert.Equal(entity.BcraDataUpdatedAt, reloaded.BcraDataUpdatedAt);
    }

    [Fact]
    public async Task RefreshBcra_BcraAnswersNothing_502_AndNothingIsWritten()
    {
        using var h = BuildHarness();
        var (_, legajo) = await SeedFiadorLegajoAsync(h);
        h.Bcra.Result = null;

        var problem = AssertProblem(await h.Sut.RefreshBcraAsync(legajo.PersonLegajoId, CancellationToken.None), 502);

        Assert.Equal("bcra_unavailable", problem.Extensions["errorCode"]);
        var reloaded = await ReloadLegajoAsync(h, legajo.PersonLegajoId);
        Assert.Null(reloaded.BcraDataJson);
        Assert.Equal(SeedTime, reloaded.UpdatedAt);
    }

    // ── V8: a failed Central de Deudores is never stored; failed cheques keep the stored ones ──

    private static readonly DateTime StoredChequesFetchedAt = new(2026, 1, 10, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>A good stored snapshot: debt at situación 1 and real rejected cheques.</summary>
    private static string StoredGoodBcraJson() => System.Text.Json.JsonSerializer.Serialize(new BcraData
    {
        Deuda   = new BcraDeudaData { Estado = BcraDeudaConsultaEstado.DebtFound, SituacionMaxima = 1, MontoTotal = 999m },
        Cheques = new BcraChequesData
        {
            Estado              = BcraChequesConsultaEstado.ChequesFound,
            TotalRechazados     = 2,
            MontoTotalRechazado = 5000m,
            MontoTotalImpago    = 1000m,
            FetchedAt           = StoredChequesFetchedAt
        }
    });

    private static async Task<(Entity Client, PersonLegajo Legajo, string StoredJson)> SeedClientWithStoredBcraAsync(Harness h)
    {
        var stored = StoredGoodBcraJson();
        var legajo = NewLegajo(ClientCuit);
        legajo.BcraDataJson      = stored;
        legajo.BcraDataUpdatedAt = SeedTime;
        var client = NewEntity(ClientCuit, legajo);
        client.BcraDataJson      = stored;
        client.BcraDataUpdatedAt = SeedTime;
        await SeedAsync(h, [legajo], [client]);
        return (client, legajo, stored);
    }

    [Fact]
    public async Task RefreshBcra_DeudaLookupFailed_502_AndNeitherLegajoNorEntityIsWritten()
    {
        using var h = BuildHarness();
        var (client, legajo, stored) = await SeedClientWithStoredBcraAsync(h);
        // Macro answered, so BcraService returns a payload — the shape the old code stored as fresh.
        h.Bcra.Result = new BcraData
        {
            Deuda   = new BcraDeudaData { Estado = BcraDeudaConsultaEstado.LookupFailed },
            Macro   = new BcraMacroData { InflacionMensual = 2.1 },
            Cheques = new BcraChequesData { Estado = BcraChequesConsultaEstado.NoChequesReported }
        };

        var problem = AssertProblem(await h.Sut.RefreshBcraAsync(legajo.PersonLegajoId, CancellationToken.None), 502);

        Assert.Equal("bcra_unavailable", problem.Extensions["errorCode"]);
        var reloadedLegajo = await ReloadLegajoAsync(h, legajo.PersonLegajoId);
        var reloadedEntity = await ReloadEntityAsync(h, client.EntityId);
        Assert.Equal(stored, reloadedLegajo.BcraDataJson);
        Assert.Equal(SeedTime, reloadedLegajo.BcraDataUpdatedAt);
        Assert.Equal(SeedTime, reloadedLegajo.UpdatedAt);
        Assert.Equal(stored, reloadedEntity.BcraDataJson);
        Assert.Equal(SeedTime, reloadedEntity.BcraDataUpdatedAt);
    }

    [Fact]
    public async Task RefreshBcra_ChequesLookupFailed_KeepsTheStoredCheques_AndStoresTheRestFresh()
    {
        using var h = BuildHarness();
        var (client, legajo, _) = await SeedClientWithStoredBcraAsync(h);
        h.Bcra.Result = new BcraData
        {
            Deuda   = new BcraDeudaData { Estado = BcraDeudaConsultaEstado.DebtFound, SituacionMaxima = 3, MontoTotal = 4242m },
            Cheques = new BcraChequesData { Estado = BcraChequesConsultaEstado.LookupFailed, FetchedAt = DateTime.UtcNow }
        };

        AssertOk(await h.Sut.RefreshBcraAsync(legajo.PersonLegajoId, CancellationToken.None));

        var reloadedLegajo = await ReloadLegajoAsync(h, legajo.PersonLegajoId);
        var saved = PersonSnapshotWriter.ReadBcra(reloadedLegajo.BcraDataJson)!;
        Assert.Equal(4242m, saved.Deuda!.MontoTotal);                                   // fresh deuda
        Assert.Equal(BcraChequesConsultaEstado.ChequesFound, saved.Cheques!.Estado);    // stored cheques
        Assert.Equal(5000m, saved.Cheques.MontoTotalRechazado);
        Assert.Equal(StoredChequesFetchedAt, saved.Cheques.FetchedAt.ToUniversalTime()); // with its own date
        Assert.True(reloadedLegajo.BcraDataUpdatedAt > SeedTime);

        var reloadedEntity = await ReloadEntityAsync(h, client.EntityId);
        Assert.Equal(reloadedLegajo.BcraDataJson, reloadedEntity.BcraDataJson);
    }

    [Fact]
    public async Task RefreshBcra_FullSuccess_ReplacesTheStoredChequesWithTheFreshAnswer()
    {
        using var h = BuildHarness();
        var (_, legajo, _) = await SeedClientWithStoredBcraAsync(h);
        h.Bcra.Result = new BcraData
        {
            Deuda   = new BcraDeudaData { Estado = BcraDeudaConsultaEstado.NoDebtReported },
            Cheques = new BcraChequesData { Estado = BcraChequesConsultaEstado.NoChequesReported }
        };

        AssertOk(await h.Sut.RefreshBcraAsync(legajo.PersonLegajoId, CancellationToken.None));

        var saved = PersonSnapshotWriter.ReadBcra((await ReloadLegajoAsync(h, legajo.PersonLegajoId)).BcraDataJson)!;
        Assert.Equal(BcraDeudaConsultaEstado.NoDebtReported, saved.Deuda!.Estado);
        Assert.Equal(BcraChequesConsultaEstado.NoChequesReported, saved.Cheques!.Estado);
    }

    [Fact]
    public async Task RefreshBcra_LegajoOfNoEntityAndNoFiador_404_BeforeAnyBcraCall()
    {
        using var h = BuildHarness();
        var orphan = NewLegajo(FiadorCuit);
        await SeedAsync(h, [orphan], []);
        h.Bcra.Result = SomeBcra();

        var problem = AssertProblem(await h.Sut.RefreshBcraAsync(orphan.PersonLegajoId, CancellationToken.None), 404);

        Assert.Equal("No se encontró el legajo.", problem.Detail);
        Assert.Empty(h.Bcra.Cuits);
    }

    [Fact]
    public async Task RefreshBcra_LegajoOfAREMOVEDFiadorOnly_404()
    {
        using var h = BuildHarness();
        var (_, legajo) = await SeedFiadorLegajoAsync(h, status: "Removed");
        h.Bcra.Result = SomeBcra();

        AssertProblem(await h.Sut.RefreshBcraAsync(legajo.PersonLegajoId, CancellationToken.None), 404);
        Assert.Empty(h.Bcra.Cuits);
    }

    [Fact]
    public async Task RefreshBcra_AnotherTenantsFiadorLegajo_404_AndUntouched()
    {
        using var h = BuildHarness();
        var foreign = NewLegajo(FiadorCuit, OtherTenant);
        var foreignPyme = NewEntity(PymeCuit, tenantId: OtherTenant);
        await SeedAsync(h, [foreign], [foreignPyme], [NewFiador(foreignPyme, foreign)]);
        h.Bcra.Result = SomeBcra();

        AssertProblem(await h.Sut.RefreshBcraAsync(foreign.PersonLegajoId, CancellationToken.None), 404);

        Assert.Empty(h.Bcra.Cuits);
        Assert.Null((await ReloadLegajoAsync(h, foreign.PersonLegajoId)).BcraDataJson);
    }

    [Fact]
    public async Task RefreshBcra_UnknownLegajo_404()
    {
        using var h = BuildHarness();

        AssertProblem(await h.Sut.RefreshBcraAsync(Guid.NewGuid(), CancellationToken.None), 404);
        Assert.Empty(h.Bcra.Cuits);
    }

    // ── refresh-arca ──────────────────────────────────────────────────────────

    [Fact]
    public async Task RefreshArca_FiadorsLegajo_WritesSnapshotRegimeAndCategory_AndLogsTheCall()
    {
        using var h = BuildHarness();
        var (_, legajo) = await SeedFiadorLegajoAsync(h);
        h.Arca.Persona = Persona(Fiscal(FiscalRegimes.Monotributo, "D"));

        var body = AssertOk(await h.Sut.RefreshArcaAsync(legajo.PersonLegajoId, CancellationToken.None));

        Assert.Equal([LegajoTestData.LegajoCuitDigits], h.Arca.Cuits);   // ARCA takes the digits
        Assert.Equal(FiscalRegimes.Monotributo, body.FiscalRegime);
        Assert.Equal("D", body.MonotributoCategory);

        var reloaded = await ReloadLegajoAsync(h, legajo.PersonLegajoId);
        Assert.Equal(FiscalRegimes.Monotributo, reloaded.FiscalRegime);
        Assert.Equal("D", reloaded.MonotributoCategory);
        Assert.Contains("ACTIVO", reloaded.ArcaSnapshotJson);
        Assert.True(reloaded.ArcaDataUpdatedAt > SeedTime);

        var call = Assert.Single(h.ApiCallLog.Calls);
        Assert.Equal("ARCA", call.Service);
        Assert.True(call.IsSuccess);
    }

    [Fact]
    public async Task RefreshArca_EntitysLegajo_KeepsEntityAndLegajoInAgreement()
    {
        using var h = BuildHarness();
        var legajo = NewLegajo(ClientCuit);
        var client = NewEntity(ClientCuit, legajo);
        await SeedAsync(h, [legajo], [client]);
        h.Arca.Persona = Persona(Fiscal(FiscalRegimes.Monotributo, "B"));

        AssertOk(await h.Sut.RefreshArcaAsync(legajo.PersonLegajoId, CancellationToken.None));

        var entity   = await ReloadEntityAsync(h, client.EntityId);
        var reloaded = await ReloadLegajoAsync(h, legajo.PersonLegajoId);
        Assert.Equal(FiscalRegimes.Monotributo, entity.FiscalRegime);
        Assert.Equal(entity.FiscalRegime, reloaded.FiscalRegime);
        Assert.Equal(entity.FiscalDataJson, reloaded.ArcaSnapshotJson);
    }

    [Fact]
    public async Task RefreshArca_KeyStatusOnly_MergesTheStatus_AndKeepsTheSnapshotStamp()
    {
        using var h = BuildHarness();
        var (_, legajo) = await SeedFiadorLegajoAsync(h);
        h.Arca.Persona = Persona(fiscal: null, estadoClave: "INACTIVO");

        AssertOk(await h.Sut.RefreshArcaAsync(legajo.PersonLegajoId, CancellationToken.None));

        var reloaded = await ReloadLegajoAsync(h, legajo.PersonLegajoId);
        Assert.Contains("INACTIVO", reloaded.ArcaSnapshotJson);
        Assert.Equal(SeedTime, reloaded.ArcaDataUpdatedAt);
        Assert.Equal(FiscalRegimes.Desconocido, reloaded.FiscalRegime);
    }

    [Fact]
    public async Task RefreshArca_Unreachable_502_ApiCallLogFailure_NothingWritten()
    {
        using var h = BuildHarness();
        var (_, legajo) = await SeedFiadorLegajoAsync(h);
        h.Arca.ToThrow = new HttpRequestException("ARCA Constancia getPersona_v2 answered a SOAP Fault.");

        var problem = AssertProblem(await h.Sut.RefreshArcaAsync(legajo.PersonLegajoId, CancellationToken.None), 502);

        Assert.Equal("arca_unavailable", problem.Extensions["errorCode"]);
        Assert.False(Assert.Single(h.ApiCallLog.Calls).IsSuccess);
        Assert.Equal(SeedTime, (await ReloadLegajoAsync(h, legajo.PersonLegajoId)).UpdatedAt);
    }

    [Fact]
    public async Task RefreshArca_NoSuchPerson_404_ArcaNotFound()
    {
        using var h = BuildHarness();
        var (_, legajo) = await SeedFiadorLegajoAsync(h);
        h.Arca.Persona = null;

        var problem = AssertProblem(await h.Sut.RefreshArcaAsync(legajo.PersonLegajoId, CancellationToken.None), 404);

        Assert.Equal("arca_not_found", problem.Extensions["errorCode"]);
        Assert.False(Assert.Single(h.ApiCallLog.Calls).IsSuccess);
    }

    [Fact]
    public async Task RefreshArca_NoTenantCuit_400_ButAnUnknownLegajoIsStill404()
    {
        using var h = BuildHarness(tenantCuitConfigured: false);
        var (_, legajo) = await SeedFiadorLegajoAsync(h);

        var problem = AssertProblem(await h.Sut.RefreshArcaAsync(legajo.PersonLegajoId, CancellationToken.None), 400);
        Assert.Equal("arca_not_configured", problem.Extensions["errorCode"]);

        AssertProblem(await h.Sut.RefreshArcaAsync(Guid.NewGuid(), CancellationToken.None), 404);
        Assert.Empty(h.Arca.Cuits);
    }

    [Fact]
    public async Task RefreshArca_UnrelatedLegajo_404_BeforeAnyArcaCall()
    {
        using var h = BuildHarness();
        var orphan = NewLegajo(FiadorCuit);
        await SeedAsync(h, [orphan], []);
        h.Arca.Persona = Persona(Fiscal(FiscalRegimes.Monotributo, "D"));

        AssertProblem(await h.Sut.RefreshArcaAsync(orphan.PersonLegajoId, CancellationToken.None), 404);

        Assert.Empty(h.Arca.Cuits);
        Assert.Empty(h.ApiCallLog.Calls);
    }

    [Fact]
    public async Task Logs_CarryIdsOnly()
    {
        using var h = BuildHarness();
        var (_, legajo) = await SeedFiadorLegajoAsync(h);
        h.Bcra.Result = SomeBcra();
        h.Arca.Persona = Persona(Fiscal(FiscalRegimes.Monotributo, "D"));

        await h.Sut.RefreshBcraAsync(legajo.PersonLegajoId, CancellationToken.None);
        await h.Sut.RefreshArcaAsync(legajo.PersonLegajoId, CancellationToken.None);
        h.Arca.Persona = Persona(fiscal: null, estadoClave: "INACTIVO");
        await h.Sut.RefreshArcaAsync(legajo.PersonLegajoId, CancellationToken.None);

        Assert.NotEmpty(h.Logger.Messages);
        foreach (var message in h.Logger.Messages)
        {
            Assert.DoesNotContain(LegajoTestData.LegajoCuitDigits, message);
            Assert.DoesNotContain(FiadorCuit, message);
            Assert.DoesNotContain("SINTETICA", message);
            Assert.DoesNotContain("INACTIVO", message);
        }
    }
}
