// Project : Api.Web.FinanceIA.Tests
// Created : 2026-07-24

using System.Net;
using System.Text;
using System.Text.Json;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Covers the BcraService "cheques rechazados" deserialization + mapping pipeline and its
/// tri-state (ChequesFound / NoChequesReported / LookupFailed).
///
/// The tri-state 404 mapping was verified against the live public API during development:
/// GET /centraldedeudores/v1.0/Deudas/ChequesRechazados/{cuit} returns HTTP 404 with body
/// {"status":404,"errorMessages":["No se encontró datos para la identificación ingresada."]}
/// for a CUIT with no rejected cheques — a good outcome (NoChequesReported), distinct from a
/// transient LookupFailed. A CUIT WITH rejected cheques was not located during development, so
/// the monetary unit (pesos, per BCRA documentation) is asserted here against a representative
/// constructed fixture and must be confirmed against real data before the monto is surfaced.
/// </summary>
public sealed class BcraChequesDeserializationTests
{
    // ── Fixture JSON ──────────────────────────────────────────────────────────
    // 2 causales, 3 cheques total. Amounts: 150000.50 + 80000 + 25000.
    //   cheque 12345679 is PAID (fechaPago set) → pagado; the other two are impagos.
    //   cheque 99999 carries procesoJud = true and enRevision = true.
    // Includes an extra field (codigoMoneda) not present in the model — must be ignored.
    private const string FixtureJson = """
        {
          "status": 200,
          "results": {
            "identificacion": 20123456789,
            "denominacion": "JUAN PEREZ",
            "causales": [
              {
                "causal": "SIN FONDOS SUFICIENTES",
                "entidades": [
                  {
                    "entidad": 11,
                    "detalle": [
                      {
                        "nroCheque": 12345678,
                        "fechaRechazo": "2025-03-15",
                        "monto": 150000.50,
                        "fechaPago": null,
                        "fechaPagoMulta": null,
                        "estadoMulta": "IMPAGA",
                        "ctaPersonal": true,
                        "denomJuridica": "",
                        "enRevision": false,
                        "procesoJud": false,
                        "codigoMoneda": "ARS"
                      },
                      {
                        "nroCheque": 12345679,
                        "fechaRechazo": "2025-06-20",
                        "monto": 80000.00,
                        "fechaPago": "2025-07-01",
                        "fechaPagoMulta": null,
                        "estadoMulta": "PAGADA",
                        "ctaPersonal": true,
                        "denomJuridica": "",
                        "enRevision": false,
                        "procesoJud": false
                      }
                    ]
                  }
                ]
              },
              {
                "causal": "DEFECTOS FORMALES",
                "entidades": [
                  {
                    "entidad": 72,
                    "detalle": [
                      {
                        "nroCheque": 99999,
                        "fechaRechazo": "2025-01-10",
                        "monto": 25000.00,
                        "fechaPago": null,
                        "fechaPagoMulta": null,
                        "estadoMulta": "N/A",
                        "ctaPersonal": false,
                        "denomJuridica": "EMPRESA SRL",
                        "enRevision": true,
                        "procesoJud": true
                      }
                    ]
                  }
                ]
              }
            ]
          }
        }
        """;

    // BCRA 404 body for a CUIT with no rejected cheques (verified live).
    private const string NoDataJson =
        """{"status":404,"errorMessages":["No se encontró datos para la identificación ingresada."]}""";

    // A BcraData blob as written BEFORE this change: no "cheques" key.
    private const string LegacyBcraDataJson = """
        {
          "deuda": { "identificacion": 20123456789, "estado": 1, "fetchedAt": "2026-05-01T00:00:00Z" },
          "macro": { "inflacionMensual": 2.5, "tasaReferencia": 40.0, "fetchedAt": "2026-05-01T00:00:00Z" },
          "cambiario": { "tipoCambioOficial": 950.0, "fetchedAt": "2026-05-01T00:00:00Z" }
        }
        """;

    // ── Infrastructure stubs ──────────────────────────────────────────────────

