// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-25

using Api.Web.FinanceIA.Clases;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// <see cref="AnnualReviewRule"/> alone: Argentine calendar days since the approval, nothing under 335,
/// coming up from 335, due from 365.
/// </summary>
public sealed class AnnualReviewRuleTests
{
    private static readonly DateOnly AsOf = new(2026, 10, 23);

    [Theory]
    [InlineData(0,   false, null)]
    [InlineData(334, false, null)]
    [InlineData(335, true,  "Medium")]
    [InlineData(364, true,  "Medium")]
    [InlineData(365, true,  "High")]
    [InlineData(366, true,  "High")]
    public void Evaluate_TheEdges(int ageDays, bool flagged, string? severity)
    {
        var approvedOn = AsOf.AddDays(-ageDays);

        var review = AnnualReviewRule.Evaluate(approvedOn.ToDateTime(new TimeOnly(15, 0), DateTimeKind.Utc), AsOf);

        Assert.Equal(flagged, review.HasValue);
        if (review is not { } r) return;

        Assert.Equal(approvedOn, r.ApprovedOn);
        Assert.Equal(approvedOn.AddDays(365), r.DueDate);
        Assert.Equal(ageDays, r.AgeDays);
        Assert.Equal(ageDays - 365, r.DaysDelta);
        Assert.Equal(ageDays >= 365, r.IsDue);
        Assert.Equal(severity, r.Severity);
    }

    [Fact]
    public void Evaluate_DatesTheApprovalOnTheArgentineCalendar()
    {
        // 01:00 UTC on 24/10/2025 is 22:00 on 23/10/2025 in Buenos Aires: 365 days, not the UTC date's 364.
        var review = AnnualReviewRule.Evaluate(new DateTime(2025, 10, 24, 1, 0, 0, DateTimeKind.Utc), AsOf);

        Assert.Equal((new DateOnly(2025, 10, 23), 365, "High"), (review!.Value.ApprovedOn, review.Value.AgeDays, review.Value.Severity));
    }
}
