// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-25

using System.Reflection;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Clases.Time;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// The billing close-out of the pass: every OPEN plan alert (<see cref="BillingAlertService.AllTypes"/>)
/// created before the current billing period started is auto-resolved — a new period is a new quota — and
/// nothing else is. The period is <see cref="ArgentineTime.CurrentPeriod"/>'s, the one the usage snapshot
/// those alerts were emitted from uses, so its boundary is 00:00 ART on the 1st, not 00:00 UTC.
/// </summary>
public sealed partial class CupoAlertServiceTests
{
    // Now is 2026-10-25T15:00Z; the October period starts at 00:00 ART on the 1st.
    private static readonly DateTime OctoberStartUtc = new(2026, 10, 1, 3, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime InAugust        = new(2026, 8, 31, 18, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime InOctober       = new(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

    private static Alert BillingAlert(string alertType, DateTime createdAt, string tenantId = Tenant) => new()
    {
        AlertId        = Guid.NewGuid(),
        TenantId       = tenantId,
        EntityId       = null,
        AnalysisId     = null,
        AlertType      = alertType,
        Severity       = alertType.EndsWith("_100", StringComparison.Ordinal) ? "High" : "Medium",
        Title          = "Consumo del plan al 80%",
        Description    = "Usaste el 92% de los análisis de tu plan (50 análisis) este mes.",
        MetricKey      = alertType.Contains("CHAT", StringComparison.Ordinal)
            ? BillingAlertService.ChatTokensMetricKey
            : BillingAlertService.AnalysesMetricKey,
        MetricValue    = 92m,
        ThresholdValue = 80m,
        CreatedAt      = createdAt,
    };

    private async Task<Alert> AlertByIdAsync(Guid alertId, string tenantId = Tenant)
        => Assert.Single(await AlertsAsync(tenantId), a => a.AlertId == alertId);

    [Fact]
    public async Task Billing_PastPeriodAlerts_OfEveryType_AreAutoResolved()
    {
        var past = BillingAlertService.AllTypes.Select(t => BillingAlert(t, InAugust)).ToList();
        await SeedAsync([.. past]);

        await ReconcileAsync();

        Assert.Equal(4, past.Count);
        foreach (var seeded in past)
        {
            var alert = await AlertByIdAsync(seeded.AlertId);
            Assert.True(alert.IsResolved, alert.AlertType);
            Assert.Equal(Now, alert.ResolvedAt);
            Assert.Null(alert.ResolvedBy); // system auto-resolution
        }
    }

    [Fact]
    public async Task Billing_CurrentPeriodAlerts_StayOpen()
    {
        var current = BillingAlertService.AllTypes.Select(t => BillingAlert(t, InOctober)).ToList();
        var past    = BillingAlert(BillingAlertService.AnalysisQuota80Type, InAugust);
        await SeedAsync([.. current, past]);

        await ReconcileAsync();

        foreach (var seeded in current)
        {
            var alert = await AlertByIdAsync(seeded.AlertId);
            Assert.False(alert.IsResolved, alert.AlertType);
            Assert.Null(alert.ResolvedAt);
        }
        Assert.True((await AlertByIdAsync(past.AlertId)).IsResolved);
    }

    /// <summary>
    /// The period is half-open: an alert at its first instant belongs to it; one a tick earlier, or one at
    /// 01:00Z on 1 October (still 30 September in Argentina), belongs to September.
    /// </summary>
    [Fact]
    public async Task Billing_Boundary_IsTheFirstInstantOfTheArgentinePeriod()
    {
        Assert.Equal(OctoberStartUtc, ArgentineTime.CurrentPeriod(Now).StartUtc); // 00:00 ART, not 00:00 UTC

        var atStart    = BillingAlert(BillingAlertService.AnalysisQuota80Type, OctoberStartUtc);
        var tickBefore = BillingAlert(BillingAlertService.AnalysisQuota100Type, OctoberStartUtc.AddTicks(-1));
        var utcOctoberArgSeptember = BillingAlert(
            BillingAlertService.ChatQuota80Type, new DateTime(2026, 10, 1, 1, 0, 0, DateTimeKind.Utc));
        await SeedAsync(atStart, tickBefore, utcOctoberArgSeptember);

        await ReconcileAsync();

        Assert.False((await AlertByIdAsync(atStart.AlertId)).IsResolved);
        Assert.True((await AlertByIdAsync(tickBefore.AlertId)).IsResolved);
        Assert.True((await AlertByIdAsync(utcOctoberArgSeptember.AlertId)).IsResolved);
    }

    [Fact]
    public async Task Billing_OtherTenants_AreUntouched()
    {
        var mine   = BillingAlert(BillingAlertService.AnalysisQuota80Type, InAugust);
        var theirs = BillingAlert(BillingAlertService.AnalysisQuota80Type, InAugust, tenantId: Other);
        await SeedAsync(mine, theirs);

        await ReconcileAsync();

        Assert.True((await AlertByIdAsync(mine.AlertId)).IsResolved);
        var other = await AlertByIdAsync(theirs.AlertId, Other);
        Assert.False(other.IsResolved);
        Assert.Null(other.ResolvedAt);
    }

    [Fact]
    public async Task Billing_SecondPass_IsIdempotent()
    {
        var past    = BillingAlert(BillingAlertService.ChatQuota100Type, InAugust);
        var current = BillingAlert(BillingAlertService.ChatQuota80Type, InOctober);
        await SeedAsync(past, current);

        await ReconcileAsync();
        await ReconcileAsync(Now.AddMinutes(1));

        var all = await AlertsAsync();
        Assert.Equal(2, all.Count);

        var closed = Assert.Single(all, a => a.AlertId == past.AlertId);
        Assert.True(closed.IsResolved);
        Assert.Equal(Now, closed.ResolvedAt); // the first pass's instant, not overwritten by the second

        Assert.False(Assert.Single(all, a => a.AlertId == current.AlertId).IsResolved);
    }

    /// <summary>
    /// Only OPEN billing alerts are closed: a user's resolution keeps its author and instant, and an
    /// analysis alert from a past month is not this rule's to touch.
    /// </summary>
    [Fact]
    public async Task Billing_LeavesResolvedAndNonBillingAlertsAlone()
    {
        var dismissedAt = new DateTime(2026, 9, 2, 10, 0, 0, DateTimeKind.Utc);
        var userId      = Guid.NewGuid();
        var dismissed   = BillingAlert(BillingAlertService.AnalysisQuota100Type, InAugust);
        dismissed.IsResolved = true;
        dismissed.ResolvedAt = dismissedAt;
        dismissed.ResolvedBy = userId;

        var finding = BillingAlert("HIGH_LEVERAGE", InAugust);
        finding.EntityId = Guid.NewGuid();

        await SeedAsync(dismissed, finding);

        await ReconcileAsync();

        var stillDismissed = await AlertByIdAsync(dismissed.AlertId);
        Assert.Equal(dismissedAt, stillDismissed.ResolvedAt);
        Assert.Equal(userId, stillDismissed.ResolvedBy);

        Assert.False((await AlertByIdAsync(finding.AlertId)).IsResolved);
    }

    /// <summary>
    /// FAULT ISOLATION. A close-out that fails costs nothing but itself: the event alerts are still
    /// reconciled, the pass still returns its facts, and the billing alert simply stays open until the next one.
    /// </summary>
    [Fact]
    public async Task Billing_CloseOutFailure_StillReconcilesTheEventAlerts()
    {
        var cupo = Cupo();
        var past = BillingAlert(BillingAlertService.AnalysisQuota80Type, InAugust);
        await SeedAsync(cupo, Operacion(cupo, 100m, validUntil: Past), past);

        await using var ctx = NewContext();
        var sut = new CupoAlertService(
            new CreditLineRepository(ctx, NullLogger<CreditLineRepository>.Instance),
            new DashboardRepository(ctx),
            CloseOutFailingAlertRepository.Wrap(new AlertRepository(ctx, NullLogger<AlertRepository>.Instance)),
            NullLogger<CupoAlertService>.Instance);

        var facts = await sut.ReconcileAsync(Tenant, Now, CancellationToken.None);

        Assert.NotNull(facts);
        var expired = Assert.Single(await OpenAsync(EventAlertTypes.CupoOperationsExpired));
        Assert.Contains(expired.AlertId, facts!);
        Assert.False((await AlertByIdAsync(past.AlertId)).IsResolved);
    }

    /// <summary>
    /// An <see cref="IAlertRepository"/> that forwards every member to a real one except the close-out read,
    /// which fails. A <see cref="DispatchProxy"/> (in the BCL, not a mocking library), like
    /// <see cref="ParkedCreditLineRepository"/>.
    /// </summary>
    public class CloseOutFailingAlertRepository : DispatchProxy
    {
        private IAlertRepository? _inner;

        public static IAlertRepository Wrap(IAlertRepository inner)
        {
            var proxy = Create<IAlertRepository, CloseOutFailingAlertRepository>();
            ((CloseOutFailingAlertRepository)(object)proxy)._inner = inner;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name == nameof(IAlertRepository.GetOpenByTypesCreatedBeforeForUpdateAsync)
                ? Task.FromException<IReadOnlyList<Alert>>(new InvalidOperationException("close-out read failed"))
                : targetMethod!.Invoke(_inner, args);
    }
}
