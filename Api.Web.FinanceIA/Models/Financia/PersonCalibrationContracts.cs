// Project : Api.Web.FinanceIA
// Created : 2026-10-30

namespace Api.Web.FinanceIA.Models.Financia;

// The wire contract of the persona humana calibration board (GET /api/dashboard/person-calibration), kept
// in ONE file like DashboardContracts.cs: the Portal consumes it as a unit and Swagger is Development-only.
//
// The population is the PersonaHumana analyses CREATED in the requested Argentine-date window, optionally
// narrowed to the analysts whose CURRENT Users.TeamId is the requested team. Every figure is DERIVED at
// read time from the frozen AnalysisParticipants columns, through PersonEvaluationJson — nothing here is
// stored. Field values are stored codes (statuses, outcomes, bands, suggestions, filter and indicator ids),
// never Spanish copy; the CSV export is the only place they become es-AR labels.
//
// A section that could not be computed is null and named in DegradedSections; the response is still 200.

/// <summary>Section names as they appear in <c>degradedSections</c>.</summary>
public static class PersonCalibrationSections
{
    /// <summary>Totals by status and suggestion, decided vs pending, and the pilot milestone.</summary>
    public const string Summary = "summary";

    /// <summary>The suggestion × outcome matrix and the flagged cases.</summary>
    public const string Decisions = "decisions";

    /// <summary>Per filter id, the outcome counts and the SinDatos rate; the cannotConclude codes.</summary>
    public const string Filters = "filters";

    /// <summary>Per indicator id, the band counts and the decisions taken in each band.</summary>
    public const string Indicators = "indicators";

    /// <summary>Per fiscal regime and document type, missing and stale documents; stale snapshots per source.</summary>
    public const string Documents = "documents";

    /// <summary>Re-evaluations of the analyses in the population and the suggestion flips they produced.</summary>
    public const string Reevaluations = "reevaluations";

    /// <summary>The pf_ parameters, current vs default, and how the frozen ones differ across the population.</summary>
    public const string Parameters = "parameters";
}

/// <summary>Where an analysis stands, as the board buckets it. Stable codes.</summary>
public static class PersonCalibrationOutcomes
{
    /// <summary>Status Approved.</summary>
    public const string Approved = "Approved";

    /// <summary>Status Rejected.</summary>
    public const string Rejected = "Rejected";

    /// <summary>Status PreApproved: an analyst approved above their limit; it waits for confirmation.</summary>
    public const string PreApproved = "PreApproved";

    /// <summary>Status PendingReview, including an analysis a confirmer returned for rework.</summary>
    public const string Pending = "Pending";

    /// <summary>Any other status the CHECK admits (Queued, Processing, Reviewed).</summary>
    public const string Other = "Other";
}

/// <summary>How the team filter attributes an analysis to a team. Stable codes.</summary>
public static class PersonCalibrationTeamAttributions
{
    /// <summary>
    /// By the requesting analyst's CURRENT Users.TeamId — the team is not stamped on the analysis, so an
    /// analyst who changed teams carries their whole history to the new one.
    /// </summary>
    public const string CurrentMembership = "current_membership";
}

/// <summary>GET /api/dashboard/person-calibration.</summary>
public sealed record PersonCalibrationResponse
{
    /// <summary>When the board was computed (UTC).</summary>
    public required DateTime GeneratedAt { get; init; }

    /// <summary>First Argentine day of the window, inclusive.</summary>
    public required DateOnly From { get; init; }

    /// <summary>Last Argentine day of the window, inclusive.</summary>
    public required DateOnly To { get; init; }

    /// <summary>The team filter; null for the whole tenant.</summary>
    public Guid? TeamId { get; init; }

    /// <summary>Always <see cref="PersonCalibrationTeamAttributions.CurrentMembership"/>.</summary>
    public required string TeamAttribution { get; init; }

    /// <summary>The sections that could not be computed, by <see cref="PersonCalibrationSections"/> name.</summary>
    public required IReadOnlyList<string> DegradedSections { get; init; }

    /// <summary>
    /// The analyses left out of every derived figure because their solicitante row is missing or its frozen
    /// JSON cannot be read. Null only when the population itself could not be loaded.
    /// </summary>
    public PersonCalibrationUnreadable? UnreadableRows { get; init; }

    /// <summary>The <see cref="PersonCalibrationSections.Summary"/> section.</summary>
    public PersonCalibrationSummary? Summary { get; init; }

    /// <summary>The <see cref="PersonCalibrationSections.Decisions"/> section.</summary>
    public PersonCalibrationDecisions? Decisions { get; init; }

