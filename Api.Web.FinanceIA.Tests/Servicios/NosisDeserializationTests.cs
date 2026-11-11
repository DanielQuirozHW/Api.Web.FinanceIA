// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-24

using System.Net;
using System.Text;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Models.Nosis;
using Api.Web.FinanceIA.Servicios;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Covers the NosisService pipeline and its tri-state (DataFound / NoDataReported / LookupFailed),
/// plus the flat-variable projection in NosisVariableProjection.
///
/// The parsing assertions are the point of this file. NOSIS returns es-AR comma decimals — confirmed
/// against the official variable dictionary — so <c>"1.234.567,89"</c> MUST read as 1234567.89 and
/// <c>"0,41"</c> as 0.41. CasfogService parses the same shape of feed with InvariantCulture and marks
/// the vendor format UNCONFIRMED; these tests exist so that gap cannot be copied here silently.
/// Booleans arrive as the TEXT "Si"/"No", and the vendor's date-shaped variables — CI_Sit1_Desde, a
/// YYYYMM period, and VI_FecNacimiento — must never be round-tripped through DateTime.
///
/// NOSIS makes ONE call per lookup (header-auth GET), so a single stub handler suffices — no login
/// leg, no 401 retry, unlike CASFOG. No live API call is made: the vendor key is not present in the
/// test environment.
/// </summary>
public sealed class NosisDeserializationTests
{
    private const long TestCuit = 30123456789L;
    private const string Tenant = "tenant-1";

    // ── Fixture JSON ──────────────────────────────────────────────────────────
    // Resultado.Estado 200; the 25 variables of the BASE VR group — what the vendor actually answers
    // today. Deliberately NOT extended with the analyst's primary set: this fixture's job is to model
    // the live response, which is what makes it the input for the "not sent yet" test below.
    // Monetary values are written the way NOSIS writes them: es-AR, '.' grouping and ',' decimal.
    private const string FixtureJson = """
        {
          "Contenido": {
            "Resultado": { "Estado": "200", "Novedad": "" },
            "Datos": {
              "Variables": [
                { "Nombre": "VI_Identificacion",           "Valor": "30-12345678-9", "Tipo": "Documento", "FechaAct": "20260801" },
                { "Nombre": "VI_RazonSocial",              "Valor": "PYME EJEMPLO SRL", "Tipo": "Texto",  "FechaAct": "20260801" },
                { "Nombre": "DF_Tiene",                    "Valor": "Si",            "Tipo": "Booleano",  "FechaAct": "20260801" },
                { "Nombre": "NSE",                         "Valor": "C1",            "Tipo": "Texto",     "FechaAct": "20260801" },
                { "Nombre": "NSE_Percentil",               "Valor": "72",            "Tipo": "Entero",    "FechaAct": "20260801" },
                { "Nombre": "FE",                          "Valor": "5",             "Tipo": "Entero",    "FechaAct": "20260801" },
                { "Nombre": "SCO_Vig",                     "Valor": "612",           "Tipo": "Entero",    "FechaAct": "20260801" },
                { "Nombre": "SCOServ_Vig",                 "Valor": "588",           "Tipo": "Entero",    "FechaAct": "20260801" },
                { "Nombre": "SCO_Vig_Fintech",             "Valor": "601",           "Tipo": "Entero",    "FechaAct": "20260801" },
                { "Nombre": "CI_Vig_PeorSit",              "Valor": "1",             "Tipo": "Texto",     "FechaAct": "20260801" },
                { "Nombre": "CI_Vig_Total_CantBcos",       "Valor": "4",             "Tipo": "Entero",    "FechaAct": "20260801" },
                { "Nombre": "CI_Vig_Total_Monto",          "Valor": "1.234.567,89",  "Tipo": "Moneda",    "FechaAct": "20260801" },
                { "Nombre": "CI_Vig_Sit1_CantBcos",        "Valor": "3",             "Tipo": "Entero",    "FechaAct": "20260801" },
                { "Nombre": "CI_Vig_CompMensual",          "Valor": "45.678,50",     "Tipo": "Moneda",    "FechaAct": "20260801" },
                { "Nombre": "CI_Sit1_Desde",               "Valor": "201601",        "Tipo": "Periodo",   "FechaAct": "20260801" },
                { "Nombre": "CI_6m_PeorSit",               "Valor": "1",             "Tipo": "Texto",     "FechaAct": "20260801" },
                { "Nombre": "CI_12m_PeorSit",              "Valor": "2",             "Tipo": "Texto",     "FechaAct": "20260801" },
                { "Nombre": "HC_12m_SF_Cant",              "Valor": "0",             "Tipo": "Entero",    "FechaAct": "20260801" },
                { "Nombre": "IN_12m_PagoMulta_Tiene",      "Valor": "No",            "Tipo": "Booleano",  "FechaAct": "20260801" },
                { "Nombre": "PQ_12m_Cant",                 "Valor": "0",             "Tipo": "Entero",    "FechaAct": "20260801" },
                { "Nombre": "RC_Vig_Cant",                 "Valor": "2",             "Tipo": "Entero",    "FechaAct": "20260801" },
                { "Nombre": "AP_Vig_Empleador_Estado_Cod", "Valor": "1",             "Tipo": "Entero",    "FechaAct": "20260801" },
                { "Nombre": "AP_Vig_Empleado_Estado_Cod",  "Valor": "0",             "Tipo": "Entero",    "FechaAct": "20260801" },
                { "Nombre": "CO_12m_Cant",                 "Valor": "17",            "Tipo": "Entero",    "FechaAct": "20260801" },
                { "Nombre": "CO_1m_Cant",                  "Valor": "3",             "Tipo": "Entero",    "FechaAct": "20260801" }
              ]
            }
          }
        }
        """;

