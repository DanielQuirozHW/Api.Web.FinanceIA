// Project : Api.Web.FinanceIA
// Created : 2026-10-19

using Api.Web.FinanceIA.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Api.Web.FinanceIA.Clases.PasswordReset;

/// <summary>
/// <see cref="IPasswordResetEmailThrottle"/> over the shared <see cref="IMemoryCache"/>. A fixed
/// window per key: it opens on the first request and expires
/// <see cref="PasswordResetOptions.PerEmailWindowMinutes"/> later. Singleton; in-process, so with
/// several App Service instances each enforces its own budget, as the IP limiter does.
/// </summary>
/// <remarks>
/// The key holds the <see cref="EmailHasher"/> tag, never the address. One lock for every key is
/// enough: the critical section is a dictionary read and an increment, on an endpoint the per-IP
/// limit already holds to a handful of calls a minute.
/// </remarks>
public sealed class PasswordResetEmailThrottle : IPasswordResetEmailThrottle
{
    private readonly IMemoryCache _cache;
    private readonly TimeProvider _timeProvider;
    private readonly int _limit;
    private readonly TimeSpan _window;
    private readonly object _gate = new();

    /// <summary>Initializes a new instance of <see cref="PasswordResetEmailThrottle"/> on the system clock.</summary>
    public PasswordResetEmailThrottle(IMemoryCache cache, IOptions<PasswordResetOptions> options)
        : this(cache, options, TimeProvider.System)
    {
    }

    /// <summary>Clock-injectable constructor, so tests can move past the window without waiting.</summary>
    internal PasswordResetEmailThrottle(
        IMemoryCache cache,
        IOptions<PasswordResetOptions> options,
        TimeProvider timeProvider)
    {
        _cache        = cache;
        _timeProvider = timeProvider;
        _limit        = Math.Max(1, options.Value.PerEmailLimit);
        _window       = TimeSpan.FromMinutes(Math.Max(1, options.Value.PerEmailWindowMinutes));
    }

    /// <inheritdoc />
    public PasswordResetThrottleDecision Register(string tenantId, string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        var key = $"pwreset:{tenantId}:{EmailHasher.Hash(email)}";
        var now = _timeProvider.GetUtcNow();

        lock (_gate)
        {
            if (!_cache.TryGetValue(key, out Window? window) || window is null || window.EndsAt <= now)
            {
                window = new Window(now + _window);

                // Relative expiry: the cache evicts on its own clock, so an absolute time taken from
                // _timeProvider must not be handed to it. EndsAt above is what decides the reset.
                _cache.Set(key, window, _window);
            }

            window.Count++;

            if (window.Count > _limit)
            {
                var retryAfter = (int)Math.Ceiling((window.EndsAt - now).TotalSeconds);
                return new PasswordResetThrottleDecision(false, Math.Max(1, retryAfter));
            }

            return new PasswordResetThrottleDecision(true, 0);
        }
    }

    private sealed class Window(DateTimeOffset endsAt)
    {
        public DateTimeOffset EndsAt { get; } = endsAt;

        public int Count { get; set; }
    }
}
