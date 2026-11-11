// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-10

using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// <see cref="NosisReportRepository"/> (DDL 087): tenant isolation on every call, writes that touch
/// the three report columns and nothing else, and reads whose SQL names the report blob and no other.
/// </summary>
public sealed class NosisReportRepositoryTests
{
    private const string Tenant = "tenant-1";
    private const string Other  = "tenant-2";

    private static readonly string[] OtherBlobColumns =
    [
        "FiscalDataJson", "LufeDataJson", "BcraDataJson", "CasfogDataJson", "NosisDataJson"
    ];

    private readonly ITestOutputHelper _output;

    public NosisReportRepositoryTests(ITestOutputHelper output) => _output = output;

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static DbContextOptions<FinanciaDbContext> InMemoryOptions()
        => new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"nosis-report-{Guid.NewGuid()}")
            .Options;

    private static FinanciaDbContext NewSqlServerContext()
        => new(new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseSqlServer("Server=localhost;Database=none;Trusted_Connection=False;")
            .Options);

    private static NosisReportRepository Repo(FinanciaDbContext ctx)
        => new(ctx, NullLogger<NosisReportRepository>.Instance);

    private static Entity Ent(Guid id, string tenantId = Tenant) => new()
    {
        EntityId           = id,
        TenantId           = tenantId,
        CUIT               = "30-71234567-1",
        BusinessName       = "Sintética S.A.",
        CreatedAt          = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        UpdatedAt          = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
        CreatedBy          = Guid.NewGuid(),
        NosisDataJson      = """{"ws":1}""",
        NosisDataUpdatedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
        BcraDataJson       = """{"bcra":1}"""
    };

    private static async Task SeedAsync(DbContextOptions<FinanciaDbContext> options, params Entity[] rows)
    {
        using var ctx = new FinanciaDbContext(options);
        ctx.Entities.AddRange(rows);
        ctx.ChangeTracker.DetectChanges();
        await ctx.SaveChangesAsync();
    }

    private static async Task<Entity> ReloadAsync(DbContextOptions<FinanciaDbContext> options, Guid id)
    {
        using var ctx = new FinanciaDbContext(options);
        return await ctx.Entities.AsNoTracking().SingleAsync(x => x.EntityId == id);
    }

    private static readonly DateTime ReportDate = new(2026, 11, 3, 14, 36, 0, DateTimeKind.Unspecified);
    private static readonly DateTime ImportedAt = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    // ── Reads ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_OfAnEntityWithNoReport_IsAnInstanceWithNullJson()
    {
        var options = InMemoryOptions();
        var id = Guid.NewGuid();
        await SeedAsync(options, Ent(id));

        using var ctx = new FinanciaDbContext(options);
        var snapshot = await Repo(ctx).GetAsync(id, Tenant, CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal(id, snapshot!.EntityId);
        Assert.Null(snapshot.Json);
        Assert.Null(snapshot.ReportDate);
        Assert.Null(snapshot.ImportedAt);
    }

    [Fact]
    public async Task SaveThenGet_RoundTripsTheThreeColumns_AndTheStoredSha()
    {
        var options = InMemoryOptions();
        var id = Guid.NewGuid();
        await SeedAsync(options, Ent(id));

        using (var ctx = new FinanciaDbContext(options))
        {
            Assert.True(await Repo(ctx).SaveAsync(
                id, Tenant, """{"fileSha256":"abc123","parserVersion":"nm-1.0"}""", ReportDate, ImportedAt, CancellationToken.None));
        }

        using var read = new FinanciaDbContext(options);
        var snapshot = await Repo(read).GetAsync(id, Tenant, CancellationToken.None);
        Assert.Contains("abc123", snapshot!.Json!, StringComparison.Ordinal);
        Assert.Equal(ReportDate, snapshot.ReportDate);
        Assert.Equal(ImportedAt, snapshot.ImportedAt);
        Assert.Equal(("abc123", "nm-1.0"), await Repo(read).GetStoredFingerprintAsync(id, Tenant, CancellationToken.None));
    }

    [Fact]
    public async Task Save_TouchesOnlyTheReportColumns()
    {
        var options = InMemoryOptions();
        var id = Guid.NewGuid();
        await SeedAsync(options, Ent(id));

        using (var ctx = new FinanciaDbContext(options))
            await Repo(ctx).SaveAsync(id, Tenant, """{"fileSha256":"x"}""", ReportDate, ImportedAt, CancellationToken.None);

        var e = await ReloadAsync(options, id);
        Assert.Equal("""{"ws":1}""", e.NosisDataJson);
        Assert.Equal(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), e.NosisDataUpdatedAt);
        Assert.Equal("""{"bcra":1}""", e.BcraDataJson);
        Assert.Equal(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), e.UpdatedAt);
    }

    [Fact]
    public async Task Clear_RemovesTheThreeColumns_OnceThenReportsNothingToClear()
    {
        var options = InMemoryOptions();
        var id = Guid.NewGuid();
        await SeedAsync(options, Ent(id));

        using (var ctx = new FinanciaDbContext(options))
            await Repo(ctx).SaveAsync(id, Tenant, """{"fileSha256":"x"}""", ReportDate, ImportedAt, CancellationToken.None);

        using (var ctx = new FinanciaDbContext(options))
            Assert.True(await Repo(ctx).ClearAsync(id, Tenant, CancellationToken.None));

        using (var ctx = new FinanciaDbContext(options))
            Assert.False(await Repo(ctx).ClearAsync(id, Tenant, CancellationToken.None));

        var e = await ReloadAsync(options, id);
        Assert.Null(e.NosisReportJson);
        Assert.Null(e.NosisReportDate);
        Assert.Null(e.NosisReportImportedAt);
        Assert.Equal("""{"ws":1}""", e.NosisDataJson);
    }

    // ── Tenant isolation ──────────────────────────────────────────────────────

    [Fact]
    public async Task EveryCall_IsTenantIsolated()
    {
        var options = InMemoryOptions();
        var id = Guid.NewGuid();
        var foreign = Ent(id, Other);
        foreign.NosisReportJson       = """{"fileSha256":"theirs"}""";
        foreign.NosisReportDate       = ReportDate;
        foreign.NosisReportImportedAt = ImportedAt;
        await SeedAsync(options, foreign);

        using (var ctx = new FinanciaDbContext(options))
        {
            var repo = Repo(ctx);
            Assert.Null(await repo.GetAsync(id, Tenant, CancellationToken.None));
            Assert.Equal((null, null), await repo.GetStoredFingerprintAsync(id, Tenant, CancellationToken.None));
            Assert.False(await repo.SaveAsync(id, Tenant, """{"fileSha256":"mine"}""", ReportDate, ImportedAt, CancellationToken.None));
            Assert.False(await repo.ClearAsync(id, Tenant, CancellationToken.None));
        }

        var e = await ReloadAsync(options, id);
        Assert.Equal("""{"fileSha256":"theirs"}""", e.NosisReportJson);
        Assert.Equal(ReportDate, e.NosisReportDate);
    }

    [Fact]
    public async Task Missing_Entity_IsNullOrFalse()
    {
        var options = InMemoryOptions();
        using var ctx = new FinanciaDbContext(options);
        var repo = Repo(ctx);
        var id = Guid.NewGuid();

        Assert.Null(await repo.GetAsync(id, Tenant, CancellationToken.None));
        Assert.False(await repo.SaveAsync(id, Tenant, "{}", ReportDate, ImportedAt, CancellationToken.None));
        Assert.False(await repo.ClearAsync(id, Tenant, CancellationToken.None));
    }

    // ── SQL shape ─────────────────────────────────────────────────────────────

    [Fact]
    public void SnapshotQuery_NamesTheReportColumns_AndNoOtherBlob()
    {
        using var ctx = NewSqlServerContext();
        var sql = Repo(ctx).SnapshotQuery(Guid.NewGuid(), Tenant).ToQueryString();
        _output.WriteLine(sql);

        Assert.Contains("NosisReportJson", sql, StringComparison.Ordinal);
        Assert.Contains("NosisReportDate", sql, StringComparison.Ordinal);
        Assert.Contains("NosisReportImportedAt", sql, StringComparison.Ordinal);
        Assert.Contains("[TenantId]", sql, StringComparison.Ordinal);
        foreach (var other in OtherBlobColumns)
            Assert.DoesNotContain(other, sql, StringComparison.Ordinal);
    }

    [Fact]
    public void SubjectQuery_ReadsCuitAndPersonType_AndNoBlob()
    {
        using var ctx = NewSqlServerContext();
        var sql = Repo(ctx).SubjectQuery(Guid.NewGuid(), Tenant).ToQueryString();
        _output.WriteLine(sql);

        Assert.Contains("[CUIT]", sql, StringComparison.Ordinal);
        Assert.Contains("[PersonType]", sql, StringComparison.Ordinal);
        Assert.Contains("[TenantId]", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("NosisReportJson", sql, StringComparison.Ordinal);
        foreach (var other in OtherBlobColumns)
            Assert.DoesNotContain(other, sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetSubject_IsTenantIsolated()
    {
        var options = InMemoryOptions();
        var id = Guid.NewGuid();
        await SeedAsync(options, Ent(id, Other));

        using var ctx = new FinanciaDbContext(options);
        Assert.Null(await Repo(ctx).GetSubjectAsync(id, Tenant, CancellationToken.None));
        Assert.Equal("30-71234567-1", (await Repo(ctx).GetSubjectAsync(id, Other, CancellationToken.None))!.CUIT);
    }

    [Fact]
    public void StoredFingerprintQuery_ReadsShaAndVersionInsideTheDatabase()
    {
        using var ctx = NewSqlServerContext();
        var sql = Repo(ctx).StoredFingerprintQuery(Guid.NewGuid(), Tenant).ToQueryString();
        _output.WriteLine(sql);

        Assert.Contains("JSON_VALUE([e].[NosisReportJson], N'$.fileSha256')", sql, StringComparison.Ordinal);
        Assert.Contains("JSON_VALUE([e].[NosisReportJson], N'$.parserVersion')", sql, StringComparison.Ordinal);
        Assert.Contains("[TenantId]", sql, StringComparison.Ordinal);
        foreach (var other in OtherBlobColumns)
            Assert.DoesNotContain(other, sql, StringComparison.Ordinal);
    }
}
