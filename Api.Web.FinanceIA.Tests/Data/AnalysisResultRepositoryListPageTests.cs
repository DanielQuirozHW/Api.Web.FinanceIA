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
/// Covers the SQL-side list page: <see cref="AnalysisResultRepository.GetListPageByTenantAsync"/> and
/// <see cref="AnalysisResultRepository.GetListPageByEntityAsync"/>.
///
/// <para>
/// #137 stopped the list SHIPPING the heavy columns. It did not stop the query READING them: the
/// repository still ran <c>.Where(tenant).ToListAsync()</c> — every column of every row in the
/// tenant, including 15 <c>nvarchar(max)</c> blobs — and the controller paged that in memory. Against
/// Azure SQL serverless the disk read was the 8–30s, and no DTO change could touch it.
/// </para>
///
/// <para>
/// The load-bearing test here is <see cref="ListPageQuery_SqlNeverNamesAHeavyBlobColumn"/>. It is the
/// query-level counterpart to #137's malformed-blob tests: those proved the mapper never PARSES the
/// blobs, this proves the SELECT never NAMES them. It asserts against the SQL EF actually emits for
/// the production query, not a re-declared copy — hence the internal seams on the repository.
/// </para>
///
/// <para>
/// Behavioural tests run on the EF Core in-memory provider (see
/// <see cref="AnalysisResultRepositoryTests"/> for the same pattern); the SQL-text test needs a
/// relational provider, so it builds a SqlServer-backed context. No connection is ever opened —
/// <c>ToQueryString()</c> only compiles the query.
/// </para>
/// </summary>
public sealed class AnalysisResultRepositoryListPageTests
{
    private const string Tenant = "tenant-1";
    private const string Other  = "tenant-2";

    private readonly ITestOutputHelper _output;

    public AnalysisResultRepositoryListPageTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Every <c>nvarchar(max)</c> column on AnalysisResults that the list does NOT need. Reading any
    /// one of these per row is the defect; ScoreBreakdownJson is deliberately absent from this list
    /// because the four flattened scoring signals have no columns of their own.
    /// </summary>
    private static readonly string[] HeavyBlobColumns =
    [
        "BalanceJson", "RatiosJson", "BcraDataJson", "MacroDataJson",
        "ExecutiveSummary", "ScoringNarrative", "RiskAlerts", "CatalogueAlerts",
        "Anomalies", "ValidationNotes", "LufeDataJson", "FiscalDataJson",
        "NosisDataJson", "CasfogDataJson"
    ];

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static FinanciaDbContext NewInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"listpage-{Guid.NewGuid()}")
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

    private static AnalysisResultRepository Repo(FinanciaDbContext ctx)
        => new(ctx, NullLogger<AnalysisResultRepository>.Instance);

    private static AnalysisResult Result(
        DateTime generatedAt, string tenantId = Tenant, Guid? resultId = null, Guid? analysisId = null)
        => new()
        {
            ResultId    = resultId   ?? Guid.NewGuid(),
            AnalysisId  = analysisId ?? Guid.NewGuid(),
            TenantId    = tenantId,
            GeneratedAt = generatedAt,
            // Every heavy column populated, so a projection that leaked one would be visible.
            BalanceJson        = """{"activo":1}""",
            RatiosJson         = """{"liquidez":1.2}""",
            ExecutiveSummary   = "resumen",
            ScoringNarrative   = "narrativa",
            ScoreBreakdownJson = """{"sectorProfile":"Industria"}"""
        };

    private static DateTime Utc(int day) => new(2026, 3, day, 10, 0, 0, DateTimeKind.Utc);

    private static async Task SeedAsync(FinanciaDbContext ctx, params object[] rows)
    {
        ctx.AddRange(rows);
        ctx.ChangeTracker.DetectChanges(); // AutoDetectChanges is disabled on the context
        await ctx.SaveChangesAsync();
    }

    // ── By tenant ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The page is newest-first and honours skip/take. Ordering is new: before this change the list
    /// had no ORDER BY at all and rows came back in whatever order SQL Server chose.
    /// </summary>
    [Fact]
    public async Task GetListPageByTenant_ReturnsTheRequestedPageNewestFirst()
    {
        using var ctx = NewInMemoryContext();
        await SeedAsync(ctx, Result(Utc(1)), Result(Utc(5)), Result(Utc(3)), Result(Utc(4)));

        var (items, _) = await Repo(ctx).GetListPageByTenantAsync(Tenant, skip: 1, take: 2, CancellationToken.None);

        Assert.Equal([Utc(4), Utc(3)], items.Select(i => i.GeneratedAt));
    }

    /// <summary>TotalCount is the whole filtered set, not the page — it drives the pager.</summary>
    [Fact]
    public async Task GetListPageByTenant_TotalCount_CountsTheWholeSetNotThePage()
    {
        using var ctx = NewInMemoryContext();
        await SeedAsync(ctx, Result(Utc(1)), Result(Utc(2)), Result(Utc(3)), Result(Utc(4)), Result(Utc(5)));

        var (items, total) = await Repo(ctx).GetListPageByTenantAsync(Tenant, skip: 0, take: 2, CancellationToken.None);

        Assert.Equal(2, items.Count);
        Assert.Equal(5, total);
    }

    /// <summary>Another tenant's rows are neither returned nor counted.</summary>
    [Fact]
    public async Task GetListPageByTenant_IsTenantIsolated()
    {
        using var ctx = NewInMemoryContext();
        await SeedAsync(ctx, Result(Utc(1)), Result(Utc(2), tenantId: Other), Result(Utc(3), tenantId: Other));

        var (items, total) = await Repo(ctx).GetListPageByTenantAsync(Tenant, skip: 0, take: 20, CancellationToken.None);

        Assert.Equal(1, total);
        Assert.Equal(Utc(1), Assert.Single(items).GeneratedAt);
    }

    /// <summary>
    /// Why the ResultId tie-break exists. GeneratedAt is a non-unique datetime2, and OFFSET/FETCH
    /// over a non-total order lets a row sharing a timestamp land on two pages or on neither. Three
    /// rows with the SAME instant, walked two pages at a time, must come back exactly once each.
    /// </summary>
    [Fact]
    public async Task GetListPageByTenant_RowsSharingATimestamp_ArePagedWithoutOverlapOrLoss()
    {
        var sameInstant = Utc(7);
        using var ctx = NewInMemoryContext();
        await SeedAsync(ctx, Result(sameInstant), Result(sameInstant), Result(sameInstant));

        var repo = Repo(ctx);
        var (first, total)  = await repo.GetListPageByTenantAsync(Tenant, skip: 0, take: 2, CancellationToken.None);
        var (second, _)     = await repo.GetListPageByTenantAsync(Tenant, skip: 2, take: 2, CancellationToken.None);

        var seen = first.Concat(second).Select(i => i.ResultId).ToList();
        Assert.Equal(3, total);
        Assert.Equal(3, seen.Count);
        Assert.Equal(3, seen.Distinct().Count());
    }

    /// <summary>A page past the end is empty, not an error, and TotalCount still reports the truth.</summary>
    [Fact]
    public async Task GetListPageByTenant_SkipPastTheEnd_ReturnsEmptyPageWithHonestTotal()
    {
        using var ctx = NewInMemoryContext();
        await SeedAsync(ctx, Result(Utc(1)), Result(Utc(2)));

        var (items, total) = await Repo(ctx).GetListPageByTenantAsync(Tenant, skip: 40, take: 20, CancellationToken.None);

        Assert.Empty(items);
        Assert.Equal(2, total);
    }

    /// <summary>The projected values survive the trip, including the one blob the list does read.</summary>
    [Fact]
    public async Task GetListPageByTenant_CarriesTheSixProjectedColumns()
    {
        var resultId   = Guid.NewGuid();
        var analysisId = Guid.NewGuid();
        using var ctx  = NewInMemoryContext();
        var row = Result(Utc(2), resultId: resultId, analysisId: analysisId);
        row.AiSuggestedScore = 72.50m;
        row.AiSuggestedLevel = "High";
        await SeedAsync(ctx, row);

        var (items, _) = await Repo(ctx).GetListPageByTenantAsync(Tenant, skip: 0, take: 20, CancellationToken.None);

        var item = Assert.Single(items);
        Assert.Equal(resultId, item.ResultId);
        Assert.Equal(analysisId, item.AnalysisId);
        Assert.Equal(Utc(2), item.GeneratedAt);
        Assert.Equal(72.50m, item.AiSuggestedScore);
        Assert.Equal("High", item.AiSuggestedLevel);
        Assert.Equal("""{"sectorProfile":"Industria"}""", item.ScoreBreakdownJson);
    }

    // ── By entity ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The Documents → Analyses → AnalysisResults chain, preserved from GetByEntityIdAsync. A result
    /// reached through a SOFT-DELETED document is excluded, and so is another entity's — neither may
    /// appear in the page or in the count.
    /// </summary>
    [Fact]
    public async Task GetListPageByEntity_WalksTheChain_ExcludingSoftDeletedDocsAndOtherEntities()
    {
        var entityId      = Guid.NewGuid();
        var otherEntityId = Guid.NewGuid();

        var liveDoc    = Doc(entityId, isDeleted: false);
        var deletedDoc = Doc(entityId, isDeleted: true);
        var otherDoc   = Doc(otherEntityId, isDeleted: false);

        var wanted        = Analysis(liveDoc);
        var viaDeletedDoc = Analysis(deletedDoc);
        var viaOtherEnt   = Analysis(otherDoc);

        using var ctx = NewInMemoryContext();
        await SeedAsync(
            ctx,
            liveDoc, deletedDoc, otherDoc,
            wanted, viaDeletedDoc, viaOtherEnt,
            Result(Utc(3), analysisId: wanted.AnalysisId),
            Result(Utc(4), analysisId: viaDeletedDoc.AnalysisId),
            Result(Utc(5), analysisId: viaOtherEnt.AnalysisId));

        var (items, total) = await Repo(ctx)
            .GetListPageByEntityAsync(entityId, Tenant, skip: 0, take: 20, CancellationToken.None);

        Assert.Equal(1, total);
        Assert.Equal(Utc(3), Assert.Single(items).GeneratedAt);
    }

    /// <summary>Newest-first and TotalCount hold on the by-entity variant too.</summary>
    [Fact]
    public async Task GetListPageByEntity_PagesNewestFirstAndCountsOnlyThatEntity()
    {
        var entityId = Guid.NewGuid();
        var doc      = Doc(entityId, isDeleted: false);
        var a1       = Analysis(doc);
        var a2       = Analysis(doc);
        var a3       = Analysis(doc);

        var otherDoc      = Doc(Guid.NewGuid(), isDeleted: false);
        var otherAnalysis = Analysis(otherDoc);

        using var ctx = NewInMemoryContext();
        await SeedAsync(
            ctx,
            doc, otherDoc, a1, a2, a3, otherAnalysis,
            Result(Utc(1), analysisId: a1.AnalysisId),
            Result(Utc(9), analysisId: a2.AnalysisId),
            Result(Utc(5), analysisId: a3.AnalysisId),
            Result(Utc(8), analysisId: otherAnalysis.AnalysisId));

        var (items, total) = await Repo(ctx)
            .GetListPageByEntityAsync(entityId, Tenant, skip: 0, take: 2, CancellationToken.None);

        Assert.Equal(3, total);
        Assert.Equal([Utc(9), Utc(5)], items.Select(i => i.GeneratedAt));
    }

    /// <summary>An entity with no analyses yields an empty page and a zero count, not an error.</summary>
    [Fact]
    public async Task GetListPageByEntity_WhenEntityHasNoAnalyses_ReturnsEmpty()
    {
        using var ctx = NewInMemoryContext();
        await SeedAsync(ctx, Result(Utc(1)));

        var (items, total) = await Repo(ctx)
            .GetListPageByEntityAsync(Guid.NewGuid(), Tenant, skip: 0, take: 20, CancellationToken.None);

        Assert.Empty(items);
        Assert.Equal(0, total);
    }

    private static Document Doc(Guid entityId, bool isDeleted) => new()
    {
        DocumentId = Guid.NewGuid(),
        EntityId   = entityId,
        TenantId   = Tenant,
        IsDeleted  = isDeleted
    };

    private static Analysis Analysis(Document doc) => new()
    {
        AnalysisId = Guid.NewGuid(),
        TenantId   = Tenant,
        EntityId   = doc.EntityId,
        DocumentId = doc.DocumentId
    };

    // ── The query itself ──────────────────────────────────────────────────────

    /// <summary>
    /// THE test for this change. The emitted T-SQL must not name a single heavy blob column — if it
    /// does, SQL Server reads it off disk whether or not anything deserialises it, which is the
    /// entire defect. ScoreBreakdownJson is the one blob that MUST be there: the four flattened
    /// scoring signals have no columns of their own. And the paging must be in the SQL
    /// (ORDER BY / OFFSET / FETCH), not in the controller.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ListPageQuery_SqlNeverNamesAHeavyBlobColumn(bool byEntity)
    {
        using var ctx = NewSqlServerContext();
        var repo = Repo(ctx);

        var sql = byEntity
            ? repo.EntityListPageQuery(Guid.NewGuid(), Tenant, 20, 20).ToQueryString()
            : repo.TenantListPageQuery(Tenant, 20, 20).ToQueryString();

        _output.WriteLine(sql);

        foreach (var blob in HeavyBlobColumns)
            Assert.DoesNotContain(blob, sql, StringComparison.Ordinal);

        Assert.Contains("ScoreBreakdownJson", sql, StringComparison.Ordinal);
        // Score credibility 1b-2b: the two scalars the reliability verdict needs, read as plain columns.
        Assert.Contains("[EnrichmentStatus]", sql, StringComparison.Ordinal);
        Assert.Contains("[RescoredAt]", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY", sql, StringComparison.Ordinal);
        Assert.Contains("OFFSET", sql, StringComparison.Ordinal);
        Assert.Contains("FETCH", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// The shape lock, mirroring AnalysisListItemResponse_ExposesExactlyTheProjectedProperties.
    /// A blob added here would be selected by the query above and silently reintroduce the defect,
    /// so the property set is pinned.
    /// </summary>
    [Fact]
    public void AnalysisListProjection_ExposesExactlyTheColumnsTheListNeeds()
    {
        var actual = typeof(AnalysisListProjection)
            .GetProperties()
            .Select(p => p.Name)
            .ToHashSet();

        var expected = new HashSet<string>
        {
            nameof(AnalysisListProjection.ResultId),
            nameof(AnalysisListProjection.AnalysisId),
            nameof(AnalysisListProjection.GeneratedAt),
            nameof(AnalysisListProjection.AiSuggestedScore),
            nameof(AnalysisListProjection.AiSuggestedLevel),
            nameof(AnalysisListProjection.EnrichmentStatus),   // 1b-2b: a short scalar, for the reliability verdict
            nameof(AnalysisListProjection.RescoredAt),         // 1b-2b: the score's date when re-scored
            nameof(AnalysisListProjection.ScoreBreakdownJson)
        };

        Assert.Equal(expected, actual);
    }
}
