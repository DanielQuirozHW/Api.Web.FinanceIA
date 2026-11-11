// Project : Api.Web.FinanceIA

using System.Diagnostics;
using System.Security;
using System.Text;
using System.Xml.Linq;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Models.Arca;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// SOAP client for ARCA ws_sr_constancia_inscripcion (getPersona_v2).
/// Queries entity data by CUIT only. All SOAP XML is built and parsed manually
/// to avoid WCF/WSDL tooling dependencies.
/// </summary>
/// <remarks>
/// Besides the raw data, the parser derives the fiscal regime and the monotributo category
/// (<see cref="ResolveFiscalRegime"/>) into <see cref="ArcaFiscalData"/>, and tolerates the error
/// sections ARCA can place beside <c>datosGenerales</c>: that answer comes back as partial
/// (<see cref="ArcaPersonaData.IsIncomplete"/>), not as "not found".
/// </remarks>
public sealed class ArcaConstanciaService : IArcaConstanciaService
{
    private const string ServiceName = "ws_sr_constancia_inscripcion";

    // ASSUMPTIONS, not verified against live ARCA: idImpuesto 20 (monotributo), 30 (IVA) and
    // 32 (IVA exento), and the estadoImpuesto codes "AC" / "ACTIVO" for an active impuesto, are
    // taken from ARCA's public padrón catalogue. No constant or document in this repository
    // confirms any of them (searched). Reconcile against a captured getPersona_v2 response.
    // 20 is not read: monotributo is decided by the presence of datosMonotributo, not by an id.
    private const string IvaImpuestoId       = "30";
    private const string IvaExentoImpuestoId = "32";

    private static readonly string[] ActiveEstadoImpuestoCodes = ["AC", "ACTIVO"];

    // Error sections getPersona_v2 can place beside datosGenerales. Each makes the answer partial
    // (IsIncomplete), never absent — see ParseGetPersonaV2Response.
    private static readonly string[] ErrorSectionNames =
    [
        "errorConstancia",
        "errorRegimenGeneral",
        "errorMonotributo"
    ];

    private static readonly XNamespace SoapNs =
        "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace A5Ns =
        "http://a5.soap.ws.server.puc.sr/";

    private readonly HttpClient                          _httpClient;
    private readonly ILogger<ArcaConstanciaService>     _logger;
    private readonly IArcaWsaaService                   _wsaaService;

    public ArcaConstanciaService(
        HttpClient                       httpClient,
        ILogger<ArcaConstanciaService>   logger,
        IArcaWsaaService                 wsaaService)
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

        var soapRequest = BuildGetPersonaV2Request(cuit, cuitRepresentada, ticket);

        var sw = Stopwatch.StartNew();
        var responseXml = await CallSoapAsync(soapRequest, "getPersona_v2", cancellationToken)
            .ConfigureAwait(false);
        sw.Stop();

        _logger.LogDebug("ARCA getPersona_v2 completed in {DurationMs}ms", sw.ElapsedMilliseconds);

