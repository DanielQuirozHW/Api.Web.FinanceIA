// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-25

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// Covers <see cref="CupoAlertService"/>: which event alerts it raises, that it keeps exactly one open
/// alert per (type, key), that it updates and auto-resolves them as the cupo or the PyME changes, and that
/// a user's dismissal holds until the situation gets worse. This file holds the helpers and the
/// CUPO_OPERATIONS_EXPIRED / CUPO_UTILIZATION_HIGH cases; the other types live in the partial files beside it.
///
/// <para>
/// Runs the REAL <see cref="CreditLineRepository"/>, <see cref="DashboardRepository"/> and
/// <see cref="AlertRepository"/> on the EF Core
/// in-memory provider (a database provider, not a mocking library), so the TenantId predicates of every
/// query the service relies on are exercised — there is no EF global query filter to fall back on. Each
/// pass gets a fresh DbContext on the same named database, the way each GET /api/alerts gets its own
/// request scope.
/// </para>
///
/// <para>
/// The instant is pinned: whether an operation is 'Vencida' is CreditLineRules.EffectiveStatus(op,
/// nowUtc), decided on the UTC date, and these tests must not depend on the day they run.
/// </para>
/// </summary>
public sealed partial class CupoAlertServiceTests
{
    private const string Tenant = "tenant-cupo-alerts-1";
    private const string Other  = "tenant-cupo-alerts-2";

