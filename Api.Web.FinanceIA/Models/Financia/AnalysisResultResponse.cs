// Project : Api.Web.FinanceIA
// Created : 2026-05-04
// Modified: 2026-10-20

using Api.Web.FinanceIA.Models.Arca;
using Api.Web.FinanceIA.Models.Lufe;
using Api.Web.FinanceIA.Models.Nosis;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Response DTO for a single analysis result.
/// Returned by POST /api/analyses and GET /api/analyses/{id}.
/// </summary>
public sealed class AnalysisResultResponse
{
    public Guid ResultId { get; set; }
    public Guid AnalysisId { get; set; }
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// One of <see cref="AnalysisKinds.Known"/> (DDL 072). A <see cref="AnalysisKinds.PersonaHumana"/>
    /// analysis has no balance: <see cref="DocumentId"/> is <see cref="Guid.Empty"/>, every balance field and
    /// the score are null, and <see cref="PersonEvaluation"/> carries the evaluation instead.
    /// </summary>
    public string AnalysisKind { get; set; } = AnalysisKinds.Balance;

    /// <summary>
    /// The persona humana evaluation of the solicitante (M10 Phase 4a): filters, indicators, coverage and
    /// the flag. Null on a balance analysis. Set on the detail and on POST /api/person-analyses.
    /// </summary>
    public PersonEvaluationResponse? PersonEvaluation { get; set; }

    /// <summary>The confirmations the pre-approval of a persona humana analysis carried (DDL 072); null otherwise.</summary>
    public AnalysisDecisionChecks? PreApprovalChecks { get; set; }

    /// <summary>The confirmations the final approval of a persona humana analysis carried (DDL 072); null otherwise.</summary>
    public AnalysisDecisionChecks? ConfirmationChecks { get; set; }

    /// <summary>
    /// Persona humana only: whether an approval may carry an operation a sola firma, from the tenant's CURRENT
    /// pf_contragarantia_cobertura_min_pct — exactly what PATCH .../decision enforces (400
    /// person_sola_firma_not_allowed). Top-level rather than only inside <see cref="PersonEvaluation"/>, which
    /// is null when the analysis has no solicitante. Null on a balance analysis.
    /// </summary>
    public bool? SolaFirmaAllowed { get; set; }

    /// <summary>
    /// Persona humana only: the minimum share of the aval, in whole percent (0-100), the counter-guarantee must
    /// cover. Above 0, <see cref="SolaFirmaAllowed"/> is false. Null on a balance analysis.
    /// </summary>
    public int? MinCounterGuaranteeCoveragePct { get; set; }

    /// <summary>Document that was analysed. Sourced from the Analyses table.</summary>
    public Guid DocumentId { get; set; }

    /// <summary>Identifier of the entity being analysed. Sourced from the Analyses table.</summary>
    public Guid EntityId { get; set; }

    /// <summary>Legal business name of the entity being analysed. Sourced from the Entities table.</summary>
    public string? EntityName { get; set; }

    /// <summary>CUIT of the entity being analysed. Sourced from the Entities table.</summary>
    public string? EntityCuit { get; set; }

    /// <summary>Credit amount requested for this analysis. Sourced from the Analyses table.</summary>
    public decimal? RequestedAmount { get; set; }

    /// <summary>Fiscal year of the analysed balance sheet. Sourced from the Analyses table.</summary>
    public int? FiscalYear { get; set; }

    /// <summary>
    /// Workflow status (Queued, Processing, PendingReview, Reviewed, PreApproved, Approved,
    /// Rejected). Sourced from the Analyses table. <c>PreApproved</c> means an analyst committed to
    /// an amount above their own limit and it is waiting on a RiskManager or Admin.
    /// </summary>
    public string? Status { get; set; }

    /// <summary>UserId of the analyst who requested the analysis.</summary>
    public string? RequestedBy { get; set; }

    /// <summary>Display name of the analyst who requested the analysis.</summary>
    public string? RequestedByName { get; set; }

    // ── Pre-approval record ───────────────────────────────────────────────────
    // The FIRST of two signatures, present only on an analysis that went through the two-step path.
    // All five are null on a direct approval, on a rejection, and on every analysis decided before
    // the two-step flow shipped — a client must treat "no pre-approval record" as normal, not as
    // missing data. Deliberately raw facts: there is no server-side "canIConfirm" or "isMine"
    // boolean, because who may act on this is the caller's own role question, not the payload's.

