// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-13

using System.Globalization;
using System.Net;
using System.Text;
using Api.Web.FinanceIA.Servicios;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Mapping guard for <see cref="BcraService.GetMacroAsync"/>: proves that each macro property is
/// populated from the BCRA "Monetarias" variable id whose description its label claims —
///   InflacionMensual ← id 27  ("Inflación mensual")
///   TasaReferencia   ← id 135 ("Tasa de interés TAMAR de bancos públicos y privados")
/// so a future swap of the id constants/assignments fails loudly.
///
/// The routing handler also serves a distinctive POISON value for id 29
/// ("Mediana … expectativas de mercado" — expected annual inflation, the value the pre-fix code
/// wrongly read into InflacionMensual). The test asserts that value reaches NO field, so the guard
/// keeps proving something even if id 29 ever creeps back in. This test FAILS against the pre-fix
/// mapping (which requested id 29 for InflacionMensual → the poison value surfaced).
/// </summary>
public sealed class BcraMacroMappingTests
{
    // Distinct, id-keyed valores. Each is exactly what its id's series should carry.
    private const double InflacionValue = 1.90;  // id 27  → InflacionMensual
    private const double TamarValue     = 21.56; // id 135 → TasaReferencia
    private const double PoisonValue    = 99.99; // id 29  → must surface in NO field

    // ── Per-id routing handler ─────────────────────────────────────────────────
    // Returns a Monetarias v4.0 envelope whose single detalle valor is keyed to the requested id.
    private sealed class MonetariasRoutingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken  cancellationToken)
        {
            var path      = request.RequestUri!.AbsolutePath;          // /estadisticas/v4.0/Monetarias/27
            var idSegment = path[(path.LastIndexOf('/') + 1)..];

            double? valor = idSegment switch
            {
                "27"  => InflacionValue,
                "135" => TamarValue,
                "29"  => PoisonValue,   // the fixed code must never request this
                _     => null
            };

            if (valor is null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            var valorStr = valor.Value.ToString(CultureInfo.InvariantCulture);
            var json =
                "{\"status\":200,\"results\":[{\"idVariable\":" + idSegment +
                ",\"detalle\":[{\"fecha\":\"2026-07-31\",\"valor\":" + valorStr + "}]}]}";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new MonetariasRoutingHandler()) { BaseAddress = new Uri("https://api.bcra.gob.ar") };
    }

    private static BcraService BuildSut() =>
        BcraServiceTestFactory.Create(new StubHttpClientFactory(),
            new MemoryCache(Options.Create(new MemoryCacheOptions())));

    // ── Tests ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetMacroAsync_InflacionMensual_IsPopulatedFromVariable27()
    {
        var data = await BuildSut().GetMacroAsync(CancellationToken.None);

        Assert.NotNull(data);
        Assert.Equal(InflacionValue, data!.InflacionMensual!.Value, precision: 2);
    }

    [Fact]
    public async Task GetMacroAsync_TasaReferencia_IsPopulatedFromTamarVariable135()
    {
        var data = await BuildSut().GetMacroAsync(CancellationToken.None);

        Assert.NotNull(data);
        Assert.Equal(TamarValue, data!.TasaReferencia!.Value, precision: 2);
    }

    [Fact]
    public async Task GetMacroAsync_ExpectedInflationVariable29_SurfacesInNoField()
    {
        var data = await BuildSut().GetMacroAsync(CancellationToken.None);

        Assert.NotNull(data);
        Assert.False(Nearly(data!.InflacionMensual, PoisonValue),
            "BCRA variable 29 (expected annual inflation) must not populate InflacionMensual.");
        Assert.False(Nearly(data.TasaReferencia, PoisonValue),
            "BCRA variable 29 (expected annual inflation) must not populate TasaReferencia.");
    }

    private static bool Nearly(double? actual, double expected)
        => actual.HasValue && Math.Abs(actual.Value - expected) < 0.005;
}
