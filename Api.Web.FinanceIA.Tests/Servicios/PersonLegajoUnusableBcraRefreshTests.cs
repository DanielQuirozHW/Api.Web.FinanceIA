// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-29

using System.Text.Json;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Arca;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.Legajo;
using Api.Web.FinanceIA.Tests.Legajo;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// <see cref="PersonLegajoSnapshotRefreshService.RefreshUnusableBcraAsync"/> (M10 Phase 5a-2): the fiador's
/// counterpart of the solicitante's re-query at evaluation. A usable snapshot costs no call; a missing or
/// failed one is asked once; only an answer whose Central de Deudores part is real is kept; a BCRA exception
/// is isolated. The REAL service and repositories on one InMemory context; every CUIT is synthetic.
/// </summary>
public sealed class PersonLegajoUnusableBcraRefreshTests
{
    private const string Tenant     = LegajoTestData.Tenant;
    private const string FiadorCuit = LegajoTestData.LegajoCuit;
    private const string PymeCuit   = "30-55555555-1";

    private sealed class StubBcraService : IBcraService
    {
        public BcraData? Result { get; set; }
        public bool Throws { get; set; }
        public List<string> Cuits { get; } = [];

        public Task<BcraData?> GetBcraDataForAnalysisAsync(string cuit, CancellationToken ct)
        {
            Cuits.Add(cuit);
            return Throws ? throw new HttpRequestException("BCRA down") : Task.FromResult(Result);
        }

