// Project : Api.Web.FinanceIA.Tests
// Created : 2026-06-16

using System.Globalization;
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
/// Regression test: proves that the BcraService deserialization + mapping pipeline
/// produces correct output when FetchAsync receives a valid HTTP 200 response from
/// the Central de Deudores endpoint.
///
/// Diagnosis confirmed (case d): the model is correct; any LookupFailed result in
/// production is a transport failure (non-2xx status or network error), not a
/// deserialization bug.
/// </summary>
public sealed class BcraDeudaDeserializationTests
{
    // ── Fixture JSON ──────────────────────────────────────────────────────────
    // 3 entities in period 202604; montos 360 + 249 + 942 = 1551; all situacion = 1.
    // Includes extra fields (fechaSit1, recategorizacionOblig, irrecDisposicionTecnica)
    // that are NOT present in the model — they must be silently ignored.
    private const string FixtureJson = """
        {
          "status": 200,
          "results": {
            "identificacion": 30716327279,
            "denominacion": "EMPRESA DE PRUEBA SA",
            "periodos": [
              {
                "periodo": "202604",
                "entidades": [
                  {
                    "entidad": "BANCO NACION",
                    "situacion": 1,
                    "monto": 360.0,
                    "diasAtrasoPago": 0,
                    "refinanciaciones": false,
                    "situacionJuridica": false,
                    "enRevision": false,
                    "procesoJud": false,
                    "fechaSit1": "2026-04-01",
                    "recategorizacionOblig": false,
                    "irrecDisposicionTecnica": false
                  },
                  {
                    "entidad": "BANCO GALICIA",
                    "situacion": 1,
                    "monto": 249.0,
                    "diasAtrasoPago": 0,
                    "refinanciaciones": false,
                    "situacionJuridica": false,
                    "enRevision": false,
                    "procesoJud": false,
                    "fechaSit1": "2026-04-01",
                    "recategorizacionOblig": false,
                    "irrecDisposicionTecnica": false
                  },
                  {
                    "entidad": "BANCO MACRO",
                    "situacion": 1,
                    "monto": 942.0,
                    "diasAtrasoPago": 0,
                    "refinanciaciones": false,
                    "situacionJuridica": false,
                    "enRevision": false,
                    "procesoJud": false,
                    "fechaSit1": "2026-04-01",
                    "recategorizacionOblig": false,
                    "irrecDisposicionTecnica": false
                  }
                ]
              }
            ]
          }
        }
        """;

    // ── Fixture JSON: several períodos, two of them malformed ─────────────────
    // Ascending, as the real API returns. Períodos, oldest first:
    //   202603  → 100.5 + 200  = 300.5, situaciones 2 and 1
    //   ""      → 999, but the periodo is BLANK   → must be skipped
    //   202604  → "entidades": []                 → must be skipped
    //   202605  → 50
    //   202606  → 1000 + 551   = 1551  (last período, so it is also the montoTotal scalar)
    // Expected historialMontos, newest first: 202606→1551, 202605→50, 202603→300.5.
    private const string MultiPeriodoFixtureJson = """
        {
          "status": 200,
          "results": {
            "identificacion": 30716327279,
            "denominacion": "EMPRESA DE PRUEBA SA",
            "periodos": [
              {
                "periodo": "202603",
                "entidades": [
                  { "entidad": "BANCO NACION",  "situacion": 2, "monto": 100.5 },
                  { "entidad": "BANCO GALICIA", "situacion": 1, "monto": 200.0 }
                ]
              },
              {
                "periodo": "",
                "entidades": [
                  { "entidad": "BANCO MACRO", "situacion": 1, "monto": 999.0 }
                ]
              },
              {
                "periodo": "202604",
                "entidades": []
              },
              {
                "periodo": "202605",
                "entidades": [
                  { "entidad": "BANCO NACION", "situacion": 1, "monto": 50.0 }
                ]
              },
              {
                "periodo": "202606",
                "entidades": [
                  { "entidad": "BANCO NACION",  "situacion": 1, "monto": 1000.0 },
                  { "entidad": "BANCO GALICIA", "situacion": 3, "monto": 551.0 }
                ]
              }
            ]
          }
        }
        """;

