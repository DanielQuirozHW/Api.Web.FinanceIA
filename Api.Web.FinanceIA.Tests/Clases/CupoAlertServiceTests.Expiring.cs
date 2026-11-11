// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-25

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// CUPO_OPERATIONS_EXPIRING: 'Vigente' operations of an Active cupo inside the dashboard's Days0To30 band,
/// counted in Argentine calendar days from the Argentine date of the pass (Now is 12:00 in Buenos Aires on
/// 25/10/2026, so day 30 is 24/11/2026).
/// </summary>
public sealed partial class CupoAlertServiceTests
{
    private async Task SetOperationAsync(Guid operationId, Action<CreditLineOperation> change)
    {
        await using var ctx = NewContext();
        var op = await ctx.CreditLineOperations.SingleAsync(o => o.OperationId == operationId);
        change(op);
        ctx.ChangeTracker.DetectChanges();
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task Expiring_CountsVigenteOperationsInTheFirstBand_WithItsIdentityAndCopy()
    {
        var originId = Guid.NewGuid();
        var cupo     = Cupo(amount: 5_000m, originAnalysisId: originId);
        await SeedAsync(
            cupo,
            Operacion(cupo, 100m, validUntil: Now.Date.AddDays(10)),
            Operacion(cupo, 200m, validUntil: Now.Date.AddDays(30)),                                  // the band's last day
            Operacion(cupo, 400m, validUntil: Now.Date.AddDays(31)),                                  // next band
            Operacion(cupo, 800m, status: CreditLineRules.Aprobada, validUntil: Now.Date.AddDays(5)), // not in force
            Operacion(cupo, 900m, status: CreditLineRules.Cancelada, validUntil: Now.Date.AddDays(5)),
            Operacion(cupo, 50m));                                                                    // no ValidUntil

        await ReconcileAsync();

        var alert = Assert.Single(await OpenAsync(EventAlertTypes.CupoOperationsExpiring));
        Assert.Equal(EventAlertTypes.CupoMetricKey(cupo.CreditLineId), alert.MetricKey);
        Assert.Equal(cupo.EntityId, alert.EntityId);
        Assert.Equal(originId, alert.AnalysisId);
        Assert.Equal("Medium", alert.Severity);
        Assert.Equal("Operaciones por vencer", alert.Title);
        Assert.Equal(2m, alert.MetricValue);
        Assert.Equal(30m, alert.ThresholdValue);
        Assert.Equal(
            "2 operaciones vencen en los próximos 30 días por ARS 300,00. La más próxima vence el 04/11/2026. " +
            "Renová la operación o cerrala cuando termine.",
            alert.Description);
    }

    [Fact]
    public async Task Expiring_OneOperation_UsesTheSingularCopy()
    {
        var cupo = Cupo(currency: "USD");
        await SeedAsync(cupo, Operacion(cupo, 75m, validUntil: Now.Date));

        await ReconcileAsync();

        var alert = Assert.Single(await OpenAsync(EventAlertTypes.CupoOperationsExpiring));
        Assert.Equal(
            "1 operación vence en los próximos 30 días por USD 75,00. Vence el 25/10/2026. " +
            "Renová la operación o cerrala cuando termine.",
            alert.Description);
    }

    [Fact]
    public async Task Expiring_TheBandIsCountedInArgentineDays()
    {
        // 01:00 UTC on the 26th is still 22:00 on the 25th in Buenos Aires: day 30 is the 24/11, not the 25/11.
        var lateEvening = new DateTime(2026, 10, 26, 1, 0, 0, DateTimeKind.Utc);
        var cupo  = Cupo();
        var in30  = Operacion(cupo, 10m, validUntil: new DateTime(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc));
        var out31 = Operacion(cupo, 20m, validUntil: new DateTime(2026, 11, 25, 0, 0, 0, DateTimeKind.Utc));
        await SeedAsync(cupo, in30, out31);

        await ReconcileAsync(lateEvening);

        var alert = Assert.Single(await OpenAsync(EventAlertTypes.CupoOperationsExpiring));
        Assert.Equal(1m, alert.MetricValue);
        Assert.Contains("ARS 10,00", alert.Description);
        Assert.Equal(
            MaturityBands.Days0To30,
            MaturityBands.BucketOf(in30.ValidUntil!.Value, MaturityBands.AsOf(lateEvening), lateEvening));
        Assert.Equal(
            MaturityBands.Days31To60,
            MaturityBands.BucketOf(out31.ValidUntil!.Value, MaturityBands.AsOf(lateEvening), lateEvening));
    }

    [Fact]
    public async Task Expiring_VencidaOnTheUtcDate_IsExpiredNotExpiring()
    {
        // At 01:00 UTC on the 26th an operation due the 25th is 'Vencida' (EffectiveStatus decides on the UTC
        // date), even though it is still the 25th in Buenos Aires.
        var lateEvening = new DateTime(2026, 10, 26, 1, 0, 0, DateTimeKind.Utc);
        var cupo = Cupo();
        await SeedAsync(cupo, Operacion(cupo, 10m, validUntil: new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc)));

        await ReconcileAsync(lateEvening);

        Assert.Empty(await OfTypeAsync(EventAlertTypes.CupoOperationsExpiring));
        Assert.Single(await OpenAsync(EventAlertTypes.CupoOperationsExpired));
    }

