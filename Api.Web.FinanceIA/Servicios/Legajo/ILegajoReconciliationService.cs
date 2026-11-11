// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// Rebuilds a legajo's reconciled inventory and incomes (M10 Phase 3b) from its approved documents —
/// every live document in Ready with a current extraction run, read on its effective values.
/// Deterministic: no model call. The tenant is passed as a parameter; it is never resolved here.
/// </summary>
public interface ILegajoReconciliationService
{
    /// <summary>
    /// Rebuilds the legajo's rows. Retries once when it loses a race to a concurrent rebuild or edit;
    /// reports <see cref="LegajoRebuildOutcome.LostRace"/> when the retry loses too.
    /// </summary>
    Task<LegajoRebuildOutcome> RebuildAsync(Guid personLegajoId, string tenantId, CancellationToken ct);

    /// <summary>
    /// The trigger form, for the flows that change what is approved (an approval, a correction that
    /// reopens a document, a new extraction, a delete): never throws — a failure is logged and the rows
    /// stay as they were until the next rebuild. Cancellation is still honoured.
    /// </summary>
    Task TryRebuildAsync(Guid personLegajoId, string tenantId, string trigger, CancellationToken ct);
}
