// Project : Api.Web.FinanceIA
// Created : 2026-10-23

namespace Api.Web.FinanceIA.Models.Financia;

// The wire contract of the role dashboards — my-work, approvals, portfolio, oversight, operations — kept in ONE file
// on purpose (the MfaBackupCodeContracts precedent): the Portal consumes these shapes as a unit and
// Swagger is Development-only, so a reviewer must be able to read the whole contract in one place.
//
// Field values are the STORED CODES — statuses, risk levels, enrichment outcomes, suggestions, acts.
// Spanish copy is the Portal's job. A section that could not be computed is null and named in
// DegradedSections; a count is null exactly when the section it counts is.
//
// MONEY IS NEVER SUMMED ACROSS CURRENCIES. Every amount below sits under a `currency` key; no record
// carries a cross-currency total.

/// <summary>A capped list plus the size of the set it was cut from.</summary>
/// <typeparam name="TItem">The item shape.</typeparam>
public sealed record DashboardPage<TItem>
{
    /// <summary>How many items the whole set holds, before the cap.</summary>
    public required int TotalCount { get; init; }

    /// <summary>The first items of the set, in the section's documented order, at most the cap.</summary>
    public required IReadOnlyList<TItem> Items { get; init; }
}

/// <summary>The acts <see cref="DecidedByMeItem.Act"/> reports, as stable codes.</summary>
public static class DashboardActs
{
    /// <summary>The caller cast an approval vote on a pre-approved analysis (it may still be waiting).</summary>
    public const string ApprovalVote = "ApprovalVote";

    /// <summary>The caller confirmed a pre-approval — their signature settled it.</summary>
    public const string Confirmed = "Confirmed";

    /// <summary>The caller approved an analysis that was never pre-approved, in one step.</summary>
    public const string Approved = "Approved";

    /// <summary>The caller rejected the analysis (a veto, when it was pre-approved).</summary>
    public const string Rejected = "Rejected";

    /// <summary>The caller returned a pre-approval to review for rework.</summary>
    public const string Returned = "Returned";
}

/// <summary>Section names as they appear in <c>degradedSections</c>.</summary>
public static class DashboardSections
{
    /// <summary>my-work: the caller's analyses waiting for their first decision.</summary>
    public const string Pending = "pending";

    /// <summary>my-work: the caller's analyses a confirmer returned for rework.</summary>
    public const string Returned = "returned";

    /// <summary>my-work: the count of pending analyses whose BCRA enrichment degraded and can be re-scored.</summary>
    public const string BcraDegraded = "bcraDegraded";

    /// <summary>my-work: the caller's pre-approvals still waiting for signatures.</summary>
    public const string AwaitingSignature = "awaitingSignature";

    /// <summary>my-work: the caller's latest analyses in any status.</summary>
    public const string Recent = "recent";

    /// <summary>approvals: the pre-approvals the caller can still sign.</summary>
    public const string WaitingForMe = "waitingForMe";

    /// <summary>approvals: what the caller decided in the last 30 days.</summary>
    public const string DecidedByMe = "decidedByMe";

    /// <summary>portfolio: otorgado, utilizado and disponible of the Active cupos, plus the Suspended ones.</summary>
    public const string Totals = "totals";

    /// <summary>portfolio: otorgado and utilizado by the risk bucket of each cupo's analysis.</summary>
    public const string ByRisk = "byRisk";

    /// <summary>portfolio: the entities with the most utilizado.</summary>
    public const string TopExposures = "topExposures";

    /// <summary>portfolio: otorgado and utilizado by entity sector.</summary>
    public const string BySector = "bySector";

    /// <summary>portfolio: Vigente operations falling due, and Vencida ones still occupying cupo.</summary>
    public const string Maturities = "maturities";

    /// <summary>portfolio: entities whose current BCRA situación is worse than at their cupo's analysis.</summary>
    public const string BcraSignals = "bcraSignals";

    /// <summary>portfolio: Active cupos at 90% utilization or more.</summary>
    public const string HighUtilization = "highUtilization";

    /// <summary>portfolio: Active cupos by the CURRENT BCRA situación of their entity, per currency.</summary>
    public const string BcraCurrent = "bcraCurrent";

    /// <summary>portfolio: PyMEs with an Active cupo whose annual review is due or coming up.</summary>
    public const string AnnualReview = "annualReview";

    /// <summary>portfolio: Fondo de Riesgo usage of an SGR tenant (creditline.mode "sgr" only).</summary>
    public const string Sgr = "sgr";

    /// <summary>oversight: every PendingReview analysis of the tenant.</summary>
    public const string TeamQueue = "teamQueue";

    /// <summary>oversight: approvals against the system's suggestion in the last 90 days.</summary>
    public const string AgainstSuggestion = "againstSuggestion";

    /// <summary>oversight: the tenant's latest decisions.</summary>
    public const string RecentDecisions = "recentDecisions";

    /// <summary>oversight: the tenant's latest re-scores.</summary>
    public const string RecentRescores = "recentRescores";

    /// <summary>oversight: throughput of the current and the previous Argentine month.</summary>
    public const string MonthStats = "monthStats";

    /// <summary>operations: ApiCallLogs of the last 24 hours, per service.</summary>
    public const string Integrations = "integrations";

    /// <summary>operations: how old each source snapshot is, over the entities with an Active cupo.</summary>
    public const string Freshness = "freshness";
}

/// <summary>The buckets <see cref="PortfolioRiskBucket.Bucket"/> reports, as stable codes.</summary>
public static class DashboardRiskBuckets
{
    /// <summary>AiSuggestedLevel Critical.</summary>
    public const string Critical = "Critical";

    /// <summary>AiSuggestedLevel High.</summary>
    public const string High = "High";

    /// <summary>AiSuggestedLevel Medium.</summary>
    public const string Medium = "Medium";

    /// <summary>AiSuggestedLevel Low.</summary>
    public const string Low = "Low";

    /// <summary>The cupo's analysis is a persona humana analysis, which carries no score.</summary>
    public const string PersonaHumana = "PersonaHumana";

    /// <summary>No analysis on the cupo, an analysis with no result, or a result with no level.</summary>
    public const string Unclassified = "Unclassified";

    /// <summary>Every bucket, highest risk first — the order of <see cref="PortfolioRiskByCurrency.Buckets"/>.</summary>
    public static readonly IReadOnlyList<string> Ordered = [Critical, High, Medium, Low, PersonaHumana, Unclassified];
}

/// <summary>Why an item is in <see cref="OversightResponse.AgainstSuggestion"/>, as stable codes.</summary>
public static class DashboardAgainstSuggestionReasons
{
    /// <summary>A balance analysis approved with AiSuggestedLevel High or Critical.</summary>
    public const string AiLevel = "AiLevel";

    /// <summary>A persona humana analysis approved with personSuggestion RechazoSugerido.</summary>
    public const string PersonSuggestion = "PersonSuggestion";
}

// ── GET /api/dashboard/my-work ────────────────────────────────────────────────────

/// <summary>Response of GET /api/dashboard/my-work.</summary>
public sealed record MyWorkResponse
{
    /// <summary>When this response was computed (UTC).</summary>
    public required DateTime GeneratedAt { get; init; }

    /// <summary>The sections that failed and are null below. Empty when everything was computed.</summary>
    public required IReadOnlyList<string> DegradedSections { get; init; }

    /// <summary>The badge numbers.</summary>
    public required MyWorkCounts Counts { get; init; }

    /// <summary>
    /// The caller's PendingReview analyses with no decision on record (RequestedBy = caller, ReviewedBy
    /// null). Risk level descending — Critical, High, Medium, Low, then none — then oldest first. Cap 20.
    /// </summary>
    public required DashboardPage<MyWorkPendingItem>? Pending { get; init; }

