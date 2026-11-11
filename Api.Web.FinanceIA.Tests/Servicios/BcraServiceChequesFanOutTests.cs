// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-05

using System.Net;
using System.Text;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Guards the cheques leg of <c>BcraService.GetBcraDataForAnalysisAsync</c>, the fan-out that freezes
/// an analysis's BCRA snapshot.
/// <para>
/// GAP THIS FILLS: the four calls have always run under one <c>Task.WhenAll</c>, but no test had ever
/// exercised the aggregate with one leg failing and the others answering. That became load-bearing
/// with scoring algorithm version 5, which reads <c>BcraData.Cheques</c>: the engine distinguishes "no
/// cheques" from "no answer" by <c>Estado</c> alone, so if a cheques failure could null the whole
/// payload, throw, or arrive as anything other than a <c>LookupFailed</c> block, the penalty's
/// contract would rest on nothing.
/// </para>
/// <para>
/// Hand-rolled stubs, no mock library. The handler routes BY PATH, in the style of
/// <see cref="BcraMacroMappingTests"/> — necessary here because the deuda path is a PREFIX of the
/// cheques path (<c>/centraldedeudores/v1.0/Deudas/{cuit}</c> vs
/// <c>/centraldedeudores/v1.0/Deudas/ChequesRechazados/{cuit}</c>), so cheques must be matched first
/// or every cheques request would be answered as a debt lookup.
/// </para>
/// </summary>
public sealed class BcraServiceChequesFanOutTests
{
    private const string Cuit = "30712345678";

    // Situación 3 for one entity in one period — enough for the deuda leg to map to DebtFound.
    private const string DeudaJson = """
        {"status":200,"results":{"identificacion":30712345678,"denominacion":"ACME SA",
         "periodos":[{"periodo":"202601","entidades":[
           {"entidad":"BANCO X","situacion":3,"monto":1500.0,"diasAtrasoPago":45,
            "situacionJuridica":false,"enRevision":false,"procesoJud":false,"refinanciaciones":false}]}]}}
        """;

    private const string ChequesJson = """
        {"status":200,"results":{"identificacion":30712345678,"denominacion":"ACME SA",
         "causales":[{"causal":"SIN FONDOS SUFICIENTES","entidades":[{"entidad":11,"detalle":[
           {"nroCheque":1,"fechaRechazo":"2026-01-10","monto":100000.00,"fechaPago":null,
            "fechaPagoMulta":null,"estadoMulta":"IMPAGA","ctaPersonal":false,"denomJuridica":"ACME SA",
            "enRevision":false,"procesoJud":false}]}]}]}}
        """;

    private const string ChequesNotFoundJson =
        """{"status":404,"errorMessages":["No se encontró datos para la identificación ingresada."]}""";

    private const string MonetariasJson =
        """{"status":200,"results":[{"idVariable":27,"detalle":[{"fecha":"2026-01-31","valor":2.5}]}]}""";

    private const string CotizacionesJson =
        """{"status":200,"results":[{"fecha":"2026-01-31","detalle":[{"codigoMoneda":"USD","tipoCotizacion":1200.0}]}]}""";

    // ── Stubs ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Answers each leg of the fan-out independently. The cheques leg is configurable: a status plus
    /// body, or an exception to throw — the two shapes a real outage takes.
    /// </summary>
    private sealed class FanOutHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _chequesStatus;
        private readonly string         _chequesBody;
        private readonly bool           _chequesThrows;

        public FanOutHandler(HttpStatusCode chequesStatus, string chequesBody, bool chequesThrows = false)
        {
            _chequesStatus = chequesStatus;
            _chequesBody   = chequesBody;
            _chequesThrows = chequesThrows;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken  cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            // Cheques BEFORE deuda: the deuda path is a prefix of this one.
            if (path.Contains("/Deudas/ChequesRechazados/", StringComparison.Ordinal))
            {
                if (_chequesThrows)
                {
                    // Not HttpRequestException and not a cancellation: only the service's catch-all
                    // backstop can absorb this, which is the arm that keeps the "never throws"
                    // contract when BCRA misbehaves in a way nobody enumerated.
                    throw new InvalidOperationException("cheques leg exploded");
                }

                return Respond(_chequesStatus, _chequesBody);
            }

            if (path.Contains("/Deudas/", StringComparison.Ordinal))
                return Respond(HttpStatusCode.OK, DeudaJson);

            if (path.Contains("/Monetarias/", StringComparison.Ordinal))
                return Respond(HttpStatusCode.OK, MonetariasJson);

            if (path.Contains("/Cotizaciones/", StringComparison.Ordinal))
                return Respond(HttpStatusCode.OK, CotizacionesJson);

            return Respond(HttpStatusCode.NotFound, "{}");
        }

