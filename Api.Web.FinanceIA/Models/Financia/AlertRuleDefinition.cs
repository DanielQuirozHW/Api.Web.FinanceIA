// Project : Api.Web.FinanceIA
// Created : 2026-08-16

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// EF Core entity that maps to [dbo].[AlertRuleCatalogue].
/// One per-tenant deterministic alert rule: the closed identifier the model's free-form
/// <c>code</c> never had, plus the severity and threshold an analyst controls.
///
/// A row is inert until <see cref="Enabled"/> is true — every seeded rule ships disabled.
/// <see cref="ThresholdValue"/> is null for rules that have no threshold (a rule either has
/// one or it does not; it is never zero-as-absent).
/// </summary>
public sealed class AlertRuleDefinition
{
    /// <summary>Primary key.</summary>
    public Guid RuleId { get; set; }

    /// <summary>Firebase tenant slug that owns this rule.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Closed rule identifier (e.g. PASIVO_100_CORRIENTE). Unique per tenant. The rule engine
    /// dispatches on this value; a code it does not implement is ignored, never guessed at.
    /// </summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Whether the engine evaluates this rule. Seeded rules start disabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Severity in the model's own lowercase vocabulary: low | medium | high | critical.</summary>
    public string Severity { get; set; } = string.Empty;

    /// <summary>
    /// Tunable threshold, or null when the rule has none. Its unit is rule-specific and is
    /// documented on the seed row's description (e.g. a proportion of current assets).
    /// </summary>
    public decimal? ThresholdValue { get; set; }

    /// <summary>Short es-AR title shown to the analyst.</summary>
    public string DisplayNameEs { get; set; } = string.Empty;

    /// <summary>
    /// es-AR description template. May contain the tokens <c>{valor}</c> and <c>{umbral}</c>,
    /// substituted with es-AR formatted figures when the rule fires.
    /// </summary>
    public string DescriptionTemplateEs { get; set; } = string.Empty;

    /// <summary>UTC timestamp of row creation.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>UTC timestamp of the last update. Audit trail for threshold changes.</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// User who last changed this rule, or null for the seeded rows. A threshold change alters
    /// which alerts fire, so who moved it is recorded.
    /// </summary>
    public Guid? UpdatedBy { get; set; }
}