    /// <summary>The <see cref="PersonCalibrationSections.Filters"/> section.</summary>
    public PersonCalibrationFilters? Filters { get; init; }

    /// <summary>The <see cref="PersonCalibrationSections.Indicators"/> section.</summary>
    public IReadOnlyList<PersonCalibrationIndicator>? Indicators { get; init; }

    /// <summary>The <see cref="PersonCalibrationSections.Documents"/> section.</summary>
    public PersonCalibrationDocuments? Documents { get; init; }

    /// <summary>The <see cref="PersonCalibrationSections.Reevaluations"/> section.</summary>
    public PersonCalibrationReevaluations? Reevaluations { get; init; }

    /// <summary>The <see cref="PersonCalibrationSections.Parameters"/> section.</summary>
    public PersonCalibrationParameters? Parameters { get; init; }
}

/// <summary>Analyses skipped as unreadable. Ids only — the board never logs or returns their content.</summary>
public sealed record PersonCalibrationUnreadable
{
    /// <summary>How many.</summary>
    public required int Count { get; init; }

    /// <summary>Which.</summary>
    public required IReadOnlyList<Guid> AnalysisIds { get; init; }
}

/// <summary>A code and how many times it occurs.</summary>
public sealed record PersonCalibrationCount
{
    /// <summary>The stored code (a status, a suggestion, a cannotConclude code, a version…).</summary>
    public required string Code { get; init; }

    /// <summary>Occurrences.</summary>
    public required int Count { get; init; }
}

/// <summary>Totals over the population.</summary>
public sealed record PersonCalibrationSummary
{
    /// <summary>Every persona analysis in the window, readable or not.</summary>
    public required int Total { get; init; }

    /// <summary>Counts by stored Analyses.Status, every analysis in the window.</summary>
    public required IReadOnlyList<PersonCalibrationCount> ByStatus { get; init; }

    /// <summary>Counts by derived suggestion, readable analyses only.</summary>
    public required IReadOnlyList<PersonCalibrationCount> BySuggestion { get; init; }

    /// <summary>Settled: Approved or Rejected.</summary>
    public required int Decided { get; init; }

    /// <summary>Everything else: PendingReview, PreApproved and any other status.</summary>
    public required int Pending { get; init; }

    /// <summary>The pilot milestone — TENANT-WIDE, whatever the window and the team filter.</summary>
    public required PersonCalibrationPilot Pilot { get; init; }
}

/// <summary>Whether the pilot reached the review point (30 days or 20 analyses).</summary>
public sealed record PersonCalibrationPilot
{
    /// <summary>CreatedAt of the tenant's first persona analysis (UTC); null when there is none.</summary>
    public DateTime? FirstAnalysisAt { get; init; }

    /// <summary>Persona analyses the tenant has created to date.</summary>
    public required int TotalToDate { get; init; }

    /// <summary>TotalToDate ≥ 20.</summary>
    public required bool Reached20 { get; init; }

    /// <summary>At least 30 days since the first persona analysis.</summary>
    public required bool Reached30Days { get; init; }
}

/// <summary>How the analysts decided against the derived suggestion.</summary>
public sealed record PersonCalibrationDecisions
{
    /// <summary>One row per suggestion, counting the readable analyses in each outcome.</summary>
    public required IReadOnlyList<PersonCalibrationMatrixRow> Matrix { get; init; }

    /// <summary>The flagged cases, newest first, capped (TotalCount is the uncapped size).</summary>
    public required DashboardPage<PersonCalibrationCase> FlaggedCases { get; init; }
}

/// <summary>One suggestion's outcomes.</summary>
public sealed record PersonCalibrationMatrixRow
{
    /// <summary>A <see cref="PersonSuggestions"/> code.</summary>
    public required string Suggestion { get; init; }

    /// <summary>Status Approved.</summary>
    public required int Approved { get; init; }

    /// <summary>Status Rejected.</summary>
    public required int Rejected { get; init; }

    /// <summary>Status PreApproved, awaiting confirmation.</summary>
    public required int PreApproved { get; init; }

    /// <summary>Status PendingReview.</summary>
    public required int Pending { get; init; }

    /// <summary>Any other status.</summary>
    public required int Other { get; init; }
}

/// <summary>
/// The derived calibration flags of one analysis. "Approved" means the analyst's APPROVING act: status
/// Approved or PreApproved — the latter is how an approval against a Rechazo sugerido usually shows up
/// while pf_rechazo_sugerido_requiere_preaprobacion is on.
/// </summary>
public sealed record PersonCalibrationFlags
{
    /// <summary>Approved or PreApproved with suggestion RechazoSugerido.</summary>
    public required bool ApprovedWithRechazoSugerido { get; init; }