        return ParseGetPersonaV2Response(responseXml, _logger);
    }

    // ── SOAP request builder ─────────────────────────────────────────────────────

    private static string BuildGetPersonaV2Request(
        string cuit,
        string cuitRepresentada,
        WsaaTicket ticket)
    {
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/"
                              xmlns:a5="http://a5.soap.ws.server.puc.sr/">
              <soapenv:Header/>
              <soapenv:Body>
                <a5:getPersona_v2>
                  <token>{SecurityElement.Escape(ticket.Token)}</token>
                  <sign>{SecurityElement.Escape(ticket.Sign)}</sign>
                  <cuitRepresentada>{SecurityElement.Escape(cuitRepresentada)}</cuitRepresentada>
                  <idPersona>{SecurityElement.Escape(cuit)}</idPersona>
                </a5:getPersona_v2>
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

            // ARCA answers an unknown CUIT with a SOAP Fault over HTTP 500: that is "no person", not an
            // outage, so the body goes to the parser, which reads it as null. The body is not logged —
            // a fault's text can echo the CUIT queried.
            if (ArcaFault.IsPersonNotFound(ArcaFault.ReadFaultString(errorBody)))
            {
                _logger.LogInformation(
                    "ARCA Constancia {Method} answered HTTP {StatusCode} with the no-such-person fault; read as no person.",
                    method, (int)response.StatusCode);

                return errorBody;
            }

            _logger.LogError(
                "ARCA Constancia {Method} failed with status {StatusCode}. Response: {Response}",
                method, response.StatusCode, errorBody);

            throw new HttpRequestException(
                $"ARCA Constancia {method} returned {response.StatusCode}: {errorBody}");
        }

        return await response.Content
            .ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    // ── XML response parser ──────────────────────────────────────────────────────

    // PROVISIONAL — parser built from documented schema; reconcile against a real captured XML response before retiring A13.
    // The fixtures in ArcaConstanciaParserTests are derived from that same published getPersona_v2
    // schema, NOT captured from live ARCA: they pin this reading of the schema, they do not prove it.
    // The error-section tolerance, the descripcionCategoria format, the tipoPersona literals and the
    // impuesto ids / estadoImpuesto codes (see IvaImpuestoId) all still await a captured response.
    /// <summary>
    /// Parses a getPersona_v2 SOAP response. Returns null only when ARCA reports no person: the
    /// no-such-person SOAP Fault (<see cref="ArcaFault.IsPersonNotFound"/>), a <c>personaNoRegistrada</c>
    /// child of <c>personaReturn</c>, or no <c>datosGenerales</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Any OTHER SOAP Fault — even one that arrives with HTTP 200 — and an answer with neither a Fault
    /// nor a <c>personaReturn</c> are failures: they throw <see cref="HttpRequestException"/>, which the
    /// callers map to 502, exactly like a non-2xx. The exception message carries no fault text, since
    /// it can echo the CUIT and callers write the message to ApiCallLogs.
    /// </para>
    /// <para>
    /// An error section beside <c>datosGenerales</c> (<c>errorConstancia</c>,
    /// <c>errorRegimenGeneral</c>, <c>errorMonotributo</c>) makes the answer partial, not absent: what
    /// did arrive is returned with <see cref="ArcaPersonaData.IsIncomplete"/> set, after ONE Warning
    /// naming only the sections and how many <c>error</c> entries they carry. No element text is ever
    /// logged — the entries are free text and <c>errorConstancia</c> repeats apellido, nombre and
    /// idPersona — and neither is the CUIT.
    /// </para>
    /// <para>
    /// Internal (InternalsVisibleTo: Api.Web.FinanceIA.Tests) and static, with the logger passed in,
    /// so the parser tests drive it without a WSAA ticket or a SOAP transport.
    /// </para>
    /// </remarks>
    internal static ArcaPersonaData? ParseGetPersonaV2Response(string xml, ILogger logger)
    {
        var doc = XDocument.Parse(xml);

        // A SOAP Fault means "no person" only when it is ARCA's no-such-person answer
        if (ArcaFault.HasFault(doc))
        {
            if (ArcaFault.IsPersonNotFound(ArcaFault.ReadFaultString(doc))) return null;

            throw new HttpRequestException("ARCA Constancia getPersona_v2 answered a SOAP Fault.");
        }

        var personaReturn = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "personaReturn");

        if (personaReturn is null)
            throw new HttpRequestException("ARCA Constancia getPersona_v2 answered without a personaReturn.");

        // personaNoRegistrada is the one error shape that means "no such person"
        if (personaReturn.Elements().Any(e => e.Name.LocalName == "personaNoRegistrada"))
            return null;

        // datosGenerales is the primary source of scalar fields
        var datosGenerales = FindChild(personaReturn, "datosGenerales");

        if (datosGenerales is null) return null;

        // Error sections beside datosGenerales: partial data, not "not found"
        var errorSections = personaReturn.Elements()
            .Where(e => ErrorSectionNames.Contains(e.Name.LocalName))
            .ToList();
        var isIncomplete = errorSections.Count > 0;

        if (isIncomplete)
        {
            // Element names and a count only — never an element's text
            var errorCount = errorSections
                .Sum(s => s.Elements().Count(e => e.Name.LocalName == "error"));

            logger.LogWarning(
                "ARCA getPersona_v2 answered with error sections {ErrorSections} beside datosGenerales ({ErrorCount} error entries); returning the partial data.",
                string.Join(", ", errorSections.Select(s => s.Name.LocalName).Distinct()),
                errorCount);
        }

        var domicilioFiscalEl = datosGenerales.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "domicilioFiscal");

        var domicilioFiscal = domicilioFiscalEl is not null
            ? ParseDomicilio(domicilioFiscalEl)
            : null;

        // Principal activity — try datosRegimenGeneral first, then datosMonotributo
        var (idActividad, descripcionActividad, periodoActividad) = ResolveActividadPrincipal(personaReturn);

        var mesCierre            = ParseInt(GetElementValue(datosGenerales, "mesCierre"));
        var fechaContratoSocial  = ParseDateTime(GetElementValue(datosGenerales, "fechaContratoSocial"));
        var esSucesion           = ParseSiNo(GetElementValue(datosGenerales, "esSucesion"));

        // Fiscal regime and monotributo category — the rules live in ResolveFiscalRegime
        var datosMonotributo    = FindChild(personaReturn, "datosMonotributo");
        var datosRegimenGeneral = FindChild(personaReturn, "datosRegimenGeneral");

        var categoriaMonotributo = datosMonotributo is not null
            ? FindChild(datosMonotributo, "categoriaMonotributo")
            : null;
        var descripcionCategoria = categoriaMonotributo is not null
            ? GetElementValue(categoriaMonotributo, "descripcionCategoria")
            : null;

        IReadOnlyList<ArcaImpuesto> regimenGeneralImpuestos = datosRegimenGeneral is not null
            ? ParseImpuestos(datosRegimenGeneral)
            : [];

        var regime = ResolveFiscalRegime(
            hasMonotributo:          datosMonotributo is not null,
            descripcionCategoria:    descripcionCategoria,
            monotributoErrored:      errorSections.Any(e => e.Name.LocalName == "errorMonotributo"),
            hasRegimenGeneral:       datosRegimenGeneral is not null,
            regimenGeneralErrored:   errorSections.Any(e => e.Name.LocalName == "errorRegimenGeneral"),
            regimenGeneralImpuestos: regimenGeneralImpuestos);

        if (regime.CategoriaUnparsable)
        {
            // The length, never the text: descripcionCategoria is ARCA free text
            logger.LogWarning(
                "ARCA categoriaMonotributo/descripcionCategoria could not be parsed to a category A-K ({DescripcionLength} characters); storing no category.",
                descripcionCategoria?.Length ?? 0);
        }

        var fiscalData = ResolveFullFiscalData(
            personaReturn,
            mesCierre,
            fechaContratoSocial,
            esSucesion,
            regime.FiscalRegime,
            regime.MonotributoCategoria);

        return new ArcaPersonaData(
            Cuit:                          GetElementValue(datosGenerales, "idPersona") ?? string.Empty,
            TipoPersona:                   GetElementValue(datosGenerales, "tipoPersona"),
            EstadoClave:                   GetElementValue(datosGenerales, "estadoClave"),
            Nombre:                        GetElementValue(datosGenerales, "nombre"),
            Apellido:                      GetElementValue(datosGenerales, "apellido"),
            RazonSocial:                   GetElementValue(datosGenerales, "razonSocial"),
            TipoDocumento:                 null,   // not available in constancia response
            NumeroDocumento:               null,   // not available in constancia response
            FechaNacimiento:               null,   // not available in constancia response
            MesCierre:                     mesCierre,
            IdActividadPrincipal:          idActividad,
            DescripcionActividadPrincipal: descripcionActividad,
            PeriodoActividadPrincipal:     periodoActividad,
            FormaJuridica:                 null,   // not available in constancia response
            DomicilioFiscal:               domicilioFiscal,
            DomicilioLegal:                null,   // not available in constancia response
            FiscalData:                    fiscalData,
            IsIncomplete:                  isIncomplete
        );
    }

    /// <summary>
    /// Resolves the principal activity from datosRegimenGeneral or datosMonotributo.
    /// Reads both <c>actividad</c> and <c>actividadMonotributista</c> (the element datosMonotributo
    /// uses). Prefers the element where orden == "1"; falls back to the first element.
    /// </summary>
    private static (string? id, string? descripcion, string? periodo) ResolveActividadPrincipal(XElement personaReturn)
    {
        foreach (var sectionName in new[] { "datosRegimenGeneral", "datosMonotributo" })
        {
            var section = personaReturn.Elements()
                .FirstOrDefault(e => e.Name.LocalName == sectionName);
            if (section is null) continue;

            var actividades = section.Elements()
                .Where(IsActividad)
                .ToList();
            if (actividades.Count == 0) continue;

            var principal = actividades
                .FirstOrDefault(a => GetElementValue(a, "orden") == "1")
                ?? actividades[0];

            return (
                GetElementValue(principal, "idActividad"),
                GetElementValue(principal, "descripcionActividad"),
                GetElementValue(principal, "periodo")
            );
        }

        return (null, null, null);
    }

    /// <summary>
    /// Extracts the full fiscal data block from the response.
    /// Tries datosRegimenGeneral first, then datosMonotributo: the lists come from the first section
    /// present only. <paramref name="fiscalRegime"/> and <paramref name="monotributoCategoria"/> are
    /// <see cref="ResolveFiscalRegime"/>'s verdict, which looks at both sections.
    /// Returns null if neither section is present.
    /// </summary>
    private static ArcaFiscalData? ResolveFullFiscalData(
        XElement personaReturn,
        int? mesCierre,
        DateTime? fechaContratoSocial,
        bool? esSucesion,
        string fiscalRegime,
        string? monotributoCategoria)
    {
        XElement? section = null;
        foreach (var sectionName in new[] { "datosRegimenGeneral", "datosMonotributo" })
        {
            section = personaReturn.Elements()
                .FirstOrDefault(e => e.Name.LocalName == sectionName);
            if (section is not null) break;
        }

        if (section is null) return null;

        // ARCA sends a monotributista's activity TWICE — as <actividad> and as
        // <actividadMonotributista> — so without the dedupe the same activity shows up twice as
        // PRINCIPAL. One per (IdActividad, Periodo), keeping the lowest Orden: the list is already
        // ordered by Orden, and GroupBy keeps first-occurrence order, so First() is that row.
        var actividades = section.Elements()
            .Where(IsActividad)
            .OrderBy(a => ParseInt(GetElementValue(a, "orden")) ?? int.MaxValue)
            .Select(a => new ArcaActividad(
                IdActividad:          GetElementValue(a, "idActividad"),
                DescripcionActividad: GetElementValue(a, "descripcionActividad"),
                Nomenclador:          GetElementValue(a, "nomenclador"),
                Orden:                ParseInt(GetElementValue(a, "orden")),
                Periodo:              GetElementValue(a, "periodo")))
            .GroupBy(a => (a.IdActividad, a.Periodo))
            .Select(g => g.First())
            .ToList();

        var impuestos = ParseImpuestos(section);

        var regimenes = section.Elements()
            .Where(e => e.Name.LocalName == "regimen")
            .Select(r => new ArcaRegimen(
                IdRegimen:          GetElementValue(r, "idRegimen"),
                DescripcionRegimen: GetElementValue(r, "descripcionRegimen"),
                IdImpuesto:         GetElementValue(r, "idImpuesto"),
                TipoRegimen:        GetElementValue(r, "tipoRegimen"),
                Periodo:            GetElementValue(r, "periodo")))
            .ToList();

        return new ArcaFiscalData(
            FechaContratoSocial:  fechaContratoSocial,
            EsSucesion:           esSucesion,
            MesCierre:            mesCierre,
            Actividades:          actividades,
            Impuestos:            impuestos,
            Regimenes:            regimenes,
            FiscalRegime:         fiscalRegime,
            MonotributoCategoria: monotributoCategoria,
            FechaInicioActividad: EarliestActivityPeriod(personaReturn));
    }

    /// <summary>
    /// The first day of the earliest parsable <c>periodo</c> of the actividades (<c>actividad</c>,
    /// <c>actividadMonotributista</c>), the impuestos and <c>categoriaMonotributo</c> of BOTH
    /// datosRegimenGeneral and datosMonotributo — unlike the lists, which keep the first section only.
    /// Regímenes are not counted. Null when no period parses.
    /// </summary>
    private static DateTime? EarliestActivityPeriod(XElement personaReturn)
    {
        var periods = new List<string?>();

        foreach (var sectionName in new[] { "datosRegimenGeneral", "datosMonotributo" })
        {
            var section = FindChild(personaReturn, sectionName);
            if (section is null) continue;

            periods.AddRange(section.Elements()
                .Where(e => IsActividad(e) || e.Name.LocalName is "impuesto" or "categoriaMonotributo")
                .Select(e => GetElementValue(e, "periodo")));
        }

        return ArcaActivityStart.EarliestPeriod(periods);
    }

    // ── Fiscal regime ────────────────────────────────────────────────────────────

    /// <summary>
    /// Derives the fiscal regime (one of <see cref="FiscalRegimes.Known"/>) and the monotributo
    /// category from which getPersona_v2 sections arrived. Pure: the parser supplies the flags, and
    /// the tests drive it directly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rules, first match wins:
    /// <list type="number">
    /// <item><paramref name="hasMonotributo"/> (datosMonotributo) → Monotributo. The category is the
    /// leading letter token of <paramref name="descripcionCategoria"/> ("H LOCACIONES DE SERVICIO" →
    /// "H"), A–K in either case. A non-blank description with no such token yields no category and
    /// <see cref="ArcaFiscalRegimeResult.CategoriaUnparsable"/>; a blank one yields no category,
    /// silently.</item>
    /// <item><paramref name="monotributoErrored"/> (errorMonotributo) → Desconocido: the half that
    /// would show monotributo failed, so monotributo cannot be ruled out.</item>
    /// <item><paramref name="hasRegimenGeneral"/> (datosRegimenGeneral) → Desconocido when
    /// <paramref name="regimenGeneralErrored"/>; else an ACTIVE IVA exento (32) → Exento; else an
    /// ACTIVE IVA (30) → ResponsableInscripto; else Desconocido.</item>
    /// <item>Otherwise → Desconocido.</item>
    /// </list>
    /// </para>
    /// <para>
    /// ACTIVE means estadoImpuesto is blank, "AC" or "ACTIVO" (trimmed, any case). Any other code
    /// ("BD", "EX", "NA", …) is not active, so an unexpected code degrades the result to Desconocido
    /// rather than to a wrong regime. idImpuesto is compared trimmed and ordinal. The ids and codes
    /// are assumptions — see the comment on <see cref="IvaImpuestoId"/>.
    /// </para>
    /// </remarks>
    internal static ArcaFiscalRegimeResult ResolveFiscalRegime(
        bool                        hasMonotributo,
        string?                     descripcionCategoria,
        bool                        monotributoErrored,
        bool                        hasRegimenGeneral,
        bool                        regimenGeneralErrored,
        IReadOnlyList<ArcaImpuesto> regimenGeneralImpuestos)
    {
        if (hasMonotributo)
        {
            if (string.IsNullOrWhiteSpace(descripcionCategoria))
                return new ArcaFiscalRegimeResult(FiscalRegimes.Monotributo, null, CategoriaUnparsable: false);

            var categoria = ParseCategoriaMonotributo(descripcionCategoria);
            return new ArcaFiscalRegimeResult(FiscalRegimes.Monotributo, categoria, CategoriaUnparsable: categoria is null);
        }

        var desconocido = new ArcaFiscalRegimeResult(FiscalRegimes.Desconocido, null, CategoriaUnparsable: false);

        // Monotributo cannot be ruled out, so nothing regimen general says settles the regime
        if (monotributoErrored) return desconocido;

        if (!hasRegimenGeneral || regimenGeneralErrored) return desconocido;

        if (HasActiveImpuesto(regimenGeneralImpuestos, IvaExentoImpuestoId))
            return new ArcaFiscalRegimeResult(FiscalRegimes.Exento, null, CategoriaUnparsable: false);

        if (HasActiveImpuesto(regimenGeneralImpuestos, IvaImpuestoId))
            return new ArcaFiscalRegimeResult(FiscalRegimes.ResponsableInscripto, null, CategoriaUnparsable: false);

        return desconocido;
    }

    /// <summary>
    /// The leading letter token of descripcionCategoria as a category A–K, or null. The first
    /// character must be the letter and must stand alone — followed by the end or by a non-letter —
    /// so "H LOCACIONES DE SERVICIO" → "H", while "HX", "CATEGORIA X" and "L OTRA" → null.
    /// </summary>
    private static string? ParseCategoriaMonotributo(string descripcionCategoria)
    {
        var trimmed = descripcionCategoria.Trim();
        if (trimmed.Length == 0) return null;
        if (trimmed.Length > 1 && char.IsLetter(trimmed[1])) return null;

        return FiscalRegimes.NormalizeMonotributoCategory(trimmed[..1]);
    }

    private static bool HasActiveImpuesto(IReadOnlyList<ArcaImpuesto> impuestos, string idImpuesto)
        => impuestos.Any(i =>
            string.Equals(i.IdImpuesto?.Trim(), idImpuesto, StringComparison.Ordinal)
            && IsActiveEstadoImpuesto(i.EstadoImpuesto));

    private static bool IsActiveEstadoImpuesto(string? estadoImpuesto)
        => string.IsNullOrWhiteSpace(estadoImpuesto)
           || ActiveEstadoImpuestoCodes.Contains(estadoImpuesto.Trim(), StringComparer.OrdinalIgnoreCase);

    // ── XML helpers ──────────────────────────────────────────────────────────────

    private static XElement? FindChild(XElement parent, string localName)
        => parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private static bool IsActividad(XElement el)
        => el.Name.LocalName is "actividad" or "actividadMonotributista";

    private static List<ArcaImpuesto> ParseImpuestos(XElement section)
        => section.Elements()
            .Where(e => e.Name.LocalName == "impuesto")
            .Select(i => new ArcaImpuesto(
                IdImpuesto:          GetElementValue(i, "idImpuesto"),
                DescripcionImpuesto: GetElementValue(i, "descripcionImpuesto"),
                EstadoImpuesto:      GetElementValue(i, "estadoImpuesto"),
                Motivo:              GetElementValue(i, "motivo"),
                Periodo:             GetElementValue(i, "periodo")))
            .ToList();

    private static ArcaDomicilio ParseDomicilio(XElement el) => new(
        TipoDomicilio:       GetElementValue(el, "tipoDomicilio") ?? string.Empty,
        Direccion:           null,
        Calle:               GetElementValue(el, "calle"),
        Numero:              GetElementValue(el, "numero"),
        Localidad:           GetElementValue(el, "localidad"),
        CodigoPostal:        GetElementValue(el, "codigoPostal") ?? GetElementValue(el, "codPostal"),
        IdProvincia:         ParseInt(GetElementValue(el, "idProvincia")),
        DescripcionProvincia:GetElementValue(el, "descripcionProvincia"),
        EstadoDomicilio:     GetElementValue(el, "estadoDomicilio")
    );

    private static string? GetElementValue(XElement parent, string localName)
    {
        var el = parent.Elements()
            .FirstOrDefault(e => e.Name.LocalName == localName);
        return string.IsNullOrWhiteSpace(el?.Value) ? null : el.Value.Trim();
    }

    private static int? ParseInt(string? value)
        => int.TryParse(value, out var result) ? result : null;

    private static DateTime? ParseDateTime(string? value)
        => DateTime.TryParse(value, out var result) ? result : null;

    /// <summary>
    /// ARCA's yes/no flags (esSucesion): "SI", "S" or "true", trimmed and in any case, are true; any
    /// other present value is false; an absent element stays null.
    /// </summary>
    private static bool? ParseSiNo(string? value)
    {
        if (value is null) return null;

        var trimmed = value.Trim();
        return trimmed.Equals("true", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("SI", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("S", StringComparison.OrdinalIgnoreCase);
    }

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
