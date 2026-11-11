// Project : Api.Web.FinanceIA

using Api.Web.FinanceIA.Models.Arca;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// Client for ARCA ws_sr_constancia_inscripcion (getPersona_v2).
/// Supports persona lookup by CUIT only (no DNI lookup).
/// </summary>
public interface IArcaConstanciaService
{
    /// <summary>Looks up <paramref name="cuit"/> in ARCA's constancia de inscripción.</summary>
    /// <returns>
    /// Null only when ARCA reports no person: its no-such-person SOAP Fault
    /// (<see cref="Clases.ArcaFault.IsPersonNotFound"/>, whatever the HTTP status), a
    /// <c>personaNoRegistrada</c> answer, or no <c>datosGenerales</c>. An answer that carries error
    /// sections (<c>errorConstancia</c> / <c>errorRegimenGeneral</c> / <c>errorMonotributo</c>) beside
    /// <c>datosGenerales</c> is returned — not nulled — with <see cref="ArcaPersonaData.IsIncomplete"/>
    /// set, and its <see cref="ArcaPersonaData.FiscalData"/> may then be null or partial.
    /// </returns>
    /// <exception cref="HttpRequestException">
    /// ARCA answered with a non-success HTTP status other than the no-such-person fault, with any other
    /// SOAP Fault, or without a <c>personaReturn</c>.
    /// </exception>
    Task<ArcaPersonaData?> GetPersonaByCuitAsync(
        string cuit,
        string cuitRepresentada,
        CancellationToken cancellationToken = default);
}