    /// <summary>Returns a fixed status code + body for any request.</summary>
    private sealed class StatusResponseHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string         _body;
        public StatusResponseHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body   = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken  cancellationToken)
        {
            var response = new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public StubHttpClientFactory(HttpStatusCode status, string body)
        {
            _client = new HttpClient(new StatusResponseHandler(status, body))
            {
                BaseAddress = new Uri("https://api.bcra.gob.ar")
            };
        }

        public HttpClient CreateClient(string name) => _client;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static BcraService BuildSut(HttpStatusCode status, string body)
    {
        var factory = new StubHttpClientFactory(status, body);
        var cache   = new MemoryCache(Options.Create(new MemoryCacheOptions()));
        return BcraServiceTestFactory.Create(factory, cache);
    }

    private static BcraService BuildSut(string json) => BuildSut(HttpStatusCode.OK, json);

    // ── Tests: ChequesFound (200 with fixture) ─────────────────────────────────

    [Fact]
    public async Task GetChequesRechazadosAsync_WhenApiReturns200WithFixture_MapsToChequesFound()
    {
        var sut = BuildSut(FixtureJson);

        var result = await sut.GetChequesRechazadosAsync("20-12345678-9", CancellationToken.None);

        Assert.Equal(BcraChequesConsultaEstado.ChequesFound, result.Estado);
    }

    [Fact]
    public async Task GetChequesRechazadosAsync_WhenApiReturns200WithFixture_TotalRechazadosIsThree()
    {
        var sut = BuildSut(FixtureJson);

        var result = await sut.GetChequesRechazadosAsync("20-12345678-9", CancellationToken.None);

        Assert.Equal(3, result.TotalRechazados);
    }

    [Fact]
    public async Task GetChequesRechazadosAsync_WhenApiReturns200WithFixture_TotalImpagosIsTwo()
    {
        var sut = BuildSut(FixtureJson);

        var result = await sut.GetChequesRechazadosAsync("20-12345678-9", CancellationToken.None);

        Assert.Equal(2, result.TotalImpagos);
    }

    [Fact]
    public async Task GetChequesRechazadosAsync_WhenApiReturns200WithFixture_MontoTotalRechazadoSumsAllInPesos()
    {
        var sut = BuildSut(FixtureJson);

        var result = await sut.GetChequesRechazadosAsync("20-12345678-9", CancellationToken.None);

        Assert.Equal(255000.50m, result.MontoTotalRechazado);
    }

    [Fact]
    public async Task GetChequesRechazadosAsync_WhenApiReturns200WithFixture_MontoTotalImpagoSumsUnpaidOnly()
    {
        var sut = BuildSut(FixtureJson);

        var result = await sut.GetChequesRechazadosAsync("20-12345678-9", CancellationToken.None);

        Assert.Equal(175000.50m, result.MontoTotalImpago);
    }

    [Fact]
    public async Task GetChequesRechazadosAsync_WhenApiReturns200WithFixture_DetalleOrderedByFechaRechazoDescending()
    {
        var sut = BuildSut(FixtureJson);

        var result = await sut.GetChequesRechazadosAsync("20-12345678-9", CancellationToken.None);

        var fechas = result.Detalle!.Select(c => c.FechaRechazo).ToList();
        Assert.Equal(
            new DateTime?[] { new DateTime(2025, 6, 20), new DateTime(2025, 3, 15), new DateTime(2025, 1, 10) },
            fechas);
    }

    [Fact]
    public async Task GetChequesRechazadosAsync_WhenApiReturns200WithFixture_PagadoDerivedFromFechaPago()
    {
        var sut = BuildSut(FixtureJson);

        var result = await sut.GetChequesRechazadosAsync("20-12345678-9", CancellationToken.None);

        var paid   = result.Detalle!.Single(c => c.NroCheque == 12345679);
        var impago = result.Detalle!.Single(c => c.NroCheque == 12345678);
        Assert.True(paid.Pagado);
        Assert.False(impago.Pagado);
    }

    [Fact]
    public async Task GetChequesRechazadosAsync_WhenApiReturns200WithFixture_CarriesCausalAndEntidadOntoRows()
    {
        var sut = BuildSut(FixtureJson);

        var result = await sut.GetChequesRechazadosAsync("20-12345678-9", CancellationToken.None);

        var row = result.Detalle!.Single(c => c.NroCheque == 99999);
        Assert.Equal("DEFECTOS FORMALES", row.Causal);
        Assert.Equal(72, row.Entidad);
    }

    [Fact]
    public async Task GetChequesRechazadosAsync_WhenApiReturns200WithFixture_CausalAggregateComputed()
    {
        var sut = BuildSut(FixtureJson);

        var result = await sut.GetChequesRechazadosAsync("20-12345678-9", CancellationToken.None);

        Assert.Equal(2, result.Causales!.Count);
        var sinFondos = result.Causales!.Single(c => c.Causal == "SIN FONDOS SUFICIENTES");
        Assert.Equal(2, sinFondos.Cantidad);
        Assert.Equal(230000.50m, sinFondos.Monto);
        Assert.Equal(1, sinFondos.CantidadImpagos);
    }

    [Fact]
    public async Task GetChequesRechazadosAsync_WhenApiReturns200WithFixture_FirstAndLastRejectionDates()
    {
        var sut = BuildSut(FixtureJson);

        var result = await sut.GetChequesRechazadosAsync("20-12345678-9", CancellationToken.None);

        Assert.Equal(new DateTime(2025, 1, 10), result.FechaPrimerRechazo);
        Assert.Equal(new DateTime(2025, 6, 20), result.FechaUltimoRechazo);
    }

    [Fact]
    public async Task GetChequesRechazadosAsync_WhenApiReturns200WithFixture_AnyFlagsAggregated()
    {
        var sut = BuildSut(FixtureJson);

        var result = await sut.GetChequesRechazadosAsync("20-12345678-9", CancellationToken.None);

        Assert.True(result.TieneProcesoJudicial);
        Assert.True(result.EnRevision);
    }

    // ── Tests: tri-state (NoChequesReported / LookupFailed) ────────────────────

    [Fact]
    public async Task GetChequesRechazadosAsync_When404NoData_MapsToNoChequesReported()
    {
        var sut = BuildSut(HttpStatusCode.NotFound, NoDataJson);

        var result = await sut.GetChequesRechazadosAsync("20-12345678-9", CancellationToken.None);

        Assert.Equal(BcraChequesConsultaEstado.NoChequesReported, result.Estado);
    }

    [Fact]
    public async Task GetChequesRechazadosAsync_When404NoData_HasEmptyNonNullCollections()
    {
        var sut = BuildSut(HttpStatusCode.NotFound, NoDataJson);

        var result = await sut.GetChequesRechazadosAsync("20-12345678-9", CancellationToken.None);

        Assert.NotNull(result.Causales);
        Assert.NotNull(result.Detalle);
        Assert.Empty(result.Causales!);
        Assert.Empty(result.Detalle!);
    }

    [Fact]
    public async Task GetChequesRechazadosAsync_When500_MapsToLookupFailed()
    {
        var sut = BuildSut(HttpStatusCode.InternalServerError, "boom");

        var result = await sut.GetChequesRechazadosAsync("20-12345678-9", CancellationToken.None);

        Assert.Equal(BcraChequesConsultaEstado.LookupFailed, result.Estado);
        Assert.NotNull(result.Causales);
        Assert.NotNull(result.Detalle);
        Assert.Empty(result.Detalle!);
    }

    [Fact]
    public async Task GetChequesRechazadosAsync_WhenMalformedJsonOn200_MapsToLookupFailed()
    {
        var sut = BuildSut(HttpStatusCode.OK, "{ this is not valid json");

        var result = await sut.GetChequesRechazadosAsync("20-12345678-9", CancellationToken.None);

        Assert.Equal(BcraChequesConsultaEstado.LookupFailed, result.Estado);
    }

    // ── Test: legacy stored JSON round-trip (Cheques must be optional) ─────────

    [Fact]
    public void BcraData_WithLegacyJsonLackingChequesKey_DeserializesWithNullCheques()
    {
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        var data = JsonSerializer.Deserialize<BcraData>(LegacyBcraDataJson, opts);

        Assert.NotNull(data);
        Assert.NotNull(data!.Deuda);       // sanity: the rest still deserializes
        Assert.Null(data.Cheques);         // the new block is absent, not defaulted
    }
}
