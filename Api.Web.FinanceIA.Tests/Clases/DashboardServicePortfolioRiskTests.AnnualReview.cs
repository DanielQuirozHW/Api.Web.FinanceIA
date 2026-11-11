// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-25

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// annualReview: PYME_REVIEW_DUE's rule (<see cref="AnnualReviewRule"/>) over PYME_REVIEW_DUE's query — Argentine
/// days since the latest approved analysis of a PyME with an Active cupo.
/// </summary>
public sealed partial class DashboardServicePortfolioRiskTests
{
    private static readonly DateOnly TodayAr = new(2026, 10, 23);

    /// <summary>15:00 UTC (12:00 in Buenos Aires) on the Argentine date <paramref name="ageDays"/> before today.</summary>
    private static DateTime ApprovedDaysAgo(int ageDays)
        => TodayAr.AddDays(-ageDays).ToDateTime(new TimeOnly(15, 0), DateTimeKind.Utc);

    /// <summary>A PyME with an Active cupo and one Approved analysis created at <paramref name="createdAt"/>.</summary>
    private static Entity ApprovedPyme(DashboardFixture f, DateTime createdAt, DateTime? reviewedAt = null, string tenant = Tenant)
    {
        var entity = f.AddEntity(tenant: tenant);
        var (analysis, _) = f.AddAnalysis(Guid.NewGuid(), entity.EntityId, a =>
        {
            a.Status     = AnalysisStatuses.Approved;
            a.CreatedAt  = createdAt;
            a.ReviewedAt = reviewedAt;
        }, tenant: tenant);
        f.AddCreditLine(entity.EntityId, originAnalysisId: analysis.AnalysisId, tenant: tenant);
        return entity;
    }

    [Theory]
    [InlineData(334, null)]
    [InlineData(335, "Medium")]
    [InlineData(364, "Medium")]
    [InlineData(365, "High")]
    [InlineData(400, "High")]
    public async Task AnnualReview_SeverityByArgentineDaysSinceTheLatestApproval(int ageDays, string? severity)
    {
        await using var f = DashboardFixture.Create(nameof(AnnualReview_SeverityByArgentineDaysSinceTheLatestApproval) + ageDays);
        var pyme = ApprovedPyme(f, ApprovedDaysAgo(ageDays));
        await f.SaveAsync();

        var review = (await PortfolioAsync(f)).AnnualReview!;

        if (severity is null)
        {
            Assert.Empty(review.Items);
            Assert.Equal((0, 0), (review.OverdueCount, review.UpcomingCount));
            return;
        }

        var item = Assert.Single(review.Items);
        Assert.Equal(pyme.EntityId, item.EntityId);
        Assert.Equal(pyme.BusinessName, item.EntityName);
        Assert.Equal(pyme.CUIT, item.EntityCuit);
        Assert.Equal(TodayAr.AddDays(-ageDays), item.LastApprovedAt);
        Assert.Equal(TodayAr.AddDays(-ageDays + 365), item.DueDate);
        Assert.Equal(ageDays - 365, item.DaysDelta);
        Assert.Equal(severity, item.Severity);
        Assert.Equal(severity == "High" ? (1, 0) : (0, 1), (review.OverdueCount, review.UpcomingCount));
    }

    [Fact]
    public async Task AnnualReview_CountsFromReviewedAt_ElseCreatedAt_OnTheArgentineDate()
    {
        await using var f = DashboardFixture.Create(nameof(AnnualReview_CountsFromReviewedAt_ElseCreatedAt_OnTheArgentineDate));
        // Created long ago, approved (ReviewedAt) 300 days ago: not flagged — ReviewedAt wins.
        ApprovedPyme(f, ApprovedDaysAgo(800), reviewedAt: ApprovedDaysAgo(300));
        // No ReviewedAt: CreatedAt counts.
        var byCreated = ApprovedPyme(f, ApprovedDaysAgo(389));
        // 01:00 UTC on 24/10/2025 is 22:00 on 23/10/2025 in Buenos Aires: 365 days, not the UTC date's 364.
        var lateNight = ApprovedPyme(f, ApprovedDaysAgo(800), reviewedAt: new DateTime(2025, 10, 24, 1, 0, 0, DateTimeKind.Utc));
        await f.SaveAsync();

        var items = (await PortfolioAsync(f)).AnnualReview!.Items;

        Assert.Equal(
            [(byCreated.EntityId, 24, "High"), (lateNight.EntityId, 0, "High")],
            items.Select(i => (i.EntityId, i.DaysDelta, i.Severity)));
    }

