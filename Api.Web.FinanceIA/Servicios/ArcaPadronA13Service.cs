// Project : Api.Web.FinanceIA
// Created : 2026-05-12

using System.Diagnostics;
using System.Security;
using System.Text;
using System.Xml.Linq;
using Api.Web.FinanceIA.Models.Arca;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// SOAP client for ARCA Padron A13 web service (ws_sr_padron_a13).
/// Queries entity data by CUIT or DNI. All SOAP XML is built and parsed manually
/// to avoid WCF/WSDL tooling dependencies.
/// </summary>
public sealed class ArcaPadronA13Service : IArcaPadronService
{
    //private const string ServiceName = "ws_sr_constancia_inscripcion";

    private static readonly XNamespace SoapNs =
        "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace A13Ns =
        "http://a13.soap.ws.server.puc.sr/";

    private readonly HttpClient                        _httpClient;
    private readonly ILogger<ArcaPadronA13Service>     _logger;
    private readonly IArcaWsaaService                  _wsaaService;

    // Nombre del servicio AFIP para solicitar tickets WSAA
    private const string ServiceName = "ws_sr_padron_a13";

    public ArcaPadronA13Service(
        HttpClient                    httpClient,
        ILogger<ArcaPadronA13Service> logger,
        IArcaWsaaService              wsaaService)
    {
        _httpClient  = httpClient;
        _logger      = logger;
        _wsaaService = wsaaService;
    }

    /// <inheritdoc />
    public async Task<ArcaPersonaData?> GetPersonaByCuitAsync(
        string cuit,
        string cuitRepresentada,
        CancellationToken cancellationToken = default)
    {
        ValidateCuit(cuit);

        var ticket = await _wsaaService
            .GetTicketAsync(ServiceName, cancellationToken)
            .ConfigureAwait(false);

        var soapRequest = BuildGetPersonaRequest(cuit, cuitRepresentada, ticket);

        var sw = Stopwatch.StartNew();
        var responseXml = await CallSoapAsync(soapRequest, "getPersona", cancellationToken)
            .ConfigureAwait(false);
        sw.Stop();

        _logger.LogDebug("ARCA getPersona completed in {DurationMs}ms", sw.ElapsedMilliseconds);

        return ParseGetPersonaResponse(responseXml);
    }

    /// <inheritdoc />
    public async Task<List<string>> GetCuitsByDniAsync(
        string dni,
        string cuitRepresentada,
        CancellationToken cancellationToken = default)
    {
        var ticket = await _wsaaService
            .GetTicketAsync(ServiceName, cancellationToken)
            .ConfigureAwait(false);

        var soapRequest = BuildGetIdPersonaListByDocumentoRequest(dni, cuitRepresentada, ticket);

        var responseXml = await CallSoapAsync(soapRequest, "getIdPersonaListByDocumento", cancellationToken)
            .ConfigureAwait(false);

        return ParseGetIdPersonaListResponse(responseXml);
    }

    // ── SOAP request builders ────────────────────────────────────────────────────

    private static string BuildGetPersonaRequest(
        string cuit,
        string cuitRepresentada,
        WsaaTicket ticket)
    {
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/"
                              xmlns:a13="http://a13.soap.ws.server.puc.sr/">
              <soapenv:Header/>
              <soapenv:Body>
                <a13:getPersona>
                  <token>{SecurityElement.Escape(ticket.Token)}</token>
                  <sign>{SecurityElement.Escape(ticket.Sign)}</sign>
                  <cuitRepresentada>{SecurityElement.Escape(cuitRepresentada)}</cuitRepresentada>
                  <idPersona>{SecurityElement.Escape(cuit)}</idPersona>
                </a13:getPersona>
              </soapenv:Body>
            </soapenv:Envelope>
            """;
    }

    private static string BuildGetIdPersonaListByDocumentoRequest(
        string dni,
        string cuitRepresentada,
        WsaaTicket ticket)
    {
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/"
                              xmlns:a13="http://a13.soap.ws.server.puc.sr/">
              <soapenv:Header/>
              <soapenv:Body>
                <a13:getIdPersonaListByDocumento>
                  <token>{SecurityElement.Escape(ticket.Token)}</token>
                  <sign>{SecurityElement.Escape(ticket.Sign)}</sign>
                  <cuitRepresentada>{SecurityElement.Escape(cuitRepresentada)}</cuitRepresentada>
                  <documento>{SecurityElement.Escape(dni)}</documento>
                </a13:getIdPersonaListByDocumento>
              </soapenv:Body>
            </soapenv:Envelope>
            """;
    }

    // ── SOAP transport ───────────────────────────────────────────────────────────

