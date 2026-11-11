// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-29

using System.Security.Claims;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Tests.Legajo;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// POST api/entities/{entityId}/fiadores/{fiadorId}/legajo (M10 Phase 5): a fiador persona humana gets
/// the tenant's legajo for its CUIT — created, or linked when the tenant already has one — and nothing
/// else on the fiador row moves.
///
/// <para>
/// Harness: the REAL <see cref="FiadorRepository"/>, <see cref="PersonLegajoRepository"/> and
/// <see cref="EntityRepository"/> over ONE InMemory <see cref="FinanciaDbContext"/>, because "one legajo
/// per (tenant, CUIT)", "only PersonLegajoId is written" and the tenant scoping are what is under test.
/// InMemory enforces no unique index, so the lost insert race is staged by a spy that inserts the
/// winner and throws what the real repository throws. Every CUIT and name is synthetic.
/// </para>
/// </summary>
public sealed class FiadoresControllerLegajoTests
{
    private const string Tenant         = LegajoTestData.Tenant;
    private const string OtherTenant    = LegajoTestData.OtherTenant;
    private const string PymeCuit       = "30-55555555-1";
    private const string FiadorCuit     = LegajoTestData.LegajoCuit;        // 20-…, persona humana
    private const string JuridicaCuit   = "30-50001234-5";
    private const string FiadorName     = "FIADOR SINTETICO, PERSONA";
    private const string ClientName     = "CLIENTE SINTETICO, PERSONA";

    // ── Harness ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Delegates to the real repository; when <see cref="LoseRaceWith"/> is set, the first save that
    /// inserts a legajo instead inserts that WINNER (as the concurrent request would have) and throws
    /// what SQL Server's unique index makes the real repository throw.
    /// </summary>
    private sealed class RacingLegajoRepository(PersonLegajoRepository inner, FinanciaDbContext db) : IPersonLegajoRepository
    {
        public PersonLegajo? LoseRaceWith { get; set; }
        public List<bool> LinkSaves { get; } = [];

        public Task<PersonLegajo?> GetByCuitAsync(string cuit, string tenantId, CancellationToken ct) => inner.GetByCuitAsync(cuit, tenantId, ct);
        public Task<PersonLegajo?> GetByIdAsync(Guid personLegajoId, string tenantId, CancellationToken ct) => inner.GetByIdAsync(personLegajoId, tenantId, ct);
        public Task<string?> GetMonotributoCategoryAsync(Guid personLegajoId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task SaveEntityWithLegajoAsync(Entity entity, bool entityIsNew, PersonLegajo legajo, bool legajoIsNew, CancellationToken ct) => throw new NotImplementedException();
        public Task SaveLegajoSnapshotsAsync(PersonLegajo legajo, CancellationToken ct) => throw new NotImplementedException();

        public async Task SaveFiadorLegajoLinkAsync(Fiador fiador, PersonLegajo legajo, bool legajoIsNew, CancellationToken ct)
        {
            LinkSaves.Add(legajoIsNew);

            if (legajoIsNew && LoseRaceWith is { } winner)
            {
                LoseRaceWith = null;
                db.PersonLegajos.Add(winner);
                db.ChangeTracker.DetectChanges();
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
                throw new DuplicateCuitException(DuplicateCuitScope.PersonLegajo);
            }

            await inner.SaveFiadorLegajoLinkAsync(fiador, legajo, legajoIsNew, ct);
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception) + (exception is null ? "" : " " + exception.Message));
    }

    private sealed record Harness(
        FiadoresController                  Sut,
        FinanciaDbContext                   Db,
        RacingLegajoRepository              Legajos,
        RecordingAuditService               Audit,
        RecordingLogger<FiadoresController> Logger) : IDisposable
    {
        public void Dispose() => Db.Dispose();
    }

    private static Harness BuildHarness()
    {
        var db = new FinanciaDbContext(new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"fiador-legajo-{Guid.NewGuid()}")
            .Options);

        var legajos = new RacingLegajoRepository(new PersonLegajoRepository(db, NullLogger<PersonLegajoRepository>.Instance), db);
        var audit   = new RecordingAuditService();
        var logger  = new RecordingLogger<FiadoresController>();

