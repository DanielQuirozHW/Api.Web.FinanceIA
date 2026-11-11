// Project : Api.Web.FinanceIA
// Created : 2026-11-09

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The one automatic retry of a background run, as GET /api/analyses/runs reports it on a running or a
/// failed item. Absent (<c>null</c>) when no automatic retry applies.
/// </summary>
public sealed record AnalysisRunAutoRetry
{
    /// <summary>
    /// <c>scheduled</c> (waiting for its delay; the item is in <c>running</c>), <c>running</c> (the retry is
    /// queued or executing; in <c>running</c>) or <c>exhausted</c> (the retry ran and failed too; in
    /// <c>failed</c>, whose failure fields are reported exactly as without a retry). One of
    /// <see cref="AnalysisRunAutoRetryStates.All"/>.
    /// </summary>
    public required string State { get; init; }

    /// <summary>
    /// When the retry was due to start (UTC): set for <c>scheduled</c> and <c>running</c>, null for
    /// <c>exhausted</c>.
    /// </summary>
    public DateTime? ScheduledAt { get; init; }
}

/// <summary>The closed vocabulary of <see cref="AnalysisRunAutoRetry.State"/>.</summary>
public static class AnalysisRunAutoRetryStates
{
    /// <summary>Waiting for its delay.</summary>
    public const string Scheduled = "scheduled";

    /// <summary>Queued again, or executing.</summary>
    public const string Running = "running";

    /// <summary>Ran, and failed too.</summary>
    public const string Exhausted = "exhausted";

    /// <summary>Every state.</summary>
    public static IReadOnlySet<string> All { get; } =
        new HashSet<string>(StringComparer.Ordinal) { Scheduled, Running, Exhausted };
}
