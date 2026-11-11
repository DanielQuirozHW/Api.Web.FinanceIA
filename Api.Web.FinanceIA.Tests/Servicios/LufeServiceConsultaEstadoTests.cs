// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-23

using System.Net;
using System.Text;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Lufe;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Tests.Clases;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Covers the LufeConsultaEstado split in LufeService.GetLufeDataAsync. The old contract returned
/// a bare null both when Lufe was down and when the PyME simply has no legajo, so refresh-lufe
/// answered 502 for a legitimate not-found. Lufe answers are classified by HTTP status CLASS, not
/// body text: any 4xx except 408/429 is NoLegajoReported (Lufe's 401 for a CUIT with no legajo, or
/// one not shared with this entity, does not reliably carry the spec's "No se encontró ninguna
/// Entidad" text — verified in production), and so is a 200 whose entidades/{cuit} payload carries
/// no real entity (Cuit 0, JSON null, or an empty body). 408, 429, 5xx and network errors stay
/// LookupFailed, and a partial snapshot must stay LegajoFound so the controller's partial-snapshot
/// guard keeps working. Every sub-call's ApiCallLogs row carries the real HTTP status.
///
/// Hand-rolled HttpMessageHandler + IHttpClientFactory stubs, per the repo's no-mocking-library
/// convention. The handler answers by path; any path without a route gets the default response.
/// </summary>
public sealed class LufeServiceConsultaEstadoTests
{
    private const long Cuit = 30556565798;
    private const string TenantId = "tenant-1";

    // ── Stubs ─────────────────────────────────────────────────────────────────
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Func<string, HttpResponseMessage> _respond;

        public RoutingHandler(Func<string, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Path relative to the base address, e.g. "entidades/30556565798/autoridades".
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            return Task.FromResult(_respond(path));
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("connection refused");
    }