    /// <summary>
    /// PendingReview analyses a confirmer returned (ReviewedBy set) that the caller requested or
    /// pre-approved. Newest return first. Cap 20.
    /// </summary>
    public required DashboardPage<MyWorkReturnedItem>? Returned { get; init; }

    /// <summary>The caller's PreApproved analyses (PreApprovedBy = caller). Oldest pre-approval first. Cap 20.</summary>
    public required DashboardPage<MyWorkAwaitingSignatureItem>? AwaitingSignature { get; init; }

    /// <summary>The caller's last 8 analyses in any status, newest first.</summary>
    public required IReadOnlyList<MyWorkRecentItem>? Recent { get; init; }
}

/// <summary>The my-work badge numbers. Each is null when its section is degraded.</summary>
public sealed record MyWorkCounts
{
    /// <summary>Total of <see cref="MyWorkResponse.Pending"/>.</summary>
    public required int? Pending { get; init; }

    /// <summary>Total of <see cref="MyWorkResponse.Returned"/>.</summary>
    public required int? Returned { get; init; }

    /// <summary>
    /// How many of the caller's pending analyses have <c>EnrichmentStatus = 'Degraded'</c> AND may be
    /// re-scored now — the same rule as each pending item's <c>canRescore</c>.
    /// </summary>
    public required int? BcraDegraded { get; init; }

    /// <summary>Total of <see cref="MyWorkResponse.AwaitingSignature"/>.</summary>
    public required int? AwaitingSignature { get; init; }
}

/// <summary>One of the caller's analyses waiting for a decision.</summary>
public sealed record MyWorkPendingItem
{
    /// <summary>AnalysisResults.ResultId — what the Portal links to (GET /api/analyses/{resultId}).</summary>
    public required Guid ResultId { get; init; }

    /// <summary>Analyses.AnalysisId.</summary>
    public required Guid AnalysisId { get; init; }

    /// <summary><c>Balance</c> or <c>PersonaHumana</c>, as stored.</summary>
    public required string AnalysisKind { get; init; }

    /// <summary>The analysed entity.</summary>
    public required Guid EntityId { get; init; }

    /// <summary>The entity's business name; null when the entity no longer resolves in this tenant.</summary>
    public required string? EntityName { get; init; }

    /// <summary>The entity's CUIT; null when the entity no longer resolves in this tenant.</summary>
    public required string? EntityCuit { get; init; }

    /// <summary>Analyses.RequestedAmount.</summary>
    public required decimal? RequestedAmount { get; init; }

    /// <summary>AnalysisResults.AiSuggestedLevel: Low, Medium, High, Critical or null.</summary>
    public required string? AiSuggestedLevel { get; init; }

    /// <summary>AnalysisResults.AiSuggestedScore.</summary>
    public required decimal? AiSuggestedScore { get; init; }

    /// <summary>AnalysisResults.GeneratedAt (UTC).</summary>
    public required DateTime GeneratedAt { get; init; }

    /// <summary>Whole days since <see cref="GeneratedAt"/>, floored.</summary>
    public required int AgeDays { get; init; }

    /// <summary>AnalysisResults.EnrichmentStatus: Complete, NotApplicable or Degraded.</summary>
    public required string EnrichmentStatus { get; init; }

    /// <summary>Whether POST /api/analyses/{resultId}/rescore would accept it — the detail's canRescore.</summary>
    public required bool CanRescore { get; init; }

    /// <summary>
    /// "Confiabilidad del score" (score credibility 1b-2b): the verdict GET /api/analyses/{id} carries for
    /// this row, from the same adapter and columns. Null on a persona humana analysis.
    /// </summary>
    public ScoreReliabilityResponse? ScoreReliability { get; init; }
}

/// <summary>One of the caller's analyses a confirmer returned for rework.</summary>
public sealed record MyWorkReturnedItem
{
    /// <summary>AnalysisResults.ResultId.</summary>
    public required Guid ResultId { get; init; }

    /// <summary>Analyses.AnalysisId.</summary>
    public required Guid AnalysisId { get; init; }

    /// <summary><c>Balance</c> or <c>PersonaHumana</c>.</summary>
    public required string AnalysisKind { get; init; }

    /// <summary>The analysed entity.</summary>
    public required Guid EntityId { get; init; }

    /// <summary>The entity's business name.</summary>
    public required string? EntityName { get; init; }

    /// <summary>The entity's CUIT.</summary>
    public required string? EntityCuit { get; init; }

    /// <summary>Analyses.RequestedAmount.</summary>
    public required decimal? RequestedAmount { get; init; }

    /// <summary>AnalysisResults.AiSuggestedLevel.</summary>
    public required string? AiSuggestedLevel { get; init; }

    /// <summary>AnalysisResults.AiSuggestedScore.</summary>
    public required decimal? AiSuggestedScore { get; init; }

    /// <summary>AnalysisResults.GeneratedAt (UTC).</summary>
    public required DateTime GeneratedAt { get; init; }

    /// <summary>Whole days since <see cref="GeneratedAt"/>, floored.</summary>
    public required int AgeDays { get; init; }

    /// <summary>AnalysisResults.EnrichmentStatus.</summary>
    public required string EnrichmentStatus { get; init; }

    /// <summary>Who returned it; "Usuario" when that user no longer resolves.</summary>
    public required string? ReviewedByName { get; init; }

    /// <summary>When it was returned (UTC).</summary>
    public required DateTime? ReviewedAt { get; init; }

    /// <summary>The confirmer's notes, as stored.</summary>
    public required string? ReviewNotes { get; init; }

    /// <summary>
    /// "Confiabilidad del score" (score credibility 1b-2b): the verdict GET /api/analyses/{id} carries for
    /// this row, from the same adapter and columns. Null on a persona humana analysis.
    /// </summary>
    public ScoreReliabilityResponse? ScoreReliability { get; init; }
}

/// <summary>One of the caller's pre-approvals waiting for signatures.</summary>
public sealed record MyWorkAwaitingSignatureItem
{
    /// <summary>AnalysisResults.ResultId.</summary>
    public required Guid ResultId { get; init; }

    /// <summary>Analyses.AnalysisId.</summary>
    public required Guid AnalysisId { get; init; }

    /// <summary><c>Balance</c> or <c>PersonaHumana</c>.</summary>
    public required string AnalysisKind { get; init; }

    /// <summary>The analysed entity.</summary>
    public required Guid EntityId { get; init; }

    /// <summary>The entity's business name.</summary>
    public required string? EntityName { get; init; }

    /// <summary>The entity's CUIT.</summary>
    public required string? EntityCuit { get; init; }

    /// <summary>AnalysisResults.AiSuggestedLevel.</summary>
    public required string? AiSuggestedLevel { get; init; }

    /// <summary>AnalysisResults.AiSuggestedScore.</summary>
    public required decimal? AiSuggestedScore { get; init; }

    /// <summary>When the caller pre-approved it (UTC).</summary>
    public required DateTime? PreApprovedAt { get; init; }

    /// <summary>The amount pre-approved.</summary>
    public required decimal? PreApprovedAmount { get; init; }

    /// <summary>The currency pre-approved (ISO code).</summary>
    public required string? PreApprovedCurrency { get; init; }

    /// <summary>
    /// Counting approvals so far. Null when no quorum is in force — exactly when GET /api/analyses/{id}
    /// omits <c>approvalCount</c>.
    /// </summary>
    public required int? ApprovalCount { get; init; }

    /// <summary>The effective quorum; null when no quorum is in force, as on the detail.</summary>
    public required int? ApprovalQuorum { get; init; }

