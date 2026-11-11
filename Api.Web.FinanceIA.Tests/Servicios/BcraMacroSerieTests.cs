// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-05

using System.Globalization;
using System.Net;
using System.Text;
using System.Web;
using Api.Web.FinanceIA.Servicios;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// §7.3b — <see cref="BcraService.GetMacroAsync"/> now also brings back variable 27 as a MONTHLY
/// SERIES, which the scoring engine deflates the LUFE sales figures by.
///
/// <para>
/// The load-bearing property of this suite is ISOLATION. The series comes from its own request, over
/// a much wider window than the scalar's, and the two must not be able to hurt each other:
/// <c>FetchWithStatusAsync</c> returns a null body on any non-success status, so had one request
/// served both, a range the BCRA refused would take <c>InflacionMensual</c> down with it — and
/// <c>ScoringService</c> reads a missing inflation figure as the NEUTRAL 50 rather than the 30 base,
/// silently shifting the composite of every analysis of every tenant, LUFE or not. Several tests
/// below exist only to prove that each leg can fail alone.
/// </para>
/// <para>
/// The handler routes on the <c>Desde</c> query parameter because both variable-27 requests hit the
/// same path — which is also the only way to tell them apart in production.
/// </para>
/// </summary>
public sealed class BcraMacroSerieTests
{
    private const double ScalarValue = 1.90;   // the narrow var-27 window's last point
    private const double TamarValue  = 21.56;  // id 135

    // ── Routing handler ────────────────────────────────────────────────────────
    //
    // Both var-27 requests share a path, so the discriminator is the window: the series leg asks for
    // ~26 months back, the scalar leg for 60 days. Anything older than a year is the series request.

    private sealed class MacroHandler : HttpMessageHandler
    {
        private readonly string?        _serieBody;
        private readonly HttpStatusCode _serieStatus;
        private readonly HttpStatusCode _scalarStatus;

        public MacroHandler(
            string?        serieBody,
            HttpStatusCode serieStatus  = HttpStatusCode.OK,
            HttpStatusCode scalarStatus = HttpStatusCode.OK)
        {
            _serieBody    = serieBody;
            _serieStatus  = serieStatus;
            _scalarStatus = scalarStatus;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken  cancellationToken)
        {
            var uri       = request.RequestUri!;
            var idSegment = uri.AbsolutePath[(uri.AbsolutePath.LastIndexOf('/') + 1)..];
            var desde     = HttpUtility.ParseQueryString(uri.Query)["Desde"] ?? "";

            if (idSegment == "135")
                return Respond(_scalarStatus, Envelope(135, ("2026-07-31", TamarValue)));

            if (idSegment != "27")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            // The series leg reaches much further back than the scalar's rolling 60 days.
            bool isSerieRequest = string.CompareOrdinal(desde, "2025-09-01") < 0;

            if (isSerieRequest)
                return Respond(_serieStatus, _serieBody ?? "{}");

            return Respond(_scalarStatus, Envelope(27, ("2026-07-31", ScalarValue)));
        }

        private static Task<HttpResponseMessage> Respond(HttpStatusCode status, string body) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }

