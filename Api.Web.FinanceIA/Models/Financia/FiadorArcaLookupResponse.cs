// Project : Api.Web.FinanceIA
// Created : 2026-10-23

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Response of GET /api/entities/{entityId}/fiadores/arca-lookup: what ARCA knows about a CUIT, to
/// prefill the manual fiador form. Nothing is created. PII — names a person; never logged.
/// </summary>
public sealed class FiadorArcaLookupResponse
{
    /// <summary>The CUIT, hyphenated as XX-XXXXXXXX-X.</summary>
    public required string Cuit { get; init; }

    /// <summary>
    /// "APELLIDO, NOMBRE" for a persona humana, the razón social otherwise; null when ARCA sent no name.
    /// </summary>
    public string? Denominacion { get; init; }

    /// <summary><see cref="PersonTypes.Humana"/> or <see cref="PersonTypes.Juridica"/>.</summary>
    public required string TipoPersona { get; init; }
}
