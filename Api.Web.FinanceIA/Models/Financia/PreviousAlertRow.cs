// Project : Api.Web.FinanceIA
// Created : 2026-10-25

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// A resolved event alert, projected for <c>reopenedAfter</c>: no Title or Description travels. Read by
/// <c>AlertRepository.GetPreviousByKeysAsync</c>.
/// </summary>
public sealed record PreviousAlertRow(
    Guid      AlertId,
    string    AlertType,
    string    MetricKey,
    DateTime  CreatedAt,
    DateTime? ResolvedAt,
    Guid?     ResolvedBy,
    decimal?  MetricValue);