    private async Task<string> CallSoapAsync(
        string soapRequest,
        string method,
        CancellationToken cancellationToken)
    {
        var content  = new StringContent(soapRequest, Encoding.UTF8, "text/xml");
        var response = await _httpClient
            .PostAsync("", content, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            
            _logger.LogError(
                "ARCA Padron A13 {Method} failed with status {StatusCode}. Response: {Response}",
                method, response.StatusCode, errorBody);

            throw new HttpRequestException(
                $"ARCA Padron A13 {method} returned {response.StatusCode}: {errorBody}");
        }

        return await response.Content
            .ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    // ── XML response parsers ─────────────────────────────────────────────────────

    private static ArcaPersonaData? ParseGetPersonaResponse(string xml)
    {
        var doc = XDocument.Parse(xml);

        // Navigate: Envelope > Body > getPersonaResponse > personaReturn > persona
        var persona = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "persona");

        if (persona is null)
        {
            // Check for fault/error
            var fault = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Fault");
            if (fault is not null) return null;

            return null;
        }

        var domicilios = persona.Elements()
            .Where(e => e.Name.LocalName == "domicilio")
            .Select(ParseDomicilio)
            .ToList();

        return new ArcaPersonaData(
            Cuit:                         GetElementValue(persona, "idPersona") ?? string.Empty,
            TipoPersona:                  GetElementValue(persona, "tipoPersona"),
            EstadoClave:                  GetElementValue(persona, "estadoClave"),
            Nombre:                       GetElementValue(persona, "nombre"),
            Apellido:                     GetElementValue(persona, "apellido"),
            RazonSocial:                  GetElementValue(persona, "razonSocial"),
            TipoDocumento:                GetElementValue(persona, "tipoDocumento"),
            NumeroDocumento:              GetElementValue(persona, "numeroDocumento"),
            FechaNacimiento:              ParseDate(GetElementValue(persona, "fechaNacimiento")),
            MesCierre:                    ParseInt(GetElementValue(persona, "mesCierre")),
            IdActividadPrincipal:         GetElementValue(persona, "idActividadPrincipal"),
            DescripcionActividadPrincipal:GetElementValue(persona, "descripcionActividadPrincipal"),
            PeriodoActividadPrincipal:    GetElementValue(persona, "periodoActividadPrincipal"),
            FormaJuridica:                GetElementValue(persona, "formaJuridica"),
            DomicilioFiscal:              domicilios.FirstOrDefault(d => d.TipoDomicilio == "FISCAL"),
            DomicilioLegal:               domicilios.FirstOrDefault(d =>
                d.TipoDomicilio is "LEGAL/REAL" or "LEGAL")
        );
    }

    private static List<string> ParseGetIdPersonaListResponse(string xml)
    {
        var doc = XDocument.Parse(xml);

        return doc.Descendants()
            .Where(e => e.Name.LocalName == "idPersona")
            .Select(e => e.Value.Trim())
            .Where(v => v.Length == 11 && v.All(char.IsDigit))
            .ToList();
    }

    // ── XML helpers ──────────────────────────────────────────────────────────────

    private static ArcaDomicilio ParseDomicilio(XElement el) => new(
        TipoDomicilio:      GetElementValue(el, "tipoDomicilio") ?? string.Empty,
        Direccion:          GetElementValue(el, "direccion"),
        Calle:              GetElementValue(el, "calle"),
        Numero:             GetElementValue(el, "numero"),
        Localidad:          GetElementValue(el, "localidad"),
        CodigoPostal:       GetElementValue(el, "codigoPostal"),
        IdProvincia:        ParseInt(GetElementValue(el, "idProvincia")),
        DescripcionProvincia: GetElementValue(el, "descripcionProvincia"),
        EstadoDomicilio:    GetElementValue(el, "estadoDomicilio")
    );

    private static string? GetElementValue(XElement parent, string localName)
    {
        var el = parent.Elements()
            .FirstOrDefault(e => e.Name.LocalName == localName);
        return string.IsNullOrWhiteSpace(el?.Value) ? null : el.Value.Trim();
    }

    private static int? ParseInt(string? value)
        => int.TryParse(value, out var result) ? result : null;

    private static DateTime? ParseDate(string? value)
        => DateTime.TryParse(value, out var result) ? result : null;

    // ── Validation ───────────────────────────────────────────────────────────────

    private static void ValidateCuit(string cuit)
    {
        var normalized = cuit.Replace("-", "");
        if (normalized.Length != 11 || !normalized.All(char.IsDigit))
        {
            throw new ArgumentException("CUIT must be 11 digits", nameof(cuit));
        }
    }
}
