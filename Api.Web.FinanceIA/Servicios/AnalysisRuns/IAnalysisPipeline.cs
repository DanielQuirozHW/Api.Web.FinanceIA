// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Servicios.AnalysisRuns;

/// <summary>
/// The long part of POST /api/analyses: from reading the stored balance to the persisted, scored,
/// notified analysis. It starts on a document the caller has ALREADY moved to "Processing" and always
/// leaves it terminal — Completed, or Failed — except where noted on
/// <see cref="RunAsync"/>.
/// </summary>
public interface IAnalysisPipeline
{
    /// <summary>
    /// Runs one analysis.
    /// <para>
    /// <paramref name="ct"/> is observed only by the storage read; from the Claude call onward the server
    /// owns the work and everything runs on CancellationToken.None. A fault is rethrown after the
    /// document has been driven to "Failed". The one exception, kept from the synchronous endpoint: a
    /// cancellation surfacing AFTER the server took the work on, inside an HTTP request, is rethrown
    /// untouched and leaves the document as it is (a client disconnect must not clobber the run).
    /// A background run has no client, so there it is a fault like any other.
    /// </para>
    /// </summary>
    Task<AnalysisRunOutcome> RunAsync(AnalysisRun run, CancellationToken ct);
}
