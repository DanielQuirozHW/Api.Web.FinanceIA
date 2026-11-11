// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using System.Threading.Channels;

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// <see cref="ILegajoPipelineQueue"/> on an unbounded <see cref="Channel{T}"/>. Singleton: the request
/// scopes write, the worker reads. Unbounded because an item is two small values and the producers are
/// analysts uploading by hand; the worker's concurrency, not the queue, bounds the spend.
/// </summary>
public sealed class LegajoPipelineQueue : ILegajoPipelineQueue
{
    private readonly Channel<LegajoWorkItem> _channel = Channel.CreateUnbounded<LegajoWorkItem>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

    /// <inheritdoc />
    public void Enqueue(LegajoWorkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _channel.Writer.TryWrite(item);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<LegajoWorkItem> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}