        var sut = new FiadoresController(
            new StubTenantContextAccessor(),
            new FiadorRepository(db, NullLogger<FiadorRepository>.Instance),
            new EntityRepository(db, NullLogger<EntityRepository>.Instance),
            new StubUserRepository(),
            audit,
            null!,   // INosisService: refresh-nosis only
            null!,   // IIntegrationAccessService: refresh-nosis only
            null!,   // IArcaConstanciaService: arca-lookup only
            null!,   // IApiCallLogService: arca-lookup only
            legajos,
            null!,   // IConfiguration: arca-lookup only
            logger)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("uid", LegajoTestData.FirebaseUid)], "Test"))
                }
            }
        };

        return new Harness(sut, db, legajos, audit, logger);
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static Entity Pyme(string cuit = PymeCuit, string tenantId = Tenant, PersonLegajo? legajo = null) => new()
    {
        EntityId       = Guid.NewGuid(),
        TenantId       = tenantId,
        CUIT           = cuit,
        BusinessName   = "PYME SINTETICA SA",
        PersonType     = CuitHelperPersonType(cuit),
        PersonLegajoId = legajo?.PersonLegajoId,
        CreatedAt      = DateTime.UtcNow,
        UpdatedAt      = DateTime.UtcNow
    };

    private static string CuitHelperPersonType(string cuit)
        => Api.Web.FinanceIA.Clases.CuitHelper.PersonTypeFromPrefix(cuit) ?? PersonTypes.Juridica;

    private static Fiador ManualFiador(Entity entity, string cuit = FiadorCuit, string status = "Active") => new()
    {
        FiadorId        = Guid.NewGuid(),
        TenantId        = entity.TenantId,
        EntityId        = entity.EntityId,
        CUIT            = cuit,
        Denominacion    = FiadorName,
        Cargo           = "Socio",
        Origen          = "Manual",
        Status          = status,
        CreatedByUserId = Guid.NewGuid(),
        CreatedAt       = DateTime.UtcNow
    };

    private static PersonLegajo LegajoFor(string cuit, string tenantId = Tenant, string fullName = ClientName) => new()
    {
        PersonLegajoId = Guid.NewGuid(),
        TenantId       = tenantId,
        CUIT           = cuit,
        FullName       = fullName,
        FiscalRegime   = FiscalRegimes.Monotributo,
        CreatedAt      = DateTime.UtcNow,
        UpdatedAt      = DateTime.UtcNow
    };

    private static async Task SeedAsync(Harness h, IEnumerable<Entity> entities, IEnumerable<Fiador> fiadores,
        IEnumerable<PersonLegajo>? legajos = null)
    {
        h.Db.PersonLegajos.AddRange(legajos ?? []);
        h.Db.Entities.AddRange(entities);
        h.Db.Fiadores.AddRange(fiadores);
        h.Db.ChangeTracker.DetectChanges();   // AutoDetectChanges is disabled on the context
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
    }

    private static Task<Fiador> ReloadFiadorAsync(Harness h, Guid fiadorId)
        => h.Db.Fiadores.AsNoTracking().SingleAsync(f => f.FiadorId == fiadorId);

    private static Task<List<PersonLegajo>> LegajosForAsync(Harness h, string cuit, string tenantId = Tenant)
        => h.Db.PersonLegajos.AsNoTracking().Where(l => l.CUIT == cuit && l.TenantId == tenantId).ToListAsync();

    private static FiadorLegajoLinkResponse AssertLink(IActionResult result, int status)
    {
        var obj = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(status, obj.StatusCode ?? StatusCodes.Status200OK);
        return Assert.IsType<FiadorLegajoLinkResponse>(obj.Value);
    }

    private static void AssertCodedProblem(IActionResult result, int status, string errorCode)
    {
        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(status, obj.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(obj.Value);
        Assert.Equal(errorCode, problem.Extensions["errorCode"]);
    }

    // ── Create / link ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ManualHumanaFiador_WithoutLegajo_CreatesOneAndLinksIt_201()
    {
        using var h = BuildHarness();
        var pyme = Pyme();
        var fiador = ManualFiador(pyme);
        await SeedAsync(h, [pyme], [fiador]);

        var result = await h.Sut.LinkFiadorLegajoAsync(pyme.EntityId, fiador.FiadorId, CancellationToken.None);

        var body = AssertLink(result, 201);
        Assert.True(body.Created);

        var legajo = Assert.Single(await LegajosForAsync(h, FiadorCuit));
        Assert.Equal(body.PersonLegajoId, legajo.PersonLegajoId);
        Assert.Equal(FiadorName, legajo.FullName);
        Assert.Equal(FiscalRegimes.Desconocido, legajo.FiscalRegime);
        Assert.Null(legajo.ConsentRecordedAt);

        var reloaded = await ReloadFiadorAsync(h, fiador.FiadorId);
        Assert.Equal(legajo.PersonLegajoId, reloaded.PersonLegajoId);

        // Only the link moved: the 'Editado' stamp of DDL 074 and the analyst's fields are untouched.
        Assert.Null(reloaded.UpdatedAt);
        Assert.Null(reloaded.UpdatedByUserId);
        Assert.Equal(FiadorName, reloaded.Denominacion);
        Assert.Equal("Active", reloaded.Status);

        var audit = Assert.Single(h.Audit.Entries);
        Assert.Equal("Fiador.LegajoLinked", audit.Action);
        Assert.Equal(fiador.FiadorId.ToString(), audit.EntityId);
        Assert.DoesNotContain(LegajoTestData.LegajoCuitDigits, audit.NewJson);
        Assert.DoesNotContain(FiadorCuit, audit.NewJson);
        Assert.DoesNotContain("SINTETICO", audit.NewJson);
    }

    [Fact]
    public async Task SecondCall_ReturnsTheSameLegajo_200_NotCreated()
    {
        using var h = BuildHarness();
        var pyme = Pyme();
        var fiador = ManualFiador(pyme);
        await SeedAsync(h, [pyme], [fiador]);

        var first  = AssertLink(await h.Sut.LinkFiadorLegajoAsync(pyme.EntityId, fiador.FiadorId, CancellationToken.None), 201);
        var second = AssertLink(await h.Sut.LinkFiadorLegajoAsync(pyme.EntityId, fiador.FiadorId, CancellationToken.None), 200);

        Assert.Equal(first.PersonLegajoId, second.PersonLegajoId);
        Assert.False(second.Created);
        Assert.Single(await LegajosForAsync(h, FiadorCuit));
        Assert.Equal([true], h.Legajos.LinkSaves);   // the idempotent call writes nothing
    }

    [Fact]
    public async Task FiadorWhoIsAlsoAClient_LinksTheClientsLegajo_NotADuplicate()
    {
        using var h = BuildHarness();
        var clientLegajo = LegajoFor(FiadorCuit);
        var client = Pyme(FiadorCuit, legajo: clientLegajo);   // the fiador is a persona humana client
        var pyme = Pyme();
        var fiador = ManualFiador(pyme);
        await SeedAsync(h, [client, pyme], [fiador], [clientLegajo]);

        var body = AssertLink(await h.Sut.LinkFiadorLegajoAsync(pyme.EntityId, fiador.FiadorId, CancellationToken.None), 200);

        Assert.False(body.Created);
        Assert.Equal(clientLegajo.PersonLegajoId, body.PersonLegajoId);

        var legajo = Assert.Single(await LegajosForAsync(h, FiadorCuit));
        Assert.Equal(ClientName, legajo.FullName);   // the existing legajo is linked, never rewritten
        Assert.Equal(FiscalRegimes.Monotributo, legajo.FiscalRegime);
        Assert.Equal(clientLegajo.PersonLegajoId, (await ReloadFiadorAsync(h, fiador.FiadorId)).PersonLegajoId);
        Assert.Equal([false], h.Legajos.LinkSaves);
    }

    [Fact]
    public async Task FiadorOfTwoPymes_SharesOneLegajo()
    {
        using var h = BuildHarness();
        var pymeA = Pyme();
        var pymeB = Pyme("30-60001234-1");
        var fiadorA = ManualFiador(pymeA);
        var fiadorB = ManualFiador(pymeB);
        await SeedAsync(h, [pymeA, pymeB], [fiadorA, fiadorB]);

        var a = AssertLink(await h.Sut.LinkFiadorLegajoAsync(pymeA.EntityId, fiadorA.FiadorId, CancellationToken.None), 201);
        var b = AssertLink(await h.Sut.LinkFiadorLegajoAsync(pymeB.EntityId, fiadorB.FiadorId, CancellationToken.None), 200);

        Assert.Equal(a.PersonLegajoId, b.PersonLegajoId);
        Assert.False(b.Created);
        Assert.Single(await LegajosForAsync(h, FiadorCuit));
    }

    [Fact]
    public async Task LostInsertRace_LinksTheWinner_OneLegajo()
    {
        using var h = BuildHarness();
        var pyme = Pyme();
        var fiador = ManualFiador(pyme);
        await SeedAsync(h, [pyme], [fiador]);

        var winner = LegajoFor(FiadorCuit, fullName: FiadorName);
        h.Legajos.LoseRaceWith = winner;

        var body = AssertLink(await h.Sut.LinkFiadorLegajoAsync(pyme.EntityId, fiador.FiadorId, CancellationToken.None), 200);

        Assert.False(body.Created);
        Assert.Equal(winner.PersonLegajoId, body.PersonLegajoId);
        Assert.Equal(winner.PersonLegajoId, Assert.Single(await LegajosForAsync(h, FiadorCuit)).PersonLegajoId);
        Assert.Equal(winner.PersonLegajoId, (await ReloadFiadorAsync(h, fiador.FiadorId)).PersonLegajoId);
        Assert.Equal([true, false], h.Legajos.LinkSaves);   // one lost insert, one link of the winner
    }

    [Fact]
    public async Task LegajoOfAnotherTenant_WithTheSameCuit_IsNeverLinked()
    {
        using var h = BuildHarness();
        var foreign = LegajoFor(FiadorCuit, OtherTenant);
        var pyme = Pyme();
        var fiador = ManualFiador(pyme);
        await SeedAsync(h, [pyme], [fiador], [foreign]);

        var body = AssertLink(await h.Sut.LinkFiadorLegajoAsync(pyme.EntityId, fiador.FiadorId, CancellationToken.None), 201);

        Assert.NotEqual(foreign.PersonLegajoId, body.PersonLegajoId);
        Assert.Single(await LegajosForAsync(h, FiadorCuit));
        Assert.Single(await LegajosForAsync(h, FiadorCuit, OtherTenant));
    }

    // ── Refusals ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task JuridicaFiador_400_FiadorNotPersonaHumana()
    {
        using var h = BuildHarness();
        var pyme = Pyme();
        var fiador = ManualFiador(pyme, JuridicaCuit);
        await SeedAsync(h, [pyme], [fiador]);

        AssertCodedProblem(await h.Sut.LinkFiadorLegajoAsync(pyme.EntityId, fiador.FiadorId, CancellationToken.None),
            400, "fiador_not_persona_humana");

        Assert.Empty(await h.Db.PersonLegajos.AsNoTracking().ToListAsync());
        Assert.Null((await ReloadFiadorAsync(h, fiador.FiadorId)).PersonLegajoId);
    }

    [Fact]
    public async Task RemovedFiador_409_FiadorNotActive()
    {
        using var h = BuildHarness();
        var pyme = Pyme();
        var fiador = ManualFiador(pyme, status: "Removed");
        await SeedAsync(h, [pyme], [fiador]);

        AssertCodedProblem(await h.Sut.LinkFiadorLegajoAsync(pyme.EntityId, fiador.FiadorId, CancellationToken.None),
            409, "fiador_not_active");

        Assert.Empty(await h.Db.PersonLegajos.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task FiadorWithThePymesOwnCuit_409_FiadorIsSolicitante()
    {
        using var h = BuildHarness();
        var ownLegajo = LegajoFor(FiadorCuit);
        var humanaPyme = Pyme(FiadorCuit, legajo: ownLegajo);
        var fiador = ManualFiador(humanaPyme);
        await SeedAsync(h, [humanaPyme], [fiador], [ownLegajo]);

        AssertCodedProblem(await h.Sut.LinkFiadorLegajoAsync(humanaPyme.EntityId, fiador.FiadorId, CancellationToken.None),
            409, "fiador_is_solicitante");

        Assert.Null((await ReloadFiadorAsync(h, fiador.FiadorId)).PersonLegajoId);
        Assert.Empty(h.Legajos.LinkSaves);
    }

    [Fact]
    public async Task UnknownFiador_404()
    {
        using var h = BuildHarness();
        var pyme = Pyme();
        await SeedAsync(h, [pyme], []);

        var obj = Assert.IsType<ObjectResult>(await h.Sut.LinkFiadorLegajoAsync(pyme.EntityId, Guid.NewGuid(), CancellationToken.None));
        Assert.Equal(404, obj.StatusCode);
    }

    [Fact]
    public async Task FiadorOfAnotherPyme_404_AndNothingIsWritten()
    {
        using var h = BuildHarness();
        var pymeA = Pyme();
        var pymeB = Pyme("30-60001234-1");
        var fiadorOfA = ManualFiador(pymeA);
        await SeedAsync(h, [pymeA, pymeB], [fiadorOfA]);

        var obj = Assert.IsType<ObjectResult>(await h.Sut.LinkFiadorLegajoAsync(pymeB.EntityId, fiadorOfA.FiadorId, CancellationToken.None));

        Assert.Equal(404, obj.StatusCode);
        Assert.Empty(await h.Db.PersonLegajos.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task FiadorOfAnotherTenant_404()
    {
        using var h = BuildHarness();
        var foreignPyme = Pyme(tenantId: OtherTenant);
        var foreignFiador = ManualFiador(foreignPyme);
        await SeedAsync(h, [foreignPyme], [foreignFiador]);

        var obj = Assert.IsType<ObjectResult>(await h.Sut.LinkFiadorLegajoAsync(foreignPyme.EntityId, foreignFiador.FiadorId, CancellationToken.None));

        Assert.Equal(404, obj.StatusCode);
        Assert.Null((await ReloadFiadorAsync(h, foreignFiador.FiadorId)).PersonLegajoId);
    }

    // ── Reads and PII ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetFiadores_CarriesThePersonLegajoId_OnceLinked()
    {
        using var h = BuildHarness();
        var pyme = Pyme();
        var linked = ManualFiador(pyme);
        var unlinked = ManualFiador(pyme, "27-11223344-5");
        await SeedAsync(h, [pyme], [linked, unlinked]);

        var link = AssertLink(await h.Sut.LinkFiadorLegajoAsync(pyme.EntityId, linked.FiadorId, CancellationToken.None), 201);
        var list = Assert.IsAssignableFrom<IEnumerable<FiadorResponse>>(
            Assert.IsType<OkObjectResult>(await h.Sut.GetFiadoresAsync(pyme.EntityId, CancellationToken.None)).Value).ToList();

        Assert.Equal(link.PersonLegajoId, list.Single(f => f.FiadorId == linked.FiadorId).PersonLegajoId);
        Assert.Null(list.Single(f => f.FiadorId == unlinked.FiadorId).PersonLegajoId);
    }

    [Fact]
    public async Task Logs_CarryIdsOnly_NeverTheCuitOrTheName()
    {
        using var h = BuildHarness();
        var pyme = Pyme();
        var fiador = ManualFiador(pyme);
        var juridica = ManualFiador(pyme, JuridicaCuit);
        await SeedAsync(h, [pyme], [fiador, juridica]);

        await h.Sut.LinkFiadorLegajoAsync(pyme.EntityId, fiador.FiadorId, CancellationToken.None);
        await h.Sut.LinkFiadorLegajoAsync(pyme.EntityId, fiador.FiadorId, CancellationToken.None);
        await h.Sut.LinkFiadorLegajoAsync(pyme.EntityId, juridica.FiadorId, CancellationToken.None);

        Assert.NotEmpty(h.Logger.Messages);
        foreach (var message in h.Logger.Messages)
        {
            Assert.DoesNotContain(LegajoTestData.LegajoCuitDigits, message);
            Assert.DoesNotContain(FiadorCuit, message);
            Assert.DoesNotContain(JuridicaCuit, message);
            Assert.DoesNotContain(PymeCuit, message);
            Assert.DoesNotContain("SINTETICO", message);
        }
    }
}
