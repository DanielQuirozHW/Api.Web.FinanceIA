// Project : Api.Web.FinanceIA
// Created : 2026-08-28

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// One candidate fiador for a PyME, projected from the entity's PERSISTED LUFE blob
/// (<c>LufeData.Autoridades[]</c>), as returned by
/// <c>GET /api/entities/{entityId}/fiadores/candidates</c>. Nothing here is fetched from LUFE at
/// request time — the endpoint reads the snapshot already stored on the entity.
///
/// <para>
/// PII — a candidate names a natural person (<see cref="Cuit"/>, <see cref="Denominacion"/>).
/// Same rule as <see cref="FiadorResponse"/>: never serialized wholesale into a Claude prompt.
/// </para>
/// </summary>
public sealed class FiadorCandidateResponse
{
    /// <summary>
    /// The candidate's CUIT, hyphenated as XX-XXXXXXXX-X. LUFE informs it as a JSON number; it is
    /// formatted here into the same string form [dbo].[Entities].CUIT and [dbo].[Fiadores].CUIT
    /// store, so the client can post it straight back to the add endpoint.
    /// </summary>
    public string Cuit { get; set; } = string.Empty;

    /// <summary>Name as LUFE informed it, or null when it did not.</summary>
    public string? Denominacion { get; set; }

    /// <summary>Role within the PyME (socio, presidente, …), or null when LUFE did not inform it.</summary>
    public string? Cargo { get; set; }

    /// <summary>Shareholding percentage, or null when LUFE did not inform it. 0 is a real value.</summary>
    public decimal? PorcAccionista { get; set; }

    /// <summary>LUFE's shareholder flag, or null when it did not inform it.</summary>
    public int? EsAccionista { get; set; }

    /// <summary>
    /// True when this candidate is ALREADY an Active fiador of the PyME, so the Portal can disable
    /// the row instead of offering an add that would come back 409.
    /// </summary>
    public bool AlreadyAdded { get; set; }
}