    /// <summary>UserId of the analyst who pre-approved, if the analysis was pre-approved.</summary>
    public string? PreApprovedBy { get; set; }

    /// <summary>Display name of the pre-approver. "Usuario" when the account no longer resolves.</summary>
    public string? PreApprovedByName { get; set; }

    /// <summary>UTC timestamp of the pre-approval.</summary>
    public DateTime? PreApprovedAt { get; set; }

    /// <summary>Amount the pre-approver proposed. May differ from <see cref="DecisionAmount"/>.</summary>
    public decimal? PreApprovedAmount { get; set; }

    /// <summary>ISO 4217 currency of <see cref="PreApprovedAmount"/>.</summary>
    public string? PreApprovedCurrency { get; set; }

    // ── Final decision record ─────────────────────────────────────────────────

    /// <summary>UserId of whoever recorded the final decision: the deciding analyst, or the confirming RiskManager/Admin.</summary>
    public string? ReviewedBy { get; set; }

    /// <summary>Display name of the final decider. "Usuario" when the account no longer resolves.</summary>
    public string? ReviewedByName { get; set; }

    /// <summary>Reviewer notes recorded at decision time. Sourced from the Analyses table.</summary>
    public string? ReviewNotes { get; set; }

    /// <summary>
    /// Amount actually committed by the final decision. Null while an analysis is only pre-approved,
    /// and on a rejection. Where a pre-approval record is also present and the two amounts differ,
    /// the confirmer changed the number.
    /// </summary>
    public decimal? DecisionAmount { get; set; }

    /// <summary>ISO 4217 currency of <see cref="DecisionAmount"/>.</summary>
    public string? DecisionCurrency { get; set; }

    // ── Operations behind each record (DDL 061) ───────────────────────────────
    // The amounts above say how much; these say what of. Both are typed arrays, not raw JSON: the
    // Portal renders the proposal on the confirm screen and builds the granted line from the
    // confirmed version, and neither should have to parse a string to do it.
    //
    // Read them together with the amounts, and with the same rule: proposed is populated only where
    // an analyst escalated a proposal above their limit, so a one-step approval carries confirmed
    // and no proposed. Where BOTH are present, the two are exactly what the confirmer was handed
    // and what they settled on — that difference is the record of the override.
    //
    // Either can be null while the other is not, and both are null on an analysis decided with no
    // operations or before this shipped. Deliberately raw facts, like the pre-approval block above:
    // no server-side "wasChanged" flag, no diff.

    /// <summary>
    /// Operations the pre-approver proposed. Null when no pre-approval step occurred, when the
    /// pre-approval carried none, and on every analysis decided before this shipped.
    /// </summary>
    public IReadOnlyList<AnalysisDecisionOperation>? ProposedOperations { get; set; }

    /// <summary>
    /// Operations the final decision settled on — the confirmer's version, which may differ from
    /// <see cref="ProposedOperations"/>. Null on a rejection, on a return, while an analysis is only
    /// pre-approved, and when the approval carried none.
    /// </summary>
    public IReadOnlyList<AnalysisDecisionOperation>? ConfirmedOperations { get; set; }

    /// <summary>Final risk score assigned after review. Sourced from the Analyses table.</summary>
    public decimal? FinalRiskScore { get; set; }

    /// <summary>Final risk level assigned after review (Low | Medium | High | Critical). Sourced from the Analyses table.</summary>
    public string? FinalRiskLevel { get; set; }

    /// <summary>JSON object of computed financial ratios.</summary>
    public string? RatiosJson { get; set; }

    /// <summary>JSON object of the balance check including fiscal_year_detected.</summary>
    public string? BalanceJson { get; set; }

    /// <summary>
    /// JSON array of risk alerts identified by Claude.
    /// LEGACY SURFACE — kept populated and unchanged so existing consumers (the PDF export and the
    /// current Portal) keep working. Its content is the same as <see cref="Observations"/>; new
    /// consumers should read the two explicit channels below instead.
    /// </summary>
    public string? RiskAlerts { get; set; }

