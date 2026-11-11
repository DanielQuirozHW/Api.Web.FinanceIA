// Project : Api.Web.FinanceIA
// Created : 2026-10-19

using Api.Web.FinanceIA.Models;
using Microsoft.Extensions.Options;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// <see cref="SemaphoreSlim"/>-backed <see cref="IBcraCentralDeudoresGate"/>. A permit covers ONE
/// HTTP request including its resilience-pipeline retries and their back-off, so a request waiting
/// out a retry delay keeps counting against the limit — which is the point: a retry against a
/// rate-limited endpoint must not make room for yet another request.
/// </summary>
public sealed class BcraCentralDeudoresGate : IBcraCentralDeudoresGate, IDisposable
{
    private readonly SemaphoreSlim _semaphore;

    /// <summary>
    /// Initializes a new instance of <see cref="BcraCentralDeudoresGate"/>.
    /// </summary>
    public BcraCentralDeudoresGate(IOptions<BcraOptions> options)
    {
        var permits = Math.Max(1, options.Value.CentralDeudoresMaxConcurrency);
        _semaphore  = new SemaphoreSlim(permits, permits);
    }

    /// <inheritdoc />
    public async ValueTask<IDisposable> EnterAsync(CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        return new Permit(_semaphore);
    }

    /// <inheritdoc />
    public void Dispose() => _semaphore.Dispose();

    /// <summary>Releases its permit exactly once, however many times it is disposed.</summary>
    private sealed class Permit(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                semaphore.Release();
        }
    }
}
