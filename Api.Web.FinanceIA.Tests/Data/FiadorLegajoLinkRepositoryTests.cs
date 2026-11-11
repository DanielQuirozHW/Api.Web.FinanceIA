// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-29

using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// The repository half of the fiador legajo link (M10 Phase 5):
/// <see cref="PersonLegajoRepository.SaveFiadorLegajoLinkAsync"/> writes ONLY Fiadores.PersonLegajoId
/// (never the analyst's name, cargo or the DDL 074 edit stamp), inserts a new legajo in the same save,
/// and turns a lost UX_PersonLegajos_TenantId_CUIT race into a <see cref="DuplicateCuitException"/>
/// with nothing left tracked; <see cref="PersonLegajoRepository.SaveLegajoSnapshotsAsync"/> writes the
/// snapshot columns only, so a consent recorded meanwhile survives; and the two fiador reads behind the
/// legajo upload and refresh are tenant-scoped and Active-only.
///
/// <para>
/// InMemory provider, like PersonLegajoRepositoryTests. It writes only the properties an entry marks
/// modified on an update, which is what lets a stale instance prove a column was NOT written; the
/// modified-property set itself is also captured by an interceptor, which is provider-independent.
/// Synthetic data throughout.
/// </para>
/// </summary>
public sealed class FiadorLegajoLinkRepositoryTests
{
    private const string Tenant      = "tenant-1";
    private const string OtherTenant = "tenant-2";
    private const string Cuit        = "20-12345678-6";

