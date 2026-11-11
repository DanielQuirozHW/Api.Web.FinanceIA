// Project : Api.Web.FinanceIA
// Created : 2026-06-03

namespace Api.Web.FinanceIA.Models.Settings;

/// <summary>
/// A single scoring configuration parameter (key, value, description).
/// </summary>
public sealed record ScoringConfigItemDto
{
    /// <summary>Configuration key (e.g. weight_roe).</summary>
    public required string Key { get; init; }

    /// <summary>Numeric configuration value.</summary>
    public required decimal Value { get; init; }

    /// <summary>Human-readable description of the parameter.</summary>
    public string? Description { get; init; }
}

/// <summary>
/// Full scoring configuration grouped into logical categories.
/// </summary>
public sealed record ScoringConfigDto
{
    /// <summary>Per-ratio weights (must sum to 1.0).</summary>
    public required IReadOnlyList<ScoringConfigItemDto> RatioWeights { get; init; }

    /// <summary>Ok/warn/danger thresholds for each ratio.</summary>
    public required IReadOnlyList<ScoringConfigItemDto> RatioThresholds { get; init; }

    /// <summary>Component weights — ratios, BCRA, macro (must sum to 1.0).</summary>
    public required IReadOnlyList<ScoringConfigItemDto> ComponentWeights { get; init; }

    /// <summary>Score level thresholds (low &lt; medium &lt; high).</summary>
    public required IReadOnlyList<ScoringConfigItemDto> ScoreThresholds { get; init; }

    /// <summary>Macro-economic parameters (inflation threshold, penalty points).</summary>
    public required IReadOnlyList<ScoringConfigItemDto> Macro { get; init; }

    /// <summary>
    /// The 0/1 flags that switch the critical-floor rules (1 = the narrower rule, 0 = floor on any critical
    /// value): <c>critical_floor_profitability_requires_operating_loss</c> (ROE / ROA / margen neto floors only
    /// with an operating loss or interest that absorbs it) and
    /// <c>critical_floor_acid_test_requires_weak_current_ratio</c> (the prueba ácida floor only while liquidez
    /// corriente is below its warn threshold or was not read), in that order. Same item shape as the groups
    /// above; PUT validates each as 0 or 1.
    /// </summary>
    public required IReadOnlyList<ScoringConfigItemDto> FloorRules { get; init; }

    /// <summary>
    /// The persona humana evaluation parameters (the <c>pf_</c> keys), every key in a fixed order,
    /// each with its default, bounds, unit and Settings UI group.
    /// </summary>
    public required IReadOnlyList<PersonEvaluationConfigItemDto> PersonaHumana { get; init; }
}

/// <summary>
/// One persona humana evaluation parameter (a <c>pf_</c> key) as GET scoring-config returns it.
/// </summary>
public sealed record PersonEvaluationConfigItemDto
{
    /// <summary>Configuration key (e.g. pf_dscr_verde).</summary>
    public required string Key { get; init; }

    /// <summary>The effective value: the tenant's row when one exists, otherwise the code default.</summary>
    public required decimal Value { get; init; }

    /// <summary>The code default, which a reset brings the key back to.</summary>
    public required decimal DefaultValue { get; init; }

    /// <summary>True when the tenant has its own row for this key.</summary>
    public required bool IsOverride { get; init; }

    /// <summary>integer, flag (0/1), fraction (0-1) or ratio (the DSCR bands).</summary>
    public required string Kind { get; init; }

    /// <summary>Inclusive lower bound.</summary>
    public required decimal Min { get; init; }

    /// <summary>Inclusive upper bound.</summary>
    public required decimal Max { get; init; }

    /// <summary>meses, dias, miles_ars, fraccion, veces, or null when none applies.</summary>
    public string? Unit { get; init; }

    /// <summary>Settings UI section: elegibilidad, bcra, capacidad, solvencia, documentacion or decision.</summary>
    public required string Group { get; init; }

    /// <summary>Human-readable description of the parameter.</summary>
    public string? Description { get; init; }
}

/// <summary>
/// Payload for updating one or more scoring configuration parameters.
/// </summary>
public sealed record UpdateScoringConfigRequest
{
    /// <summary>Configuration items to create, update or (pf_ keys only) reset.</summary>
    public required IReadOnlyList<ScoringConfigUpdateItem> Items { get; init; }
}

/// <summary>
/// One item of PUT scoring-config: a key with its new value, or a <c>pf_</c> key with
/// <c>reset: true</c>, which deletes the tenant's row so the key goes back to its code default.
/// </summary>
public sealed record ScoringConfigUpdateItem
{
    /// <summary>Configuration key (e.g. weight_roe, pf_dscr_verde).</summary>
    public required string Key { get; init; }

    /// <summary>The new value. Required unless <see cref="Reset"/> is true, in which case it is ignored.</summary>
    public decimal? Value { get; init; }

    /// <summary>True to drop the tenant's row and fall back to the code default. pf_ keys only.</summary>
    public bool? Reset { get; init; }
}