    // All 41 variables of the analyst's primary set — what a lookup will look like once the vendor
    // widens the configured Nosis:VR report group. That widening is a separate vendor-side change,
    // so this shape is not yet observable in production; the mapping is deliberately in place ahead
    // of it. Values chosen to exercise every parse helper: "Si"/"No" booleans, es-AR comma decimals,
    // plain integers and free text.
    private const string WidenedVrFixtureJson = """
        {
          "Contenido": {
            "Resultado": { "Estado": "200", "Novedad": "" },
            "Datos": {
              "Variables": [
                { "Nombre": "VI_Fallecido_Es",                   "Valor": "No",           "Tipo": "Booleano", "FechaAct": "20260801" },
                { "Nombre": "VI_DNI",                            "Valor": "12345678",     "Tipo": "Documento","FechaAct": "20260801" },
                { "Nombre": "VI_FecNacimiento",                  "Valor": "19750312",     "Tipo": "Fecha",    "FechaAct": "20260801" },
                { "Nombre": "VI_Sexo",                           "Valor": "M",            "Tipo": "Texto",    "FechaAct": "20260801" },
                { "Nombre": "VI_Edad",                           "Valor": "51",           "Tipo": "Entero",   "FechaAct": "20260801" },
                { "Nombre": "VI_DomAF_Loc",                      "Valor": "ROSARIO",      "Tipo": "Texto",    "FechaAct": "20260801" },
                { "Nombre": "VI_DomAF_Prov",                     "Valor": "SANTA FE",     "Tipo": "Texto",    "FechaAct": "20260801" },
                { "Nombre": "SCO_24m",                           "Valor": "574",          "Tipo": "Entero",   "FechaAct": "20260801" },
                { "Nombre": "CI_12m_Total_CantBcos",             "Valor": "6",            "Tipo": "Entero",   "FechaAct": "20260801" },
                { "Nombre": "CI_Vig_TC_Monto",                   "Valor": "1.234.567,89", "Tipo": "Moneda",   "FechaAct": "20260801" },
                { "Nombre": "CI_Vig_Li_CC_Monto",                "Valor": "250.000,00",   "Tipo": "Moneda",   "FechaAct": "20260801" },
                { "Nombre": "CI_Vig_Li_HI_Monto",                "Valor": "80.500,25",    "Tipo": "Moneda",   "FechaAct": "20260801" },
                { "Nombre": "CI_Vig_Li_PR_Monto",                "Valor": "12.000,00",    "Tipo": "Moneda",   "FechaAct": "20260801" },
                { "Nombre": "CI_Vig_Li_OPR_Monto",               "Valor": "3.750,10",     "Tipo": "Moneda",   "FechaAct": "20260801" },
                { "Nombre": "CI_Vig_LargoPlazo",                 "Valor": "900.000,00",   "Tipo": "Moneda",   "FechaAct": "20260801" },
                { "Nombre": "CI_Vig_CortoPlazo",                 "Valor": "334.567,89",   "Tipo": "Moneda",   "FechaAct": "20260801" },
                { "Nombre": "VR_12m_Cumplimiento",               "Valor": "NORMAL",       "Tipo": "Texto",    "FechaAct": "20260801" },
                { "Nombre": "VR_60m_Atraso_Cumplimiento_v2",     "Valor": "SIN ATRASOS",  "Tipo": "Texto",    "FechaAct": "20260801" },
                { "Nombre": "RC_Vig_Fuente",                     "Valor": "BCRA",         "Tipo": "Texto",    "FechaAct": "20260801" },
                { "Nombre": "JU_60m_RZ_Cant",                    "Valor": "1",            "Tipo": "Entero",   "FechaAct": "20260801" },
                { "Nombre": "CQ_60m_RZ_Cant",                    "Valor": "0",            "Tipo": "Entero",   "FechaAct": "20260801" },
                { "Nombre": "MN_24m_Cant",                       "Valor": "4",            "Tipo": "Entero",   "FechaAct": "20260801" },
                { "Nombre": "AP_6m_Empleador_Pagos_Cant",        "Valor": "6",            "Tipo": "Entero",   "FechaAct": "20260801" },
                { "Nombre": "AP_6m_Empleador_PagoParcial_Cant",  "Valor": "0",            "Tipo": "Entero",   "FechaAct": "20260801" },
                { "Nombre": "AP_6m_Empleador_Impagos_Cant",      "Valor": "0",            "Tipo": "Entero",   "FechaAct": "20260801" },
                { "Nombre": "AP_12m_Empleador_Pagos_Cant",       "Valor": "11",           "Tipo": "Entero",   "FechaAct": "20260801" },
                { "Nombre": "AP_12m_Empleador_PagoParcial_Cant", "Valor": "1",            "Tipo": "Entero",   "FechaAct": "20260801" },
                { "Nombre": "AP_12m_Empleador_Impagos_Cant",     "Valor": "0",            "Tipo": "Entero",   "FechaAct": "20260801" },
                { "Nombre": "AP_24m_Empleador_Pagos_Cant",       "Valor": "22",           "Tipo": "Entero",   "FechaAct": "20260801" },
                { "Nombre": "AP_24m_Empleador_PagoParcial_Cant", "Valor": "1",            "Tipo": "Entero",   "FechaAct": "20260801" },
                { "Nombre": "AP_24m_Empleador_Impagos_Cant",     "Valor": "1",            "Tipo": "Entero",   "FechaAct": "20260801" },
                { "Nombre": "SRT_ContratoNro",                   "Valor": "ART-998877",   "Tipo": "Texto",    "FechaAct": "20260801" },
                { "Nombre": "SRT_Aseguradora",                   "Valor": "PREVENCION",   "Tipo": "Texto",    "FechaAct": "20260801" },
                { "Nombre": "SRT_Estado",                        "Valor": "VIGENTE",      "Tipo": "Texto",    "FechaAct": "20260801" },
                { "Nombre": "SRT_FaltaPago",                     "Valor": "No",           "Tipo": "Texto",    "FechaAct": "20260801" },
                { "Nombre": "CE_Importador_Es",                  "Valor": "Si",           "Tipo": "Booleano", "FechaAct": "20260801" },
                { "Nombre": "CE_Exportador_Es",                  "Valor": "No",           "Tipo": "Booleano", "FechaAct": "20260801" },
                { "Nombre": "CE_Importador_Hist_Es",             "Valor": "Si",           "Tipo": "Booleano", "FechaAct": "20260801" },
                { "Nombre": "CE_Exportador_Hist_Es",             "Valor": "Si",           "Tipo": "Booleano", "FechaAct": "20260801" },
                { "Nombre": "CO_6m_Cant",                        "Valor": "9",            "Tipo": "Entero",   "FechaAct": "20260801" },
                { "Nombre": "CO_3m_Cant",                        "Valor": "5",            "Tipo": "Entero",   "FechaAct": "20260801" }
              ]
            }
          }
        }
        """;

