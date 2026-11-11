// Project : Api.Web.FinanceIA
// Created : 2026-09-16

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The [dbo].[Analyses].[Status] values the decision flow writes or branches on — the vocabulary of
/// CK_Analyses_Status.
/// </summary>
/// <remarks>
/// Named, and named in ONE place, because the quorum engine compares against them from three layers
/// at once: the controller routes on them, the repository guards its conditional UPDATE on them, and
/// the service decides whether voting is still open by them. A typo in any one copy would route a
/// credit decision silently wrong, and the compiler cannot see a typo inside a string literal.
///
/// <para>
/// 'PreApproved' was admitted to the constraint by DDL 060; the other three have always been legal.
/// Adding a value here is not enough on its own — the constraint lives on every tenant database and
/// takes a numbered DDL to widen, which is precisely why 060 exists.
/// </para>
/// </remarks>
public static class AnalysisStatuses
{
    /// <summary>Awaiting a decision, and the destination when a pre-approval is returned for rework.</summary>
    public const string PendingReview = "PendingReview";

    /// <summary>An analyst committed above their limit; it waits for the manager signatures.</summary>
    public const string PreApproved = "PreApproved";

    /// <summary>Settled: the credit is committed.</summary>
    public const string Approved = "Approved";

    /// <summary>Settled: the credit is refused.</summary>
    public const string Rejected = "Rejected";
}
