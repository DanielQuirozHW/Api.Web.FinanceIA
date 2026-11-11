// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-27

using Api.Web.FinanceIA.Models.Arca;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Covers <see cref="ArcaConstanciaService.ParseGetPersonaV2Response"/> — ARCA's getPersona_v2
/// answer read into <see cref="ArcaPersonaData"/> — and the pure
/// <see cref="ArcaConstanciaService.ResolveFiscalRegime"/> behind its fiscal regime and monotributo
/// category (M10, persona humana).
///
/// <para>
/// THE FIXTURES ARE SCHEMA-DERIVED, NOT CAPTURED. Every envelope here is composed from the published
/// ws_sr_constancia_inscripcion getPersona_v2 schema (personaReturn → datosGenerales,
/// datosMonotributo, datosRegimenGeneral, errorConstancia, errorMonotributo, errorRegimenGeneral,
/// personaNoRegistrada, metadata); none was captured from live ARCA. They pin the parser's reading of
/// that schema, not ARCA's real behaviour: the error-section tolerance, the descripcionCategoria
/// format, the tipoPersona literals, the impuesto ids 30 / 32 and the estadoImpuesto codes all still
/// await a captured response — which is also what retires the PROVISIONAL marker on the parser.
/// </para>
///
/// <para>
/// The PII half matters as much as the parsing. An error section beside datosGenerales now yields
/// the partial data (<see cref="ArcaPersonaData.IsIncomplete"/>) instead of null, plus ONE Warning
/// that names sections and counts entries — never their text, because the error entries are free
/// text and errorConstancia repeats apellido, nombre and idPersona. Those cases therefore assert on
/// what the Warning does NOT contain. Every CUIT, name and message here is synthetic.
/// </para>
/// </summary>
public sealed class ArcaConstanciaParserTests
{
    private const string CompanyCuit  = "30123456789";
    private const string HumanaCuit   = "20111111112";
    private const string RazonSocial  = "EMPRESA DE PRUEBA SA";
    private const string Apellido     = "EJEMPLO";
    private const string Nombre       = "PERSONA DE PRUEBA";
    private const string ErrorText    = "LA CLAVE REGISTRA UNA INCONSISTENCIA SINTETICA";
    private const string ErrorText2   = "OTRA OBSERVACION SINTETICA";
    private const string ErrorMensaje = "MENSAJE SINTETICO DE ERROR";

    private const string SoapNs = "http://schemas.xmlsoap.org/soap/envelope/";
    private const string A5Ns   = "http://a5.soap.ws.server.puc.sr/";

    private const string SoapFaultXml = """
        <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
          <soap:Body>
            <soap:Fault>
              <faultcode>soap:Server</faultcode>
              <faultstring>No existe persona con ese Id</faultstring>
            </soap:Fault>
          </soap:Body>
        </soap:Envelope>
        """;

    private static readonly ArcaFiscalRegimeResult Desconocido =
        new(FiscalRegimes.Desconocido, null, CategoriaUnparsable: false);