    private static readonly DateTime Now      = new(2026, 10, 25, 15, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Past     = new(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Future   = new(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _databaseName = $"cupo-alerts-{Guid.NewGuid()}";

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private FinanciaDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase(_databaseName)
            .Options;
        return new FinanciaDbContext(options);
    }

    private static CupoAlertService NewService(FinanciaDbContext ctx)
        => new(
            new CreditLineRepository(ctx, NullLogger<CreditLineRepository>.Instance),
            new DashboardRepository(ctx),
            new AlertRepository(ctx, NullLogger<AlertRepository>.Instance),
            NullLogger<CupoAlertService>.Instance);

    /// <summary>One reconciliation pass in its own scope, like one GET /api/alerts.</summary>
    private async Task ReconcileAsync(DateTime? nowUtc = null, string tenantId = Tenant)
    {
        await using var ctx = NewContext();
        await NewService(ctx).ReconcileAsync(tenantId, nowUtc ?? Now, CancellationToken.None);
    }

    private static CreditLine Cupo(
        decimal amount = 1_000m, string status = "Active", string currency = "ARS", string tenantId = Tenant,
        Guid? originAnalysisId = null, Guid? lastRaiseAnalysisId = null) => new()
    {
        CreditLineId        = Guid.NewGuid(),
        TenantId            = tenantId,
        EntityId            = Guid.NewGuid(),
        Amount              = amount,
        Currency            = currency,
        Status              = status,
        CreatedByUserId     = Guid.NewGuid(),
        CreatedAt           = new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc),
        OriginAnalysisId    = originAnalysisId,
        LastRaiseAnalysisId = lastRaiseAnalysisId,
    };

    private static CreditLineOperation Operacion(
        CreditLine cupo, decimal amount, string status = CreditLineRules.Vigente,
        DateTime? validUntil = null, string? tenantId = null) => new()
    {
        OperationId     = Guid.NewGuid(),
        TenantId        = tenantId ?? cupo.TenantId,
        CreditLineId    = cupo.CreditLineId,
        AnalysisId      = Guid.NewGuid(),
        Type            = 0,
        Amount          = amount,
        Currency        = cupo.Currency,
        Status          = status,
        ValidUntil      = validUntil,
        CreatedByUserId = Guid.NewGuid(),
        CreatedAt       = new DateTime(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc),
    };

    private async Task SeedAsync(params object[] rows)
    {
        await using var ctx = NewContext();
        foreach (var row in rows)
        {
            switch (row)
            {
                case CreditLine line:           ctx.CreditLines.Add(line); break;
                case CreditLineOperation op:    ctx.CreditLineOperations.Add(op); break;
                case Alert alert:               ctx.Alerts.Add(alert); break;
                case Entity entity:             ctx.Entities.Add(entity); break;
                case Analysis analysis:         ctx.Analyses.Add(analysis); break;
                case AnalysisResult result:     ctx.AnalysisResults.Add(result); break;
                default: throw new ArgumentException($"Unexpected seed row {row.GetType().Name}");
            }
        }
        ctx.ChangeTracker.DetectChanges(); // AutoDetectChanges is disabled on the context
        await ctx.SaveChangesAsync();
    }

    private async Task SetOperationStatusAsync(Guid operationId, string status)
    {
        await using var ctx = NewContext();
        var op = await ctx.CreditLineOperations.SingleAsync(o => o.OperationId == operationId);
        op.Status = status;
        ctx.ChangeTracker.DetectChanges();
        await ctx.SaveChangesAsync();
    }

    private async Task SetCupoStatusAsync(Guid creditLineId, string status)
    {
        await using var ctx = NewContext();
        var line = await ctx.CreditLines.SingleAsync(c => c.CreditLineId == creditLineId);
        line.Status = status;
        ctx.ChangeTracker.DetectChanges();
        await ctx.SaveChangesAsync();
    }

    private async Task ResolveByUserAsync(Guid alertId, DateTime resolvedAt)
    {
        await using var ctx = NewContext();
        var alert = await ctx.Alerts.SingleAsync(a => a.AlertId == alertId);
        alert.IsResolved = true;
        alert.ResolvedBy = Guid.NewGuid();
        alert.ResolvedAt = resolvedAt;
        ctx.ChangeTracker.DetectChanges();
        await ctx.SaveChangesAsync();
    }

    private async Task MarkReadAsync(Guid alertId)
    {
        await using var ctx = NewContext();
        var ok = await new AlertRepository(ctx, NullLogger<AlertRepository>.Instance)
            .MarkAsReadAsync(alertId, Tenant, Guid.NewGuid(), CancellationToken.None);
        Assert.True(ok);
    }

    private async Task<List<Alert>> AlertsAsync(string tenantId = Tenant)
    {
        await using var ctx = NewContext();
        return await ctx.Alerts.AsNoTracking()
            .Where(a => a.TenantId == tenantId)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync();
    }

    private async Task<List<Alert>> OpenAsync(string alertType, string tenantId = Tenant)
        => (await AlertsAsync(tenantId)).Where(a => a.AlertType == alertType && !a.IsResolved).ToList();

    private async Task<List<Alert>> OfTypeAsync(string alertType, string tenantId = Tenant)
        => (await AlertsAsync(tenantId)).Where(a => a.AlertType == alertType).ToList();

    private static Alert OpenCupoAlert(
        string alertType, Guid creditLineId, DateTime createdAt, string tenantId = Tenant,
        string severity = "High", decimal metricValue = 1m) => new()
    {
        AlertId        = Guid.NewGuid(),
        TenantId       = tenantId,
        EntityId       = Guid.NewGuid(),
        AlertType      = alertType,
        Severity       = severity,
        Title          = "t",
        Description    = "d",
        MetricKey      = EventAlertTypes.CupoMetricKey(creditLineId),
        MetricValue    = metricValue,
        ThresholdValue = 0m,
        CreatedAt      = createdAt,
    };

    // ── CUPO_OPERATIONS_EXPIRED ─────────────────────────────────────────────────

    [Fact]
    public async Task Expired_CreatesOneAlertPerCupo_WithItsIdentityAndCopy()
    {
        var originId = Guid.NewGuid();
        var raiseId  = Guid.NewGuid();
        var a = Cupo(amount: 5_000_000m, originAnalysisId: originId, lastRaiseAnalysisId: raiseId);
        var b = Cupo(amount: 800m, currency: "USD", originAnalysisId: originId);
        await SeedAsync(
            a, Operacion(a, 1_000_000m, validUntil: Past), Operacion(a, 500_000m, validUntil: Past.AddDays(5)),
            Operacion(a, 100m, validUntil: Future),
            b, Operacion(b, 300m, validUntil: Past));

        await ReconcileAsync();

        var open = await OpenAsync(EventAlertTypes.CupoOperationsExpired);
        Assert.Equal(2, open.Count);

        var alertA = Assert.Single(open, x => x.MetricKey == EventAlertTypes.CupoMetricKey(a.CreditLineId));
        Assert.Equal($"cupo:{a.CreditLineId}", alertA.MetricKey);
        Assert.Equal(a.EntityId, alertA.EntityId);
        Assert.Equal(raiseId, alertA.AnalysisId);          // COALESCE(LastRaiseAnalysisId, OriginAnalysisId)
        Assert.Equal("High", alertA.Severity);
        Assert.Equal("Operaciones vencidas ocupando cupo", alertA.Title);
        Assert.Equal(2m, alertA.MetricValue);
        Assert.Equal(0m, alertA.ThresholdValue);
        Assert.False(alertA.IsRead);
        Assert.Equal(Now, alertA.CreatedAt);
        Assert.Equal(
            "2 operaciones vencidas siguen ocupando ARS 1.500.000,00 de un cupo de ARS 5.000.000,00. " +
            "La más antigua venció el 03/09/2026. Cerralas para liberar el cupo.",
            alertA.Description);

        var alertB = Assert.Single(open, x => x.MetricKey == EventAlertTypes.CupoMetricKey(b.CreditLineId));
        Assert.Equal(originId, alertB.AnalysisId);
        Assert.Equal(1m, alertB.MetricValue);
        Assert.Equal(
            "1 operación vencida sigue ocupando USD 300,00 de un cupo de USD 800,00. " +
            "Venció el 03/09/2026. Cerrala para liberar el cupo.",
            alertB.Description);
    }

    [Fact]
    public async Task Expired_SecondReconcile_DoesNotDuplicate()
    {
        var cupo = Cupo();
        await SeedAsync(cupo, Operacion(cupo, 100m, validUntil: Past));

        await ReconcileAsync();
        var first = Assert.Single(await AlertsAsync());

        await ReconcileAsync(Now.AddMinutes(1));

        var after = Assert.Single(await AlertsAsync());
        Assert.Equal(first.AlertId, after.AlertId);
        Assert.False(after.IsResolved);
    }

    [Fact]
    public async Task Expired_ANewExpiredOperation_UpdatesTheOpenAlertInPlace()
    {
        var cupo = Cupo();
        var later = Operacion(cupo, 50m, validUntil: Now.Date.AddDays(1));
        await SeedAsync(cupo, Operacion(cupo, 100m, validUntil: Past), later);

        await ReconcileAsync();
        var first = Assert.Single(await OfTypeAsync(EventAlertTypes.CupoOperationsExpired));
        Assert.Equal(1m, first.MetricValue);

        // Two days on, the second operation has expired too.
        await ReconcileAsync(Now.AddDays(2));

        var after = Assert.Single(await OfTypeAsync(EventAlertTypes.CupoOperationsExpired));
        Assert.Equal(first.AlertId, after.AlertId);
        Assert.Equal(2m, after.MetricValue);
        Assert.StartsWith("2 operaciones vencidas siguen ocupando ARS 150,00", after.Description);
    }

    [Fact]
    public async Task Expired_CancellingTheOperations_AutoResolves()
    {
        var cupo = Cupo();
        var op   = Operacion(cupo, 100m, validUntil: Past);
        await SeedAsync(cupo, op);
        await ReconcileAsync();

        await SetOperationStatusAsync(op.OperationId, CreditLineRules.Cancelada);
        var later = Now.AddHours(1);
        await ReconcileAsync(later);

        var alert = Assert.Single(await AlertsAsync());
        Assert.True(alert.IsResolved);
        Assert.Equal(later, alert.ResolvedAt);
        Assert.Null(alert.ResolvedBy); // system auto-resolution
    }

    [Fact]
    public async Task Expired_AnAprobadaOperationPastValidUntil_NeverCounts()
    {
        // EffectiveStatus: an approval nobody confirmed cannot expire into 'Vencida'.
        var cupo = Cupo();
        await SeedAsync(cupo, Operacion(cupo, 100m, status: CreditLineRules.Aprobada, validUntil: Past));

        await ReconcileAsync();

        Assert.Empty(await OpenAsync(EventAlertTypes.CupoOperationsExpired));
    }

    [Fact]
    public async Task Expired_ValidUntilToday_IsNotYetVencida_ButIsExpiring()
    {
        var cupo = Cupo();
        await SeedAsync(cupo, Operacion(cupo, 100m, validUntil: Now.Date));

        await ReconcileAsync();

        Assert.Empty(await OfTypeAsync(EventAlertTypes.CupoOperationsExpired));
        Assert.Single(await OpenAsync(EventAlertTypes.CupoOperationsExpiring));
    }

    [Fact]
    public async Task SuspendedCupo_IsIgnored_AndItsOpenAlertsAreResolved()
    {
        var suspended = Cupo(status: "Suspended");
        var active    = Cupo();
        await SeedAsync(
            suspended, Operacion(suspended, 1_050m, validUntil: Past),
            active, Operacion(active, 1_050m, validUntil: Past));

        await ReconcileAsync();

        var all = await AlertsAsync();
        Assert.Equal(2, all.Count); // expired + exceeded, both for the Active cupo only
        Assert.All(all, a => Assert.Equal(EventAlertTypes.CupoMetricKey(active.CreditLineId), a.MetricKey));

        await SetCupoStatusAsync(active.CreditLineId, "Suspended");
        await ReconcileAsync(Now.AddMinutes(5));

        var after = await AlertsAsync();
        Assert.Equal(2, after.Count);
        Assert.All(after, a =>
        {
            Assert.True(a.IsResolved);
            Assert.Null(a.ResolvedBy);
        });
    }

    [Fact]
    public async Task SupersededCupo_ItsOpenAlertsAreResolved()
    {
        var cupo = Cupo(status: "Superseded");
        var orphan = OpenCupoAlert(EventAlertTypes.CupoUtilizationHigh, cupo.CreditLineId, Now.AddDays(-1));
        await SeedAsync(cupo, orphan);

        await ReconcileAsync();

        var alert = Assert.Single(await AlertsAsync());
        Assert.True(alert.IsResolved);
    }

    // ── CUPO_UTILIZATION_HIGH (exceeded only) ───────────────────────────────────

    [Fact]
    public async Task Utilization_99Percent_RaisesNothing()
    {
        var cupo = Cupo(amount: 1_000m);
        await SeedAsync(cupo, Operacion(cupo, 990m));

        await ReconcileAsync();

        Assert.Empty(await AlertsAsync());
    }

    [Fact]
    public async Task Utilization_Exactly100_DisponibleZero_RaisesNothing()
    {
        // The normal state of a cupo approved for one operation of its full amount.
        var cupo = Cupo(amount: 1_000m);
        await SeedAsync(cupo, Operacion(cupo, 600m), Operacion(cupo, 400m, status: CreditLineRules.Aprobada));

        await ReconcileAsync();

        Assert.Empty(await AlertsAsync());
    }

    [Fact]
    public async Task Utilization_JustOver100_IsHighCupoExcedido()
    {
        var cupo = Cupo(amount: 10_000m);
        await SeedAsync(cupo, Operacion(cupo, 10_001m), Operacion(cupo, 500m, status: CreditLineRules.Cancelada));

        await ReconcileAsync();

        var alert = Assert.Single(await OpenAsync(EventAlertTypes.CupoUtilizationHigh));
        Assert.Equal("High", alert.Severity);
        Assert.Equal("Cupo excedido", alert.Title);
        Assert.Equal(100.01m, alert.MetricValue);
        Assert.Equal(100m, alert.ThresholdValue);
        Assert.Equal(cupo.EntityId, alert.EntityId);
        Assert.Null(alert.AnalysisId); // neither LastRaiseAnalysisId nor OriginAnalysisId on this cupo
        Assert.Equal(EventAlertTypes.CupoMetricKey(cupo.CreditLineId), alert.MetricKey);
        Assert.Equal(
            "Utilizado: ARS 10.001,00 de un cupo de ARS 10.000,00. Disponible: ARS -1,00. " +
            "Revisá las operaciones: cerrá las que ya terminaron o ampliá el cupo.",
            alert.Description);
    }

    [Fact]
    public async Task Utilization_Over100_CountsAprobadaButNotCancelada()
    {
        // The create-operation guard refuses this; concurrent creates or legacy data can still get here.
        var cupo = Cupo(amount: 1_000m);
        await SeedAsync(cupo, Operacion(cupo, 700m), Operacion(cupo, 500m, status: CreditLineRules.Aprobada),
            Operacion(cupo, 900m, status: CreditLineRules.Cancelada));

        await ReconcileAsync();

        var alert = Assert.Single(await AlertsAsync());
        Assert.Equal(120m, alert.MetricValue);
        Assert.Equal(
            "Utilizado: ARS 1.200,00 de un cupo de ARS 1.000,00. Disponible: ARS -200,00. " +
            "Revisá las operaciones: cerrá las que ya terminaron o ampliá el cupo.",
            alert.Description);
    }

    [Fact]
    public async Task Utilization_FarOverTheCupo_ClampsMetricValueToTheColumnRange()
    {
        var cupo = Cupo(amount: 1m);
        await SeedAsync(cupo, Operacion(cupo, 50_000m));

        await ReconcileAsync();

        var alert = Assert.Single(await AlertsAsync());
        Assert.Equal(999.9999m, alert.MetricValue);
        Assert.Contains("Disponible: ARS -49.999,00.", alert.Description);
    }

    [Theory]
    [InlineData("Medium", 95, 950)]   // the old 90–100% tier
    [InlineData("High", 100, 1_000)]  // the old "exactly 100%" High
    public async Task Utilization_AnOpenAlertFromTheOldRule_AutoResolves(string severity, int metric, int utilizado)
    {
        var cupo     = Cupo(amount: 1_000m);
        var existing = OpenCupoAlert(EventAlertTypes.CupoUtilizationHigh, cupo.CreditLineId, Now.AddDays(-1),
            severity: severity, metricValue: metric);
        await SeedAsync(cupo, Operacion(cupo, utilizado), existing);

        await ReconcileAsync();

        var alert = Assert.Single(await AlertsAsync());
        Assert.Equal(existing.AlertId, alert.AlertId);
        Assert.True(alert.IsResolved);
        Assert.Null(alert.ResolvedBy);
    }

    [Fact]
    public async Task Utilization_ChangeWithoutEscalation_KeepsItRead()
    {
        var cupo = Cupo(amount: 1_000m);
        await SeedAsync(cupo, Operacion(cupo, 1_100m));
        await ReconcileAsync();
        var first = Assert.Single(await AlertsAsync());
        await MarkReadAsync(first.AlertId);

        await SeedAsync(Operacion(cupo, 30m));
        await ReconcileAsync(Now.AddMinutes(1));

        var after = Assert.Single(await AlertsAsync());
        Assert.Equal(first.AlertId, after.AlertId);
        Assert.Equal(113m, after.MetricValue);
        Assert.True(after.IsRead);
    }

    [Fact]
    public async Task Utilization_BackToExactly100_AutoResolves()
    {
        var cupo  = Cupo(amount: 1_000m);
        var extra = Operacion(cupo, 100m);
        await SeedAsync(cupo, extra, Operacion(cupo, 1_000m));
        await ReconcileAsync();

        await SetOperationStatusAsync(extra.OperationId, CreditLineRules.Cancelada);
        await ReconcileAsync(Now.AddMinutes(1));

        var alert = Assert.Single(await AlertsAsync());
        Assert.True(alert.IsResolved);
        Assert.Null(alert.ResolvedBy);
    }

    [Fact]
    public async Task Utilization_AmountZero_IsSkipped_ButExpiredStillEvaluated()
    {
        var cupo = Cupo(amount: 0m);
        await SeedAsync(cupo, Operacion(cupo, 100m, validUntil: Past));

        await ReconcileAsync();

        var alert = Assert.Single(await AlertsAsync());
        Assert.Equal(EventAlertTypes.CupoOperationsExpired, alert.AlertType);
    }

    [Fact]
    public async Task Utilization_AmountZero_LeavesAnExistingOpenUtilizationAlertAlone()
    {
        var cupo = Cupo(amount: 0m);
        var existing = OpenCupoAlert(EventAlertTypes.CupoUtilizationHigh, cupo.CreditLineId, Now.AddDays(-1));
        await SeedAsync(cupo, existing);

        await ReconcileAsync();

        Assert.False(Assert.Single(await AlertsAsync()).IsResolved);
    }

    // ── Suppression ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Suppression_UserResolvedAndUnchanged_IsNotRecreated()
    {
        var cupo = Cupo(amount: 1_000m);
        await SeedAsync(cupo, Operacion(cupo, 1_050m), Operacion(cupo, 10m, validUntil: Past));
        await ReconcileAsync();
        foreach (var alert in await AlertsAsync())
            await ResolveByUserAsync(alert.AlertId, Now.AddMinutes(1));

        await ReconcileAsync(Now.AddDays(1));

        var all = await AlertsAsync();
        Assert.Equal(2, all.Count);
        Assert.All(all, a => Assert.True(a.IsResolved));
    }

    [Fact]
    public async Task Suppression_UtilizationWorsened_IsRecreated()
    {
        var cupo = Cupo(amount: 1_000m);
        await SeedAsync(cupo, Operacion(cupo, 1_050m));
        await ReconcileAsync();
        var dismissed = Assert.Single(await AlertsAsync());
        await ResolveByUserAsync(dismissed.AlertId, Now.AddMinutes(1));

        await SeedAsync(Operacion(cupo, 20m));
        await ReconcileAsync(Now.AddMinutes(2));

        var open = Assert.Single(await OpenAsync(EventAlertTypes.CupoUtilizationHigh));
        Assert.NotEqual(dismissed.AlertId, open.AlertId);
        Assert.Equal(107m, open.MetricValue);
        Assert.False(open.IsRead);
    }

    [Fact]
    public async Task Suppression_UtilizationImproved_StaysDismissed()
    {
        var cupo = Cupo(amount: 1_000m);
        var op   = Operacion(cupo, 50m);
        await SeedAsync(cupo, Operacion(cupo, 1_100m), op);
        await ReconcileAsync();
        await ResolveByUserAsync(Assert.Single(await AlertsAsync()).AlertId, Now.AddMinutes(1));

        await SetOperationStatusAsync(op.OperationId, CreditLineRules.Cancelada); // 115% → 110%
        await ReconcileAsync(Now.AddMinutes(2));

        Assert.Empty(await OpenAsync(EventAlertTypes.CupoUtilizationHigh));
    }

    [Fact]
    public async Task Suppression_ExpiredCountUp_IsRecreated()
    {
        var cupo = Cupo();
        await SeedAsync(cupo, Operacion(cupo, 10m, validUntil: Past));
        await ReconcileAsync();
        await ResolveByUserAsync(Assert.Single(await AlertsAsync()).AlertId, Now.AddMinutes(1));

        await SeedAsync(Operacion(cupo, 10m, validUntil: Past.AddDays(1)));
        await ReconcileAsync(Now.AddMinutes(2));

        var open = Assert.Single(await OpenAsync(EventAlertTypes.CupoOperationsExpired));
        Assert.Equal(2m, open.MetricValue);
    }

    [Fact]
    public async Task Suppression_ExpiredSameCountButANewlyExpiredOperation_IsRecreated()
    {
        // Dismissed at "2 vencidas"; then one of them is cancelled and a different one expires. The
        // count is back to 2, but the analyst has never been told about the new one.
        var cupo     = Cupo();
        var old1     = Operacion(cupo, 10m, validUntil: Past);
        var upcoming = Operacion(cupo, 10m, validUntil: Now.Date.AddDays(3));
        await SeedAsync(cupo, old1, Operacion(cupo, 10m, validUntil: Past), upcoming);
        await ReconcileAsync();
        await ResolveByUserAsync(Assert.Single(await OfTypeAsync(EventAlertTypes.CupoOperationsExpired)).AlertId, Now.AddMinutes(1));

        await SetOperationStatusAsync(old1.OperationId, CreditLineRules.Cancelada);
        await ReconcileAsync(Now.AddDays(5));

        var open = Assert.Single(await OpenAsync(EventAlertTypes.CupoOperationsExpired));
        Assert.Equal(2m, open.MetricValue);
    }

    [Fact]
    public async Task Suppression_ASystemResolvedAlert_NeverSuppressesItsReturn()
    {
        var cupo = Cupo();
        var op   = Operacion(cupo, 10m, validUntil: Past);
        await SeedAsync(cupo, op);
        await ReconcileAsync();
        await SetCupoStatusAsync(cupo.CreditLineId, "Suspended");
        await ReconcileAsync(Now.AddMinutes(1)); // auto-resolved, ResolvedBy null

        await SetCupoStatusAsync(cupo.CreditLineId, "Active");
        await ReconcileAsync(Now.AddMinutes(2));

        Assert.Single(await OpenAsync(EventAlertTypes.CupoOperationsExpired));
        Assert.Equal(2, (await AlertsAsync()).Count);
    }

    // ── Isolation and self-healing ──────────────────────────────────────────────

    [Fact]
    public async Task TenantIsolation_AnotherTenantsCuposNeverProduceOrResolveAlertsHere()
    {
        var mine   = Cupo(amount: 1_000m);
        var theirs = Cupo(amount: 1_000m, tenantId: Other);
        var theirOpen = OpenCupoAlert(EventAlertTypes.CupoOperationsExpired, theirs.CreditLineId, Now.AddDays(-1), tenantId: Other);
        await SeedAsync(
            mine,
            // Another tenant's operation pointing at this tenant's cupo must not eat into its disponible.
            Operacion(mine, 1_050m, tenantId: Other),
            theirs, Operacion(theirs, 1_050m, validUntil: Past),
            theirOpen);

        await ReconcileAsync();

        Assert.Empty(await AlertsAsync(Tenant));
        var stillTheirs = Assert.Single(await AlertsAsync(Other));
        Assert.Equal(theirOpen.AlertId, stillTheirs.AlertId);
        Assert.False(stillTheirs.IsResolved);
    }

    [Fact]
    public async Task DuplicateOpenAlerts_KeepTheNewest_AndResolveTheRest()
    {
        var cupo  = Cupo();
        var older = OpenCupoAlert(EventAlertTypes.CupoOperationsExpired, cupo.CreditLineId, Now.AddDays(-2));
        var newer = OpenCupoAlert(EventAlertTypes.CupoOperationsExpired, cupo.CreditLineId, Now.AddDays(-1));
        await SeedAsync(cupo, Operacion(cupo, 10m, validUntil: Past), older, newer);

        await ReconcileAsync();

        var all = await AlertsAsync();
        Assert.Equal(2, all.Count);
        Assert.True(all.Single(a => a.AlertId == older.AlertId).IsResolved);
        var kept = all.Single(a => a.AlertId == newer.AlertId);
        Assert.False(kept.IsResolved);
        Assert.Equal("Operaciones vencidas ocupando cupo", kept.Title); // and brought up to date
    }

    [Fact]
    public async Task NonCupoAlerts_AreNeverTouched()
    {
        var unrelated = new Alert
        {
            AlertId = Guid.NewGuid(), TenantId = Tenant, EntityId = Guid.NewGuid(), AlertType = "HIGH_LEVERAGE",
            Severity = "High", Title = "t", Description = "d", MetricKey = string.Empty, MetricValue = 0m,
            ThresholdValue = 0m, CreatedAt = Now.AddDays(-1)
        };
        await SeedAsync(unrelated);

        await ReconcileAsync();

        Assert.False(Assert.Single(await AlertsAsync()).IsResolved);
    }

    // ── Fault isolation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task AFailingRead_IsSwallowed()
    {
        var ctx = NewContext();
        var sut = NewService(ctx);
        await ctx.DisposeAsync(); // every query now throws ObjectDisposedException

        var ex = await Record.ExceptionAsync(() => sut.ReconcileAsync(Tenant, Now, CancellationToken.None));

        Assert.Null(ex);
    }

    [Fact]
    public async Task ACancelledRequest_Propagates()
    {
        await using var ctx = NewContext();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NewService(ctx).ReconcileAsync(Tenant, Now, cts.Token));
    }
}
