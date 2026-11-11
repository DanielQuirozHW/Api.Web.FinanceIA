// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-25

using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// Covers the SQL-side entity DETAIL read: <see cref="EntityRepository.GetDetailByIdAsync"/> and the
/// three on-demand snapshot reads.
///
/// <para>
/// The list path got this treatment in #138 (stop deserialising) and #140 (stop selecting). The
/// detail path got neither: <c>GetByIdAsync</c> is a full-row load, so GET /api/entities/{id} read
/// all five <c>nvarchar(max)</c> snapshots off disk and parsed them — for a view that renders FISCAL
/// and LUFE on load and shows BCRA, CASFOG and NOSIS only when the user opens their tab. NOSIS and
/// CASFOG are the heaviest of the five.
/// </para>
///
/// <para>
/// The load-bearing test is <see cref="DetailQuery_SqlNeverNamesTheThreeLazySnapshots"/>. Its
/// positive half matters as much as its negative half: proving FiscalDataJson and LufeDataJson are
/// still selected is what distinguishes "we made the detail lazy" from "we gutted the detail". It
/// asserts against the SQL EF actually emits for the production query, not a re-declared copy —
/// hence the internal seams on the repository.
/// </para>
///
/// <para>
/// <c>GetByIdAsync</c> is deliberately NOT changed and is not under test here: every
/// <c>refresh-*</c> action and the update path mutate the entity and hand it to
/// <c>UpdateAsync</c>, which marks every property modified, so those callers need the whole row.
/// </para>
/// </summary>
public sealed class EntityRepositoryDetailTests
{
    private const string Tenant = "tenant-1";
    private const string Other  = "tenant-2";

    private readonly ITestOutputHelper _output;

    public EntityRepositoryDetailTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// The three snapshots the DETAIL query must never read, plus the uploaded NOSIS Manager report
    /// (DDL 087), which only its own repository reads.
    /// </summary>
    private static readonly string[] LazySnapshotColumns =
    [
        "BcraDataJson", "CasfogDataJson", "NosisDataJson", "NosisReportJson"
    ];