    /// <summary>A Monetarias v4.0 envelope with the given ascending detalle points.</summary>
    private static string Envelope(int idVariable, params (string Fecha, double Valor)[] puntos)
    {
        var detalle = string.Join(",", puntos.Select(p =>
            $"{{\"fecha\":\"{p.Fecha}\",\"valor\":{p.Valor.ToString(CultureInfo.InvariantCulture)}}}"));

        return $"{{\"status\":200,\"results\":[{{\"idVariable\":{idVariable},\"detalle\":[{detalle}]}}]}}";
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler) { BaseAddress = new Uri("https://api.bcra.gob.ar") };
    }

    private static BcraService BuildSut(MacroHandler handler) =>
        BcraServiceTestFactory.Create(new StubHttpClientFactory(handler),
            new MemoryCache(Options.Create(new MemoryCacheOptions())));

    // ── 1. The series is built, keyed by reference month ────────────────────────

    [Fact]
    public async Task GetMacroAsync_PopulatesTheMonthlySeriesFromEveryDetallePoint()
    {
        var sut = BuildSut(new MacroHandler(Envelope(27,
            ("2025-11-30", 2.5), ("2025-12-31", 2.7), ("2026-01-31", 2.2))));

        var data = await sut.GetMacroAsync(CancellationToken.None);

        Assert.NotNull(data);
        Assert.NotNull(data!.InflacionMensualSerie);
        Assert.Equal(3, data.InflacionMensualSerie!.Count);
        Assert.Equal(2.5, data.InflacionMensualSerie["202511"]);
        Assert.Equal(2.7, data.InflacionMensualSerie["202512"]);
        Assert.Equal(2.2, data.InflacionMensualSerie["202601"]);
    }

    // The scalar keeps coming from its own, narrower window — the whole reason there are two
    // requests. A value only the series request serves must never reach it.
    [Fact]
    public async Task GetMacroAsync_TheScalarStillComesFromTheNarrowWindow()
    {
        var sut = BuildSut(new MacroHandler(Envelope(27,
            ("2025-11-30", 99.99), ("2026-01-31", 88.88))));

        var data = await sut.GetMacroAsync(CancellationToken.None);

        Assert.NotNull(data);
        Assert.Equal(ScalarValue, data!.InflacionMensual!.Value, precision: 2);
        Assert.Equal(TamarValue,  data.TasaReferencia!.Value,    precision: 2);
    }

    // ── 2. Each leg fails alone ─────────────────────────────────────────────────

    // The series request failing costs the series and NOTHING else. This is the property the
    // three-request shape exists for: the sales-drop penalty degrades to a nominal reading it
    // reports, while the macro component keeps the 30 base its scalar earns.
    [Fact]
    public async Task GetMacroAsync_WhenTheSeriesRequestFails_TheScalarsSurvive()
    {
        var sut = BuildSut(new MacroHandler(serieBody: null, serieStatus: HttpStatusCode.BadRequest));

        var data = await sut.GetMacroAsync(CancellationToken.None);

        Assert.NotNull(data);
        Assert.Null(data!.InflacionMensualSerie);
        Assert.Equal(ScalarValue, data.InflacionMensual!.Value, precision: 2);
        Assert.Equal(TamarValue,  data.TasaReferencia!.Value,   precision: 2);
    }

    // And the reverse: a series that arrives cannot resurrect a macro payload whose two scalars both
    // failed. GetMacroAsync's null guard deliberately does not consult it — the series is an input to
    // one optional signal, not evidence that macro data exists.
    [Fact]
    public async Task GetMacroAsync_WhenBothScalarsFail_TheSeriesDoesNotRescueThePayload()
    {
        var sut = BuildSut(new MacroHandler(
            Envelope(27, ("2026-01-31", 2.2)),
            scalarStatus: HttpStatusCode.InternalServerError));

        Assert.Null(await sut.GetMacroAsync(CancellationToken.None));
    }

    // ── 3. What the wire can carry ──────────────────────────────────────────────

    // A revised month arriving twice must take the later reading, not throw. Add() would raise
    // ArgumentException here, and nothing between GetMacroAsync and GetBcraDataForAnalysisAsync
    // catches it — the analysis would lose its entire BCRA block, and its 0.35 weight, over a
    // duplicate row.
    [Fact]
    public async Task GetMacroAsync_WithTwoPointsInOneMonth_TakesTheLastAndDoesNotThrow()
    {
        var sut = BuildSut(new MacroHandler(Envelope(27,
            ("2026-01-15", 1.1), ("2026-01-31", 2.2))));

        var data = await sut.GetMacroAsync(CancellationToken.None);

        Assert.NotNull(data!.InflacionMensualSerie);
        Assert.Equal(2.2, data.InflacionMensualSerie!["202601"]);
        Assert.Single(data.InflacionMensualSerie);
    }

    // Malformed dates are skipped, never guessed at: a fabricated key would sit in the series as a
    // month that can never match, and a month that cannot match is a hole the deflator would trip on
    // far from here.
    [Theory]
    [InlineData("20260131")]      // no separators
    [InlineData("2026-13-31")]    // month out of range
    [InlineData("2026-00-31")]
    [InlineData("2026/01/31")]    // wrong separator
    [InlineData("2026-1-31")]     // unpadded month
    [InlineData("")]
    public async Task GetMacroAsync_SkipsPointsWhoseFechaIsNotAWellFormedDate(string fecha)
    {
        var sut = BuildSut(new MacroHandler(Envelope(27,
            (fecha, 9.9), ("2026-01-31", 2.2))));

        var data = await sut.GetMacroAsync(CancellationToken.None);

        Assert.NotNull(data!.InflacionMensualSerie);
        Assert.Equal(2.2, data.InflacionMensualSerie!["202601"]);
        Assert.Single(data.InflacionMensualSerie);   // the malformed point reached no key at all
    }

    // Nothing usable is null, not an empty dictionary — "no series" is one shape, so the engine has
    // one thing to test rather than two.
    [Fact]
    public async Task GetMacroAsync_WhenNoPointIsUsable_TheSeriesIsNullNotEmpty()
    {
        var sut = BuildSut(new MacroHandler(Envelope(27, ("no-es-fecha", 1.0))));

        var data = await sut.GetMacroAsync(CancellationToken.None);

        Assert.NotNull(data);
        Assert.Null(data!.InflacionMensualSerie);
    }

    [Fact]
    public async Task GetMacroAsync_WithAnEmptyDetalle_TheSeriesIsNull()
    {
        var sut = BuildSut(new MacroHandler(
            """{"status":200,"results":[{"idVariable":27,"detalle":[]}]}"""));

        var data = await sut.GetMacroAsync(CancellationToken.None);

        Assert.NotNull(data);
        Assert.Null(data!.InflacionMensualSerie);
    }

    [Fact]
    public async Task GetMacroAsync_WithAMalformedSeriesBody_TheSeriesIsNullAndMacroSurvives()
    {
        var sut = BuildSut(new MacroHandler("{ this is not json"));

        var data = await sut.GetMacroAsync(CancellationToken.None);

        Assert.NotNull(data);
        Assert.Null(data!.InflacionMensualSerie);
        Assert.Equal(ScalarValue, data.InflacionMensual!.Value, precision: 2);
    }

    // ── 4. The window is month-aligned ──────────────────────────────────────────

    // Anchoring the series window on today's day-of-month would move the set of months it covers as
    // the month advances, so the same entity could deflate on the 28th and not on the 1st with no
    // code change and no new data. Asserted through the request the service actually makes.
    [Fact]
    public async Task GetMacroAsync_RequestsTheSeriesFromTheFirstOfAMonthWellBeforeTheScalarWindow()
    {
        var windows = new List<string>();

        var sut = BcraServiceTestFactory.Create(
            new StubHttpClientFactory(new CapturingHandler(windows)),
            new MemoryCache(Options.Create(new MemoryCacheOptions())));

        await sut.GetMacroAsync(CancellationToken.None);

        var serieDesde = Assert.Single(windows, w => string.CompareOrdinal(w, "2025-09-01") < 0);
        Assert.EndsWith("-01", serieDesde);

        // And it reaches back far enough for a twelve-month span plus a year of LUFE filing lag.
        Assert.True(
            DateTime.UtcNow - DateTime.ParseExact(serieDesde, "yyyy-MM-dd", CultureInfo.InvariantCulture)
                > TimeSpan.FromDays(24 * 30),
            $"the CPI window must reach at least two years back; it started at {serieDesde}");
    }

    private sealed class CapturingHandler(List<string> desdeValues) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.EndsWith("/27", StringComparison.Ordinal))
            {
                lock (desdeValues)
                    desdeValues.Add(HttpUtility.ParseQueryString(uri.Query)["Desde"] ?? "");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    Envelope(27, ("2026-01-31", 2.2)), Encoding.UTF8, "application/json")
            });
        }
    }
}