    /// <summary>Approved or PreApproved with any solicitante indicator in band Rojo.</summary>
    public required bool ApprovedWithRedBand { get; init; }

    /// <summary>Rejected, suggestion ParaDecisionDelAnalista and no solicitante indicator in band Rojo.</summary>
    public required bool RejectedInGreen { get; init; }

    /// <summary>
    /// The aggregate counter-guarantee coverage of the frozen fiador rows is below the minimum FROZEN in this
    /// analysis' parameters (not the tenant's current one). Null without fiadores, when the coverage has no
    /// ratio (a non-ARS or non-comparable operation) or when the frozen parameters predate the key.
    /// </summary>
    public bool? CounterGuaranteeBelowMin { get; init; }
}

/// <summary>One flagged analysis.</summary>
public sealed record PersonCalibrationCase
{
    /// <summary>Analyses.AnalysisId.</summary>
    public required Guid AnalysisId { get; init; }

    /// <summary>
    /// AnalysisResults.ResultId — the id GET /api/analyses/{id} and the Portal's analysis detail take, which is
    /// NOT the analysis id. Null only when the analysis has no result row.
    /// </summary>
    public Guid? ResultId { get; init; }

    /// <summary>The entity analysed.</summary>
    public required Guid EntityId { get; init; }

    /// <summary>Entity BusinessName, as the dashboard shows it; null when the entity no longer resolves.</summary>
    public string? EntityName { get; init; }

    /// <summary>Entity CUIT, as the dashboard shows it.</summary>
    public string? EntityCuit { get; init; }

    /// <summary>Analyses.CreatedAt (UTC).</summary>
    public required DateTime CreatedAt { get; init; }

    /// <summary>The requesting analyst.</summary>
    public required Guid RequestedBy { get; init; }

    /// <summary>Their display name.</summary>
    public string? RequestedByName { get; init; }

    /// <summary>The requesting analyst's CURRENT team.</summary>
    public Guid? TeamId { get; init; }

    /// <summary>That team's name.</summary>
    public string? TeamName { get; init; }

    /// <summary>Stored Analyses.Status.</summary>
    public required string Status { get; init; }

    /// <summary>A <see cref="PersonCalibrationOutcomes"/> code.</summary>
    public required string Outcome { get; init; }

    /// <summary>The derived <see cref="PersonSuggestions"/> code.</summary>
    public required string Suggestion { get; init; }

    /// <summary>
    /// Who took the act the status records: the reviewer of an Approved or Rejected analysis — or of a
    /// PendingReview one a confirmer returned — and the pre-approver of a PreApproved one; null otherwise.
    /// </summary>
    public Guid? DecidedBy { get; init; }

    /// <summary>Their display name.</summary>
    public string? DecidedByName { get; init; }

    /// <summary>When (UTC).</summary>
    public DateTime? DecidedAt { get; init; }

    /// <summary>
    /// The reason of the act: the reason of the checks that act wrote (the confirmation's for Approved, the
    /// pre-approval's for PreApproved), else Analyses.ReviewNotes. Free text, as the analyst typed it.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>Solicitante filter ids with outcome Rechazo.</summary>
    public required IReadOnlyList<string> RechazoFilterIds { get; init; }

    /// <summary>Solicitante indicator ids in band Rojo.</summary>
    public required IReadOnlyList<string> RojoIndicatorIds { get; init; }

    /// <summary>The aggregate counter-guarantee coverage ratio (0.85 = 85 %); null without fiadores or ratio.</summary>
    public decimal? CounterGuaranteeRatio { get; init; }

    /// <summary>pf_contragarantia_cobertura_min_pct as frozen in this analysis' parameters (whole percent).</summary>
    public int? FrozenMinCounterGuaranteePct { get; init; }

    /// <summary>The operation currency, from the solicitante row.</summary>
    public string? Currency { get; init; }

    /// <summary>The flags.</summary>
    public required PersonCalibrationFlags Flags { get; init; }
}

/// <summary>Which filters return SinDatos — missing integration data, not a threshold problem.</summary>
public sealed record PersonCalibrationFilters
{
    /// <summary>Per filter id, sorted by SinDatos desc, then id.</summary>
    public required IReadOnlyList<PersonCalibrationFilter> Items { get; init; }

