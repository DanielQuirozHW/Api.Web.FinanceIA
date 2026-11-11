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
/// Covers the SQL-side BCRA read: <see cref="AnalysisResultRepository.GetBcraSnapshotByIdAsync"/>.
///
/// <para>
/// It exists for GET /api/analyses/{id}/bcra/cheques, which serves the per-cheque table that
/// /context stopped shipping. Reading it through <c>GetContextSnapshotByIdAsync</c> would have
/// worked and been wrong: that query names five blobs because the Contexto section renders them
/// together, so one table would have paid for LufeDataJson —26KB on a representative row— plus the
/// Fiscal, CASFOG and NOSIS snapshots it renders none of.
/// </para>
///
/// <para>
/// The load-bearing test is <see cref="BcraQuery_SqlNamesOnlyTheBcraBlob"/>. Its negative half is
/// the point — proving the emitted T-SQL names none of the other blobs is what proves the endpoint
/// is actually narrow, since SQL Server reads a column off disk whether or not anything
/// deserialises it. It asserts against the SQL EF emits for the PRODUCTION query, not a re-declared
/// copy, hence the internal seam on the repository.
/// </para>
///
/// <para>
/// <c>GetContextSnapshotByIdAsync</c> is deliberately NOT changed and is not under test here: the
/// Contexto tab renders every part of that blob except the per-cheque array, so it still reads the
/// same column — see <c>ContextQuery_AlsoNamesTheBcraBlob</c>. The elision happens in the
/// controller, not in SQL.
/// </para>
/// </summary>
public sealed class AnalysisResultRepositoryBcraTests
{
    private const string Tenant = "tenant-1";
    private const string Other  = "tenant-2";

    private readonly ITestOutputHelper _output;

    public AnalysisResultRepositoryBcraTests(ITestOutputHelper output) => _output = output;

    /// <summary>The one blob the cheque table renders, so the query must read it.</summary>
    private static readonly string[] BcraColumns =
    [
        "BcraDataJson"
    ];