    /// <summary>Records, at SavingChanges, which properties of each Fiador / PersonLegajo entry are written.</summary>
    private sealed class WriteCapture : SaveChangesInterceptor
    {
        public List<(string Type, EntityState State, string[] Modified)> Writes { get; } = [];

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            foreach (var entry in eventData.Context!.ChangeTracker.Entries()
                         .Where(e => e.Entity is Fiador or PersonLegajo))
            {
                Writes.Add((entry.Entity.GetType().Name, entry.State,
                    entry.Properties.Where(p => p.IsModified).Select(p => p.Metadata.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray()));
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    /// <summary>Fails the next SaveChanges the way SQL Server does on UX_PersonLegajos_TenantId_CUIT.</summary>
    private sealed class UniqueViolationOnce : SaveChangesInterceptor
    {
        public bool Armed { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed)
            {
                Armed = false;
                throw new DbUpdateException(
                    "An error occurred while saving the entity changes. See the inner exception for details.",
                    new InvalidOperationException(
                        "Cannot insert duplicate key row in object 'dbo.PersonLegajos' with unique index " +
                        $"'UX_PersonLegajos_TenantId_CUIT'. The duplicate key value is ({Tenant}, {Cuit})."));
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private static (FinanciaDbContext Db, PersonLegajoRepository Legajos, FiadorRepository Fiadores, WriteCapture Capture, UniqueViolationOnce Race) Build()
    {
        var capture = new WriteCapture();
        var race    = new UniqueViolationOnce();
        var db = new FinanciaDbContext(new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"fiador-link-{Guid.NewGuid()}")
            .AddInterceptors(race, capture)
            .Options);

        return (db,
                new PersonLegajoRepository(db, NullLogger<PersonLegajoRepository>.Instance),
                new FiadorRepository(db, NullLogger<FiadorRepository>.Instance),
                capture, race);
    }

    private static Fiador NewFiador(string tenantId = Tenant, Guid? entityId = null, string status = "Active", Guid? legajoId = null) => new()
    {
        FiadorId        = Guid.NewGuid(),
        TenantId        = tenantId,
        EntityId        = entityId ?? Guid.NewGuid(),
        CUIT            = Cuit,
        Denominacion    = "NOMBRE GUARDADO",
        Cargo           = "Socio",
        Origen          = "Manual",
        Status          = status,
        PersonLegajoId  = legajoId,
        CreatedByUserId = Guid.NewGuid(),
        CreatedAt       = DateTime.UtcNow
    };

    private static PersonLegajo NewLegajo(string tenantId = Tenant) => new()
    {
        PersonLegajoId = Guid.NewGuid(),
        TenantId       = tenantId,
        CUIT           = Cuit,
        FullName       = "NOMBRE DEL LEGAJO",
        FiscalRegime   = FiscalRegimes.Desconocido,
        CreatedAt      = DateTime.UtcNow,
        UpdatedAt      = DateTime.UtcNow
    };

    private static async Task SeedAsync(FinanciaDbContext db, IEnumerable<object> rows)
    {
        db.AddRange(rows);
        db.ChangeTracker.DetectChanges();
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    // ── SaveFiadorLegajoLinkAsync ─────────────────────────────────────────────

    [Fact]
    public async Task Link_NewLegajo_InsertsIt_AndWritesOnlyTheFiadorsPersonLegajoId()
    {
        var (db, legajos, _, capture, _) = Build();
        using var _db = db;
        var fiador = NewFiador();
        await SeedAsync(db, [fiador]);
        capture.Writes.Clear();

        // A STALE instance: what the request read, with values a concurrent PATCH has since replaced.
        var stale = NewFiador();
        stale.FiadorId     = fiador.FiadorId;
        stale.EntityId     = fiador.EntityId;
        stale.Denominacion = "NOMBRE VIEJO";
        stale.UpdatedAt    = DateTime.UtcNow;
        var legajo = NewLegajo();
        stale.PersonLegajoId = legajo.PersonLegajoId;

        await legajos.SaveFiadorLegajoLinkAsync(stale, legajo, legajoIsNew: true, CancellationToken.None);

        Assert.Contains(capture.Writes, w => w.Type == nameof(PersonLegajo) && w.State == EntityState.Added);
        var fiadorWrite = Assert.Single(capture.Writes, w => w.Type == nameof(Fiador));
        Assert.Equal(EntityState.Modified, fiadorWrite.State);
        Assert.Equal([nameof(Fiador.PersonLegajoId)], fiadorWrite.Modified);

        var reloaded = await db.Fiadores.AsNoTracking().SingleAsync(f => f.FiadorId == fiador.FiadorId);
        Assert.Equal(legajo.PersonLegajoId, reloaded.PersonLegajoId);
        Assert.Equal("NOMBRE GUARDADO", reloaded.Denominacion);
        Assert.Null(reloaded.UpdatedAt);
        Assert.NotNull(await db.PersonLegajos.AsNoTracking().SingleOrDefaultAsync(l => l.PersonLegajoId == legajo.PersonLegajoId));
        Assert.Empty(db.ChangeTracker.Entries<Fiador>());
    }

    [Fact]
    public async Task Link_ExistingLegajo_IsNeverRewritten()
    {
        var (db, legajos, _, capture, _) = Build();
        using var _db = db;
        var legajo = NewLegajo();
        var fiador = NewFiador();
        await SeedAsync(db, [legajo, fiador]);
        capture.Writes.Clear();

        legajo.FullName = "OTRO NOMBRE";
        fiador.PersonLegajoId = legajo.PersonLegajoId;
        await legajos.SaveFiadorLegajoLinkAsync(fiador, legajo, legajoIsNew: false, CancellationToken.None);

        Assert.DoesNotContain(capture.Writes, w => w.Type == nameof(PersonLegajo));
        Assert.Equal("NOMBRE DEL LEGAJO", (await db.PersonLegajos.AsNoTracking().SingleAsync()).FullName);
        Assert.Equal(legajo.PersonLegajoId, (await db.Fiadores.AsNoTracking().SingleAsync()).PersonLegajoId);
    }

    [Fact]
    public async Task Link_RefusesAnotherTenantsLegajo_AndAFiadorNotPointingAtIt()
    {
        var (db, legajos, _, _, _) = Build();
        using var _db = db;

        var foreign = NewLegajo(OtherTenant);
        var fiador = NewFiador(legajoId: foreign.PersonLegajoId);
        await Assert.ThrowsAsync<ArgumentException>(
            () => legajos.SaveFiadorLegajoLinkAsync(fiador, foreign, legajoIsNew: false, CancellationToken.None));

        var legajo = NewLegajo();
        var unlinked = NewFiador();
        await Assert.ThrowsAsync<ArgumentException>(
            () => legajos.SaveFiadorLegajoLinkAsync(unlinked, legajo, legajoIsNew: true, CancellationToken.None));
    }

    [Fact]
    public async Task Link_LostInsertRace_ThrowsDuplicateCuit_AndLeavesNothingTracked()
    {
        var (db, legajos, _, _, race) = Build();
        using var _db = db;
        var fiador = NewFiador();
        await SeedAsync(db, [fiador]);

        var legajo = NewLegajo();
        fiador.PersonLegajoId = legajo.PersonLegajoId;
        race.Armed = true;

        var ex = await Assert.ThrowsAsync<DuplicateCuitException>(
            () => legajos.SaveFiadorLegajoLinkAsync(fiador, legajo, legajoIsNew: true, CancellationToken.None));

        Assert.Equal(DuplicateCuitScope.PersonLegajo, ex.Scope);
        Assert.Null(ex.InnerException);   // the provider message carries the CUIL
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Null((await db.Fiadores.AsNoTracking().SingleAsync()).PersonLegajoId);
    }

    // ── SaveLegajoSnapshotsAsync ──────────────────────────────────────────────

    [Fact]
    public async Task SaveSnapshots_WritesTheSnapshotColumnsOnly_SoAConcurrentConsentSurvives()
    {
        var (db, legajos, _, capture, _) = Build();
        using var _db = db;
        var seeded = NewLegajo();
        seeded.ConsentRecordedAt = DateTime.UtcNow;
        seeded.ConsentDocumentId = Guid.NewGuid();
        await SeedAsync(db, [seeded]);
        capture.Writes.Clear();

        // Read before the consent was recorded, refreshed afterwards.
        var stale = NewLegajo();
        stale.PersonLegajoId      = seeded.PersonLegajoId;
        stale.FullName            = "NOMBRE VIEJO";
        stale.BcraDataJson        = """{"Deuda":{}}""";
        stale.BcraDataUpdatedAt   = DateTime.UtcNow;
        stale.FiscalRegime        = FiscalRegimes.Monotributo;
        stale.MonotributoCategory = "D";

        await legajos.SaveLegajoSnapshotsAsync(stale, CancellationToken.None);

        var write = Assert.Single(capture.Writes);
        Assert.Equal(
            new[]
            {
                nameof(PersonLegajo.ActivityStartDate), nameof(PersonLegajo.ArcaDataUpdatedAt), nameof(PersonLegajo.ArcaSnapshotJson),
                nameof(PersonLegajo.BcraDataJson), nameof(PersonLegajo.BcraDataUpdatedAt), nameof(PersonLegajo.FiscalRegime),
                nameof(PersonLegajo.MonotributoCategory), nameof(PersonLegajo.UpdatedAt)
            },
            write.Modified);

        var reloaded = await db.PersonLegajos.AsNoTracking().SingleAsync();
        Assert.Equal(seeded.ConsentRecordedAt, reloaded.ConsentRecordedAt);
        Assert.Equal(seeded.ConsentDocumentId, reloaded.ConsentDocumentId);
        Assert.Equal("NOMBRE DEL LEGAJO", reloaded.FullName);
        Assert.Equal("""{"Deuda":{}}""", reloaded.BcraDataJson);
        Assert.Equal("D", reloaded.MonotributoCategory);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    // ── The fiador reads behind upload and refresh ────────────────────────────

    [Fact]
    public async Task HasActiveFiadorWithLegajo_IsScopedToEntityTenantAndActive()
    {
        var (db, _, fiadores, _, _) = Build();
        using var _db = db;
        var legajoId = Guid.NewGuid();
        var pyme = Guid.NewGuid();
        var otherPyme = Guid.NewGuid();
        await SeedAsync(db,
        [
            NewFiador(entityId: pyme, legajoId: legajoId),
            NewFiador(entityId: otherPyme, legajoId: legajoId, status: "Removed"),
            NewFiador(OtherTenant, entityId: otherPyme, legajoId: legajoId),
        ]);

        Assert.True(await fiadores.HasActiveFiadorWithLegajoAsync(pyme, legajoId, Tenant, CancellationToken.None));
        Assert.False(await fiadores.HasActiveFiadorWithLegajoAsync(otherPyme, legajoId, Tenant, CancellationToken.None));       // removed
        Assert.False(await fiadores.HasActiveFiadorWithLegajoAsync(pyme, Guid.NewGuid(), Tenant, CancellationToken.None));     // other legajo
        Assert.False(await fiadores.HasActiveFiadorWithLegajoAsync(pyme, legajoId, OtherTenant, CancellationToken.None));      // other tenant's view of pyme
        Assert.True(await fiadores.HasActiveFiadorWithLegajoAsync(otherPyme, legajoId, OtherTenant, CancellationToken.None));
    }

    [Fact]
    public async Task AnyActiveWithLegajo_IgnoresRemovedFiadoresAndOtherTenants()
    {
        var (db, _, fiadores, _, _) = Build();
        using var _db = db;
        var activeLegajo = Guid.NewGuid();
        var removedLegajo = Guid.NewGuid();
        await SeedAsync(db,
        [
            NewFiador(legajoId: activeLegajo),
            NewFiador(legajoId: removedLegajo, status: "Removed"),
        ]);

        Assert.True(await fiadores.AnyActiveWithLegajoAsync(activeLegajo, Tenant, CancellationToken.None));
        Assert.False(await fiadores.AnyActiveWithLegajoAsync(removedLegajo, Tenant, CancellationToken.None));
        Assert.False(await fiadores.AnyActiveWithLegajoAsync(activeLegajo, OtherTenant, CancellationToken.None));
    }
}