    // One período whose reporting entities span every situación band, with a distinct monto per
    // band so a misplaced entidad surfaces as a wrong SUM rather than a wrong count:
    //   situacion 0 → 5   and situacion 1 → 10   ⇒ banda 1   = 15
    //   situacion 2 → 20  and situacion 3 → 30   ⇒ banda 2-3 = 50
    //   situacion 4 → 40  and situacion 6 → 60   ⇒ banda 4-6 = 100
    // Total 165. The situación 0 is deliberate: BCRA documents 1-6, and the bands are written to
    // partition every integer anyway, so a value below 1 must still land somewhere.
    private const string BandasFixtureJson = """
        {
          "status": 200,
          "results": {
            "identificacion": 30716327279,
            "denominacion": "EMPRESA DE PRUEBA SA",
            "periodos": [
              {
                "periodo": "202606",
                "entidades": [
                  { "entidad": "BANCO CERO",    "situacion": 0, "monto":  5.0 },
                  { "entidad": "BANCO NACION",  "situacion": 1, "monto": 10.0 },
                  { "entidad": "BANCO GALICIA", "situacion": 2, "monto": 20.0 },
                  { "entidad": "BANCO MACRO",   "situacion": 3, "monto": 30.0 },
                  { "entidad": "BANCO BBVA",    "situacion": 4, "monto": 40.0 },
                  { "entidad": "BANCO SANTANDER","situacion": 6, "monto": 60.0 }
                ]
              }
            ]
          }
        }
        """;

    /// <summary>A 200 response whose results node reports no períodos at all — NoDebtReported.</summary>
    private const string NoPeriodosFixtureJson = """
        {
          "status": 200,
          "results": {
            "identificacion": 30716327279,
            "denominacion": "EMPRESA DE PRUEBA SA",
            "periodos": []
          }
        }
        """;

    /// <summary>
    /// Builds a fixture with <paramref name="count"/> CONSECUTIVE months ending 202606, ascending like
    /// the real API, one reporting entity per month whose monto is the month's 1-based position — so
    /// the newest month's monto is <paramref name="count"/> and an ordering slip surfaces as a wrong
    /// number, not merely a wrong count.
    /// </summary>
    private static string BuildFixtureWithConsecutivePeriodos(int count)
    {
        var oldest = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-(count - 1));

        var periodos = Enumerable.Range(0, count).Select(i =>
        {
            // InvariantCulture: the period key is a Gregorian YYYYMM, not a localised date, so a
            // runner culture with another calendar must not shift the year.
            var periodo = oldest.AddMonths(i).ToString("yyyyMM", CultureInfo.InvariantCulture);
            return $$"""
                {"periodo":"{{periodo}}",
                 "entidades":[{"entidad":"BANCO NACION","situacion":1,"monto":{{i + 1}}.0}]}
                """;
        });

