// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-19

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Guards the Central de Deudores traffic shaping in <see cref="BcraService"/>.
/// <para>
/// THE INCIDENT THIS PINS (2026-10-19, Kudu console of the App Service): Deudas/{cuit} answered
/// "curl: (56) Recv failure: Connection was reset" for a few minutes and then recovered on its own,
/// while /estadisticas answered 200 throughout — consistent with a per-source-IP rate limit. Our
/// traffic was the burst that trips one: three simultaneous requests per lookup (Deudas, Historicas,
/// Cheques), each retried up to three times at 0.5 s back-off, and a LookupFailed never cached so
/// every "Reintentar" click fired the burst again. These tests pin the four counter-measures: the
/// legs are SEQUENCED (and Historicas sent only when Deudas gave a real answer — debt or no debt —
/// never after a failed Deudas), a process-wide GATE caps
/// concurrency and queues rather than refuses, a lookup BUDGET bounds the whole sequence including
/// the queue, and a LookupFailed is CACHED for a short window.
/// </para>
/// Hand-rolled stubs, no mock library. The handler routes BY PATH, matching Historicas and
/// ChequesRechazados before Deudas because the Deudas path is a prefix of both.
/// </summary>
public sealed class BcraCentralDeudoresThrottlingTests
{
    private const string Cuit = "30712345678";

    private const string DeudaJson = """
        {"status":200,"results":{"identificacion":30712345678,"denominacion":"ACME SA",
         "periodos":[{"periodo":"202601","entidades":[
           {"entidad":"BANCO X","situacion":3,"monto":1500.0,"diasAtrasoPago":45,
            "situacionJuridica":false,"enRevision":false,"procesoJud":false,"refinanciaciones":false}]}]}}
        """;

    private const string NoPeriodosJson = """
        {"status":200,"results":{"identificacion":30712345678,"denominacion":"ACME SA","periodos":[]}}
        """;

    private const string NotFoundJson =
        """{"status":404,"errorMessages":["No se encontró datos para la identificación ingresada."]}""";

    private const string MonetariasJson =
        """{"status":200,"results":[{"idVariable":27,"detalle":[{"fecha":"2026-01-31","valor":2.5}]}]}""";

    // ── Stubs ─────────────────────────────────────────────────────────────────

    private enum Leg { Deudas, Historicas, Cheques, Estadisticas }

    /// <summary>One leg's answer: a status and body, or an exception to throw.</summary>
    private sealed record Answer(HttpStatusCode Status, string Body, Exception? Throws = null)
    {
        public static Answer Ok(string body)         => new(HttpStatusCode.OK, body);
        public static Answer Fails(HttpStatusCode s)  => new(s, NotFoundJson);
        public static Answer Throwing(Exception ex)  => new(HttpStatusCode.OK, string.Empty, ex);
    }

    /// <summary>
    /// Answers every leg from <c>answers</c>, holding each Central de Deudores request for
    /// <c>delay</c> so overlapping requests would be visible, and records the order the legs arrive
    /// in and the highest number of Central de Deudores requests ever in flight at once.
    /// </summary>
    private sealed class ScriptedHandler(Func<Leg, Answer> answers, TimeSpan delay = default) : HttpMessageHandler
    {
        private int _inFlight;
        private int _maxInFlight;