    [Fact]
    public async Task AnnualReview_TheLatestApprovalOfAnyKindCounts_AndOtherStatusesDoNot()
    {
        await using var f = DashboardFixture.Create(nameof(AnnualReview_TheLatestApprovalOfAnyKindCounts_AndOtherStatusesDoNot));
        var pyme = ApprovedPyme(f, ApprovedDaysAgo(500));
        f.AddAnalysis(Guid.NewGuid(), pyme.EntityId, a =>
        {
            a.AnalysisKind = AnalysisKinds.PersonaHumana;
            a.Status       = AnalysisStatuses.Approved;
            a.CreatedAt    = ApprovedDaysAgo(340);
        });
        f.AddAnalysis(Guid.NewGuid(), pyme.EntityId, a => a.CreatedAt = ApprovedDaysAgo(10));   // PendingReview: ignored
        await f.SaveAsync();

        var item = Assert.Single((await PortfolioAsync(f)).AnnualReview!.Items);

        Assert.Equal((340 - 365, "Medium"), (item.DaysDelta, item.Severity));
    }

    [Fact]
    public async Task AnnualReview_APymeWithoutAnApprovedAnalysis_IsCountedApart_WithNoGuessedDate()
    {
        await using var f = DashboardFixture.Create(nameof(AnnualReview_APymeWithoutAnApprovedAnalysis_IsCountedApart_WithNoGuessedDate));
        var pending = f.AddEntity();
        f.AddAnalysis(Guid.NewGuid(), pending.EntityId, a => a.CreatedAt = ApprovedDaysAgo(900));
        f.AddCreditLine(pending.EntityId);
        f.AddCreditLine(f.AddEntity().EntityId);
        await f.SaveAsync();

        var review = (await PortfolioAsync(f)).AnnualReview!;

        Assert.Equal((0, 0, 2), (review.OverdueCount, review.UpcomingCount, review.WithoutApprovedAnalysisCount));
        Assert.Empty(review.Items);
    }

    [Fact]
    public async Task AnnualReview_OnlyPymesWithAnActiveCupo_OnePerPyme()
    {
        await using var f = DashboardFixture.Create(nameof(AnnualReview_OnlyPymesWithAnActiveCupo_OnePerPyme));
        var twoCupos = ApprovedPyme(f, ApprovedDaysAgo(400));
        f.AddCreditLine(twoCupos.EntityId, "USD", 10m);
        var suspended = f.AddEntity();
        f.AddAnalysis(Guid.NewGuid(), suspended.EntityId, a => { a.Status = AnalysisStatuses.Approved; a.CreatedAt = ApprovedDaysAgo(400); });
        f.AddCreditLine(suspended.EntityId, status: "Suspended");
        await f.SaveAsync();

        var review = (await PortfolioAsync(f)).AnnualReview!;

        Assert.Equal(twoCupos.EntityId, Assert.Single(review.Items).EntityId);
        Assert.Equal(1, review.OverdueCount);
    }

