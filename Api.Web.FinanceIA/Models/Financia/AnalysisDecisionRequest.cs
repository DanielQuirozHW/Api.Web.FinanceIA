// Project : Api.Web.FinanceIA
// Created : 2026-07-13
// Modified: 2026-09-07

using System.ComponentModel.DataAnnotations;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Request body for PATCH /api/analyses/{id}/decision.
/// Carries one step of the two-step decision flow: an Analyst deciding an amount, or a
/// RiskManager/Admin confirming, rejecting or returning what an Analyst pre-approved.
/// </summary>
public sealed class AnalysisDecisionRequest
{
    /// <summary>
    /// The decision. One of:
    /// <list type="bullet">
    /// <item><c>Approved</c> — commit the amount. For an Analyst this becomes a PRE-approval when
    /// the amount exceeds their limit for the currency; for a RiskManager/Admin it confirms.</item>
    /// <item><c>Rejected</c> — refuse the analysis.</item>
    /// <item><c>PendingReview</c> — return it to the analyst to redo. RiskManager/Admin only, and
    /// only on an analysis that is currently pre-approved.</item>
    /// </list>
    /// Case-sensitive, to match [dbo].[Analyses] CK_Analyses_Status.
    /// </summary>
    [Required]
    public string Decision { get; set; } = string.Empty;

    /// <summary>
    /// Amount being committed. REQUIRED when <see cref="Decision"/> is <c>Approved</c>, and must be
    /// greater than zero; ignored otherwise.
    /// <para>
    /// Nullable on the type rather than <c>[Required]</c> on purpose: a rejection has no amount, and
    /// enforcing it here would reject a legitimate payload. The requirement is applied in the action,
    /// which can also name the offending field in the 400.
    /// </para>
    /// </summary>
    public decimal? Amount { get; set; }

    /// <summary>
    /// ISO 4217 currency of <see cref="Amount"/> (ARS, USD or EUR). REQUIRED when
    /// <see cref="Decision"/> is <c>Approved</c>; ignored otherwise. The currency selects WHICH
    /// approval limit applies, so it is not a display detail.
    /// </summary>
    public string? Currency { get; set; }

    /// <summary>
    /// Optional reviewer notes (recommended on rejection and when returning for rework). On a persona humana
    /// analysis whose tenant sets pf_aprobacion_requiere_motivo, an approval without it is 400 reason_required.
    /// </summary>
    public string? ReviewNotes { get; set; }

    /// <summary>
    /// Persona humana analyses only (DDL 072): the approver confirms the counter-guarantees. Every approval
    /// of a persona analysis must carry it as true (400 confirmations_required otherwise); ignored on a
    /// balance analysis.
    /// </summary>
    public bool? CounterGuaranteesConfirmed { get; set; }

    /// <summary>
    /// Persona humana analyses only (DDL 072): the approver confirms the Fondo de Riesgo exposure limit.
    /// Same rule as <see cref="CounterGuaranteesConfirmed"/>.
    /// </summary>
    public bool? FdrLimitConfirmed { get; set; }

    /// <summary>
    /// Balance analyses only (score credibility 1b-5): the decider confirms they read the score-reliability
    /// caveats. Required as true on an <c>Approved</c> or <c>Rejected</c> — a pre-approval and a confirmer's
    /// quorum vote included — whenever GET /api/analyses/{id} reports
    /// <c>decisionRequirements.acknowledgeCaveats</c> (409 <c>caveats_not_acknowledged</c> otherwise, and nothing
    /// is written). Ignored on <c>PendingReview</c> and on a persona humana analysis. Recorded in the decision's
    /// audit row.
    /// </summary>
    public bool? AcknowledgedCaveats { get; set; }

    /// <summary>
    /// Balance analyses only (score credibility 1b-5): the decider's record that they checked the balance figures
    /// against the document. Required — at least 20 characters once trimmed — on an <c>Approved</c> or
    /// <c>Rejected</c> whenever <c>decisionRequirements.figuresReviewNote</c> is true, i.e. the figures failed
    /// their consistency checks (409 <c>figures_review_required</c> otherwise; correcting the figures with
    /// POST /balance-corrections first clears the requirement). At most 1000 characters (400). Free text: stored
    /// in the decision's audit row only, never logged and never sent to a model.
    /// </summary>
    public string? FiguresReviewNote { get; set; }

    /// <summary>
    /// The operations this decision is made up of. Optional, and validated only when supplied.
    ///
    /// <para>
    /// WHICH record they become follows the same rule as the amount beside them. On a PRE-approval
    /// they are what the analyst PROPOSED and are stored as such; on an approval or a confirmation
    /// they are what was SETTLED ON, and the confirmer's version is the one the granted line is
    /// built from. A confirmation never rewrites the proposal — the pair is the traceability.
    /// </para>
    ///
    /// <para>
    /// Omitted or empty is legitimate: a decision with no operations is stored with none, not with
    /// an empty list. Ignored entirely on a rejection or a return, which commit nothing.
    /// </para>
    ///
    /// <para>
    /// On a persona humana approval whose tenant requires a counter-guarantee coverage
    /// (pf_contragarantia_cobertura_min_pct above 0), none of them may be <c>aSolaFirma</c>: 400
    /// person_sola_firma_not_allowed, and nothing is written. GET /api/analyses/{id} exposes the rule
    /// as <c>solaFirmaAllowed</c> and <c>minCounterGuaranteeCoveragePct</c>.
    /// </para>
    /// </summary>
    public IReadOnlyList<AnalysisDecisionOperation>? Operations { get; set; }
}
