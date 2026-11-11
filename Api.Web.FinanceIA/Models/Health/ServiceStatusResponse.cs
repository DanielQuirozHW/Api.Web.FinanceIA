// Project : Api.Web.FinanceIA
// Created : 2026-06-07

namespace Api.Web.FinanceIA.Models.Health;

public sealed record ServiceStatusResponse(
    ClaudePlatformStatus ClaudePlatform,
    DatabaseStatus Database,
    LastAiCallStatus? LastAiCall);

public sealed record ClaudePlatformStatus(
    string Indicator,
    string Description,
    List<AffectedComponent> AffectedComponents,
    List<ActiveIncident> ActiveIncidents);

public sealed record AffectedComponent(string Name, string Status);

public sealed record ActiveIncident(
    string Name,
    string Impact,
    string? LatestUpdate,
    DateTime? UpdatedAt);

public sealed record DatabaseStatus(string Status, DateTime CheckedAt);

public sealed record LastAiCallStatus(
    string Status,
    DateTime OccurredAt,
    string? Operation,
    string? Model);