        public ConcurrentQueue<Leg> Order { get; } = new();
        public int MaxInFlight => Volatile.Read(ref _maxInFlight);
        public int Calls(Leg leg) => Order.Count(l => l == leg);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken  cancellationToken)
        {
            var leg = Classify(request.RequestUri!.AbsolutePath);
            if (leg == Leg.Estadisticas)
                return Build(answers(leg));

            Order.Enqueue(leg);
            var now = Interlocked.Increment(ref _inFlight);
            int seen;
            while (now > (seen = Volatile.Read(ref _maxInFlight)) &&
                   Interlocked.CompareExchange(ref _maxInFlight, now, seen) != seen)
            {
            }

            try
            {
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                return Build(answers(leg));
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        private static Leg Classify(string path)
        {
            // Historicas and ChequesRechazados BEFORE Deudas: the Deudas path is a prefix of both.
            if (path.Contains("/Deudas/Historicas/", StringComparison.Ordinal))        return Leg.Historicas;
            if (path.Contains("/Deudas/ChequesRechazados/", StringComparison.Ordinal)) return Leg.Cheques;
            if (path.Contains("/Deudas/", StringComparison.Ordinal))                   return Leg.Deudas;
            return Leg.Estadisticas;
        }

        private static HttpResponseMessage Build(Answer answer)
        {
            if (answer.Throws is not null)
                throw answer.Throws;

            return new HttpResponseMessage(answer.Status)
            {
                Content = new StringContent(answer.Body, Encoding.UTF8, "application/json")
            };
        }
    }

    /// <summary>
    /// Hands every named client the same handler, and records which client name each request path
    /// went out on — so a test can see the Central de Deudores using its own (gentler) pipeline.
    /// </summary>
    private sealed class NameRecordingFactory(HttpMessageHandler inner) : IHttpClientFactory
    {
        public ConcurrentQueue<(string Name, string Path)> Requests { get; } = new();

        public HttpClient CreateClient(string name) =>
            new(new NameTag(name, Requests) { InnerHandler = inner }, disposeHandler: false)
            {
                BaseAddress = new Uri("https://api.bcra.gob.ar")
            };

        private sealed class NameTag(string name, ConcurrentQueue<(string, string)> requests) : DelegatingHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                requests.Enqueue((name, request.RequestUri!.AbsolutePath));
                return base.SendAsync(request, cancellationToken);
            }
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

        public IReadOnlyCollection<(LogLevel Level, string Message)> Entries => _entries;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => _entries.Enqueue((logLevel, formatter(state, exception)));
    }

    private static BcraService BuildSut(
        ScriptedHandler            handler,
        BcraOptions?               options = null,
        IBcraCentralDeudoresGate?  gate    = null,
        BcraTestClock?             clock   = null,
        ILogger<BcraService>?      logger  = null)
        => BcraServiceTestFactory.Create(
            new NameRecordingFactory(handler),
            (clock ?? new BcraTestClock()).CreateCache(),
            options,
            gate,
            logger);

    private static HttpRequestException ConnectionReset() =>
        new("An error occurred while sending the request.",
            new IOException("Unable to read data from the transport connection: Connection reset by peer.",
                new SocketException((int)SocketError.ConnectionReset)));

