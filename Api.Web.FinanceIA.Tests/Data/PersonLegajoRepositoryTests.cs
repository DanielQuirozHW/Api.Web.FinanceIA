// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-27

using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// <see cref="PersonLegajoRepository"/> — the legajo of a persona humana (DDL 069) — plus the
/// lost-race mapping it shares with <see cref="EntityRepository.CreateAsync"/> and
/// <see cref="EntityRepository.UpdateAsync"/>, over the EF Core in-memory provider (a database
/// provider, not a mocking library).
/// </summary>
/// <remarks>
/// THREE THINGS ARE LOCKED DOWN HERE, each one a way a natural person's data could go wrong.
///
/// <para>
/// TENANT SCOPING. A legajo is keyed by (TenantId, CUIT) and the same person can legitimately have
/// a legajo in two tenants. <see cref="FinanciaDbContext"/> has no global query filter, so every
/// read on this type is shown to ignore another tenant's row — including the case where both
/// tenants hold the same CUIT, which would also make the <c>SingleOrDefault</c> throw if the tenant
/// predicate were ever dropped.
/// </para>
///
/// <para>
/// ONE SAVECHANGES PER WRITE. SQL Server runs one SaveChanges as one transaction, which is what
/// makes an entity and its legajo land together or not at all. The in-memory provider has no
/// transactions, so atomicity itself cannot be shown here; what CAN be shown, and is, is its
/// precondition: a counting <see cref="SaveChangesInterceptor"/> proves every combination of new
/// and existing rows reaches the database in exactly one save, and that a guard failure reaches
/// it in none.
/// </para>
///
/// <para>
/// THE RACE MAPPING. The in-memory provider does not enforce unique indexes (the limitation
/// <c>AnalysisApprovalVoteRepositoryTests</c> records for its own constraint), so a lost race cannot
/// happen for real here. It is simulated instead: an armed interceptor throws the
/// <see cref="DbUpdateException"/> SQL Server would, its inner message shaped exactly like error
/// 2601 ("… with unique index 'X'") or 2627 ("Violation of UNIQUE KEY constraint 'X'") and ending in
/// "The duplicate key value is (tenant, CUIT)". The repository must turn that into a
/// <see cref="DuplicateCuitException"/> of the right scope that carries NONE of the provider text —
/// the CUIL of a natural person would otherwise reach ErrorLogs.StackTrace — and must detach its
/// entries so the controller can re-read and retry on the same request-scoped context. Anything that
/// is not one of the three per-tenant CUIT indexes must propagate untouched.
/// </para>
///
/// <para>
/// NOT COVERED HERE: the INSERT ORDER that the modelled FK_Entities_PersonLegajos buys (legajo before
/// entity). Command ordering is a relational-provider concern the in-memory provider never exercises;
/// the FK itself is pinned by the mapping tests.
/// </para>
/// </remarks>
public sealed class PersonLegajoRepositoryTests
{
    private const string Tenant = "tenant-1";
    private const string Other  = "tenant-2";

    // Synthetic CUITs, stored hyphenated exactly as [dbo].[Entities].CUIT stores them.
    private const string PersonCuit        = "20-11111111-2";
    private const string SecondPersonCuit  = "27-22222222-3";
    private const string CompanyCuit       = "30-12345678-9";
    private const string SecondCompanyCuit = "50-12345678-1";

    private const string PersonName  = "PERSONA DE PRUEBA";
    private const string CompanyName = "EMPRESA DE PRUEBA SA";

    // The three per-tenant CUIT indexes of DDL 069, spelled out rather than read from UniqueViolation
    // so a rename on either side is caught here instead of silently agreeing with itself.
    private const string PersonLegajoIndex = "UX_PersonLegajos_TenantId_CUIT";
    private const string EntityCuitIndex   = "UQ_Entities_TenantId_CUIT";
    private const string EntityLegajoIndex = "UX_Entities_PersonLegajoId";

    // "The duplicate key value is (…)": the tenant and the CUIT for the two CUIT indexes, the legajo
    // id for the filtered one.
    private const string PersonKey  = "tenant-1, 20-11111111-2";
    private const string CompanyKey = "tenant-1, 30-12345678-9";
    private const string LegajoKey  = "6b1f0e2a-0000-4000-8000-000000000069";

    /// <summary>The digit bodies of every synthetic CUIT above: none may leave the repository.</summary>
    private static readonly string[] CuitBodies = ["11111111", "22222222", "12345678"];

    private static readonly DateTime Created = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Later   = new(2026, 9, 27, 9, 30, 0, DateTimeKind.Utc);

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static PersonLegajo Legajo(
        string cuit = PersonCuit,
        string tenantId = Tenant,
        string fiscalRegime = FiscalRegimes.Desconocido,
        string? category = null) => new()
    {
        PersonLegajoId      = Guid.NewGuid(),
        TenantId            = tenantId,
        CUIT                = cuit,
        FullName            = PersonName,
        FiscalRegime        = fiscalRegime,
        MonotributoCategory = category,
        CreatedBy           = Guid.NewGuid(),
        CreatedAt           = Created,
        UpdatedAt           = Created,
    };

