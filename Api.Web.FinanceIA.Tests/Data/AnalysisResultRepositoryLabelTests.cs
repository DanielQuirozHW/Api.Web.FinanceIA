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
/// Covers the SQL-side analysis LABEL read: <see cref="AnalysisResultRepository.GetLabelByIdAsync"/>.
///
/// <para>
/// The Portal's breadcrumb hook runs in the app shell, OUTSIDE the page transition, so it is a
/// SECOND full read on every navigation to an analysis detail — and it filled the crumb by calling
/// GET /api/analyses/{id}, the heaviest read in the app, to take <c>entityName</c> off it. Even
/// after #143 moved four frozen snapshots to /context and #147 moved BcraDataJson, that detail read
/// still names seven <c>nvarchar(max)</c> columns.
/// </para>
///
/// <para>
/// The name is not on the result row: it is reached ResultId → AnalysisId → EntityId →
/// <c>Entities.BusinessName</c>. This is the only projection in the repository that leaves
/// AnalysisResults, and it does so in ONE statement rather than the three round-trips the detail
/// path runs — two of which name a blob, since <c>IAnalysisRepository.GetByIdAsync</c> loads the
/// Analyses row unprojected (<c>ReviewNotes</c> is <c>nvarchar(max)</c>) and
/// <c>IEntityRepository.GetByIdAsync</c> loads the Entities row with all five snapshots on it.
/// </para>
///
/// <para>
/// The load-bearing test is <see cref="LabelQuery_SqlNamesOnlyTheBusinessName"/>, and it has to reach
/// wider than its siblings: a three-table join can drag a blob off ANY of the three, so the
/// forbidden list spans all of them — including <c>ReviewNotes</c>, the one <c>nvarchar(max)</c> on
/// Analyses and the reason the resolve-then-fetch chain was rejected.
/// </para>
///
/// <para>
/// <c>GetByIdAsync</c>, <c>GetDetailByIdAsync</c> and <c>GetRatiosByIdAsync</c> are deliberately NOT
/// changed and are not under test here.
/// </para>
/// </summary>
public sealed class AnalysisResultRepositoryLabelTests
{
    private const string Tenant = "tenant-1";
    private const string Other  = "tenant-2";

    private const string Name = "Metalúrgica del Sur S.A.";

    private readonly ITestOutputHelper _output;

    public AnalysisResultRepositoryLabelTests(ITestOutputHelper output) => _output = output;

    /// <summary>The one string the breadcrumb renders, plus the join that reaches it.</summary>
    private static readonly string[] LabelColumns =
    [
        "BusinessName", "INNER JOIN"
    ];

    /// <summary>
    /// Every blob on all three tables. The join makes this list wider than the single-table ones:
    /// naming any of these would read that column off disk for a query whose whole output is one
    /// name.
    /// <para>
    /// The five <c>*DataJson</c> snapshot names each cover TWO tables — the frozen copies on
    /// AnalysisResults and the live ones on Entities — so one assertion per name guards both.
    /// </para>
    /// </summary>
    private static readonly string[] ForbiddenBlobColumns =
    [
        // AnalysisResults.
        "BalanceJson", "RatiosJson", "MacroDataJson", "ScoreBreakdownJson",
        "ExecutiveSummary", "ScoringNarrative", "RiskAlerts", "CatalogueAlerts",
        "Anomalies", "ValidationNotes",
        // The five snapshot blobs — frozen on AnalysisResults, live on Entities.
        "BcraDataJson", "LufeDataJson", "FiscalDataJson", "CasfogDataJson", "NosisDataJson",
        // Analyses. The only nvarchar(max) on that table, and the reason resolving the label through
        // IAnalysisRepository.GetByIdAsync — an unprojected load — was rejected.
        "ReviewNotes"
    ];

