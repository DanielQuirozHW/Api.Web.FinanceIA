// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-26

using System.Text.RegularExpressions;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// Covers the SQL-side analysis DETAIL read: <see cref="AnalysisResultRepository.GetDetailByIdAsync"/>
/// and the on-demand context snapshot read.
///
/// <para>
/// The list path got this treatment in #139. The detail path got none of it: <c>GetByIdAsync</c> is a
/// full-row load, so GET /api/analyses/{id} read all fourteen <c>nvarchar(max)</c> columns off disk on
/// every request. Four of them are the frozen "Contexto" snapshots — LufeDataJson, FiscalDataJson,
/// CasfogDataJson, NosisDataJson — which on a representative 81KB row are ~34KB, about half the blob
/// I/O, for a section that renders only once the user opens it.
/// </para>
///
/// <para>
/// The load-bearing test is <see cref="DetailQuery_SqlNeverNamesAContextSnapshot"/>. Its positive half
/// matters as much as its negative half: proving BcraDataJson, BalanceJson, RatiosJson and
/// ScoreBreakdownJson are STILL selected is what distinguishes "we made Contexto lazy" from "we gutted
/// the detail" — BCRA in particular feeds the Score section at the top of the page and is not lazy in
/// any sense. It asserts against the SQL EF actually emits for the production query, not a re-declared
/// copy — hence the internal seams on the repository.
/// </para>
///
/// <para>
/// <c>GetByIdAsync</c> is deliberately NOT changed and is not under test here: <c>DecideAsync</c>
/// mutates the result and hands it to <c>UpdateAsync</c>, the chat endpoints build a prompt from
/// columns the projection does not carry (MacroDataJson among them), and the PDF export renders the
/// whole row. Those callers need the full entity.
/// </para>
/// </summary>
public sealed class AnalysisResultRepositoryDetailTests
{
    private const string Tenant = "tenant-1";
    private const string Other  = "tenant-2";

    private readonly ITestOutputHelper _output;

    public AnalysisResultRepositoryDetailTests(ITestOutputHelper output) => _output = output;

    /// <summary>The four frozen snapshots the DETAIL query must never read.</summary>
    private static readonly string[] ContextSnapshotColumns =
    [
        "LufeDataJson", "FiscalDataJson", "CasfogDataJson", "NosisDataJson"
    ];

    /// <summary>
    /// Blobs the detail renders on load, so the detail query must still read them.
    /// <para>
    /// BcraDataJson is deliberately NOT in this list, and not in <see cref="ContextSnapshotColumns"/>
    /// either. It belongs to neither group: the detail query names it, but only as an argument to
    /// JSON_VALUE, never as a projected column. A plain substring assertion cannot tell those two
    /// apart, so it gets its own pair of tests —
    /// <see cref="DetailQuery_ExtractsTheTwoScoreFieldsAndNeverProjectsTheBlob"/> and
    /// <see cref="ContextQuery_AlsoNamesTheBcraBlob"/>.
    /// </para>
    /// </summary>
    private static readonly string[] DetailBlobColumns =
    [
        "BalanceJson", "RatiosJson", "ScoreBreakdownJson",
        "ExecutiveSummary", "ScoringNarrative", "RiskAlerts", "CatalogueAlerts", "Anomalies"
    ];