        private static Task<HttpResponseMessage> Respond(HttpStatusCode status, string body) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) =>
            new(_handler) { BaseAddress = new Uri("https://api.bcra.gob.ar") };
    }

    private static BcraService BuildSut(
        HttpStatusCode chequesStatus, string chequesBody, bool chequesThrows = false) =>
        BcraServiceTestFactory.Create(new StubHttpClientFactory(new FanOutHandler(chequesStatus, chequesBody, chequesThrows)),
            new MemoryCache(Options.Create(new MemoryCacheOptions())));

    // ── Tests ─────────────────────────────────────────────────────────────────

    // The happy path, so the failure cases below are read against a known-good aggregate: all four
    // legs answer and the cheques block arrives populated with the aggregates scoring reads.
    [Fact]
    public async Task GetBcraDataForAnalysisAsync_WhenEveryLegAnswers_PopulatesCheques()
    {
        var sut = BuildSut(HttpStatusCode.OK, ChequesJson);

        var data = await sut.GetBcraDataForAnalysisAsync(Cuit, CancellationToken.None);

        Assert.NotNull(data);
        Assert.Equal(BcraDeudaConsultaEstado.DebtFound, data!.Deuda!.Estado);
        Assert.NotNull(data.Cheques);
        Assert.Equal(BcraChequesConsultaEstado.ChequesFound, data.Cheques!.Estado);
        Assert.Equal(100000.00m, data.Cheques.MontoTotalRechazado);
        Assert.Equal(100000.00m, data.Cheques.MontoTotalImpago);   // fechaPago null → impago
    }

    // A cheques outage (a 5xx) must cost the analysis the cheques signal and nothing else: the block
    // is present and marked LookupFailed — never null, never an exception — and deuda, macro and
    // cambiario are untouched. Scoring reads that Estado as "no answer" and applies no penalty.
    [Fact]
    public async Task GetBcraDataForAnalysisAsync_WhenTheChequesLegFails_KeepsTheRestAndMarksLookupFailed()
    {
        var sut = BuildSut(HttpStatusCode.InternalServerError, "boom");

        var data = await sut.GetBcraDataForAnalysisAsync(Cuit, CancellationToken.None);

        Assert.NotNull(data);
        Assert.NotNull(data!.Cheques);
        Assert.Equal(BcraChequesConsultaEstado.LookupFailed, data.Cheques!.Estado);

        // The sentinel carries zeroed amounts, byte-identical to a clean record — which is exactly why
        // the engine gates on Estado and not on these numbers.
        Assert.Equal(0m, data.Cheques.MontoTotalRechazado);
        Assert.Equal(0m, data.Cheques.MontoTotalImpago);
        Assert.Equal(0,  data.Cheques.TotalRechazados);

        // Everything else survived the failure.
        Assert.Equal(BcraDeudaConsultaEstado.DebtFound, data.Deuda!.Estado);
        Assert.Equal(3, data.Deuda.SituacionMaxima);
        Assert.NotNull(data.Macro);
        Assert.NotNull(data.Cambiario);
    }

    // The same guarantee when the cheques leg throws something nobody enumerated. Without the
    // service's catch-all backstop this would escape Task.WhenAll and reach the caller, where
    // AnalysisController records the WHOLE BCRA enrichment as failed — losing deuda, macro and
    // cambiario, and the entire 0.35 BCRA weight, because of one optional block.
    [Fact]
    public async Task GetBcraDataForAnalysisAsync_WhenTheChequesLegThrows_DoesNotFailTheAnalysis()
    {
        var sut = BuildSut(HttpStatusCode.OK, ChequesJson, chequesThrows: true);

        var data = await sut.GetBcraDataForAnalysisAsync(Cuit, CancellationToken.None);

        Assert.NotNull(data);
        Assert.Equal(BcraChequesConsultaEstado.LookupFailed, data!.Cheques!.Estado);
        Assert.Equal(BcraDeudaConsultaEstado.DebtFound, data.Deuda!.Estado);
        Assert.NotNull(data.Macro);
    }

    // BCRA answers 404 for a CUIT with no rejected cheques. That is a real answer and a good one —
    // NoChequesReported, not LookupFailed — and the distinction is what lets the engine leave a clean
    // record unpenalised without also treating an outage as clean.
    [Fact]
    public async Task GetBcraDataForAnalysisAsync_WhenNoChequesAreReported_IsNotAFailedLookup()
    {
        var sut = BuildSut(HttpStatusCode.NotFound, ChequesNotFoundJson);

        var data = await sut.GetBcraDataForAnalysisAsync(Cuit, CancellationToken.None);

        Assert.NotNull(data);
        Assert.Equal(BcraChequesConsultaEstado.NoChequesReported, data!.Cheques!.Estado);
        Assert.Equal(BcraDeudaConsultaEstado.DebtFound, data.Deuda!.Estado);
    }
}