    [Fact]
    public async Task AnnualReview_OverdueFirst_ThenUpcoming_ByDaysDelta_CappedAtTen_WithUncappedCounts()
    {
        await using var f = DashboardFixture.Create(nameof(AnnualReview_OverdueFirst_ThenUpcoming_ByDaysDelta_CappedAtTen_WithUncappedCounts));
        for (var i = 0; i < 9; i++) ApprovedPyme(f, ApprovedDaysAgo(365 + i * 10));   // 9 overdue: 365..445
        foreach (var age in new[] { 335, 364, 350 }) ApprovedPyme(f, ApprovedDaysAgo(age));
        ApprovedPyme(f, ApprovedDaysAgo(100));                                             // fresh
        await f.SaveAsync();

        var review = (await PortfolioAsync(f)).AnnualReview!;

        Assert.Equal((9, 3), (review.OverdueCount, review.UpcomingCount));
        Assert.Equal(
            [80, 70, 60, 50, 40, 30, 20, 10, 0, -1],
            review.Items.Select(i => i.DaysDelta));
        Assert.Equal(["High", "High", "High", "High", "High", "High", "High", "High", "High", "Medium"], review.Items.Select(i => i.Severity));
    }

    [Fact]
    public async Task AnnualReview_IsTenantIsolated()
    {
        await using var f = DashboardFixture.Create(nameof(AnnualReview_IsTenantIsolated));
        var mine = ApprovedPyme(f, ApprovedDaysAgo(370));
        ApprovedPyme(f, ApprovedDaysAgo(500), tenant: Other);
        // Another tenant's approval of MY entity id does not reset my count.
        f.AddAnalysis(Guid.NewGuid(), mine.EntityId, a => { a.Status = AnalysisStatuses.Approved; a.CreatedAt = ApprovedDaysAgo(1); }, tenant: Other);
        await f.SaveAsync();

        var review = (await PortfolioAsync(f)).AnnualReview!;

        Assert.Equal((mine.EntityId, 5), (Assert.Single(review.Items).EntityId, review.Items[0].DaysDelta));
        Assert.Equal((1, 0), (review.OverdueCount, review.UpcomingCount));
    }

    /// <summary>
    /// The dashboard's annualReview and the PYME_REVIEW_DUE alerts on the same fixture: the same PyMEs, the
    /// same severity, day count and due date — one rule over one query, so they cannot disagree.
    /// </summary>
    [Fact]
    public async Task AnnualReview_AndPymeReviewDueAlerts_AgreeOnTheSameFixture()
    {
        await using var f = DashboardFixture.Create(nameof(AnnualReview_AndPymeReviewDueAlerts_AgreeOnTheSameFixture));
        foreach (var age in new[] { 100, 334, 335, 350, 364, 365, 366, 500 })
            ApprovedPyme(f, ApprovedDaysAgo(age));
        ApprovedPyme(f, ApprovedDaysAgo(900), reviewedAt: new DateTime(2025, 10, 24, 1, 0, 0, DateTimeKind.Utc));
        var noApproval = f.AddEntity();
        f.AddCreditLine(noApproval.EntityId);
        await f.SaveAsync();

        var review = (await PortfolioAsync(f)).AnnualReview!;

        await new CupoAlertService(
                new CreditLineRepository(f.Context, NullLogger<CreditLineRepository>.Instance),
                f.Repository(),
                new AlertRepository(f.Context, NullLogger<AlertRepository>.Instance),
                NullLogger<CupoAlertService>.Instance)
            .ReconcileAsync(Tenant, Now, Ct);
        f.Context.ChangeTracker.Clear();

        var alerts = await f.Context.Alerts.AsNoTracking()
            .Where(a => a.TenantId == Tenant && a.AlertType == EventAlertTypes.PymeReviewDue && !a.IsResolved)
            .ToListAsync();

        Assert.Equal(7, review.Items.Count);
        Assert.Equal(
            review.Items.Select(i => (i.EntityId, i.Severity, (decimal)(i.DaysDelta + 365))).OrderBy(x => x.EntityId),
            alerts.Select(a => (a.EntityId!.Value, a.Severity, a.MetricValue!.Value)).OrderBy(x => x.Item1));
        Assert.Equal(alerts.Count(a => a.Severity == "High"), review.OverdueCount);
        Assert.Equal(alerts.Count(a => a.Severity == "Medium"), review.UpcomingCount);
    }
}