    /// <summary>
    /// "Confiabilidad del score" (score credibility 1b-2b): the verdict GET /api/analyses/{id} carries for
    /// this row, from the same adapter and columns. Null on a persona humana analysis.
    /// </summary>
    public ScoreReliabilityResponse? ScoreReliability { get; init; }
}

/// <summary>One of the caller's latest analyses.</summary>
public sealed record MyWorkRecentItem
{
    /// <summary>AnalysisResults.ResultId.</summary>
    public required Guid ResultId { get; init; }

    /// <summary>Analyses.AnalysisId.</summary>
    public required Guid AnalysisId { get; init; }

    /// <summary><c>Balance</c> or <c>PersonaHumana</c>.</summary>
    public required string AnalysisKind { get; init; }

    /// <summary>The analysed entity.</summary>
    public required Guid EntityId { get; init; }

    /// <summary>The entity's business name.</summary>
    public required string? EntityName { get; init; }

    /// <summary>The entity's CUIT.</summary>
    public required string? EntityCuit { get; init; }

    /// <summary>Analyses.Status: PendingReview, PreApproved, Approved or Rejected.</summary>
    public required string Status { get; init; }

    /// <summary>AnalysisResults.AiSuggestedLevel.</summary>
    public required string? AiSuggestedLevel { get; init; }

    /// <summary>AnalysisResults.AiSuggestedScore.</summary>
    public required decimal? AiSuggestedScore { get; init; }

    /// <summary>When the analysis was created (UTC) — the order of this list.</summary>
    public required DateTime CreatedAt { get; init; }

    /// <summary>AnalysisResults.GeneratedAt (UTC).</summary>
    public required DateTime GeneratedAt { get; init; }

    /// <summary>
    /// "Confiabilidad del score" (score credibility 1b-2b): the verdict GET /api/analyses/{id} carries for
    /// this row, from the same adapter and columns. Null on a persona humana analysis.
    /// </summary>
    public ScoreReliabilityResponse? ScoreReliability { get; init; }
}

// ── GET /api/dashboard/approvals ──────────────────────────────────────────────────

/// <summary>Response of GET /api/dashboard/approvals.</summary>
public sealed record ApprovalsResponse
{
    /// <summary>When this response was computed (UTC).</summary>
    public required DateTime GeneratedAt { get; init; }

    /// <summary>The sections that failed and are null below. Empty when everything was computed.</summary>
    public required IReadOnlyList<string> DegradedSections { get; init; }

    /// <summary>The badge numbers.</summary>
    public required ApprovalsCounts Counts { get; init; }

    /// <summary>
    /// Every PreApproved analysis of the tenant the caller can still sign: no Approved vote of theirs on
    /// record, and none the decision endpoint would refuse them as a self-confirmation. Not filtered by
    /// team — any confirmer's vote counts. Oldest pre-approval first. Cap 50.
    /// </summary>
    public required DashboardPage<WaitingForMeItem>? WaitingForMe { get; init; }

    /// <summary>The caller's last 10 acts in the last 30 days, newest first.</summary>
    public required IReadOnlyList<DecidedByMeItem>? DecidedByMe { get; init; }
}

/// <summary>The approvals badge numbers. Each is null when waitingForMe is degraded.</summary>
public sealed record ApprovalsCounts
{
    /// <summary>Total of <see cref="ApprovalsResponse.WaitingForMe"/>.</summary>
    public required int? WaitingForMe { get; init; }

    /// <summary>How many of the whole waitingForMe set carry <c>personSuggestion = RechazoSugerido</c>.</summary>
    public required int? SuggestedRejections { get; init; }
}

/// <summary>One pre-approval the caller can sign.</summary>
public sealed record WaitingForMeItem
{
    /// <summary>AnalysisResults.ResultId.</summary>
    public required Guid ResultId { get; init; }

    /// <summary>Analyses.AnalysisId.</summary>
    public required Guid AnalysisId { get; init; }

    /// <summary><c>Balance</c> or <c>PersonaHumana</c>.</summary>
    public required string AnalysisKind { get; init; }

    /// <summary>The analysed entity.</summary>
    public required Guid EntityId { get; init; }

    /// <summary>The entity's business name.</summary>
    public required string? EntityName { get; init; }

    /// <summary>The entity's CUIT.</summary>
    public required string? EntityCuit { get; init; }

    /// <summary>The amount pre-approved.</summary>
    public required decimal? PreApprovedAmount { get; init; }

    /// <summary>The currency pre-approved (ISO code).</summary>
    public required string? PreApprovedCurrency { get; init; }

    /// <summary>Who pre-approved it (Users.UserId).</summary>
    public required Guid? PreApprovedBy { get; init; }

    /// <summary>Their display name; "Usuario" when that user no longer resolves.</summary>
    public required string? PreApprovedByName { get; init; }

    /// <summary>When it was pre-approved (UTC).</summary>
    public required DateTime? PreApprovedAt { get; init; }

    /// <summary>Whole days since <see cref="PreApprovedAt"/>, floored; 0 when it is unknown.</summary>
    public required int AgeDays { get; init; }

    /// <summary>AnalysisResults.AiSuggestedLevel.</summary>
    public required string? AiSuggestedLevel { get; init; }

    /// <summary>AnalysisResults.AiSuggestedScore.</summary>
    public required decimal? AiSuggestedScore { get; init; }

    /// <summary>
    /// The persona humana suggestion the detail shows as <c>personEvaluation.suggestion</c>:
    /// <c>RechazoSugerido</c> or <c>ParaDecisionDelAnalista</c>. Null for a balance analysis, and for a
    /// persona analysis with no solicitante on record (where the detail's personEvaluation is null too).
    /// </summary>
    public required string? PersonSuggestion { get; init; }

    /// <summary>Counting approvals so far; null when no quorum is in force, as on the detail.</summary>
    public required int? ApprovalCount { get; init; }

    /// <summary>The effective quorum; null when no quorum is in force, as on the detail.</summary>
    public required int? ApprovalQuorum { get; init; }

    /// <summary>
    /// Whether the caller is a named manager (TeamManagers or the legacy Teams.ManagerUserId) of the
    /// pre-approver's team. Informational: the list is never filtered by it.
    /// </summary>
    public required bool InMyTeam { get; init; }

    /// <summary>
    /// "Confiabilidad del score" (score credibility 1b-2b): the verdict GET /api/analyses/{id} carries for
    /// this row, from the same adapter and columns. Null on a persona humana analysis.
    /// </summary>
    public ScoreReliabilityResponse? ScoreReliability { get; init; }
}

/// <summary>One act of the caller's in the last 30 days.</summary>
public sealed record DecidedByMeItem
{
    /// <summary>AnalysisResults.ResultId.</summary>
    public required Guid ResultId { get; init; }

    /// <summary>Analyses.AnalysisId.</summary>
    public required Guid AnalysisId { get; init; }

    /// <summary><c>Balance</c> or <c>PersonaHumana</c>.</summary>
    public required string AnalysisKind { get; init; }

    /// <summary>The analysed entity.</summary>
    public required Guid EntityId { get; init; }

    /// <summary>The entity's business name.</summary>
    public required string? EntityName { get; init; }

    /// <summary>The entity's CUIT.</summary>
    public required string? EntityCuit { get; init; }

    /// <summary>One of <see cref="DashboardActs"/>.</summary>
    public required string Act { get; init; }

    /// <summary>When the act happened (UTC): the vote's VotedAt, or Analyses.ReviewedAt.</summary>
    public required DateTime ActedAt { get; init; }

    /// <summary>The analysis' status NOW, which may have moved on since the act.</summary>
    public required string Status { get; init; }
}

