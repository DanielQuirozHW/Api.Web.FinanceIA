// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Servicios.AnalysisRuns;

/// <summary>
/// Hands background analyses from the request that accepted them to <see cref="AnalysisJobWorker"/>.
/// In memory: a restart loses whatever is queued, and the worker's stale-Processing sweep is what
/// releases those documents.
/// </summary>
public interface IAnalysisJobQueue
{
    /// <summary>
    /// Queues one run. Never blocks; false only if the channel is full, which admission
    /// (<see cref="AnalysisRunRegistry.TryAdmit"/>) is sized to prevent.
    /// </summary>
    bool TryEnqueue(AnalysisRun run);

    /// <summary>Reads queued runs until <paramref name="ct"/> is cancelled.</summary>
    IAsyncEnumerable<AnalysisRun> ReadAllAsync(CancellationToken ct);
}
