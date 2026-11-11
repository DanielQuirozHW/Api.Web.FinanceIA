// Project : Api.Web.FinanceIA
// Created : 2026-10-19

using System.Threading.Channels;
using Api.Web.FinanceIA.Models;
using Microsoft.Extensions.Options;

namespace Api.Web.FinanceIA.Clases.PasswordReset;

/// <summary>
/// <see cref="IPasswordResetQueue"/> on a BOUNDED <see cref="Channel{T}"/> of
/// <see cref="PasswordResetOptions.QueueCapacity"/>. Singleton: request scopes write, the worker
/// reads. Bounded because the endpoint is anonymous — the per-IP and per-email limits keep it small,
/// but the bound must hold even if they were bypassed. Items still queued when the process stops are
/// lost; the user simply asks again.
/// </summary>
public sealed class PasswordResetQueue : IPasswordResetQueue
{
    private readonly Channel<PasswordResetWorkItem> _channel;

    /// <summary>Initializes a new instance of <see cref="PasswordResetQueue"/>.</summary>
    public PasswordResetQueue(IOptions<PasswordResetOptions> options)
    {
        _channel = Channel.CreateBounded<PasswordResetWorkItem>(
            new BoundedChannelOptions(Math.Max(1, options.Value.QueueCapacity))
            {
                FullMode     = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
    }

    /// <inheritdoc />
    public bool TryEnqueue(PasswordResetWorkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return _channel.Writer.TryWrite(item);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<PasswordResetWorkItem> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}