    /// <summary>The solicitante coverage's cannotConclude codes, most frequent first.</summary>
    public required IReadOnlyList<PersonCalibrationCount> CannotConclude { get; init; }
}

/// <summary>One filter id across the population (solicitante rows only).</summary>
public sealed record PersonCalibrationFilter
{
    /// <summary>PersonFilterResult.Id.</summary>
    public required string FilterId { get; init; }

    /// <summary>Outcome Ok.</summary>
    public required int Ok { get; init; }

    /// <summary>Outcome Alerta.</summary>
    public required int Alerta { get; init; }

    /// <summary>Outcome Rechazo.</summary>
    public required int Rechazo { get; init; }

    /// <summary>Outcome SinDatos.</summary>
    public required int SinDatos { get; init; }

    /// <summary>Every result for this id, whatever its outcome.</summary>
    public required int Total { get; init; }

    /// <summary>SinDatos / Total, 4 decimals.</summary>
    public required decimal SinDatosRate { get; init; }
}

/// <summary>One indicator id across the population (solicitante rows only).</summary>
public sealed record PersonCalibrationIndicator
{
    /// <summary>PersonIndicatorResult.Id.</summary>
    public required string IndicatorId { get; init; }

    /// <summary>
    /// One entry per band seen: Verde, Amarillo, Naranja, Rojo, plus <c>SinDatos</c> (status SinDatos) and
    /// <c>SinBanda</c> (status Ok with no band), in that order.
    /// </summary>
    public required IReadOnlyList<PersonCalibrationBand> Bands { get; init; }
}

/// <summary>One band of one indicator, and what was decided in it.</summary>
public sealed record PersonCalibrationBand
{
    /// <summary>A <see cref="PersonIndicatorBands"/> code, <c>SinDatos</c> or <c>SinBanda</c>.</summary>
    public required string Band { get; init; }

    /// <summary>Analyses with the indicator in this band.</summary>
    public required int Count { get; init; }

    /// <summary>Of those, status Approved.</summary>
    public required int Approved { get; init; }

    /// <summary>Of those, status PreApproved.</summary>
    public required int PreApproved { get; init; }

    /// <summary>Of those, status Rejected.</summary>
    public required int Rejected { get; init; }
}

/// <summary>Which documents are missing or arrive stale — to tune the tenant's expected-documents matrix.</summary>
public sealed record PersonCalibrationDocuments
{
    /// <summary>Per fiscal regime and document type, sorted by regime, then missing + stale desc, then type.</summary>
    public required IReadOnlyList<PersonCalibrationDocument> Items { get; init; }

    /// <summary>Per snapshot source (BCRA, ARCA, LUFE).</summary>
    public required IReadOnlyList<PersonCalibrationSnapshot> Snapshots { get; init; }
}

/// <summary>One frozen checklist line across the analyses of one fiscal regime.</summary>
public sealed record PersonCalibrationDocument
{
    /// <summary>PersonCoverageResult.FiscalRegime as frozen.</summary>
    public required string FiscalRegime { get; init; }

    /// <summary>PersonCoverageItem.DocumentType.</summary>
    public required string DocumentType { get; init; }

    /// <summary>Analyses whose checklist carried the line.</summary>
    public required int Analyses { get; init; }

    /// <summary>Of those, where it was required.</summary>
    public required int Required { get; init; }

    /// <summary>Required and not present.</summary>
    public required int Missing { get; init; }

    /// <summary>
    /// Stale (isStale and not a seasonal gap). The engine already clears isStale on a seasonal gap, so this is
    /// isStale; the guard stays in case a future engine sets both.
    /// </summary>
    public required int Stale { get; init; }

    /// <summary>Older than the maximum age, but every month of the gap is off-season (agro mode).</summary>
    public required int SeasonalGap { get; init; }
}

/// <summary>One vendor snapshot source.</summary>
public sealed record PersonCalibrationSnapshot
{
    /// <summary>PersonCoverageSnapshot.Source.</summary>
    public required string Source { get; init; }

    /// <summary>Analyses whose coverage listed it.</summary>
    public required int Total { get; init; }

    /// <summary>Of those, stale at evaluation time.</summary>
    public required int Stale { get; init; }
}

/// <summary>Re-evaluations ("Reanalizar") of the analyses in the population, whenever they happened.</summary>
public sealed record PersonCalibrationReevaluations
{
    /// <summary>Analyses re-evaluated at least once.</summary>
    public required int AnalysesReevaluated { get; init; }

    /// <summary>AnalysisRescores rows.</summary>
    public required int TotalReevaluations { get; init; }

