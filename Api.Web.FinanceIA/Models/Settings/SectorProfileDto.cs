// Project : Api.Web.FinanceIA
// Created : 2026-06-05

namespace Api.Web.FinanceIA.Models.Settings;

/// <summary>
/// Summary item for the sector list endpoint.
/// </summary>
public sealed record SectorListItemDto
{
    /// <summary>Canonical sector name.</summary>
    public required string Sector { get; init; }

    /// <summary>True if the tenant has at least one threshold override for this sector.</summary>
    public required bool HasOverrides { get; init; }
}

/// <summary>
/// Full threshold profile for a sector, showing effective values after merging defaults + sector overrides.
/// </summary>
public sealed record SectorProfileDto
{
    /// <summary>Canonical sector name.</summary>
    public required string Sector { get; init; }

    /// <summary>The 24 ratio threshold items with their effective values.</summary>
    public required IReadOnlyList<ScoringConfigItemDto> Thresholds { get; init; }
}

/// <summary>
/// Payload for updating sector threshold overrides.
/// </summary>
public sealed record UpdateSectorProfileRequest
{
    /// <summary>Threshold items to create or update for this sector.</summary>
    public required IReadOnlyList<ScoringConfigItemDto> Thresholds { get; init; }
}
