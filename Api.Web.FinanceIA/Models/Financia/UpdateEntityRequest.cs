// Project : Api.Web.FinanceIA
// Created : 2026-05-04
// Modified: 2026-05-04

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Request body for PUT /api/entities/{id}.
/// </summary>
public sealed class UpdateEntityRequest
{
    /// <summary>Argentine tax identification number. Format: DD-DDDDDDDD-D.</summary>
    public string CUIT { get; set; } = string.Empty;

    /// <summary>Legal business name.</summary>
    public string BusinessName { get; set; } = string.Empty;

    /// <summary>Commercial trade name, if different from the legal business name.</summary>
    public string? TradeName { get; set; }

    /// <summary>Industry sector.</summary>
    public string? Sector { get; set; }

    /// <summary>MiPyME category (e.g., Micro, Pequeña, Mediana).</summary>
    public string? MipymeCategory { get; set; }

    /// <summary>Linked SGR OneClick entity identifier, if applicable.</summary>
    public string? SgrOneClickEntityId { get; set; }

    /// <summary>Whether the entity should be actively monitored for alerts.</summary>
    public bool IsMonitored { get; set; }
}