    /// <summary>Re-evaluations whose suggestion changed.</summary>
    public required int SuggestionChanges { get; init; }

    /// <summary>Counts per previous → new suggestion pair, most frequent first.</summary>
    public required IReadOnlyList<PersonCalibrationFlip> Flips { get; init; }

    /// <summary>The most recent re-evaluations.</summary>
    public required IReadOnlyList<PersonCalibrationReevaluation> Recent { get; init; }
}

/// <summary>One previous → new suggestion pair.</summary>
public sealed record PersonCalibrationFlip
{
    /// <summary>$.suggestions.previous of PreviousValuesJson; null when the row has none.</summary>
    public string? Previous { get; init; }

    /// <summary>$.suggestions.new of PreviousValuesJson; null when the row has none.</summary>
    public string? New { get; init; }

    /// <summary>Re-evaluations with this pair.</summary>
    public required int Count { get; init; }
}

/// <summary>One re-evaluation.</summary>
public sealed record PersonCalibrationReevaluation
{
    /// <summary>AnalysisRescores.RescoreId.</summary>
    public required Guid RescoreId { get; init; }

    /// <summary>The analysis.</summary>
    public required Guid AnalysisId { get; init; }

    /// <summary>AnalysisRescores.ResultId — the result the re-evaluation overwrote, which the detail route takes.</summary>
    public required Guid ResultId { get; init; }

    /// <summary>The analysis' entity; null only if the analysis is not in the board's population.</summary>
    public Guid? EntityId { get; init; }

    /// <summary>When (UTC).</summary>
    public required DateTime RescoredAt { get; init; }

    /// <summary>Who.</summary>
    public required Guid RescoredBy { get; init; }

    /// <summary>Their display name.</summary>
    public string? RescoredByName { get; init; }

    /// <summary>The suggestion before.</summary>
    public string? PreviousSuggestion { get; init; }

    /// <summary>The suggestion after.</summary>
    public string? NewSuggestion { get; init; }
}

/// <summary>The pf_ parameters now, and as the population froze them.</summary>
public sealed record PersonCalibrationParameters
{
    /// <summary>Every key of PersonEvaluationDefaults.Layout, in Layout order.</summary>
    public required IReadOnlyList<PersonCalibrationParameter> Items { get; init; }

    /// <summary>Solicitante EvaluationVersion counts over the readable analyses.</summary>
    public required IReadOnlyList<PersonCalibrationCount> EvaluationVersions { get; init; }

    /// <summary>
    /// The frozen parameters that DIFFER across the readable analyses — "the threshold changed mid-pilot".
    /// Only parameters with more than one distinct frozen value are listed.
    /// </summary>
    public required IReadOnlyList<PersonCalibrationFrozenDifference> FrozenDifferences { get; init; }
}

/// <summary>One pf_ key, current vs default.</summary>
public sealed record PersonCalibrationParameter
{
    /// <summary>The pf_ key.</summary>
    public required string Key { get; init; }

    /// <summary>Layout group.</summary>
    public required string Group { get; init; }

    /// <summary>Layout unit; null for a unitless key.</summary>
    public string? Unit { get; init; }

    /// <summary>The code default.</summary>
    public required decimal DefaultValue { get; init; }

    /// <summary>The tenant's value now: its ScoringConfig row, else the default.</summary>
    public required decimal CurrentValue { get; init; }

    /// <summary>The tenant has a row for it (PersonEvaluationParameters.TenantOverrides).</summary>
    public required bool Overridden { get; init; }

    /// <summary>ScoringConfig.UpdatedAt of that row; null when not overridden.</summary>
    public DateTime? UpdatedAt { get; init; }
}

/// <summary>One frozen parameter and its distinct values across the population.</summary>
public sealed record PersonCalibrationFrozenDifference
{
    /// <summary>The camelCase member of the frozen <c>sources.parameters</c> object (e.g. contragarantiaCoberturaMinPct).</summary>
    public required string Parameter { get; init; }

    /// <summary>Each distinct value (its JSON text; null when absent from the blob) and how many analyses froze it.</summary>
    public required IReadOnlyList<PersonCalibrationFrozenValue> Values { get; init; }
}

/// <summary>One frozen value.</summary>
public sealed record PersonCalibrationFrozenValue
{
    /// <summary>The value as JSON text (<c>60</c>, <c>0.35</c>, <c>true</c>); null when the blob predates the key.</summary>
    public string? Value { get; init; }

    /// <summary>Analyses that froze it.</summary>
    public required int Count { get; init; }
}
