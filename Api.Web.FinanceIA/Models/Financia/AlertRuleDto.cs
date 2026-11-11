// Project : Api.Web.FinanceIA
// Created : 2026-08-16

using Api.Web.FinanceIA.Clases.Json;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// DTO for a single catalogue rule returned by the alert-rules endpoint.
/// Carries the whole row: the configuration screen renders <see cref="DisplayNameEs"/> and
/// <see cref="DescriptionTemplateEs"/> alongside the three fields it can actually change.
/// </summary>
public sealed class AlertRuleDto
{
    public Guid RuleId { get; init; }
    public string Code { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public string Severity { get; init; } = string.Empty;
    public decimal? ThresholdValue { get; init; }

    /// <summary>
    /// Unit <see cref="ThresholdValue"/> is read in — <c>"proportion"</c> or <c>"absolute"</c> —
    /// or null for a rule that takes no threshold.
    ///
    /// On the wire so the configuration screen does not have to rediscover it by trial and error
    /// against a 400: it can label the input, convert safely in either direction, and warn that
    /// typing 55 instead of 0,55 makes the rule unfireable. Null is the same signal the PATCH guard
    /// uses, so the screen can disable the input rather than offer one the API will reject.
    /// </summary>
    public string? ThresholdUnit { get; init; }

    public string DisplayNameEs { get; init; } = string.Empty;
    public string DescriptionTemplateEs { get; init; } = string.Empty;

    /// <summary>
    /// False when no engine rule implements <see cref="Code"/>. Such a rule cannot be enabled —
    /// the screen surfaces it so a row that can never fire is visibly inert rather than looking
    /// like an ordinary toggle that silently does nothing.
    /// </summary>
    public bool Implemented { get; init; }

    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public Guid? UpdatedBy { get; init; }

    /// <summary>
    /// Display name of <see cref="UpdatedBy"/>, or null when the rule has never been changed
    /// through the API (the seeded rows) or the user no longer resolves.
    ///
    /// On the wire because the raw id tells the analyst nothing: a threshold change alters which
    /// alerts fire and therefore what the SGR decides on, so "quién" has to be legible on the
    /// screen rather than a GUID. Resolved the same way ReviewedByName is on
    /// AnalysisResultResponse — Users.DisplayName, verbatim, with no fallback string.
    /// </summary>
    public string? UpdatedByName { get; init; }
}

/// <summary>
/// Response envelope for the alert-rules listing endpoint.
/// </summary>
public sealed class AlertRulesResponseDto
{
    public List<AlertRuleDto> Rules { get; init; } = new();
    public int TotalCount { get; init; }
    public int EnabledCount { get; init; }
}

/// <summary>
/// PATCH body for a catalogue rule. Every field is a <see cref="Patch{T}"/>, so an omitted field
/// is left unchanged and is distinguishable from an explicit null — a rule's threshold can
/// legitimately be cleared, and <c>"thresholdValue": null</c> is how that is expressed.
///
/// Only these three fields are patchable. <c>Code</c> in particular is immutable through the API:
/// the code set is closed because a code the engine does not implement would be a configurable
/// rule that silently never fires.
/// </summary>
public sealed class UpdateAlertRuleRequest
{
    public Patch<bool> Enabled { get; init; }
    public Patch<string?> Severity { get; init; }
    public Patch<decimal?> ThresholdValue { get; init; }
}