    /// <summary>An HttpClient timeout: a TaskCanceledException while the caller's token is NOT cancelled.</summary>
    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout");
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public StubHttpClientFactory(HttpMessageHandler handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name)
            => new(_handler, disposeHandler: false) { BaseAddress = new Uri("https://lufe.test/") };
    }

    private sealed class RecordingApiCallLogService : IApiCallLogService
    {
        public List<(string Endpoint, int? ResponseStatus, bool IsSuccess, string? ErrorMessage)> Calls { get; } = [];

        public Task LogAsync(string tenantId, string service, string endpoint, int? responseStatus,
            int? inputTokens, int? outputTokens, decimal? estimatedCostUsd, bool isSuccess, string? errorMessage,
            int durationMs, string? operation = null, string? model = null, Guid? userId = null,
            Guid? analysisId = null, long? chatMessageId = null, Guid? documentId = null, CancellationToken ct = default)
        {
            lock (Calls)
            {
                Calls.Add((endpoint, responseStatus, isSuccess, errorMessage));
            }
            return Task.CompletedTask;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Text(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/plain")
    };

    private static LufeService BuildSut(HttpMessageHandler handler, IApiCallLogService? apiCallLog = null)
        => new(new StubHttpClientFactory(handler), apiCallLog ?? new RecordingApiCallLogService(),
            NullLogger<LufeService>.Instance);

    private static LufeService BuildSut(Func<string, HttpResponseMessage> respond)
        => BuildSut(new RoutingHandler(respond));

    private static Task<LufeLookupResult> RunAsync(LufeService sut)
        => sut.GetLufeDataAsync(TenantId, Cuit, "test-key", CancellationToken.None);

    // ── NoLegajoReported ──────────────────────────────────────────────────────
    // The live shape for a PyME with no legajo: Lufe answers 200 but the entity is empty.
    [Fact]
    public async Task GetLufeData_When200WithZeroCuitEntity_ReturnsNoLegajoReported()
    {
        var sut = BuildSut(path => path == $"entidades/{Cuit}"
            ? Json(HttpStatusCode.OK, """{"cuit":0}""")
            : Json(HttpStatusCode.OK, ""));

        var result = await RunAsync(sut);

        Assert.Equal(LufeConsultaEstado.NoLegajoReported, result.Estado);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task GetLufeData_When200WithEmptyBody_ReturnsNoLegajoReported()
    {
        var sut = BuildSut(_ => Json(HttpStatusCode.OK, ""));

        var result = await RunAsync(sut);

        Assert.Equal(LufeConsultaEstado.NoLegajoReported, result.Estado);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task GetLufeData_When404_ReturnsNoLegajoReported()
    {
        var sut = BuildSut(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await RunAsync(sut);

        Assert.Equal(LufeConsultaEstado.NoLegajoReported, result.Estado);
        Assert.Null(result.Data);
    }

    // Production (CUIT 30556565798): Lufe 401s a PyME with no legajo WITHOUT the spec's documented
    // text. The body no longer matters — the documented message, a JSON envelope, anything, or none.
    [Theory]
    [InlineData("No se encontró ninguna Entidad para el CUIT enviado")]
    [InlineData("""{"status":401,"message":"Unauthorized"}""")]
    [InlineData("invalid api key")]
    [InlineData("")]
    public async Task GetLufeData_When401WithArbitraryBody_ReturnsNoLegajoReported(string body)
    {
        var sut = BuildSut(_ => Text(HttpStatusCode.Unauthorized, body));

        var result = await RunAsync(sut);

        Assert.Equal(LufeConsultaEstado.NoLegajoReported, result.Estado);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task GetLufeData_When401WithNoContent_ReturnsNoLegajoReported()
    {
        var sut = BuildSut(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var result = await RunAsync(sut);

        Assert.Equal(LufeConsultaEstado.NoLegajoReported, result.Estado);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task GetLufeData_When403_ReturnsNoLegajoReported()
    {
        var sut = BuildSut(_ => Text(HttpStatusCode.Forbidden, "forbidden"));

        var result = await RunAsync(sut);

        Assert.Equal(LufeConsultaEstado.NoLegajoReported, result.Estado);
        Assert.Null(result.Data);
    }

    // ApiCallLogs records what Lufe actually answered for the entidad call, not a hardcoded 502.
    [Fact]
    public async Task GetLufeData_When401_LogsRealStatusForEntidadCall()
    {
        var log = new RecordingApiCallLogService();
        var sut = BuildSut(new RoutingHandler(_ => Text(HttpStatusCode.Unauthorized, "whatever")), log);

        await RunAsync(sut);

        var entidadCall = Assert.Single(log.Calls, c => c.Endpoint == $"entidades/{Cuit}");
        Assert.Equal(401, entidadCall.ResponseStatus);
        Assert.False(entidadCall.IsSuccess);
        Assert.Equal("No legajo (HTTP 401)", entidadCall.ErrorMessage);
    }

    // ── The operations dashboard reads these rows ─────────────────────────────
    // The rows LufeService writes, as ApiCallNoDataClassifier reads them: a "no legajo" answer is not an
    // integration failure; a 5xx, a network error and a timeout are.
    private static IEnumerable<bool> ClassifiedAsNoData(RecordingApiCallLogService log)
        => log.Calls.Select(c => ApiCallLogRowMarkers.IsNoData(
            RescoreSource.Lufe, c.IsSuccess, c.ResponseStatus, c.ErrorMessage));

    [Fact]
    public async Task GetLufeData_When404NoLegajo_EveryRowReadsAsNoData_NotAsAFailure()
    {
        var log = new RecordingApiCallLogService();
        var sut = BuildSut(new RoutingHandler(_ => Text(HttpStatusCode.NotFound, "No legajo")), log);

        await RunAsync(sut);

        var entidadCall = Assert.Single(log.Calls, c => c.Endpoint == $"entidades/{Cuit}");
        Assert.Equal((404, false, "No legajo (HTTP 404)"), (entidadCall.ResponseStatus, entidadCall.IsSuccess, entidadCall.ErrorMessage));
        Assert.NotEmpty(log.Calls);
        Assert.All(ClassifiedAsNoData(log), Assert.True);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task GetLufeData_WhenTheVendorFails_EveryRowReadsAsAFailure(HttpStatusCode status)
    {
        var log = new RecordingApiCallLogService();
        var sut = BuildSut(new RoutingHandler(_ => new HttpResponseMessage(status)), log);

        await RunAsync(sut);

        Assert.NotEmpty(log.Calls);
        Assert.All(log.Calls, c => Assert.False(c.IsSuccess));
        Assert.All(ClassifiedAsNoData(log), Assert.False);
    }

    [Fact]
    public async Task GetLufeData_OnANetworkError_EveryRowReadsAsAFailure()
    {
        var log = new RecordingApiCallLogService();
        var sut = BuildSut(new ThrowingHandler(), log);

        await RunAsync(sut);

        Assert.NotEmpty(log.Calls);
        Assert.All(log.Calls, c => Assert.Null(c.ResponseStatus));
        Assert.All(ClassifiedAsNoData(log), Assert.False);
    }

    [Fact]
    public async Task GetLufeData_OnATimeout_EveryRowReadsAsAFailure()
    {
        var log = new RecordingApiCallLogService();
        var sut = BuildSut(new TimeoutHandler(), log);

        await RunAsync(sut);

        Assert.NotEmpty(log.Calls);
        Assert.All(log.Calls, c => Assert.Equal((null, false), (c.ResponseStatus, c.IsSuccess)));
        Assert.All(ClassifiedAsNoData(log), Assert.False);
    }

    // ── LookupFailed ──────────────────────────────────────────────────────────
    // 408 and 429 are the transient 4xx: they say nothing about the legajo.
    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task GetLufeData_WhenTransient4xx_ReturnsLookupFailed(HttpStatusCode status)
    {
        var sut = BuildSut(_ => new HttpResponseMessage(status));

        var result = await RunAsync(sut);

        Assert.Equal(LufeConsultaEstado.LookupFailed, result.Estado);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task GetLufeData_When429_LogsLookupFailedWithRealStatusForEntidadCall()
    {
        var log = new RecordingApiCallLogService();
        var sut = BuildSut(new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)), log);

        await RunAsync(sut);

        var entidadCall = Assert.Single(log.Calls, c => c.Endpoint == $"entidades/{Cuit}");
        Assert.Equal(429, entidadCall.ResponseStatus);
        Assert.Equal("Lookup failed (HTTP 429)", entidadCall.ErrorMessage);
    }

    [Fact]
    public async Task GetLufeData_When500_ReturnsLookupFailed()
    {
        var sut = BuildSut(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await RunAsync(sut);

        Assert.Equal(LufeConsultaEstado.LookupFailed, result.Estado);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task GetLufeData_When5xx_ReturnsLookupFailed()
    {
        var sut = BuildSut(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var result = await RunAsync(sut);

        Assert.Equal(LufeConsultaEstado.LookupFailed, result.Estado);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task GetLufeData_WhenNetworkError_ReturnsLookupFailedWithoutThrowing()
    {
        var sut = BuildSut(new ThrowingHandler());

        var result = await RunAsync(sut);

        Assert.Equal(LufeConsultaEstado.LookupFailed, result.Estado);
        Assert.Null(result.Data);
    }

    // A 200 that is not JSON cannot be read as "no legajo" — it is a vendor fault, not a not-found.
    [Fact]
    public async Task GetLufeData_When200WithMalformedBody_ReturnsLookupFailed()
    {
        var sut = BuildSut(_ => Json(HttpStatusCode.OK, "<html>gateway error</html>"));

        var result = await RunAsync(sut);

        Assert.Equal(LufeConsultaEstado.LookupFailed, result.Estado);
        Assert.Null(result.Data);
    }

    // ── LegajoFound ───────────────────────────────────────────────────────────
    [Fact]
    public async Task GetLufeData_WhenRealEntity_ReturnsLegajoFoundWithData()
    {
        var sut = BuildSut(path => path == $"entidades/{Cuit}"
            ? Json(HttpStatusCode.OK, $$"""{"cuit":{{Cuit}},"nombre":"PyME SA"}""")
            : new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await RunAsync(sut);

        Assert.Equal(LufeConsultaEstado.LegajoFound, result.Estado);
        Assert.NotNull(result.Data);
        Assert.Equal(Cuit, result.Data!.Entidad!.Cuit);
    }

    // Entidad failed at transport level but indicadores answered: still a snapshot, still
    // LegajoFound, so refresh-lufe's partial-snapshot guard (not a 502) decides what to persist.
    [Fact]
    public async Task GetLufeData_WhenEntidadFailsButIndicadoresAnswer_ReturnsLegajoFoundPartial()
    {
        var sut = BuildSut(path => path switch
        {
            _ when path == $"entidades/{Cuit}"             => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ when path == $"entidades/{Cuit}/indicadores" => Json(HttpStatusCode.OK, """{"periodo":202401}"""),
            _                                              => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        });

        var result = await RunAsync(sut);

        Assert.Equal(LufeConsultaEstado.LegajoFound, result.Estado);
        Assert.NotNull(result.Data);
        Assert.Null(result.Data!.Entidad);
        Assert.NotNull(result.Data.Indicadores);
    }

    // ── ApiCallLogs for the sub-calls ─────────────────────────────────────────
    // Each sub-section row carries the real HTTP status, not the old hardcoded 200/502.
    [Fact]
    public async Task GetLufeData_When401Everywhere_LogsNoDataWithRealStatusForSubCalls()
    {
        var log = new RecordingApiCallLogService();
        var sut = BuildSut(new RoutingHandler(_ => Text(HttpStatusCode.Unauthorized, "whatever")), log);

        await RunAsync(sut);

        foreach (var suffix in new[] { "autoridades", "indicadores", "documentos", "periodos", "indicadorespostbalance" })
        {
            var call = Assert.Single(log.Calls, c => c.Endpoint == $"entidades/{Cuit}/{suffix}");
            Assert.Equal(401, call.ResponseStatus);
            Assert.False(call.IsSuccess);
            Assert.Equal("No data (HTTP 401)", call.ErrorMessage);
        }
    }

    [Fact]
    public async Task GetLufeData_When503Everywhere_LogsFetchFailedWithRealStatusForSubCalls()
    {
        var log = new RecordingApiCallLogService();
        var sut = BuildSut(new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)), log);

        await RunAsync(sut);

        var call = Assert.Single(log.Calls, c => c.Endpoint == $"entidades/{Cuit}/autoridades");
        Assert.Equal(503, call.ResponseStatus);
        Assert.Equal("Fetch failed (HTTP 503)", call.ErrorMessage);
    }

    [Fact]
    public async Task GetLufeData_WhenNetworkError_LogsNullStatusForSubCalls()
    {
        var log = new RecordingApiCallLogService();
        var sut = BuildSut(new ThrowingHandler(), log);

        await RunAsync(sut);

        var call = Assert.Single(log.Calls, c => c.Endpoint == $"entidades/{Cuit}/autoridades");
        Assert.Null(call.ResponseStatus);
        Assert.Equal("Fetch failed (network error or timeout)", call.ErrorMessage);
    }

    [Fact]
    public async Task GetLufeData_WhenSubCallAnswers200Empty_LogsEmpty()
    {
        var log = new RecordingApiCallLogService();
        var sut = BuildSut(new RoutingHandler(path => path == $"entidades/{Cuit}"
            ? Json(HttpStatusCode.OK, $$"""{"cuit":{{Cuit}},"nombre":"PyME SA"}""")
            : Json(HttpStatusCode.OK, "")), log);

        await RunAsync(sut);

        var call = Assert.Single(log.Calls, c => c.Endpoint == $"entidades/{Cuit}/autoridades");
        Assert.Equal(200, call.ResponseStatus);
        Assert.False(call.IsSuccess);
        Assert.Equal("Empty (HTTP 200)", call.ErrorMessage);
    }

    [Fact]
    public async Task GetLufeData_WhenSubCallBodyIsMalformed_LogsParseErrorWithStatus()
    {
        var log = new RecordingApiCallLogService();
        var sut = BuildSut(new RoutingHandler(path => path == $"entidades/{Cuit}"
            ? Json(HttpStatusCode.OK, $$"""{"cuit":{{Cuit}},"nombre":"PyME SA"}""")
            : Json(HttpStatusCode.OK, "<html>gateway error</html>")), log);

        await RunAsync(sut);

        var call = Assert.Single(log.Calls, c => c.Endpoint == $"entidades/{Cuit}/autoridades");
        Assert.Equal(200, call.ResponseStatus);
        Assert.False(call.IsSuccess);
        Assert.Equal("Parse error", call.ErrorMessage);
    }

    [Fact]
    public async Task GetLufeData_WhenSubCallSucceeds_LogsSuccessWithNoError()
    {
        var log = new RecordingApiCallLogService();
        var sut = BuildSut(new RoutingHandler(path => path switch
        {
            _ when path == $"entidades/{Cuit}"             => Json(HttpStatusCode.OK, $$"""{"cuit":{{Cuit}}}"""),
            _ when path == $"entidades/{Cuit}/indicadores" => Json(HttpStatusCode.OK, """{"periodo":202401}"""),
            _                                              => new HttpResponseMessage(HttpStatusCode.NotFound)
        }), log);

        await RunAsync(sut);

        var call = Assert.Single(log.Calls, c => c.Endpoint == $"entidades/{Cuit}/indicadores");
        Assert.Equal(200, call.ResponseStatus);
        Assert.True(call.IsSuccess);
        Assert.Null(call.ErrorMessage);
    }

    // ── A 2xx with nothing usable is "no data"; a malformed 2xx is a failure ──
    // Production: 52 sub-section rows of "Empty (HTTP 200)" counted as integration failures. The vendor
    // answered — it simply had nothing — so the dashboard reads them as no data, by the marker LufeService
    // itself writes (LufeCallOutcome), never by vendor text. "Parse error" stays a failure.
    [Fact]
    public async Task GetLufeData_WhenASubSectionAnswers200Empty_ItsRowReadsAsNoData()
    {
        var log = new RecordingApiCallLogService();
        var sut = BuildSut(new RoutingHandler(path => path switch
        {
            _ when path == $"entidades/{Cuit}"             => Json(HttpStatusCode.OK, $$"""{"cuit":{{Cuit}}}"""),
            _ when path == $"entidades/{Cuit}/autoridades" => Json(HttpStatusCode.OK, ""),
            _                                              => Json(HttpStatusCode.OK, """{"periodo":202401}""")
        }), log);

        await RunAsync(sut);

        var call = Assert.Single(log.Calls, c => c.Endpoint == $"entidades/{Cuit}/autoridades");
        Assert.Equal((200, false), (call.ResponseStatus, call.IsSuccess));   // ApiCallLogs keeps what it records
        Assert.Equal(LufeCallOutcome.DescribeEmptyAnswer(200), call.ErrorMessage);
        Assert.Equal("Empty (HTTP 200)", call.ErrorMessage);
        Assert.True(ApiCallLogRowMarkers.IsNoData(RescoreSource.Lufe, call.IsSuccess, call.ResponseStatus, call.ErrorMessage));
    }

    [Fact]
    public async Task GetLufeData_WhenASubSectionAnswers200Malformed_ItsRowStaysAFailure()
    {
        var log = new RecordingApiCallLogService();
        var sut = BuildSut(new RoutingHandler(path => path switch
        {
            _ when path == $"entidades/{Cuit}"             => Json(HttpStatusCode.OK, $$"""{"cuit":{{Cuit}}}"""),
            _ when path == $"entidades/{Cuit}/indicadores" => Json(HttpStatusCode.OK, "{not json"),
            _                                              => new HttpResponseMessage(HttpStatusCode.NotFound)
        }), log);

        await RunAsync(sut);

        var call = Assert.Single(log.Calls, c => c.Endpoint == $"entidades/{Cuit}/indicadores");
        Assert.Equal((200, false, "Parse error"), (call.ResponseStatus, call.IsSuccess, call.ErrorMessage));
        Assert.False(ApiCallLogRowMarkers.IsNoData(RescoreSource.Lufe, call.IsSuccess, call.ResponseStatus, call.ErrorMessage));
    }

    [Fact]
    public async Task GetLufeData_WhenEntidadAnswers200WithNoEntity_ItsRowReadsAsNoData()
    {
        var log = new RecordingApiCallLogService();
        var sut = BuildSut(new RoutingHandler(path => path == $"entidades/{Cuit}"
            ? Json(HttpStatusCode.OK, """{"cuit":0}""")
            : Json(HttpStatusCode.OK, "")), log);

        var result = await RunAsync(sut);

        Assert.Equal(LufeConsultaEstado.NoLegajoReported, result.Estado);
        var call = Assert.Single(log.Calls, c => c.Endpoint == $"entidades/{Cuit}");
        Assert.Equal((200, false, "No legajo (HTTP 200)"), (call.ResponseStatus, call.IsSuccess, call.ErrorMessage));
        Assert.All(ClassifiedAsNoData(log), Assert.True);
    }

    // ── Fault isolation ───────────────────────────────────────────────────────
    // A failed post-balance call costs only its own section.
    [Fact]
    public async Task GetLufeData_WhenPostBalanceFails_KeepsTheRestOfTheSnapshot()
    {
        var sut = BuildSut(path => path switch
        {
            _ when path == $"entidades/{Cuit}"                        => Json(HttpStatusCode.OK, $$"""{"cuit":{{Cuit}}}"""),
            _ when path == $"entidades/{Cuit}/indicadores"            => Json(HttpStatusCode.OK, """{"periodo":202401}"""),
            _ when path == $"entidades/{Cuit}/indicadorespostbalance" => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            _                                                         => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        var result = await RunAsync(sut);

        Assert.Equal(LufeConsultaEstado.LegajoFound, result.Estado);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data!.Entidad);
        Assert.NotNull(result.Data.Indicadores);
        Assert.Null(result.Data.VentasComprasMensual);
    }

    // postBalance alone must not resurrect a dead snapshot: with the three core sections empty the
    // estado still follows the entidad call.
    [Fact]
    public async Task GetLufeData_WhenOnlyPostBalanceAnswers_DoesNotResurrectSnapshot()
    {
        var sut = BuildSut(path => path == $"entidades/{Cuit}/indicadorespostbalance"
            ? Json(HttpStatusCode.OK, """{"ventas":{"202401":100}}""")
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var result = await RunAsync(sut);

        Assert.Equal(LufeConsultaEstado.LookupFailed, result.Estado);
        Assert.Null(result.Data);
    }
}
