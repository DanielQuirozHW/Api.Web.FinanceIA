// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-17

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
/// Guards the boundary between "BCRA failed" and "the caller asked us to stop" in
/// <c>BcraService.FetchWithStatusAsync</c>.
///
/// <para>
/// <see cref="IBcraService"/> promises its methods never throw, and a catch-all backstop enforces
/// that. But <see cref="TaskCanceledException"/> derives from <see cref="OperationCanceledException"/>,
/// so the backstop also caught caller-requested cancellation and converted it into a fabricated
/// <c>LookupFailed</c> — inventing a BCRA outage out of a client disconnect, and rendering the
/// existing <c>when (!ct.IsCancellationRequested)</c> filter dead in the process. Reachable today:
/// <c>BcraController</c> and <c>EntitiesController</c>'s refresh-bcra endpoint both pass a real
/// token (<c>AnalysisController</c> passes <c>CancellationToken.None</c> and is unaffected).
/// </para>
///
/// <para>
/// The two tests are a matched pair and must be read together: cancellation propagates, and
/// everything else still degrades rather than throwing. Fixing the first by weakening the backstop
/// would break the second.
/// </para>
///
/// Hand-rolled stubs, no mock library — mirroring <see cref="BcraMacroMappingTests"/>.
/// </summary>
public sealed class BcraServiceCancellationTests
{
    private const string Cuit = "30712345678";

    // ── Stubs ─────────────────────────────────────────────────────────────────

    /// <summary>Honours the token, exactly as a real HttpClient does when the caller cancels.</summary>
    private sealed class CancellationObservingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken  cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":200,\"results\":null}", Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>Fails in a way none of the specific catch clauses name, so only the backstop can handle it.</summary>
    private sealed class UnexpectedFailureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken  cancellationToken)
            => throw new InvalidOperationException("connection pool exploded");
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler) { BaseAddress = new Uri("https://api.bcra.gob.ar") };
    }

    private static BcraService BuildSut(HttpMessageHandler handler) =>
        BcraServiceTestFactory.Create(new StubHttpClientFactory(handler),
            new MemoryCache(Options.Create(new MemoryCacheOptions())));

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetDeudaAsync_WhenCallerCancels_PropagatesCancellation_RatherThanFakingLookupFailed()
    {
        var sut = BuildSut(new CancellationObservingHandler());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.GetDeudaAsync(Cuit, cts.Token));
    }

    [Fact]
    public async Task GetMacroAsync_WhenCallerCancels_PropagatesCancellation()
    {
        // Macro returns null on failure rather than a status object, so swallowing cancellation here
        // would silently hand the caller "no macro data" for a request they had already abandoned.
        var sut = BuildSut(new CancellationObservingHandler());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.GetMacroAsync(cts.Token));
    }

    [Fact]
    public async Task GetDeudaAsync_WhenUnexpectedErrorAndNoCancellation_StillDegradesToLookupFailed()
    {
        // The other half of the pair: the backstop must keep doing its job. An unanticipated
        // exception is still absorbed into LookupFailed so a caller's analysis never fails because
        // BCRA misbehaved — that is the contract IBcraService documents.
        var sut = BuildSut(new UnexpectedFailureHandler());

        var result = await sut.GetDeudaAsync(Cuit, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(BcraDeudaConsultaEstado.LookupFailed, result.Estado);

        // Stamped rather than left at default(DateTime), which used to persist as year 1.
        Assert.NotEqual(default, result.FetchedAt);
    }

    [Fact]
    public async Task GetBcraDataForAnalysisAsync_WhenCallerCancels_PropagatesCancellation()
    {
        // The aggregate path fans out four calls via Task.WhenAll. Cancellation must survive that
        // too, rather than being reduced to the "all three failed" null return.
        var sut = BuildSut(new CancellationObservingHandler());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.GetBcraDataForAnalysisAsync(Cuit, cts.Token));
    }
}
