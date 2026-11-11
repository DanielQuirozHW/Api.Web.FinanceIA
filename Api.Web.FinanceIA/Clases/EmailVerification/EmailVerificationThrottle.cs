// Project : Api.Web.FinanceIA
// Created : 2026-10-21

using Api.Web.FinanceIA.Clases.PasswordReset;
using Api.Web.FinanceIA.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Api.Web.FinanceIA.Clases.EmailVerification;

/// <summary>
/// <see cref="IEmailVerificationThrottle"/> over the shared <see cref="IMemoryCache"/>: the same fixed
/// window as <see cref="PasswordResetEmailThrottle"/>, keyed by UserId instead of the email tag (the
/// caller is signed in, so there is no address to hide behind and no enumeration to defend). It
/// opens on the first request and expires <see cref="EmailVerificationOptions.PerUserWindowMinutes"/>
/// later. Singleton; in-process, so with several App Service instances each enforces its own budget.
/// </summary>
public sealed class EmailVerificationThrottle : IEmailVerificationThrottle
{
    private readonly IMemoryCache _cache;
    private readonly TimeProvider _timeProvider;
    private readonly int _limit;
    private readonly TimeSpan _window;
    private readonly object _gate = new();

    /// <summary>Initializes a new instance of <see cref="EmailVerificationThrottle"/> on the system clock.</summary>
    public EmailVerificationThrottle(IMemoryCache cache, IOptions<EmailVerificationOptions> options)
        : this(cache, options, TimeProvider.System)
    {
    }

    /// <summary>Clock-injectable constructor, so tests can move past the window without waiting.</summary>
    internal EmailVerificationThrottle(
        IMemoryCache cache,
        IOptions<EmailVerificationOptions> options,
        TimeProvider timeProvider)
    {
        _cache        = cache;
        _timeProvider = timeProvider;
        _limit        = Math.Max(1, options.Value.PerUserLimit);
        _window       = TimeSpan.FromMinutes(Math.Max(1, options.Value.PerUserWindowMinutes));
    }

    /// <inheritdoc />
    public PasswordResetThrottleDecision Register(string tenantId, Guid userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var key = $"emailverify:{tenantId}:{userId:N}";
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