    // ── Stubs ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Captures Warning and Error entries as rendered text, so a test can assert both that exactly
    /// one Warning was written and that it carries no CUIT, name or ARCA free text.
    /// </summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];
        public List<string> Errors   { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
            if (logLevel >= LogLevel.Error)   Errors.Add(formatter(state, exception));
        }
    }

    // ── Envelope builder ──────────────────────────────────────────────────────
    // Element names and nesting follow the published getPersona_v2 schema (see the class summary).
    // personaReturn and everything under it are unqualified, as the schema declares them; the parser
    // matches on LocalName, so the ns2 prefix on the response wrapper is cosmetic.

    private static string Envelope(params string[] personaReturnChildren)
        => $"""
            <soap:Envelope xmlns:soap="{SoapNs}">
              <soap:Body>
                <ns2:getPersona_v2Response xmlns:ns2="{A5Ns}">
                  <personaReturn>
                    {string.Concat(personaReturnChildren)}
                    <metadata><fechaHora>2026-09-27T10:00:00.000-03:00</fechaHora><servidor>servidor-prueba</servidor></metadata>
                  </personaReturn>
                </ns2:getPersona_v2Response>
              </soap:Body>
            </soap:Envelope>
            """;

    /// <summary>One element, or nothing at all when <paramref name="value"/> is null.</summary>
    private static string El(string name, string? value)
        => value is null ? string.Empty : $"<{name}>{value}</{name}>";

    private static string DatosGenerales(
        string  idPersona,
        string  tipoPersona,
        string? razonSocial         = null,
        string? apellido            = null,
        string? nombre              = null,
        string? esSucesion          = "NO",
        string? mesCierre           = "12",
        string? fechaContratoSocial = null,
        string? estadoClave         = "ACTIVO")
        => "<datosGenerales>"
         + El("apellido", apellido)
         + "<domicilioFiscal>"
         + El("codPostal", "1000")
         + El("descripcionProvincia", "PROVINCIA DE PRUEBA")
         + El("direccion", "CALLE SINTETICA 123")
         + El("idProvincia", "0")
         + El("localidad", "LOCALIDAD DE PRUEBA")
         + El("tipoDomicilio", "FISCAL")
         + "</domicilioFiscal>"
         + El("esSucesion", esSucesion)
         + El("estadoClave", estadoClave)
         + El("fechaContratoSocial", fechaContratoSocial)
         + El("idPersona", idPersona)
         + El("mesCierre", mesCierre)
         + El("nombre", nombre)
         + El("razonSocial", razonSocial)
         + El("tipoPersona", tipoPersona)
         + "</datosGenerales>";

    /// <param name="elementName">"actividad" under datosRegimenGeneral, "actividadMonotributista" under datosMonotributo.</param>
    private static string Actividad(
        string elementName, string idActividad, string descripcion, string orden, string? periodo = "201901")
        => $"<{elementName}>"
         + El("descripcionActividad", descripcion)
         + El("idActividad", idActividad)
         + El("nomenclador", "883")
         + El("orden", orden)
         + El("periodo", periodo)
         + $"</{elementName}>";

    private static string Impuesto(string idImpuesto, string descripcion, string? estado, string? periodo = "201901")
        => "<impuesto>"
         + El("descripcionImpuesto", descripcion)
         + El("estadoImpuesto", estado)
         + El("idImpuesto", idImpuesto)
         + El("motivo", "ALTA DE PRUEBA")
         + El("periodo", periodo)
         + "</impuesto>";

    private static string Regimen(string idRegimen, string idImpuesto, string descripcion)
        => "<regimen>"
         + El("descripcionRegimen", descripcion)
         + El("idImpuesto", idImpuesto)
         + El("idRegimen", idRegimen)
         + El("periodo", "201901")
         + El("tipoRegimen", "RETENCION")
         + "</regimen>";

    private static string DatosRegimenGeneral(params string[] children)
        => "<datosRegimenGeneral>" + string.Concat(children) + "</datosRegimenGeneral>";

    /// <param name="actividad">An <see cref="Actividad"/> built as "actividadMonotributista", or null.</param>
    /// <param name="descripcionCategoria">categoriaMonotributo/descripcionCategoria, or null for no categoriaMonotributo.</param>
    private static string DatosMonotributo(string? actividad, string? descripcionCategoria, params string[] impuestos)
        => "<datosMonotributo>"
         + (actividad ?? string.Empty)
         + (descripcionCategoria is null
               ? string.Empty
               : "<categoriaMonotributo>"
                 + El("descripcionCategoria", descripcionCategoria)
                 + El("idCategoria", "25")
                 + El("idImpuesto", "20")
                 + El("periodo", "202601")
                 + "</categoriaMonotributo>")
         + string.Concat(impuestos)
         + "</datosMonotributo>";

    private static string ErrorConstancia(params string[] errors)
        => "<errorConstancia>"
         + El("apellido", Apellido)
         + string.Concat(errors.Select(e => El("error", e)))
         + El("idPersona", HumanaCuit)
         + El("nombre", Nombre)
         + "</errorConstancia>";

    private static string ErrorMonotributo(params string[] errors)
        => "<errorMonotributo>"
         + string.Concat(errors.Select(e => El("error", e)))
         + El("mensaje", ErrorMensaje)
         + "</errorMonotributo>";

    private static string ErrorRegimenGeneral(params string[] errors)
        => "<errorRegimenGeneral>"
         + string.Concat(errors.Select(e => El("error", e)))
         + El("mensaje", ErrorMensaje)
         + "</errorRegimenGeneral>";

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ArcaPersonaData? Parse(string xml, ILogger? logger = null)
        => ArcaConstanciaService.ParseGetPersonaV2Response(xml, logger ?? NullLogger.Instance);

    private static ArcaImpuesto Imp(string? idImpuesto, string? estadoImpuesto)
        => new(idImpuesto, "IMPUESTO SINTETICO", estadoImpuesto, null, "202601");

    private static ArcaFiscalRegimeResult Resolve(
        bool                         hasMonotributo        = false,
        string?                      descripcionCategoria  = null,
        bool                         monotributoErrored    = false,
        bool                         hasRegimenGeneral     = false,
        bool                         regimenGeneralErrored = false,
        IReadOnlyList<ArcaImpuesto>? impuestos             = null)
        => ArcaConstanciaService.ResolveFiscalRegime(
            hasMonotributo,
            descripcionCategoria,
            monotributoErrored,
            hasRegimenGeneral,
            regimenGeneralErrored,
            impuestos ?? []);

    /// <summary>The logged text never carries a CUIT, a name or any of ARCA's free text.</summary>
    private static void AssertCarriesNoPersonalData(string message)
    {
        Assert.DoesNotContain(CompanyCuit,  message);
        Assert.DoesNotContain(HumanaCuit,   message);
        Assert.DoesNotContain(RazonSocial,  message);
        Assert.DoesNotContain(Apellido,     message);
        Assert.DoesNotContain(Nombre,       message);
        Assert.DoesNotContain(ErrorText,    message);
        Assert.DoesNotContain(ErrorText2,   message);
        Assert.DoesNotContain(ErrorMensaje, message);
    }

    // ── Tests: complete answers, one per regime ───────────────────────────────

    [Fact]
    public void ParseGetPersonaV2Response_CompanyWithActiveIva_ReadsAJuridicaResponsableInscripto()
    {
        var logger = new RecordingLogger<ArcaConstanciaService>();
        var xml = Envelope(
            DatosGenerales(CompanyCuit, "JURIDICA", razonSocial: RazonSocial,
                fechaContratoSocial: "2010-03-15T00:00:00-03:00"),
            DatosRegimenGeneral(
                Actividad("actividad", "620100", "SERVICIOS DE CONSULTORES EN INFORMATICA", "1"),
                Impuesto("10", "GANANCIAS SOCIEDADES", "AC"),
                Impuesto("30", "IVA", "AC"),
                Regimen("216", "30", "REGIMEN DE PRUEBA")));

        var result = Parse(xml, logger);

        Assert.NotNull(result);
        Assert.Equal(CompanyCuit, result.Cuit);
        Assert.Equal("JURIDICA", result.TipoPersona);
        Assert.Equal(RazonSocial, result.RazonSocial);
        Assert.Equal(12, result.MesCierre);
        Assert.Equal("620100", result.IdActividadPrincipal);
        Assert.False(result.IsIncomplete);
        Assert.NotNull(result.DomicilioFiscal);
        Assert.Equal("1000", result.DomicilioFiscal.CodigoPostal);

        Assert.NotNull(result.FiscalData);
        Assert.Equal(FiscalRegimes.ResponsableInscripto, result.FiscalData.FiscalRegime);
        Assert.Null(result.FiscalData.MonotributoCategoria);
        Assert.True(result.FiscalData.FechaContratoSocial.HasValue);
        Assert.False(result.FiscalData.EsSucesion);
        Assert.Equal(2, result.FiscalData.Impuestos.Count);
        Assert.Equal("216", Assert.Single(result.FiscalData.Regimenes).IdRegimen);

        Assert.Empty(logger.Warnings);
        Assert.Empty(logger.Errors);
    }

    [Fact]
    public void ParseGetPersonaV2Response_PersonaHumanaMonotributo_ReadsTheCategoryAndTheActividadMonotributista()
    {
        var logger = new RecordingLogger<ArcaConstanciaService>();
        var xml = Envelope(
            DatosGenerales(HumanaCuit, "FISICA", apellido: Apellido, nombre: Nombre),
            DatosMonotributo(
                Actividad("actividadMonotributista", "620100", "SERVICIOS DE CONSULTORES EN INFORMATICA", "1"),
                "H LOCACIONES DE SERVICIO",
                Impuesto("20", "MONOTRIBUTO", "AC")));

        var result = Parse(xml, logger);

        Assert.NotNull(result);
        Assert.Equal(HumanaCuit, result.Cuit);
        Assert.Equal("FISICA", result.TipoPersona);
        Assert.Equal(Apellido, result.Apellido);
        Assert.Equal(Nombre, result.Nombre);
        Assert.Null(result.RazonSocial);
        Assert.False(result.IsIncomplete);

        // Before M10 only "actividad" was read, so a monotributista had no principal activity at all.
        Assert.Equal("620100", result.IdActividadPrincipal);
        Assert.Equal("SERVICIOS DE CONSULTORES EN INFORMATICA", result.DescripcionActividadPrincipal);

        Assert.NotNull(result.FiscalData);
        Assert.Equal(FiscalRegimes.Monotributo, result.FiscalData.FiscalRegime);
        Assert.Equal("H", result.FiscalData.MonotributoCategoria);
        Assert.Equal("620100", Assert.Single(result.FiscalData.Actividades).IdActividad);
        Assert.Equal("20", Assert.Single(result.FiscalData.Impuestos).IdImpuesto);

        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public void ParseGetPersonaV2Response_SameActivityAsActividadAndActividadMonotributista_IsListedOnce()
    {
        // ARCA sends a monotributista's activity TWICE — as <actividad> and as
        // <actividadMonotributista> — for the same (idActividad, periodo). Deduplicated, keeping the
        // lowest orden; a genuinely different activity stays.
        var xml = Envelope(
            DatosGenerales(HumanaCuit, "FISICA", apellido: Apellido, nombre: Nombre),
            DatosMonotributo(
                Actividad("actividad", "620100", "SERVICIOS DE CONSULTORES EN INFORMATICA", "2")
                + Actividad("actividadMonotributista", "620100", "SERVICIOS DE CONSULTORES EN INFORMATICA", "1")
                + Actividad("actividad", "711001", "SERVICIOS DE ARQUITECTURA SINTETICOS", "3"),
                "H LOCACIONES DE SERVICIO",
                Impuesto("20", "MONOTRIBUTO", "AC")));

        var result = Parse(xml, new RecordingLogger<ArcaConstanciaService>());

        Assert.NotNull(result?.FiscalData);
        var actividades = result!.FiscalData!.Actividades;
        Assert.Equal(["620100", "711001"], actividades.Select(a => a.IdActividad));
        Assert.Equal(1, actividades[0].Orden);
        Assert.Single(actividades, a => a.Orden == 1);
        Assert.Equal("620100", result.IdActividadPrincipal);
    }

    [Fact]
    public void ParseGetPersonaV2Response_PersonaHumanaWithActiveIva_ReadsAResponsableInscripto()
    {
        var xml = Envelope(
            DatosGenerales(HumanaCuit, "FISICA", apellido: Apellido, nombre: Nombre),
            DatosRegimenGeneral(
                Actividad("actividad", "691001", "SERVICIOS JURIDICOS", "1"),
                Impuesto("11", "GANANCIAS PERSONAS FISICAS", "AC"),
                Impuesto("30", "IVA", "AC")));

        var result = Parse(xml);

        Assert.NotNull(result);
        Assert.Equal("FISICA", result.TipoPersona);
        Assert.False(result.IsIncomplete);
        Assert.NotNull(result.FiscalData);
        Assert.Equal(FiscalRegimes.ResponsableInscripto, result.FiscalData.FiscalRegime);
        Assert.Null(result.FiscalData.MonotributoCategoria);
    }

    [Fact]
    public void ParseGetPersonaV2Response_ActiveIvaExento_ReadsExento()
    {
        var xml = Envelope(
            DatosGenerales(CompanyCuit, "JURIDICA", razonSocial: RazonSocial),
            DatosRegimenGeneral(
                Actividad("actividad", "949100", "SERVICIOS DE ORGANIZACIONES RELIGIOSAS", "1"),
                Impuesto("32", "IVA EXENTO", "AC")));

        var result = Parse(xml);

        Assert.NotNull(result);
        Assert.NotNull(result.FiscalData);
        Assert.Equal(FiscalRegimes.Exento, result.FiscalData.FiscalRegime);
    }

    [Fact]
    public void ParseGetPersonaV2Response_NeitherRegimeSection_LeavesFiscalDataNull()
    {
        // refresh-arca relies on a null FiscalData meaning "ARCA sent no fiscal section at all".
        var logger = new RecordingLogger<ArcaConstanciaService>();

        var result = Parse(Envelope(DatosGenerales(CompanyCuit, "JURIDICA", razonSocial: RazonSocial)), logger);

        Assert.NotNull(result);
        Assert.Null(result.FiscalData);
        Assert.Null(result.IdActividadPrincipal);
        Assert.False(result.IsIncomplete);
        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public void ParseGetPersonaV2Response_BothSections_TakesTheListsFromRegimenGeneralAndTheRegimeFromMonotributo()
    {
        // ResolveFullFiscalData keeps its first-section-wins lists; the regime looks at both sections.
        var xml = Envelope(
            DatosGenerales(HumanaCuit, "FISICA", apellido: Apellido, nombre: Nombre),
            DatosRegimenGeneral(
                Actividad("actividad", "111111", "ACTIVIDAD SINTETICA RG", "1"),
                Impuesto("11", "GANANCIAS PERSONAS FISICAS", "AC")),
            DatosMonotributo(
                Actividad("actividadMonotributista", "222222", "ACTIVIDAD SINTETICA MONOTRIBUTO", "1"),
                "B VENTA DE COSAS MUEBLES",
                Impuesto("20", "MONOTRIBUTO", "AC")));

        var result = Parse(xml);

        Assert.NotNull(result);
        Assert.Equal("111111", result.IdActividadPrincipal);
        Assert.NotNull(result.FiscalData);
        Assert.Equal("111111", Assert.Single(result.FiscalData.Actividades).IdActividad);
        Assert.Equal("11", Assert.Single(result.FiscalData.Impuestos).IdImpuesto);
        Assert.Equal(FiscalRegimes.Monotributo, result.FiscalData.FiscalRegime);
        Assert.Equal("B", result.FiscalData.MonotributoCategoria);
    }

    // ── Tests: activity start and key status (M10, persona humana ARCA gaps) ──

    [Fact]
    public void ParseGetPersonaV2Response_FechaInicioActividad_IsTheEarliestPeriodAcrossBothSections()
    {
        // The lists keep regimen general only (2019-01 / 2018-06); the monotributo half, which the
        // lists drop, holds the earliest period (2015-03, on the actividadMonotributista).
        var xml = Envelope(
            DatosGenerales(HumanaCuit, "FISICA", apellido: Apellido, nombre: Nombre),
            DatosRegimenGeneral(
                Actividad("actividad", "111111", "ACTIVIDAD SINTETICA RG", "1", periodo: "201901"),
                Impuesto("11", "GANANCIAS PERSONAS FISICAS", "AC", periodo: "201806")),
            DatosMonotributo(
                Actividad("actividadMonotributista", "222222", "ACTIVIDAD SINTETICA MONOTRIBUTO", "1", periodo: "201503"),
                "B VENTA DE COSAS MUEBLES",
                Impuesto("20", "MONOTRIBUTO", "AC", periodo: "202001")));

        var fiscal = Parse(xml)!.FiscalData!;

        Assert.Equal(new DateTime(2015, 3, 1), fiscal.FechaInicioActividad);
        Assert.DoesNotContain(fiscal.Actividades, a => a.Periodo == "201503");   // the lists did not change
    }

    [Fact]
    public void ParseGetPersonaV2Response_FechaInicioActividad_CountsTheCategoriaMonotributoPeriod()
    {
        // categoriaMonotributo/periodo is 202601 in the builder; everything else is later.
        var xml = Envelope(
            DatosGenerales(HumanaCuit, "FISICA", apellido: Apellido, nombre: Nombre),
            DatosMonotributo(
                Actividad("actividadMonotributista", "222222", "ACTIVIDAD SINTETICA", "1", periodo: "202603"),
                "C SERVICIOS",
                Impuesto("20", "MONOTRIBUTO", "AC", periodo: "202602")));

        Assert.Equal(new DateTime(2026, 1, 1), Parse(xml)!.FiscalData!.FechaInicioActividad);
    }

    [Fact]
    public void ParseGetPersonaV2Response_FechaInicioActividad_IgnoresRegimenesAndUnparsablePeriods()
    {
        // The regimen's period (201901) is earlier but does not count; "ALTA" and a missing period
        // do not parse; 202204 is what is left.
        var xml = Envelope(
            DatosGenerales(HumanaCuit, "FISICA", apellido: Apellido, nombre: Nombre),
            DatosRegimenGeneral(
                Actividad("actividad", "111111", "ACTIVIDAD SINTETICA", "1", periodo: "ALTA"),
                Impuesto("30", "IVA", "AC", periodo: null),
                Impuesto("11", "GANANCIAS PERSONAS FISICAS", "AC", periodo: "202204"),
                Regimen("200", "30", "RETENCION SINTETICA")));

        Assert.Equal(new DateTime(2022, 4, 1), Parse(xml)!.FiscalData!.FechaInicioActividad);
    }

    [Fact]
    public void ParseGetPersonaV2Response_FechaInicioActividad_NoParsablePeriod_IsNull()
    {
        var xml = Envelope(
            DatosGenerales(HumanaCuit, "FISICA", apellido: Apellido, nombre: Nombre),
            DatosRegimenGeneral(Impuesto("30", "IVA", "AC", periodo: null)));

        Assert.Null(Parse(xml)!.FiscalData!.FechaInicioActividad);
    }

    [Theory]
    [InlineData("ACTIVO", "ACTIVO")]
    [InlineData(" INACTIVO ", "INACTIVO")]
    [InlineData(null, null)]
    public void ParseGetPersonaV2Response_EstadoClave_IsReadFromDatosGenerales(string? sent, string? expected)
    {
        var xml = Envelope(
            DatosGenerales(HumanaCuit, "FISICA", apellido: Apellido, nombre: Nombre, estadoClave: sent),
            DatosRegimenGeneral(Impuesto("30", "IVA", "AC")));

        var result = Parse(xml)!;

        Assert.Equal(expected, result.EstadoClave);
        // The parser does not stamp the snapshot: the controller that called ARCA does.
        Assert.Null(result.FiscalData!.EstadoClave);
        Assert.Null(result.FiscalData.EstadoClaveConsultadoAt);
    }

    [Fact]
    public void ParseGetPersonaV2Response_EstadoClaveWithoutAnyRegimeSection_KeepsTheStatusWithNoFiscalData()
    {
        // The shape refresh-arca stores as "key status only".
        var xml = Envelope(
            DatosGenerales(HumanaCuit, "FISICA", apellido: Apellido, nombre: Nombre, estadoClave: "INACTIVO"));

        var result = Parse(xml)!;

        Assert.Equal("INACTIVO", result.EstadoClave);
        Assert.Null(result.FiscalData);
    }

    // ── Tests: error sections beside datosGenerales (partial, never null) ─────

    [Fact]
    public void ParseGetPersonaV2Response_ErrorMonotributoBesideDatosGenerales_ReturnsThePartialDataAsDesconocido()
    {
        // Until M10 any error sibling nulled the whole answer, so lookup-arca answered 404 for a
        // person ARCA had in fact described. errorMonotributo also means monotributo cannot be ruled
        // out, so the active IVA beside it must NOT make this a ResponsableInscripto.
        var logger = new RecordingLogger<ArcaConstanciaService>();
        var xml = Envelope(
            DatosGenerales(HumanaCuit, "FISICA", apellido: Apellido, nombre: Nombre),
            DatosRegimenGeneral(Impuesto("30", "IVA", "AC")),
            ErrorMonotributo(ErrorText));

        var result = Parse(xml, logger);

        Assert.NotNull(result);
        Assert.True(result.IsIncomplete);
        Assert.Equal(HumanaCuit, result.Cuit);
        Assert.Equal(Apellido, result.Apellido);
        Assert.NotNull(result.FiscalData);
        Assert.Equal(FiscalRegimes.Desconocido, result.FiscalData.FiscalRegime);
        Assert.Null(result.FiscalData.MonotributoCategoria);

        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("errorMonotributo", warning);
        Assert.Contains("1 error entries", warning);
        AssertCarriesNoPersonalData(warning);
        Assert.Empty(logger.Errors);
    }

    [Fact]
    public void ParseGetPersonaV2Response_ErrorConstanciaBesideDatosGenerales_ReturnsThePartialDataAndLogsNoPersonalData()
    {
        // errorConstancia repeats apellido, nombre and idPersona next to its free-text entries —
        // every one of them must stay out of the log.
        var logger = new RecordingLogger<ArcaConstanciaService>();
        var xml = Envelope(
            DatosGenerales(HumanaCuit, "FISICA", apellido: Apellido, nombre: Nombre),
            DatosRegimenGeneral(Impuesto("30", "IVA", "AC")),
            ErrorConstancia(ErrorText, ErrorText2));

        var result = Parse(xml, logger);

        Assert.NotNull(result);
        Assert.True(result.IsIncomplete);
        Assert.Equal(Apellido, result.Apellido);
        Assert.Equal(Nombre, result.Nombre);

        // errorConstancia says nothing about either regime half, so the regime still resolves.
        Assert.NotNull(result.FiscalData);
        Assert.Equal(FiscalRegimes.ResponsableInscripto, result.FiscalData.FiscalRegime);

        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("errorConstancia", warning);
        Assert.Contains("2 error entries", warning);
        AssertCarriesNoPersonalData(warning);
        Assert.Empty(logger.Errors);
    }

    [Fact]
    public void ParseGetPersonaV2Response_SeveralErrorSections_LogsExactlyOneWarningNamingEach()
    {
        var logger = new RecordingLogger<ArcaConstanciaService>();
        var xml = Envelope(
            DatosGenerales(HumanaCuit, "FISICA", apellido: Apellido, nombre: Nombre),
            DatosRegimenGeneral(Impuesto("30", "IVA", "AC")),
            ErrorRegimenGeneral(ErrorText),
            ErrorMonotributo(ErrorText, ErrorText2));

        var result = Parse(xml, logger);

        Assert.NotNull(result);
        Assert.True(result.IsIncomplete);
        Assert.NotNull(result.FiscalData);
        Assert.Equal(FiscalRegimes.Desconocido, result.FiscalData.FiscalRegime);

        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("errorRegimenGeneral", warning);
        Assert.Contains("errorMonotributo", warning);
        Assert.Contains("3 error entries", warning);
        AssertCarriesNoPersonalData(warning);
    }

    [Fact]
    public void ParseGetPersonaV2Response_ErrorRegimenGeneralAlone_WiresTheFlagAndReadsDesconocido()
    {
        // Pins the parser's WIRING of errorRegimenGeneral into ResolveFiscalRegime: without the error
        // section this same fixture reads as ResponsableInscripto (an active IVA 30).
        var logger = new RecordingLogger<ArcaConstanciaService>();
        var xml = Envelope(
            DatosGenerales(HumanaCuit, "FISICA", apellido: Apellido, nombre: Nombre),
            DatosRegimenGeneral(Impuesto("30", "IVA", "AC")),
            ErrorRegimenGeneral(ErrorText));

        var result = Parse(xml, logger);

        Assert.NotNull(result);
        Assert.True(result.IsIncomplete);
        Assert.NotNull(result.FiscalData);
        Assert.Equal(FiscalRegimes.Desconocido, result.FiscalData.FiscalRegime);

        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("errorRegimenGeneral", warning);
        Assert.Contains("1 error entries", warning);
        AssertCarriesNoPersonalData(warning);
    }

    [Fact]
    public void ParseGetPersonaV2Response_ErrorSectionAndNoRegimeSection_IsIncompleteWithNoFiscalData()
    {
        // The shape behind refresh-arca's 502 "sin datos fiscales": a person, a partial answer, and
        // no regime section to build a fiscal snapshot from — so the stored snapshot must survive.
        var logger = new RecordingLogger<ArcaConstanciaService>();
        var xml = Envelope(
            DatosGenerales(HumanaCuit, "FISICA", apellido: Apellido, nombre: Nombre),
            ErrorConstancia(ErrorText));

        var result = Parse(xml, logger);

        Assert.NotNull(result);
        Assert.True(result.IsIncomplete);
        Assert.Null(result.FiscalData);

        var warning = Assert.Single(logger.Warnings);
        AssertCarriesNoPersonalData(warning);
    }

    // ── Tests: the answers that still mean "no person" ────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParseGetPersonaV2Response_PersonaNoRegistrada_ReturnsNull(bool withDatosGenerales)
    {
        var xml = withDatosGenerales
            ? Envelope(
                El("personaNoRegistrada", "PERSONA NO REGISTRADA"),
                DatosGenerales(HumanaCuit, "FISICA", apellido: Apellido, nombre: Nombre))
            : Envelope(El("personaNoRegistrada", "PERSONA NO REGISTRADA"));

        Assert.Null(Parse(xml));
    }

    [Fact]
    public void ParseGetPersonaV2Response_ErrorSectionWithoutDatosGenerales_ReturnsNull()
    {
        var xml = Envelope(
            DatosRegimenGeneral(Impuesto("30", "IVA", "AC")),
            ErrorConstancia(ErrorText));

        Assert.Null(Parse(xml));
    }

    [Fact]
    public void ParseGetPersonaV2Response_NoDatosGeneralesAndNoErrorSection_ReturnsNull()
    {
        var xml = Envelope(DatosRegimenGeneral(Impuesto("30", "IVA", "AC")));

        Assert.Null(Parse(xml));
    }

    // ARCA's no-such-person fault is the one fault that means "no person", whatever the HTTP status.
    [Fact]
    public void ParseGetPersonaV2Response_SoapFault_ReturnsNull()
        => Assert.Null(Parse(SoapFaultXml));

    // ── Tests: every other fault, and a body with no answer, are failures ─────

    /// <summary>
    /// Only the no-such-person fault is "no person". Any other fault — even with HTTP 200 — is a
    /// failure the callers map to 502, and its text (which can echo the CUIT) is not in the message.
    /// </summary>
    [Theory]
    [InlineData("El token ha expirado")]
    [InlineData("La CUIT representada no se encuentra autorizada")]
    [InlineData("Computador no autorizado a acceder al servicio")]
    [InlineData("Error interno de base de datos")]
    [InlineData("No existe persona con ese Id 20111111112")]   // contains the phrase — not the answer
    public void ParseGetPersonaV2Response_AnyOtherSoapFault_Throws(string faultString)
    {
        var xml = SoapFaultXml.Replace("No existe persona con ese Id", faultString);

        var ex = Assert.Throws<HttpRequestException>(() => Parse(xml));

        Assert.DoesNotContain(faultString, ex.Message);
        Assert.DoesNotContain(HumanaCuit, ex.Message);
    }

    [Fact]
    public void ParseGetPersonaV2Response_NoPersonaReturnAndNoFault_Throws()
    {
        const string xml = $"""
            <soap:Envelope xmlns:soap="{SoapNs}">
              <soap:Body>
                <ns2:getPersona_v2Response xmlns:ns2="{A5Ns}"/>
              </soap:Body>
            </soap:Envelope>
            """;

        Assert.Throws<HttpRequestException>(() => Parse(xml));
    }

    // ── Tests: esSucesion ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("SI",    true)]
    [InlineData("si",    true)]
    [InlineData("S",     true)]
    [InlineData("s",     true)]
    [InlineData("true",  true)]
    [InlineData("TRUE",  true)]
    [InlineData("NO",    false)]
    [InlineData("N",     false)]
    [InlineData("false", false)]
    [InlineData(null,    null)]
    public void ParseGetPersonaV2Response_EsSucesion_ReadsSiSAndTrueAsTrueAndAnAbsentElementAsNull(
        string? wire, bool? expected)
    {
        // Before M10 only "true" counted, so ARCA's own "SI" read as false.
        var xml = Envelope(
            DatosGenerales(HumanaCuit, "FISICA", apellido: Apellido, nombre: Nombre, esSucesion: wire),
            DatosRegimenGeneral(Impuesto("30", "IVA", "AC")));

        var result = Parse(xml);

        Assert.NotNull(result);
        Assert.NotNull(result.FiscalData);
        Assert.Equal(expected, result.FiscalData.EsSucesion);
    }

    // ── Tests: an unparsable category ─────────────────────────────────────────

    [Theory]
    [InlineData("ZZ SIN CATEGORIA")]
    [InlineData("CATEGORIA X")]
    [InlineData("L OTRA")]
    public void ParseGetPersonaV2Response_UnparsableCategory_KeepsMonotributoWithNoCategoryAndWarnsWithoutTheText(
        string descripcionCategoria)
    {
        var logger = new RecordingLogger<ArcaConstanciaService>();
        var xml = Envelope(
            DatosGenerales(HumanaCuit, "FISICA", apellido: Apellido, nombre: Nombre),
            DatosMonotributo(null, descripcionCategoria, Impuesto("20", "MONOTRIBUTO", "AC")));

        var result = Parse(xml, logger);

        Assert.NotNull(result);
        Assert.False(result.IsIncomplete);
        Assert.NotNull(result.FiscalData);
        Assert.Equal(FiscalRegimes.Monotributo, result.FiscalData.FiscalRegime);
        Assert.Null(result.FiscalData.MonotributoCategoria);

        var warning = Assert.Single(logger.Warnings);
        Assert.DoesNotContain(descripcionCategoria, warning);
        AssertCarriesNoPersonalData(warning);
    }

    [Fact]
    public void ParseGetPersonaV2Response_MonotributoWithoutCategoriaMonotributo_StoresNoCategoryAndDoesNotWarn()
    {
        var logger = new RecordingLogger<ArcaConstanciaService>();
        var xml = Envelope(
            DatosGenerales(HumanaCuit, "FISICA", apellido: Apellido, nombre: Nombre),
            DatosMonotributo(null, null, Impuesto("20", "MONOTRIBUTO", "AC")));

        var result = Parse(xml, logger);

        Assert.NotNull(result);
        Assert.NotNull(result.FiscalData);
        Assert.Equal(FiscalRegimes.Monotributo, result.FiscalData.FiscalRegime);
        Assert.Null(result.FiscalData.MonotributoCategoria);
        Assert.Empty(logger.Warnings);
    }

    // ── Tests: ResolveFiscalRegime — the monotributo category ─────────────────

    [Theory]
    [InlineData("H LOCACIONES DE SERVICIO", "H",  false)]
    [InlineData("A",                        "A",  false)]
    [InlineData("k",                        "K",  false)]
    [InlineData("  d  ",                    "D",  false)]
    [InlineData("B-VENTA DE COSAS MUEBLES", "B",  false)]
    [InlineData("L OTRA",                   null, true)]
    [InlineData("HX",                       null, true)]
    [InlineData("ZZ SIN CATEGORIA",         null, true)]
    [InlineData("CATEGORIA X",              null, true)]
    [InlineData("1 CATEGORIA",              null, true)]
    [InlineData(null,                       null, false)]
    [InlineData("",                         null, false)]
    [InlineData("   ",                      null, false)]
    public void ResolveFiscalRegime_Monotributo_TakesTheLeadingLetterTokenAsTheCategory(
        string? descripcionCategoria, string? expectedCategoria, bool expectedUnparsable)
    {
        var result = Resolve(hasMonotributo: true, descripcionCategoria: descripcionCategoria);

        Assert.Equal(FiscalRegimes.Monotributo, result.FiscalRegime);
        Assert.Equal(expectedCategoria, result.MonotributoCategoria);
        Assert.Equal(expectedUnparsable, result.CategoriaUnparsable);
    }

    [Fact]
    public void ResolveFiscalRegime_DatosMonotributo_WinsOverEveryOtherSignal()
    {
        var result = Resolve(
            hasMonotributo:        true,
            descripcionCategoria:  "C SERVICIOS",
            monotributoErrored:    true,
            hasRegimenGeneral:     true,
            regimenGeneralErrored: true,
            impuestos:             [Imp("32", "AC"), Imp("30", "AC")]);

        Assert.Equal(new ArcaFiscalRegimeResult(FiscalRegimes.Monotributo, "C", CategoriaUnparsable: false), result);
    }

    // ── Tests: ResolveFiscalRegime — what degrades to Desconocido ────────────

    [Fact]
    public void ResolveFiscalRegime_MonotributoErrored_IsDesconocidoEvenWithAnActiveIva()
    {
        // The monotributo half failed, so monotributo cannot be ruled out.
        var result = Resolve(
            monotributoErrored: true,
            hasRegimenGeneral:  true,
            impuestos:          [Imp("30", "AC")]);

        Assert.Equal(Desconocido, result);
    }

    [Fact]
    public void ResolveFiscalRegime_RegimenGeneralErrored_IsDesconocidoEvenWithAnActiveIva()
    {
        var result = Resolve(
            hasRegimenGeneral:     true,
            regimenGeneralErrored: true,
            impuestos:             [Imp("30", "AC")]);

        Assert.Equal(Desconocido, result);
    }

    [Fact]
    public void ResolveFiscalRegime_RegimenGeneralWithNoIvaImpuesto_IsDesconocido()
    {
        var result = Resolve(hasRegimenGeneral: true, impuestos: [Imp("11", "AC"), Imp("20", "AC")]);

        Assert.Equal(Desconocido, result);
    }

    [Fact]
    public void ResolveFiscalRegime_NoSectionAtAll_IsDesconocido()
        => Assert.Equal(Desconocido, Resolve());

    // ── Tests: ResolveFiscalRegime — IVA and IVA exento ───────────────────────

    [Fact]
    public void ResolveFiscalRegime_ActiveIvaExentoBeatsActiveIva_IsExento()
    {
        var result = Resolve(hasRegimenGeneral: true, impuestos: [Imp("30", "AC"), Imp("32", "AC")]);

        Assert.Equal(new ArcaFiscalRegimeResult(FiscalRegimes.Exento, null, CategoriaUnparsable: false), result);
    }

    [Fact]
    public void ResolveFiscalRegime_InactiveIvaExentoBesideActiveIva_IsResponsableInscripto()
    {
        var result = Resolve(hasRegimenGeneral: true, impuestos: [Imp("32", "BD"), Imp("30", "AC")]);

        Assert.Equal(FiscalRegimes.ResponsableInscripto, result.FiscalRegime);
    }

    [Theory]
    [InlineData("AC",     FiscalRegimes.ResponsableInscripto)]
    [InlineData("ac",     FiscalRegimes.ResponsableInscripto)]
    [InlineData(" AC ",   FiscalRegimes.ResponsableInscripto)]
    [InlineData("ACTIVO", FiscalRegimes.ResponsableInscripto)]
    [InlineData("Activo", FiscalRegimes.ResponsableInscripto)]
    [InlineData(null,     FiscalRegimes.ResponsableInscripto)]
    [InlineData("",       FiscalRegimes.ResponsableInscripto)]
    [InlineData("BD",     FiscalRegimes.Desconocido)]
    [InlineData("EX",     FiscalRegimes.Desconocido)]
    [InlineData("NA",     FiscalRegimes.Desconocido)]
    [InlineData("ACTIVA", FiscalRegimes.Desconocido)]
    public void ResolveFiscalRegime_Iva_CountsOnlyAnActiveEstadoImpuesto(string? estadoImpuesto, string expectedRegime)
    {
        // An unexpected code degrades to Desconocido rather than to a wrong regime.
        var result = Resolve(hasRegimenGeneral: true, impuestos: [Imp("30", estadoImpuesto)]);

        Assert.Equal(expectedRegime, result.FiscalRegime);
        Assert.Null(result.MonotributoCategoria);
        Assert.False(result.CategoriaUnparsable);
    }

    [Theory]
    [InlineData("30",   FiscalRegimes.ResponsableInscripto)]
    [InlineData(" 30 ", FiscalRegimes.ResponsableInscripto)]
    [InlineData("030",  FiscalRegimes.Desconocido)]
    [InlineData("300",  FiscalRegimes.Desconocido)]
    [InlineData(null,   FiscalRegimes.Desconocido)]
    public void ResolveFiscalRegime_IdImpuesto_IsComparedTrimmedAndOrdinal(string? idImpuesto, string expectedRegime)
    {
        var result = Resolve(hasRegimenGeneral: true, impuestos: [Imp(idImpuesto, "AC")]);

        Assert.Equal(expectedRegime, result.FiscalRegime);
    }
}
