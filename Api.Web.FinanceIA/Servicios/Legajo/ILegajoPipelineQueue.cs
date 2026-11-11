// Project : Api.Web.FinanceIA
// Created : 2026-09-28

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// The in-process queue between the request path (upload, consent, reprocess) and
/// <see cref="LegajoPipelineWorker"/>. The DATABASE is the source of truth — a document is only
/// processed if the worker can claim it from Queued — so the queue is a wake-up signal: losing an item
/// (a restart) is repaired by the startup requeue, and a duplicate item is refused by the claim.
/// </summary>
public interface ILegajoPipelineQueue
{
    /// <summary>Queues a document for classification. Never blocks; never throws for a full queue.</summary>
    void Enqueue(LegajoWorkItem item);

    /// <summary>Reads items as they arrive, until <paramref name="ct"/> is cancelled.</summary>
    IAsyncEnumerable<LegajoWorkItem> ReadAllAsync(CancellationToken ct);
}