    /// <summary>
    /// DETERMINISTIC CHANNEL. JSON array of alerts produced by the rule engine from the per-tenant
    /// catalogue: closed codes, severity bound to the catalogue row, same input → same output.
    /// Null for analyses persisted before the catalogue existed, and whenever no rule fired.
    /// Set only on the analysis DETAIL responses, not on the list endpoint.
    /// </summary>
    public string? CatalogueAlerts { get; set; }

    /// <summary>
    /// MODEL CHANNEL. JSON array of the findings the model itself emitted. Carries no severity
    /// binding, never feeds scoring, and its <c>code</c> values are free-form — the same finding was
    /// measured under different code strings across identical runs. Present for every analysis,
    /// including those persisted before this field existed.
    /// Set only on the analysis DETAIL responses, not on the list endpoint.
    /// </summary>
    public string? Observations { get; set; }

    /// <summary>JSON array of anomalies detected by Claude.</summary>
    public string? Anomalies { get; set; }

    public string? ExecutiveSummary { get; set; }

    /// <summary>Claude confidence score 0.0–1.0.</summary>
    public decimal? ConfidenceScore { get; set; }

    /// <summary>Highest alert severity level (low|medium|high|critical) or null.</summary>
    public string? HighestAlertLevel { get; set; }

    /// <summary>
    /// Balance-sheet validation: Passed, Warning or Failed. Empty for a persona humana analysis, which checks no
    /// balance (the row stores Passed only because the column's constraint requires one of the three).
    /// </summary>
    public string ValidationStatus { get; set; } = string.Empty;

    /// <summary>
    /// Whether the BCRA enrichment behind this analysis actually happened:
    /// <c>Complete</c> | <c>NotApplicable</c> | <c>Degraded</c>.
    /// <para>
    /// <c>Degraded</c> means the enrichment was attempted and failed — the entity could not be
    /// resolved, or BCRA did not answer. <c>NotApplicable</c> means the entity resolved and has no
    /// CUIT, so there was never anything to query.
    /// </para>
    /// <para>
    /// Both are scored WITHOUT the BCRA component, with its weight renormalised onto ratios and
    /// macro. What separates them is whether that is permanent: a <c>NotApplicable</c> analysis can
    /// never score differently, while a <c>Degraded</c> one would on a retry that succeeded. Treat
    /// <c>Degraded</c> as provisional.
    /// </para>
    /// <para>
    /// Only meaningful for analyses created after DDL 039: earlier rows were backfilled to
    /// <c>Complete</c> by a default constraint and cannot be re-derived.
    /// </para>
    /// </summary>
    public string EnrichmentStatus { get; set; } = string.Empty;

    /// <summary>
    /// Short es-AR explanation of a <c>Degraded</c> enrichment, suitable for showing to an analyst.
    /// Null for <c>Complete</c> and <c>NotApplicable</c>.
    /// </summary>
    public string? EnrichmentNotes { get; set; }

    public int? AiInputTokens { get; set; }
    public int? AiOutputTokens { get; set; }
    public DateTime GeneratedAt { get; set; }

    /// <summary>
    /// BCRA data fetched at analysis time (Central de Deudores + macro + cambiario).
    /// Null when BCRA was unavailable during analysis or the entity had no CUIT.
    ///
    /// <para>
    /// ALWAYS NULL on GET /api/analyses/{id}, which serves <see cref="BcraSummary"/> instead and
    /// leaves the full object to GET /api/analyses/{id}/context — same treatment as
    /// <see cref="Lufe"/> and the other three snapshots. Still populated in full by POST
    /// /api/analyses and the decision endpoint, which load the whole row anyway.
    /// </para>
    /// </summary>
    public BcraData? BcraData { get; set; }

    /// <summary>
    /// The two Central de Deudores fields the Score section renders — <c>deuda.estado</c> and
    /// <c>deuda.situacionMaxima</c> — extracted server-side so the detail does not have to ship the
    /// ~40KB <see cref="BcraData"/> blob to show them.
    ///
    /// <para>
    /// DETAIL-ONLY: populated exclusively by GET /api/analyses/{id}. Null everywhere else, including
    /// on the list endpoint and on the responses from POST /api/analyses and the decision endpoint,
    /// which carry the full <see cref="BcraData"/> instead. Null on the detail too when the analysis
    /// has no BCRA debt data at all — see <see cref="AnalysisResultBcraSummary"/> for what that
    /// collapses together.
    /// </para>
    /// </summary>
    public AnalysisResultBcraSummary? BcraSummary { get; set; }

