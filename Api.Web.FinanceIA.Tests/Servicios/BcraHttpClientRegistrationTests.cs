// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-19

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Api.Web.FinanceIA.Clases;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Guards the two BCRA resilience pipelines registered by
/// <see cref="BcraHttpClientRegistration.AddBcraHttpClients"/> — the REAL pipelines, built through
/// DI exactly as Program.cs builds them, with only the primary (network) handler replaced.
/// <para>
/// WHY: a connection reset surfaces as <see cref="HttpRequestException"/>, which the estadísticas
/// pipeline retries three times at 0.5 s exponential back-off. Against the Central de Deudores on
/// 2026-10-19 that turned one lookup into up to twelve requests in a few seconds — precisely the
/// shape that extends a per-IP rate-limit penalty. The Central de Deudores now gets ONE retry, never
/// sooner than 2 s; estadísticas must keep exactly what it had.
/// </para>
/// These tests wait out real back-off delays (a few seconds in all), on purpose: the delay IS the
/// behaviour under test.
/// </summary>
public sealed class BcraHttpClientRegistrationTests
{
    /// <summary>Answers every attempt from a script and timestamps each one.</summary>
    private sealed class CountingHandler(Func<HttpResponseMessage> answer) : HttpMessageHandler
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public ConcurrentQueue<TimeSpan> Attempts { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken  cancellationToken)
        {
            Attempts.Enqueue(_clock.Elapsed);
            return Task.FromResult(answer());
        }
    }

    private static HttpClient BuildClient(string name, CountingHandler handler)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBcraHttpClients();
        services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => handler);

        return services.BuildServiceProvider()
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient(name);
    }

    private static HttpResponseMessage Throw(Exception ex) => throw ex;

    private static HttpRequestException ConnectionReset() =>
        new("An error occurred while sending the request.",
            new IOException("Connection reset by peer.", new SocketException((int)SocketError.ConnectionReset)));

    private const string DeudaPath = "/centraldedeudores/v1.0/Deudas/30712345678";
    private const string MonetariasPath = "/estadisticas/v4.0/Monetarias/27?Desde=2026-01-01&Hasta=2026-01-31";

    // ── Central de Deudores ───────────────────────────────────────────────────

    [Fact]
    public async Task CentralDeudores_AConnectionReset_IsRetriedOnce_AfterAtLeastTwoSeconds()
    {
        var handler = new CountingHandler(() => Throw(ConnectionReset()));
        var client  = BuildClient(BcraHttpClientRegistration.CentralDeudoresClientName, handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(DeudaPath));

        var attempts = handler.Attempts.ToArray();
        Assert.Equal(2, attempts.Length);
        Assert.True(attempts[1] - attempts[0] >= TimeSpan.FromSeconds(2),
            $"retry came after {(attempts[1] - attempts[0]).TotalMilliseconds} ms");
    }

    [Fact]
    public async Task CentralDeudores_A5xx_IsRetriedOnce()
    {
        var handler = new CountingHandler(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var client  = BuildClient(BcraHttpClientRegistration.CentralDeudoresClientName, handler);

        var response = await client.GetAsync(DeudaPath);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(2, handler.Attempts.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]          // the BCRA's real "nothing reported" answer
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.TooManyRequests)]   // the endpoint asking us to stop
    public async Task CentralDeudores_A4xx_IsNeverRetried(HttpStatusCode status)
    {
        var handler = new CountingHandler(() => new HttpResponseMessage(status));
        var client  = BuildClient(BcraHttpClientRegistration.CentralDeudoresClientName, handler);

        var response = await client.GetAsync(DeudaPath);

        Assert.Equal(status, response.StatusCode);
        Assert.Single(handler.Attempts);
    }

    [Fact]
    public void CentralDeudores_TheRetryDelay_NeverFallsBelowTwoSeconds()
    {
        // Polly's own jitter on a constant delay spreads AROUND the base; the hand-rolled one only adds.
        var random = new Random(20261019);
        for (var i = 0; i < 10_000; i++)
        {
            var delay = BcraHttpClientRegistration.CentralDeudoresRetryDelay(random);
            Assert.InRange(delay, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
        }
    }

    // ── Estadísticas: unchanged ───────────────────────────────────────────────

    [Fact]
    public async Task Estadisticas_AConnectionReset_IsStillRetriedThreeTimes()
    {
        var handler = new CountingHandler(() => Throw(ConnectionReset()));
        var client  = BuildClient(BcraHttpClientRegistration.EstadisticasClientName, handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(MonetariasPath));

        Assert.Equal(4, handler.Attempts.Count);
    }

    [Fact]
    public async Task Estadisticas_A404_IsStillNotRetried()
    {
        var handler = new CountingHandler(() => new HttpResponseMessage(HttpStatusCode.NotFound));
        var client  = BuildClient(BcraHttpClientRegistration.EstadisticasClientName, handler);

        await client.GetAsync(MonetariasPath);

        Assert.Single(handler.Attempts);
    }

    [Fact]
    public void BothClients_ShareTheBcraBaseAddressAndHeaders()
    {
        var handler = new CountingHandler(() => new HttpResponseMessage(HttpStatusCode.OK));

        foreach (var name in new[] { BcraHttpClientRegistration.EstadisticasClientName, BcraHttpClientRegistration.CentralDeudoresClientName })
        {
            var client = BuildClient(name, handler);
            Assert.Equal(new Uri("https://api.bcra.gob.ar"), client.BaseAddress);
            Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
            Assert.Contains(client.DefaultRequestHeaders.UserAgent, ua => ua.Product?.Name == "FinancIA");
        }
    }
}
