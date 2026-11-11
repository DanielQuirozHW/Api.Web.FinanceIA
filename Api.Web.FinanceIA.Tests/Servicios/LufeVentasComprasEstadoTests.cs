// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-02

using System.Net;
using System.Text;
using System.Text.Json;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Models.Lufe;
using Api.Web.FinanceIA.Servicios;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Score credibility 1b-1, V3: a FAILED indicadorespostbalance call is told apart from "Lufe answered
/// with no monthly data", and a refresh carries a good stored series forward instead of wiping it.
/// Three layers: <see cref="LufeService"/> marks the failure (HTTP-level, hand-rolled handler);
/// <see cref="LufeSalesSeriesCarryForward"/> decides carry vs lookup_failed (pure); and the snapshot
/// bytes of an unmarked series stay exactly what they were before the change (golden captured on the
/// pre-change build).
/// </summary>
public sealed class LufeVentasComprasEstadoTests
{
    private const long Cuit = 30556565798;

    // ── Stubs ─────────────────────────────────────────────────────────────────
    private sealed class RoutingHandler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request.RequestUri!.AbsolutePath.TrimStart('/')));
    }

    private sealed class PostBalanceThrowsHandler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            if (path.EndsWith("/indicadorespostbalance", StringComparison.Ordinal))
                throw new HttpRequestException("connection reset");
            return Task.FromResult(respond(path));
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(handler, disposeHandler: false) { BaseAddress = new Uri("https://lufe.test/") };
    }

    private sealed class NullApiCallLogService : IApiCallLogService
    {
        public Task LogAsync(string tenantId, string service, string endpoint, int? responseStatus,
            int? inputTokens, int? outputTokens, decimal? estimatedCostUsd, bool isSuccess, string? errorMessage,
            int durationMs, string? operation = null, string? model = null, Guid? userId = null,
            Guid? analysisId = null, long? chatMessageId = null, Guid? documentId = null, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    /// <summary>A legajo with an entity and indicators; the post-balance answer is the variable.</summary>
    private static Func<string, HttpResponseMessage> Routes(Func<HttpResponseMessage> postBalance) => path => path switch
    {
        _ when path == $"entidades/{Cuit}"                        => Json(HttpStatusCode.OK, $$"""{"cuit":{{Cuit}}}"""),
        _ when path == $"entidades/{Cuit}/indicadores"            => Json(HttpStatusCode.OK, """{"periodo":202401}"""),
        _ when path == $"entidades/{Cuit}/indicadorespostbalance" => postBalance(),
        _                                                         => new HttpResponseMessage(HttpStatusCode.NotFound)
    };

    private static async Task<LufeData> FetchAsync(HttpMessageHandler handler)
    {
        var sut = new LufeService(new StubHttpClientFactory(handler), new NullApiCallLogService(), NullLogger<LufeService>.Instance);
        var result = await sut.GetLufeDataAsync("tenant-1", Cuit, "key", CancellationToken.None);
        Assert.Equal(LufeConsultaEstado.LegajoFound, result.Estado);
        return result.Data!;
    }

    // ── LufeService: failed vs answered-empty ─────────────────────────────────

    public static TheoryData<HttpStatusCode, string> FailedAnswers => new()
    {
        { HttpStatusCode.InternalServerError, "" },
        { HttpStatusCode.TooManyRequests,     "" },
        { HttpStatusCode.RequestTimeout,      "" },
        { HttpStatusCode.OK,                  "{not json" },
    };

    [Theory]
    [MemberData(nameof(FailedAnswers))]
    public async Task Service_WhenPostBalanceCallFails_MarksLookupFailed(HttpStatusCode status, string body)
    {
        var data = await FetchAsync(new RoutingHandler(Routes(() => Json(status, body))));

        Assert.Null(data.VentasComprasMensual);
        Assert.Equal(LufeVentasComprasEstados.LookupFailed, data.VentasComprasMensualEstado);
        Assert.Null(data.VentasComprasMensualAsOf);
    }

    [Fact]
    public async Task Service_WhenPostBalanceTransportFails_MarksLookupFailed()
    {
        var data = await FetchAsync(new PostBalanceThrowsHandler(Routes(() => throw new InvalidOperationException())));

        Assert.Null(data.VentasComprasMensual);
        Assert.Equal(LufeVentasComprasEstados.LookupFailed, data.VentasComprasMensualEstado);
    }

    public static TheoryData<HttpStatusCode, string> EmptyAnswers => new()
    {
        { HttpStatusCode.OK,       "" },
        { HttpStatusCode.OK,       """{"ventas":{},"compras":{"total":5}}""" },
        { HttpStatusCode.OK,       """{"ventas":{"202501":null}}""" },
        { HttpStatusCode.NotFound, "" },
    };

    [Theory]
    [MemberData(nameof(EmptyAnswers))]
    public async Task Service_WhenPostBalanceAnswersWithNothing_LeavesTheSeriesNullAndUnmarked(HttpStatusCode status, string body)
    {
        var data = await FetchAsync(new RoutingHandler(Routes(() => Json(status, body))));

        Assert.Null(data.VentasComprasMensual);
        Assert.Null(data.VentasComprasMensualEstado);
    }

    [Fact]
    public async Task Service_WhenPostBalanceAnswers_ReturnsTheSeriesUnmarked()
    {
        var data = await FetchAsync(new RoutingHandler(Routes(() =>
            Json(HttpStatusCode.OK, """{"ventas":{"202502":200,"202501":100},"compras":{"202501":50}}"""))));

        Assert.Equal(new[] { "202501", "202502" }, data.VentasComprasMensual!.Select(m => m.Periodo));
        Assert.Null(data.VentasComprasMensualEstado);
        Assert.Null(data.VentasComprasMensualAsOf);
    }

    // ── LufeSalesSeriesCarryForward ───────────────────────────────────────────

    private static readonly DateTime StoredAt = new(2026, 8, 15, 12, 30, 0, DateTimeKind.Utc);

    private static List<LufeVentaCompraMes> Series(params string[] periodos)
        => periodos.Select(p => new LufeVentaCompraMes { Periodo = p, Ventas = 10m }).ToList();

    private static LufeData Failed() => new() { VentasComprasMensualEstado = LufeVentasComprasEstados.LookupFailed };

    [Fact]
    public void CarryForward_FailedWithStoredSeries_CarriesItDatedByTheStoredSnapshot()
    {
        var stored = JsonSerializer.Serialize(new LufeData { VentasComprasMensual = Series("202506", "202507") });
        var fresh  = Failed();

        Assert.True(LufeSalesSeriesCarryForward.Apply(fresh, stored, StoredAt));

        Assert.Equal(new[] { "202506", "202507" }, fresh.VentasComprasMensual!.Select(m => m.Periodo));
        Assert.Equal(LufeVentasComprasEstados.CarriedForward, fresh.VentasComprasMensualEstado);
        Assert.Equal(StoredAt, fresh.VentasComprasMensualAsOf);
        Assert.Equal(DateTimeKind.Utc, fresh.VentasComprasMensualAsOf!.Value.Kind);
    }

    [Fact]
    public void CarryForward_SecondFailure_KeepsTheOriginalDate()
    {
        var original = new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc);
        var stored = JsonSerializer.Serialize(new LufeData
        {
            VentasComprasMensual       = Series("202505"),
            VentasComprasMensualEstado = LufeVentasComprasEstados.CarriedForward,
            VentasComprasMensualAsOf   = original
        });
        var fresh = Failed();

        Assert.True(LufeSalesSeriesCarryForward.Apply(fresh, stored, StoredAt));   // StoredAt is the 1st carry's write

        Assert.Equal(LufeVentasComprasEstados.CarriedForward, fresh.VentasComprasMensualEstado);
        Assert.Equal(original, fresh.VentasComprasMensualAsOf);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{broken")]
    [InlineData("""{"VentasComprasMensual":null}""")]
    [InlineData("""{"VentasComprasMensual":[]}""")]
    [InlineData("""{"VentasComprasMensual":null,"VentasComprasMensualEstado":"lookup_failed"}""")]
    public void CarryForward_FailedWithNothingUsableStored_StaysLookupFailed(string? stored)
    {
        var fresh = Failed();

        Assert.False(LufeSalesSeriesCarryForward.Apply(fresh, stored, StoredAt));

        Assert.Null(fresh.VentasComprasMensual);
        Assert.Equal(LufeVentasComprasEstados.LookupFailed, fresh.VentasComprasMensualEstado);
        Assert.Null(fresh.VentasComprasMensualAsOf);
    }

    [Fact]
    public void CarryForward_UnmarkedFreshSnapshot_IsNeverTouched()
    {
        // Covers both a fresh series and a genuine empty answer: neither is marked, so neither changes.
        var stored = JsonSerializer.Serialize(new LufeData { VentasComprasMensual = Series("202506") });
        var empty  = new LufeData();
        var fresh  = new LufeData { VentasComprasMensual = Series("202508") };

        Assert.False(LufeSalesSeriesCarryForward.Apply(empty, stored, StoredAt));
        Assert.False(LufeSalesSeriesCarryForward.Apply(fresh, stored, StoredAt));

        Assert.Null(empty.VentasComprasMensual);
        Assert.Null(empty.VentasComprasMensualEstado);
        Assert.Equal("202508", Assert.Single(fresh.VentasComprasMensual!).Periodo);
        Assert.Null(fresh.VentasComprasMensualEstado);
    }

    // ── Golden bytes: an unmarked snapshot serializes as it did before ───────

    // Captured by serializing these two objects with JsonSerializer's default options (what
    // EntitiesController writes LufeDataJson with) on b88441b, BEFORE the two new properties existed.
    private const string GoldenWithSeries =
        """{"Entidad":null,"Autoridades":null,"Indicadores":null,"Documentos":null,"IndicadoresHistoricos":null,"VentasComprasMensual":[{"periodo":"202501","ventas":1000.5,"compras":400,"ventasMercadoLocal":null,"ventasExportacion":null},{"periodo":"202502","ventas":1200,"compras":null,"ventasMercadoLocal":1100,"ventasExportacion":100}]}""";

    private const string GoldenEmpty =
        """{"Entidad":null,"Autoridades":null,"Indicadores":null,"Documentos":null,"IndicadoresHistoricos":null,"VentasComprasMensual":null}""";

    [Fact]
    public void HappyPath_SerializesByteIdenticalToBeforeTheChange()
    {
        var data = new LufeData
        {
            VentasComprasMensual =
            [
                new LufeVentaCompraMes { Periodo = "202501", Ventas = 1000.5m, Compras = 400m },
                new LufeVentaCompraMes { Periodo = "202502", Ventas = 1200m, Compras = null, VentasMercadoLocal = 1100m, VentasExportacion = 100m }
            ]
        };

        Assert.Equal(GoldenWithSeries, JsonSerializer.Serialize(data));
        Assert.Equal(GoldenEmpty, JsonSerializer.Serialize(new LufeData()));
    }

    [Fact]
    public void MarkedSnapshot_SerializesTheTwoNewFields_AndReadsBackCaseInsensitively()
    {
        var data = new LufeData
        {
            VentasComprasMensual       = Series("202501"),
            VentasComprasMensualEstado = LufeVentasComprasEstados.CarriedForward,
            VentasComprasMensualAsOf   = StoredAt
        };

        var json = JsonSerializer.Serialize(data);
        Assert.Contains("\"VentasComprasMensualEstado\":\"carried_forward\"", json, StringComparison.Ordinal);
        Assert.Contains("\"VentasComprasMensualAsOf\":\"2026-08-15T12:30:00Z\"", json, StringComparison.Ordinal);

        var back = JsonSerializer.Deserialize<LufeData>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(LufeVentasComprasEstados.CarriedForward, back.VentasComprasMensualEstado);
        Assert.Equal(StoredAt, back.VentasComprasMensualAsOf!.Value.ToUniversalTime());
    }
}
