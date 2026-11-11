// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-27

using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// Covers the SQL-side entity NAME read: <see cref="EntityRepository.GetNameByIdAsync"/>, the
/// single-id form of <c>GetNameLookupByIdsAsync</c> that both now compose off one shared
/// <c>BuildName</c>.
///
/// <para>
/// The Portal's breadcrumb hook runs in the app shell, OUTSIDE the page transition, so it is a
/// SECOND read on every navigation to a PyME detail — and it filled the crumb by calling GET
/// /api/entities/{id} purely to read <c>businessName</c>. Even after #143 made three snapshots lazy,
/// that detail read still names FiscalDataJson and LufeDataJson, and LUFE alone is ~26KB on a
/// representative PyME. A breadcrumb was paying for two <c>nvarchar(max)</c> columns and eleven
/// scalars it never renders.
/// </para>
///
/// <para>
/// The load-bearing test is <see cref="NameByIdQuery_SqlNamesOnlyTheNameAndCuit"/>. Its negative half
/// is the point — proving the emitted T-SQL names none of the five source snapshots is what proves
/// the endpoint is actually light, since SQL Server reads a column off disk whether or not anything
/// deserialises it. It asserts against the SQL EF emits for the PRODUCTION query, not a re-declared
/// copy, hence the internal seam on the repository.
/// </para>
///
/// <para>
/// <c>GetByIdAsync</c> and <c>GetDetailByIdAsync</c> are deliberately NOT changed and are not under
/// test here: the detail page renders far more than a crumb, and every <c>refresh-*</c> and update
/// path hands the whole entity to <c>UpdateAsync</c>.
/// </para>
/// </summary>
public sealed class EntityRepositoryNameTests
{
    private const string Tenant = "tenant-1";
    private const string Other  = "tenant-2";

    private readonly ITestOutputHelper _output;

    public EntityRepositoryNameTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// The two strings the label renders, and the person type the alerts list badges (one short
    /// constrained column, CK_Entities_PersonType), so the query must read them.
    /// </summary>
    private static readonly string[] NameColumns =
    [
        "BusinessName", "CUIT", "PersonType"
    ];

