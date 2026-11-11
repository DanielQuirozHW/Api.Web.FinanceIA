// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-26

using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// Covers the SQL-side ratios read: <see cref="AnalysisResultRepository.GetRatiosByIdAsync"/>.
///
/// <para>
/// The PyME (entity) detail renders a "Ratios calculados por FinancIA" panel for the entity's latest
/// analysis, and filled it by calling GET /api/analyses/{id} purely to read two fields. Even after
/// #143 dropped the four frozen Contexto snapshots, that detail read still names nine
/// <c>nvarchar(max)</c> columns — ~73KB on a representative row — so a small ratio table was paying
/// for BalanceJson, BcraDataJson, RiskAlerts, CatalogueAlerts, Anomalies, ExecutiveSummary and
/// ScoringNarrative that it never renders.
/// </para>
///
/// <para>
/// The load-bearing test is <see cref="RatiosQuery_SqlNamesOnlyRatiosAndScoreBreakdown"/>. Its
/// negative half is the point — proving the emitted T-SQL names none of the other blobs is what
/// proves the endpoint is actually light, since SQL Server reads a column off disk whether or not
/// anything deserialises it. It asserts against the SQL EF emits for the PRODUCTION query, not a
/// re-declared copy, hence the internal seam on the repository.
/// </para>
///
/// <para>
/// <c>GetByIdAsync</c> and <c>GetDetailByIdAsync</c> are deliberately NOT changed and are not under
/// test here: the detail page renders far more than this panel, and the decision, chat and PDF-export
/// paths need the full entity.
/// </para>
/// </summary>
public sealed class AnalysisResultRepositoryRatiosTests
{
    private const string Tenant = "tenant-1";
    private const string Other  = "tenant-2";

    private readonly ITestOutputHelper _output;

    public AnalysisResultRepositoryRatiosTests(ITestOutputHelper output) => _output = output;

    /// <summary>The two blobs the ratios panel renders, so the query must read them.</summary>
    private static readonly string[] RatiosColumns =
    [
        "RatiosJson", "ScoreBreakdownJson"
    ];