// ── GET /api/dashboard/portfolio ──────────────────────────────────────────────────

/// <summary>Response of GET /api/dashboard/portfolio. Every money figure is per currency.</summary>
public sealed record PortfolioResponse
{
    /// <summary>When this response was computed (UTC).</summary>
    public required DateTime GeneratedAt { get; init; }

    /// <summary>The sections that failed and are null below. Empty when everything was computed.</summary>
    public required IReadOnlyList<string> DegradedSections { get; init; }

    /// <summary>One row per currency that has an Active or a Suspended cupo, ordered by currency code.</summary>
    public required IReadOnlyList<PortfolioCurrencyTotals>? Totals { get; init; }

    /// <summary>Active cupos by the risk bucket of their analysis, per currency.</summary>
    public required IReadOnlyList<PortfolioRiskByCurrency>? ByRisk { get; init; }

    /// <summary>The 10 Active cupos with the most utilizado, per currency.</summary>
    public required IReadOnlyList<PortfolioTopExposures>? TopExposures { get; init; }

    /// <summary>Active cupos by entity sector, per currency.</summary>
    public required IReadOnlyList<PortfolioSectorByCurrency>? BySector { get; init; }

    /// <summary>Operations of Active cupos falling due, and the Vencida ones still occupying cupo.</summary>
    public required PortfolioMaturities? Maturities { get; init; }

    /// <summary>
    /// Entities with an Active cupo whose current BCRA situación is worse than at the cupo's analysis.
    /// Largest worsening first, then the most recently queried. Cap 20.
    /// </summary>
    public required DashboardPage<PortfolioBcraSignalItem>? BcraSignals { get; init; }

    /// <summary>
    /// Active cupos whose utilizado is at least 90% of their Amount, per currency: one row per currency
    /// with at least one, ordered by currency code. Informational — not an alert.
    /// </summary>
    public required IReadOnlyList<PortfolioHighUtilization>? HighUtilization { get; init; }

    /// <summary>
    /// Active cupos by the CURRENT BCRA situación of their entity, per currency: one row per currency with
    /// an Active cupo, ordered by currency code.
    /// </summary>
    public required IReadOnlyList<PortfolioBcraCurrent>? BcraCurrent { get; init; }

    /// <summary>PyMEs with an Active cupo whose annual review is due or coming up — the PYME_REVIEW_DUE rule.</summary>
    public required PortfolioAnnualReview? AnnualReview { get; init; }

    /// <summary>
    /// Fondo de Riesgo usage. Null — and NOT listed in <see cref="DegradedSections"/> — when the tenant's
    /// creditline.mode is not "sgr".
    /// </summary>
    public required PortfolioSgr? Sgr { get; init; }
}

/// <summary>A count and an amount in one currency.</summary>
public sealed record DashboardCountAmount
{
    /// <summary>How many.</summary>
    public required int Count { get; init; }

    /// <summary>Their total, in the enclosing currency.</summary>
    public required decimal Amount { get; init; }
}

/// <summary>The portfolio totals of one currency.</summary>
public sealed record PortfolioCurrencyTotals
{
    /// <summary>ISO 4217 code of every figure in this row.</summary>
    public required string Currency { get; init; }

    /// <summary>Sum of Amount over the Active cupos.</summary>
    public required decimal Otorgado { get; init; }

    /// <summary>
    /// Sum of Amount over the operations of the Active cupos that occupy cupo — every status except
    /// Cancelada, the rule GET /api/entities/{id}/credit-lines applies.
    /// </summary>
    public required decimal Utilizado { get; init; }

    /// <summary><see cref="Otorgado"/> minus <see cref="Utilizado"/>; negative when the cupos are over-used.</summary>
    public required decimal Disponible { get; init; }

    /// <summary>How many Active cupos.</summary>
    public required int LineCount { get; init; }

    /// <summary>How many distinct entities hold them.</summary>
    public required int EntityCount { get; init; }

    /// <summary>The Suspended cupos of this currency: how many, and their Amount. Not included above.</summary>
    public required DashboardCountAmount Suspended { get; init; }
}

/// <summary>The Active cupos of one currency, by risk bucket.</summary>
public sealed record PortfolioRiskByCurrency
{
    /// <summary>ISO 4217 code of every figure in this row.</summary>
    public required string Currency { get; init; }

    /// <summary>The buckets that hold at least one cupo, in <see cref="DashboardRiskBuckets.Ordered"/> order.</summary>
    public required IReadOnlyList<PortfolioRiskBucket> Buckets { get; init; }
}

/// <summary>One risk bucket of one currency.</summary>
public sealed record PortfolioRiskBucket
{
    /// <summary>One of <see cref="DashboardRiskBuckets"/>.</summary>
    public required string Bucket { get; init; }

    /// <summary>How many Active cupos.</summary>
    public required int LineCount { get; init; }

    /// <summary>Their Amount.</summary>
    public required decimal Otorgado { get; init; }

    /// <summary>Their operations that occupy cupo.</summary>
    public required decimal Utilizado { get; init; }
}

/// <summary>The top exposures of one currency.</summary>
public sealed record PortfolioTopExposures
{
    /// <summary>ISO 4217 code of every figure in this row.</summary>
    public required string Currency { get; init; }

    /// <summary>Utilizado descending, then EntityId. Cap 10.</summary>
    public required IReadOnlyList<PortfolioExposureItem> Items { get; init; }
}

/// <summary>One entity's Active cupo in one currency — at most one per entity and currency.</summary>
public sealed record PortfolioExposureItem
{
    /// <summary>The entity.</summary>
    public required Guid EntityId { get; init; }

    /// <summary>The entity's business name; null when it no longer resolves in this tenant.</summary>
    public required string? EntityName { get; init; }

    /// <summary>The entity's CUIT; null when it no longer resolves in this tenant.</summary>
    public required string? EntityCuit { get; init; }

    /// <summary>The cupo.</summary>
    public required Guid CreditLineId { get; init; }

    /// <summary>The cupo's Amount.</summary>
    public required decimal Otorgado { get; init; }

    /// <summary>Its operations that occupy cupo.</summary>
    public required decimal Utilizado { get; init; }

    /// <summary><see cref="Otorgado"/> minus <see cref="Utilizado"/>.</summary>
    public required decimal Disponible { get; init; }

    /// <summary>AiSuggestedLevel of the cupo's analysis result; null without one.</summary>
    public required string? AiSuggestedLevel { get; init; }

    /// <summary>One of <see cref="DashboardRiskBuckets"/>.</summary>
    public required string RiskBucket { get; init; }

    /// <summary>The cupo's analysis: LastRaiseAnalysisId, else OriginAnalysisId; null on a legacy cupo.</summary>
    public required Guid? AnalysisId { get; init; }

    /// <summary>That analysis' ResultId — what the Portal links to; null when it has no result.</summary>
    public required Guid? ResultId { get; init; }
}

/// <summary>The Active cupos of one currency, by entity sector.</summary>
public sealed record PortfolioSectorByCurrency
{
    /// <summary>ISO 4217 code of every figure in this row.</summary>
    public required string Currency { get; init; }

    /// <summary>Utilizado descending, then otorgado descending; the null-sector bucket last.</summary>
    public required IReadOnlyList<PortfolioSectorItem> Sectors { get; init; }
}

/// <summary>One sector of one currency.</summary>
public sealed record PortfolioSectorItem
{
    /// <summary>
    /// Entities.Sector, trimmed. Entities whose sector differs only in case or surrounding blanks are one
    /// bucket, shown with its most frequent spelling. NULL is the "Sin sector" bucket: no sector, or a
    /// blank one. The label is the Portal's.
    /// </summary>
    public required string? Sector { get; init; }

