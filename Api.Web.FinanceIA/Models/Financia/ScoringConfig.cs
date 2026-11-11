// Project : Api.Web.FinanceIA
// Created : 2026-05-08

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// EF Core entity that maps to [dbo].[ScoringConfig].
/// Stores per-tenant configuration parameters for the deterministic credit-scoring engine.
/// </summary>
public sealed class ScoringConfig
{
    /// <summary>Primary key.</summary>
    public Guid ConfigId { get; set; }

    /// <summary>Firebase tenant slug that owns this configuration entry.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Configuration key (e.g. weight_liquidez_corriente).</summary>
    public string ConfigKey { get; set; } = string.Empty;

    /// <summary>Configuration value.</summary>
    public decimal ConfigValue { get; set; }

    /// <summary>Human-readable description of what the parameter controls.</summary>
    public string? Description { get; set; }

    /// <summary>UTC timestamp of the last update.</summary>
    public DateTime UpdatedAt { get; set; }
}
