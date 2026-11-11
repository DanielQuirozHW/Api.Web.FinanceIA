// Project : Api.Web.FinanceIA
// Created : 2026-10-25

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// On an OPEN event alert: the previous alert of the same (AlertType, MetricKey), which a USER resolved
/// before the situation got worse and the alert came back.
/// </summary>
public sealed record AlertReopenedAfterDto
{
    /// <summary>When the user resolved the previous alert.</summary>
    public DateTime? ResolvedAt { get; init; }

    /// <summary>Their DisplayName; null when the user is not found in this tenant.</summary>
    public string? ResolvedByName { get; init; }

    /// <summary>The previous alert's MetricValue, what the situation was when it was dismissed.</summary>
    public decimal? MetricValue { get; init; }
}
