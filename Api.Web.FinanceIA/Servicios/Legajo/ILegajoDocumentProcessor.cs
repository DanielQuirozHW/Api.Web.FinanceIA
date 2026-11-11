// Project : Api.Web.FinanceIA
// Created : 2026-09-28

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// The per-document work of the legajo pipeline, run by <see cref="LegajoPipelineWorker"/> in a fresh
/// DI scope per job. Separate from the hosted service so it can be tested without a host.
/// </summary>
public interface ILegajoDocumentProcessor
{
    /// <summary>
    /// Dispatches by status. A Queued document is claimed (Queued → Classifying) and classified; a
    /// ReadyForExtraction document of a type with an extractor is claimed (ReadyForExtraction →
    /// Extracting) and extracted (Phase 2b). Does nothing when neither claim is won — a duplicate item,
    /// another worker, or a type without an extractor. Never throws once a claim is taken, except on
    /// shutdown: any failure, expected or not, ends in Failed with a short code.
    /// </summary>
    Task ProcessAsync(LegajoWorkItem item, CancellationToken ct);

    /// <summary>
    /// The documents the worker may have lost, made claimable again and returned for enqueueing:
    /// Classifying rows past <c>Legajo:StaleMinutes</c> (moved back to Queued), Extracting rows past it
    /// (moved back to ReadyForExtraction), and the waiting rows — Queued, and ReadyForExtraction of a
    /// type with an extractor whose legajo has consent — ALL of them when
    /// <paramref name="requeueEveryQueued"/> (startup: the in-memory queue did not survive the
    /// restart), otherwise only those waiting for longer than the window (the periodic sweep).
    /// Documents in <paramref name="inFlight"/> are being processed by this instance and are skipped.
    /// </summary>
    Task<IReadOnlyList<LegajoWorkItem>> RecoverAsync(
        bool requeueEveryQueued, IReadOnlySet<Guid> inFlight, CancellationToken ct);
}
