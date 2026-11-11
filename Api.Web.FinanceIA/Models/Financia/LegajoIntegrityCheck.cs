// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using System.Text.Json.Serialization;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// One deterministic arithmetic check over a legajo extraction (M10 Phase 2b), persisted in
/// LegajoExtractions.ChecksJson and recomputed on the effective values for the review view. Amounts are
/// in PESOS (the printed figures scaled by the document's amountsUnit).
/// </summary>
public sealed record LegajoIntegrityCheck
{
    /// <summary>Stable check identifier (LegajoIntegrityService's constants), e.g. <c>bp_total_bienes</c>.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>The group a repeated check is about: <c>"Inmuebles/Pais"</c>, a section, an annex reference; else null.</summary>
    [JsonPropertyName("subject")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Subject { get; init; }

    [JsonPropertyName("passed")]
    public required bool Passed { get; init; }

    /// <summary>
    /// Structured form of <see cref="Subject"/> for the Portal, which should not parse the string: e.g.
    /// <c>{rubro, location}</c>, <c>{section}</c>, <c>{annexRef}</c>, <c>{period, rectificativa, alicuotaPct}</c>,
    /// <c>{period, rectificativa, jurisdiction}</c>. <c>period</c> is YYYY-MM, <c>anual</c> or <c>sin-periodo</c>,
    /// as in the subject. Null — omitted — when the check has no subject.
    /// </summary>
    [JsonPropertyName("subjectParts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? SubjectParts { get; init; }

    /// <summary>
    /// What <see cref="Expected"/>, <see cref="Actual"/> and <see cref="Diff"/> are (<see cref="LegajoFigureKinds"/>):
    /// <c>money</c> (pesos), <c>month</c> (YYYYMM; diff in months) or <c>year</c>. Null — omitted — for a range or
    /// sign rule, which carries no figures.
    /// </summary>
    [JsonPropertyName("figureKind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FigureKind { get; init; }

    /// <summary>The figure the other fields imply (null for a range or sign check).</summary>
    [JsonPropertyName("expected")]
    public decimal? Expected { get; init; }

    /// <summary>The figure the document reports.</summary>
    [JsonPropertyName("actual")]
    public decimal? Actual { get; init; }

    /// <summary>|expected − actual| in pesos (null for a range or sign check).</summary>
    [JsonPropertyName("diff")]
    public decimal? Diff { get; init; }

    /// <summary>JSON Pointers of the fields involved, so the Portal can highlight them.</summary>
    [JsonPropertyName("fieldPaths")]
    public IReadOnlyList<string> FieldPaths { get; init; } = [];

    /// <summary>
    /// The subset of <see cref="FieldPaths"/> a failed check sends to the review queue. Set on the checks
    /// that compare ONE printed total (or result) with a sum of its parts: only the printed total is
    /// flagged, the parts stay in <see cref="FieldPaths"/> to be highlighted. Null — omitted from the JSON —
    /// means every field path: peer comparisons, range and sign rules, and every run stored before the
    /// property existed.
    /// </summary>
    [JsonPropertyName("flaggedPaths")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? FlaggedPaths { get; init; }

    /// <summary>The paths a failure flags: <see cref="FlaggedPaths"/>, or every field path when it is null.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> PathsToFlag => FlaggedPaths ?? FieldPaths;

    /// <summary>The es-AR anomaly for a failed check (no "%" anywhere); null when it passed.</summary>
    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }
}