    /// <summary>How many Active cupos.</summary>
    public required int LineCount { get; init; }

    /// <summary>Their Amount.</summary>
    public required decimal Otorgado { get; init; }

    /// <summary>Their operations that occupy cupo.</summary>
    public required decimal Utilizado { get; init; }
}

/// <summary>Maturities of the operations on Active cupos.</summary>
public sealed record PortfolioMaturities
{
    /// <summary>
    /// Today in America/Argentina/Buenos_Aires — the date the day buckets count from. Whether an
    /// operation is Vencida is decided by the cupo screen's own rule, on the UTC date.
    /// </summary>
    public required DateOnly AsOfDate { get; init; }

    /// <summary>One row per currency with at least one figure, ordered by currency code.</summary>
    public required IReadOnlyList<PortfolioMaturitiesByCurrency> ByCurrency { get; init; }

    /// <summary>The next 10 Vigente operations to fall due, any currency, soonest first.</summary>
    public required IReadOnlyList<PortfolioUpcomingMaturity> Upcoming { get; init; }
}

/// <summary>The maturity buckets of one currency.</summary>
public sealed record PortfolioMaturitiesByCurrency
{
    /// <summary>ISO 4217 code of every figure in this row.</summary>
    public required string Currency { get; init; }

    /// <summary>Vigente, ValidUntil 0 to 30 days from <see cref="PortfolioMaturities.AsOfDate"/>, inclusive.</summary>
    public required DashboardCountAmount Days0To30 { get; init; }

    /// <summary>Vigente, ValidUntil 31 to 60 days away.</summary>
    public required DashboardCountAmount Days31To60 { get; init; }

    /// <summary>Vigente, ValidUntil 61 to 90 days away.</summary>
    public required DashboardCountAmount Days61To90 { get; init; }

    /// <summary>Effective status Vencida — they still occupy cupo until someone cancels them.</summary>
    public required DashboardCountAmount Vencidas { get; init; }
}

/// <summary>One Vigente operation falling due.</summary>
public sealed record PortfolioUpcomingMaturity
{
    /// <summary>The operation.</summary>
    public required Guid OperationId { get; init; }

    /// <summary>Its cupo.</summary>
    public required Guid CreditLineId { get; init; }

    /// <summary>The cupo's entity.</summary>
    public required Guid EntityId { get; init; }

    /// <summary>The entity's business name.</summary>
    public required string? EntityName { get; init; }

    /// <summary>The entity's CUIT.</summary>
    public required string? EntityCuit { get; init; }

    /// <summary>SGR One Click catalogue code.</summary>
    public required int Type { get; init; }

    /// <summary>ISO 4217 code of <see cref="Amount"/>.</summary>
    public required string Currency { get; init; }

    /// <summary>The operation's Amount.</summary>
    public required decimal Amount { get; init; }

    /// <summary>ValidUntil, as a date.</summary>
    public required DateOnly ValidUntil { get; init; }

    /// <summary>Days from <see cref="PortfolioMaturities.AsOfDate"/> to <see cref="ValidUntil"/>; never negative.</summary>
    public required int DaysUntil { get; init; }
}

/// <summary>One entity whose BCRA situación got worse since its cupo's analysis.</summary>
public sealed record PortfolioBcraSignalItem
{
    /// <summary>The entity.</summary>
    public required Guid EntityId { get; init; }

    /// <summary>The entity's business name.</summary>
    public required string? EntityName { get; init; }

    /// <summary>The entity's CUIT.</summary>
    public required string? EntityCuit { get; init; }

    /// <summary>
    /// The worst situación (1..6) frozen in the analysis' AnalysisResults.BcraDataJson, read exactly as the
    /// scoring engine reads it: Deuda.SituacionMaxima of the latest period, and 1 when no debt was reported.
    /// </summary>
    public required int SituacionThen { get; init; }

    /// <summary>The same reading of the entity's current Entities.BcraDataJson.</summary>
    public required int SituacionNow { get; init; }

    /// <summary>Entities.BcraDataUpdatedAt (UTC); null on rows written before it was stamped.</summary>
    public required DateTime? LastQueriedAt { get; init; }

    /// <summary>The analysis compared against — the most recent one behind the entity's Active cupos.</summary>
    public required Guid AnalysisId { get; init; }

    /// <summary>Its ResultId.</summary>
    public required Guid ResultId { get; init; }
}

/// <summary>The highly utilized cupos of one currency.</summary>
public sealed record PortfolioHighUtilization
{
    /// <summary>ISO 4217 code of every figure in this row.</summary>
    public required string Currency { get; init; }

    /// <summary>How many Active cupos of this currency are at 90% or more, before the cap.</summary>
    public required int TotalCount { get; init; }

    /// <summary>Utilization descending, then otorgado descending, then EntityId. Cap 10.</summary>
    public required IReadOnlyList<PortfolioHighUtilizationItem> Items { get; init; }
}

/// <summary>One Active cupo whose utilizado is at least 90% of its Amount.</summary>
public sealed record PortfolioHighUtilizationItem
{
    /// <summary>The entity.</summary>
    public required Guid EntityId { get; init; }

    /// <summary>The entity's business name; null when it no longer resolves in this tenant.</summary>
    public required string? EntityName { get; init; }

    /// <summary>The entity's CUIT; null when it no longer resolves in this tenant.</summary>
    public required string? EntityCuit { get; init; }

    /// <summary>The cupo.</summary>
    public required Guid CreditLineId { get; init; }

    /// <summary>The cupo's Amount, always greater than zero here.</summary>
    public required decimal Otorgado { get; init; }

    /// <summary>Its operations that occupy cupo — every status except Cancelada.</summary>
    public required decimal Utilizado { get; init; }

    /// <summary><see cref="Otorgado"/> minus <see cref="Utilizado"/>; zero or negative from 100%.</summary>
    public required decimal Disponible { get; init; }

    /// <summary>
    /// Utilizado as a percentage of Otorgado, rounded to one decimal (half away from zero). Membership is
    /// decided on the exact amounts, so this is never below 90.0; it exceeds 100 on an over-used cupo.
    /// </summary>
    public required decimal UtilizationPct { get; init; }

    /// <summary>One of <see cref="DashboardRiskBuckets"/>, as on <see cref="PortfolioExposureItem.RiskBucket"/>.</summary>
    public required string RiskBucket { get; init; }

    /// <summary>The cupo's analysis: LastRaiseAnalysisId, else OriginAnalysisId; null on a legacy cupo.</summary>
    public required Guid? AnalysisId { get; init; }

    /// <summary>That analysis' ResultId — what the Portal links to; null when it has no result.</summary>
    public required Guid? ResultId { get; init; }
}

/// <summary>The Active cupos of one currency, by the current BCRA situación of their entity.</summary>
public sealed record PortfolioBcraCurrent
{
    /// <summary>ISO 4217 code of every figure in this row.</summary>
    public required string Currency { get; init; }

    /// <summary>The situaciones that hold at least one entity: 1..6 ascending, then null.</summary>
    public required IReadOnlyList<PortfolioBcraBucket> Buckets { get; init; }

    /// <summary>Entities of this currency whose last BCRA query is more than 30 Argentine calendar days old.</summary>
    public required int StaleEntityCount { get; init; }

    /// <summary>Entities of this currency never queried at BCRA (no Entities.BcraDataUpdatedAt).</summary>
    public required int NeverQueriedCount { get; init; }

    /// <summary>The oldest Entities.BcraDataUpdatedAt (UTC) among them; null when none was ever queried.</summary>
    public required DateTime? OldestQueryAt { get; init; }
}

