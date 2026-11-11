// Project : Api.Web.FinanceIA
// Created : 2026-05-04
// Modified: 2026-05-04

using Api.Web.FinanceIA.Models.Arca;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Request body for POST /api/entities.
/// </summary>
public sealed class CreateEntityRequest
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

    /// <summary>Full ARCA fiscal data captured at lookup time. Serialized to FiscalDataJson on create.</summary>
    public ArcaFiscalData? FiscalData { get; set; }

    /// <summary>
    /// Optional "Juridica" or "Humana" (any case), typically lookup-arca's <c>personType</c>. Anything
    /// else is a 400. Advisory only when the CUIT prefix is unambiguous (20/23/24/27 or 30/33/34): the
    /// prefix wins and a disagreement is logged. Omitted, the type comes from the prefix alone.
    /// </summary>
    public string? PersonType { get; set; }
}
