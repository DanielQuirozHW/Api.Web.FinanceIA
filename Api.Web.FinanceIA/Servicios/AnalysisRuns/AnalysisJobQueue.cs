// Project : Api.Web.FinanceIA
// Created : 2026-10-19

using System.Threading.Channels;
using Api.Web.FinanceIA.Models;
using Microsoft.Extensions.Options;

namespace Api.Web.FinanceIA.Servicios.AnalysisRuns;

/// <summary>
/// <see cref="IAnalysisJobQueue"/> on a BOUNDED <see cref="Channel{T}"/> of
/// <see cref="AnalysisOptions.QueueCapacity"/>. Singleton: request scopes write, the worker reads.
/// Bounded, unlike the legajo queue, because each item is minutes of Claude spend and the bound must
/// hold even if admission were bypassed; admission counts queued AND running runs against the same
/// number, so an admitted write always fits.
/// </summary>
public sealed class AnalysisJobQueue : IAnalysisJobQueue
{
    private readonly Channel<AnalysisRun> _channel;

    /// <summary>Initializes a new instance of <see cref="AnalysisJobQueue"/>.</summary>
    public AnalysisJobQueue(IOptions<AnalysisOptions> options)
    {
        _channel = Channel.CreateBounded<AnalysisRun>(
            new BoundedChannelOptions(Math.Max(1, options.Value.QueueCapacity))
            {
                FullMode     = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false
            });
    }

    /// <inheritdoc />
    public bool TryEnqueue(AnalysisRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return _channel.Writer.TryWrite(run);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<AnalysisRun> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}
