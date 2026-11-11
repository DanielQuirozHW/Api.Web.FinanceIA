// Project : Api.Web.FinanceIA.Tests
// Created : 2026-07-29

using System.Net;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Covers the CasfogService deserialization + mapping pipeline and its tri-state
/// (DataFound / NoDataReported / LookupFailed), plus defensive string parsing
/// ("-" sentinel → null, invariant-culture numbers, timezone-safe dates, situación
/// capped at 10) and the login-token / 401-refresh flow.
///
/// CASFOG makes TWO calls per lookup (POST login → token, then GET central_deudores/{cuit}),
/// so the stub handler routes by path. No live API call is made: the vendor credentials are
/// not present in the test/dev environment, and the monetary decimal/thousands format is
/// therefore UNCONFIRMED — amounts here are asserted against representative fixtures using the
/// invariant-culture rule the service applies, and must be confirmed against real data before
/// they are surfaced.
/// </summary>
public sealed class CasfogDeserializationTests
{
    // ── Fixture JSON ──────────────────────────────────────────────────────────
    // success:true; general with one party of each kind (cuit_de_terceros has a "-"
    // business_name); garantías metrics + 2 SGR rows (row 1 SITUACION 15 → must cap to 10;
    // row 2 has "-" sentinels for abonadas/mora/atraso); one certificate.
    private const string FixtureJson = """
        {
          "success": true,
          "data": {
            "general": {
              "socio_protector": [
                { "business_name": "SGR PROTECTORA SA", "cuit": "30123456789" }
              ],
              "socio_participe": [
                { "business_name": "PYME PARTICIPE SRL", "cuit": "30987654321" }
              ],
              "cuit_de_terceros": [
                { "business_name": "-", "cuit": "20111111112" }
              ]
            },
            "informacion_de_garantias_de_la_pyme": {
              "metricas": {
                "cantidad_total_garantias_otorgadas": "12",
                "monto_total_garantias_otorgadas": "1500000.50",
                "cantidad_total_garantias_vigentes": "5",
                "saldo_bruto_garantias_vigentes": "750000.25"
              },
              "data": [
                {
                  "SGR": "GARANTIZAR SGR",
                  "SALDO_BRUTO_DE_GTIAS_VIGENTE": "500000.00",
                  "SALDO_DEUDA_POR_GTIAS_ABONADAS": "12000.00",
                  "CANTIDAD_GTIAS_EN_MORA": "1",
                  "SITUACION_DE_LA_DEUDA": "15",
                  "DIAS_ATRASO": "45"
                },
                {
                  "SGR": "ACINDAR PYMES SGR",
                  "SALDO_BRUTO_DE_GTIAS_VIGENTE": "250000.25",
                  "SALDO_DEUDA_POR_GTIAS_ABONADAS": "-",
                  "CANTIDAD_GTIAS_EN_MORA": "-",
                  "SITUACION_DE_LA_DEUDA": "1",
                  "DIAS_ATRASO": "-"
                }
              ]
            },
            "certificados_pymes": [
              {
                "NUMERO": "PYME-000123",
                "FECHA_REGISTRO": "2025-03-15",
                "FECHA_VENCIMIENTO": "2026-03-15",
                "SECTOR": "INDUSTRIA",
                "CATEGORIA": "MICRO"
              }
            ]
          }
        }
        """;

    // A vendor "no data" answer: success:false + data.error.message.
    private const string ErrorJson = """
        {
          "success": false,
          "data": { "error": { "message": "No se encontraron datos para el CUIT ingresado." } }
        }
        """;

    // success:true but an empty data block (no general / garantías / certificados).
    private const string EmptyDataJson = """
        { "success": true, "data": {} }
        """;

    // success:true with one SGR row whose DIAS_ATRASO overflows Int32 (parses as decimal,
    // then the (int) cast throws OverflowException unless guarded). Used to prove a single
    // out-of-range field degrades to null instead of failing the whole lookup.
    private const string OverflowFieldJson = """
        {
          "success": true,
          "data": {
            "informacion_de_garantias_de_la_pyme": {
              "data": [
                { "SGR": "BIG SGR", "SALDO_BRUTO_DE_GTIAS_VIGENTE": "100.00", "DIAS_ATRASO": "99999999999" }
              ]
            }
          }
        }
        """;

