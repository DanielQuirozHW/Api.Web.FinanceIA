// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-31

using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// Query-level tests for <see cref="AlertRepository.ExistsByTypeInPeriodAsync"/>, run against the
/// EF Core in-memory provider (a database provider, not a mocking library) so the real WHERE
/// clause is exercised.
///
/// <para>
/// TENANT ISOLATION IS PROVEN HERE, not in <c>BillingAlertServiceTests</c>: that suite stubs this
/// repository away, so it can show the service passes a tenant id but never that the query honours
/// it. There is NO EF global query filter in this project, so the <c>TenantId</c> predicate written
/// here is the only thing stopping one client's quota alert from silencing another's.
/// </para>
///
/// <para>
/// The window is half-open — <c>[PeriodStart, PeriodEnd)</c> — because it is fed
/// <c>BillingUsageSnapshot.PeriodStart</c>/<c>PeriodEnd</c>, which are the 1st of this month and the
/// 1st of the next. A closed interval would let an alert written in the first instant of September
/// also count as August's, and the month a quota alert belongs to is the whole basis of the
/// once-per-month guarantee.
/// </para>
/// </summary>
public sealed class AlertRepositoryExistsByTypeTests
{
    private const string Tenant    = "tenant-1";
    private const string Other     = "tenant-2";
    private const string AlertType = "BILLING_ANALYSIS_QUOTA_80";

    private static readonly DateTime PeriodStart = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PeriodEnd   = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static FinanciaDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"alerts-{Guid.NewGuid()}")
            .Options;
        return new FinanciaDbContext(options);
    }

    private static AlertRepository NewRepository(FinanciaDbContext ctx)
        => new(ctx, NullLogger<AlertRepository>.Instance);

    private static Alert BillingAlert(string tenantId, string alertType, DateTime createdAt) => new()
    {
        AlertId    = Guid.NewGuid(),
        TenantId   = tenantId,
        EntityId   = null,          // system-level, exactly as BillingAlertService writes it
        AnalysisId = null,
        AlertType  = alertType,
        Severity   = "Medium",
        Title      = "Consumo del plan al 80%",
        Description = "Usaste el 82% de los análisis de tu plan (100 análisis) este mes.",
        MetricKey      = "analyses_percent",
        MetricValue    = 82m,
        ThresholdValue = 80m,
        IsRead         = false,
        IsResolved     = false,
        CreatedAt      = createdAt
    };

    private static async Task SeedAsync(FinanciaDbContext ctx, params Alert[] alerts)
    {
        ctx.Alerts.AddRange(alerts);
        ctx.ChangeTracker.DetectChanges(); // AutoDetectChanges is disabled on the context
        await ctx.SaveChangesAsync();
    }

    private static Task<bool> ExistsAsync(FinanciaDbContext ctx)
        => NewRepository(ctx).ExistsByTypeInPeriodAsync(
            Tenant, AlertType, PeriodStart, PeriodEnd, CancellationToken.None);

    // ── The happy path ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ExistsByTypeInPeriodAsync_WhenTheAlertIsInTheWindow_ReturnsTrue()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, BillingAlert(Tenant, AlertType, new DateTime(2026, 8, 14, 9, 30, 0, DateTimeKind.Utc)));

        Assert.True(await ExistsAsync(ctx));
    }

    [Fact]
    public async Task ExistsByTypeInPeriodAsync_WhenThereAreNoAlertsAtAll_ReturnsFalse()
    {
        await using var ctx = NewContext();

        Assert.False(await ExistsAsync(ctx));
    }

    // ── Tenant isolation ────────────────────────────────────────────────────────

    /// <summary>
    /// Another tenant's identical alert must be invisible. If it were not, one client crossing 80%
    /// would suppress every other client's warning for the rest of the month.
    /// </summary>
    [Fact]
    public async Task ExistsByTypeInPeriodAsync_IgnoresAnotherTenantsAlert()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, BillingAlert(Other, AlertType, new DateTime(2026, 8, 14, 9, 30, 0, DateTimeKind.Utc)));

        Assert.False(await ExistsAsync(ctx));
    }

    // ── Type isolation ──────────────────────────────────────────────────────────

    /// <summary>
    /// The 100% alert must not satisfy a check for the 80% one. They are distinct types precisely
    /// so both can fire, in order, within a single month.
    /// </summary>
    [Fact]
    public async Task ExistsByTypeInPeriodAsync_IgnoresADifferentAlertType()
    {
        await using var ctx = NewContext();
        await SeedAsync(
            ctx,
            BillingAlert(Tenant, "BILLING_ANALYSIS_QUOTA_100", new DateTime(2026, 8, 14, 9, 30, 0, DateTimeKind.Utc)),
            BillingAlert(Tenant, "HIGH_LEVERAGE", new DateTime(2026, 8, 15, 9, 30, 0, DateTimeKind.Utc)));

        Assert.False(await ExistsAsync(ctx));
    }

    // ── The window is half-open ─────────────────────────────────────────────────

    /// <summary>
    /// PeriodStart is INCLUSIVE: an alert written in the first instant of the month belongs to it.
    /// </summary>
    [Fact]
    public async Task ExistsByTypeInPeriodAsync_IncludesAnAlertExactlyAtPeriodStart()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, BillingAlert(Tenant, AlertType, PeriodStart));

        Assert.True(await ExistsAsync(ctx));
    }

    /// <summary>
    /// PeriodEnd is EXCLUSIVE: the first instant of September is September's, and must not silence
    /// an August check — nor, read the other way, be silenced by August.
    /// </summary>
    [Fact]
    public async Task ExistsByTypeInPeriodAsync_ExcludesAnAlertExactlyAtPeriodEnd()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, BillingAlert(Tenant, AlertType, PeriodEnd));

        Assert.False(await ExistsAsync(ctx));
    }

    [Fact]
    public async Task ExistsByTypeInPeriodAsync_IgnoresAnAlertFromAPreviousMonth()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, BillingAlert(Tenant, AlertType, new DateTime(2026, 7, 31, 23, 59, 59, DateTimeKind.Utc)));

        Assert.False(await ExistsAsync(ctx));
    }

    /// <summary>
    /// The month rolls over on its own: last month's alert does not carry into this one, which is
    /// what makes the quota warning recur every billing period without any reset job.
    /// </summary>
    [Fact]
    public async Task ExistsByTypeInPeriodAsync_WithLastMonthsAlertOnly_AllowsThisMonthToFireAgain()
    {
        await using var ctx = NewContext();
        await SeedAsync(
            ctx,
            BillingAlert(Tenant, AlertType, new DateTime(2026, 7, 14, 9, 30, 0, DateTimeKind.Utc)),
            BillingAlert(Tenant, AlertType, new DateTime(2026, 6, 14, 9, 30, 0, DateTimeKind.Utc)));

        Assert.False(await ExistsAsync(ctx));
    }
}
