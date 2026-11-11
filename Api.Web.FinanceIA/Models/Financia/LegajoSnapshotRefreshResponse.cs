// Project : Api.Web.FinanceIA
// Created : 2026-10-29

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Response of POST api/person-legajos/{legajoId}/refresh-bcra and /refresh-arca: what the refresh left
/// on the legajo. Carries no snapshot, name or CUIT; the snapshots themselves are read through the
/// legajo's evaluation, never echoed here.
/// </summary>
public sealed class LegajoSnapshotRefreshResponse
{
    /// <summary>The refreshed legajo.</summary>
    public Guid PersonLegajoId { get; set; }

    /// <summary>The entity refreshed with it, when the legajo is an entity's; null for a fiador's legajo.</summary>
    public Guid? EntityId { get; set; }

    /// <summary>The legajo's fiscal regime after the refresh (FiscalRegimes).</summary>
    public string FiscalRegime { get; set; } = FiscalRegimes.Desconocido;

    /// <summary>The monotributo category after the refresh, when the regime is Monotributo.</summary>
    public string? MonotributoCategory { get; set; }

    /// <summary>UTC time of the stored BCRA snapshot, or null when the legajo has none.</summary>
    public DateTime? BcraDataUpdatedAt { get; set; }

    /// <summary>UTC time of the stored ARCA snapshot, or null when the legajo has none.</summary>
    public DateTime? ArcaDataUpdatedAt { get; set; }
}