    /// <summary>
    /// Every other column on the row. None of them is rendered by the ratios panel, so naming any of
    /// them would put its I/O straight back and undo the change. The nine blobs the detail still
    /// reads are the ones this endpoint exists to avoid; the scalars are here because a projection
    /// that quietly widened would show up as a failure rather than as a slow endpoint nobody noticed.
    /// <para>
    /// <c>ResultId</c> and <c>TenantId</c> are deliberately absent from this list — both legitimately
    /// appear in the WHERE clause that isolates the row and the tenant.
    /// </para>
    /// </summary>
    private static readonly string[] ForbiddenColumns =
    [
        // The blobs the analysis DETAIL still reads and this panel does not render.
        "BalanceJson", "BcraDataJson", "RiskAlerts", "CatalogueAlerts", "Anomalies",
        "ExecutiveSummary", "ScoringNarrative",
        // The four frozen Contexto snapshots — already lazy since #143, and not wanted here either.
        "LufeDataJson", "FiscalDataJson", "CasfogDataJson", "NosisDataJson",
        // Columns no read path on the detail touches at all.
        "MacroDataJson", "ValidationNotes", "AiInputHash", "AiOutputHash",
        // Scalars the panel does not use.
        "AnalysisId", "GeneratedAt", "AiSuggestedScore", "AiSuggestedLevel",
        "AiInputTokens", "AiOutputTokens", "ValidationStatus", "EnrichmentStatus", "EnrichmentNotes"
    ];

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static FinanciaDbContext NewInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"analysis-ratios-{Guid.NewGuid()}")
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
        BcraDataJson       = """{"bcra":3}""",
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

    /// <summary>
    /// The two blobs come back exactly as stored. They are echoed to the client verbatim, so an
    /// equality assertion on the raw string is the contract.
    /// </summary>
    [Fact]
    public async Task GetRatiosById_ReturnsRatiosAndScoreBreakdown()
    {
        var resultId = Guid.NewGuid();
        using var ctx = NewInMemoryContext();
        await SeedAsync(ctx, Res(resultId, Guid.NewGuid()));

        var ratios = await Repo(ctx).GetRatiosByIdAsync(resultId, Tenant, CancellationToken.None);

        Assert.NotNull(ratios);
        Assert.Equal(resultId,                 ratios.ResultId);
        Assert.Equal("""{"ratios":2}""",       ratios.RatiosJson);
        Assert.Equal("""{"breakdown":5}""",    ratios.ScoreBreakdownJson);
    }

    /// <summary>
    /// The distinction the endpoint depends on: an analysis that exists but was never scored returns
    /// an INSTANCE with null blobs — a 200 carrying nulls — not a null projection, which would be a
    /// 404. Projecting into a class rather than a tuple is what keeps these apart. This is the normal
    /// state for analyses that predate the scoring engine.
    /// </summary>
    [Fact]
    public async Task GetRatiosById_ResultExistsButNeverScored_ReturnsInstanceWithNullBlobs()
    {
        var resultId = Guid.NewGuid();
        using var ctx = NewInMemoryContext();
        var row = Res(resultId, Guid.NewGuid());
        row.RatiosJson         = null;
        row.ScoreBreakdownJson = null;
        await SeedAsync(ctx, row);

        var ratios = await Repo(ctx).GetRatiosByIdAsync(resultId, Tenant, CancellationToken.None);

        Assert.NotNull(ratios);          // the analysis exists → 200
        Assert.Null(ratios.RatiosJson);
        Assert.Null(ratios.ScoreBreakdownJson);
        Assert.Equal(resultId, ratios.ResultId);
    }

    /// <summary>A missing result is a null, which the controller turns into a 404.</summary>
    [Fact]
    public async Task GetRatiosById_UnknownId_ReturnsNull()
    {
        using var ctx = NewInMemoryContext();
        await SeedAsync(ctx, Res(Guid.NewGuid(), Guid.NewGuid()));

        Assert.Null(await Repo(ctx).GetRatiosByIdAsync(Guid.NewGuid(), Tenant, CancellationToken.None));
    }

    /// <summary>Another tenant's ratios are not readable, even by exact result id.</summary>
    [Fact]
    public async Task GetRatiosById_IsTenantIsolated()
    {
        var resultId = Guid.NewGuid();
        using var ctx = NewInMemoryContext();
        await SeedAsync(ctx, Res(resultId, Guid.NewGuid(), Other));

        Assert.Null(await Repo(ctx).GetRatiosByIdAsync(resultId, Tenant, CancellationToken.None));
    }

    // ── The query itself ──────────────────────────────────────────────────────

    /// <summary>
    /// THE test for this change. The emitted T-SQL must name RatiosJson and ScoreBreakdownJson and
    /// NOTHING else off the row — no BalanceJson, no BcraDataJson, none of the alert or summary
    /// columns, none of the four frozen snapshots. If it named any of them SQL Server would read that
    /// column off disk whether or not anything deserialises it, which is precisely the cost this
    /// endpoint exists to avoid; a passing controller test would not notice.
    /// </summary>
    [Fact]
    public void RatiosQuery_SqlNamesOnlyRatiosAndScoreBreakdown()
    {
        using var ctx = NewSqlServerContext();

        var sql = Repo(ctx).RatiosQuery(Guid.NewGuid(), Tenant).ToQueryString();

        _output.WriteLine(sql);

        foreach (var needed in RatiosColumns)
            Assert.Contains(needed, sql, StringComparison.Ordinal);

        foreach (var forbidden in ForbiddenColumns)
            Assert.DoesNotContain(forbidden, sql, StringComparison.Ordinal);
    }

    // ── Shape lock ────────────────────────────────────────────────────────────

    /// <summary>
    /// The shape lock, mirroring <c>AnalysisContextProjection_ExposesExactlyFiveColumns</c>. Adding a
    /// property here would put that column straight back into the SELECT above and silently
    /// reintroduce the defect, so the property set is pinned.
    /// </summary>
    [Fact]
    public void AnalysisRatiosProjection_ExposesExactlyThreeColumns()
    {
        var actual = typeof(AnalysisRatiosProjection)
            .GetProperties()
            .Select(p => p.Name)
            .ToHashSet();

        var expected = new HashSet<string>
        {
            nameof(AnalysisRatiosProjection.ResultId),
            nameof(AnalysisRatiosProjection.RatiosJson),
            nameof(AnalysisRatiosProjection.ScoreBreakdownJson)
        };

        Assert.Equal(expected, actual);
    }
}
