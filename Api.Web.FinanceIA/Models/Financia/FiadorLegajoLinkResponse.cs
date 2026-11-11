// Project : Api.Web.FinanceIA
// Created : 2026-10-29

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Response of POST api/entities/{entityId}/fiadores/{fiadorId}/legajo: the legajo the fiador is now
/// linked to. Carries ids only — never the person's name or CUIT.
/// </summary>
public sealed class FiadorLegajoLinkResponse
{
    /// <summary>The fiador's legajo in [dbo].[PersonLegajos].</summary>
    public Guid PersonLegajoId { get; set; }

    /// <summary>
    /// True when this call created the legajo; false when it linked one the tenant already had (the
    /// fiador is also a client, or a fiador of another PyME) or the fiador was already linked.
    /// </summary>
    public bool Created { get; set; }
}