    // Login envelope shared by the stub handlers.
    private const string DefaultLoginBody = """
        { "success": { "token": "test-token-abc", "user": { "id": 1, "name": "tester" } } }
        """;

    private const long TestCuit = 30716327279;

    // ── Infrastructure stubs ──────────────────────────────────────────────────

    /// <summary>Routes by URL: /login returns the login envelope; everything else returns the data response.</summary>
    private sealed class RoutingTransport : ICasfogTransport
    {
        private readonly HttpStatusCode _dataStatus;
        private readonly string         _dataBody;
        private readonly HttpStatusCode _loginStatus;
        private readonly string         _loginBody;

        public RoutingTransport(
            HttpStatusCode dataStatus, string dataBody,
            HttpStatusCode loginStatus, string loginBody)
        {
            _dataStatus  = dataStatus;
            _dataBody    = dataBody;
            _loginStatus = loginStatus;
            _loginBody   = loginBody;
        }

        public Task<(int? StatusCode, string? Body)> SendAsync(CasfogHttpRequest request, CancellationToken ct)
        {
            var isLogin = request.Url.Contains("login", StringComparison.OrdinalIgnoreCase);
            return Task.FromResult(isLogin
                ? ((int?)(int)_loginStatus, (string?)_loginBody)
                : ((int?)(int)_dataStatus,  (string?)_dataBody));
        }
    }

    /// <summary>Login always OK; the first data call returns 401, the second returns the fixture.</summary>
    private sealed class Retry401Transport : ICasfogTransport
    {
        private readonly string _dataBody;
        private int _dataCalls;

        public Retry401Transport(string dataBody) => _dataBody = dataBody;

        public int DataCalls => _dataCalls;

        public Task<(int? StatusCode, string? Body)> SendAsync(CasfogHttpRequest request, CancellationToken ct)
        {
            if (request.Url.Contains("login", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(((int?)200, (string?)DefaultLoginBody));

            var n = Interlocked.Increment(ref _dataCalls);
            return Task.FromResult(n == 1
                ? ((int?)401, (string?)"unauthorized")
                : ((int?)200, (string?)_dataBody));
        }
    }

    /// <summary>Every send fails with a fresh exception from the factory — simulates the transport throwing.</summary>
    private sealed class ThrowingTransport : ICasfogTransport
    {
        private readonly Func<Exception> _exceptionFactory;
        public ThrowingTransport(Func<Exception> exceptionFactory) => _exceptionFactory = exceptionFactory;

        public Task<(int? StatusCode, string? Body)> SendAsync(CasfogHttpRequest request, CancellationToken ct)
            => Task.FromException<(int?, string?)>(_exceptionFactory());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static CasfogService BuildSut(ICasfogTransport transport)
    {
        var cache  = new MemoryCache(Options.Create(new MemoryCacheOptions()));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Casfog:BaseUrl"]  = "https://sgr.casfog.com.ar/api/v1.1/",
                ["Casfog:Email"]    = "test@example.com",
                ["Casfog:Password"] = "secret"
            })
            .Build();
        return new CasfogService(transport, cache, NullLogger<CasfogService>.Instance, config);
    }

    private static CasfogService BuildSut(
        HttpStatusCode dataStatus, string dataBody,
        HttpStatusCode loginStatus = HttpStatusCode.OK, string? loginBody = null)
        => BuildSut(new RoutingTransport(dataStatus, dataBody, loginStatus, loginBody ?? DefaultLoginBody));

    private static CasfogService BuildSut(string dataBody) => BuildSut(HttpStatusCode.OK, dataBody);

    // ── Tests: DataFound (200 with fixture) ────────────────────────────────────

    [Fact]
    public async Task GetCentralDeudoresAsync_WhenApiReturns200WithFixture_MapsToDataFound()
    {
        var sut = BuildSut(FixtureJson);

        var result = await sut.GetCentralDeudoresAsync(TestCuit, CancellationToken.None);

        Assert.Equal(CasfogConsultaEstado.DataFound, result.Estado);
    }