    [Fact]
    public async Task Expiring_SecondPass_DoesNotDuplicate_AndANewOperationUpdatesInPlace()
    {
        var cupo = Cupo();
        await SeedAsync(cupo, Operacion(cupo, 10m, validUntil: Now.Date.AddDays(3)));
        await ReconcileAsync();
        var first = Assert.Single(await OfTypeAsync(EventAlertTypes.CupoOperationsExpiring));

        await ReconcileAsync(Now.AddMinutes(1));
        Assert.Equal(first.AlertId, Assert.Single(await OfTypeAsync(EventAlertTypes.CupoOperationsExpiring)).AlertId);

        await SeedAsync(Operacion(cupo, 15m, validUntil: Now.Date.AddDays(1)));
        await ReconcileAsync(Now.AddMinutes(2));

        var after = Assert.Single(await OfTypeAsync(EventAlertTypes.CupoOperationsExpiring));
        Assert.Equal(first.AlertId, after.AlertId);
        Assert.False(after.IsResolved);
        Assert.Equal(2m, after.MetricValue);
        Assert.Contains("por ARS 25,00. La más próxima vence el 26/10/2026.", after.Description);
    }

    [Fact]
    public async Task Expiring_OnceTheOperationExpires_ExpiredTakesOver()
    {
        var cupo = Cupo();
        await SeedAsync(cupo, Operacion(cupo, 10m, validUntil: Now.Date.AddDays(1)));
        await ReconcileAsync();
        Assert.Single(await OpenAsync(EventAlertTypes.CupoOperationsExpiring));

        await ReconcileAsync(Now.AddDays(2));

        var expiring = Assert.Single(await OfTypeAsync(EventAlertTypes.CupoOperationsExpiring));
        Assert.True(expiring.IsResolved);
        Assert.Null(expiring.ResolvedBy);
        Assert.Single(await OpenAsync(EventAlertTypes.CupoOperationsExpired));
    }

    [Fact]
    public async Task Expiring_ClosingOrRenewingTheOperation_AutoResolves()
    {
        var cupo    = Cupo();
        var closed  = Operacion(cupo, 10m, validUntil: Now.Date.AddDays(5));
        var renewed = Operacion(cupo, 10m, validUntil: Now.Date.AddDays(6));
        await SeedAsync(cupo, closed, renewed);
        await ReconcileAsync();

        await SetOperationStatusAsync(closed.OperationId, CreditLineRules.Cancelada);
        await SetOperationAsync(renewed.OperationId, op => op.ValidUntil = Now.Date.AddDays(200));
        await ReconcileAsync(Now.AddMinutes(1));

        var alert = Assert.Single(await AlertsAsync());
        Assert.True(alert.IsResolved);
        Assert.Null(alert.ResolvedBy);
    }

    [Fact]
    public async Task Expiring_SuspendedCupo_IsIgnored_AndItsOpenAlertResolves()
    {
        var cupo      = Cupo();
        var suspended = Cupo(status: "Suspended");
        await SeedAsync(
            cupo, Operacion(cupo, 10m, validUntil: Now.Date.AddDays(5)),
            suspended, Operacion(suspended, 10m, validUntil: Now.Date.AddDays(5)));
        await ReconcileAsync();
        Assert.Equal(EventAlertTypes.CupoMetricKey(cupo.CreditLineId), Assert.Single(await AlertsAsync()).MetricKey);

        await SetCupoStatusAsync(cupo.CreditLineId, "Suspended");
        await ReconcileAsync(Now.AddMinutes(1));

        Assert.True(Assert.Single(await AlertsAsync()).IsResolved);
    }

