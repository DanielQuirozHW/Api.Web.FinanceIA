// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-25

using System.Reflection;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// What ReconcileAsync returns: the facts of every alert the pass left open, keyed by AlertId, built from
/// the same figures as the Description — and null when no pass ran (gate timeout, failure).
/// </summary>
public sealed partial class CupoAlertServiceTests
{
    private async Task<IReadOnlyDictionary<Guid, AlertFactsDto>?> ReconcileWithFactsAsync(DateTime? nowUtc = null)
    {
        await using var ctx = NewContext();
        return await NewService(ctx).ReconcileAsync(Tenant, nowUtc ?? Now, CancellationToken.None);
    }

    private async Task<AlertFactsDto> FactsOfSingleOpenAsync(string alertType, DateTime? nowUtc = null)
    {
        var facts = await ReconcileWithFactsAsync(nowUtc);
        var alert = Assert.Single(await OpenAsync(alertType));
        Assert.NotNull(facts);
        return Assert.Contains(alert.AlertId, facts!);
    }

    [Fact]
    public async Task Facts_Expired_CarryTheOccupiedAmountCountAndOldestDate()
    {
        var cupo = Cupo(amount: 5_000m, currency: "USD");
        await SeedAsync(cupo, Operacion(cupo, 300m, validUntil: Past), Operacion(cupo, 200m, validUntil: Past.AddDays(4)));

        var facts = await FactsOfSingleOpenAsync(EventAlertTypes.CupoOperationsExpired);

        Assert.Equal(new AlertFactsDto
        {
            Currency = "USD", Amount = 500m, CupoAmount = 5_000m, Count = 2, Date = new DateOnly(2026, 9, 3)
        }, facts);
    }

    [Fact]
    public async Task Facts_Expiring_CarryTheWindowSumCountAndNearestDate()
    {
        var cupo = Cupo(amount: 1_000m);
        await SeedAsync(cupo, Operacion(cupo, 10m, validUntil: Now.Date.AddDays(12)), Operacion(cupo, 15m, validUntil: Now.Date.AddDays(4)));

        var facts = await FactsOfSingleOpenAsync(EventAlertTypes.CupoOperationsExpiring);

        Assert.Equal(new AlertFactsDto
        {
            Currency = "ARS", Amount = 25m, CupoAmount = 1_000m, Count = 2, Date = new DateOnly(2026, 10, 29)
        }, facts);
    }

    [Fact]
    public async Task Facts_Exceeded_CarryUtilizadoAndTheCupo()
    {
        var cupo = Cupo(amount: 1_000m);
        await SeedAsync(cupo, Operacion(cupo, 1_200m));

        var facts = await FactsOfSingleOpenAsync(EventAlertTypes.CupoUtilizationHigh);

        Assert.Equal(new AlertFactsDto { Currency = "ARS", Amount = 1_200m, CupoAmount = 1_000m }, facts);
    }

    [Theory]
    [InlineData(2025, 11, 24, -30)] // 335 days: due in 30
    [InlineData(2025, 10, 25, 0)]   // 365 days: due today
    [InlineData(2025, 9, 20, 35)]   // 400 days: 35 overdue
    public async Task Facts_Review_CarryTheApprovalTheDueDateAndTheDelta(int year, int month, int day, int delta)
    {
        var pyme     = Pyme();
        var approved = AnalysisOf(pyme.EntityId, createdAt: At(year, month, day).AddDays(-2), reviewedAt: At(year, month, day));
        await SeedAsync(pyme, approved, CupoOf(pyme, approved));

        var facts = await FactsOfSingleOpenAsync(EventAlertTypes.PymeReviewDue);

        var approvedOn = new DateOnly(year, month, day);
        Assert.Equal(new AlertFactsDto { Date = approvedOn, SecondaryDate = approvedOn.AddDays(365), DaysDelta = delta }, facts);
    }

    [Fact]
    public async Task Facts_Bcra_CarryTheSituacionesAndTheArgentineQueryDate()
    {
        await SeedBcraAsync(Debt(4), Debt(2)); // queried 02:00 UTC on 25/10 = 24/10 in Buenos Aires

        var facts = await FactsOfSingleOpenAsync(EventAlertTypes.PymeBcraWorsened);

        Assert.Equal(new AlertFactsDto { SituacionFrom = 2, SituacionTo = 4, Date = new DateOnly(2026, 10, 24) }, facts);
    }

    [Fact]
    public async Task Facts_Bcra_NeverQueried_HasNoDate()
    {
        var pyme     = Pyme(bcraJson: Debt(3));
        var analysis = AnalysisOf(pyme.EntityId, createdAt: Now.AddDays(-30), status: AnalysisStatuses.PendingReview);
        await SeedAsync(pyme, analysis, ResultOf(analysis, Debt(1)), CupoOf(pyme, analysis));

        var facts = await FactsOfSingleOpenAsync(EventAlertTypes.PymeBcraWorsened);

        Assert.Null(facts.Date);
        Assert.Equal((1, 3), (facts.SituacionFrom, facts.SituacionTo));
    }