    /// <summary>
    /// LUFE (government registry) data as it was when the analysis ran — a point-in-time
    /// snapshot, not the live entity value. Null when it was unavailable at analysis time
    /// or the stored snapshot could not be parsed.
    /// </summary>
    public LufeData? Lufe { get; set; }

    /// <summary>
    /// Fiscal/ARCA data as it was when the analysis ran — a point-in-time snapshot, not the
    /// live entity value. Null when it was unavailable at analysis time or the stored
    /// snapshot could not be parsed.
    /// </summary>
    public ArcaFiscalData? Fiscal { get; set; }

    /// <summary>
    /// CASFOG (SGR central de deudores / garantías) data as it was when the analysis ran —
    /// a point-in-time snapshot, not the live entity value. Null when it was unavailable at
    /// analysis time or the stored snapshot could not be parsed.
    /// </summary>
    public CasfogData? Casfog { get; set; }

    /// <summary>
    /// NOSIS (WS.01 variables) data as it was when the analysis ran — a point-in-time snapshot,
    /// not the live entity value. Null when it was unavailable at analysis time or the stored
    /// snapshot could not be parsed. Analyses created before the NOSIS integration shipped, and
    /// any created while <c>Nosis:Enabled</c> is false, carry null here permanently — the snapshot
    /// is write-once and never back-filled from the live entity.
    ///
    /// <see cref="NosisData"/> carries PII — including natural-person data (documento, fecha de
    /// nacimiento, sexo, edad, domicilio) since the analyst's primary variable set was mapped. It
    /// is returned to the authenticated, tenant-scoped caller, and it is never logged.
    /// </summary>
    public NosisData? Nosis { get; set; }

    // ── Scoring engine fields (MVP 2) ─────────────────────────────────────────

    /// <summary>
    /// Composite risk score (0–100) computed by the deterministic scoring engine.
    /// Higher values indicate higher credit risk.
    /// Null for analyses created before the scoring engine was introduced.
    /// </summary>
    public decimal? AiSuggestedScore { get; set; }

    /// <summary>
    /// Risk level label: Low / Medium / High / Critical.
    /// Derived from <see cref="AiSuggestedScore"/> using per-tenant thresholds.
    /// Null for pre-scoring analyses.
    /// </summary>
    public string? AiSuggestedLevel { get; set; }

    /// <summary>
    /// Professional narrative in Spanish justifying the scoring result,
    /// generated by Claude based on the deterministic engine output.
    /// </summary>
    public string? ScoringNarrative { get; set; }

    /// <summary>
    /// Per-component and per-ratio score breakdown for UI display.
    /// Contains keys: scoreRatios, scoreBcra, scoreMacro, breakdown (per-ratio scores 0–100),
    /// scoreBeforeFloor, appliedFloor, criticalRatios, bcraTrendDelta, bcraTrendAdj, chequesAdj,
    /// chequesProporcionRecuperada (both since scoring algorithm version 5), ventasAdj,
    /// ventasVariacion, ventasBase (all three since scoring algorithm version 6),
    /// ventasVariacionEsReal (since version 7 — absent means ventasVariacion is nominal),
    /// casfogMoraAdj, casfogProporcionMora (both since scoring algorithm version 8),
    /// criticalFloorTrigger, criticalFloors, thresholdFallbacks (all three since version 9, which also
    /// adds the ventasBase value "interanual_3m"),
    /// profitabilityFloorWaived, profitabilityFloorWaivedRatios, operatingResultSign (all three since
    /// version 10, which also adds the per-ratio exclusionReason value "no_current_liabilities" on a
    /// NON-excluded liquidez corriente / prueba ácida entry: scored at the best band, value null),
    /// acidTestFloorWaived, currentRatioVsWarn (both since version 11),
    /// sectorProfile.
    /// Null for pre-scoring analyses.
    /// </summary>
    public object? ScoreBreakdown { get; set; }

    // ── Provenance (detail endpoint only) ─────────────────────────────────────