/// <summary>One current BCRA situación of one currency.</summary>
public sealed record PortfolioBcraBucket
{
    /// <summary>
    /// The worst situación (1..6) of the entity's CURRENT Entities.BcraDataJson, read exactly as bcraSignals
    /// reads its "now" side: Deuda.SituacionMaxima of the latest period, 1 when no debt is reported. Null
    /// when the snapshot supports none: never queried, a failed lookup, or no Deuda block.
    /// </summary>
    public required int? Situacion { get; init; }

    /// <summary>How many distinct entities.</summary>
    public required int EntityCount { get; init; }

    /// <summary>Sum of Amount over their Active cupos of this currency.</summary>
    public required decimal Otorgado { get; init; }

    /// <summary>Their operations that occupy cupo — every status except Cancelada.</summary>
    public required decimal Utilizado { get; init; }
}

/// <summary>The annual review of the PyMEs with an Active cupo.</summary>
public sealed record PortfolioAnnualReview
{
    /// <summary>PyMEs whose latest approved analysis is 365 or more Argentine days old. Uncapped.</summary>
    public required int OverdueCount { get; init; }

    /// <summary>PyMEs whose latest approved analysis is 335 to 364 Argentine days old. Uncapped.</summary>
    public required int UpcomingCount { get; init; }

    /// <summary>PyMEs with an Active cupo but no approved analysis at all: there is no date to count from.</summary>
    public required int WithoutApprovedAnalysisCount { get; init; }

    /// <summary>Overdue first, then upcoming; each by <see cref="PortfolioAnnualReviewItem.DaysDelta"/> descending. Cap 10.</summary>
    public required IReadOnlyList<PortfolioAnnualReviewItem> Items { get; init; }
}

/// <summary>One PyME whose annual review is due or coming up.</summary>
public sealed record PortfolioAnnualReviewItem
{
    /// <summary>The PyME.</summary>
    public required Guid EntityId { get; init; }

    /// <summary>The entity's business name; null when it no longer resolves in this tenant.</summary>
    public required string? EntityName { get; init; }

    /// <summary>The entity's CUIT; null when it no longer resolves in this tenant.</summary>
    public required string? EntityCuit { get; init; }

    /// <summary>The Argentine date of its latest approved analysis: ReviewedAt, else CreatedAt.</summary>
    public required DateOnly LastApprovedAt { get; init; }

    /// <summary><see cref="LastApprovedAt"/> plus 365 days.</summary>
    public required DateOnly DueDate { get; init; }

    /// <summary>Days past <see cref="DueDate"/>: zero or more when overdue, -30 to -1 when coming up.</summary>
    public required int DaysDelta { get; init; }

    /// <summary>The PYME_REVIEW_DUE alert's severity: "High" when overdue, "Medium" when coming up.</summary>
    public required string Severity { get; init; }
}

/// <summary>
/// Fondo de Riesgo usage of an SGR tenant. Every amount is ARS unless its name says otherwise. No regulatory
/// maximum is asserted, and mora is not computable from the data on record.
/// </summary>
public sealed record PortfolioSgr
{
    /// <summary>Whether the Fondo de Riesgo is set; when false every FdR-derived figure below is null.</summary>
    public required bool Configured { get; init; }

    /// <summary>sgr.fondo_riesgo_disponible, ARS.</summary>
    public required decimal? FondoRiesgoDisponible { get; init; }

    /// <summary>sgr.fondo_riesgo_fecha.</summary>
    public required DateOnly? FondoRiesgoFecha { get; init; }

    /// <summary>sgr.tipo_cambio_usd, ARS per USD; null when not set.</summary>
    public required decimal? TipoCambioUsd { get; init; }

    /// <summary>sgr.tipo_cambio_fecha.</summary>
    public required DateOnly? TipoCambioFecha { get; init; }

    /// <summary>
    /// Amount of the operations of Active cupos whose effective status is Vigente (issued, not past
    /// ValidUntil, not cancelled), in ARS: ARS at face value, USD converted with
    /// <see cref="TipoCambioUsd"/> when it is set. Aprobada (not issued) and Vencida are not in it.
    /// </summary>
    public required decimal SaldoGarantiasVigentesArs { get; init; }

    /// <summary>Whether USD operations are converted into the ARS figures (an exchange rate is set).</summary>
    public required bool UsdIncluded { get; init; }

    /// <summary>The Vigente USD operations left out for want of an exchange rate, in USD; null when <see cref="UsdIncluded"/>.</summary>
    public required decimal? UsdExcludedAmount { get; init; }

    /// <summary><see cref="SaldoGarantiasVigentesArs"/> / <see cref="FondoRiesgoDisponible"/>, 2 decimals (1.00 = 100%).</summary>
    public required decimal? GradoUtilizacion { get; init; }

    /// <summary>5% of <see cref="FondoRiesgoDisponible"/>, ARS.</summary>
    public required decimal? LimitePorSocio { get; init; }

    /// <summary>Socios partícipes whose exposure is strictly above <see cref="LimitePorSocio"/>. Uncapped.</summary>
    public required int? OverLimitCount { get; init; }

    /// <summary>Those socios, exposure descending, then EntityId. Cap 10.</summary>
    public required IReadOnlyList<PortfolioSgrOverLimitItem>? OverLimit { get; init; }

    /// <summary>Operations of Active cupos whose effective status is Vencida — past ValidUntil, never closed — under the same currency rule.</summary>
    public required int VencidasSinCerrarCount { get; init; }

    /// <summary>Their amount, ARS, under the same currency rule.</summary>
    public required decimal VencidasSinCerrarArs { get; init; }

    /// <summary>Always false: no payment or honoured-guarantee data exists to compute mora from.</summary>
    public required bool MoraDisponible { get; init; }
}

/// <summary>One socio partícipe whose exposure is above 5% of the Fondo de Riesgo.</summary>
public sealed record PortfolioSgrOverLimitItem
{
    /// <summary>The entity.</summary>
    public required Guid EntityId { get; init; }

    /// <summary>The entity's business name; null when it no longer resolves in this tenant.</summary>
    public required string? EntityName { get; init; }

    /// <summary>The entity's CUIT; null when it no longer resolves in this tenant.</summary>
    public required string? EntityCuit { get; init; }

    /// <summary>Its saldo de garantías vigentes, ARS, 2 decimals.</summary>
    public required decimal ExposicionArs { get; init; }

    /// <summary>Exposure as a percentage of the Fondo de Riesgo, 1 decimal.</summary>
    public required decimal PctDelFondo { get; init; }
}

// ── GET /api/dashboard/oversight ──────────────────────────────────────────────────

/// <summary>Response of GET /api/dashboard/oversight.</summary>
public sealed record OversightResponse
{
    /// <summary>When this response was computed (UTC).</summary>
    public required DateTime GeneratedAt { get; init; }

    /// <summary>The sections that failed and are null below. Empty when everything was computed.</summary>
    public required IReadOnlyList<string> DegradedSections { get; init; }

    /// <summary>Every PendingReview analysis of the tenant, all analysts.</summary>
    public required OversightTeamQueue? TeamQueue { get; init; }

    /// <summary>
    /// Approvals of the last 90 days against the system's suggestion: a balance analysis at High or
    /// Critical, or a persona humana analysis whose suggestion was RechazoSugerido. Newest first. Cap 20.
    /// </summary>
    public required DashboardPage<AgainstSuggestionItem>? AgainstSuggestion { get; init; }

    /// <summary>The tenant's last 20 decisions by ReviewedAt, newest first.</summary>
    public required IReadOnlyList<RecentDecisionItem>? RecentDecisions { get; init; }

    /// <summary>The tenant's last 10 re-scores, newest first.</summary>
    public required IReadOnlyList<RecentRescoreItem>? RecentRescores { get; init; }