    /// <summary>
    /// Every other column on the row. None of them is rendered by a breadcrumb, so naming any of them
    /// would put its I/O straight back. The five blobs are the ones this read exists to avoid; the
    /// scalars are here because a projection that quietly widened would show up as a failure rather
    /// than as a slow endpoint nobody noticed.
    /// <para>
    /// <c>EntityId</c> and <c>TenantId</c> are deliberately absent from this list — both legitimately
    /// appear, EntityId in the projection as the dictionary key the by-ids lookup needs, TenantId in
    /// the WHERE clause that isolates the tenant.
    /// </para>
    /// </summary>
    private static readonly string[] ForbiddenColumns =
    [
        // The five source snapshots. FiscalDataJson and LufeDataJson are the two the DETAIL still
        // reads and this label does not render; the other three have been lazy since #143.
        "FiscalDataJson", "LufeDataJson", "BcraDataJson", "CasfogDataJson", "NosisDataJson",
        // The uploaded NOSIS Manager report (DDL 087) and its two dates.
        "NosisReportJson", "NosisReportDate", "NosisReportImportedAt",
        // Their refresh timestamps — free to read, but nothing here shows "last refreshed".
        "FiscalDataUpdatedAt", "LufeDataUpdatedAt", "BcraDataUpdatedAt",
        "CasfogDataUpdatedAt", "NosisDataUpdatedAt",
        // Scalars the label does not use.
        "TradeName", "Sector", "MipymeCategory", "SgrOneClickEntityId",
        "RiskScore", "RiskScoreUpdatedAt", "IsMonitored", "CreatedAt", "UpdatedAt", "CreatedBy"
    ];

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static FinanciaDbContext NewInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"entity-name-{Guid.NewGuid()}")
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

    /// <summary>
    /// Every column populated with a distinguishable value — all five snapshots included — so a
    /// projection that leaked one would be visible rather than merely absent.
    /// </summary>
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

    // ── Behaviour ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The three columns come back exactly as stored. Both strings are NOT NULL in the database, so
    /// there is no "exists but has no name" state to represent — unlike the snapshot reads, a
    /// materialised instance always carries both values.
    /// </summary>
    [Fact]
    public async Task GetNameById_ReturnsBusinessNameAndCuit()
    {
        var entityId = Guid.NewGuid();
        using var ctx = NewInMemoryContext();
        await SeedAsync(ctx, Ent(entityId));

        var name = await Repo(ctx).GetNameByIdAsync(entityId, Tenant, CancellationToken.None);

        Assert.NotNull(name);
        Assert.Equal(entityId,                   name.EntityId);
        Assert.Equal("Metalúrgica del Sur S.A.", name.BusinessName);
        Assert.Equal("30-71234567-1",            name.CUIT);
    }

    /// <summary>A missing entity is a null, which the controller turns into a 404.</summary>
    [Fact]
    public async Task GetNameById_UnknownId_ReturnsNull()
    {
        using var ctx = NewInMemoryContext();
        await SeedAsync(ctx, Ent(Guid.NewGuid()));

        Assert.Null(await Repo(ctx).GetNameByIdAsync(Guid.NewGuid(), Tenant, CancellationToken.None));
    }

    /// <summary>
    /// Another tenant's entity is not readable, even by exact id. The predicate composed here is the
    /// same <c>EntityByIdSource</c> the detail and the three snapshot reads compose, so this cannot
    /// drift away from them — but a name is exactly the kind of field a leak would expose, so it is
    /// asserted rather than assumed.
    /// </summary>
    [Fact]
    public async Task GetNameById_IsTenantIsolated()
    {
        var entityId = Guid.NewGuid();
        using var ctx = NewInMemoryContext();
        await SeedAsync(ctx, Ent(entityId, Other));

        Assert.Null(await Repo(ctx).GetNameByIdAsync(entityId, Tenant, CancellationToken.None));
    }

    /// <summary>
    /// The lookup that already existed still behaves identically after being rewritten to compose the
    /// shared <c>BuildName</c>. Same projection, same tenant isolation, same 1:1 map keyed by id.
    /// </summary>
    [Fact]
    public async Task GetNameLookupByIds_StillMapsOnlyThisTenantsEntities()
    {
        var mine    = Guid.NewGuid();
        var theirs  = Guid.NewGuid();
        using var ctx = NewInMemoryContext();
        await SeedAsync(ctx, Ent(mine), Ent(theirs, Other));

        var map = await Repo(ctx).GetNameLookupByIdsAsync([mine, theirs], Tenant, CancellationToken.None);

        Assert.Equal("Metalúrgica del Sur S.A.", Assert.Contains(mine, map).BusinessName);
        Assert.DoesNotContain(theirs, map);
    }

    // ── The query itself ──────────────────────────────────────────────────────

    /// <summary>
    /// THE test for this change. The emitted T-SQL must name BusinessName and CUIT and NOTHING else
    /// off the row — none of the five source snapshots, none of their timestamps, none of the
    /// scalars the detail carries. If it named a blob SQL Server would read that column off disk
    /// whether or not anything deserialises it, which is precisely the cost this read exists to
    /// avoid; a passing controller test would not notice.
    /// </summary>
    [Fact]
    public void NameByIdQuery_SqlNamesOnlyTheNameAndCuit()
    {
        using var ctx = NewSqlServerContext();

        var sql = Repo(ctx).NameByIdQuery(Guid.NewGuid(), Tenant).ToQueryString();

        _output.WriteLine(sql);

        foreach (var needed in NameColumns)
            Assert.Contains(needed, sql, StringComparison.Ordinal);

        foreach (var forbidden in ForbiddenColumns)
            Assert.DoesNotContain(forbidden, sql, StringComparison.Ordinal);
    }

    // ── Shape lock ────────────────────────────────────────────────────────────

    /// <summary>
    /// The shape lock, mirroring <c>AnalysisRatiosProjection_ExposesExactlyThreeColumns</c>. This
    /// projection is now shared by two reads, so adding a property here would widen BOTH SELECTs at
    /// once — the by-ids lookup on the analyses and alerts list paths as well as the label.
    /// <para>
    /// PersonType was added deliberately for the Alertas redesign (entityIsPersonaHumana): a short
    /// nvarchar the CHECK constrains to two values, not a snapshot, so widening every caller by it costs
    /// nothing worth a second projection. Anything wider still needs its own read.
    /// </para>
    /// </summary>
    [Fact]
    public void EntityNameProjection_ExposesExactlyFourColumns()
    {
        var actual = typeof(EntityNameProjection)
            .GetProperties()
            .Select(p => p.Name)
            .ToHashSet();

        var expected = new HashSet<string>
        {
            nameof(EntityNameProjection.EntityId),
            nameof(EntityNameProjection.BusinessName),
            nameof(EntityNameProjection.CUIT),
            nameof(EntityNameProjection.PersonType)
        };

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// The wire shape. Property names must match <see cref="EntityResponse"/> exactly — that is what
    /// lets the Portal swap the URL and keep reading <c>businessName</c> — so the set is pinned and
    /// the types are checked against the detail DTO's own properties rather than restated.
    /// </summary>
    [Fact]
    public void EntityLabelResponse_ExposesExactlyTheTwoFieldsTheDetailAlreadyNames()
    {
        var actual = typeof(EntityLabelResponse)
            .GetProperties()
            .Select(p => p.Name)
            .ToHashSet();

        var expected = new HashSet<string>
        {
            nameof(EntityLabelResponse.BusinessName),
            nameof(EntityLabelResponse.CUIT)
        };

        Assert.Equal(expected, actual);

        // …and they are the same types the detail response declares, so the JSON is identical.
        Assert.Equal(
            typeof(EntityResponse).GetProperty(nameof(EntityResponse.BusinessName))!.PropertyType,
            typeof(EntityLabelResponse).GetProperty(nameof(EntityLabelResponse.BusinessName))!.PropertyType);
        Assert.Equal(
            typeof(EntityResponse).GetProperty(nameof(EntityResponse.CUIT))!.PropertyType,
            typeof(EntityLabelResponse).GetProperty(nameof(EntityLabelResponse.CUIT))!.PropertyType);
    }
}