    /// <summary>The two the Resumen tab renders on load, so the detail query must still read them.</summary>
    private static readonly string[] ResumenSnapshotColumns =
    [
        "FiscalDataJson", "LufeDataJson"
    ];

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static FinanciaDbContext NewInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"entity-detail-{Guid.NewGuid()}")
            .Options;
        return new FinanciaDbContext(options);
    }

    /// <summary>
    /// A context on the SqlServer provider so the query can be compiled to real T-SQL. The server
    /// does not exist and is never contacted: ToQueryString() builds the command text and stops.
    /// </summary>
    private static FinanciaDbContext NewSqlServerContext()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseSqlServer("Server=localhost;Database=none;Trusted_Connection=False;")
            .Options;
        return new FinanciaDbContext(options);
    }

    private static EntityRepository Repo(FinanciaDbContext ctx)
        => new(ctx, NullLogger<EntityRepository>.Instance);

    private static DateTime Utc(int day) => new(2026, 3, day, 10, 0, 0, DateTimeKind.Utc);

    private static Entity Ent(Guid entityId, string tenantId = Tenant) => new()
    {
        EntityId            = entityId,
        TenantId            = tenantId,
        CUIT                = "30-71234567-1",
        BusinessName        = "Metalúrgica del Sur S.A.",
        TradeName           = "MetalSur",
        Sector              = "Manufactura",
        MipymeCategory      = "Pequeña",
        SgrOneClickEntityId = "SGR-99",
        RiskScore           = 7.5m,
        RiskScoreUpdatedAt  = Utc(6),
        IsMonitored         = true,
        CreatedAt           = Utc(1),
        UpdatedAt           = Utc(2),
        CreatedBy           = Guid.NewGuid(),
        // All five populated, so a projection that leaked one would be visible.
        FiscalDataJson      = """{"a":1}""",
        FiscalDataUpdatedAt = Utc(1),
        LufeDataJson        = """{"b":2}""",
        LufeDataUpdatedAt   = Utc(2),
        BcraDataJson        = """{"c":3}""",
        BcraDataUpdatedAt   = Utc(3),
        CasfogDataJson      = """{"d":4}""",
        CasfogDataUpdatedAt = Utc(4),
        NosisDataJson       = """{"e":5}""",
        NosisDataUpdatedAt  = Utc(5)
    };

    private static async Task SeedAsync(FinanciaDbContext ctx, params Entity[] rows)
    {
        ctx.Entities.AddRange(rows);
        ctx.ChangeTracker.DetectChanges(); // AutoDetectChanges is disabled on the context
        await ctx.SaveChangesAsync();
    }

    // ── Detail behaviour ──────────────────────────────────────────────────────

    /// <summary>
    /// The projection carries every scalar the detail DTO renders, plus the two Resumen snapshots
    /// and all three lazy timestamps. Nothing GetById needs is silently dropped.
    /// </summary>
    [Fact]
    public async Task GetDetailById_CarriesEveryColumnTheDetailRenders()
    {
        var id = Guid.NewGuid();
        using var ctx = NewInMemoryContext();
        var seeded = Ent(id);
        await SeedAsync(ctx, seeded);

        var detail = await Repo(ctx).GetDetailByIdAsync(id, Tenant, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(id,                     detail.EntityId);
        Assert.Equal(Tenant,                 detail.TenantId);
        Assert.Equal("30-71234567-1",        detail.CUIT);
        Assert.Equal("Metalúrgica del Sur S.A.", detail.BusinessName);
        Assert.Equal("MetalSur",             detail.TradeName);
        Assert.Equal("Manufactura",          detail.Sector);
        Assert.Equal("Pequeña",              detail.MipymeCategory);
        Assert.Equal("SGR-99",               detail.SgrOneClickEntityId);
        Assert.Equal(7.5m,                   detail.RiskScore);
        Assert.Equal(Utc(6),                 detail.RiskScoreUpdatedAt);
        Assert.True(detail.IsMonitored);
        Assert.Equal(Utc(1),                 detail.CreatedAt);
        Assert.Equal(Utc(2),                 detail.UpdatedAt);
        Assert.Equal(seeded.CreatedBy,       detail.CreatedBy);

        // The two the Resumen tab renders.
        Assert.Equal("""{"a":1}""",          detail.FiscalDataJson);
        Assert.Equal("""{"b":2}""",          detail.LufeDataJson);
        Assert.Equal(Utc(1),                 detail.FiscalDataUpdatedAt);
        Assert.Equal(Utc(2),                 detail.LufeDataUpdatedAt);

        // The three lazy ones: timestamp only, and the timestamps are real.
        Assert.Equal(Utc(3),                 detail.BcraDataUpdatedAt);
        Assert.Equal(Utc(4),                 detail.CasfogDataUpdatedAt);
        Assert.Equal(Utc(5),                 detail.NosisDataUpdatedAt);
    }

    /// <summary>
    /// A persona humana's detail carries its person type, regime and legajo id off the entity, and
    /// the monotributo category off the linked legajo (DDL 069).
    /// </summary>
    [Fact]
    public async Task GetDetailById_Humana_ReadsTheCategoryFromItsLegajo()
    {
        var id       = Guid.NewGuid();
        var legajoId = Guid.NewGuid();
        using var ctx = NewInMemoryContext();

        ctx.PersonLegajos.Add(Legajo(legajoId, Tenant, category: "H"));
        var entity = Ent(id);
        entity.CUIT           = "20-11111111-2";
        entity.PersonType     = PersonTypes.Humana;
        entity.FiscalRegime   = FiscalRegimes.Monotributo;
        entity.PersonLegajoId = legajoId;
        await SeedAsync(ctx, entity);

        var detail = await Repo(ctx).GetDetailByIdAsync(id, Tenant, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(PersonTypes.Humana,        detail!.PersonType);
        Assert.Equal(FiscalRegimes.Monotributo, detail.FiscalRegime);
        Assert.Equal(legajoId,                  detail.PersonLegajoId);
        Assert.Equal("H",                       detail.MonotributoCategory);
    }

    /// <summary>
    /// The legajo subquery repeats the tenant predicate: even a (corrupt) link to another tenant's
    /// legajo can never surface that legajo's data under this tenant's entity.
    /// </summary>
    [Fact]
    public async Task GetDetailById_NeverReadsAnotherTenantsLegajo()
    {
        var id       = Guid.NewGuid();
        var legajoId = Guid.NewGuid();
        using var ctx = NewInMemoryContext();

        ctx.PersonLegajos.Add(Legajo(legajoId, Other, category: "K"));
        var entity = Ent(id);
        entity.PersonType     = PersonTypes.Humana;
        entity.PersonLegajoId = legajoId;
        await SeedAsync(ctx, entity);

        var detail = await Repo(ctx).GetDetailByIdAsync(id, Tenant, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Null(detail!.MonotributoCategory);
    }

    /// <summary>A Juridica has no legajo, so no category — and the default person type.</summary>
    [Fact]
    public async Task GetDetailById_Juridica_HasNoLegajoFields()
    {
        var id = Guid.NewGuid();
        using var ctx = NewInMemoryContext();
        await SeedAsync(ctx, Ent(id));

        var detail = await Repo(ctx).GetDetailByIdAsync(id, Tenant, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(PersonTypes.Juridica, detail!.PersonType);
        Assert.Null(detail.PersonLegajoId);
        Assert.Null(detail.MonotributoCategory);
    }

    private static PersonLegajo Legajo(Guid legajoId, string tenantId, string? category) => new()
    {
        PersonLegajoId      = legajoId,
        TenantId            = tenantId,
        CUIT                = "20-11111111-2",
        FullName            = "PERSONA DE PRUEBA",
        FiscalRegime        = FiscalRegimes.Monotributo,
        MonotributoCategory = category,
        ArcaSnapshotJson    = """{"x":1}""",
        BcraDataJson        = """{"y":2}""",
        CreatedAt           = Utc(1),
        UpdatedAt           = Utc(1)
    };

    /// <summary>An entity belonging to another tenant is not visible, even by exact id.</summary>
    [Fact]
    public async Task GetDetailById_IsTenantIsolated()
    {
        var id = Guid.NewGuid();
        using var ctx = NewInMemoryContext();
        await SeedAsync(ctx, Ent(id, Other));

        Assert.Null(await Repo(ctx).GetDetailByIdAsync(id, Tenant, CancellationToken.None));
    }

    /// <summary>An id that does not exist is a null, which the controller turns into a 404.</summary>
    [Fact]
    public async Task GetDetailById_UnknownId_ReturnsNull()
    {
        using var ctx = NewInMemoryContext();
        await SeedAsync(ctx, Ent(Guid.NewGuid()));

        Assert.Null(await Repo(ctx).GetDetailByIdAsync(Guid.NewGuid(), Tenant, CancellationToken.None));
    }

    // ── Snapshot behaviour ────────────────────────────────────────────────────

    /// <summary>Each read returns its OWN source's blob and timestamp, not a neighbour's.</summary>
    [Fact]
    public async Task GetSnapshots_EachReturnsItsOwnSourceOnly()
    {
        var id = Guid.NewGuid();
        using var ctx = NewInMemoryContext();
        await SeedAsync(ctx, Ent(id));
        var repo = Repo(ctx);

        var bcra   = await repo.GetBcraSnapshotAsync(id, Tenant, CancellationToken.None);
        var casfog = await repo.GetCasfogSnapshotAsync(id, Tenant, CancellationToken.None);
        var nosis  = await repo.GetNosisSnapshotAsync(id, Tenant, CancellationToken.None);
        var lufe   = await repo.GetLufeSnapshotAsync(id, Tenant, CancellationToken.None);

        Assert.Equal("""{"c":3}""", bcra!.Json);
        Assert.Equal(Utc(3),        bcra.UpdatedAt);
        Assert.Equal(id,            bcra.EntityId);

        Assert.Equal("""{"d":4}""", casfog!.Json);
        Assert.Equal(Utc(4),        casfog.UpdatedAt);

        Assert.Equal("""{"e":5}""", nosis!.Json);
        Assert.Equal(Utc(5),        nosis.UpdatedAt);

        // The fiadores candidates read. It must return LufeDataJson/LufeDataUpdatedAt and not, say,
        // the FISCAL blob sitting next to it in the same projection shape.
        Assert.Equal("""{"b":2}""", lufe!.Json);
        Assert.Equal(Utc(2),        lufe.UpdatedAt);
        Assert.Equal(id,            lufe.EntityId);
    }

    /// <summary>
    /// The distinction the endpoints depend on: an entity that exists but has never had this source
    /// fetched returns an INSTANCE with a null Json — a 200 carrying null — not a null projection,
    /// which would be a 404. Projecting into a class rather than a tuple is what keeps these apart.
    /// </summary>
    [Fact]
    public async Task GetSnapshot_EntityExistsButSourceNeverFetched_ReturnsInstanceWithNullJson()
    {
        var id = Guid.NewGuid();
        using var ctx = NewInMemoryContext();
        var entity = Ent(id);
        entity.NosisDataJson      = null;
        entity.NosisDataUpdatedAt = null;
        await SeedAsync(ctx, entity);

        var nosis = await Repo(ctx).GetNosisSnapshotAsync(id, Tenant, CancellationToken.None);

        Assert.NotNull(nosis);        // the entity exists → 200
        Assert.Null(nosis.Json);      // ...but this source has never been fetched → null payload
        Assert.Null(nosis.UpdatedAt);
        Assert.Equal(id, nosis.EntityId);
    }

    /// <summary>A missing entity is a null from all four reads, which the controller turns into 404.</summary>
    [Fact]
    public async Task GetSnapshots_UnknownId_ReturnNull()
    {
        using var ctx = NewInMemoryContext();
        await SeedAsync(ctx, Ent(Guid.NewGuid()));
        var repo = Repo(ctx);
        var missing = Guid.NewGuid();

        Assert.Null(await repo.GetBcraSnapshotAsync(missing, Tenant, CancellationToken.None));
        Assert.Null(await repo.GetCasfogSnapshotAsync(missing, Tenant, CancellationToken.None));
        Assert.Null(await repo.GetNosisSnapshotAsync(missing, Tenant, CancellationToken.None));
        Assert.Null(await repo.GetLufeSnapshotAsync(missing, Tenant, CancellationToken.None));
    }

    /// <summary>Another tenant's snapshot is not readable, even by exact entity id.</summary>
    [Fact]
    public async Task GetSnapshots_AreTenantIsolated()
    {
        var id = Guid.NewGuid();
        using var ctx = NewInMemoryContext();
        await SeedAsync(ctx, Ent(id, Other));
        var repo = Repo(ctx);

        Assert.Null(await repo.GetBcraSnapshotAsync(id, Tenant, CancellationToken.None));
        Assert.Null(await repo.GetCasfogSnapshotAsync(id, Tenant, CancellationToken.None));
        Assert.Null(await repo.GetNosisSnapshotAsync(id, Tenant, CancellationToken.None));
        // The fiadores candidates read is the newest way into an entity, so it gets the same proof:
        // another tenant's LUFE blob is invisible even when the caller knows the exact entity id.
        Assert.Null(await repo.GetLufeSnapshotAsync(id, Tenant, CancellationToken.None));
    }

    // ── The queries themselves ────────────────────────────────────────────────

    /// <summary>
    /// THE test for this change. The emitted T-SQL for the detail read must not name BcraDataJson,
    /// CasfogDataJson or NosisDataJson — if it does, SQL Server reads them off disk whether or not
    /// anything deserialises them, which is the half-fix #138 left behind on the list.
    ///
    /// <para>
    /// It must still name FiscalDataJson and LufeDataJson, and all five <c>*DataUpdatedAt</c>: those
    /// are what keeps the detail correct rather than merely small.
    /// </para>
    /// </summary>
    [Fact]
    public void DetailQuery_SqlNeverNamesTheThreeLazySnapshots()
    {
        using var ctx = NewSqlServerContext();

        var sql = Repo(ctx).DetailQuery(Guid.NewGuid(), Tenant).ToQueryString();

        _output.WriteLine(sql);

        // "BcraDataJson" is not a substring of "BcraDataUpdatedAt" — they diverge at character 9 —
        // so an ordinal DoesNotContain is safe even though the timestamp IS selected.
        foreach (var lazy in LazySnapshotColumns)
            Assert.DoesNotContain(lazy, sql, StringComparison.Ordinal);

        foreach (var needed in ResumenSnapshotColumns)
            Assert.Contains(needed, sql, StringComparison.Ordinal);

        Assert.Contains("BcraDataUpdatedAt",   sql, StringComparison.Ordinal);
        Assert.Contains("CasfogDataUpdatedAt", sql, StringComparison.Ordinal);
        Assert.Contains("NosisDataUpdatedAt",  sql, StringComparison.Ordinal);

        // DDL 069: the persona humana fields ride along. The legajo is read for ONE column; the
        // LazySnapshotColumns loop above already proves its BcraDataJson / NosisDataJson (same names
        // as the entity's) are absent, and its own ARCA snapshot must be too.
        Assert.Contains("PersonType",          sql, StringComparison.Ordinal);
        Assert.Contains("MonotributoCategory", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ArcaSnapshotJson", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// Opening one tab must not pay for the other two. Each snapshot query names exactly its own
    /// blob and none of the other four.
    /// </summary>
    [Theory]
    [InlineData("BcraDataJson")]
    [InlineData("CasfogDataJson")]
    [InlineData("NosisDataJson")]
    public void SnapshotQuery_SelectsOnlyItsOwnBlob(string ownColumn)
    {
        using var ctx = NewSqlServerContext();
        var repo = Repo(ctx);
        var id = Guid.NewGuid();

        var sql = ownColumn switch
        {
            "BcraDataJson"   => repo.BcraSnapshotQuery(id, Tenant).ToQueryString(),
            "CasfogDataJson" => repo.CasfogSnapshotQuery(id, Tenant).ToQueryString(),
            _                => repo.NosisSnapshotQuery(id, Tenant).ToQueryString()
        };

        _output.WriteLine(sql);

        Assert.Contains(ownColumn, sql, StringComparison.Ordinal);

        var allSnapshots = LazySnapshotColumns.Concat(ResumenSnapshotColumns);
        foreach (var other in allSnapshots.Where(c => c != ownColumn))
            Assert.DoesNotContain(other, sql, StringComparison.Ordinal);
    }

    // ── Shape locks ───────────────────────────────────────────────────────────

    /// <summary>
    /// The shape lock, mirroring <c>EntityListProjection_ExposesExactlyTheColumnsTheListNeeds</c>.
    /// Adding a lazy snapshot here would put the column straight back into the SELECT above and
    /// silently reintroduce the defect, so the property set is pinned.
    /// </summary>
    [Fact]
    public void EntityDetailProjection_ExposesExactlyTheColumnsTheDetailNeeds()
    {
        var actual = typeof(EntityDetailProjection)
            .GetProperties()
            .Select(p => p.Name)
            .ToHashSet();

        var expected = new HashSet<string>
        {
            nameof(EntityDetailProjection.EntityId),
            nameof(EntityDetailProjection.TenantId),
            nameof(EntityDetailProjection.CUIT),
            nameof(EntityDetailProjection.BusinessName),
            nameof(EntityDetailProjection.TradeName),
            nameof(EntityDetailProjection.Sector),
            nameof(EntityDetailProjection.MipymeCategory),
            nameof(EntityDetailProjection.SgrOneClickEntityId),
            nameof(EntityDetailProjection.RiskScore),
            nameof(EntityDetailProjection.RiskScoreUpdatedAt),
            nameof(EntityDetailProjection.IsMonitored),
            nameof(EntityDetailProjection.CreatedAt),
            nameof(EntityDetailProjection.UpdatedAt),
            nameof(EntityDetailProjection.CreatedBy),
            nameof(EntityDetailProjection.FiscalDataJson),
            nameof(EntityDetailProjection.FiscalDataUpdatedAt),
            nameof(EntityDetailProjection.LufeDataJson),
            nameof(EntityDetailProjection.LufeDataUpdatedAt),
            nameof(EntityDetailProjection.BcraDataUpdatedAt),
            nameof(EntityDetailProjection.CasfogDataUpdatedAt),
            nameof(EntityDetailProjection.NosisDataUpdatedAt),
            // DDL 069: three scalars off the entity and ONE column of the legajo — never a snapshot.
            nameof(EntityDetailProjection.PersonType),
            nameof(EntityDetailProjection.FiscalRegime),
            nameof(EntityDetailProjection.PersonLegajoId),
            nameof(EntityDetailProjection.MonotributoCategory)
        };

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// The snapshot projection stays source-agnostic and three columns wide. A second blob property
    /// here would mean one tab paying for another's data.
    /// </summary>
    [Fact]
    public void EntitySnapshotProjection_ExposesExactlyThreeColumns()
    {
        var actual = typeof(EntitySnapshotProjection)
            .GetProperties()
            .Select(p => p.Name)
            .ToHashSet();

        var expected = new HashSet<string>
        {
            nameof(EntitySnapshotProjection.EntityId),
            nameof(EntitySnapshotProjection.Json),
            nameof(EntitySnapshotProjection.UpdatedAt)
        };

        Assert.Equal(expected, actual);
    }
}
