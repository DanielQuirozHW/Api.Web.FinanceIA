// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// Process-wide concurrency limit for BCRA Central de Deudores HTTP requests. Registered as a
/// singleton so every request scope — every user, every analysis — shares the same permits.
/// </summary>
public interface IBcraCentralDeudoresGate
{
    /// <summary>
    /// Waits (queues) for a permit and returns a handle that releases it on dispose. Honours
    /// <paramref name="ct"/>: a cancelled wait throws <see cref="OperationCanceledException"/> and
    /// holds no permit.
    /// </summary>
    ValueTask<IDisposable> EnterAsync(CancellationToken ct);
}