    [Fact]
    public async Task Expiring_Suppression_TheSameOperationsAFewDaysCloser_StayDismissed()
    {
        var cupo = Cupo();
        await SeedAsync(cupo, Operacion(cupo, 10m, validUntil: Now.Date.AddDays(20)), Operacion(cupo, 10m, validUntil: Now.Date.AddDays(25)));
        await ReconcileAsync();
        await ResolveByUserAsync(Assert.Single(await AlertsAsync()).AlertId, Now.AddMinutes(1));

        await ReconcileAsync(Now.AddDays(4));

        Assert.True(Assert.Single(await AlertsAsync()).IsResolved);
    }

    [Fact]
    public async Task Expiring_Suppression_AnOperationThatEnteredTheWindowSinceTheDismissal_IsRecreated()
    {
        var cupo = Cupo();
        await SeedAsync(cupo, Operacion(cupo, 10m, validUntil: Now.Date.AddDays(20)), Operacion(cupo, 10m, validUntil: Now.Date.AddDays(35)));
        await ReconcileAsync();
        var dismissed = Assert.Single(await AlertsAsync());
        await ResolveByUserAsync(dismissed.AlertId, Now.AddMinutes(1));

        // A day later the second one is still at 34 days: nothing new.
        await ReconcileAsync(Now.AddDays(1));
        Assert.Empty(await OpenAsync(EventAlertTypes.CupoOperationsExpiring));

        // Six days later it is at 29 days — in the window, and it was not when the user dismissed it.
        await ReconcileAsync(Now.AddDays(6));

        var open = Assert.Single(await OpenAsync(EventAlertTypes.CupoOperationsExpiring));
        Assert.NotEqual(dismissed.AlertId, open.AlertId);
        Assert.Equal(2m, open.MetricValue);
    }

    [Fact]
    public async Task Expiring_Suppression_ANewOperationAtTheSameCount_IsRecreated()
    {
        var cupo = Cupo();
        var old  = Operacion(cupo, 10m, validUntil: Now.Date.AddDays(20));
        await SeedAsync(cupo, old);
        await ReconcileAsync();
        await ResolveByUserAsync(Assert.Single(await AlertsAsync()).AlertId, Now.AddMinutes(1));

        // Closed, and replaced by a new short one created after the dismissal: still "1 operación".
        await SetOperationStatusAsync(old.OperationId, CreditLineRules.Cancelada);
        var fresh = Operacion(cupo, 10m, validUntil: Now.Date.AddDays(10));
        fresh.CreatedAt = Now.AddHours(2);
        await SeedAsync(fresh);
        await ReconcileAsync(Now.AddHours(3));

        var open = Assert.Single(await OpenAsync(EventAlertTypes.CupoOperationsExpiring));
        Assert.Equal(1m, open.MetricValue);
    }

    [Fact]
    public async Task Expiring_Suppression_AnOperationConfirmedVigenteSinceTheDismissal_IsRecreated()
    {
        var cupo     = Cupo();
        var approved = Operacion(cupo, 10m, status: CreditLineRules.Aprobada, validUntil: Now.Date.AddDays(10));
        await SeedAsync(cupo, Operacion(cupo, 10m, validUntil: Now.Date.AddDays(20)), approved);
        await ReconcileAsync();
        await ResolveByUserAsync(Assert.Single(await AlertsAsync()).AlertId, Now.AddMinutes(1));

        await SetOperationAsync(approved.OperationId, op =>
        {
            op.Status              = CreditLineRules.Vigente;
            op.VigenteConfirmadaEl = Now.AddHours(1);
        });
        await ReconcileAsync(Now.AddHours(2));

        Assert.Equal(2m, Assert.Single(await OpenAsync(EventAlertTypes.CupoOperationsExpiring)).MetricValue);
    }

    [Fact]
    public async Task Expiring_TenantIsolation_AnotherTenantsOperationOnThisCupoIsIgnored()
    {
        var mine = Cupo();
        await SeedAsync(mine, Operacion(mine, 10m, validUntil: Now.Date.AddDays(5), tenantId: Other));

        await ReconcileAsync();

        Assert.Empty(await AlertsAsync());
    }
}