    // The 16 "green set" variables the vendor added when it widened the report groups: the labour /
    // AFIP-inscription block, the subject's employer, the COM_* compliance flags and the SC_* count.
    // These are the variables that make a FIADOR lookup worth making, which is why they are pinned
    // separately from the primary set above. Values chosen to exercise every parse helper AND the
    // two typing decisions that are easy to get wrong: COM_SO_Es / COM_PEP_Es arrive as graded free
    // text, NOT as Si/No, and VI_Inscrip_IVA carries no _Es suffix.
    private const string GreenSetFixtureJson = """
        {
          "Contenido": {
            "Resultado": { "Estado": "200", "Novedad": "" },
            "Datos": {
              "Variables": [
                { "Nombre": "VI_Inscrip_Afip_Antiguedad",           "Valor": "14",             "Tipo": "Entero",   "FechaAct": "20260801" },
                { "Nombre": "VI_Inscrip_Monotributo",               "Valor": "C",              "Tipo": "Texto",    "FechaAct": "20260801" },
                { "Nombre": "VI_Inscrip_IVA",                       "Valor": "Responsable Inscripto", "Tipo": "Texto", "FechaAct": "20260801" },
                { "Nombre": "VI_Jubilado_Es",                       "Valor": "No",             "Tipo": "Booleano", "FechaAct": "20260801" },
                { "Nombre": "VI_Empleado_Es",                       "Valor": "Si",             "Tipo": "Booleano", "FechaAct": "20260801" },
                { "Nombre": "VI_Empleado_12m_Es",                   "Valor": "Si",             "Tipo": "Booleano", "FechaAct": "20260801" },
                { "Nombre": "VI_IntegranteSociedad_Es",             "Valor": "Si",             "Tipo": "Booleano", "FechaAct": "20260801" },
                { "Nombre": "VI_Empleados_Cant",                    "Valor": "7",              "Tipo": "Entero",   "FechaAct": "20260801" },
                { "Nombre": "VI_AntiguedadLaboral",                 "Valor": "22",             "Tipo": "Entero",   "FechaAct": "20260801" },
                { "Nombre": "VI_AntiguedadLaboral_EmpleadorActual", "Valor": "6",              "Tipo": "Entero",   "FechaAct": "20260801" },
                { "Nombre": "VI_Empleador_CUIT",                    "Valor": "30-70123456-4",  "Tipo": "Texto",    "FechaAct": "20260801" },
                { "Nombre": "VI_Empleador_RZ",                      "Valor": "METALURGICA DEL LITORAL SA", "Tipo": "Texto", "FechaAct": "20260801" },
                { "Nombre": "VI_Empleador_Empleados_Cant",          "Valor": "128",            "Tipo": "Entero",   "FechaAct": "20260801" },
                { "Nombre": "COM_SO_Es",                            "Valor": "Posible SO",     "Tipo": "Texto",    "FechaAct": "20260801" },
                { "Nombre": "COM_PEP_Es",                           "Valor": "Posible",        "Tipo": "Texto",    "FechaAct": "20260801" },
                { "Nombre": "SC_60m_Cant",                          "Valor": "3",              "Tipo": "Entero",   "FechaAct": "20260801" }
              ]
            }
          }
        }
        """;

    private const string EmptyVariablesJson = """
        { "Contenido": { "Resultado": { "Estado": "200" }, "Datos": { "Variables": [] } } }
        """;

    private const string VendorErrorJson = """
        { "Contenido": { "Resultado": { "Estado": "401", "Novedad": "API-KEY invalida" }, "Datos": null } }
        """;

    // ── Stubs ─────────────────────────────────────────────────────────────────

