// Project : Api.Web.FinanceIA
// Created : 2026-05-10
// Modified: 2026-10-25

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// DTO for a single alert returned by the alerts endpoint.
/// </summary>
public sealed class AlertDto
{
    public Guid AlertId { get; init; }
    public string TenantId { get; init; } = string.Empty;
    public Guid? EntityId { get; init; }
    public Guid? AnalysisId { get; init; }
    /// <summary>
    /// PK of the related AnalysisResult, resolved from AnalysisId. The frontend
    /// navigates to the analysis detail page by ResultId (GET /api/analyses/{id}
    /// looks up by ResultId, not AnalysisId), so this is what the "Ver" action uses.
    /// Null when no AnalysisResult exists for the alert's AnalysisId.
    /// </summary>
    public Guid? ResultId { get; init; }
    public string AlertType { get; init; } = string.Empty;
    public string Severity { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string? MetricKey { get; init; }
    public decimal? MetricValue { get; init; }
    public decimal? ThresholdValue { get; init; }
    public bool IsRead { get; init; }
    public bool IsResolved { get; init; }
    public DateTime? ResolvedAt { get; init; }
    public string EntityName { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }

    // ── Structured fields for the Alertas page (additive; every field above is unchanged) ──

    /// <summary>The entity's CUIT, from the same lookup as <see cref="EntityName"/>; null without an entity.</summary>
    public string? EntityCuit { get; init; }

    /// <summary>
    /// Whether the entity is a persona humana (Entities.PersonType = 'Humana', DDL 069); null without an entity.
    /// </summary>
    public bool? EntityIsPersonaHumana { get; init; }

    /// <summary>The user who resolved the alert (Alerts.ResolvedBy); null when open or resolved by the system.</summary>
    public Guid? ResolvedByUserId { get; init; }

    /// <summary>That user's DisplayName; null when there is none or the user is not found in this tenant.</summary>
    public string? ResolvedByName { get; init; }

    /// <summary>Resolved by the system rather than a user: IsResolved and no ResolvedBy.</summary>
    public bool IsAutoResolved { get; init; }

    /// <summary>The cupo of a CUPO_* event alert, read from its MetricKey; null for every other type.</summary>
    public Guid? CreditLineId { get; init; }

    /// <summary>
    /// The figures of an OPEN event alert as the reconcile pass of THIS request computed them. Null for
    /// resolved and non-event alerts, on GET ?entityId= (no pass runs there), and whenever the pass was
    /// skipped or failed — the Portal then falls back to <see cref="Description"/>.
    /// </summary>
    public AlertFactsDto? Facts { get; init; }

    /// <summary>
    /// On an OPEN event alert whose previous alert of the same key was resolved by a user: that dismissal.
    /// Null otherwise.
    /// </summary>
    public AlertReopenedAfterDto? ReopenedAfter { get; init; }
}

/// <summary>
/// Response envelope for the alerts listing endpoint.
/// Includes severity counts for dashboard summary display.
/// </summary>
public sealed class AlertsResponseDto
{
    public List<AlertDto> Alerts { get; init; } = new();
    public int TotalCount { get; init; }
    public int CriticalCount { get; init; }
    public int HighCount { get; init; }
    public int MediumCount { get; init; }
    public int LowCount { get; init; }
    public int ResolvedCount { get; init; }
}
