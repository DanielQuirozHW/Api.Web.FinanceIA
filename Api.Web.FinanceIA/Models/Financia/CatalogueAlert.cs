// Project : Api.Web.FinanceIA
// Created : 2026-08-16

using System.Text.Json.Serialization;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// One alert produced by the deterministic rule engine and persisted to
/// [dbo].[AnalysisResults].[CatalogueAlerts]. This is the <c>alerts</c> channel: same input,
/// same output, always.
///
/// The first four keys deliberately mirror the model's own alert element
/// (<c>level</c> / <c>code</c> / <c>title</c> / <c>description</c>) so a consumer can render both
/// channels with one component. The three metric keys are what the model channel cannot supply:
/// a catalogue alert knows the figure it measured and the threshold it was measured against.
/// </summary>
public sealed record CatalogueAlert
{
    /// <summary>Severity, lowercase: low | medium | high | critical. Taken from the catalogue row.</summary>
    [JsonPropertyName("level")]
    public string Level { get; init; } = string.Empty;

    /// <summary>The catalogue's closed rule identifier.</summary>
    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    /// <summary>es-AR title from the catalogue row.</summary>
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    /// <summary>es-AR description, with any template tokens already substituted.</summary>
    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Which figure the rule measured (e.g. <c>inventory_over_current_assets</c>), or null for a
    /// rule whose finding is structural rather than numeric.
    /// </summary>
    [JsonPropertyName("metric_key")]
    public string? MetricKey { get; init; }

    /// <summary>The measured value, or null when the rule measures nothing numeric.</summary>
    [JsonPropertyName("metric_value")]
    public decimal? MetricValue { get; init; }

    /// <summary>The threshold it was compared against, or null when the rule has no threshold.</summary>
    [JsonPropertyName("threshold_value")]
    public decimal? ThresholdValue { get; init; }

    /// <summary>
    /// Unit shared by <see cref="MetricValue"/> and <see cref="ThresholdValue"/> —
    /// <c>"proportion"</c> or <c>"absolute"</c> — or null when the rule measures nothing numeric.
    /// The two always share a unit because the engine compares them directly.
    ///
    /// On the wire so a consumer knows whether the figures may be rendered as a percentage, rather
    /// than keeping its own metric-key → unit registry: that knowledge belongs to the rule that
    /// produced the figure.
    ///
    /// OPTIONAL BY CONSTRUCTION. Rows written to [dbo].[AnalysisResults].[CatalogueAlerts] before
    /// this field existed simply omit the key and deserialize it as null.
    /// </summary>
    [JsonPropertyName("metric_unit")]
    public string? MetricUnit { get; init; }
}