    [Fact]
    public async Task GetCentralDeudoresAsync_WhenApiReturns200WithFixture_ParsesGarantiaMetrics()
    {
        var sut = BuildSut(FixtureJson);

        var result = await sut.GetCentralDeudoresAsync(TestCuit, CancellationToken.None);

        Assert.Equal(12, result.Garantias.CantidadGarantiasOtorgadas);
        Assert.Equal(1500000.50m, result.Garantias.MontoGarantiasOtorgadas);
        Assert.Equal(5, result.Garantias.CantidadGarantiasVigentes);
        Assert.Equal(750000.25m, result.Garantias.SaldoBrutoGarantiasVigentes);
    }

    [Fact]
    public async Task GetCentralDeudoresAsync_WhenApiReturns200WithFixture_MapsTwoSgrRows()
    {
        var sut = BuildSut(FixtureJson);

        var result = await sut.GetCentralDeudoresAsync(TestCuit, CancellationToken.None);

        Assert.Equal(2, result.Garantias.PorSgr.Count);
        var first = result.Garantias.PorSgr[0];
        Assert.Equal("GARANTIZAR SGR", first.Sgr);
        Assert.Equal(500000.00m, first.SaldoBrutoVigente);
        Assert.Equal(12000.00m, first.SaldoDeudaAbonadas);
        Assert.Equal(1, first.CantidadEnMora);
        Assert.Equal(45, first.DiasAtraso);
    }

    [Fact]
    public async Task GetCentralDeudoresAsync_WhenSituacionExceedsTen_IsCappedAtTen()
    {
        var sut = BuildSut(FixtureJson);

        var result = await sut.GetCentralDeudoresAsync(TestCuit, CancellationToken.None);

        // Fixture row 1 has SITUACION_DE_LA_DEUDA = "15".
        Assert.Equal(10, result.Garantias.PorSgr[0].SituacionDeuda);
        // Row 2 is within range and passes through unchanged.
        Assert.Equal(1, result.Garantias.PorSgr[1].SituacionDeuda);
    }

    [Fact]
    public async Task GetCentralDeudoresAsync_WhenFieldIsDashSentinel_ParsesToNull()
    {
        var sut = BuildSut(FixtureJson);

        var result = await sut.GetCentralDeudoresAsync(TestCuit, CancellationToken.None);

        var second = result.Garantias.PorSgr[1];
        Assert.Null(second.SaldoDeudaAbonadas); // "-"
        Assert.Null(second.CantidadEnMora);     // "-"
        Assert.Null(second.DiasAtraso);         // "-"
        // A "-" business_name is likewise dropped to null (fidelity of the rest preserved).
        Assert.Null(result.Socios.CuitTerceros[0].BusinessName);
        Assert.Equal("20111111112", result.Socios.CuitTerceros[0].Cuit);
    }

    [Fact]
    public async Task GetCentralDeudoresAsync_WhenApiReturns200WithFixture_ParsesCertificateWithDates()
    {
        var sut = BuildSut(FixtureJson);

        var result = await sut.GetCentralDeudoresAsync(TestCuit, CancellationToken.None);

        var cert = Assert.Single(result.Certificados);
        Assert.Equal("PYME-000123", cert.Numero);
        Assert.Equal(new DateTime(2025, 3, 15), cert.FechaRegistro);
        Assert.Equal(new DateTime(2026, 3, 15), cert.FechaVencimiento);
        Assert.Equal("INDUSTRIA", cert.Sector);
        Assert.Equal("MICRO", cert.Categoria);
    }

    [Fact]
    public async Task GetCentralDeudoresAsync_WhenApiReturns200WithFixture_MapsSocios()
    {
        var sut = BuildSut(FixtureJson);

        var result = await sut.GetCentralDeudoresAsync(TestCuit, CancellationToken.None);

        Assert.Equal("SGR PROTECTORA SA", Assert.Single(result.Socios.SocioProtector).BusinessName);
        Assert.Equal("PYME PARTICIPE SRL", Assert.Single(result.Socios.SocioParticipe).BusinessName);
        Assert.Single(result.Socios.CuitTerceros);
    }