    /// <summary>
    /// Every other column on the row. None of them is rendered by the cheque table, so naming any of
    /// them would put its I/O straight back and undo the change. The four frozen Contexto snapshots
    /// are the ones this read exists to avoid; the scalars are here because a projection that
    /// quietly widened would show up as a failure rather than as a slow endpoint nobody noticed.
    /// <para>
    /// <c>ResultId</c> and <c>TenantId</c> are deliberately absent from this list — both legitimately
    /// appear in the WHERE clause that isolates the row and the tenant, and ResultId is the key this
    /// projection carries.
    /// </para>
    /// </summary>
    private static readonly string[] ForbiddenColumns =
    [
        // The four frozen Contexto snapshots — read together by the context query, never here.
        "LufeDataJson", "FiscalDataJson", "CasfogDataJson", "NosisDataJson",
        // The blobs the analysis DETAIL reads and this table does not render.
        "BalanceJson", "RatiosJson", "ScoreBreakdownJson", "RiskAlerts", "CatalogueAlerts",
        "Anomalies", "ExecutiveSummary", "ScoringNarrative",
        // Columns no read path on the detail touches at all.
        "MacroDataJson", "ValidationNotes", "AiInputHash", "AiOutputHash",
        // Scalars the cheque table does not use.
        "AnalysisId", "GeneratedAt", "AiSuggestedScore", "AiSuggestedLevel",
        "AiInputTokens", "AiOutputTokens", "ValidationStatus", "EnrichmentStatus", "EnrichmentNotes"
    ];

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static FinanciaDbContext NewInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"analysis-bcra-{Guid.NewGuid()}")
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

    private static DateTime Utc(int day) => new(2026, 4, day, 10, 0, 0, DateTimeKind.Utc);

    private const string StoredBcraJson = """
        {"cheques":{"totalRechazados":2,"estado":0,
        "detalle":[{"nroCheque":1,"monto":100},{"nroCheque":2,"monto":200}]}}
        """;

    /// <summary>
    /// Every column populated with a distinguishable value, so a projection that dropped one — or
    /// leaked one it should not carry — is visible rather than merely absent.
    /// </summary>
    private static AnalysisResult Res(Guid resultId, Guid analysisId, string tenantId = Tenant) => new()
    {
        ResultId           = resultId,
        AnalysisId         = analysisId,
        TenantId           = tenantId,
        GeneratedAt        = Utc(1),
        BalanceJson        = """{"balance":1}""",
        RatiosJson         = """{"ratios":2}""",
        BcraDataJson       = StoredBcraJson,
        MacroDataJson      = """{"macro":4}""",
        ScoreBreakdownJson = """{"breakdown":5}""",
        ExecutiveSummary   = "Resumen ejecutivo.",
        ScoringNarrative   = "Narrativa.",
        RiskAlerts         = """[{"code":"LIQ"}]""",
        CatalogueAlerts    = """[{"code":"CAT-1"}]""",
        Anomalies          = """["Una anomalía."]""",
        AiSuggestedScore   = 62.5m,
        AiSuggestedLevel   = "Medium",
        AiInputHash        = "in-hash",
        AiOutputHash       = "out-hash",
        AiInputTokens      = 1234,
        AiOutputTokens     = 567,
        ValidationStatus   = "Valid",
        ValidationNotes    = "Notas de validación.",
        EnrichmentStatus   = "Degraded",
        EnrichmentNotes    = "BCRA no respondió.",
        // The four frozen snapshots — all populated, so a projection that leaked one would show.
        LufeDataJson       = """{"lufe":6}""",
        FiscalDataJson     = """{"fiscal":7}""",
        CasfogDataJson     = """{"casfog":8}""",
        NosisDataJson      = """{"nosis":9}"""
    };

    private static async Task SeedAsync(FinanciaDbContext ctx, params AnalysisResult[] rows)
    {
        ctx.AnalysisResults.AddRange(rows);
        ctx.ChangeTracker.DetectChanges(); // AutoDetectChanges is disabled on the context
        await ctx.SaveChangesAsync();
    }

    // ── Behaviour ─────────────────────────────────────────────────────────────

    /// <summary>The blob comes back verbatim, under the key of the result it belongs to.</summary>
    [Fact]
    public async Task GetBcraSnapshotById_ReturnsTheStoredBlob()
    {
        var resultId = Guid.NewGuid();
        using var ctx = NewInMemoryContext();
        await SeedAsync(ctx, Res(resultId, Guid.NewGuid()));

        var snapshot = await Repo(ctx).GetBcraSnapshotByIdAsync(resultId, Tenant, CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal(resultId,        snapshot.ResultId);
        Assert.Equal(StoredBcraJson,  snapshot.BcraDataJson);
    }

    /// <summary>A result id that matches no row is null — which the controller turns into a 404.</summary>
    [Fact]
    public async Task GetBcraSnapshotById_UnknownId_ReturnsNull()
    {
        using var ctx = NewInMemoryContext();
        await SeedAsync(ctx, Res(Guid.NewGuid(), Guid.NewGuid()));

        Assert.Null(await Repo(ctx).GetBcraSnapshotByIdAsync(Guid.NewGuid(), Tenant, CancellationToken.None));
    }

    /// <summary>
    /// The load-bearing null distinction, and the reason this projects into a class rather than a
    /// bare <c>string?</c>. A result that exists but froze no BCRA snapshot must materialise an
    /// INSTANCE with a null blob, not a null instance — otherwise "this analysis has no cheques"
    /// would be indistinguishable from "wrong id" and the endpoint would 404 on the common case.
    /// </summary>
    [Fact]
    public async Task GetBcraSnapshotById_ResultWithNoSnapshot_ReturnsInstanceWithNullBlob()
    {
        var resultId = Guid.NewGuid();
        var row = Res(resultId, Guid.NewGuid());
        row.BcraDataJson = null;

        using var ctx = NewInMemoryContext();
        await SeedAsync(ctx, row);

        var snapshot = await Repo(ctx).GetBcraSnapshotByIdAsync(resultId, Tenant, CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal(resultId, snapshot.ResultId);
        Assert.Null(snapshot.BcraDataJson);
    }

    /// <summary>Another tenant's BCRA snapshot is not readable, even by exact result id.</summary>
    [Fact]
    public async Task GetBcraSnapshotById_IsTenantIsolated()
    {
        var resultId = Guid.NewGuid();
        using var ctx = NewInMemoryContext();
        await SeedAsync(ctx, Res(resultId, Guid.NewGuid(), Other));

        Assert.Null(await Repo(ctx).GetBcraSnapshotByIdAsync(resultId, Tenant, CancellationToken.None));
    }

    // ── The query itself ──────────────────────────────────────────────────────

    /// <summary>
    /// THE test for this change. The emitted T-SQL must name BcraDataJson and NOTHING else off the
    /// row — none of the four frozen snapshots, none of the detail blobs, none of the unused
    /// scalars. If it named any of them SQL Server would read that column off disk whether or not
    /// anything deserialises it, which is precisely the cost this endpoint exists to avoid; a
    /// passing controller test would not notice.
    ///
    /// <para>
    /// <c>JSON_VALUE</c> must be absent too. This read ships the column whole because the caller
    /// needs the array itself — that is the opposite of <c>DetailQuery</c>, which names the same
    /// column only as a JSON_VALUE argument to pull two scalars out of it.
    /// </para>
    /// </summary>
    [Fact]
    public void BcraQuery_SqlNamesOnlyTheBcraBlob()
    {
        using var ctx = NewSqlServerContext();

        var sql = Repo(ctx).BcraQuery(Guid.NewGuid(), Tenant).ToQueryString();

        _output.WriteLine(sql);

        foreach (var needed in BcraColumns)
            Assert.Contains(needed, sql, StringComparison.Ordinal);

        foreach (var forbidden in ForbiddenColumns)
            Assert.DoesNotContain(forbidden, sql, StringComparison.Ordinal);

        // Projected whole, not extracted from.
        Assert.DoesNotContain("JSON_VALUE", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// The tenant predicate is in the SQL, not applied in memory afterwards. Without it the read
    /// would return another tenant's cheque rows — which carry a CUIT — for an exact result id.
    /// </summary>
    [Fact]
    public void BcraQuery_SqlFiltersOnTenant()
    {
        using var ctx = NewSqlServerContext();

        var sql = Repo(ctx).BcraQuery(Guid.NewGuid(), Tenant).ToQueryString();

        _output.WriteLine(sql);

        Assert.Contains("TenantId", sql, StringComparison.Ordinal);
        Assert.Contains("ResultId", sql, StringComparison.Ordinal);
    }

    // ── Shape lock ────────────────────────────────────────────────────────────

    /// <summary>
    /// The shape lock, mirroring <c>AnalysisRatiosProjection_ExposesExactlyThreeColumns</c>. Adding a
    /// property here would put that column straight back into the SELECT above and silently
    /// reintroduce the defect, so the property set is pinned.
    /// </summary>
    [Fact]
    public void AnalysisBcraProjection_ExposesExactlyTwoColumns()
    {
        var actual = typeof(AnalysisBcraProjection)
            .GetProperties()
            .Select(p => p.Name)
            .ToHashSet();

        var expected = new HashSet<string>
        {
            nameof(AnalysisBcraProjection.ResultId),
            nameof(AnalysisBcraProjection.BcraDataJson)
        };

        Assert.Equal(expected, actual);
    }
}