        // $$$ / {{{ }}}: the JSON closes on "}}", which a $$ literal would read as an interpolation
        // delimiter rather than content.
        return $$$"""
            {"status":200,
             "results":{"identificacion":30716327279,"denominacion":"EMPRESA DE PRUEBA SA",
             "periodos":[{{{string.Join(",", periodos)}}}]}}
            """;
    }

    // ── Infrastructure stubs ──────────────────────────────────────────────────

    /// <summary>
    /// Returns the given body with the given status — HTTP 200 unless a test asks otherwise — for any
    /// request. The status is parameterised so the LookupFailed path can be exercised on the same
    /// harness: FetchWithStatusAsync yields a null body on ANY non-success status.
    /// </summary>
    private sealed class FixedResponseHandler : HttpMessageHandler
    {
        private readonly string         _json;
        private readonly HttpStatusCode _status;

        public FixedResponseHandler(string json, HttpStatusCode status)
        {
            _json   = json;
            _status = status;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken  cancellationToken)
        {
            var response = new HttpResponseMessage(_status)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    /// <summary>
    /// IHttpClientFactory stub: always returns an HttpClient backed by
    /// <see cref="FixedResponseHandler"/> with the BCRA base address set.
    /// </summary>
    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public StubHttpClientFactory(string json, HttpStatusCode status)
        {
            _client = new HttpClient(new FixedResponseHandler(json, status))
            {
                BaseAddress = new Uri("https://api.bcra.gob.ar")
            };
        }

        public HttpClient CreateClient(string name) => _client;
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private static BcraService BuildSut(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        var factory = new StubHttpClientFactory(json, status);
        var cache   = new MemoryCache(Options.Create(new MemoryCacheOptions()));
        return BcraServiceTestFactory.Create(factory, cache);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetDeudaAsync_WhenApiReturns200WithValidFixture_MapsToDebtFound()
    {
        var sut = BuildSut(FixtureJson);

        var result = await sut.GetDeudaAsync("30-71632727-9", CancellationToken.None);

        Assert.Equal(BcraDeudaConsultaEstado.DebtFound, result.Estado);
    }

    [Fact]
    public async Task GetDeudaAsync_WhenApiReturns200WithValidFixture_SituacionMaximaIsOne()
    {
        var sut = BuildSut(FixtureJson);

        var result = await sut.GetDeudaAsync("30-71632727-9", CancellationToken.None);

        Assert.Equal(1, result.SituacionMaxima);
    }

    [Fact]
    public async Task GetDeudaAsync_WhenApiReturns200WithValidFixture_MontoTotalIs1551()
    {
        var sut = BuildSut(FixtureJson);

        var result = await sut.GetDeudaAsync("30-71632727-9", CancellationToken.None);

        Assert.Equal(1551m, result.MontoTotal);
    }

    [Fact]
    public async Task GetDeudaAsync_WhenApiReturns200WithValidFixture_ThreeEntidadesInformantes()
    {
        var sut = BuildSut(FixtureJson);

        var result = await sut.GetDeudaAsync("30-71632727-9", CancellationToken.None);

        Assert.Equal(3, result.EntidadesInformantes!.Count);
    }

    [Fact]
    public async Task GetDeudaAsync_WhenApiReturns200WithValidFixture_ThreeEntidadesDetalle()
    {
        var sut = BuildSut(FixtureJson);

        var result = await sut.GetDeudaAsync("30-71632727-9", CancellationToken.None);

        Assert.Equal(3, result.EntidadesDetalle!.Count);
    }

    [Fact]
    public async Task GetDeudaAsync_WhenApiReturns200WithValidFixture_PeriodoIs202604()
    {
        var sut = BuildSut(FixtureJson);

        var result = await sut.GetDeudaAsync("30-71632727-9", CancellationToken.None);

        Assert.Equal("202604", result.PeriodoConsultado);
    }

    [Fact]
    public async Task GetDeudaAsync_WhenApiReturns200WithValidFixture_NoProcesoJudicial()
    {
        var sut = BuildSut(FixtureJson);

        var result = await sut.GetDeudaAsync("30-71632727-9", CancellationToken.None);

        Assert.False(result.TieneProcesoJudicial);
    }

    [Fact]
    public async Task GetDeudaAsync_WhenApiReturns200WithValidFixture_MaxDiasAtrasoIsZero()
    {
        var sut = BuildSut(FixtureJson);

        var result = await sut.GetDeudaAsync("30-71632727-9", CancellationToken.None);

        Assert.Equal(0, result.MaxDiasAtraso);
    }

    // ── historialMontos: the per-period TOTAL debt series the chart renders ────

    [Fact]
    public async Task GetDeudaAsync_MultiPeriodo_HistorialMontosSkipsTheTwoMalformedPeriodos()
    {
        var sut = BuildSut(MultiPeriodoFixtureJson);

        var result = await sut.GetDeudaAsync("30-71632727-9", CancellationToken.None);

        // Five períodos in, three out: the blank periodo and the empty-entidades one are dropped and
        // the rest of the series survives — one bad período costs one bar, never the whole series.
        Assert.Equal(3, result.HistorialMontos!.Count);
    }

    [Fact]
    public async Task GetDeudaAsync_MultiPeriodo_HistorialMontosIsNewestFirst()
    {
        var sut = BuildSut(MultiPeriodoFixtureJson);

        var result = await sut.GetDeudaAsync("30-71632727-9", CancellationToken.None);

        Assert.Equal(
            new[] { "202606", "202605", "202603" },
            result.HistorialMontos!.Select(m => m.Periodo).ToArray());
    }

    [Fact]
    public async Task GetDeudaAsync_MultiPeriodo_HistorialMontosSumsEveryEntidadInThePeriodo()
    {
        var sut = BuildSut(MultiPeriodoFixtureJson);

        var result = await sut.GetDeudaAsync("30-71632727-9", CancellationToken.None);

        // 100.5 + 200 on the oldest surviving período — both reporting entities, not just one.
        Assert.Equal(300.5m, result.HistorialMontos!.Single(m => m.Periodo == "202603").MontoTotal);
    }

    [Fact]
    public async Task GetDeudaAsync_MultiPeriodo_NewestHistorialMontoEqualsTheMontoTotalScalar()
    {
        var sut = BuildSut(MultiPeriodoFixtureJson);

        var result = await sut.GetDeudaAsync("30-71632727-9", CancellationToken.None);

        // The unit contract: the series carries the SAME thousands-of-pesos figure as MontoTotal, so
        // the portal formats both identically. A unit drift here breaks the chart silently.
        Assert.Equal(result.MontoTotal, result.HistorialMontos![0].MontoTotal);
    }

    [Fact]
    public async Task GetDeudaAsync_MultiPeriodo_HistorialMontosNeverCarriesABlankPeriodo()
    {
        var sut = BuildSut(MultiPeriodoFixtureJson);

        var result = await sut.GetDeudaAsync("30-71632727-9", CancellationToken.None);

        Assert.DoesNotContain(result.HistorialMontos!, m => string.IsNullOrWhiteSpace(m.Periodo));
    }

    [Fact]
    public async Task GetDeudaAsync_With30Periodos_HistorialMontosIsCappedAt24()
    {
        var sut = BuildSut(BuildFixtureWithConsecutivePeriodos(30));

        var result = await sut.GetDeudaAsync("30-71632727-9", CancellationToken.None);

        Assert.Equal(24, result.HistorialMontos!.Count);
    }

    [Fact]
    public async Task GetDeudaAsync_With30Periodos_HistorialMontosKeepsThe24NewestMonths()
    {
        var sut = BuildSut(BuildFixtureWithConsecutivePeriodos(30));

        var result = await sut.GetDeudaAsync("30-71632727-9", CancellationToken.None);

        // 30 consecutive months ending 202606 start at 202401, so the cap keeps the NEWEST 24 —
        // 202407..202606, montos 30 down to 7 — not the first 24 the API happened to send.
        Assert.Equal("202606", result.HistorialMontos![0].Periodo);
        Assert.Equal(30m,      result.HistorialMontos![0].MontoTotal);
        Assert.Equal("202407", result.HistorialMontos![23].Periodo);
        Assert.Equal(7m,       result.HistorialMontos![23].MontoTotal);
    }

    [Fact]
    public async Task GetDeudaAsync_With30Periodos_HistorialSituacionesStaysCappedAt12()
    {
        var sut = BuildSut(BuildFixtureWithConsecutivePeriodos(30));

        var result = await sut.GetDeudaAsync("30-71632727-9", CancellationToken.None);

        // The scoring input is untouched by the 24-month chart series: its only consumer is
        // ScoringService's trend window (bcra_trend_window_months, default 6), so it stays at 12.
        Assert.Equal(12, result.HistorialSituaciones!.Count);
    }

    [Fact]
    public async Task GetDeudaAsync_WhenNoDebtReported_HistorialMontosIsEmptyNotNull()
    {
        var sut = BuildSut(NoPeriodosFixtureJson);

        var result = await sut.GetDeudaAsync("30-71632727-9", CancellationToken.None);

        Assert.Equal(BcraDeudaConsultaEstado.NoDebtReported, result.Estado);
        Assert.Empty(result.HistorialMontos!);
    }

    [Fact]
    public async Task GetDeudaAsync_WhenLookupFails_HistorialMontosIsEmptyNotNull()
    {
        var sut = BuildSut(FixtureJson, HttpStatusCode.InternalServerError);

        var result = await sut.GetDeudaAsync("30-71632727-9", CancellationToken.None);

        Assert.Equal(BcraDeudaConsultaEstado.LookupFailed, result.Estado);
        Assert.Empty(result.HistorialMontos!);
    }

    // ── historialMontosPorSituacion: the same montos, split for the stacked chart ──

    [Fact]
    public async Task GetDeudaAsync_Bandas_SplitsThePeriodoAcrossTheThreeSituacionBands()
    {
        var sut = BuildSut(BandasFixtureJson);

        var result = await sut.GetDeudaAsync("30-71632727-9", CancellationToken.None);

        var fila = result.HistorialMontosPorSituacion!.Single();

        Assert.Equal( 15m, fila.MontoSituacion1);   // situaciones 0 + 1
        Assert.Equal( 50m, fila.MontoSituacion23);  // situaciones 2 + 3
        Assert.Equal(100m, fila.MontoSituacion46);  // situaciones 4 + 6
    }

    [Fact]
    public async Task GetDeudaAsync_Bandas_ASituacionOutsideOneToSixNeverVanishesFromTheStack()
    {
        var sut = BuildSut(BandasFixtureJson);

        var result = await sut.GetDeudaAsync("30-71632727-9", CancellationToken.None);

        var fila = result.HistorialMontosPorSituacion!.Single();

        // The bands are `<= 1` / `2 or 3` / `>= 4`, not `== 1` / `2 or 3` / `4..6`: the entidad
        // reported under situación 0 is folded into the normal band, so the stack still adds up to
        // the total the other series draws. Written the literal way it would fall between two
        // bands and shrink the bar by 5 with nothing to show for it.
        Assert.Equal(165m, fila.MontoSituacion1 + fila.MontoSituacion23 + fila.MontoSituacion46);
        Assert.Equal(result.HistorialMontos!.Single().MontoTotal,
                     fila.MontoSituacion1 + fila.MontoSituacion23 + fila.MontoSituacion46);
    }

    [Fact]
    public async Task GetDeudaAsync_MultiPeriodo_HistorialMontosPorSituacionWalksTheSamePeriodos()
    {
        var sut = BuildSut(MultiPeriodoFixtureJson);

        var result = await sut.GetDeudaAsync("30-71632727-9", CancellationToken.None);

        // Same filter, same order, same cap as the totals series — the two charts share an x axis,
        // so a bar in one must line up with the bar in the other, malformed períodos dropped alike.
        Assert.Equal(
            result.HistorialMontos!.Select(m => m.Periodo).ToArray(),
            result.HistorialMontosPorSituacion!.Select(m => m.Periodo).ToArray());
    }

    [Fact]
    public async Task GetDeudaAsync_MultiPeriodo_EveryRowsBandsSumToThatPeriodosMontoTotal()
    {
        var sut = BuildSut(MultiPeriodoFixtureJson);

        var result = await sut.GetDeudaAsync("30-71632727-9", CancellationToken.None);

        // The invariant the model's doc comment promises, checked on every surviving período rather
        // than the newest one: 202603 is 200 normal + 100.5 en banda 2-3, 202606 is 1000 + 551.
        foreach (var total in result.HistorialMontos!)
        {
            var fila = result.HistorialMontosPorSituacion!.Single(f => f.Periodo == total.Periodo);

            Assert.Equal(total.MontoTotal,
                         fila.MontoSituacion1 + fila.MontoSituacion23 + fila.MontoSituacion46);
        }

        var masViejo = result.HistorialMontosPorSituacion!.Single(f => f.Periodo == "202603");

        Assert.Equal(200m,   masViejo.MontoSituacion1);
        Assert.Equal(100.5m, masViejo.MontoSituacion23);
        Assert.Equal(0m,     masViejo.MontoSituacion46);
    }

    [Fact]
    public async Task GetDeudaAsync_With30Periodos_HistorialMontosPorSituacionIsCappedAt24AndNewestFirst()
    {
        var sut = BuildSut(BuildFixtureWithConsecutivePeriodos(30));

        var result = await sut.GetDeudaAsync("30-71632727-9", CancellationToken.None);

        Assert.Equal(24, result.HistorialMontosPorSituacion!.Count);

        // Every month in that generator reports situación 1, so the whole bar is the normal band
        // and its height is the month's 1-based position — 30 for 202606, 7 for the 24th row back.
        Assert.Equal("202606", result.HistorialMontosPorSituacion![0].Periodo);
        Assert.Equal(30m,      result.HistorialMontosPorSituacion![0].MontoSituacion1);
        Assert.Equal("202407", result.HistorialMontosPorSituacion![23].Periodo);
        Assert.Equal(7m,       result.HistorialMontosPorSituacion![23].MontoSituacion1);
    }

    [Fact]
    public async Task GetDeudaAsync_WhenNoDebtReported_HistorialMontosPorSituacionIsEmptyNotNull()
    {
        var sut = BuildSut(NoPeriodosFixtureJson);

        var result = await sut.GetDeudaAsync("30-71632727-9", CancellationToken.None);

        Assert.Equal(BcraDeudaConsultaEstado.NoDebtReported, result.Estado);
        Assert.Empty(result.HistorialMontosPorSituacion!);
    }

    [Fact]
    public async Task GetDeudaAsync_WhenLookupFails_HistorialMontosPorSituacionIsEmptyNotNull()
    {
        var sut = BuildSut(FixtureJson, HttpStatusCode.InternalServerError);

        var result = await sut.GetDeudaAsync("30-71632727-9", CancellationToken.None);

        // Empty, never null, for the same reason as the series beside it: a failed lookup must not
        // read back as "legacy stored data" to anything reading the persisted blob later.
        Assert.Equal(BcraDeudaConsultaEstado.LookupFailed, result.Estado);
        Assert.Empty(result.HistorialMontosPorSituacion!);
    }
}
