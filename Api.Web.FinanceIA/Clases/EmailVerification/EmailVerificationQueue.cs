// Project : Api.Web.FinanceIA
// Created : 2026-10-21

using System.Threading.Channels;
using Api.Web.FinanceIA.Models;
using Microsoft.Extensions.Options;

namespace Api.Web.FinanceIA.Clases.EmailVerification;

/// <summary>
/// <see cref="IEmailVerificationQueue"/> on a BOUNDED <see cref="Channel{T}"/> of
/// <see cref="EmailVerificationOptions.QueueCapacity"/>. Singleton: request scopes write, the worker
/// reads. Items still queued when the process stops are lost; the user simply asks again.
/// </summary>
public sealed class EmailVerificationQueue : IEmailVerificationQueue
{
    private readonly Channel<EmailVerificationWorkItem> _channel;

    /// <summary>Initializes a new instance of <see cref="EmailVerificationQueue"/>.</summary>
    public EmailVerificationQueue(IOptions<EmailVerificationOptions> options)
    {
        _channel = Channel.CreateBounded<EmailVerificationWorkItem>(
            new BoundedChannelOptions(Math.Max(1, options.Value.QueueCapacity))
            {
                FullMode     = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
    }

    /// <inheritdoc />
    public bool TryEnqueue(EmailVerificationWorkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return _channel.Writer.TryWrite(item);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<EmailVerificationWorkItem> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}