    // ── Tests: tri-state (NoDataReported) ──────────────────────────────────────

    [Fact]
    public async Task GetCentralDeudoresAsync_WhenDataError_MapsToNoDataReported()
    {
        var sut = BuildSut(ErrorJson);

        var result = await sut.GetCentralDeudoresAsync(TestCuit, CancellationToken.None);

        Assert.Equal(CasfogConsultaEstado.NoDataReported, result.Estado);
    }

    [Fact]
    public async Task GetCentralDeudoresAsync_WhenEmptyDataBlock_MapsToNoDataReported()
    {
        var sut = BuildSut(EmptyDataJson);

        var result = await sut.GetCentralDeudoresAsync(TestCuit, CancellationToken.None);

        Assert.Equal(CasfogConsultaEstado.NoDataReported, result.Estado);
    }

    [Fact]
    public async Task GetCentralDeudoresAsync_WhenNoDataReported_HasEmptyNonNullCollections()
    {
        var sut = BuildSut(ErrorJson);

        var result = await sut.GetCentralDeudoresAsync(TestCuit, CancellationToken.None);

        Assert.NotNull(result.Garantias);
        Assert.NotNull(result.Garantias.PorSgr);
        Assert.Empty(result.Garantias.PorSgr);
        Assert.NotNull(result.Certificados);
        Assert.Empty(result.Certificados);
        Assert.NotNull(result.Socios);
        Assert.Empty(result.Socios.SocioProtector);
    }

    // ── Tests: tri-state (LookupFailed) ────────────────────────────────────────

    [Fact]
    public async Task GetCentralDeudoresAsync_When500_MapsToLookupFailed()
    {
        var sut = BuildSut(HttpStatusCode.InternalServerError, "boom");

        var result = await sut.GetCentralDeudoresAsync(TestCuit, CancellationToken.None);

        Assert.Equal(CasfogConsultaEstado.LookupFailed, result.Estado);
        Assert.NotNull(result.Garantias.PorSgr);
        Assert.Empty(result.Garantias.PorSgr);
    }

    [Fact]
    public async Task GetCentralDeudoresAsync_WhenMalformedJsonOn200_MapsToLookupFailed()
    {
        var sut = BuildSut(HttpStatusCode.OK, "{ this is not valid json");

        var result = await sut.GetCentralDeudoresAsync(TestCuit, CancellationToken.None);

        Assert.Equal(CasfogConsultaEstado.LookupFailed, result.Estado);
    }

    [Fact]
    public async Task GetCentralDeudoresAsync_WhenLoginFails_MapsToLookupFailed()
    {
        var sut = BuildSut(HttpStatusCode.OK, FixtureJson,
            loginStatus: HttpStatusCode.InternalServerError, loginBody: "login boom");

        var result = await sut.GetCentralDeudoresAsync(TestCuit, CancellationToken.None);

        Assert.Equal(CasfogConsultaEstado.LookupFailed, result.Estado);
    }

    [Fact]
    public async Task GetCentralDeudoresAsync_WhenLoginReturnsNoToken_MapsToLookupFailed()
    {
        var sut = BuildSut(HttpStatusCode.OK, FixtureJson,
            loginBody: """{ "success": { "token": "" } }""");

        var result = await sut.GetCentralDeudoresAsync(TestCuit, CancellationToken.None);

        Assert.Equal(CasfogConsultaEstado.LookupFailed, result.Estado);
    }

    // ── Tests: 401-driven token refresh ────────────────────────────────────────

    [Fact]
    public async Task GetCentralDeudoresAsync_WhenFirstDataCall401_RelogsInAndSucceeds()
    {
        var handler = new Retry401Transport(FixtureJson);
        var sut     = BuildSut(handler);

        var result = await sut.GetCentralDeudoresAsync(TestCuit, CancellationToken.None);

        Assert.Equal(CasfogConsultaEstado.DataFound, result.Estado);
        Assert.Equal(2, handler.DataCalls); // proves the single retry happened
    }