        public Task<BcraDeudaData> GetDeudaAsync(string cuit, CancellationToken ct) => throw new NotImplementedException();
        public Task<BcraChequesData> GetChequesRechazadosAsync(string cuit, CancellationToken ct) => throw new NotImplementedException();
        public Task<BcraMacroData?> GetMacroAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<BcraCambiarioData?> GetCambiarioAsync(CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class NoArca : IArcaConstanciaService
    {
        public Task<ArcaPersonaData?> GetPersonaByCuitAsync(string cuit, string cuitRepresentada, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private static (PersonLegajoSnapshotRefreshService Service, FinanciaDbContext Db, StubBcraService Bcra) Build()
    {
        var db = new FinanciaDbContext(new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"legajo-unusable-bcra-{Guid.NewGuid()}")
            .Options);
        var bcra = new StubBcraService();
        var service = new PersonLegajoSnapshotRefreshService(
            new LegajoDocumentRepository(db, NullLogger<LegajoDocumentRepository>.Instance),
            new PersonLegajoRepository(db, NullLogger<PersonLegajoRepository>.Instance),
            new EntityRepository(db, NullLogger<EntityRepository>.Instance),
            new FiadorRepository(db, NullLogger<FiadorRepository>.Instance),
            bcra,
            new NoArca(),
            new RecordingApiCallLogService(),
            NullLogger<PersonLegajoSnapshotRefreshService>.Instance);
        return (service, db, bcra);
    }

    private static BcraData Snapshot(BcraDeudaConsultaEstado deuda, BcraChequesConsultaEstado cheques) => new()
    {
        Deuda   = new BcraDeudaData { Estado = deuda, EntidadesDetalle = [], HistorialSituaciones = [] },
        Cheques = new BcraChequesData { Estado = cheques }
    };

    /// <summary>A PyME with one active fiador whose legajo holds <paramref name="stored"/> (null: no snapshot).</summary>
    private static async Task<PersonLegajo> SeedAsync(FinanciaDbContext db, BcraData? stored)
    {
        var now = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        var legajo = new PersonLegajo
        {
            PersonLegajoId    = Guid.NewGuid(),
            TenantId          = Tenant,
            CUIT              = FiadorCuit,
            FullName          = "PERSONA SINTETICA",
            FiscalRegime      = FiscalRegimes.Desconocido,
            BcraDataJson      = stored is null ? null : JsonSerializer.Serialize(stored),
            BcraDataUpdatedAt = stored is null ? null : now,
            CreatedAt         = now,
            UpdatedAt         = now
        };
        var pyme = new Entity
        {
            EntityId = Guid.NewGuid(), TenantId = Tenant, CUIT = PymeCuit, BusinessName = "PYME SINTETICA",
            PersonType = PersonTypes.Juridica, CreatedAt = now, UpdatedAt = now
        };
        var fiador = new Fiador
        {
            FiadorId = Guid.NewGuid(), TenantId = Tenant, EntityId = pyme.EntityId, CUIT = FiadorCuit,
            Denominacion = "PERSONA SINTETICA", Origen = "Manual", Status = "Active",
            PersonLegajoId = legajo.PersonLegajoId, CreatedByUserId = Guid.NewGuid(), CreatedAt = now
        };
        db.PersonLegajos.Add(legajo);
        db.Entities.Add(pyme);
        db.Fiadores.Add(fiador);
        db.ChangeTracker.DetectChanges();
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return legajo;
    }

    private static Task<PersonLegajo> ReloadAsync(FinanciaDbContext db, Guid id)
        => db.PersonLegajos.AsNoTracking().SingleAsync(l => l.PersonLegajoId == id);

    [Fact]
    public async Task AUsableSnapshot_IsNotNeeded_AndCostsNoCall()
    {
        var (service, db, bcra) = Build();
        var legajo = await SeedAsync(db, Snapshot(BcraDeudaConsultaEstado.NoDebtReported, BcraChequesConsultaEstado.NoChequesReported));

        var outcome = await service.RefreshUnusableBcraAsync(legajo.PersonLegajoId, Tenant, CancellationToken.None);

        Assert.Equal(LegajoSnapshotRefreshStatus.NotNeeded, outcome.Status);
        Assert.Equal(legajo.PersonLegajoId, outcome.Legajo!.PersonLegajoId);
        Assert.Empty(bcra.Cuits);
    }

    [Fact]
    public async Task AMissingSnapshot_IsAskedOnce_AndARealAnswerIsKept()
    {
        var (service, db, bcra) = Build();
        var legajo = await SeedAsync(db, null);
        bcra.Result = Snapshot(BcraDeudaConsultaEstado.DebtFound, BcraChequesConsultaEstado.NoChequesReported);

        var outcome = await service.RefreshUnusableBcraAsync(legajo.PersonLegajoId, Tenant, CancellationToken.None);

        Assert.Equal(LegajoSnapshotRefreshStatus.Refreshed, outcome.Status);
        Assert.Equal([FiadorCuit], bcra.Cuits);
        var stored = await ReloadAsync(db, legajo.PersonLegajoId);
        Assert.NotNull(stored.BcraDataUpdatedAt);
        Assert.Equal(BcraDeudaConsultaEstado.DebtFound, PersonSnapshotWriter.ReadBcra(stored.BcraDataJson)!.Deuda!.Estado);
        Assert.Equal(stored.BcraDataJson, outcome.Legajo!.BcraDataJson);
    }

    [Fact]
    public async Task AFailedChequesQuery_IsUnusable_AndAsked()
    {
        var (service, db, bcra) = Build();
        var legajo = await SeedAsync(db, Snapshot(BcraDeudaConsultaEstado.NoDebtReported, BcraChequesConsultaEstado.LookupFailed));
        bcra.Result = Snapshot(BcraDeudaConsultaEstado.NoDebtReported, BcraChequesConsultaEstado.NoChequesReported);

        var outcome = await service.RefreshUnusableBcraAsync(legajo.PersonLegajoId, Tenant, CancellationToken.None);

        Assert.Equal(LegajoSnapshotRefreshStatus.Refreshed, outcome.Status);
        Assert.Single(bcra.Cuits);
    }

    [Fact]
    public async Task AnAnswerWhoseCentralDeDeudoresFailedAgain_IsNotKept()
    {
        var (service, db, bcra) = Build();
        var stored = Snapshot(BcraDeudaConsultaEstado.LookupFailed, BcraChequesConsultaEstado.ChequesFound);
        var legajo = await SeedAsync(db, stored);
        bcra.Result = Snapshot(BcraDeudaConsultaEstado.LookupFailed, BcraChequesConsultaEstado.NoChequesReported);

        var outcome = await service.RefreshUnusableBcraAsync(legajo.PersonLegajoId, Tenant, CancellationToken.None);

        Assert.Equal(LegajoSnapshotRefreshStatus.BcraUnavailable, outcome.Status);
        Assert.Equal(JsonSerializer.Serialize(stored), (await ReloadAsync(db, legajo.PersonLegajoId)).BcraDataJson);
    }

    [Fact]
    public async Task ABcraException_IsIsolated_AndTheSnapshotKept()
    {
        var (service, db, bcra) = Build();
        var legajo = await SeedAsync(db, null);
        bcra.Throws = true;

        var outcome = await service.RefreshUnusableBcraAsync(legajo.PersonLegajoId, Tenant, CancellationToken.None);

        Assert.Equal(LegajoSnapshotRefreshStatus.BcraUnavailable, outcome.Status);
        Assert.NotNull(outcome.Legajo);
        Assert.Null((await ReloadAsync(db, legajo.PersonLegajoId)).BcraDataJson);
    }

    [Fact]
    public async Task AnUnreachableLegajo_IsNotFound_BeforeAnyCall()
    {
        var (service, db, bcra) = Build();
        var legajo = await SeedAsync(db, null);

        var outcome = await service.RefreshUnusableBcraAsync(legajo.PersonLegajoId, LegajoTestData.OtherTenant, CancellationToken.None);

        Assert.Equal(LegajoSnapshotRefreshStatus.LegajoNotFound, outcome.Status);
        Assert.Empty(bcra.Cuits);
    }
}