    /// <summary>A Humana entity already pointing at <paramref name="legajo"/>, in its tenant, with its CUIT.</summary>
    private static Entity HumanaEntity(PersonLegajo legajo) => new()
    {
        EntityId       = Guid.NewGuid(),
        TenantId       = legajo.TenantId,
        CUIT           = legajo.CUIT,
        PersonType     = PersonTypes.Humana,
        FiscalRegime   = legajo.FiscalRegime,
        PersonLegajoId = legajo.PersonLegajoId,
        BusinessName   = PersonName,
        CreatedAt      = Created,
        UpdatedAt      = Created,
    };

    private static Entity CompanyEntity(string cuit = CompanyCuit) => new()
    {
        EntityId     = Guid.NewGuid(),
        TenantId     = Tenant,
        CUIT         = cuit,
        PersonType   = PersonTypes.Juridica,
        BusinessName = CompanyName,
        CreatedAt    = Created,
        UpdatedAt    = Created,
    };

    /// <summary>
    /// The provider exception a lost race produces: EF's generic outer message around SQL Server's
    /// inner one, in either of its two forms (2601 for a unique index, 2627 for a UNIQUE constraint).
    /// </summary>
    private static DbUpdateException Violation(string indexName, string table, string keyValue, bool asConstraint)
    {
        var providerMessage = asConstraint
            ? $"Violation of UNIQUE KEY constraint '{indexName}'. Cannot insert duplicate key in object 'dbo.{table}'. The duplicate key value is ({keyValue})."
            : $"Cannot insert duplicate key row in object 'dbo.{table}' with unique index '{indexName}'. The duplicate key value is ({keyValue}).";

        return new DbUpdateException(
            "An error occurred while saving the entity changes. See the inner exception for details.",
            new Exception(providerMessage));
    }

    /// <summary>Any other failure: the inner message names no per-tenant CUIT index, or there is no inner exception at all.</summary>
    private static DbUpdateException Unrelated(string? providerMessage)
        => providerMessage is null
            ? new DbUpdateException("An error occurred while saving the entity changes.")
            : new DbUpdateException(
                "An error occurred while saving the entity changes. See the inner exception for details.",
                new Exception(providerMessage));

    /// <summary>Failures that must NOT be mistaken for a lost CUIT race, whatever their wording.</summary>
    public static TheoryData<string?> UnrelatedProviderMessages => new()
    {
        // Another table's CUIT index: a CUIT in the message is not enough, the NAME decides.
        "Cannot insert duplicate key row in object 'dbo.Fiadores' with unique index 'UX_Fiadores_ActivePerEntityCuit'. The duplicate key value is (tenant-1, 20-11111111-2).",
        // A primary-key collision says "duplicate key" too, and is a bug, not a race.
        "Violation of PRIMARY KEY constraint 'PK_PersonLegajos'. Cannot insert duplicate key in object 'dbo.PersonLegajos'. The duplicate key value is (6b1f0e2a-0000-4000-8000-000000000069).",
        "The INSERT statement conflicted with the FOREIGN KEY constraint \"FK_Entities_PersonLegajos\". The conflict occurred in database \"financia\", table \"dbo.PersonLegajos\", column 'PersonLegajoId'.",
        "The INSERT statement conflicted with the CHECK constraint \"CK_PersonLegajos_FiscalRegime\". The conflict occurred in database \"financia\", table \"dbo.PersonLegajos\", column 'FiscalRegime'.",
        null,
    };