    /// <summary>
    /// Scalars across the three tables that the label does not use. Cheap individually, but a
    /// projection that quietly widened to them would mean the join had stopped being a label read,
    /// and that should fail rather than merely get slower.
    /// <para>
    /// <c>ResultId</c>, <c>TenantId</c>, <c>AnalysisId</c> and <c>EntityId</c> are deliberately
    /// absent: the first two isolate the row and the tenant, and the last two are the join keys.
    /// </para>
    /// </summary>
    private static readonly string[] ForbiddenScalarColumns =
    [
        // AnalysisResults.
        "GeneratedAt", "AiSuggestedScore", "AiSuggestedLevel", "AiInputHash", "AiOutputHash",
        "AiInputTokens", "AiOutputTokens", "ValidationStatus", "EnrichmentStatus", "EnrichmentNotes",
        // Analyses.
        "DocumentId", "RequestedBy", "RequestedAmount", "FiscalYear", "AiModel", "AiTemperature",
        "PromptVersion", "FinalRiskScore", "FinalRiskLevel", "DynamicsEntityId", "DynamicsEntityUrl",
        "ComparativeAnalysisId", "SentToSGR", "ReviewedBy", "ReviewedAt",
        // Entities. CUIT is here and not in the projection: this endpoint returns the name and the
        // id, and the CUIT lives behind GET /api/entities/{id}/label for callers that want it.
        "CUIT", "TradeName", "Sector", "MipymeCategory", "SgrOneClickEntityId",
        "RiskScore", "IsMonitored", "CreatedAt", "UpdatedAt", "CreatedBy"
    ];

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static FinanciaDbContext NewInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"analysis-label-{Guid.NewGuid()}")
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
    /// Every blob populated with a distinguishable value, so a projection that leaked one would be
    /// visible rather than merely absent.
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
        ValidationStatus   = "Valid",
        ValidationNotes    = "Notas de validación.",
        EnrichmentStatus   = "Ok",
        LufeDataJson       = """{"lufe":6}""",
        FiscalDataJson     = """{"fiscal":7}""",
        CasfogDataJson     = """{"casfog":8}""",
        NosisDataJson      = """{"nosis":9}"""
    };

    /// <summary>ReviewNotes populated deliberately — it is the nvarchar(max) on this table.</summary>
    private static Analysis Ana(Guid analysisId, Guid entityId, string tenantId = Tenant) => new()
    {
        AnalysisId    = analysisId,
        TenantId      = tenantId,
        EntityId      = entityId,
        DocumentId    = Guid.NewGuid(),
        RequestedBy   = Guid.NewGuid(),
        Status        = "Reviewed",
        FiscalYear    = 2025,
        AiModel       = "claude-sonnet-4-6",
        AiTemperature = 0.00m,
        PromptVersion = "v3",
        ReviewNotes   = "Notas de revisión largas.",
        CreatedAt     = Utc(1)
    };

    private static Entity Ent(Guid entityId, string tenantId = Tenant) => new()
    {
        EntityId       = entityId,
        TenantId       = tenantId,
        CUIT           = "30-71234567-1",
        BusinessName   = Name,
        TradeName      = "MetalSur",
        Sector         = "Manufactura",
        CreatedAt      = Utc(1),
        UpdatedAt      = Utc(2),
        FiscalDataJson = """{"a":1}""",
        LufeDataJson   = """{"b":2}""",
        BcraDataJson   = """{"c":3}""",
        CasfogDataJson = """{"d":4}""",
        NosisDataJson  = """{"e":5}"""
    };

    private static async Task SeedAsync(FinanciaDbContext ctx, params object[] rows)
    {
        ctx.AddRange(rows);
        ctx.ChangeTracker.DetectChanges(); // AutoDetectChanges is disabled on the context
        await ctx.SaveChangesAsync();
    }

    /// <summary>The happy chain: one result, its analysis, its entity, all in <paramref name="tenantId"/>.</summary>
    private static async Task<Guid> SeedChainAsync(
        FinanciaDbContext ctx,
        string resultTenant = Tenant,
        string analysisTenant = Tenant,
        string entityTenant = Tenant)
    {
        var resultId   = Guid.NewGuid();
        var analysisId = Guid.NewGuid();
        var entityId   = Guid.NewGuid();

        await SeedAsync(ctx,
            Res(resultId, analysisId, resultTenant),
            Ana(analysisId, entityId, analysisTenant),
            Ent(entityId, entityTenant));

        return resultId;
    }

    // ── Behaviour ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The name arrives from two tables away, and the entity id comes with it — it is the join key,
    /// so naming it costs nothing and it is what lets the crumb link to the PyME page.
    /// </summary>
    [Fact]
    public async Task GetLabelById_ReturnsTheEntityNameAndId()
    {
        var resultId   = Guid.NewGuid();
        var analysisId = Guid.NewGuid();
        var entityId   = Guid.NewGuid();

        using var ctx = NewInMemoryContext();
        await SeedAsync(ctx, Res(resultId, analysisId), Ana(analysisId, entityId), Ent(entityId));

        var label = await Repo(ctx).GetLabelByIdAsync(resultId, Tenant, CancellationToken.None);

        Assert.NotNull(label);
        Assert.Equal(resultId, label.ResultId);
        Assert.Equal(entityId, label.EntityId);
        Assert.Equal(Name,     label.EntityName);
    }

    /// <summary>A missing result is a null, which the controller turns into a 404.</summary>
    [Fact]
    public async Task GetLabelById_UnknownId_ReturnsNull()
    {
        using var ctx = NewInMemoryContext();
        await SeedChainAsync(ctx);

        Assert.Null(await Repo(ctx).GetLabelByIdAsync(Guid.NewGuid(), Tenant, CancellationToken.None));
    }

    /// <summary>Another tenant's result is not readable, even by exact result id.</summary>
    [Fact]
    public async Task GetLabelById_IsTenantIsolated()
    {
        using var ctx = NewInMemoryContext();
        var resultId = await SeedChainAsync(ctx, resultTenant: Other, analysisTenant: Other, entityTenant: Other);

        Assert.Null(await Repo(ctx).GetLabelByIdAsync(resultId, Tenant, CancellationToken.None));
    }

    /// <summary>
    /// The predicate on the JOINED table, not the filtered one. A result of this tenant whose analysis
    /// row belongs to another must not resolve — that is a corrupted chain, and the honest answer is
    /// "no label" rather than a name pulled across a tenant boundary. This is what the second
    /// <c>Where(a =&gt; a.TenantId == tenantId)</c> in BuildLabel buys; without it this test returns
    /// the name.
    /// </summary>
    [Fact]
    public async Task GetLabelById_AnalysisInAnotherTenant_ReturnsNull()
    {
        using var ctx = NewInMemoryContext();
        var resultId = await SeedChainAsync(ctx, analysisTenant: Other);

        Assert.Null(await Repo(ctx).GetLabelByIdAsync(resultId, Tenant, CancellationToken.None));
    }

    /// <summary>
    /// The same argument one join further out, and the one that would actually leak a business name:
    /// the entity row is what carries the string. Guarded by the third
    /// <c>Where(e =&gt; e.TenantId == tenantId)</c>.
    /// </summary>
    [Fact]
    public async Task GetLabelById_EntityInAnotherTenant_ReturnsNull()
    {
        using var ctx = NewInMemoryContext();
        var resultId = await SeedChainAsync(ctx, entityTenant: Other);

        Assert.Null(await Repo(ctx).GetLabelByIdAsync(resultId, Tenant, CancellationToken.None));
    }

    // ── The query itself ──────────────────────────────────────────────────────

    /// <summary>
    /// THE test for this change. The emitted T-SQL must name BusinessName and NOTHING else off any of
    /// the three rows it touches — no blob on AnalysisResults, no ReviewNotes on Analyses, no source
    /// snapshot on Entities, and none of the scalars either. If it named a blob SQL Server would read
    /// that column off disk whether or not anything deserialises it, which is precisely the cost this
    /// endpoint exists to avoid; a passing controller test would not notice.
    ///
    /// <para>
    /// The INNER JOIN assertion is the positive half. It is what distinguishes "one statement across
    /// three tables" from a query that silently stopped joining and started returning nothing, or
    /// from a rewrite into separate round-trips.
    /// </para>
    /// </summary>
    [Fact]
    public void LabelQuery_SqlNamesOnlyTheBusinessName()
    {
        using var ctx = NewSqlServerContext();

        var sql = Repo(ctx).LabelQuery(Guid.NewGuid(), Tenant).ToQueryString();

        _output.WriteLine(sql);

        foreach (var needed in LabelColumns)
            Assert.Contains(needed, sql, StringComparison.Ordinal);

        foreach (var forbidden in ForbiddenBlobColumns.Concat(ForbiddenScalarColumns))
            Assert.DoesNotContain(forbidden, sql, StringComparison.Ordinal);

        // Two joins, not one: the name is two tables away from the result.
        Assert.Equal(2, sql.Split("INNER JOIN").Length - 1);
    }

    // ── Shape locks ───────────────────────────────────────────────────────────

    /// <summary>
    /// The shape lock, mirroring <c>AnalysisRatiosProjection_ExposesExactlyThreeColumns</c>. Adding a
    /// property here would put that column straight back into the SELECT above — and across a join,
    /// that could be a blob on a table this query only visits to read a key.
    /// </summary>
    [Fact]
    public void AnalysisLabelProjection_ExposesExactlyThreeColumns()
    {
        var actual = typeof(AnalysisLabelProjection)
            .GetProperties()
            .Select(p => p.Name)
            .ToHashSet();

        var expected = new HashSet<string>
        {
            nameof(AnalysisLabelProjection.ResultId),
            nameof(AnalysisLabelProjection.EntityId),
            nameof(AnalysisLabelProjection.EntityName)
        };

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// The wire shape. Property names AND types must match <see cref="AnalysisResultResponse"/>
    /// exactly — that is what lets the Portal swap the URL and change nothing about the fields it
    /// reads — so both are asserted against the detail DTO rather than restated.
    /// </summary>
    [Fact]
    public void AnalysisLabelResponse_ExposesExactlyTheTwoFieldsTheDetailAlreadyNames()
    {
        var actual = typeof(AnalysisLabelResponse)
            .GetProperties()
            .Select(p => p.Name)
            .ToHashSet();

        var expected = new HashSet<string>
        {
            nameof(AnalysisLabelResponse.EntityName),
            nameof(AnalysisLabelResponse.EntityId)
        };

        Assert.Equal(expected, actual);

        Assert.Equal(
            typeof(AnalysisResultResponse).GetProperty(nameof(AnalysisResultResponse.EntityName))!.PropertyType,
            typeof(AnalysisLabelResponse).GetProperty(nameof(AnalysisLabelResponse.EntityName))!.PropertyType);
        Assert.Equal(
            typeof(AnalysisResultResponse).GetProperty(nameof(AnalysisResultResponse.EntityId))!.PropertyType,
            typeof(AnalysisLabelResponse).GetProperty(nameof(AnalysisLabelResponse.EntityId))!.PropertyType);
    }
}