    /// <summary>The current Argentine month, then the previous one.</summary>
    public required IReadOnlyList<MonthStatsItem>? MonthStats { get; init; }
}

/// <summary>The tenant's review queue.</summary>
public sealed record OversightTeamQueue
{
    /// <summary>How many PendingReview analyses there are, before the cap.</summary>
    public required int TotalCount { get; init; }

    /// <summary>Oldest GeneratedAt first. Cap 50.</summary>
    public required IReadOnlyList<TeamQueueItem> Items { get; init; }

    /// <summary>The whole queue by requester, most items first.</summary>
    public required IReadOnlyList<TeamQueueRequester> ByRequester { get; init; }

    /// <summary>How many analyses of the tenant are PreApproved, waiting for confirmation.</summary>
    public required int PreApprovedCount { get; init; }
}

/// <summary>One analysis in the review queue.</summary>
public sealed record TeamQueueItem
{
    /// <summary>AnalysisResults.ResultId.</summary>
    public required Guid ResultId { get; init; }

    /// <summary>Analyses.AnalysisId.</summary>
    public required Guid AnalysisId { get; init; }

    /// <summary><c>Balance</c> or <c>PersonaHumana</c>.</summary>
    public required string AnalysisKind { get; init; }

    /// <summary>The analysed entity.</summary>
    public required Guid EntityId { get; init; }

    /// <summary>The entity's business name.</summary>
    public required string? EntityName { get; init; }

    /// <summary>The entity's CUIT.</summary>
    public required string? EntityCuit { get; init; }

    /// <summary>Who requested it (Users.UserId).</summary>
    public required Guid RequestedBy { get; init; }

    /// <summary>Their display name; "Usuario" when that user no longer resolves.</summary>
    public required string? RequestedByName { get; init; }

    /// <summary>True when a confirmer returned it for rework (ReviewedBy is set).</summary>
    public required bool Returned { get; init; }

    /// <summary>AnalysisResults.AiSuggestedLevel.</summary>
    public required string? AiSuggestedLevel { get; init; }

    /// <summary>AnalysisResults.AiSuggestedScore.</summary>
    public required decimal? AiSuggestedScore { get; init; }

    /// <summary>Analyses.RequestedAmount. The column carries no currency.</summary>
    public required decimal? RequestedAmount { get; init; }

    /// <summary>AnalysisResults.GeneratedAt (UTC).</summary>
    public required DateTime GeneratedAt { get; init; }

    /// <summary>Whole days since <see cref="GeneratedAt"/>, floored.</summary>
    public required int AgeDays { get; init; }

    /// <summary>
    /// "Confiabilidad del score" (score credibility 1b-2b): the verdict GET /api/analyses/{id} carries for
    /// this row, from the same adapter and columns. Null on a persona humana analysis.
    /// </summary>
    public ScoreReliabilityResponse? ScoreReliability { get; init; }
}

/// <summary>One requester's share of the queue.</summary>
public sealed record TeamQueueRequester
{
    /// <summary>Users.UserId.</summary>
    public required Guid UserId { get; init; }

    /// <summary>Their display name; "Usuario" when that user no longer resolves.</summary>
    public required string? Name { get; init; }

    /// <summary>Their PendingReview analyses with no decision on record.</summary>
    public required int Pending { get; init; }

    /// <summary>Their PendingReview analyses returned for rework.</summary>
    public required int Returned { get; init; }

    /// <summary>Whole days since the oldest GeneratedAt among them.</summary>
    public required int OldestAgeDays { get; init; }
}

/// <summary>One approval against the system's suggestion.</summary>
public sealed record AgainstSuggestionItem
{
    /// <summary>AnalysisResults.ResultId.</summary>
    public required Guid ResultId { get; init; }

    /// <summary>Analyses.AnalysisId.</summary>
    public required Guid AnalysisId { get; init; }

    /// <summary><c>Balance</c> or <c>PersonaHumana</c>.</summary>
    public required string AnalysisKind { get; init; }

    /// <summary>The analysed entity.</summary>
    public required Guid EntityId { get; init; }

    /// <summary>The entity's business name.</summary>
    public required string? EntityName { get; init; }

    /// <summary>The entity's CUIT.</summary>
    public required string? EntityCuit { get; init; }

    /// <summary>One of <see cref="DashboardAgainstSuggestionReasons"/>.</summary>
    public required string Reason { get; init; }

    /// <summary>AnalysisResults.AiSuggestedLevel.</summary>
    public required string? AiSuggestedLevel { get; init; }

    /// <summary>The persona humana suggestion, derived as approvals' waitingForMe derives it; null on a balance analysis.</summary>
    public required string? PersonSuggestion { get; init; }

    /// <summary>Who approved it (Analyses.ReviewedBy).</summary>
    public required Guid? DecidedBy { get; init; }

    /// <summary>Their display name; "Usuario" when that user no longer resolves.</summary>
    public required string? DecidedByName { get; init; }

    /// <summary>When (UTC).</summary>
    public required DateTime? ReviewedAt { get; init; }

    /// <summary>The decider's notes, as stored.</summary>
    public required string? ReviewNotes { get; init; }

    /// <summary>Analyses.DecisionAmount — what was committed.</summary>
    public required decimal? DecisionAmount { get; init; }

    /// <summary>ISO 4217 code of <see cref="DecisionAmount"/>.</summary>
    public required string? DecisionCurrency { get; init; }
}

/// <summary>One of the tenant's latest decisions.</summary>
public sealed record RecentDecisionItem
{
    /// <summary>AnalysisResults.ResultId.</summary>
    public required Guid ResultId { get; init; }

    /// <summary>Analyses.AnalysisId.</summary>
    public required Guid AnalysisId { get; init; }

    /// <summary><c>Balance</c> or <c>PersonaHumana</c>.</summary>
    public required string AnalysisKind { get; init; }

    /// <summary>The analysed entity.</summary>
    public required Guid EntityId { get; init; }

    /// <summary>The entity's business name.</summary>
    public required string? EntityName { get; init; }

    /// <summary>The entity's CUIT.</summary>
    public required string? EntityCuit { get; init; }

    /// <summary>
    /// <see cref="DashboardActs.Approved"/>, <see cref="DashboardActs.Confirmed"/> (an approval with a
    /// pre-approval on the row), <see cref="DashboardActs.Rejected"/> or <see cref="DashboardActs.Returned"/>.
    /// </summary>
    public required string Act { get; init; }

    /// <summary>Who decided (Analyses.ReviewedBy).</summary>
    public required Guid? DecidedBy { get; init; }

    /// <summary>Their display name; "Usuario" when that user no longer resolves.</summary>
    public required string? DecidedByName { get; init; }

    /// <summary>When (UTC) — the order of this list.</summary>
    public required DateTime ReviewedAt { get; init; }

    /// <summary>The decider's notes, as stored.</summary>
    public required string? ReviewNotes { get; init; }

    /// <summary>Analyses.DecisionAmount; null on a rejection or a return.</summary>
    public required decimal? DecisionAmount { get; init; }

    /// <summary>ISO 4217 code of <see cref="DecisionAmount"/>.</summary>
    public required string? DecisionCurrency { get; init; }

    /// <summary>The analysis' status NOW.</summary>
    public required string Status { get; init; }
}

/// <summary>One re-score of an analysis (AnalysisRescores, DDL 073): a balance re-score or a persona re-evaluation.</summary>
public sealed record RecentRescoreItem
{
    /// <summary>AnalysisRescores.RescoreId.</summary>
    public required Guid RescoreId { get; init; }