    // ── Tests: fault isolation (public method never throws) ────────────────────
    // Contract: GetCentralDeudoresAsync must never throw for any reason other than a
    // genuine caller cancellation; every HTTP-layer failure degrades to LookupFailed.

    [Fact]
    public async Task GetCentralDeudoresAsync_WhenHttpThrowsTaskCanceled_NonCancelledToken_MapsToLookupFailed()
    {
        // Simulates a client-side HttpClient.Timeout (the caller's token is NOT cancelled).
        var sut = BuildSut(new ThrowingTransport(() => new TaskCanceledException("simulated timeout")));

        var result = await sut.GetCentralDeudoresAsync(TestCuit, CancellationToken.None);

        Assert.Equal(CasfogConsultaEstado.LookupFailed, result.Estado);
    }

    [Fact]
    public async Task GetCentralDeudoresAsync_WhenHttpThrowsOperationCanceled_NonCancelledToken_MapsToLookupFailed()
    {
        // A plain OperationCanceledException (not TaskCanceledException) escaped the old
        // `catch (TaskCanceledException)` — the broadened catch must now absorb it.
        var sut = BuildSut(new ThrowingTransport(() => new OperationCanceledException("simulated")));

        var result = await sut.GetCentralDeudoresAsync(TestCuit, CancellationToken.None);

        Assert.Equal(CasfogConsultaEstado.LookupFailed, result.Estado);
    }

    [Fact]
    public async Task GetCentralDeudoresAsync_WhenHttpThrowsTimeoutException_MapsToLookupFailed()
    {
        var sut = BuildSut(new ThrowingTransport(() => new TimeoutException("simulated")));

        var result = await sut.GetCentralDeudoresAsync(TestCuit, CancellationToken.None);

        Assert.Equal(CasfogConsultaEstado.LookupFailed, result.Estado);
    }

    [Fact]
    public async Task GetCentralDeudoresAsync_WhenHttpThrowsUnexpectedException_MapsToLookupFailed()
    {
        // Proves the final defensive catch-all (e.g. a misconfigured client / Polly rejection).
        var sut = BuildSut(new ThrowingTransport(() => new InvalidOperationException("simulated")));

        var result = await sut.GetCentralDeudoresAsync(TestCuit, CancellationToken.None);

        Assert.Equal(CasfogConsultaEstado.LookupFailed, result.Estado);
    }

    [Fact]
    public async Task GetCentralDeudoresAsync_WhenIntegerFieldOverflows_DropsFieldToNull_WithoutThrowing()
    {
        // DIAS_ATRASO = "99999999999" overflows the (int) cast; the guard must map it to
        // null (per-field tolerance) rather than throwing and failing the whole lookup.
        var sut = BuildSut(OverflowFieldJson);

        var result = await sut.GetCentralDeudoresAsync(TestCuit, CancellationToken.None);

        Assert.Equal(CasfogConsultaEstado.DataFound, result.Estado);
        Assert.Null(Assert.Single(result.Garantias.PorSgr).DiasAtraso);
    }

    [Fact]
    public async Task GetCentralDeudoresAsync_WhenCallerCancels_PropagatesOperationCanceled()
    {
        // Genuine caller cancellation is NOT swallowed — it must surface as cancellation.
        // The HTTP layer throws OperationCanceledException while the caller's token is cancelled.
        var sut = BuildSut(new ThrowingTransport(() => new OperationCanceledException("aborted")));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.GetCentralDeudoresAsync(TestCuit, cts.Token));
    }

    // ── Test: model round-trips through stored JSON ────────────────────────────

    [Fact]
    public void CasfogData_DefaultInstance_HasNonNullEmptyCollections()
    {
        var data = new CasfogData();

        Assert.NotNull(data.Garantias);
        Assert.NotNull(data.Garantias.PorSgr);
        Assert.NotNull(data.Certificados);
        Assert.NotNull(data.Socios);
        Assert.NotNull(data.Socios.SocioProtector);
        Assert.NotNull(data.Socios.SocioParticipe);
        Assert.NotNull(data.Socios.CuitTerceros);
    }
}