    // ── Sequencing ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ALookup_SendsDeudasThenHistoricasThenCheques_NeverTwoAtOnce()
    {
        var handler = new ScriptedHandler(
            leg => leg switch
            {
                Leg.Deudas => Answer.Ok(DeudaJson),
                _          => Answer.Fails(HttpStatusCode.NotFound)
            },
            delay: TimeSpan.FromMilliseconds(50));
        var sut = BuildSut(handler);

        var result = await sut.GetBcraDataForAnalysisAsync(Cuit, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(BcraDeudaConsultaEstado.DebtFound, result.Deuda!.Estado);
        Assert.Equal([Leg.Deudas, Leg.Historicas, Leg.Cheques], handler.Order.ToArray());

        // They used to be fired together: three in flight at once against a rate-limited endpoint.
        Assert.Equal(1, handler.MaxInFlight);
    }

    public static TheoryData<string> DeudasFailedOutcomes => ["500", "429", "reset"];

    public static TheoryData<string> NoDebtOutcomes => ["404", "noPeriodos"];

    private static Answer DeudasAnswer(string kind) => kind switch
    {
        "404"        => Answer.Fails(HttpStatusCode.NotFound),
        "noPeriodos" => Answer.Ok(NoPeriodosJson),
        "500"        => Answer.Fails(HttpStatusCode.InternalServerError),
        "429"        => Answer.Fails(HttpStatusCode.TooManyRequests),
        "reset"      => Answer.Throwing(ConnectionReset()),
        _            => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    [Theory]
    [MemberData(nameof(DeudasFailedOutcomes))]
    public async Task Historicas_IsNotRequested_WhenDeudasFailed(string kind)
    {
        // A failed Deudas is LookupFailed whatever the history says, so asking for it would only add
        // load to an endpoint that is already refusing us. The outcomes are pinned by
        // BcraDeudaHistoricasTests.
        var handler = new ScriptedHandler(leg => leg == Leg.Deudas ? DeudasAnswer(kind) : Answer.Ok(DeudaJson));
        var sut     = BuildSut(handler);

        await sut.GetDeudaAsync(Cuit, CancellationToken.None);

        Assert.Equal(1, handler.Calls(Leg.Deudas));
        Assert.Equal(0, handler.Calls(Leg.Historicas));
    }

    [Theory]
    [MemberData(nameof(NoDebtOutcomes))]
    public async Task Historicas_IsRequestedOnce_WhenDeudasReportedNoDebt(string kind)
    {
        // No debt in the latest período says nothing about the 24 before it: the history is asked for,
        // once, through the same gate and budget.
        var handler = new ScriptedHandler(leg => leg == Leg.Deudas ? DeudasAnswer(kind) : Answer.Fails(HttpStatusCode.NotFound));
        var sut     = BuildSut(handler);

        await sut.GetDeudaAsync(Cuit, CancellationToken.None);

        Assert.Equal(1, handler.Calls(Leg.Deudas));
        Assert.Equal(1, handler.Calls(Leg.Historicas));
    }

    [Theory]
    [MemberData(nameof(NoDebtOutcomes))]
    public async Task ANoDebtLookup_SendsDeudasThenHistoricasThenCheques_NeverTwoAtOnce(string kind)
    {
        var handler = new ScriptedHandler(
            leg => leg switch
            {
                Leg.Deudas => DeudasAnswer(kind),
                _          => Answer.Fails(HttpStatusCode.NotFound)
            },
            delay: TimeSpan.FromMilliseconds(50));
        var sut = BuildSut(handler);

        var result = await sut.GetBcraDataForAnalysisAsync(Cuit, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(BcraDeudaConsultaEstado.NoDebtReported, result.Deuda!.Estado);
        Assert.Equal([Leg.Deudas, Leg.Historicas, Leg.Cheques], handler.Order.ToArray());
        Assert.Equal(1, handler.MaxInFlight);
    }

    [Fact]
    public async Task CentralDeudoresUsesItsOwnClient_AndEstadisticasKeepsBcraClient()
    {
        var handler = new ScriptedHandler(leg => leg switch
        {
            Leg.Deudas       => Answer.Ok(DeudaJson),
            Leg.Estadisticas => Answer.Ok(MonetariasJson),
            _                => Answer.Fails(HttpStatusCode.NotFound)
        });
        var factory = new NameRecordingFactory(handler);
        var sut     = BcraServiceTestFactory.Create(factory);

        await sut.GetBcraDataForAnalysisAsync(Cuit, CancellationToken.None);

        var requests = factory.Requests.ToArray();
        Assert.Equal(3, requests.Count(r => r.Path.StartsWith("/centraldedeudores/", StringComparison.Ordinal)));
        Assert.All(
            requests.Where(r => r.Path.StartsWith("/centraldedeudores/", StringComparison.Ordinal)),
            r => Assert.Equal(BcraHttpClientRegistration.CentralDeudoresClientName, r.Name));
        Assert.Contains(requests, r => !r.Path.StartsWith("/centraldedeudores/", StringComparison.Ordinal));
        Assert.All(
            requests.Where(r => !r.Path.StartsWith("/centraldedeudores/", StringComparison.Ordinal)),
            r => Assert.Equal(BcraHttpClientRegistration.EstadisticasClientName, r.Name));
    }

    // Not a speed assertion — the stub's fixed delay makes the added latency of sequencing
    // measurable: with D per request the three legs now take about 3·D instead of D, far inside the
    // lookup budget, while the estadísticas legs still overlap them rather than adding to them.
    [Fact]
    public async Task SequencingAddsTheLegLatencies_AndStaysFarInsideTheBudget()
    {
        var perRequest = TimeSpan.FromMilliseconds(200);
        var handler = new ScriptedHandler(
            leg => leg switch
            {
                Leg.Deudas       => Answer.Ok(DeudaJson),
                Leg.Estadisticas => Answer.Ok(MonetariasJson),
                _                => Answer.Fails(HttpStatusCode.NotFound)
            },
            delay: perRequest);
        var sut = BuildSut(handler);

        var stopwatch = Stopwatch.StartNew();
        await sut.GetBcraDataForAnalysisAsync(Cuit, CancellationToken.None);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed >= perRequest * 3 - TimeSpan.FromMilliseconds(30),
            $"expected the three legs to run one after another, took {stopwatch.Elapsed.TotalMilliseconds} ms");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(new BcraOptions().CentralDeudoresLookupBudgetSeconds));
    }

    // ── Process-wide concurrency gate ─────────────────────────────────────────

    [Fact]
    public async Task TheGate_CapsCentralDeudoresRequests_AcrossParallelLookups_AndQueuesRatherThanFails()
    {
        // One gate shared by eight SUTs — eight request scopes, as in production, where BcraService
        // is scoped and the gate a singleton. Distinct CUITs so no lookup is served from the cache.
        var options = new BcraOptions { CentralDeudoresMaxConcurrency = 2 };
        var gate    = new BcraCentralDeudoresGate(Options.Create(options));
        var handler = new ScriptedHandler(
            leg => leg == Leg.Estadisticas ? Answer.Ok(MonetariasJson) : Answer.Fails(HttpStatusCode.NotFound),
            delay: TimeSpan.FromMilliseconds(60));

        var lookups = Enumerable.Range(0, 8)
            .Select(i => BuildSut(handler, options, gate)
                .GetBcraDataForAnalysisAsync($"3071234567{i}", CancellationToken.None))
            .ToArray();
        var results = await Task.WhenAll(lookups);

        Assert.Equal(2, handler.MaxInFlight);
        Assert.Equal(16, handler.Calls(Leg.Deudas) + handler.Calls(Leg.Cheques));
        // Every lookup is no-debt (404), so each also asks Historicas once — through the same gate.
        Assert.Equal(8, handler.Calls(Leg.Historicas));
        Assert.All(results, r =>
        {
            Assert.NotNull(r);
            Assert.Equal(BcraDeudaConsultaEstado.NoDebtReported,       r.Deuda!.Estado);
            Assert.Equal(BcraChequesConsultaEstado.NoChequesReported,  r.Cheques!.Estado);
        });
    }

    [Fact]
    public async Task AQueuedLookup_HonoursTheCallerToken_AndSendsNothing()
    {
        var options = new BcraOptions { CentralDeudoresMaxConcurrency = 1 };
        var gate    = new BcraCentralDeudoresGate(Options.Create(options));
        var handler = new ScriptedHandler(_ => Answer.Ok(DeudaJson));
        var sut     = BuildSut(handler, options, gate);

        using var heldPermit = await gate.EnterAsync(CancellationToken.None);
        using var cts        = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        // The caller abandoning the request is cancellation, never a fabricated LookupFailed.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.GetDeudaAsync(Cuit, cts.Token));
        Assert.Empty(handler.Order);
    }

    [Fact]
    public async Task ALookupWhoseBudgetRunsOutWhileQueued_IsLookupFailed_AndSendsNothing()
    {
        // The analysis and entity-create paths call with CancellationToken.None: the budget is the
        // only thing that stops a queued lookup from waiting forever.
        var options = new BcraOptions { CentralDeudoresMaxConcurrency = 1, CentralDeudoresLookupBudgetSeconds = 1 };
        var gate    = new BcraCentralDeudoresGate(Options.Create(options));
        var handler = new ScriptedHandler(_ => Answer.Ok(DeudaJson));
        var logger  = new RecordingLogger<BcraService>();
        var sut     = BuildSut(handler, options, gate, logger: logger);

        using var heldPermit = await gate.EnterAsync(CancellationToken.None);

        var result = await sut.GetDeudaAsync(Cuit, CancellationToken.None);

        Assert.Equal(BcraDeudaConsultaEstado.LookupFailed, result.Estado);
        Assert.Empty(handler.Order);
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("budget exhausted while queued", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheGate_ReleasesItsPermit_WhenARequestFails()
    {
        // A permit leaked on a failure path would shrink the limit for the life of the process.
        var options = new BcraOptions { CentralDeudoresMaxConcurrency = 1, FailureCacheSeconds = 0 };
        var gate    = new BcraCentralDeudoresGate(Options.Create(options));
        var handler = new ScriptedHandler(_ => Answer.Throwing(ConnectionReset()));
        var sut     = BuildSut(handler, options, gate);

        await sut.GetDeudaAsync(Cuit, CancellationToken.None);
        await sut.GetDeudaAsync(Cuit, CancellationToken.None);

        using var cts    = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var permit = await gate.EnterAsync(cts.Token);
        Assert.Equal(2, handler.Calls(Leg.Deudas));
    }

    // ── Negative cache ────────────────────────────────────────────────────────

    [Fact]
    public async Task ADeudaLookupFailed_IsCachedForTheWindow_ThenRetried()
    {
        var clock   = new BcraTestClock();
        var handler = new ScriptedHandler(_ => Answer.Throwing(ConnectionReset()));
        var sut     = BuildSut(handler, new BcraOptions { FailureCacheSeconds = 60 }, clock: clock);

        var first = await sut.GetDeudaAsync(Cuit, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(59));
        var second = await sut.GetDeudaAsync(Cuit, CancellationToken.None);

        Assert.Equal(BcraDeudaConsultaEstado.LookupFailed, first.Estado);
        Assert.Equal(BcraDeudaConsultaEstado.LookupFailed, second.Estado);
        Assert.Equal(1, handler.Calls(Leg.Deudas));

        clock.Advance(TimeSpan.FromSeconds(2));
        await sut.GetDeudaAsync(Cuit, CancellationToken.None);

        Assert.Equal(2, handler.Calls(Leg.Deudas));
    }

    [Fact]
    public async Task AChequesLookupFailed_IsCachedForTheWindow_ThenRetried()
    {
        var clock   = new BcraTestClock();
        var handler = new ScriptedHandler(_ => Answer.Fails(HttpStatusCode.ServiceUnavailable));
        var sut     = BuildSut(handler, new BcraOptions { FailureCacheSeconds = 60 }, clock: clock);

        var first  = await sut.GetChequesRechazadosAsync(Cuit, CancellationToken.None);
        var second = await sut.GetChequesRechazadosAsync(Cuit, CancellationToken.None);

        Assert.Equal(BcraChequesConsultaEstado.LookupFailed, first.Estado);
        Assert.Equal(BcraChequesConsultaEstado.LookupFailed, second.Estado);
        Assert.Equal(1, handler.Calls(Leg.Cheques));

        clock.Advance(TimeSpan.FromSeconds(61));
        await sut.GetChequesRechazadosAsync(Cuit, CancellationToken.None);

        Assert.Equal(2, handler.Calls(Leg.Cheques));
    }

    [Fact]
    public async Task ARefreshDuringTheWindow_StillReportsTheHonestLookupFailed_WithoutAskingAgain()
    {
        // What refresh-bcra serialises: the macro and cambiario legs answer, so the payload is not
        // null, and the deuda and cheques blocks say LookupFailed — the outage, not a clean bill.
        var clock   = new BcraTestClock();
        var handler = new ScriptedHandler(leg => leg == Leg.Estadisticas
            ? Answer.Ok(MonetariasJson)
            : Answer.Throwing(ConnectionReset()));
        var factory = new NameRecordingFactory(handler);
        var sut     = BcraServiceTestFactory.Create(factory, clock.CreateCache());

        await sut.GetBcraDataForAnalysisAsync(Cuit, CancellationToken.None);
        var refreshed = await sut.GetBcraDataForAnalysisAsync(Cuit, CancellationToken.None);

        Assert.NotNull(refreshed);
        Assert.Equal(BcraDeudaConsultaEstado.LookupFailed,   refreshed.Deuda!.Estado);
        Assert.Equal(BcraChequesConsultaEstado.LookupFailed, refreshed.Cheques!.Estado);
        Assert.Equal(1, handler.Calls(Leg.Deudas));
        Assert.Equal(1, handler.Calls(Leg.Cheques));
    }

    [Fact]
    public async Task AZeroFailureWindow_CachesNoFailure()
    {
        var handler = new ScriptedHandler(_ => Answer.Fails(HttpStatusCode.InternalServerError));
        var sut     = BuildSut(handler, new BcraOptions { FailureCacheSeconds = 0 });

        await sut.GetDeudaAsync(Cuit, CancellationToken.None);
        await sut.GetDeudaAsync(Cuit, CancellationToken.None);

        Assert.Equal(2, handler.Calls(Leg.Deudas));
    }

    [Fact]
    public async Task TheSuccessTtls_AreUnchanged_ARealAnswerIsStillCachedPastTheFailureWindow()
    {
        var clock   = new BcraTestClock();
        var handler = new ScriptedHandler(_ => Answer.Fails(HttpStatusCode.NotFound));
        var sut     = BuildSut(handler, clock: clock);

        await sut.GetDeudaAsync(Cuit, CancellationToken.None);
        await sut.GetChequesRechazadosAsync(Cuit, CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(23));
        await sut.GetDeudaAsync(Cuit, CancellationToken.None);
        await sut.GetChequesRechazadosAsync(Cuit, CancellationToken.None);

        Assert.Equal(1, handler.Calls(Leg.Deudas));
        Assert.Equal(1, handler.Calls(Leg.Cheques));
    }

    // ── Logging a reset ───────────────────────────────────────────────────────

    [Fact]
    public async Task AConnectionReset_IsLoggedAsAPossibleRateLimit_WithThePathOnly()
    {
        var handler = new ScriptedHandler(_ => Answer.Throwing(ConnectionReset()));
        var logger  = new RecordingLogger<BcraService>();
        var sut     = BuildSut(handler, logger: logger);

        await sut.GetDeudaAsync(Cuit, CancellationToken.None);

        var reset = Assert.Single(logger.Entries, e =>
            e.Message.Contains("connection reset — possible rate limit", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, reset.Level);
        Assert.Contains($"/centraldedeudores/v1.0/Deudas/{Cuit}", reset.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", reset.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOrdinaryNetworkError_KeepsTheGenericMessage()
    {
        var handler = new ScriptedHandler(_ => Answer.Throwing(new HttpRequestException("Name or service not known")));
        var logger  = new RecordingLogger<BcraService>();
        var sut     = BuildSut(handler, logger: logger);

        await sut.GetDeudaAsync(Cuit, CancellationToken.None);

        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("possible rate limit", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e => e.Message.Contains("BCRA API network error", StringComparison.Ordinal));
    }

    [Fact]
    public void IsConnectionReset_RecognisesAResetAtAnyDepth_AndNothingElse()
    {
        Assert.True(BcraService.IsConnectionReset(ConnectionReset()));
        Assert.True(BcraService.IsConnectionReset(new SocketException((int)SocketError.ConnectionAborted)));
        Assert.False(BcraService.IsConnectionReset(new HttpRequestException("dns")));
        Assert.False(BcraService.IsConnectionReset(
            new HttpRequestException("refused", new SocketException((int)SocketError.ConnectionRefused))));
    }
}