    /// <summary>Returns a fixed status + body, and records how many times it was called.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private int _calls;

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body   = body;
        }

        public int Calls => _calls;
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>Every send throws a fresh exception from the factory.</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Func<Exception> _factory;
        public ThrowingHandler(Func<Exception> factory) => _factory = factory;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw _factory();
    }

    /// <summary>Honours the token, exactly as a real HttpClient does when the caller cancels.</summary>
    private sealed class CancellationObservingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(FixtureJson, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private sealed class StubApiCallLogService : IApiCallLogService
    {
        public int Calls { get; private set; }
        public string? LastService { get; private set; }
        public string? LastTenantId { get; private set; }

        public Task LogAsync(string tenantId, string service, string endpoint, int? responseStatus,
            int? inputTokens, int? outputTokens, decimal? estimatedCostUsd, bool isSuccess, string? errorMessage,
            int durationMs, string? operation = null, string? model = null, Guid? userId = null,
            Guid? analysisId = null, long? chatMessageId = null, Guid? documentId = null, CancellationToken ct = default)
        {
            Calls++;
            LastService  = service;
            LastTenantId = tenantId;
            return Task.CompletedTask;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static NosisService BuildSut(
        HttpMessageHandler handler,
        bool enabled = true,
        string apiKey = "test-key",
        string vr = "42",
        IApiCallLogService? apiCallLog = null,
        IMemoryCache? cache = null,
        string? vrFiador = null)
    {
        // vrFiador defaults to null — the shipping state, in which a Fiador lookup falls back to
        // Nosis:VR. A test that cares about the fiador group sets it explicitly.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nosis:BaseUrl"]  = "https://ws01.nosis.com/rest/variables",
                ["Nosis:ApiKey"]   = apiKey,
                ["Nosis:VR"]       = vr,
                ["Nosis:VrFiador"] = vrFiador,
                ["Nosis:Enabled"]  = enabled ? "true" : "false"
            })
            .Build();

        return new NosisService(
            new StubHttpClientFactory(handler),
            cache ?? new MemoryCache(Options.Create(new MemoryCacheOptions())),
            apiCallLog ?? new StubApiCallLogService(),
            NullLogger<NosisService>.Instance,
            config);
    }

    private static NosisService BuildSut(string body, HttpStatusCode status = HttpStatusCode.OK)
        => BuildSut(new StubHandler(status, body));

    // ── Tests: DataFound ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetVariablesAsync_WhenApiReturns200WithFixture_MapsToDataFound()
    {
        var sut = BuildSut(FixtureJson);

        var result = await sut.GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);

        Assert.Equal(NosisConsultaEstado.DataFound, result.Estado);
        Assert.NotEqual(default, result.FetchedAt);
    }

    /// <summary>
    /// Scoped to the BASE VR group — the 25 variables the vendor answers today. The analyst's
    /// primary set is covered separately below; do not read this test as covering every mapped key.
    /// </summary>
    [Fact]
    public async Task GetVariablesAsync_MapsEveryStringAndIntVariableOfTheBaseVrGroupByName()
    {
        var r = await BuildSut(FixtureJson).GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);

        Assert.Equal("30-12345678-9",    r.Identificacion);
        Assert.Equal("PYME EJEMPLO SRL", r.RazonSocial);
        Assert.Equal("C1",               r.Nse);
        Assert.Equal(72,                 r.NsePercentil);
        Assert.Equal(5,                  r.FacturacionEstimada);
        Assert.Equal(612,                r.ScoreVigente);
        Assert.Equal(588,                r.ScoreServVigente);
        Assert.Equal(601,                r.ScoreVigenteFintech);
        Assert.Equal("1",                r.PeorSituacionVigente);
        Assert.Equal(4,                  r.CantidadBancosVigente);
        Assert.Equal(3,                  r.CantidadBancosSituacion1Vigente);
        Assert.Equal("1",                r.PeorSituacion6m);
        Assert.Equal("2",                r.PeorSituacion12m);
        Assert.Equal(0,                  r.ChequesSinFondos12m);
        Assert.Equal(0,                  r.PedidosQuiebra12m);
        Assert.Equal(2,                  r.RcVigenteCantidad);
        Assert.Equal(1,                  r.EstadoEmpleadorCod);
        Assert.Equal(0,                  r.EstadoEmpleadoCod);
        Assert.Equal(17,                 r.Consultas12m);
        Assert.Equal(3,                  r.Consultas1m);
    }

    // ── Tests: the analyst's primary set (pending a wider Nosis:VR) ───────────
    // The 41 variables the analyst selected are mapped ahead of the vendor widening the configured
    // Nosis:VR report group. Until that lands the live answer is still the base 25, so both halves
    // are pinned here: what the projection does once the keys arrive, and what it does meanwhile.

    [Fact]
    public async Task GetVariablesAsync_WhenVendorWidensVr_MapsTheNewPrimaryVariables()
    {
        var r = await BuildSut(WidenedVrFixtureJson).GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);

        Assert.Equal(NosisConsultaEstado.DataFound, r.Estado);

        // One per parse helper: ParseBool, ParseInt, ParseString, ParseDecimal.
        Assert.True(r.EsImportador);                          // "Si"  → true
        Assert.False(r.EsExportador);                         // "No"  → false
        Assert.Equal(51,           r.Edad);
        Assert.Equal("PREVENCION", r.SrtAseguradora);
        Assert.Equal(250000.00m,   r.MontoLiCcVigente);

        // Then a spot check per region, so a key wired to the wrong field is caught.
        Assert.False(r.EsFallecido);
        Assert.Equal("12345678",     r.Dni);
        Assert.Equal("SANTA FE",     r.DomAfProvincia);
        Assert.Equal(574,            r.Score24m);
        Assert.Equal(6,              r.CantidadBancos12m);
        Assert.Equal("NORMAL",       r.Cumplimiento12m);
        Assert.Equal("SIN ATRASOS",  r.AtrasoCumplimiento60mV2);
        Assert.Equal("BCRA",         r.RcVigenteFuente);
        Assert.Equal(1,              r.JuiciosRz60m);
        Assert.Equal(0,              r.ConcursosQuiebrasRz60m);
        Assert.Equal(4,              r.MnCantidad24m);
        Assert.Equal(11,             r.EmpleadorPagos12m);
        Assert.Equal(1,              r.EmpleadorPagosParciales24m);
        Assert.Equal(0,              r.EmpleadorImpagos6m);
        Assert.Equal("ART-998877",   r.SrtContratoNro);
        Assert.True(r.EsImportadorHistorico);
        Assert.Equal(9,              r.Consultas6m);
        Assert.Equal(5,              r.Consultas3m);
    }

    [Fact]
    public async Task GetVariablesAsync_WhenVendorWidensVr_KeepsDateShapedAndUntypedVariablesAsText()
    {
        var r = await BuildSut(WidenedVrFixtureJson).GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);

        // Same rule as CI_Sit1_Desde: the vendor's date never round-trips through DateTime, so no
        // timezone can shift it. It is stored exactly as sent.
        Assert.Equal("19750312", r.FechaNacimiento);

        // SRT_FaltaPago is deliberately string?, not bool? — it carries no _Es/_Tiene suffix and we
        // hold no sample proving it is a Si/No variable. A "No" must survive as text, not become false.
        Assert.Equal("No", r.SrtFaltaPago);
    }

    [Fact]
    public async Task GetVariablesAsync_WhenVendorWidensVr_ParsesTheNewMonedaFieldsWithEsArCommaDecimals()
    {
        var r = await BuildSut(WidenedVrFixtureJson).GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);

        // The CASFOG lesson again, on the new CI amounts: '.' groups, ',' decides. Under
        // InvariantCulture "3.750,10" would not parse at all.
        Assert.Equal(1234567.89m, r.MontoTcVigente);
        Assert.Equal(80500.25m,   r.MontoLiHiVigente);
        Assert.Equal(12000.00m,   r.MontoLiPrVigente);
        Assert.Equal(3750.10m,    r.MontoLiOprVigente);
        Assert.Equal(900000.00m,  r.LargoPlazoVigente);
        Assert.Equal(334567.89m,  r.CortoPlazoVigente);
    }

    /// <summary>
    /// The LIVE situation until the vendor widens Nosis:VR. Every newly mapped key is simply a
    /// dictionary miss, so it must project to null — and, just as importantly, must not disturb the
    /// 25 fields that DO arrive. This is what makes shipping the mapping early safe.
    /// </summary>
    // ── Tests: the green set (labour, employer, compliance) ──────────────────
    // The 16 variables the vendor added when it widened the report groups. These are what makes a
    // FIADOR lookup worth making; the fiador group is selected via NosisVariableGroup, covered
    // further down under "VR group selection".

    [Fact]
    public async Task GetVariablesAsync_WhenVendorWidensVrForFiadores_MapsTheGreenSetVariables()
    {
        var r = await BuildSut(GreenSetFixtureJson)
            .GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Fiador, CancellationToken.None);

        Assert.Equal(NosisConsultaEstado.DataFound, r.Estado);

        // One per parse helper that this set exercises: ParseBool, ParseInt, ParseString.
        Assert.True(r.EsEmpleado);                      // "Si" → true
        Assert.False(r.EsJubilado);                     // "No" → false
        Assert.Equal(14, r.InscripAfipAntiguedad);
        Assert.Equal("C", r.InscripMonotributo);

        // Then a spot check per new region, so a key wired to the wrong field is caught.
        Assert.True(r.EsEmpleado12m);
        Assert.True(r.EsIntegranteSociedad);
        Assert.Equal(7,   r.EmpleadosCant);
        Assert.Equal(22,  r.AntiguedadLaboral);
        Assert.Equal(6,   r.AntiguedadLaboralEmpleadorActual);
        Assert.Equal("30-70123456-4", r.EmpleadorCuit);
        Assert.Equal("METALURGICA DEL LITORAL SA", r.EmpleadorRazonSocial);
        Assert.Equal(128, r.EmpleadorEmpleadosCant);
        Assert.Equal(3,   r.ScCantidad60m);
    }

    /// <summary>
    /// The two typing decisions in this set that are easy to get wrong, pinned so a later "tidy-up"
    /// cannot quietly undo them.
    ///
    /// <para>
    /// COM_SO_Es and COM_PEP_Es carry the _Es suffix this file otherwise treats as a Si/No marker,
    /// but the vendor answers them with graded free text. ParseBool maps anything outside
    /// si/sí/no to NULL, so typing them bool? would silently erase a UIF sujeto-obligado or PEP
    /// finding — a regulatory miss, not a formatting one. VI_Inscrip_IVA is string for the plainer
    /// SrtFaltaPago reason: no _Es/_Tiene suffix and no raw sample proving it is Si/No.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GetVariablesAsync_KeepsComplianceAndCategoryVariablesAsText()
    {
        var r = await BuildSut(GreenSetFixtureJson)
            .GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Fiador, CancellationToken.None);

        Assert.Equal("Posible SO", r.SujetoObligado);
        Assert.Equal("Posible",    r.Pep);
        Assert.Equal("C",          r.InscripMonotributo);
        Assert.Equal("Responsable Inscripto", r.InscripIva);
    }

    /// <summary>
    /// The same free text through the boolean helper is null — the concrete loss the string typing
    /// above avoids. If someone retypes SujetoObligado to bool?, this is what they would be shipping.
    /// </summary>
    [Fact]
    public async Task GetVariablesAsync_ComplianceFreeText_WouldHaveBeenLostAsABoolean()
    {
        var r = await BuildSut(BuildSingleVariableJson("DF_Tiene", "Posible SO"))
            .GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Fiador, CancellationToken.None);

        Assert.Null(r.DfTiene);
    }

    /// <summary>
    /// HasContent is a 1:1 mirror of the mapped keys, so a response carrying ONLY a compliance flag
    /// still counts as real data. Getting this wrong would persist a fiador who IS flagged as a
    /// possible sujeto obligado as "NOSIS has nothing on this person".
    /// </summary>
    [Fact]
    public async Task GetVariablesAsync_WhenOnlyAGreenSetVariableArrives_StillMapsToDataFound()
    {
        var json = BuildSingleVariableJson("COM_SO_Es", "Posible SO");

        var r = await BuildSut(json)
            .GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Fiador, CancellationToken.None);

        Assert.Equal(NosisConsultaEstado.DataFound, r.Estado);
        Assert.Equal("Posible SO", r.SujetoObligado);
    }

    /// <summary>
    /// The employer's headcount and the subject's own are different variables and must not be
    /// crossed: VI_Empleados_Cant is the person's, VI_Empleador_Empleados_Cant is their employer's.
    /// The names differ by two letters, which is exactly why this is pinned on its own.
    /// </summary>
    [Fact]
    public async Task GetVariablesAsync_DoesNotCrossTheSubjectAndEmployerHeadcounts()
    {
        var r = await BuildSut(BuildSingleVariableJson("VI_Empleador_Empleados_Cant", "128"))
            .GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Fiador, CancellationToken.None);

        Assert.Equal(128, r.EmpleadorEmpleadosCant);
        Assert.Null(r.EmpleadosCant);
    }

    [Fact]
    public async Task GetVariablesAsync_WhenTheNewVariablesAreAbsent_LeavesTheirFieldsNull()
    {
        var r = await BuildSut(FixtureJson).GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);

        Assert.Null(r.EsFallecido);
        Assert.Null(r.Dni);
        Assert.Null(r.FechaNacimiento);
        Assert.Null(r.Sexo);
        Assert.Null(r.Edad);
        Assert.Null(r.DomAfLocalidad);
        Assert.Null(r.DomAfProvincia);
        Assert.Null(r.Score24m);
        Assert.Null(r.CantidadBancos12m);
        Assert.Null(r.MontoTcVigente);
        Assert.Null(r.MontoLiCcVigente);
        Assert.Null(r.MontoLiOprVigente);
        Assert.Null(r.LargoPlazoVigente);
        Assert.Null(r.CortoPlazoVigente);
        Assert.Null(r.Cumplimiento12m);
        Assert.Null(r.AtrasoCumplimiento60mV2);
        Assert.Null(r.RcVigenteFuente);
        Assert.Null(r.JuiciosRz60m);
        Assert.Null(r.ConcursosQuiebrasRz60m);
        Assert.Null(r.MnCantidad24m);
        Assert.Null(r.EmpleadorPagos12m);
        Assert.Null(r.EmpleadorPagosParciales12m);
        Assert.Null(r.EmpleadorImpagos24m);
        Assert.Null(r.SrtContratoNro);
        Assert.Null(r.SrtAseguradora);
        Assert.Null(r.SrtEstado);
        Assert.Null(r.SrtFaltaPago);
        Assert.Null(r.EsImportador);
        Assert.Null(r.EsExportador);
        Assert.Null(r.EsImportadorHistorico);
        Assert.Null(r.EsExportadorHistorico);
        Assert.Null(r.Consultas6m);
        Assert.Null(r.Consultas3m);

        // The green set is absent from the base group too, and nulls the same way. A fiador that has
        // never been asked must be indistinguishable from one the vendor answered with nothing on
        // these keys — at the field level. The DIFFERENCE between those two lives in Estado, not here.
        Assert.Null(r.InscripAfipAntiguedad);
        Assert.Null(r.InscripMonotributo);
        Assert.Null(r.InscripIva);
        Assert.Null(r.EsJubilado);
        Assert.Null(r.EsEmpleado);
        Assert.Null(r.EsEmpleado12m);
        Assert.Null(r.EsIntegranteSociedad);
        Assert.Null(r.EmpleadosCant);
        Assert.Null(r.AntiguedadLaboral);
        Assert.Null(r.AntiguedadLaboralEmpleadorActual);
        Assert.Null(r.EmpleadorCuit);
        Assert.Null(r.EmpleadorRazonSocial);
        Assert.Null(r.EmpleadorEmpleadosCant);
        Assert.Null(r.SujetoObligado);
        Assert.Null(r.Pep);
        Assert.Null(r.ScCantidad60m);

        // The pre-existing 25 are untouched by the widening — including the three keys the analyst
        // also selected, which were ALREADY mapped and must not have been duplicated onto new fields.
        Assert.Equal(NosisConsultaEstado.DataFound, r.Estado);
        Assert.Equal(612,       r.ScoreVigente);              // SCO_Vig, not SCO_24m
        Assert.Equal("2",       r.PeorSituacion12m);          // CI_12m_PeorSit — already mapped
        Assert.Equal(45678.50m, r.CompromisoMensualVigente);  // CI_Vig_CompMensual — already mapped
        Assert.Equal(1,         r.EstadoEmpleadorCod);        // AP_Vig_Empleador_Estado_Cod — already mapped
        Assert.Equal(2,         r.RcVigenteCantidad);         // RC_Vig_Cant, not RC_Vig_Fuente
        Assert.Equal(17,        r.Consultas12m);
        Assert.Equal(3,         r.Consultas1m);
    }

    /// <summary>
    /// HasContent gates DataFound → NoDataReported. It mirrors all 82 mapped keys, so a response
    /// carrying ONLY a newly mapped variable still counts as real data rather than "nothing on file".
    /// </summary>
    [Fact]
    public async Task GetVariablesAsync_WhenOnlyANewVariableArrives_StillMapsToDataFound()
    {
        var json = BuildSingleVariableJson("SRT_Aseguradora", "PREVENCION");

        var r = await BuildSut(json).GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);

        Assert.Equal(NosisConsultaEstado.DataFound, r.Estado);
        Assert.Equal("PREVENCION", r.SrtAseguradora);
    }

    // ── Tests: the es-AR decimal rule (the CASFOG lesson) ─────────────────────

    [Fact]
    public async Task GetVariablesAsync_ParsesMonedaWithEsArCommaDecimals_NotInvariantCulture()
    {
        var r = await BuildSut(FixtureJson).GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);

        // "1.234.567,89" — '.' is the GROUP separator and ',' the decimal separator.
        // Under InvariantCulture this would come back as 1234567.89 only by accident of
        // AllowThousands, and "45.678,50" would fail outright. Pin both.
        Assert.Equal(1234567.89m, r.MontoTotalVigente);
        Assert.Equal(45678.50m,   r.CompromisoMensualVigente);
    }

    [Theory]
    [InlineData("0,41",          0.41)]
    [InlineData("133,78",      133.78)]
    [InlineData("1.000,00",   1000.00)]
    [InlineData("999",         999.00)]
    public async Task GetVariablesAsync_ParsesRepresentativeEsArDecimals(string wire, double expected)
    {
        var json = BuildSingleVariableJson("CI_Vig_Total_Monto", wire);

        var r = await BuildSut(json).GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);

        Assert.Equal((decimal)expected, r.MontoTotalVigente);
    }

    // ── Tests: "Si"/"No" text booleans ────────────────────────────────────────

    [Theory]
    [InlineData("Si",   true)]
    [InlineData("si",   true)]
    [InlineData("SÍ",   true)]
    [InlineData("No",   false)]
    [InlineData("NO",   false)]
    [InlineData("true", null)]   // NOSIS never sends this — a guess would be worse than null
    [InlineData("1",    null)]
    [InlineData("",     null)]
    [InlineData("   ",  null)]
    public async Task GetVariablesAsync_ParsesBooleanoAsSiNoText(string wire, bool? expected)
    {
        var json = BuildSingleVariableJson("DF_Tiene", wire);

        var r = await BuildSut(json).GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);

        Assert.Equal(expected, r.DfTiene);
    }

    // ── Tests: Período stays a string ─────────────────────────────────────────

    [Fact]
    public async Task GetVariablesAsync_KeepsPeriodoAsRawYyyyMmString_NeverADateTime()
    {
        var r = await BuildSut(FixtureJson).GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);

        // A YYYYMM period has no day component. Parsing it into a DateTime would invent one and
        // expose it to timezone shifting on the way out — the class of bug this repo has hit before.
        Assert.Equal("201601", r.Situacion1Desde);
    }

    // ── Tests: per-field tolerance ────────────────────────────────────────────

    [Fact]
    public async Task GetVariablesAsync_WhenOneFieldIsGarbage_NullsOnlyThatFieldAndKeepsTheRest()
    {
        var json = FixtureJson.Replace("\"Valor\": \"72\"", "\"Valor\": \"no-es-un-numero\"");

        var r = await BuildSut(json).GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);

        Assert.Equal(NosisConsultaEstado.DataFound, r.Estado);
        Assert.Null(r.NsePercentil);            // the bad one
        Assert.Equal(612, r.ScoreVigente);      // its neighbours are untouched
        Assert.Equal("PYME EJEMPLO SRL", r.RazonSocial);
    }

    [Fact]
    public async Task GetVariablesAsync_WhenIntegerOverflowsInt32_YieldsNullRatherThanThrowing()
    {
        var json = BuildSingleVariableJson("CO_12m_Cant", "99999999999");

        var r = await BuildSut(json).GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);

        Assert.Null(r.Consultas12m);
    }

    [Fact]
    public async Task GetVariablesAsync_WhenAVariableIsDuplicated_TakesTheLastValueRatherThanThrowing()
    {
        const string json = """
            {
              "Contenido": {
                "Resultado": { "Estado": "200" },
                "Datos": { "Variables": [
                  { "Nombre": "SCO_Vig", "Valor": "100" },
                  { "Nombre": "SCO_Vig", "Valor": "700" }
                ] }
              }
            }
            """;

        var r = await BuildSut(json).GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);

        Assert.Equal(NosisConsultaEstado.DataFound, r.Estado);
        Assert.Equal(700, r.ScoreVigente);
    }

    [Fact]
    public async Task GetVariablesAsync_MatchesVariableNamesCaseInsensitively()
    {
        var json = BuildSingleVariableJson("sco_vig", "555");

        var r = await BuildSut(json).GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);

        Assert.Equal(555, r.ScoreVigente);
    }

    // ── Tests: vendor loose typing (number vs. string on the wire) ────────────
    //
    // NOSIS does not quote consistently. A raw JSON number landing in a string? property throws
    //   "The JSON value could not be converted to System.String. Path: $.Contenido.Resultado.Estado"
    // which aborts the WHOLE document — a healthy 200 carrying a full variable set degraded to
    // LookupFailed and the caller saw a 502. Observed in production.
    //
    // NumberHandling.AllowReadingFromString does NOT cover this: it is one-directional (numeric
    // property ← string token). FlexibleStringConverter covers the reverse.

    [Fact]
    public async Task GetVariablesAsync_WhenResultadoEstadoIsARawNumber_StillParsesAndProjects()
    {
        // The production regression: "Estado": 200 — unquoted.
        var json = FixtureJson.Replace("\"Estado\": \"200\"", "\"Estado\": 200");
        Assert.Contains("\"Estado\": 200,", json);   // guard: the Replace actually fired

        var r = await BuildSut(json).GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);

        // DataFound is only reachable through IsResultadoOk("200") returning true, so this pins
        // both halves at once: the number parsed, AND it still compares equal as a string.
        Assert.Equal(NosisConsultaEstado.DataFound, r.Estado);
        Assert.Equal(612, r.ScoreVigente);
        Assert.Equal("PYME EJEMPLO SRL", r.RazonSocial);
    }

    [Fact]
    public async Task GetVariablesAsync_WhenResultadoEstadoIsAQuotedString_StillParsesAndProjects()
    {
        // The other half of the contract: making numbers work must not break the quoted form.
        var r = await BuildSut(FixtureJson).GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);

        Assert.Equal(NosisConsultaEstado.DataFound, r.Estado);
        Assert.Equal(612, r.ScoreVigente);
    }

    [Fact]
    public async Task GetVariablesAsync_WhenResultadoEstadoIsARawNumberOtherThan200_MapsToLookupFailed()
    {
        // Tolerating the number token must not make every numeric estado "OK" — 401 is still an error.
        var json = FixtureJson.Replace("\"Estado\": \"200\"", "\"Estado\": 401");

        var r = await BuildSut(json).GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);

        Assert.Equal(NosisConsultaEstado.LookupFailed, r.Estado);
    }

    [Fact]
    public async Task GetVariablesAsync_WhenResultadoNovedadIsARawNumber_StillParsesAndProjects()
    {
        var json = FixtureJson.Replace("\"Novedad\": \"\"", "\"Novedad\": 0");

        var r = await BuildSut(json).GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);

        Assert.Equal(NosisConsultaEstado.DataFound, r.Estado);
        Assert.Equal(612, r.ScoreVigente);
    }

    [Fact]
    public async Task GetVariablesAsync_WhenAVariableValorIsARawNumber_StillParsesAndProjects()
    {
        // The second crash path, and the one the production log CANNOT rule out: System.Text.Json
        // reads sequentially and Contenido.Resultado precedes Contenido.Datos, so that parse aborted
        // before it ever reached a Variables entry. NosisVariableProjection cannot defend this —
        // its ParseInt/ParseDecimal run only after deserialization has already succeeded.
        var json = BuildSingleRawValorJson("SCO_Vig", "612");

        var r = await BuildSut(json).GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);

        Assert.Equal(NosisConsultaEstado.DataFound, r.Estado);
        Assert.Equal(612, r.ScoreVigente);
    }

    [Fact]
    public async Task GetVariablesAsync_WhenARawNumberValorIsAWholeNumber_DoesNotInventADecimalPoint()
    {
        // The converter keeps the raw token text rather than round-tripping through GetDecimal(),
        // so 17 stays "17". A reformatted "17.0" would fail ParseInt and null the field.
        var json = BuildSingleRawValorJson("CO_12m_Cant", "17");

        var r = await BuildSut(json).GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);

        Assert.Equal(17, r.Consultas12m);
    }

    [Fact]
    public async Task GetVariablesAsync_WhenAVariableValorIsAnUnexpectedObject_NullsOnlyThatFieldAndKeepsTheRest()
    {
        // Pins the reader.Skip() in the converter's default branch. Returning from a StartObject
        // without consuming the subtree leaves the reader inside it and the deserializer reads its
        // members as siblings — desynchronising the document. The variable AFTER the bad one is the
        // assertion that catches that: without Skip() the whole lookup fails instead of one field.
        const string json = """
            {
              "Contenido": {
                "Resultado": { "Estado": "200" },
                "Datos": { "Variables": [
                  { "Nombre": "VI_RazonSocial", "Valor": "PYME EJEMPLO SRL" },
                  { "Nombre": "SCO_Vig",        "Valor": { "Codigo": 1, "Texto": "raro" } },
                  { "Nombre": "CO_12m_Cant",    "Valor": "17" }
                ] }
              }
            }
            """;

        var r = await BuildSut(json).GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);

        Assert.Equal(NosisConsultaEstado.DataFound, r.Estado);
        Assert.Equal("PYME EJEMPLO SRL", r.RazonSocial);   // before the bad one
        Assert.Null(r.ScoreVigente);                       // the bad one, and only it
        Assert.Equal(17, r.Consultas12m);                  // after it — proves no desync
    }

    // ── Tests: tri-state (NoDataReported) ─────────────────────────────────────

    [Fact]
    public async Task GetVariablesAsync_When200WithNoVariables_MapsToNoDataReported()
    {
        var r = await BuildSut(EmptyVariablesJson).GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);

        Assert.Equal(NosisConsultaEstado.NoDataReported, r.Estado);
    }

    [Fact]
    public async Task GetVariablesAsync_When200WithOnlyBlankValues_MapsToNoDataReported()
    {
        var json = BuildSingleVariableJson("SCO_Vig", "   ");

        var r = await BuildSut(json).GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);

        Assert.Equal(NosisConsultaEstado.NoDataReported, r.Estado);
    }

    // ── Tests: tri-state (LookupFailed) ───────────────────────────────────────

    [Fact]
    public async Task GetVariablesAsync_WhenVendorResultadoIsNot200_MapsToLookupFailed()
    {
        // A vendor-level error is NOT "nothing on file": it must not be cached, and it must not be
        // persisted over a good stored snapshot.
        var r = await BuildSut(VendorErrorJson).GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);

        Assert.Equal(NosisConsultaEstado.LookupFailed, r.Estado);
    }

    [Fact]
    public async Task GetVariablesAsync_WhenHttp500_MapsToLookupFailed()
    {
        var sut = BuildSut(FixtureJson, HttpStatusCode.InternalServerError);

        var r = await sut.GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);

        Assert.Equal(NosisConsultaEstado.LookupFailed, r.Estado);
    }

    [Fact]
    public async Task GetVariablesAsync_WhenMalformedJsonOn200_MapsToLookupFailed()
    {
        var r = await BuildSut("{ not json ").GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);

        Assert.Equal(NosisConsultaEstado.LookupFailed, r.Estado);
    }

    [Fact]
    public async Task GetVariablesAsync_WhenHttpThrowsRequestException_MapsToLookupFailed()
    {
        var sut = BuildSut(new ThrowingHandler(() => new HttpRequestException("dns")));

        var r = await sut.GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);

        Assert.Equal(NosisConsultaEstado.LookupFailed, r.Estado);
    }

    [Fact]
    public async Task GetVariablesAsync_WhenHttpThrowsTimeout_MapsToLookupFailed()
    {
        var sut = BuildSut(new ThrowingHandler(() => new TimeoutException("slow")));

        var r = await sut.GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);

        Assert.Equal(NosisConsultaEstado.LookupFailed, r.Estado);
    }

    [Fact]
    public async Task GetVariablesAsync_WhenHttpThrowsTaskCanceled_NonCancelledToken_MapsToLookupFailed()
    {
        // A client-side timeout surfaces as TaskCanceledException even though the caller never
        // cancelled — it must degrade, not propagate.
        var sut = BuildSut(new ThrowingHandler(() => new TaskCanceledException("client timeout")));

        var r = await sut.GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);

        Assert.Equal(NosisConsultaEstado.LookupFailed, r.Estado);
    }

    [Fact]
    public async Task GetVariablesAsync_WhenHttpThrowsUnexpectedException_MapsToLookupFailed()
    {
        var sut = BuildSut(new ThrowingHandler(() => new InvalidOperationException("pool exploded")));

        var r = await sut.GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);

        Assert.Equal(NosisConsultaEstado.LookupFailed, r.Estado);
    }

    [Fact]
    public async Task GetVariablesAsync_WhenCallerCancels_PropagatesCancellation_RatherThanFakingLookupFailed()
    {
        var sut = BuildSut(new CancellationObservingHandler());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, cts.Token));
    }

    // ── Tests: the Enabled / credential gate ──────────────────────────────────

    [Fact]
    public async Task GetVariablesAsync_WhenDisabled_MakesNoHttpCallAndReturnsLookupFailed()
    {
        var handler = new StubHandler(HttpStatusCode.OK, FixtureJson);
        var sut = BuildSut(handler, enabled: false);

        var r = await sut.GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);

        Assert.Equal(NosisConsultaEstado.LookupFailed, r.Estado);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("", "42")]
    [InlineData("test-key", "")]
    [InlineData("", "")]
    public async Task GetVariablesAsync_WhenKeyOrVrMissing_MakesNoHttpCallAndReturnsLookupFailed(
        string apiKey, string vr)
    {
        var handler = new StubHandler(HttpStatusCode.OK, FixtureJson);
        var sut = BuildSut(handler, apiKey: apiKey, vr: vr);

        var r = await sut.GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);

        Assert.Equal(NosisConsultaEstado.LookupFailed, r.Estado);
        Assert.Equal(0, handler.Calls);
    }

    // ── Tests: request shape ──────────────────────────────────────────────────

    [Fact]
    public async Task GetVariablesAsync_SendsApiKeyHeaderAndRequestsJsonExplicitly()
    {
        var handler = new StubHandler(HttpStatusCode.OK, FixtureJson);
        var sut = BuildSut(handler);

        await sut.GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);

        var request = handler.LastRequest;
        Assert.NotNull(request);
        Assert.True(request!.Headers.Contains("X-API-KEY"));
        Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("X-API-KEY")));

        var url = request.RequestUri!.ToString();
        // Format=JSON is mandatory — the vendor defaults to XML, and losing it silently breaks
        // every deserialization.
        Assert.Contains("Format=JSON", url);
        Assert.Contains("FEX=TODAS", url);
        Assert.Contains($"Documento={TestCuit}", url);
        Assert.Contains("VR=42", url);
    }

    // ── Tests: VR group selection (principal vs fiador) ───────────────────────
    // NOSIS ships one report group per audience and answers a wrong group with the WRONG VARIABLES
    // rather than an error, so which VR reaches the query is a correctness question, not a config
    // detail. These tests pin it at the only place it is observable: the outgoing URL.

    [Fact]
    public async Task GetVariablesAsync_WhenGroupIsFiador_SendsVrFiadorInTheQuery()
    {
        var handler = new StubHandler(HttpStatusCode.OK, GreenSetFixtureJson);
        var sut = BuildSut(handler, vrFiador: "77");

        await sut.GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Fiador, CancellationToken.None);

        Assert.Contains("VR=77", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task GetVariablesAsync_WhenGroupIsPrincipal_SendsVrEvenWhenVrFiadorIsConfigured()
    {
        var handler = new StubHandler(HttpStatusCode.OK, FixtureJson);
        var sut = BuildSut(handler, vrFiador: "77");

        await sut.GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);

        var url = handler.LastRequest!.RequestUri!.ToString();
        Assert.Contains("VR=42", url);
        Assert.DoesNotContain("VR=77", url);
    }

    /// <summary>
    /// The shipping state: Nosis:VrFiador is empty until the group number is confirmed with the
    /// vendor, and a fiador lookup must keep working against Nosis:VR meanwhile rather than
    /// degrading to LookupFailed. Falling back to the principal group is the honest degradation —
    /// fewer of the green variables come back, but the lookup is real.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetVariablesAsync_WhenVrFiadorIsAbsentOrBlank_FallsBackToNosisVr(string? vrFiador)
    {
        var handler = new StubHandler(HttpStatusCode.OK, GreenSetFixtureJson);
        var sut = BuildSut(handler, vrFiador: vrFiador);

        var r = await sut.GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Fiador, CancellationToken.None);

        Assert.Contains("VR=42", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(NosisConsultaEstado.DataFound, r.Estado);
    }

    [Fact]
    public async Task GetVariablesAsync_WhenGroupIsFiadorAndNeitherVrIsSet_MakesNoHttpCallAndReturnsLookupFailed()
    {
        var handler = new StubHandler(HttpStatusCode.OK, GreenSetFixtureJson);
        var sut = BuildSut(handler, vr: "", vrFiador: null);

        var r = await sut.GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Fiador, CancellationToken.None);

        Assert.Equal(0, handler.Calls);
        // LookupFailed, never NoDataReported: "we did not ask" must not be persisted as
        // "NOSIS has nothing on this person".
        Assert.Equal(NosisConsultaEstado.LookupFailed, r.Estado);
    }

    /// <summary>
    /// The cache is keyed on the RESOLVED vr, so once the groups differ a principal and a fiador
    /// lookup of the SAME CUIT are two separate queries and cannot serve each other's answer. Before
    /// this, a fiador who is also the PyME's own CUIT would have read back the principal group's
    /// variables — silently, and with the green set missing.
    /// </summary>
    [Fact]
    public async Task GetVariablesAsync_DoesNotShareOneCacheEntryBetweenDistinctVrGroups()
    {
        var handler = new StubHandler(HttpStatusCode.OK, FixtureJson);
        var sut = BuildSut(handler, vrFiador: "77");

        await sut.GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);
        await sut.GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Fiador,    CancellationToken.None);

        Assert.Equal(2, handler.Calls);
    }

    /// <summary>
    /// The other half of keying on the resolved vr rather than on the group: while VrFiador is unset
    /// both groups resolve to the SAME query, so they legitimately share one cached answer instead
    /// of billing the vendor twice for an identical request.
    /// </summary>
    [Fact]
    public async Task GetVariablesAsync_WhenVrFiadorFallsBackToVr_SharesTheCachedAnswer()
    {
        var handler = new StubHandler(HttpStatusCode.OK, FixtureJson);
        var sut = BuildSut(handler, vrFiador: null);

        await sut.GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);
        await sut.GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Fiador,    CancellationToken.None);

        Assert.Equal(1, handler.Calls);
    }

    // ── Tests: caching ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetVariablesAsync_CachesASuccessfulLookupPerCuit()
    {
        var handler = new StubHandler(HttpStatusCode.OK, FixtureJson);
        var sut = BuildSut(handler);

        await sut.GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);
        await sut.GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task GetVariablesAsync_DoesNotCacheAFailedLookup()
    {
        // A transient failure must be retried on the next request, never pinned for 24 h.
        var handler = new StubHandler(HttpStatusCode.InternalServerError, "");
        var sut = BuildSut(handler);

        await sut.GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);
        await sut.GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);

        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task GetVariablesAsync_CachesNoDataReported()
    {
        var handler = new StubHandler(HttpStatusCode.OK, EmptyVariablesJson);
        var sut = BuildSut(handler);

        await sut.GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);
        await sut.GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);

        Assert.Equal(1, handler.Calls);
    }

    // ── Tests: api-call logging ───────────────────────────────────────────────

    [Fact]
    public async Task GetVariablesAsync_LogsTheCallUnderTheNosisServiceNameAndCallingTenant()
    {
        var log = new StubApiCallLogService();
        var sut = BuildSut(new StubHandler(HttpStatusCode.OK, FixtureJson), apiCallLog: log);

        await sut.GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);

        Assert.Equal(1, log.Calls);
        // Must match CK_ApiCallLogs_Service as extended by DDL 043, or the row is silently rejected.
        Assert.Equal("NOSIS", log.LastService);
        Assert.Equal(Tenant, log.LastTenantId);
    }

    [Fact]
    public async Task GetVariablesAsync_WhenDisabled_DoesNotWriteAnApiCallLogRow()
    {
        var log = new StubApiCallLogService();
        var sut = BuildSut(new StubHandler(HttpStatusCode.OK, FixtureJson), enabled: false, apiCallLog: log);

        await sut.GetVariablesAsync(Tenant, TestCuit, NosisVariableGroup.Principal, CancellationToken.None);

        Assert.Equal(0, log.Calls);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildSingleVariableJson(string nombre, string valor) => $$"""
        {
          "Contenido": {
            "Resultado": { "Estado": "200" },
            "Datos": { "Variables": [ { "Nombre": "{{nombre}}", "Valor": "{{valor}}" } ] }
          }
        }
        """;

    /// <summary>
    /// As <see cref="BuildSingleVariableJson"/>, but emits <paramref name="rawValor"/> as a RAW JSON
    /// token — unquoted — so an unquoted numeric Valor can be exercised. The sibling helper always
    /// quotes, which is exactly the shape these tests need to escape.
    /// </summary>
    private static string BuildSingleRawValorJson(string nombre, string rawValor) => $$"""
        {
          "Contenido": {
            "Resultado": { "Estado": "200" },
            "Datos": { "Variables": [ { "Nombre": "{{nombre}}", "Valor": {{rawValor}} } ] }
          }
        }
        """;
}
