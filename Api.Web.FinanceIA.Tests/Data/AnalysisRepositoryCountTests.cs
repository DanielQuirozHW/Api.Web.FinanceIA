// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-31

using Api.Web.FinanceIA.Clases.Time;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// Query-level tests for <see cref="AnalysisRepository.CountByTenantInPeriodAsync"/>, the numerator
/// of the analysis billing quota. Run against the EF Core in-memory provider so the actual WHERE
/// clause is exercised rather than a stub's return value.
///
/// <para>
/// TENANT ISOLATION IS PROVEN HERE, not in the service tests — those stub the repository away, so
/// they can show a tenant id was passed but never that the query honours it. The project has no EF
/// global query filter, so this predicate is the only thing keeping one client's consumption out
/// of another's bill.
/// </para>
///
/// <para>
/// The month window is half-open, [first instant of the month, first instant of the next). Both
/// edges are tested explicitly: an off-by-one here either bills a tenant twice for the same
/// analysis or lets one run free every month, and neither is visible without a test that names
/// the exact instant.
/// </para>
///
/// <para>
/// The repository no longer builds that window — it receives it, so that the analysis count and the
/// chat token count cannot drift onto different periods. These tests therefore pass an explicit UTC
/// pair. <see cref="UtcMonth"/> keeps the historical UTC-calendar boundaries so every edge case
/// below still reads as the same assertion it always did; <see cref="ArMonth"/> covers what the
/// caller actually passes in production, an ARGENTINE calendar month.
/// </para>
/// </summary>
public sealed class AnalysisRepositoryCountTests
{
    private const string Tenant = "tenant-1";
    private const string Other  = "tenant-2";

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static FinanciaDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"analyses-count-{Guid.NewGuid()}")
            .Options;
        return new FinanciaDbContext(options);
    }

    private static AnalysisRepository BuildSut(FinanciaDbContext ctx)
        => new(ctx, NullLogger<AnalysisRepository>.Instance);

    private static Analysis Row(DateTime createdAt, string tenantId = Tenant, string status = "PendingReview") => new()
    {
        AnalysisId    = Guid.NewGuid(),
        TenantId      = tenantId,
        EntityId      = Guid.NewGuid(),
        DocumentId    = Guid.NewGuid(),
        RequestedBy   = Guid.NewGuid(),
        Status        = status,
        FiscalYear    = 2026,
        AiModel       = "claude-sonnet-4-6",
        AiTemperature = 0.00m,
        PromptVersion = "v1",
        SentToSGR     = false,
        CreatedAt     = createdAt
    };

    private static async Task SeedAsync(FinanciaDbContext ctx, params Analysis[] analyses)
    {
        ctx.Analyses.AddRange(analyses);
        ctx.ChangeTracker.DetectChanges();   // AutoDetectChanges is disabled on the context
        await ctx.SaveChangesAsync();
    }

    private static DateTime Utc(int year, int month, int day, int hour = 12, int minute = 0)
        => new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    /// <summary>A half-open UTC calendar month — the window shape these tests were written against.</summary>
    private static (DateTime Start, DateTime End) UtcMonth(int year, int month)
    {
        var start = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        return (start, start.AddMonths(1));
    }

    /// <summary>A half-open ARGENTINE calendar month, as BillingUsageService supplies it.</summary>
    private static (DateTime Start, DateTime End) ArMonth(int year, int month)
    {
        var period = ArgentineTime.PeriodFor(year, month);
        return (period.StartUtc, period.EndUtc);
    }

    private static Task<int> CountInUtcMonth(FinanciaDbContext ctx, string tenantId, int year, int month)
    {
        var (start, end) = UtcMonth(year, month);
        return BuildSut(ctx).CountByTenantInPeriodAsync(tenantId, start, end, CancellationToken.None);
    }

    private static Task<int> CountInArMonth(FinanciaDbContext ctx, string tenantId, int year, int month)
    {
        var (start, end) = ArMonth(year, month);
        return BuildSut(ctx).CountByTenantInPeriodAsync(tenantId, start, end, CancellationToken.None);
    }

    // ── Tenant isolation ────────────────────────────────────────────────────────

    [Fact]
    public async Task CountByTenantInPeriodAsync_CountsOnlyTheGivenTenant()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx,
            Row(Utc(2026, 8, 5)),
            Row(Utc(2026, 8, 6)),
            Row(Utc(2026, 8, 7), tenantId: Other),
            Row(Utc(2026, 8, 8), tenantId: Other));

        var count = await CountInUtcMonth(ctx, Tenant, 2026, 8);

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task CountByTenantInPeriodAsync_WithNoRowsForTheTenant_ReturnsZero()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx, Row(Utc(2026, 8, 5), tenantId: Other));

        var count = await CountInUtcMonth(ctx, Tenant, 2026, 8);

        Assert.Equal(0, count);
    }

    // ── Month boundaries (half-open) ────────────────────────────────────────────

    /// <summary>
    /// The first instant of the month is INSIDE the window. An analysis triggered at
    /// 00:00:00.000 on the 1st belongs to that month, not the previous one.
    /// </summary>
    [Fact]
    public async Task CountByTenantInPeriodAsync_IncludesTheFirstInstantOfTheMonth()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx, Row(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)));

        var count = await CountInUtcMonth(ctx, Tenant, 2026, 8);

        Assert.Equal(1, count);
    }

    /// <summary>
    /// The last representable instant before the next month is still inside. This is the analysis
    /// triggered at 23:59:59.999 on the 31st — the one an inclusive upper bound would double-count
    /// into September as well.
    /// </summary>
    [Fact]
    public async Task CountByTenantInPeriodAsync_IncludesTheLastInstantOfTheMonth()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx, Row(new DateTime(2026, 8, 31, 23, 59, 59, 999, DateTimeKind.Utc)));

        Assert.Equal(1, await CountInUtcMonth(ctx, Tenant, 2026, 8));
        Assert.Equal(0, await CountInUtcMonth(ctx, Tenant, 2026, 9));
    }

    /// <summary>The first instant of the NEXT month is outside — the window is half-open.</summary>
    [Fact]
    public async Task CountByTenantInPeriodAsync_ExcludesTheFirstInstantOfTheNextMonth()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx, Row(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)));

        var count = await CountInUtcMonth(ctx, Tenant, 2026, 8);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task CountByTenantInPeriodAsync_ExcludesRowsFromEarlierMonths()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx,
            Row(Utc(2026, 7, 31, 23)),
            Row(Utc(2026, 8, 1, 1)));

        var count = await CountInUtcMonth(ctx, Tenant, 2026, 8);

        Assert.Equal(1, count);
    }

    /// <summary>December must roll into January of the FOLLOWING year, not month 13.</summary>
    [Fact]
    public async Task CountByTenantInPeriodAsync_ForDecember_RollsOverIntoTheNextYear()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx,
            Row(Utc(2026, 12, 31, 23)),
            Row(Utc(2027, 1, 1, 0, 1)));

        Assert.Equal(1, await CountInUtcMonth(ctx, Tenant, 2026, 12));
        Assert.Equal(1, await CountInUtcMonth(ctx, Tenant, 2027, 1));
    }

    // ── Argentine month boundaries — the window the caller actually passes ──────

    /// <summary>
    /// The three hours that motivated the whole change. An analysis triggered at 22:00 ART on 31
    /// August is 01:00 UTC on 1 September, so a UTC-calendar window counted it against September
    /// while the tenant — and the back office — considered it August work. It must land in August,
    /// and it must land there ONLY once.
    /// </summary>
    [Fact]
    public async Task CountByTenantInPeriodAsync_ForAnAnalysisInTheLastHoursOfAnArgentineMonth_CountsItInThatMonth()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx, Row(Utc(2026, 9, 1, 1)));   // 22:00 ART on 31 August

        Assert.Equal(1, await CountInArMonth(ctx, Tenant, 2026, 8));
        Assert.Equal(0, await CountInArMonth(ctx, Tenant, 2026, 9));
    }

    /// <summary>
    /// The other side of the same edge, thirty minutes later and on the same UTC day: 00:30 ART on
    /// 1 September really is September.
    /// </summary>
    [Fact]
    public async Task CountByTenantInPeriodAsync_ForAnAnalysisJustAfterArgentineMidnight_CountsItInTheNewMonth()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx, Row(Utc(2026, 9, 1, 3, 30)));

        Assert.Equal(0, await CountInArMonth(ctx, Tenant, 2026, 8));
        Assert.Equal(1, await CountInArMonth(ctx, Tenant, 2026, 9));
    }

    /// <summary>
    /// Consecutive Argentine months tile: every analysis in a run spanning the boundary is counted
    /// exactly once across the two periods, with none lost and none double-counted.
    /// </summary>
    [Fact]
    public async Task CountByTenantInPeriodAsync_AcrossAnArgentineBoundary_CountsEveryRowExactlyOnce()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx,
            Row(Utc(2026, 8, 31, 20)),      // 17:00 ART, 31 Aug
            Row(Utc(2026, 9, 1, 1)),        // 22:00 ART, 31 Aug
            Row(Utc(2026, 9, 1, 2, 59)),    // 23:59 ART, 31 Aug
            Row(Utc(2026, 9, 1, 3)),        // 00:00 ART, 1 Sep — the boundary instant
            Row(Utc(2026, 9, 1, 6)));       // 03:00 ART, 1 Sep

        var august = await CountInArMonth(ctx, Tenant, 2026, 8);
        var september = await CountInArMonth(ctx, Tenant, 2026, 9);

        Assert.Equal(3, august);
        Assert.Equal(2, september);
        Assert.Equal(5, august + september);
    }

    /// <summary>Argentine New Year's Eve: 23:00 ART on 31 December is already 2027 in UTC.</summary>
    [Fact]
    public async Task CountByTenantInPeriodAsync_OnArgentineNewYearsEve_CountsInTheOldYear()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx, Row(Utc(2027, 1, 1, 2)));   // 23:00 ART on 31 December 2026

        Assert.Equal(1, await CountInArMonth(ctx, Tenant, 2026, 12));
        Assert.Equal(0, await CountInArMonth(ctx, Tenant, 2027, 1));
    }

    // ── Status is not a filter ──────────────────────────────────────────────────

    /// <summary>
    /// Every status counts. A Rejected analysis was still an analysis Claude was paid for, and so
    /// is the re-analysis that replaces it — this is the test that fails if someone adds a Status
    /// predicate to "not charge for rejected work".
    /// </summary>
    [Theory]
    [InlineData("PendingReview")]
    [InlineData("Reviewed")]
    [InlineData("Approved")]
    [InlineData("Rejected")]
    public async Task CountByTenantInPeriodAsync_CountsEveryStatus(string status)
    {
        using var ctx = NewContext();
        await SeedAsync(ctx, Row(Utc(2026, 8, 5), status: status));

        var count = await CountInUtcMonth(ctx, Tenant, 2026, 8);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task CountByTenantInPeriodAsync_CountsRejectedAndItsReanalysisSeparately()
    {
        using var ctx = NewContext();
        var documentId = Guid.NewGuid();

        var rejected = Row(Utc(2026, 8, 5), status: "Rejected");
        rejected.DocumentId = documentId;
        var redone = Row(Utc(2026, 8, 6));
        redone.DocumentId = documentId;

        await SeedAsync(ctx, rejected, redone);

        var count = await CountInUtcMonth(ctx, Tenant, 2026, 8);

        Assert.Equal(2, count);
    }
}
