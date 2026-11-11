// Project : Api.Web.FinanceIA
// Created : 2026-10-25

namespace Api.Web.FinanceIA.Clases;

/// <summary>Where a PyME stands against its annual review, from the date of its latest approved analysis.</summary>
/// <param name="ApprovedOn">The Argentine calendar date of that approval.</param>
/// <param name="DueDate">The date the review falls due: <paramref name="ApprovedOn"/> plus <see cref="AnnualReviewRule.DueDays"/>.</param>
/// <param name="AgeDays">Argentine calendar days from <paramref name="ApprovedOn"/> to the day it is evaluated on.</param>
internal readonly record struct AnnualReviewStatus(DateOnly ApprovedOn, DateOnly DueDate, int AgeDays)
{
    /// <summary>Days past <see cref="DueDate"/>: zero or more when due, negative while it is still coming up.</summary>
    public int DaysDelta => AgeDays - AnnualReviewRule.DueDays;

    /// <summary>The review is due ("vencida"): <see cref="AnnualReviewRule.DueDays"/> days or more.</summary>
    public bool IsDue => AgeDays >= AnnualReviewRule.DueDays;

    /// <summary><see cref="AnnualReviewRule.DueSeverity"/> when due, <see cref="AnnualReviewRule.UpcomingSeverity"/> otherwise.</summary>
    public string Severity => IsDue ? AnnualReviewRule.DueSeverity : AnnualReviewRule.UpcomingSeverity;
}

/// <summary>
/// The annual review rule of a PyME with an Active cupo, in one place: <see cref="CupoAlertService"/>'s
/// PYME_REVIEW_DUE and the portfolio dashboard's annualReview both read it, so the two can never disagree
/// about a PyME.
/// <para>
/// Extracted verbatim from <c>CupoAlertService.EvaluateReview</c>: Argentine calendar days since the latest
/// approved analysis (ReviewedAt, else CreatedAt — the date the caller passes in); 365 or more is due, 335 to
/// 364 is coming up, anything less is nothing.
/// </para>
/// </summary>
internal static class AnnualReviewRule
{
    /// <summary>The annual review is due at this many Argentine days since the latest approval.</summary>
    public const int DueDays = 365;

    /// <summary>A month ahead of <see cref="DueDays"/>, the review is coming up.</summary>
    public const int UpcomingDays = 335;

    // The TitleCase spellings CK_Alerts_Severity allows — PYME_REVIEW_DUE stores them, and the dashboard
    // shows the same word so the Portal can render both with one severity chip.

    /// <summary>Severity of a due review ("Revisión anual vencida").</summary>
    public const string DueSeverity = "High";

    /// <summary>Severity of a review coming up ("Revisión anual próxima").</summary>
    public const string UpcomingSeverity = "Medium";

    /// <summary>
    /// The review status of a PyME approved at <paramref name="approvedAtUtc"/>, counted on the Argentine
    /// calendar to <paramref name="asOf"/>; null when it is less than <see cref="UpcomingDays"/> days old.
    /// </summary>
    public static AnnualReviewStatus? Evaluate(DateTime approvedAtUtc, DateOnly asOf)
    {
        var approvedOn = MaturityBands.AsOf(approvedAtUtc);
        var ageDays    = asOf.DayNumber - approvedOn.DayNumber;

        if (ageDays < UpcomingDays) return null;

        return new AnnualReviewStatus(approvedOn, approvedOn.AddDays(DueDays), ageDays);
    }
}
