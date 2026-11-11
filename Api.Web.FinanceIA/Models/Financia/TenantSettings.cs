// Project : Api.Web.FinanceIA
// Created : 2026-08-03

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// EF Core entity that maps to [dbo].[TenantSettings].
/// Per-tenant product-behaviour configuration as free-form key/value pairs
/// (booleans, strings or JSON). Distinct from [dbo].[ScoringConfig], which holds
/// numeric parameters for the scoring engine. There is exactly one row per
/// (TenantId, SettingKey). Defaults live in code — a tenant with no row must
/// still resolve to a sensible default (see <see cref="TenantSettingKeys"/>).
/// </summary>
public sealed class TenantSettings
{
    /// <summary>Primary key (database-assigned via NEWSEQUENTIALID()).</summary>
    public Guid SettingId { get; set; }

    /// <summary>Firebase tenant slug that owns this setting.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Setting key, e.g. <c>creditline.mode</c>.</summary>
    public string SettingKey { get; set; } = string.Empty;

    /// <summary>Setting value as a string; may hold a boolean, plain string or JSON.</summary>
    public string SettingValue { get; set; } = string.Empty;

    /// <summary>Human-readable description of what the setting controls.</summary>
    public string? Description { get; set; }

    /// <summary>UTC timestamp of the last update.</summary>
    public DateTime UpdatedAt { get; set; }
}