    /// <summary>The re-scored result — what the Portal links to.</summary>
    public required Guid ResultId { get; init; }

    /// <summary>Its analysis.</summary>
    public required Guid AnalysisId { get; init; }

    /// <summary><c>Balance</c> or <c>PersonaHumana</c>; null when the analysis is not this tenant's.</summary>
    public required string? AnalysisKind { get; init; }

    /// <summary>The analysed entity; null when the analysis is not this tenant's.</summary>
    public required Guid? EntityId { get; init; }

    /// <summary>The entity's business name.</summary>
    public required string? EntityName { get; init; }

    /// <summary>The entity's CUIT.</summary>
    public required string? EntityCuit { get; init; }

    /// <summary>When (UTC).</summary>
    public required DateTime RescoredAt { get; init; }

    /// <summary>Who re-scored it (Users.UserId).</summary>
    public required Guid RescoredBy { get; init; }

    /// <summary>Their display name; "Usuario" when that user no longer resolves.</summary>
    public required string? RescoredByName { get; init; }

    /// <summary>Score before.</summary>
    public required decimal? PreviousScore { get; init; }

    /// <summary>Score after.</summary>
    public required decimal? NewScore { get; init; }

    /// <summary>Level before.</summary>
    public required string? PreviousLevel { get; init; }

    /// <summary>Level after.</summary>
    public required string? NewLevel { get; init; }

    /// <summary>EnrichmentStatus before.</summary>
    public required string PreviousEnrichmentStatus { get; init; }

    /// <summary>EnrichmentStatus after.</summary>
    public required string NewEnrichmentStatus { get; init; }

    /// <summary>
    /// Persona humana only: the solicitante's suggestion before the re-evaluation (<see cref="PersonSuggestions"/>),
    /// as GET /api/analyses/{id}/rescores reads it. Null on a balance re-score.
    /// </summary>
    public string? PreviousSuggestion { get; init; }

    /// <summary>Persona humana only: the suggestion after it. Null on a balance re-score.</summary>
    public string? NewSuggestion { get; init; }
}

/// <summary>Throughput of one Argentine calendar month, over the analyses CREATED in it.</summary>
public sealed record MonthStatsItem
{
    /// <summary>The Argentine year.</summary>
    public required int Year { get; init; }

    /// <summary>The Argentine month, 1-12.</summary>
    public required int Month { get; init; }

    /// <summary>00:00 in Buenos Aires on the 1st, as UTC. Inclusive.</summary>
    public required DateTime PeriodStartUtc { get; init; }

    /// <summary>00:00 in Buenos Aires on the 1st of the next month, as UTC. Exclusive.</summary>
    public required DateTime PeriodEndUtc { get; init; }

    /// <summary>Analyses created in the month, in any status.</summary>
    public required int Created { get; init; }

    /// <summary>Those analyses by their status NOW, most first.</summary>
    public required IReadOnlyList<MonthStatusCount> ByStatus { get; init; }

    /// <summary>Those now Approved or Rejected, with ReviewedAt set.</summary>
    public required int Decided { get; init; }

    /// <summary>Median hours from CreatedAt to ReviewedAt over <see cref="Decided"/>; null when it is 0.</summary>
    public required double? MedianHoursToDecision { get; init; }

    /// <summary>90th percentile of the same hours (linear interpolation); null when <see cref="Decided"/> is 0.</summary>
    public required double? P90HoursToDecision { get; init; }
}

/// <summary>How many of a month's analyses are in one status.</summary>
public sealed record MonthStatusCount
{
    /// <summary>Analyses.Status as stored.</summary>
    public required string Status { get; init; }

    /// <summary>How many.</summary>
    public required int Count { get; init; }
}

// ── GET /api/dashboard/operations ─────────────────────────────────────────────────

/// <summary>Response of GET /api/dashboard/operations.</summary>
public sealed record OperationsResponse
{
    /// <summary>When this response was computed (UTC).</summary>
    public required DateTime GeneratedAt { get; init; }

    /// <summary>The sections that failed and are null below. Empty when everything was computed.</summary>
    public required IReadOnlyList<string> DegradedSections { get; init; }

    /// <summary>ApiCallLogs of the last 24 hours, per service.</summary>
    public required OperationsIntegrations? Integrations { get; init; }

    /// <summary>Source snapshot age over the entities with an Active cupo.</summary>
    public required OperationsFreshness? Freshness { get; init; }
}

/// <summary>The integrations section.</summary>
public sealed record OperationsIntegrations
{
    /// <summary>Start of the window (UTC), inclusive.</summary>
    public required DateTime WindowStartUtc { get; init; }

    /// <summary>End of the window (UTC) — the response's GeneratedAt — inclusive.</summary>
    public required DateTime WindowEndUtc { get; init; }

    /// <summary>
    /// The services that write ApiCallLogs rows, always listed, then the vendors that write none
    /// (<see cref="IntegrationServiceItem.Tracked"/> false), then any other value found in the window.
    /// </summary>
    public required IReadOnlyList<IntegrationServiceItem> Services { get; init; }
}

/// <summary>One ApiCallLogs.Service value.</summary>
public sealed record IntegrationServiceItem
{
    /// <summary>ApiCallLogs.Service as stored: ARCA, LUFE, NOSIS, Claude, ClaudeChat, BCRA, CASFOG.</summary>
    public required string Service { get; init; }

    /// <summary>
    /// False for a vendor whose calls are not written to ApiCallLogs at all — every figure below is then
    /// null, because the table cannot say.
    /// </summary>
    public required bool Tracked { get; init; }

    /// <summary>Rows in the window.</summary>
    public required int? Calls { get; init; }

    /// <summary>
    /// Rows in the window that are real failures — IsSuccess = false, minus the vendor's "no data" answers
    /// counted in <see cref="NoData"/>.
    /// </summary>
    public required int? Failures { get; init; }

    /// <summary>
    /// Rows in the window where the vendor answered that it holds no data for the subject (a LUFE "no
    /// legajo", an ARCA "not found"). Counted in <see cref="Calls"/>, never in <see cref="Failures"/>.
    /// Additive: null for an untracked vendor, like every other figure.
    /// </summary>
    public int? NoData { get; init; }

    /// <summary>The latest successful call in the window (UTC), a "no data" answer included.</summary>
    public required DateTime? LastSuccessAt { get; init; }

    /// <summary>The latest real failure in the window (UTC) — never a "no data" answer.</summary>
    public required DateTime? LastFailureAt { get; init; }

    /// <summary>95th percentile of DurationMs in the window (nearest rank), over the rows that carry one.</summary>
    public required int? P95DurationMs { get; init; }
}

/// <summary>The freshness section.</summary>
public sealed record OperationsFreshness
{
    /// <summary>How many entities have an Active cupo — the population of every row below.</summary>
    public required int EntityCount { get; init; }

    /// <summary>One row per source: ARCA, LUFE, BCRA, CASFOG, NOSIS.</summary>
    public required IReadOnlyList<FreshnessSourceItem> Sources { get; init; }
}

/// <summary>How old one source's snapshot is across the population.</summary>
public sealed record FreshnessSourceItem
{
    /// <summary>ARCA (Entities.FiscalDataUpdatedAt), LUFE, BCRA, CASFOG or NOSIS.</summary>
    public required string Source { get; init; }

    /// <summary>Updated less than 30 days ago.</summary>
    public required int Under30Days { get; init; }

    /// <summary>Updated 30 to 90 days ago, both inclusive.</summary>
    public required int Days30To90 { get; init; }

    /// <summary>Updated more than 90 days ago.</summary>
    public required int Over90Days { get; init; }

    /// <summary>No update timestamp on record.</summary>
    public required int Never { get; init; }
}
