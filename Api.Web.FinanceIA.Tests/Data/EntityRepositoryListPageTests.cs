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
/// Covers the SQL-side list page: <see cref="EntityRepository.GetListPageByTenantAsync"/>.
///
/// <para>
/// #138 stopped the list DESERIALISING the five source snapshots and shipping them — a page fell
/// from 375 KB to about 2.4 KB. It did not stop the query SELECTing them:
/// <c>.Where(tenant).ToListAsync()</c> still read FiscalDataJson, LufeDataJson, BcraDataJson,
/// CasfogDataJson and NosisDataJson off disk for every row in the tenant, after which the controller
/// paged the list in memory. Not parsing a blob you already paid to read saves the CPU and none of
/// the I/O, and the I/O was the 8–30s.
/// </para>
///
/// <para>
/// The load-bearing test is <see cref="ListPageQuery_SqlNeverNamesASourceSnapshot"/>. It is the
/// query-level counterpart to #138's <c>GetAll_MalformedSourceBlobs_SucceedAndNothingIsEverParsed</c>:
/// that one proved the mapper never parses the snapshots, this proves the SELECT never names them.
/// It asserts against the SQL EF actually emits for the production query, not a re-declared copy —
/// hence the internal seam on the repository.
/// </para>
/// </summary>
public sealed class EntityRepositoryListPageTests
{
    private const string Tenant = "tenant-1";
    private const string Other  = "tenant-2";

    private readonly ITestOutputHelper _output;

