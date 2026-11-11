// Project : Api.Web.FinanceIA
// Created : 2026-05-12

using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Api.Web.FinanceIA.Models.Arca;

/// <summary>
/// Request to lookup entity data from ARCA by CUIT.
/// </summary>
public sealed record ArcaLookupByCuitRequest
{
    [Required(ErrorMessage = "CUIT es requerido")]
    [RegularExpression(@"^\d{2}-?\d{8}-?\d{1}$", ErrorMessage = "Formato de CUIT invalido (XX-XXXXXXXX-X)")]
    public required string Cuit { get; init; }
}

/// <summary>
/// Request to lookup CUIT(s) by DNI from ARCA.
/// </summary>
public sealed record ArcaLookupByDniRequest
{
    [Required(ErrorMessage = "DNI es requerido")]
    [RegularExpression(@"^\d{7,8}$", ErrorMessage = "Formato de DNI invalido")]
    public required string Dni { get; init; }
}

/// <summary>
/// Entity data returned from ARCA Padron A13 lookup.
/// </summary>
public sealed record ArcaLookupResponse
{
    public required string Cuit { get; init; }
    public required string RazonSocial { get; init; }
    public string? TipoPersona { get; init; }
    public string? EstadoClave { get; init; }
    public ArcaDomicilioFiscalDto? DomicilioFiscal { get; init; }
    [JsonPropertyName("sectorSugerido")]
    public string? Sector { get; init; }

    [JsonPropertyName("mipymeCategoria")]
    public string? MipymeCategoria { get; init; }

    [JsonPropertyName("nombreFantasia")]
    public string? NombreFantasia { get; init; }   // always null for now; ARCA does not supply it

    public string? ActividadPrincipal { get; init; }
    public string? CodigoCiiu { get; init; }
    public int? MesCierre { get; init; }
    public string? FormaJuridica { get; init; }
    public ArcaFiscalData? Fiscal { get; init; }

    /// <summary>
    /// Resolved person type, "Juridica" or "Humana" (see <c>CuitHelper.ResolvePersonType</c>): the
    /// CUIT prefix when it is unambiguous, otherwise ARCA's <see cref="TipoPersona"/>. Unlike
    /// <see cref="TipoPersona"/>, which is ARCA's raw text, this is the value POST /api/entities
    /// accepts back as <c>personType</c>.
    /// </summary>
    public required string PersonType { get; init; }
}

/// <summary>
/// Raw data returned by ARCA Padron A13 getPersona method.
/// </summary>
public sealed record ArcaPersonaData(
    string Cuit,
    string? TipoPersona,
    string? EstadoClave,
    string? Nombre,
    string? Apellido,
    string? RazonSocial,
    string? TipoDocumento,
    string? NumeroDocumento,
    DateTime? FechaNacimiento,
    int? MesCierre,
    string? IdActividadPrincipal,
    string? DescripcionActividadPrincipal,
    string? PeriodoActividadPrincipal,
    string? FormaJuridica,
    ArcaDomicilio? DomicilioFiscal,
    ArcaDomicilio? DomicilioLegal,
    ArcaFiscalData? FiscalData = null,
    // True when getPersona_v2 answered with datosGenerales AND an error section beside it
    // (errorConstancia / errorRegimenGeneral / errorMonotributo): the data is partial. Internal to
    // the API — never shipped — so refresh-arca can refuse to downgrade a regime it already knows.
    bool IsIncomplete = false
);

/// <summary>
/// Full fiscal data snapshot from ARCA ws_sr_constancia_inscripcion.
/// Not used by the company scoring engine. The persona humana evaluation reads it from the legajo
/// (PersonLegajo.ArcaSnapshotJson): sucesión, key status, activity start, monotributo category.
/// </summary>
public sealed record ArcaFiscalData(
    DateTime? FechaContratoSocial,
    bool? EsSucesion,
    int? MesCierre,
    IReadOnlyList<ArcaActividad> Actividades,
    IReadOnlyList<ArcaImpuesto> Impuestos,
    IReadOnlyList<ArcaRegimen> Regimenes,
    ArcaDomicilioFiscalDto? DomicilioFiscal = null,
    // Appended with defaults (DDL 069 / M10) so the one named-argument construction site and every
    // FiscalDataJson already stored keep working: System.Text.Json fills a missing constructor
    // parameter with its default. Both are derived by ArcaConstanciaService.ResolveFiscalRegime.
    // FiscalRegime is one of FiscalRegimes.Known; MonotributoCategoria is a single letter A–K or null.
    string? FiscalRegime = null,
    string? MonotributoCategoria = null,
    // Appended with defaults (M10, persona humana ARCA gaps), for the same reason as the two above.
    // EstadoClave is datosGenerales/estadoClave as ARCA sent it (trimmed, raw text), and
    // EstadoClaveConsultadoAt the UTC instant it was read: stamped by the controller that called ARCA
    // (lookup-arca, refresh-arca), not by the parser. A refresh that brings the key status alone
    // updates these two and nothing else in the snapshot, so they carry their own date.
    string? EstadoClave = null,
    DateTime? EstadoClaveConsultadoAt = null,
    // The first day of the earliest parsable periodo of the actividades, impuestos and
    // categoriaMonotributo of BOTH sections (see ArcaConstanciaService.EarliestActivityPeriod).
    // Regímenes are not counted. PROVISIONAL: ARCA has no inscription date in getPersona_v2.
    DateTime? FechaInicioActividad = null
);

public sealed record ArcaActividad(
    string? IdActividad,
    string? DescripcionActividad,
    string? Nomenclador,
    int? Orden,
    string? Periodo
);

public sealed record ArcaImpuesto(
    string? IdImpuesto,
    string? DescripcionImpuesto,
    string? EstadoImpuesto,
    string? Motivo,
    string? Periodo
);

public sealed record ArcaRegimen(
    string? IdRegimen,
    string? DescripcionRegimen,
    string? IdImpuesto,
    string? TipoRegimen,
    string? Periodo
);

/// <summary>
/// Fiscal address returned in the ARCA lookup response.
/// Matches the Portal PadronDomicilioFiscal contract.
/// </summary>
public sealed record ArcaDomicilioFiscalDto(
    string?  CodPostal,
    string?  Direccion,
    string?  Localidad,
    string?  DescripcionProvincia,
    int?     IdProvincia,
    string?  TipoDomicilio
);

/// <summary>
/// Domicilio structure from ARCA Padron A13.
/// </summary>
public sealed record ArcaDomicilio(
    string TipoDomicilio,
    string? Direccion,
    string? Calle,
    string? Numero,
    string? Localidad,
    string? CodigoPostal,
    int? IdProvincia,
    string? DescripcionProvincia,
    string? EstadoDomicilio
);

/// <summary>
/// WSAA authentication ticket (Token + Sign) with expiration.
/// </summary>
public sealed record WsaaTicket(
    string Token,
    string Sign,
    DateTime ExpirationTime
);
