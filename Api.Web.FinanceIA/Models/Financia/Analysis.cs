// Project : Api.Web.FinanceIA
// Created : 2026-05-04
// Modified: 2026-10-20

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// EF Core entity that maps to [dbo].[Analyses].
/// </summary>
public sealed class Analysis
{
    /// <summary>Primary key.</summary>
    public Guid AnalysisId { get; set; }

    /// <summary>Tenant that owns this analysis.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Entity (PyME) being analysed.</summary>
    public Guid EntityId { get; set; }

    /// <summary>Document that was analysed.</summary>
    public Guid? DocumentId { get; set; }

    /// <summary>
    /// One of <see cref="AnalysisKinds.Known"/> (DDL 072). <see cref="AnalysisKinds.Balance"/> unless the
    /// analysis was created by POST /api/person-analyses; only a <see cref="AnalysisKinds.PersonaHumana"/>
    /// row may have a NULL <see cref="DocumentId"/> (CK_Analyses_DocumentId_Kind).
    /// </summary>
    public string AnalysisKind { get; set; } = AnalysisKinds.Balance;

    /// <summary>User who requested the analysis.</summary>
    public Guid RequestedBy { get; set; }

    /// <summary>Credit amount requested by the entity. Null when not applicable.</summary>
    public decimal? RequestedAmount { get; set; }

    /// <summary>
    /// Current workflow status
    /// (Queued | Processing | PendingReview | Reviewed | PreApproved | Approved | Rejected).
    /// Constrained by CK_Analyses_Status; 'PreApproved' was admitted by DDL 060.
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Fiscal year the analysis covers.</summary>
    public int FiscalYear { get; set; }

    /// <summary>Optional reference to an earlier analysis for comparison.</summary>
    public Guid? ComparativeAnalysisId { get; set; }

    /// <summary>AI model identifier used for analysis.</summary>
    public string AiModel { get; set; } = string.Empty;

    /// <summary>Sampling temperature used (must be 0.00).</summary>
    public decimal AiTemperature { get; set; }

    /// <summary>Version of the system prompt used.</summary>
    public string PromptVersion { get; set; } = string.Empty;

    /// <summary>UTC timestamp when AI processing started.</summary>
    public DateTime? ProcessingStartedAt { get; set; }

    /// <summary>UTC timestamp when AI processing completed.</summary>
    public DateTime? ProcessingCompletedAt { get; set; }

    /// <summary>Total processing time in milliseconds.</summary>
    public int? ProcessingDurationMs { get; set; }

    // ── Pre-approval record (DDL 058) ─────────────────────────────────────────
    // The FIRST of the two decision records. Written when an Analyst decides an amount ABOVE their
    // per-currency limit: the analysis moves to 'PreApproved' and waits for a RiskManager or Admin.
    // All four stay NULL on an analysis that was approved directly (within limit) or that predates
    // the two-step flow — there is no backfill, and NULL reads as "no pre-approval was made".
    // A confirmation NEVER overwrites these: the pre-approver and the amount they proposed are what
    // makes the second signature meaningful.

    /// <summary>User who pre-approved the analysis. Null when no pre-approval step occurred.</summary>
    public Guid? PreApprovedBy { get; set; }

    /// <summary>UTC timestamp of the pre-approval.</summary>
    public DateTime? PreApprovedAt { get; set; }

    /// <summary>Amount the pre-approver proposed. May differ from the amount finally confirmed.</summary>
    public decimal? PreApprovedAmount { get; set; }

    /// <summary>ISO 4217 currency of <see cref="PreApprovedAmount"/>.</summary>
    public string? PreApprovedCurrency { get; set; }

    // ── Final decision record ─────────────────────────────────────────────────
    // ReviewedBy/ReviewedAt/ReviewNotes are REUSED as the final-confirmation record rather than
    // duplicated: they already meant "who settled this and when", which is exactly what a
    // confirmation is. DecisionAmount/DecisionCurrency (DDL 058) add the figure that was actually
    // committed — on a confirmation this is the RiskManager's number, which may differ from
    // PreApprovedAmount.

    /// <summary>User who recorded the final decision: the deciding Analyst, or the confirming RiskManager/Admin.</summary>
    public Guid? ReviewedBy { get; set; }

    /// <summary>UTC timestamp of the final decision.</summary>
    public DateTime? ReviewedAt { get; set; }

    /// <summary>Free-text notes from whoever recorded the final decision.</summary>
    public string? ReviewNotes { get; set; }

    /// <summary>Amount actually committed by the final decision. Null on a rejection or while only pre-approved.</summary>
    public decimal? DecisionAmount { get; set; }

    /// <summary>ISO 4217 currency of <see cref="DecisionAmount"/>.</summary>
    public string? DecisionCurrency { get; set; }

    // ── Operations behind each record (DDL 061) ───────────────────────────────
    // The two amount columns above say HOW MUCH each side committed to; these two say WHAT OF. Both
    // hold a JSON array of AnalysisDecisionOperation as a plain string, serialised at the controller
    // boundary exactly like Entities.LufeDataJson and AnalysisResults.BcraDataJson — no value
    // converter, no owned entity.
    //
    // NULL means no operations were recorded on that side, and the two are independent:
    //   · proposed non-NULL  -> an analyst escalated a proposal above their limit
    //   · confirmed non-NULL -> a figure was actually committed, in one step or two
    // A one-step approval therefore fills CONFIRMED and leaves PROPOSED null, matching the rule DDL
    // 058 already applies to PreApprovedAmount: the pre-approval columns record an event that either
    // happened or did not. A confirmation NEVER rewrites the proposed blob.

    /// <summary>
    /// JSON array of the operations the PRE-approver proposed. Null when no pre-approval step
    /// occurred, or when the pre-approval carried no operations.
    /// </summary>
    public string? ProposedOperationsJson { get; set; }

    /// <summary>
    /// JSON array of the operations the final decision settled on — the confirmer's version, which
    /// may differ from <see cref="ProposedOperationsJson"/>. Null on a rejection, on a return, while
    /// an analysis is only pre-approved, and when the approval carried no operations.
    /// </summary>
    public string? ConfirmedOperationsJson { get; set; }

    // ── Confirmations behind each record (DDL 072, persona humana only) ───────
    // JSON of AnalysisDecisionChecks: the counter-guarantee and Fondo de Riesgo confirmations and the
    // reason one approval act carried. Paired like the operations above — the pre-approval writes its
    // own, the final approval writes the other, and neither rewrites the other. NULL on every balance
    // analysis and on a persona analysis that was not approved.

    public string? PreApprovalChecksJson { get; set; }

    public string? ConfirmationChecksJson { get; set; }

    /// <summary>Final risk score assigned after review.</summary>
    public decimal? FinalRiskScore { get; set; }

    /// <summary>Final risk level assigned after review (Low | Medium | High | Critical).</summary>
    public string? FinalRiskLevel { get; set; }

    /// <summary>Whether this analysis has been submitted to the SGR.</summary>
    public bool SentToSGR { get; set; }

    /// <summary>UTC timestamp when submitted to the SGR.</summary>
    public DateTime? SentToSGRAt { get; set; }

    /// <summary>User who submitted to the SGR.</summary>
    public Guid? SentToSGRBy { get; set; }

    /// <summary>Dynamics 365 entity record identifier. Null until synced.</summary>
    public string? DynamicsEntityId { get; set; }

    /// <summary>Deep-link URL to the Dynamics 365 record. Null until synced.</summary>
    public string? DynamicsEntityUrl { get; set; }

    /// <summary>UTC timestamp when the analysis record was created.</summary>
    public DateTime CreatedAt { get; set; }
}