    /// <summary>
    /// Columns absent for a different reason than the four above: nothing on the detail path reads
    /// them at all. MacroDataJson belongs to the chat prompt (own full-row read); ValidationNotes has
    /// no reader in the project. Both are nvarchar(max), so leaving them in would have cost real I/O.
    /// </summary>
    private static readonly string[] UnreadColumns =
    [
        "MacroDataJson", "ValidationNotes"
    ];

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static FinanciaDbContext NewInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"analysis-detail-{Guid.NewGuid()}")
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
        // A realistic BCRA document rather than a marker: the detail read no longer carries this
        // column, it resolves two paths INSIDE it, so the fixture has to have those paths. The
        // trailing bulk stands in for what the split exists to stop shipping — on a real row
        // cheques.detalle runs to 2930 entries and dominates the payload. Note estado sits after
        // the arrays, exactly as the serialiser emits it.
        BcraDataJson       = """
                             {"deuda":{"identificacion":20123456789,"situacionMaxima":3,
                             "entidadesInformantes":["BANCO A","BANCO B"],
                             "entidadesDetalle":[{"entidad":"BANCO A","situacion":3,"monto":1500}],
                             "estado":0,"fetchedAt":"2026-10-30T14:05:00.1234567Z"},
                             "cheques":{"totalRechazados":2,"estado":0,
                             "detalle":[{"nroCheque":1,"monto":100},{"nroCheque":2,"monto":200}]}}
                             """,
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

    // ── Detail behaviour ──────────────────────────────────────────────────────

    /// <summary>
    /// The projection carries every column the detail renders. Nothing GetById needs is silently
    /// dropped by making the read narrower.
    /// </summary>
    [Fact]
    public async Task GetDetailById_CarriesEveryColumnTheDetailRenders()
    {
        var resultId   = Guid.NewGuid();
        var analysisId = Guid.NewGuid();
        using var ctx = NewInMemoryContext();
        await SeedAsync(ctx, Res(resultId, analysisId));

        var detail = await Repo(ctx).GetDetailByIdAsync(resultId, Tenant, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(resultId,                  detail.ResultId);
        Assert.Equal(analysisId,                detail.AnalysisId);
        Assert.Equal(Tenant,                    detail.TenantId);
        Assert.Equal(Utc(1),                    detail.GeneratedAt);
        Assert.Equal("""{"balance":1}""",       detail.BalanceJson);
        Assert.Equal("""{"ratios":2}""",        detail.RatiosJson);
        Assert.Equal("""{"breakdown":5}""",     detail.ScoreBreakdownJson);
        Assert.Equal("Resumen ejecutivo.",      detail.ExecutiveSummary);
        Assert.Equal("Narrativa.",              detail.ScoringNarrative);
        Assert.Equal("""[{"code":"LIQ"}]""",    detail.RiskAlerts);
        Assert.Equal("""[{"code":"CAT-1"}]""",  detail.CatalogueAlerts);
        Assert.Equal("""["Una anomalía."]""",   detail.Anomalies);
        Assert.Equal(62.5m,                     detail.AiSuggestedScore);
        Assert.Equal("Medium",                  detail.AiSuggestedLevel);
        Assert.Equal(1234,                      detail.AiInputTokens);
        Assert.Equal(567,                       detail.AiOutputTokens);
        Assert.Equal("Valid",                   detail.ValidationStatus);
        Assert.Equal("Degraded",                detail.EnrichmentStatus);
        Assert.Equal("BCRA no respondió.",      detail.EnrichmentNotes);

        // BcraDataJson is NOT carried; these two are what SQL Server extracted out of it. Asserting
        // them here rather than only in the SQL-shape test is deliberate — it is what proves the
        // extraction actually resolves the paths, on a provider that runs the C# body of
        // SqlJsonFunctions.JsonValue rather than translating it.
        Assert.Equal("0", detail.BcraDeudaEstadoRaw);
        Assert.Equal("3", detail.BcraDeudaSituacionMaximaRaw);
        Assert.Equal("2026-10-30T14:05:00.1234567Z", detail.BcraDeudaFetchedAtRaw);
    }

    /// <summary>
    /// The blob is present but carries no <c>deuda</c> node, so there is nothing for either path to
    /// resolve. Both come back null — NOT the enum's default ordinal, which would report a debt
    /// lookup that never happened.
    /// </summary>
    [Fact]
    public async Task GetDetailById_BcraBlobWithoutDeuda_ExtractsNulls()
    {
        var resultId = Guid.NewGuid();
        using var ctx = NewInMemoryContext();
        var row = Res(resultId, Guid.NewGuid());
        row.BcraDataJson = """{"macro":{"inflacionMensual":2.5}}""";
        await SeedAsync(ctx, row);

        var detail = await Repo(ctx).GetDetailByIdAsync(resultId, Tenant, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Null(detail.BcraDeudaEstadoRaw);
        Assert.Null(detail.BcraDeudaSituacionMaximaRaw);
    }

    /// <summary>
    /// The normal shape for a CUIT with no reported debt: <c>estado</c> is set and
    /// <c>situacionMaxima</c> is genuinely null. The two must be extracted independently, or a null
    /// situación would suppress a perfectly good estado.
    /// </summary>
    [Fact]
    public async Task GetDetailById_NoDebtReported_ExtractsEstadoWithoutSituacion()
    {
        var resultId = Guid.NewGuid();
        using var ctx = NewInMemoryContext();
        var row = Res(resultId, Guid.NewGuid());
        row.BcraDataJson = """{"deuda":{"situacionMaxima":null,"estado":1},"cheques":{"estado":1}}""";
        await SeedAsync(ctx, row);

        var detail = await Repo(ctx).GetDetailByIdAsync(resultId, Tenant, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal("1", detail.BcraDeudaEstadoRaw);
        Assert.Null(detail.BcraDeudaSituacionMaximaRaw);
    }

    /// <summary>An analysis with no BCRA blob at all extracts nulls rather than failing.</summary>
    [Fact]
    public async Task GetDetailById_NoBcraBlob_ExtractsNulls()
    {
        var resultId = Guid.NewGuid();
        using var ctx = NewInMemoryContext();
        var row = Res(resultId, Guid.NewGuid());
        row.BcraDataJson = null;
        await SeedAsync(ctx, row);

        var detail = await Repo(ctx).GetDetailByIdAsync(resultId, Tenant, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Null(detail.BcraDeudaEstadoRaw);
        Assert.Null(detail.BcraDeudaSituacionMaximaRaw);
    }

    /// <summary>A result belonging to another tenant is not visible, even by exact id.</summary>
    [Fact]
    public async Task GetDetailById_IsTenantIsolated()
    {
        var resultId = Guid.NewGuid();
        using var ctx = NewInMemoryContext();
        await SeedAsync(ctx, Res(resultId, Guid.NewGuid(), Other));

        Assert.Null(await Repo(ctx).GetDetailByIdAsync(resultId, Tenant, CancellationToken.None));
    }

    /// <summary>An id that does not exist is a null, which the controller turns into a 404.</summary>
    [Fact]
    public async Task GetDetailById_UnknownId_ReturnsNull()
    {
        using var ctx = NewInMemoryContext();
        await SeedAsync(ctx, Res(Guid.NewGuid(), Guid.NewGuid()));

        Assert.Null(await Repo(ctx).GetDetailByIdAsync(Guid.NewGuid(), Tenant, CancellationToken.None));
    }

    // ── Context snapshot behaviour ────────────────────────────────────────────

    /// <summary>The context read returns all four frozen snapshots, each its own, in one query.</summary>
    [Fact]
    public async Task GetContextSnapshotById_ReturnsTheFourFrozenSnapshots()
    {
        var resultId = Guid.NewGuid();
        using var ctx = NewInMemoryContext();
        await SeedAsync(ctx, Res(resultId, Guid.NewGuid()));

        var ctxSnap = await Repo(ctx).GetContextSnapshotByIdAsync(resultId, Tenant, CancellationToken.None);

        Assert.NotNull(ctxSnap);
        Assert.Equal(resultId,                 ctxSnap.ResultId);
        Assert.Equal("""{"lufe":6}""",         ctxSnap.LufeDataJson);
        Assert.Equal("""{"fiscal":7}""",       ctxSnap.FiscalDataJson);
        Assert.Equal("""{"casfog":8}""",       ctxSnap.CasfogDataJson);
        Assert.Equal("""{"nosis":9}""",        ctxSnap.NosisDataJson);
        // The fifth blob, read WHOLE here — the detail only ever resolves two paths inside it.
        Assert.Equal(Res(resultId, Guid.Empty).BcraDataJson, ctxSnap.BcraDataJson);
    }

    /// <summary>
    /// The distinction the endpoint depends on: an analysis that exists but froze no snapshot returns
    /// an INSTANCE with null blobs — a 200 carrying nulls — not a null projection, which would be a
    /// 404. Projecting into a class rather than a tuple is what keeps these apart. This is the normal
    /// case for NOSIS, which no integration writes today.
    /// </summary>
    [Fact]
    public async Task GetContextSnapshotById_ResultExistsButNoSnapshotFrozen_ReturnsInstanceWithNullBlobs()
    {
        var resultId = Guid.NewGuid();
        using var ctx = NewInMemoryContext();
        var row = Res(resultId, Guid.NewGuid());
        row.LufeDataJson   = null;
        row.FiscalDataJson = null;
        row.CasfogDataJson = null;
        row.NosisDataJson  = null;
        row.BcraDataJson   = null;
        await SeedAsync(ctx, row);

        var ctxSnap = await Repo(ctx).GetContextSnapshotByIdAsync(resultId, Tenant, CancellationToken.None);

        Assert.NotNull(ctxSnap);          // the analysis exists → 200
        Assert.Null(ctxSnap.LufeDataJson);
        Assert.Null(ctxSnap.FiscalDataJson);
        Assert.Null(ctxSnap.CasfogDataJson);
        Assert.Null(ctxSnap.NosisDataJson);
        Assert.Null(ctxSnap.BcraDataJson);
        Assert.Equal(resultId, ctxSnap.ResultId);
    }

    /// <summary>A missing result is a null, which the controller turns into a 404.</summary>
    [Fact]
    public async Task GetContextSnapshotById_UnknownId_ReturnsNull()
    {
        using var ctx = NewInMemoryContext();
        await SeedAsync(ctx, Res(Guid.NewGuid(), Guid.NewGuid()));

        Assert.Null(await Repo(ctx).GetContextSnapshotByIdAsync(Guid.NewGuid(), Tenant, CancellationToken.None));
    }

    /// <summary>Another tenant's snapshots are not readable, even by exact result id.</summary>
    [Fact]
    public async Task GetContextSnapshotById_IsTenantIsolated()
    {
        var resultId = Guid.NewGuid();
        using var ctx = NewInMemoryContext();
        await SeedAsync(ctx, Res(resultId, Guid.NewGuid(), Other));

        Assert.Null(await Repo(ctx).GetContextSnapshotByIdAsync(resultId, Tenant, CancellationToken.None));
    }

    // ── The queries themselves ────────────────────────────────────────────────

    /// <summary>
    /// THE test for #143. The emitted T-SQL for the detail read must not name LufeDataJson,
    /// FiscalDataJson, CasfogDataJson or NosisDataJson — if it does, SQL Server reads them off disk
    /// whether or not anything deserialises them, which is the half-fix the list path suffered before
    /// #139.
    ///
    /// <para>
    /// It must STILL name the blobs the page renders on load. BcraDataJson is not among them and is
    /// not checked here: it is the subject of
    /// <see cref="DetailQuery_ExtractsTheTwoScoreFieldsAndNeverProjectsTheBlob"/>, which needs a
    /// sharper instrument than a substring.
    /// </para>
    /// </summary>
    [Fact]
    public void DetailQuery_SqlNeverNamesAContextSnapshot()
    {
        using var ctx = NewSqlServerContext();

        var sql = Repo(ctx).DetailQuery(Guid.NewGuid(), Tenant).ToQueryString();

        _output.WriteLine(sql);

        foreach (var lazy in ContextSnapshotColumns)
            Assert.DoesNotContain(lazy, sql, StringComparison.Ordinal);

        foreach (var unread in UnreadColumns)
            Assert.DoesNotContain(unread, sql, StringComparison.Ordinal);

        foreach (var needed in DetailBlobColumns)
            Assert.Contains(needed, sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE test for this change, and the one place a plain <c>DoesNotContain("BcraDataJson")</c>
    /// would be actively misleading: the emitted SQL contains that column name either way. What
    /// separates "we extract two scalars from it" from "we ship all 40KB of it" is WHERE the name
    /// appears — inside a JSON_VALUE call, or in the projection list.
    ///
    /// <para>
    /// So the assertion strips every JSON_VALUE(...) span and requires the name to be gone from what
    /// is left. The occurrence count is pinned alongside it: without that, a query that both
    /// extracted the scalars AND projected the blob would still pass the strip check for the two
    /// legitimate uses while quietly shipping a third.
    /// </para>
    ///
    /// <para>
    /// The paths are asserted literally because T-SQL JSON paths are CASE-SENSITIVE and the stored
    /// keys are camelCase — '$.Deuda.Estado' would compile, run, and return NULL for every row in
    /// production while every InMemory test kept passing.
    /// </para>
    /// </summary>
    [Fact]
    public void DetailQuery_ExtractsTheTwoScoreFieldsAndNeverProjectsTheBlob()
    {
        using var ctx = NewSqlServerContext();

        var sql = Repo(ctx).DetailQuery(Guid.NewGuid(), Tenant).ToQueryString();

        _output.WriteLine(sql);

        Assert.Contains("JSON_VALUE", sql, StringComparison.Ordinal);
        Assert.Contains("$.deuda.estado", sql, StringComparison.Ordinal);
        Assert.Contains("$.deuda.situacionMaxima", sql, StringComparison.Ordinal);
        Assert.Contains("$.deuda.fetchedAt", sql, StringComparison.Ordinal);

        // Exactly three calls (the two Score fields and the deuda's fetch date that dates the BCRA
        // source), and exactly three mentions of the column — one per call.
        Assert.Equal(3, Regex.Matches(sql, @"JSON_VALUE\(").Count);
        Assert.Equal(3, Regex.Matches(sql, "BcraDataJson").Count);

        // With the calls removed the column must be gone: anything left is a projection.
        var withoutJsonValueCalls = Regex.Replace(sql, @"JSON_VALUE\([^)]*\)", "<extracted>");
        Assert.DoesNotContain("BcraDataJson", withoutJsonValueCalls, StringComparison.Ordinal);
    }

    /// <summary>
    /// The mirror image: opening Contexto must not pay for the rest of the row. The context query
    /// names the four frozen snapshots and none of the other blobs.
    /// </summary>
    [Fact]
    public void ContextQuery_NamesOnlyTheFourSnapshots()
    {
        using var ctx = NewSqlServerContext();

        var sql = Repo(ctx).ContextQuery(Guid.NewGuid(), Tenant).ToQueryString();

        _output.WriteLine(sql);

        foreach (var own in ContextSnapshotColumns)
            Assert.Contains(own, sql, StringComparison.Ordinal);

        foreach (var other in DetailBlobColumns.Concat(UnreadColumns))
            Assert.DoesNotContain(other, sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other half of the split. The context query is where BcraDataJson is read WHOLE — as a
    /// projected column, not through JSON_VALUE — because the BCRA tab renders all of it. If this
    /// fails, the blob went missing from both reads and the BCRA panel has no data at all.
    /// </summary>
    [Fact]
    public void ContextQuery_AlsoNamesTheBcraBlob()
    {
        using var ctx = NewSqlServerContext();

        var sql = Repo(ctx).ContextQuery(Guid.NewGuid(), Tenant).ToQueryString();

        _output.WriteLine(sql);

        Assert.Contains("BcraDataJson", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("JSON_VALUE", sql, StringComparison.Ordinal);
    }

    // ── Shape locks ───────────────────────────────────────────────────────────

    /// <summary>
    /// The shape lock, mirroring <c>AnalysisListProjection_ExposesExactlyTheColumnsTheListNeeds</c>.
    /// Adding a snapshot property here would put the column straight back into the SELECT above and
    /// silently reintroduce the defect, so the property set is pinned.
    /// </summary>
    [Fact]
    public void AnalysisResultDetailProjection_ExposesExactlyTheColumnsTheDetailNeeds()
    {
        var actual = typeof(AnalysisResultDetailProjection)
            .GetProperties()
            .Select(p => p.Name)
            .ToHashSet();

        var expected = new HashSet<string>
        {
            nameof(AnalysisResultDetailProjection.ResultId),
            nameof(AnalysisResultDetailProjection.AnalysisId),
            nameof(AnalysisResultDetailProjection.TenantId),
            nameof(AnalysisResultDetailProjection.GeneratedAt),
            nameof(AnalysisResultDetailProjection.BalanceJson),
            nameof(AnalysisResultDetailProjection.RatiosJson),
            nameof(AnalysisResultDetailProjection.ScoreBreakdownJson),
            nameof(AnalysisResultDetailProjection.ExecutiveSummary),
            nameof(AnalysisResultDetailProjection.ScoringNarrative),
            nameof(AnalysisResultDetailProjection.RiskAlerts),
            nameof(AnalysisResultDetailProjection.CatalogueAlerts),
            nameof(AnalysisResultDetailProjection.Anomalies),
            nameof(AnalysisResultDetailProjection.AiSuggestedScore),
            nameof(AnalysisResultDetailProjection.AiSuggestedLevel),
            nameof(AnalysisResultDetailProjection.AiInputTokens),
            nameof(AnalysisResultDetailProjection.AiOutputTokens),
            nameof(AnalysisResultDetailProjection.ValidationStatus),
            nameof(AnalysisResultDetailProjection.EnrichmentStatus),
            nameof(AnalysisResultDetailProjection.EnrichmentNotes),
            // Extracted from BcraDataJson by JSON_VALUE — the column itself is NOT here, and
            // restoring it would put all 40KB back into the SELECT.
            nameof(AnalysisResultDetailProjection.BcraDeudaEstadoRaw),
            nameof(AnalysisResultDetailProjection.BcraDeudaSituacionMaximaRaw),
            nameof(AnalysisResultDetailProjection.BcraDeudaFetchedAtRaw),
            // Re-score (DDL 073): three scalars, and the latest history row's SourcesJson — a short
            // array of dates from a correlated subquery, never a snapshot blob.
            nameof(AnalysisResultDetailProjection.RescoredAt),
            nameof(AnalysisResultDetailProjection.RescoredBy),
            nameof(AnalysisResultDetailProjection.RescoreCount),
            nameof(AnalysisResultDetailProjection.LatestRescoreSourcesJson)
        };

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// The context projection stays six columns wide. A seventh blob property here would mean the
    /// Contexto section paying for data it does not render.
    /// </summary>
    [Fact]
    public void AnalysisContextProjection_ExposesExactlySixColumns()
    {
        var actual = typeof(AnalysisContextProjection)
            .GetProperties()
            .Select(p => p.Name)
            .ToHashSet();

        var expected = new HashSet<string>
        {
            nameof(AnalysisContextProjection.ResultId),
            nameof(AnalysisContextProjection.LufeDataJson),
            nameof(AnalysisContextProjection.FiscalDataJson),
            nameof(AnalysisContextProjection.CasfogDataJson),
            nameof(AnalysisContextProjection.NosisDataJson),
            nameof(AnalysisContextProjection.BcraDataJson)
        };

        Assert.Equal(expected, actual);
    }
}