    /// <summary>
    /// The cupo(s) this analysis originated (reverse of cupo → analysis). DETAIL-ONLY:
    /// populated exclusively by GET /api/analyses/{id}, where it is ALWAYS a (possibly empty)
    /// array — never null. On the list endpoint (which shares this DTO) it is ALWAYS null and
    /// must NOT be populated there, as that would be an N+1 across every analysis in the tenant.
    /// Includes cupos of EVERY status, including terminal 'Superseded': this is provenance, not a
    /// liveness check. This deliberately DIFFERS from
    /// <see cref="Api.Web.FinanceIA.Data.Repositories.ICreditLineRepository.GetIdsByOriginAnalysisAsync"/>,
    /// which is non-terminal-only for the reject-analysis guard — do NOT unify the two.
    /// </summary>
    public IReadOnlyList<OriginatedCreditLineResponse>? OriginatedCreditLines { get; set; }

    /// <summary>
    /// Deterministic es-AR reading of the cash-flow statement ("LECTURA DEL FLUJO"), one finished
    /// sentence per element. DETAIL-ONLY: populated exclusively by GET /api/analyses/{id}. On the
    /// list endpoint (which shares this DTO) it is ALWAYS null and must NOT be populated there, as
    /// that would cost a BalanceJson parse per row for data the list never shows.
    ///
    /// Computed at READ time from the persisted cash-flow figures and NEVER persisted — so it covers
    /// analyses predating the feature and any wording fix applies retroactively. It is CONTEXT for the
    /// analyst and never a scoring input; the risk score does not reflect these sentences. Null when
    /// the analysis has no cash-flow statement at all; a (possibly empty) array otherwise.
    /// </summary>
    public IReadOnlyList<string>? CashFlowNarrative { get; set; }

    /// <summary>
    /// The deterministic integrity verdict on the extracted balance — whether its totals add up,
    /// which checks failed, which ratios they feed, and whether a second extraction pass ran and
    /// which was kept. Read at response time from <see cref="BalanceJson"/>'s <c>integrity</c> node;
    /// null for analyses extracted before prompt 2.18, and always null on the list endpoint (no
    /// BalanceJson parse per row). Additive: a client that does not know it ignores it. When
    /// <c>passed</c> is false the score was still computed from these figures — the Portal should
    /// warn the analyst rather than present the ratios at face value.
    /// </summary>
    public ExtractionIntegrityResponse? ExtractionIntegrity { get; set; }

    /// <summary>
    /// Phase 10-F3a: that figures of the extracted balance were corrected by hand after the reading — how
    /// many, when and by whom last, and an es-AR note that <see cref="Anomalies"/> are the ORIGINAL
    /// reading's. Read at response time from <c>BalanceJson.manual_corrections</c>; null when nothing was
    /// corrected. Detail only: the list endpoint never sets it.
    /// </summary>
    public BalanceCorrectionsResponse? BalanceCorrections { get; set; }

    /// <summary>
    /// "Confiabilidad del score" (score credibility 1b-2b): how far <see cref="AiSuggestedScore"/> can be
    /// trusted — <c>complete</c> | <c>with_caveats</c> | <c>provisional</c> | <c>none</c>, with the es-AR
    /// reasons. Derived at response time from <c>ScoreBreakdownJson.reliabilityFacts</c> and the row's own
    /// scalars, against the score's date (<c>RescoredAt ?? GeneratedAt</c>), by the same adapter the list and
    /// the dashboard use, so the three never disagree. Set on the detail and on every decision, trigger and
    /// re-score response; null on a persona humana analysis. Reporting only: it moves no score. Additive.
    /// </summary>
    public ScoreReliabilityResponse? ScoreReliability { get; set; }

    /// <summary>
    /// What a decision on this analysis requires (score credibility 1b-5): <c>acknowledgeCaveats</c> when
    /// <see cref="ScoreReliability"/> is not <c>complete</c>, <c>figuresReviewNote</c> when it raises
    /// <c>integrity_failed</c>. From the same verdict, on the same responses. Null on a persona humana analysis.
    /// Additive: a client that does not find it requires nothing.
    /// </summary>
    public DecisionRequirementsResponse? DecisionRequirements { get; set; }

