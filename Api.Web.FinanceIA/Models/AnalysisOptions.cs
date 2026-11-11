// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Models;

/// <summary>
/// The background-analysis switches, bound from the "Analysis" configuration section. They govern
/// only POST /api/analyses requests that carry <c>Prefer: respond-async</c>; a request without that
/// header runs inside the HTTP request exactly as before. Every value has a working default, so an
/// environment with no section runs the documented behaviour.
/// </summary>
public sealed record AnalysisOptions
{
    /// <summary>
    /// How many background analyses this instance runs at once. Default 2: each run is two to three
    /// minutes of Claude extraction plus scoring, and a burst must wait in the queue rather than starve
    /// the App Service or Claude's rate limit. Values below 1 read as 1.
    /// </summary>
    public int MaxConcurrentJobs { get; init; } = 2;

    /// <summary>
    /// How many background analyses may be queued or running on this instance in total. Default 100.
    /// Past it a new request is refused with 503 BEFORE the document moves — nothing is spent and the
    /// caller can retry. Values below 1 read as 1.
    /// </summary>
    public int QueueCapacity { get; init; } = 100;

    /// <summary>
    /// How many background analyses ONE tenant may have queued or running on this instance. Default 5.
    /// Past it the request is refused with 429. It keeps one tenant from filling the shared queue, and
    /// it bounds how far a burst can overshoot the monthly analysis quota: the quota counts
    /// [dbo].[Analyses] rows, which are written only when a run completes, so none of the queued runs
    /// is visible to the gate yet. Values below 1 read as 1.
    /// </summary>
    public int MaxQueuedPerTenant { get; init; } = 5;

    /// <summary>
    /// How long, in minutes, a document may be seen in "Processing" with no run on this instance before
    /// the sweep marks it Failed and tells the uploader. Default 20.
    /// <para>
    /// Documents carries no "processing started at" column, so the clock is the time the sweep FIRST
    /// SAW the row in that state; it restarts with the process. A document this instance is running or
    /// holds in its queue is never swept, however long it waits. With more than one instance, keep this
    /// above the worst-case run (three 120 s extraction attempts plus backoff, twice when the
    /// high-DPI fallback runs, plus BCRA and the narrative: about 16 minutes) — another instance's runs
    /// are invisible here. Values below 1 read as 1.
    /// </para>
    /// </summary>
    public int StaleProcessingMinutes { get; init; } = 20;

    /// <summary>
    /// Period of the stale-Processing sweep, in minutes. Default 5. Values below 1 read as 1.
    /// </summary>
    public int StaleSweepMinutes { get; init; } = 5;
}
