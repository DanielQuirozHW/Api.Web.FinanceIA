// Project : Api.Web.FinanceIA
// Created : 2026-08-10

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios;

// ── Interface ─────────────────────────────────────────────────────────────────

/// <summary>
/// Produces a deterministic, es-AR reading of the cash-flow statement ("LECTURA DEL FLUJO") as a
/// list of finished sentences. Pure and stateless — NO model call, NO I/O, NO dependencies — derived
/// entirely from figures already present in <see cref="CashFlowData"/> plus the reconciliation state.
///
/// The reading is COMPUTED AT READ TIME and never persisted: this makes it available on analyses that
/// predate the feature without re-analysis, and lets a wording fix improve every analysis
/// retroactively instead of baking stale text into stored rows. It is CONTEXT for the analyst and
/// never a scoring input.
///
/// The narrative states WHAT the figures show, never WHY. Every sentence rests on either the SIGN of a
/// non-null subtotal or a non-null component — never on the label of a section. Causal connectors are
/// deliberately absent from the output.
/// </summary>
public interface ICashFlowNarrativeService
{
    /// <summary>
    /// Builds the reading. Returns an empty list when the statement is not present, when every figure
    /// is null, or (per the reconciliation gate) a single caution sentence when the statement does not
    /// reconcile. Never throws.
    /// </summary>
    /// <param name="cashFlow">The extracted cash-flow snapshot (figures shown as-extracted).</param>
    /// <param name="reconciliation">
    /// The per-check reconciliation outcome, recomputed at read time via
    /// <see cref="ICashFlowReconciliationService.Reconcile"/>. Passed in (rather than recomputed here)
    /// so this service stays a pure function of its inputs.
    /// </param>
    IReadOnlyList<string> Read(CashFlowData cashFlow, CashFlowReconciliationResult reconciliation);
}