    private static void AssertCarriesNoProviderText(DuplicateCuitException ex, string keyValue)
    {
        Assert.Null(ex.InnerException);

        // ToString() is what GlobalExceptionHandler would persist into ErrorLogs.StackTrace.
        foreach (var text in new[] { ex.Message, ex.ToString() })
        {
            AssertNoPii(text);
            Assert.DoesNotContain("duplicate key", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(keyValue, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void AssertNoPii(string text)
    {
        foreach (var body in CuitBodies)
        {
            Assert.DoesNotContain(body, text, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(PersonName, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(CompanyName, text, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertLogsCarryNoPii<T>(RecordingLogger<T> logger)
    {
        foreach (var entry in logger.Entries)
        {
            AssertNoPii(entry.Message);
            Assert.DoesNotContain("duplicate key", entry.Message, StringComparison.OrdinalIgnoreCase);
            // The provider exception is never handed to the logger: its message holds the CUIL.
            Assert.Null(entry.Exception);
        }
    }

    // ── GetByCuitAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetByCuit_TheTenantsLegajo_IsFoundByTheHyphenatedCuit_Untracked()
    {
        using var h = new Harness();
        var legajo = Legajo();
        await h.SeedAsync(legajo);

        var result = await h.Legajos.GetByCuitAsync(PersonCuit, Tenant, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(legajo.PersonLegajoId, result.PersonLegajoId);
        Assert.Equal(PersonCuit, result.CUIT);
        // AsNoTracking: SaveEntityWithLegajoAsync later Update()s this very instance, which would
        // collide with a tracked copy of the same key.
        Assert.Empty(h.Ctx.ChangeTracker.Entries());
    }

    /// <summary>THE tenant test for the CUIT read: another tenant's legajo for the same person never leaves the database.</summary>
    [Fact]
    public async Task GetByCuit_AnotherTenantsLegajoWithTheSameCuit_ReturnsNull()
    {
        using var h = new Harness();
        await h.SeedAsync(Legajo(tenantId: Other));

        Assert.Null(await h.Legajos.GetByCuitAsync(PersonCuit, Tenant, CancellationToken.None));
    }

    /// <summary>
    /// The same person in two tenants is legitimate (the index is per tenant). Without the tenant
    /// predicate this read would not merely leak — its SingleOrDefault would throw.
    /// </summary>
    [Fact]
    public async Task GetByCuit_SameCuitInBothTenants_ReturnsOnlyThisTenantsLegajo()
    {
        using var h = new Harness();
        var mine   = Legajo();
        var theirs = Legajo(tenantId: Other);
        await h.SeedAsync(mine, theirs);

        var result = await h.Legajos.GetByCuitAsync(PersonCuit, Tenant, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(mine.PersonLegajoId, result.PersonLegajoId);
        Assert.Equal(Tenant, result.TenantId);
    }

    /// <summary>
    /// A plain string equality against the stored hyphenated form: the caller must normalise first,
    /// exactly as it does for <see cref="IEntityRepository.GetByCuitAsync"/>.
    /// </summary>
    [Fact]
    public async Task GetByCuit_UnhyphenatedCuit_ReturnsNull()
    {
        using var h = new Harness();
        await h.SeedAsync(Legajo());

        Assert.Null(await h.Legajos.GetByCuitAsync("20111111112", Tenant, CancellationToken.None));
    }

    [Fact]
    public async Task GetByCuit_OtherPersonInTheSameTenant_ReturnsNull()
    {
        using var h = new Harness();
        await h.SeedAsync(Legajo(cuit: SecondPersonCuit));

        Assert.Null(await h.Legajos.GetByCuitAsync(PersonCuit, Tenant, CancellationToken.None));
    }

    [Theory]
    [InlineData(null, Tenant)]
    [InlineData("", Tenant)]
    [InlineData("   ", Tenant)]
    [InlineData(PersonCuit, null)]
    [InlineData(PersonCuit, "")]
    [InlineData(PersonCuit, "   ")]
    public async Task GetByCuit_BlankCuitOrTenant_ThrowsArgumentException(string? cuit, string? tenantId)
    {
        using var h = new Harness();
        await h.SeedAsync(Legajo());

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => h.Legajos.GetByCuitAsync(cuit!, tenantId!, CancellationToken.None));
    }

    // ── GetByIdAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetById_TheTenantsLegajo_IsFound()
    {
        using var h = new Harness();
        var legajo = Legajo(fiscalRegime: FiscalRegimes.Monotributo, category: "C");
        await h.SeedAsync(legajo);

        var result = await h.Legajos.GetByIdAsync(legajo.PersonLegajoId, Tenant, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(PersonCuit, result.CUIT);
        Assert.Equal(FiscalRegimes.Monotributo, result.FiscalRegime);
        Assert.Equal("C", result.MonotributoCategory);
        Assert.Empty(h.Ctx.ChangeTracker.Entries());
    }

    /// <summary>The id is globally unique, so only the tenant predicate stops it reaching across tenants.</summary>
    [Fact]
    public async Task GetById_AnotherTenantsId_ReturnsNull()
    {
        using var h = new Harness();
        var theirs = Legajo(tenantId: Other);
        await h.SeedAsync(theirs);

        Assert.Null(await h.Legajos.GetByIdAsync(theirs.PersonLegajoId, Tenant, CancellationToken.None));
    }

    [Fact]
    public async Task GetById_UnknownId_ReturnsNull()
    {
        using var h = new Harness();
        await h.SeedAsync(Legajo());

        Assert.Null(await h.Legajos.GetByIdAsync(Guid.NewGuid(), Tenant, CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ReadsById_BlankTenant_ThrowArgumentException(string? tenantId)
    {
        using var h = new Harness();
        var legajo = Legajo();
        await h.SeedAsync(legajo);

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => h.Legajos.GetByIdAsync(legajo.PersonLegajoId, tenantId!, CancellationToken.None));
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => h.Legajos.GetMonotributoCategoryAsync(legajo.PersonLegajoId, tenantId!, CancellationToken.None));
    }

    // ── GetMonotributoCategoryAsync ───────────────────────────────────────────

    [Fact]
    public async Task GetMonotributoCategory_ReturnsTheLegajosCategory()
    {
        using var h = new Harness();
        var legajo = Legajo(fiscalRegime: FiscalRegimes.Monotributo, category: "C");
        await h.SeedAsync(legajo);

        Assert.Equal("C", await h.Legajos.GetMonotributoCategoryAsync(
            legajo.PersonLegajoId, Tenant, CancellationToken.None));
    }

    /// <summary>The response paths call this with an id read off an entity; another tenant's legajo must answer nothing.</summary>
    [Fact]
    public async Task GetMonotributoCategory_AnotherTenantsLegajo_ReturnsNull()
    {
        using var h = new Harness();
        var theirs = Legajo(tenantId: Other, fiscalRegime: FiscalRegimes.Monotributo, category: "C");
        await h.SeedAsync(theirs);

        Assert.Null(await h.Legajos.GetMonotributoCategoryAsync(
            theirs.PersonLegajoId, Tenant, CancellationToken.None));
    }

    [Fact]
    public async Task GetMonotributoCategory_UnknownId_ReturnsNull()
    {
        using var h = new Harness();
        await h.SeedAsync(Legajo(fiscalRegime: FiscalRegimes.Monotributo, category: "C"));

        Assert.Null(await h.Legajos.GetMonotributoCategoryAsync(Guid.NewGuid(), Tenant, CancellationToken.None));
    }

    [Fact]
    public async Task GetMonotributoCategory_LegajoWithoutCategory_ReturnsNull()
    {
        using var h = new Harness();
        var legajo = Legajo(fiscalRegime: FiscalRegimes.ResponsableInscripto);
        await h.SeedAsync(legajo);

        Assert.Null(await h.Legajos.GetMonotributoCategoryAsync(
            legajo.PersonLegajoId, Tenant, CancellationToken.None));
    }

    // ── SaveEntityWithLegajoAsync — the four combinations ─────────────────────

    /// <summary>The Humana create with no prior legajo: both rows, one save, the entity pointing at the legajo.</summary>
    [Fact]
    public async Task SaveEntityWithLegajo_NewEntityAndNewLegajo_PersistsBothInOneSaveChanges()
    {
        using var h = new Harness();
        var legajo = Legajo(fiscalRegime: FiscalRegimes.Monotributo, category: "B");
        var entity = HumanaEntity(legajo);

        await h.Legajos.SaveEntityWithLegajoAsync(
            entity, entityIsNew: true, legajo, legajoIsNew: true, CancellationToken.None);

        Assert.Equal(1, h.Probe.SavingCalls);
        Assert.Equal(2, Assert.Single(h.Probe.RowsWritten));

        var storedLegajo = Assert.Single(await h.Ctx.PersonLegajos.AsNoTracking().ToListAsync());
        var storedEntity = Assert.Single(await h.Ctx.Entities.AsNoTracking().ToListAsync());
        Assert.Equal(legajo.PersonLegajoId, storedEntity.PersonLegajoId);
        Assert.Equal(PersonTypes.Humana, storedEntity.PersonType);
        Assert.Equal(Tenant, storedLegajo.TenantId);
        Assert.Equal(PersonCuit, storedLegajo.CUIT); // hyphenated, verbatim — the same form as Entities.CUIT
        Assert.Equal(storedEntity.CUIT, storedLegajo.CUIT);
        Assert.Equal("B", storedLegajo.MonotributoCategory);
    }

    /// <summary>
    /// The Humana create for a person the tenant already knows. The legajo arrives as the controller
    /// holds it — read AsNoTracking, then merged into — and must be UPDATED in place: a second
    /// legajo row for the same (tenant, CUIT) is exactly what UX_PersonLegajos_TenantId_CUIT forbids.
    /// </summary>
    [Fact]
    public async Task SaveEntityWithLegajo_NewEntityAndExistingLegajo_InsertsTheEntity_UpdatesTheLegajo()
    {
        using var h = new Harness();
        var seeded = Legajo();
        await h.SeedAsync(seeded);

        var legajo = await h.Legajos.GetByCuitAsync(PersonCuit, Tenant, CancellationToken.None);
        Assert.NotNull(legajo);
        legajo.FiscalRegime        = FiscalRegimes.Monotributo;
        legajo.MonotributoCategory = "C";
        legajo.UpdatedAt           = Later;
        var entity = HumanaEntity(legajo);

        await h.Legajos.SaveEntityWithLegajoAsync(
            entity, entityIsNew: true, legajo, legajoIsNew: false, CancellationToken.None);

        Assert.Equal(1, h.Probe.SavingCalls);
        Assert.Equal(2, Assert.Single(h.Probe.RowsWritten));

        var storedLegajo = Assert.Single(await h.Ctx.PersonLegajos.AsNoTracking().ToListAsync());
        Assert.Equal(seeded.PersonLegajoId, storedLegajo.PersonLegajoId);
        Assert.Equal(FiscalRegimes.Monotributo, storedLegajo.FiscalRegime);
        Assert.Equal("C", storedLegajo.MonotributoCategory);
        Assert.Equal(Later, storedLegajo.UpdatedAt);
        Assert.Equal(Created, storedLegajo.CreatedAt);

        var storedEntity = Assert.Single(await h.Ctx.Entities.AsNoTracking().ToListAsync());
        Assert.Equal(entity.EntityId, storedEntity.EntityId);
        Assert.Equal(seeded.PersonLegajoId, storedEntity.PersonLegajoId);
    }

    /// <summary>The refresh paths (ARCA, BCRA): an existing entity and its existing legajo, both updated by the one save.</summary>
    [Fact]
    public async Task SaveEntityWithLegajo_ExistingEntityAndExistingLegajo_UpdatesBoth()
    {
        using var h = new Harness();
        var seededLegajo = Legajo();
        var seededEntity = HumanaEntity(seededLegajo);
        await h.SeedAsync(seededLegajo, seededEntity);

        var entity = await h.Entities.GetByIdAsync(seededEntity.EntityId, Tenant, CancellationToken.None);
        var legajo = await h.Legajos.GetByIdAsync(seededLegajo.PersonLegajoId, Tenant, CancellationToken.None);
        Assert.NotNull(entity);
        Assert.NotNull(legajo);
        Assert.Empty(h.Ctx.ChangeTracker.Entries());

        const string bcraJson = """{"periodos":[]}""";
        entity.FiscalRegime       = FiscalRegimes.ResponsableInscripto;
        entity.BcraDataJson       = bcraJson;
        entity.BcraDataUpdatedAt  = Later;
        entity.UpdatedAt          = Later;
        legajo.FiscalRegime       = FiscalRegimes.ResponsableInscripto;
        legajo.BcraDataJson       = bcraJson;
        legajo.BcraDataUpdatedAt  = Later;
        legajo.UpdatedAt          = Later;

        await h.Legajos.SaveEntityWithLegajoAsync(
            entity, entityIsNew: false, legajo, legajoIsNew: false, CancellationToken.None);

        Assert.Equal(1, h.Probe.SavingCalls);
        Assert.Equal(2, Assert.Single(h.Probe.RowsWritten));

        var storedEntity = Assert.Single(await h.Ctx.Entities.AsNoTracking().ToListAsync());
        var storedLegajo = Assert.Single(await h.Ctx.PersonLegajos.AsNoTracking().ToListAsync());
        Assert.Equal(FiscalRegimes.ResponsableInscripto, storedEntity.FiscalRegime);
        Assert.Equal(FiscalRegimes.ResponsableInscripto, storedLegajo.FiscalRegime);
        Assert.Equal(bcraJson, storedEntity.BcraDataJson);
        Assert.Equal(bcraJson, storedLegajo.BcraDataJson);
        Assert.Equal(Later, storedLegajo.BcraDataUpdatedAt);
        Assert.Equal(seededLegajo.PersonLegajoId, storedEntity.PersonLegajoId);
    }

    /// <summary>
    /// D8: the repository's own log lines carry ids and the tenant, never the CUIL or the person's
    /// name — a natural person's identity is PII.
    /// </summary>
    [Fact]
    public async Task SaveEntityWithLegajo_LogsNeitherTheCuitNorTheName()
    {
        using var h = new Harness();
        var legajo = Legajo();

        await h.Legajos.SaveEntityWithLegajoAsync(
            HumanaEntity(legajo), entityIsNew: true, legajo, legajoIsNew: true, CancellationToken.None);

        Assert.NotEmpty(h.LegajoLogger.Entries);
        AssertLogsCarryNoPii(h.LegajoLogger);
    }

    // ── SaveEntityWithLegajoAsync — guards ────────────────────────────────────

    /// <summary>Defence in depth: nothing may link an entity to another tenant's legajo, and the guard fires before anything is tracked.</summary>
    [Fact]
    public async Task SaveEntityWithLegajo_LegajoOfAnotherTenant_ThrowsArgumentException_AndSavesNothing()
    {
        using var h = new Harness();
        var legajo = Legajo(tenantId: Other);
        var entity = HumanaEntity(legajo);
        entity.TenantId = Tenant;

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => h.Legajos.SaveEntityWithLegajoAsync(
            entity, entityIsNew: true, legajo, legajoIsNew: true, CancellationToken.None));

        Assert.Equal("legajo", ex.ParamName);
        Assert.Equal(0, h.Probe.SavingCalls);
        Assert.Empty(h.Ctx.ChangeTracker.Entries());
        Assert.Empty(await h.Ctx.PersonLegajos.AsNoTracking().ToListAsync());
        Assert.Empty(await h.Ctx.Entities.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// The entity must already carry the legajo's key: the modelled FK is what orders the legajo
    /// INSERT first, and an unlinked pair would write a legajo nobody points at.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SaveEntityWithLegajo_EntityNotPointingAtTheLegajo_ThrowsArgumentException_AndSavesNothing(
        bool pointsNowhere)
    {
        using var h = new Harness();
        var legajo = Legajo();
        var entity = HumanaEntity(legajo);
        entity.PersonLegajoId = pointsNowhere ? null : Guid.NewGuid();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => h.Legajos.SaveEntityWithLegajoAsync(
            entity, entityIsNew: true, legajo, legajoIsNew: true, CancellationToken.None));

        Assert.Equal("entity", ex.ParamName);
        Assert.Equal(0, h.Probe.SavingCalls);
        Assert.Empty(h.Ctx.ChangeTracker.Entries());
        Assert.Empty(await h.Ctx.PersonLegajos.AsNoTracking().ToListAsync());
        Assert.Empty(await h.Ctx.Entities.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task SaveEntityWithLegajo_NullArguments_ThrowArgumentNullException()
    {
        using var h = new Harness();
        var legajo = Legajo();
        var entity = HumanaEntity(legajo);

        await Assert.ThrowsAsync<ArgumentNullException>(() => h.Legajos.SaveEntityWithLegajoAsync(
            null!, entityIsNew: true, legajo, legajoIsNew: true, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => h.Legajos.SaveEntityWithLegajoAsync(
            entity, entityIsNew: true, null!, legajoIsNew: true, CancellationToken.None));

        Assert.Equal(0, h.Probe.SavingCalls);
    }

    // ── SaveEntityWithLegajoAsync — the lost race ─────────────────────────────

    /// <summary>
    /// Each of the three per-tenant CUIT indexes, in both of SQL Server's message forms, maps to its
    /// scope — the scope is what tells the controller whether to re-read and relink (PersonLegajo) or
    /// answer 409 (Entity). The exception carries no provider text, no log line does either, and the
    /// failed entries are detached.
    /// </summary>
    [Theory]
    [InlineData(PersonLegajoIndex, "PersonLegajos", PersonKey, false, DuplicateCuitScope.PersonLegajo)]
    [InlineData(PersonLegajoIndex, "PersonLegajos", PersonKey, true,  DuplicateCuitScope.PersonLegajo)]
    [InlineData(EntityCuitIndex,   "Entities",      PersonKey, false, DuplicateCuitScope.Entity)]
    [InlineData(EntityCuitIndex,   "Entities",      PersonKey, true,  DuplicateCuitScope.Entity)]
    [InlineData(EntityLegajoIndex, "Entities",      LegajoKey, false, DuplicateCuitScope.Entity)]
    [InlineData(EntityLegajoIndex, "Entities",      LegajoKey, true,  DuplicateCuitScope.Entity)]
    public async Task SaveEntityWithLegajo_LostRaceOnACuitIndex_ThrowsDuplicateCuitException_WithThatScope(
        string indexName, string table, string keyValue, bool asConstraint, DuplicateCuitScope expectedScope)
    {
        using var h = new Harness();
        var legajo = Legajo();
        var entity = HumanaEntity(legajo);
        h.Probe.ArmFailure(Violation(indexName, table, keyValue, asConstraint));

        var ex = await Assert.ThrowsAsync<DuplicateCuitException>(() => h.Legajos.SaveEntityWithLegajoAsync(
            entity, entityIsNew: true, legajo, legajoIsNew: true, CancellationToken.None));

        Assert.Equal(expectedScope, ex.Scope);
        AssertCarriesNoProviderText(ex, keyValue);
        AssertLogsCarryNoPii(h.LegajoLogger);

        Assert.Empty(h.Ctx.ChangeTracker.Entries());
        Assert.Equal(EntityState.Detached, h.Ctx.Entry(entity).State);
        Assert.Equal(EntityState.Detached, h.Ctx.Entry(legajo).State);

        Assert.Equal(1, h.Probe.SavingCalls);
        Assert.Empty(h.Probe.RowsWritten);
        Assert.Empty(await h.Ctx.PersonLegajos.AsNoTracking().ToListAsync());
        Assert.Empty(await h.Ctx.Entities.AsNoTracking().ToListAsync());
    }

    /// <summary>The race seen from the update side: an existing legajo, relinked by a new entity, loses on an Entities index.</summary>
    [Fact]
    public async Task SaveEntityWithLegajo_LostRaceWhileUpdatingAnExistingLegajo_DetachesBoth_LeavesTheStoredLegajoAlone()
    {
        using var h = new Harness();
        var seeded = Legajo();
        await h.SeedAsync(seeded);

        var legajo = await h.Legajos.GetByCuitAsync(PersonCuit, Tenant, CancellationToken.None);
        Assert.NotNull(legajo);
        legajo.MonotributoCategory = "K";
        var entity = HumanaEntity(legajo);
        h.Probe.ArmFailure(Violation(EntityLegajoIndex, "Entities", LegajoKey, asConstraint: false));

        var ex = await Assert.ThrowsAsync<DuplicateCuitException>(() => h.Legajos.SaveEntityWithLegajoAsync(
            entity, entityIsNew: true, legajo, legajoIsNew: false, CancellationToken.None));

        Assert.Equal(DuplicateCuitScope.Entity, ex.Scope);
        Assert.Empty(h.Ctx.ChangeTracker.Entries());

        var stored = Assert.Single(await h.Ctx.PersonLegajos.AsNoTracking().ToListAsync());
        Assert.Null(stored.MonotributoCategory);
        Assert.Empty(await h.Ctx.Entities.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// WHY the entries are detached. After a lost legajo race the controller re-reads the winner's
    /// legajo and retries on the SAME request-scoped context. Were this request's own legajo still
    /// tracked as Added, the retry would insert it again beside the winner's — a second legajo for
    /// the same person, the very thing the index exists to prevent.
    /// </summary>
    [Fact]
    public async Task SaveEntityWithLegajo_AfterALostLegajoRace_TheSameContextRetriesCleanly()
    {
        using var h = new Harness();
        var winner = Legajo(); // what the concurrent request inserted first
        await h.SeedAsync(winner);

        var loser  = Legajo(); // this request's own, same (tenant, CUIT)
        var entity = HumanaEntity(loser);
        h.Probe.ArmFailure(Violation(PersonLegajoIndex, "PersonLegajos", PersonKey, asConstraint: false));

        var race = await Assert.ThrowsAsync<DuplicateCuitException>(() => h.Legajos.SaveEntityWithLegajoAsync(
            entity, entityIsNew: true, loser, legajoIsNew: true, CancellationToken.None));
        Assert.Equal(DuplicateCuitScope.PersonLegajo, race.Scope);

        var reread = await h.Legajos.GetByCuitAsync(PersonCuit, Tenant, CancellationToken.None);
        Assert.NotNull(reread);
        entity.PersonLegajoId = reread.PersonLegajoId;
        await h.Legajos.SaveEntityWithLegajoAsync(
            entity, entityIsNew: true, reread, legajoIsNew: false, CancellationToken.None);

        var storedLegajo = Assert.Single(await h.Ctx.PersonLegajos.AsNoTracking().ToListAsync());
        Assert.Equal(winner.PersonLegajoId, storedLegajo.PersonLegajoId);
        var storedEntity = Assert.Single(await h.Ctx.Entities.AsNoTracking().ToListAsync());
        Assert.Equal(winner.PersonLegajoId, storedEntity.PersonLegajoId);
        Assert.DoesNotContain(h.Ctx.ChangeTracker.Entries(), e => e.State is EntityState.Added or EntityState.Modified);
    }

    /// <summary>Only the three CUIT indexes are a race. Everything else is somebody's bug and must surface as itself.</summary>
    [Theory]
    [MemberData(nameof(UnrelatedProviderMessages))]
    public async Task SaveEntityWithLegajo_UnrelatedDbUpdateException_PropagatesUnchanged(string? providerMessage)
    {
        using var h = new Harness();
        var legajo  = Legajo();
        var failure = Unrelated(providerMessage);
        h.Probe.ArmFailure(failure);

        var thrown = await Assert.ThrowsAsync<DbUpdateException>(() => h.Legajos.SaveEntityWithLegajoAsync(
            HumanaEntity(legajo), entityIsNew: true, legajo, legajoIsNew: true, CancellationToken.None));

        Assert.Same(failure, thrown);
    }

    // ── EntityRepository.CreateAsync — the same race mapping ─────────────────

    /// <summary>The Juridica create keeps CreateAsync, and gets the same Entity-scope 409 on either Entities index.</summary>
    [Theory]
    [InlineData(EntityCuitIndex,   CompanyKey, false)]
    [InlineData(EntityCuitIndex,   CompanyKey, true)]
    [InlineData(EntityLegajoIndex, LegajoKey,  false)]
    [InlineData(EntityLegajoIndex, LegajoKey,  true)]
    public async Task EntityCreate_LostRaceOnAnEntitiesIndex_ThrowsDuplicateCuitException_WithEntityScope(
        string indexName, string keyValue, bool asConstraint)
    {
        using var h = new Harness();
        var entity = CompanyEntity();
        h.Probe.ArmFailure(Violation(indexName, "Entities", keyValue, asConstraint));

        var ex = await Assert.ThrowsAsync<DuplicateCuitException>(
            () => h.Entities.CreateAsync(entity, CancellationToken.None));

        Assert.Equal(DuplicateCuitScope.Entity, ex.Scope);
        AssertCarriesNoProviderText(ex, keyValue);
        AssertLogsCarryNoPii(h.EntityLogger);

        Assert.Empty(h.Ctx.ChangeTracker.Entries());
        Assert.Equal(EntityState.Detached, h.Ctx.Entry(entity).State);
        Assert.Empty(await h.Ctx.Entities.AsNoTracking().ToListAsync());
    }

    [Theory]
    [MemberData(nameof(UnrelatedProviderMessages))]
    public async Task EntityCreate_UnrelatedDbUpdateException_PropagatesUnchanged(string? providerMessage)
    {
        using var h = new Harness();
        var failure = Unrelated(providerMessage);
        h.Probe.ArmFailure(failure);

        var thrown = await Assert.ThrowsAsync<DbUpdateException>(
            () => h.Entities.CreateAsync(CompanyEntity(), CancellationToken.None));

        Assert.Same(failure, thrown);
    }

    // ── EntityRepository.UpdateAsync — the same race mapping ─────────────────

    /// <summary>A PUT that moves an entity onto a CUIT another request just claimed: 409, not 500, and the stored row untouched.</summary>
    [Theory]
    [InlineData(EntityCuitIndex,   CompanyKey, false)]
    [InlineData(EntityCuitIndex,   CompanyKey, true)]
    [InlineData(EntityLegajoIndex, LegajoKey,  false)]
    [InlineData(EntityLegajoIndex, LegajoKey,  true)]
    public async Task EntityUpdate_LostRaceOnAnEntitiesIndex_ThrowsDuplicateCuitException_WithEntityScope(
        string indexName, string keyValue, bool asConstraint)
    {
        using var h = new Harness();
        var seeded = CompanyEntity(SecondCompanyCuit);
        await h.SeedAsync(seeded);

        var entity = await h.Entities.GetByIdAsync(seeded.EntityId, Tenant, CancellationToken.None);
        Assert.NotNull(entity);
        entity.CUIT      = CompanyCuit;
        entity.UpdatedAt = Later;
        h.Probe.ArmFailure(Violation(indexName, "Entities", keyValue, asConstraint));

        var ex = await Assert.ThrowsAsync<DuplicateCuitException>(
            () => h.Entities.UpdateAsync(entity, CancellationToken.None));

        Assert.Equal(DuplicateCuitScope.Entity, ex.Scope);
        AssertCarriesNoProviderText(ex, keyValue);
        AssertLogsCarryNoPii(h.EntityLogger);

        Assert.Empty(h.Ctx.ChangeTracker.Entries());
        Assert.Equal(EntityState.Detached, h.Ctx.Entry(entity).State);
        var stored = Assert.Single(await h.Ctx.Entities.AsNoTracking().ToListAsync());
        Assert.Equal(SecondCompanyCuit, stored.CUIT);
        Assert.Equal(Created, stored.UpdatedAt);
    }

    [Theory]
    [MemberData(nameof(UnrelatedProviderMessages))]
    public async Task EntityUpdate_UnrelatedDbUpdateException_PropagatesUnchanged(string? providerMessage)
    {
        using var h = new Harness();
        var seeded = CompanyEntity();
        await h.SeedAsync(seeded);

        var entity = await h.Entities.GetByIdAsync(seeded.EntityId, Tenant, CancellationToken.None);
        Assert.NotNull(entity);
        entity.UpdatedAt = Later;
        var failure = Unrelated(providerMessage);
        h.Probe.ArmFailure(failure);

        var thrown = await Assert.ThrowsAsync<DbUpdateException>(
            () => h.Entities.UpdateAsync(entity, CancellationToken.None));

        Assert.Same(failure, thrown);
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    /// <summary>
    /// One in-memory database per test (fresh name), one context standing in for the request scope,
    /// and both repositories over it. <see cref="SeedAsync"/> clears the change tracker afterwards,
    /// as a fresh request would find it, and resets the probe so only the code under test is counted.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        public Harness()
        {
            Probe = new SaveProbe();

            var options = new DbContextOptionsBuilder<FinanciaDbContext>()
                .UseInMemoryDatabase($"person-legajo-{Guid.NewGuid()}")
                .AddInterceptors(Probe)
                .Options;

            Ctx          = new FinanciaDbContext(options);
            LegajoLogger = new RecordingLogger<PersonLegajoRepository>();
            EntityLogger = new RecordingLogger<EntityRepository>();
            Legajos      = new PersonLegajoRepository(Ctx, LegajoLogger);
            Entities     = new EntityRepository(Ctx, EntityLogger);
        }

        public FinanciaDbContext Ctx { get; }

        public SaveProbe Probe { get; }

        public RecordingLogger<PersonLegajoRepository> LegajoLogger { get; }

        public RecordingLogger<EntityRepository> EntityLogger { get; }

        public PersonLegajoRepository Legajos { get; }

        public EntityRepository Entities { get; }

        public async Task SeedAsync(params object[] rows)
        {
            Ctx.AddRange(rows);
            Ctx.ChangeTracker.DetectChanges(); // AutoDetectChanges is disabled on the context
            await Ctx.SaveChangesAsync();
            Ctx.ChangeTracker.Clear();
            Probe.Reset();
        }

        public void Dispose() => Ctx.Dispose();
    }

    /// <summary>
    /// Counts SaveChanges calls (attempted and completed, with the rows each wrote) and, when armed,
    /// throws the given exception from the next save before anything is written — the in-memory
    /// stand-in for SQL Server rejecting the batch. Disarms itself after one throw.
    /// </summary>
    private sealed class SaveProbe : SaveChangesInterceptor
    {
        private Exception? _armedFailure;

        public int SavingCalls { get; private set; }

        public List<int> RowsWritten { get; } = [];

        public void ArmFailure(Exception failure) => _armedFailure = failure;

        public void Reset()
        {
            SavingCalls = 0;
            RowsWritten.Clear();
            _armedFailure = null;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            SavingCalls++;

            if (_armedFailure is { } failure)
            {
                _armedFailure = null;
                throw failure;
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            RowsWritten.Add(result);
            return base.SavedChangesAsync(eventData, result, cancellationToken);
        }

        // The repositories must save asynchronously; a synchronous save would be a bug of its own.
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
            => throw new NotImplementedException("Synchronous SaveChanges is not expected.");
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    /// <summary>Captures every entry, at every level, with the exception it was handed (if any).</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
    }
}