    public EntityRepositoryListPageTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// The five <c>nvarchar(max)</c> source snapshots, plus the uploaded NOSIS Manager report (DDL 087),
    /// that the list must never read.
    /// </summary>
    private static readonly string[] SourceSnapshotColumns =
    [
        "FiscalDataJson", "LufeDataJson", "BcraDataJson", "CasfogDataJson", "NosisDataJson",
        "NosisReportJson"
    ];

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static FinanciaDbContext NewInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"entity-listpage-{Guid.NewGuid()}")
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

    private static Entity Ent(DateTime createdAt, string tenantId = Tenant, Guid? entityId = null) => new()
    {
        EntityId     = entityId ?? Guid.NewGuid(),
        TenantId     = tenantId,
        CUIT         = $"30-{Random.Shared.Next(10000000, 99999999)}-1",
        BusinessName = "Metalúrgica del Sur S.A.",
        CreatedAt    = createdAt,
        UpdatedAt    = createdAt,
        // All five snapshots populated, so a projection that leaked one would be visible.
        FiscalDataJson = """{"a":1}""",
        LufeDataJson   = """{"b":2}""",
        BcraDataJson   = """{"c":3}""",
        CasfogDataJson = """{"d":4}""",
        NosisDataJson  = """{"e":5}"""
    };

    private static async Task SeedAsync(FinanciaDbContext ctx, params Entity[] rows)
    {
        ctx.Entities.AddRange(rows);
        ctx.ChangeTracker.DetectChanges(); // AutoDetectChanges is disabled on the context
        await ctx.SaveChangesAsync();
    }

    // ── Behaviour ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The page is newest-first and honours skip/take. Ordering is new: before this change the list
    /// had no ORDER BY at all and rows came back in whatever order SQL Server chose.
    /// </summary>
    [Fact]
    public async Task GetListPageByTenant_ReturnsTheRequestedPageNewestFirst()
    {
        using var ctx = NewInMemoryContext();
        await SeedAsync(ctx, Ent(Utc(1)), Ent(Utc(5)), Ent(Utc(3)), Ent(Utc(4)));

        var (items, _) = await Repo(ctx).GetListPageByTenantAsync(Tenant, skip: 1, take: 2, CancellationToken.None);

        Assert.Equal([Utc(4), Utc(3)], items.Select(i => i.CreatedAt));
    }

    /// <summary>TotalCount is the whole tenant set, not the page — it drives the pager.</summary>
    [Fact]
    public async Task GetListPageByTenant_TotalCount_CountsTheWholeSetNotThePage()
    {
        using var ctx = NewInMemoryContext();
        await SeedAsync(ctx, Ent(Utc(1)), Ent(Utc(2)), Ent(Utc(3)), Ent(Utc(4)), Ent(Utc(5)));

        var (items, total) = await Repo(ctx).GetListPageByTenantAsync(Tenant, skip: 0, take: 2, CancellationToken.None);

        Assert.Equal(2, items.Count);
        Assert.Equal(5, total);
    }

    /// <summary>Another tenant's entities are neither returned nor counted.</summary>
    [Fact]
    public async Task GetListPageByTenant_IsTenantIsolated()
    {
        using var ctx = NewInMemoryContext();
        await SeedAsync(ctx, Ent(Utc(1)), Ent(Utc(2), tenantId: Other), Ent(Utc(3), tenantId: Other));

        var (items, total) = await Repo(ctx).GetListPageByTenantAsync(Tenant, skip: 0, take: 20, CancellationToken.None);

        Assert.Equal(1, total);
        Assert.Equal(Utc(1), Assert.Single(items).CreatedAt);
    }

    /// <summary>
    /// Why the EntityId tie-break exists. CreatedAt is a non-unique datetime2 — a bulk import lands
    /// many entities in the same instant — and OFFSET/FETCH over a non-total order lets a row
    /// sharing a timestamp land on two pages or on neither. Three rows created at the SAME instant,
    /// walked two pages at a time, must come back exactly once each.
    /// </summary>
    [Fact]
    public async Task GetListPageByTenant_RowsSharingATimestamp_ArePagedWithoutOverlapOrLoss()
    {
        var sameInstant = Utc(7);
        using var ctx = NewInMemoryContext();
        await SeedAsync(ctx, Ent(sameInstant), Ent(sameInstant), Ent(sameInstant));

        var repo = Repo(ctx);
        var (first, total) = await repo.GetListPageByTenantAsync(Tenant, skip: 0, take: 2, CancellationToken.None);
        var (second, _)    = await repo.GetListPageByTenantAsync(Tenant, skip: 2, take: 2, CancellationToken.None);

        var seen = first.Concat(second).Select(i => i.EntityId).ToList();
        Assert.Equal(3, total);
        Assert.Equal(3, seen.Count);
        Assert.Equal(3, seen.Distinct().Count());
    }

    /// <summary>A page past the end is empty, not an error, and TotalCount still reports the truth.</summary>
    [Fact]
    public async Task GetListPageByTenant_SkipPastTheEnd_ReturnsEmptyPageWithHonestTotal()
    {
        using var ctx = NewInMemoryContext();
        await SeedAsync(ctx, Ent(Utc(1)), Ent(Utc(2)));

        var (items, total) = await Repo(ctx).GetListPageByTenantAsync(Tenant, skip: 40, take: 20, CancellationToken.None);

        Assert.Empty(items);
        Assert.Equal(2, total);
    }

    /// <summary>
    /// Every field the list view renders survives the projection — the eleven original columns plus
    /// PersonType and FiscalRegime (DDL 069), two short nvarchar scalars that need no join.
    /// </summary>
    [Fact]
    public async Task GetListPageByTenant_CarriesTheThirteenProjectedColumns()
    {
        var entityId  = Guid.NewGuid();
        var createdBy = Guid.NewGuid();
        using var ctx = NewInMemoryContext();

        var row = Ent(Utc(2), entityId: entityId);
        row.CUIT               = "30-71234567-1";
        row.TradeName          = "MetalSur";
        row.Sector             = "Industria";
        row.MipymeCategory     = "II";
        row.IsMonitored        = true;
        row.RiskScore          = 63.25m;
        row.RiskScoreUpdatedAt = Utc(6);
        row.CreatedBy          = createdBy;
        row.PersonType         = PersonTypes.Humana;
        row.FiscalRegime       = FiscalRegimes.Monotributo;
        await SeedAsync(ctx, row);

        var (items, _) = await Repo(ctx).GetListPageByTenantAsync(Tenant, skip: 0, take: 20, CancellationToken.None);

        var item = Assert.Single(items);
        Assert.Equal(entityId, item.EntityId);
        Assert.Equal("30-71234567-1", item.CUIT);
        Assert.Equal("Metalúrgica del Sur S.A.", item.BusinessName);
        Assert.Equal("MetalSur", item.TradeName);
        Assert.Equal("Industria", item.Sector);
        Assert.Equal("II", item.MipymeCategory);
        Assert.True(item.IsMonitored);
        Assert.Equal(63.25m, item.RiskScore);
        Assert.Equal(Utc(6), item.RiskScoreUpdatedAt);
        Assert.Equal(Utc(2), item.CreatedAt);
        Assert.Equal(createdBy, item.CreatedBy);
        Assert.Equal(PersonTypes.Humana, item.PersonType);
        Assert.Equal(FiscalRegimes.Monotributo, item.FiscalRegime);
    }

    // ── The query itself ──────────────────────────────────────────────────────

    /// <summary>
    /// THE test for this change. The emitted T-SQL must not name any of the five source snapshots —
    /// if it does, SQL Server reads them off disk whether or not anything deserialises them, which is
    /// the entire defect #138 left behind. The paging must also be in the SQL (ORDER BY / OFFSET /
    /// FETCH), not in the controller.
    /// </summary>
    [Fact]
    public void ListPageQuery_SqlNeverNamesASourceSnapshot()
    {
        using var ctx = NewSqlServerContext();

        var sql = Repo(ctx).TenantListPageQuery(Tenant, 20, 20).ToQueryString();

        _output.WriteLine(sql);

        foreach (var snapshot in SourceSnapshotColumns)
            Assert.DoesNotContain(snapshot, sql, StringComparison.Ordinal);

        // A column the list has never rendered, kept here as a canary for "select the whole row".
        Assert.DoesNotContain("SgrOneClickEntityId", sql, StringComparison.Ordinal);

        Assert.Contains("ORDER BY", sql, StringComparison.Ordinal);
        Assert.Contains("OFFSET", sql, StringComparison.Ordinal);
        Assert.Contains("FETCH", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// The shape lock, mirroring EntityListItemResponse_ExposesExactlyTheProjectedProperties.
    /// A snapshot added here would be selected by the query above and silently reintroduce the
    /// defect, so the property set is pinned.
    /// </summary>
    [Fact]
    public void EntityListProjection_ExposesExactlyTheColumnsTheListNeeds()
    {
        var actual = typeof(EntityListProjection)
            .GetProperties()
            .Select(p => p.Name)
            .ToHashSet();

        var expected = new HashSet<string>
        {
            nameof(EntityListProjection.EntityId),
            nameof(EntityListProjection.CUIT),
            nameof(EntityListProjection.BusinessName),
            nameof(EntityListProjection.TradeName),
            nameof(EntityListProjection.Sector),
            nameof(EntityListProjection.MipymeCategory),
            nameof(EntityListProjection.IsMonitored),
            nameof(EntityListProjection.RiskScore),
            nameof(EntityListProjection.RiskScoreUpdatedAt),
            nameof(EntityListProjection.CreatedAt),
            nameof(EntityListProjection.CreatedBy),
            // DDL 069: two short scalars, no join, no snapshot.
            nameof(EntityListProjection.PersonType),
            nameof(EntityListProjection.FiscalRegime)
        };

        Assert.Equal(expected, actual);
    }
}