    [Fact]
    public async Task Facts_AreKeyedByTheOpenAlertsIds_OnCreate_UpdateInPlace_AndAPassThatChangedNothing()
    {
        var cupo = Cupo(amount: 1_000m);
        await SeedAsync(cupo, Operacion(cupo, 10m, validUntil: Past), Operacion(cupo, 1_100m));

        var created = await ReconcileWithFactsAsync();
        var open    = (await AlertsAsync()).Where(a => !a.IsResolved).Select(a => a.AlertId).Order().ToList();
        Assert.Equal(2, open.Count);
        Assert.Equal(open, created!.Keys.Order());

        // Nothing changed: the early-return path still hands the facts back.
        var unchanged = await ReconcileWithFactsAsync(Now.AddMinutes(1));
        Assert.Equal(open, unchanged!.Keys.Order());

        // Updated in place: same ids, fresh figures.
        await SeedAsync(Operacion(cupo, 5m, validUntil: Past));
        var updated  = await ReconcileWithFactsAsync(Now.AddMinutes(2));
        var expired  = Assert.Single(await OpenAsync(EventAlertTypes.CupoOperationsExpired));
        Assert.Equal(open, updated!.Keys.Order());
        Assert.Equal((15m, 2), (updated[expired.AlertId].Amount!.Value, updated[expired.AlertId].Count!.Value));
    }

    [Fact]
    public async Task Facts_LeaveOutSuppressedCandidatesAndUntouchedKeys()
    {
        var dismissed = Cupo(amount: 1_000m);
        var zero      = Cupo(amount: 0m);
        var untouched = OpenCupoAlert(EventAlertTypes.CupoUtilizationHigh, zero.CreditLineId, Now.AddDays(-1));
        await SeedAsync(dismissed, Operacion(dismissed, 1_100m), zero, untouched);
        await ReconcileAsync();
        var toDismiss = Assert.Single(await OpenAsync(EventAlertTypes.CupoUtilizationHigh), a => a.AlertId != untouched.AlertId);
        await ResolveByUserAsync(toDismiss.AlertId, Now.AddMinutes(1));

        var facts = await ReconcileWithFactsAsync(Now.AddMinutes(2));

        Assert.NotNull(facts);
        Assert.Empty(facts!); // the dismissal holds, and the Amount-0 cupo's alert stays open without facts
        Assert.False((await AlertsAsync()).Single(a => a.AlertId == untouched.AlertId).IsResolved);
    }

    [Fact]
    public async Task Facts_AreNull_WhenThePassFails()
    {
        var ctx = NewContext();
        var sut = NewService(ctx);
        await ctx.DisposeAsync();

        Assert.Null(await sut.ReconcileAsync(Tenant, Now, CancellationToken.None));
    }

    /// <summary>
    /// A first pass holds the tenant gate (parked inside its first read); a second one that cannot enter
    /// within its timeout returns null — no facts, the listing is served as it stands.
    /// </summary>
    [Fact]
    public async Task Facts_AreNull_WhenTheGateTimesOut()
    {
        var tenant  = $"tenant-gate-{Guid.NewGuid():N}"; // the gates are static per tenant
        var blocked = DispatchProxy.Create<ICreditLineRepository, ParkedCreditLineRepository>();
        var parked  = (ParkedCreditLineRepository)(object)blocked;

        await using var ctx = NewContext();
        var holder = new CupoAlertService(blocked, new DashboardRepository(ctx),
            new AlertRepository(ctx, NullLogger<AlertRepository>.Instance), NullLogger<CupoAlertService>.Instance);
        var holding = holder.ReconcileAsync(tenant, Now, CancellationToken.None);
        await parked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await using var ctx2 = NewContext();
        var late = new CupoAlertService(
            new CreditLineRepository(ctx2, NullLogger<CreditLineRepository>.Instance), new DashboardRepository(ctx2),
            new AlertRepository(ctx2, NullLogger<AlertRepository>.Instance), NullLogger<CupoAlertService>.Instance,
            TimeSpan.FromMilliseconds(50));

        Assert.Null(await late.ReconcileAsync(tenant, Now, CancellationToken.None));

        parked.Release.SetResult();
        Assert.NotNull(await holding); // the holder finishes normally (no cupos → an empty map)
    }

    /// <summary>
    /// An <see cref="ICreditLineRepository"/> whose cupo read parks until released; every other member
    /// throws. A <see cref="DispatchProxy"/> (in the BCL, not a mocking library) so it does not have to
    /// restate the whole interface.
    /// </summary>
    public class ParkedCreditLineRepository : DispatchProxy
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name == nameof(ICreditLineRepository.GetActiveForCupoAlertsAsync)
                ? ParkAsync()
                : throw new NotSupportedException(targetMethod?.Name);

        private async Task<IReadOnlyList<CupoAlertSource>> ParkAsync()
        {
            Entered.SetResult();
            await Release.Task;
            return Array.Empty<CupoAlertSource>();
        }
    }
}