    // ── Team approval quorum (DDL 065 + 067) ──────────────────────────────────
    // All three are null unless a quorum is IN FORCE on this analysis: it is 'PreApproved' AND the
    // pre-approving analyst's team has at least one manager who is active and able to confirm. Null
    // is the ordinary case and means "one signature settles this", which is what every analysis did
    // before the quorum engine shipped — a client must read it as normal, not as missing data.
    //
    // Raw facts, following the pre-approval record above: there is no "canIWithdraw" or
    // "hasCurrentUserApproved" boolean, because whether the caller has already signed is a question
    // they answer from their own id against ApprovedByUserIds.
    //
    // Populated by PATCH /{id}/decision, DELETE /{id}/decision/vote and GET /{id}. Deliberately NOT
    // by the list endpoint, which shares this DTO: it would be an N+1 across the tenant, the same
    // argument OriginatedCreditLines makes above.

    /// <summary>
    /// Signatures this pre-approval needs — the team's quorum capped by how many eligible managers
    /// it actually has, and never below 1. The cap is why a quorum of 3 cannot strand a team of 2.
    /// </summary>
    public int? ApprovalQuorum { get; set; }

    /// <summary>
    /// How many of them are on record and still counting. The Portal's
    /// "aprobaron {ApprovalCount} de {ApprovalQuorum}".
    /// </summary>
    public int? ApprovalCount { get; set; }

    /// <summary>
    /// UserIds of the deciders whose approval counts, as strings — the same shape as
    /// <see cref="ReviewedBy"/> and <see cref="PreApprovedBy"/>. A signature from someone since
    /// deactivated or moved off a deciding role stays in the database but leaves this list, which is
    /// why it can be shorter than the number of votes actually cast.
    /// </summary>
    public IReadOnlyList<string>? ApprovedByUserIds { get; set; }

    // ── Re-score (DDL 073) ────────────────────────────────────────────────────
    // DETAIL-ONLY, like the provenance above: populated by GET /api/analyses/{id} and by
    // POST /api/analyses/{id}/rescore, which answers with the detail. Null on every other endpoint
    // that shares this DTO (the list, the trigger, the decision) — a client reads null there as
    // "not computed here", never as "cannot be re-scored".

    /// <summary>UTC instant of the latest re-score; null when the result was never re-scored.</summary>
    public DateTime? RescoredAt { get; set; }

    /// <summary>UserId of the analyst who ran the latest re-score, as a string like <see cref="ReviewedBy"/>.</summary>
    public string? RescoredBy { get; set; }

    /// <summary>Display name of <see cref="RescoredBy"/> ("Usuario" when the id no longer resolves).</summary>
    public string? RescoredByName { get; set; }

    /// <summary>How many times this result was re-scored (0 when never).</summary>
    public int? RescoreCount { get; set; }

    /// <summary>
    /// Whether POST /api/analyses/{id}/rescore would be accepted now, by the endpoint's own rules minus
    /// the cooldown (a 429 there carries its own Retry-After). The analyst's ROLE is not part of it —
    /// the Portal gates the button by role, as it does for a new analysis.
    /// </summary>
    public bool? CanRescore { get; set; }

    /// <summary>
    /// The errorCode the endpoint would answer with when <see cref="CanRescore"/> is false:
    /// person_analysis_not_rescorable | analysis_result_missing | analysis_not_pending |
    /// analysis_has_votes | analysis_processing. Null when it can be re-scored, or when the check
    /// itself could not be completed (CanRescore is then false as well).
    /// </summary>
    public string? RescoreBlockedReason { get; set; }

    /// <summary>
    /// The dates of the sources the CURRENT score was computed from: <c>[{ source, asOf, live }]</c> for
    /// BCRA, LUFE, CASFOG, ARCA and NOSIS. Exact for a re-scored result (read from its latest history
    /// row). For one never re-scored, the BCRA is the generation time (queried live) and a snapshot's
    /// date is the entity's refresh date when that is not later than the generation time — otherwise
    /// null, because the entity has been refreshed since and the copy's own date is no longer known.
    /// </summary>
    public IReadOnlyList<RescoreSource>? Sources { get; set; }

    /// <summary>
    /// Set ONLY on the POST /api/analyses/{id}/rescore response: the score before and after and the
    /// sources the new one used. Omitted from the JSON everywhere else.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public AnalysisRescoreSummary? Rescore { get; set; }
}
