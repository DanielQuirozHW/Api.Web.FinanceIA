// Project : Api.Web.FinanceIA
// Created : 2026-10-19

using System.Net;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.Timeout;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Registers the two named BCRA <see cref="HttpClient"/>s and their resilience pipelines.
/// <para>
/// TWO clients, not one, because the BCRA hosts two very different APIs behind one base address.
/// The estadísticas API (Monetarias, Cotizaciones) is cheap and was healthy throughout the
/// 2026-10-19 incident. The Central de Deudores (Deudas, Deudas/Historicas,
/// Deudas/ChequesRechazados) answered with TCP connection resets for minutes at a time — consistent
/// with a per-source-IP rate limit that our 3-retry, 0.5 s-back-off pipeline would trip and then
/// keep extending. A resilience pipeline belongs to a named client, so a separate client is the one
/// way to give the Central de Deudores a gentler policy while leaving estadísticas byte-identical.
/// </para>
/// Extracted from Program.cs so tests can build the real pipelines rather than a copy of them.
/// </summary>
public static class BcraHttpClientRegistration
{
    /// <summary>Named client for the estadísticas APIs (Monetarias, Cotizaciones).</summary>
    public const string EstadisticasClientName = "BcraClient";

    /// <summary>Named client for the Central de Deudores API (Deudas, Historicas, ChequesRechazados).</summary>
    public const string CentralDeudoresClientName = "BcraCentralDeudoresClient";

    // The one retry waits AT LEAST this long, plus up to CentralDeudoresRetryJitterMs of jitter.
    internal static readonly TimeSpan CentralDeudoresRetryBaseDelay = TimeSpan.FromSeconds(2);
    internal const int CentralDeudoresRetryJitterMs = 1000;

    /// <summary>
    /// Adds <see cref="EstadisticasClientName"/> and <see cref="CentralDeudoresClientName"/>.
    /// </summary>
    public static IServiceCollection AddBcraHttpClients(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // BcraClient — timeout is managed by the resilience pipeline below.
        // HttpClient.Timeout is set to InfiniteTimeSpan so it doesn't race
        // with the per-attempt and total timeouts defined in the pipeline.
        services.AddHttpClient(EstadisticasClientName, ConfigureClient)
        .AddResilienceHandler("bcra-resilience", pipeline =>
        {
            // Layer 1 (outermost): total wall-clock cap for ALL attempts combined.
            // 3 retries × 8 s/attempt + backoff (≈0.5+1+2 s) ≈ 35.5 s worst case → 40 s total.
            pipeline.AddTimeout(TimeSpan.FromSeconds(40));

            // Layer 2: retry — exponential backoff with jitter, transient errors only.
            // Retries on: network errors, per-attempt timeout, 5xx, 408.
            // Does NOT retry on 4xx (e.g. 404 "no debt" is a valid BCRA response, not an error).
            pipeline.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                Delay            = TimeSpan.FromMilliseconds(500),
                BackoffType      = DelayBackoffType.Exponential,
                UseJitter        = true,
                ShouldHandle     = args => args.Outcome switch
                {
                    { Exception: HttpRequestException }      => PredicateResult.True(),
                    { Exception: TimeoutRejectedException }  => PredicateResult.True(),
                    { Result.StatusCode: >= HttpStatusCode.InternalServerError } => PredicateResult.True(),
                    { Result.StatusCode: HttpStatusCode.RequestTimeout }         => PredicateResult.True(),
                    _ => PredicateResult.False()
                }
            });

            // Layer 3 (innermost): per-attempt timeout.
            pipeline.AddTimeout(TimeSpan.FromSeconds(8));
        });

        // BcraCentralDeudoresClient — same host and headers, gentler pipeline. Concurrency is NOT
        // limited here: that is BcraCentralDeudoresGate's job, in BcraService, where the time spent
        // queued can be charged to the lookup's own budget.
        services.AddHttpClient(CentralDeudoresClientName, ConfigureClient)
        .AddResilienceHandler("bcra-central-deudores-resilience", pipeline =>
        {
            // Layer 1 (outermost): total cap for one request. 8 s + ≤3 s delay + 8 s = 19 s → 20 s.
            pipeline.AddTimeout(TimeSpan.FromSeconds(20));

            // Layer 2: ONE retry, never sooner than 2 s. A connection reset is what a tripped rate
            // limit looks like, and an immediate burst of retries is what would extend it.
            // Same transient set as estadísticas — network error, per-attempt timeout, 5xx, 408 —
            // and never a 4xx: 404 is the BCRA's real "nothing reported" answer, and 429 is the
            // endpoint telling us to stop.
            pipeline.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = 1,
                DelayGenerator   = _ => new ValueTask<TimeSpan?>(CentralDeudoresRetryDelay(Random.Shared)),
                ShouldHandle     = args => args.Outcome switch
                {
                    { Exception: HttpRequestException }      => PredicateResult.True(),
                    { Exception: TimeoutRejectedException }  => PredicateResult.True(),
                    { Result.StatusCode: >= HttpStatusCode.InternalServerError } => PredicateResult.True(),
                    { Result.StatusCode: HttpStatusCode.RequestTimeout }         => PredicateResult.True(),
                    _ => PredicateResult.False()
                }
            });

            // Layer 3 (innermost): per-attempt timeout, as on estadísticas.
            pipeline.AddTimeout(TimeSpan.FromSeconds(8));
        });

        return services;
    }

    /// <summary>
    /// The Central de Deudores retry delay: the 2 s floor plus 0–1 s of jitter. Hand-rolled because
    /// Polly's own jitter on a constant delay spreads AROUND the base and can land below it.
    /// </summary>
    internal static TimeSpan CentralDeudoresRetryDelay(Random random)
        => CentralDeudoresRetryBaseDelay + TimeSpan.FromMilliseconds(random.Next(0, CentralDeudoresRetryJitterMs + 1));

    private static void ConfigureClient(HttpClient client)
    {
        client.BaseAddress = new Uri("https://api.bcra.gob.ar");
        client.Timeout     = Timeout.InfiniteTimeSpan;   // managed by resilience pipeline
        client.DefaultRequestHeaders.Add("Accept", "application/json");
        client.DefaultRequestHeaders.Add("User-Agent", "FinancIA/1.0 (+https://hwapplications.com)");
    }
}
