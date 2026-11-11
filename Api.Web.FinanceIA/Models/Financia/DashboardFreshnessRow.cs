// Project : Api.Web.FinanceIA
// Created : 2026-10-23

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>How old one source snapshot is across the entities with an Active cupo, counted in SQL.</summary>
/// <param name="Source">A <see cref="RescoreSource"/> code: ARCA, LUFE, BCRA, CASFOG or NOSIS.</param>
/// <param name="Under30Days">*DataUpdatedAt later than the 30-day cutoff.</param>
/// <param name="Days30To90">Between the 90-day and the 30-day cutoffs, both inclusive.</param>
/// <param name="Over90Days">Earlier than the 90-day cutoff.</param>
/// <param name="Never">NULL.</param>
public sealed record DashboardFreshnessRow(string Source, int Under30Days, int Days30To90, int Over90Days, int Never);
