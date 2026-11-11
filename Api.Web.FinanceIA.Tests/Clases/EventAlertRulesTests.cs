// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-25

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Models.Financia;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// The rules the dashboard and the event alerts share: <see cref="MaturityBands"/>,
/// <see cref="BcraSignalRule"/> and the <see cref="EventAlertTypes"/> keys.
/// </summary>
public sealed class EventAlertRulesTests
{
    // 12:00 in Buenos Aires on 25/10/2026.
    private static readonly DateTime Now = new(2026, 10, 25, 15, 0, 0, DateTimeKind.Utc);

    private static DateTime Day(int month, int day) => new(2026, month, day, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(10, 24, CreditLineRules.Vencida)]
    [InlineData(10, 25, MaturityBands.Days0To30)]
    [InlineData(11, 24, MaturityBands.Days0To30)]  // day 30
    [InlineData(11, 25, MaturityBands.Days31To60)] // day 31
    [InlineData(12, 24, MaturityBands.Days31To60)] // day 60
    [InlineData(12, 25, MaturityBands.Days61To90)]
    public void BucketOf_CountsArgentineDaysFromTheArgentineDate(int month, int day, string expected)
        => Assert.Equal(expected, MaturityBands.BucketOf(Day(month, day), MaturityBands.AsOf(Now), Now));

    [Fact]
    public void AsOf_IsTheArgentineDate_NotTheUtcOne()
    {
        Assert.Equal(new DateOnly(2026, 10, 25), MaturityBands.AsOf(new DateTime(2026, 10, 26, 2, 59, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateOnly(2026, 10, 26), MaturityBands.AsOf(new DateTime(2026, 10, 26, 3, 0, 0, DateTimeKind.Utc)));
    }

    private static DashboardBcraComparisonRow Row(
        Guid entityId, string? nowEstado, string? nowSit, string? thenEstado, string? thenSit,
        DateTime? generatedAt, Guid? resultId = null, bool hasAnalysis = true)
        => new(entityId, Guid.NewGuid(), nowEstado, nowSit, null,
            hasAnalysis ? Guid.NewGuid() : null, resultId ?? Guid.NewGuid(), generatedAt, thenEstado, thenSit);

    // BcraDeudaConsultaEstado as JSON_VALUE returns it: DebtFound / NoDebtReported / LookupFailed.
    private static readonly string Debt   = ((int)BcraDeudaConsultaEstado.DebtFound).ToString();
    private static readonly string NoDebt = ((int)BcraDeudaConsultaEstado.NoDebtReported).ToString();
    private static readonly string Failed = ((int)BcraDeudaConsultaEstado.LookupFailed).ToString();

    [Fact]
    public void PerEntity_PicksTheLatestAnalysisPerEntity_AndReadsBothSides()
    {
        var entity = Guid.NewGuid();
        var rows = new[]
        {
            Row(entity, Debt, "3", NoDebt, null, Now.AddDays(-60)),
            Row(entity, Debt, "3", Debt, "3", Now.AddDays(-10)),
        };

        var comparison = Assert.Single(BcraSignalRule.PerEntity(rows));

        Assert.Equal(rows[1], comparison.Row);
        Assert.Equal((3, 3), (comparison.Then, comparison.Now));
        Assert.True(comparison.IsComparable);
        Assert.False(comparison.IsWorsened);
    }

    [Fact]
    public void PerEntity_SkipsRowsWithoutAResultOrAnalysis_AndFlagsUnreadableSides()
    {
        var withoutResult = Row(Guid.NewGuid(), Debt, "6", Debt, "1", Now) with { ResultId = null };
        var legacy        = Row(Guid.NewGuid(), Debt, "6", Debt, "1", Now, hasAnalysis: false);
        var failedNow     = Row(Guid.NewGuid(), Failed, null, Debt, "1", Now);
        var worse         = Row(Guid.NewGuid(), Debt, "2", NoDebt, null, Now);

        var result = BcraSignalRule.PerEntity([withoutResult, legacy, failedNow, worse]).ToDictionary(c => c.Row.EntityId);

        Assert.Equal(2, result.Count);
        Assert.False(result[failedNow.EntityId].IsComparable);
        Assert.False(result[failedNow.EntityId].IsWorsened);
        Assert.Equal((1, 2), (result[worse.EntityId].Then, result[worse.EntityId].Now));
        Assert.True(result[worse.EntityId].IsWorsened);
    }

    [Fact]
    public void MetricKeys_RoundTrip_AndFitTheColumn()
    {
        var id = Guid.NewGuid();

        Assert.True(EventAlertTypes.TryParseEntityMetricKey(EventAlertTypes.EntityMetricKey(id), out var entityId));
        Assert.Equal(id, entityId);
        Assert.True(EventAlertTypes.TryParseCupoMetricKey(EventAlertTypes.CupoMetricKey(id), out var cupoId));
        Assert.Equal(id, cupoId);
        Assert.False(EventAlertTypes.TryParseEntityMetricKey(EventAlertTypes.CupoMetricKey(id), out _));
        Assert.False(EventAlertTypes.TryParseCupoMetricKey(EventAlertTypes.EntityMetricKey(id), out _));
        Assert.False(EventAlertTypes.TryParseEntityMetricKey(null, out _));
        Assert.True(EventAlertTypes.EntityMetricKey(id).Length <= 50); // Alerts.MetricKey nvarchar(50)
    }

    [Fact]
    public void All_IsEveryEventType_Once()
    {
        Assert.Equal(
            new[]
            {
                EventAlertTypes.CupoOperationsExpired, EventAlertTypes.CupoUtilizationHigh, EventAlertTypes.CupoOperationsExpiring,
                EventAlertTypes.PymeReviewDue, EventAlertTypes.PymeBcraWorsened
            },
            EventAlertTypes.All);
        Assert.Equal(EventAlertTypes.All.Length, EventAlertTypes.All.Distinct().Count());
    }
}
