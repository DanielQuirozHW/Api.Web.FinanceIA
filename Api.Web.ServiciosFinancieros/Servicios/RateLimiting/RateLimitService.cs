// Project : Api.Web.ServiciosFinancieros
// Created : 2026-04-30
// Modified: 2026-04-30

using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace Api.Web.ServiciosFinancieros.Servicios.RateLimiting;

/// <summary>
/// In-memory rate limit service using <see cref="IMemoryCache"/> for counters
/// and a <see cref="ConcurrentDictionary{TKey,TValue}"/> for active-user tracking.
/// Checks per-user, per-tenant and per-IP limits based on request path.
/// </summary>
public sealed class RateLimitService : IRateLimitService
{
    private readonly IMemoryCache                              _cache;
    private readonly IConfiguration                           _config;
    private readonly ILogger<RateLimitService>                _logger;
    private readonly ConcurrentDictionary<string, HashSet<string>> _activeUsers = new();

    public RateLimitService(
        IMemoryCache cache,
        IConfiguration config,
        ILogger<RateLimitService> logger)
    {
        _cache  = cache;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<RateLimitResult> CheckRateLimitAsync(
        HttpContext context,
        CancellationToken cancellationToken = default)
    {
        var path      = context.Request.Path.Value?.ToLowerInvariant() ?? string.Empty;
        var now       = DateTimeOffset.UtcNow;
        var windowEnd = now.AddMinutes(1).ToUnixTimeSeconds();

        var (checkUser, checkTenant, checkIP) = GetLimitsToCheck(path);

        // ── Per-IP (anonymous) ───────────────────────────────────────────────
        if (checkIP)
        {
            var ipAddress = context.Request.Headers["X-Forwarded-For"].FirstOrDefault()
                            ?? context.Connection.RemoteIpAddress?.ToString()
                            ?? "unknown";

            var ipLimit = _config.GetValue<int>("RateLimiting:PerIP:OnboardingToken");
            var ipKey   = $"rl:ip:{ipAddress}:{path}";
            var ipCount = IncrementCounter(ipKey);

            if (ipCount > ipLimit)
            {
                var retryAfter = CalculateRetryAfter(ipKey, now);

                if (_config.GetValue<bool>("RateLimiting:General:EnableLogging"))
                    _logger.LogWarning("IP rate limit exceeded. IP: {IP}, Path: {Path}, Count: {Count}", ipAddress, path, ipCount);

                return Task.FromResult(new RateLimitResult
                {
                    IsAllowed        = false,
                    ErrorCode        = "rate_limit_exceeded_ip",
                    ErrorMessage     = $"IP rate limit exceeded. Try again in {retryAfter} seconds.",
                    RetryAfterSeconds = retryAfter,
                    UserLimit         = 0,
                    UserRemaining     = 0,
                    UserResetTimestamp = windowEnd
                });
            }

            return Task.FromResult(new RateLimitResult
            {
                IsAllowed          = true,
                UserLimit          = ipLimit,
                UserRemaining      = Math.Max(0, ipLimit - ipCount),
                UserResetTimestamp = windowEnd
            });
        }

        // ── Authenticated: extract claims ────────────────────────────────────
        var tenantId = context.User.FindFirst("tenantId")?.Value;
        if (tenantId == null && context.Items["TenantContext"] is Models.TenantContext tc)
        {
            tenantId = tc.TenantId;
        }
        var uid      = context.User.FindFirst("uid")?.Value
                       ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        // ── Per-User ─────────────────────────────────────────────────────────
        int userLimit     = 0;
        int userCount     = 0;
        int userRemaining = 0;

        if (checkUser && !string.IsNullOrEmpty(tenantId) && !string.IsNullOrEmpty(uid))
        {
            userLimit = GetUserLimit(path);
            var userKey = $"rl:user:{tenantId}:{uid}:{path}";
            userCount     = IncrementCounter(userKey);
            userRemaining = Math.Max(0, userLimit - userCount);

            TrackActiveUser(tenantId, uid);

            if (userCount > userLimit)
            {
                var retryAfter = CalculateRetryAfter(userKey, now);

                if (_config.GetValue<bool>("RateLimiting:General:EnableLogging"))
                    _logger.LogWarning("User rate limit exceeded. TenantId: {TenantId}, Uid: {Uid}, Path: {Path}, Count: {Count}", tenantId, uid, path, userCount);

                return Task.FromResult(new RateLimitResult
                {
                    IsAllowed          = false,
                    ErrorCode          = "rate_limit_exceeded_user",
                    ErrorMessage       = $"User rate limit exceeded. Try again in {retryAfter} seconds.",
                    RetryAfterSeconds  = retryAfter,
                    UserLimit          = userLimit,
                    UserRemaining      = 0,
                    UserResetTimestamp = windowEnd
                });
            }
        }

        // ── Per-Tenant ───────────────────────────────────────────────────────
        int? tenantLimit     = null;
        int? tenantRemaining = null;

        if (checkTenant && !string.IsNullOrEmpty(tenantId))
        {
            tenantLimit = CalculateTenantLimit(tenantId, path);
            var tenantKey   = $"rl:tenant:{tenantId}:{path}";
            var tenantCount = IncrementCounter(tenantKey);
            tenantRemaining = Math.Max(0, tenantLimit.Value - tenantCount);

            if (tenantCount > tenantLimit)
            {
                var retryAfter = CalculateRetryAfter(tenantKey, now);

                if (_config.GetValue<bool>("RateLimiting:General:EnableLogging"))
                    _logger.LogWarning("Tenant rate limit exceeded. TenantId: {TenantId}, Path: {Path}, Count: {Count}", tenantId, path, tenantCount);

                return Task.FromResult(new RateLimitResult
                {
                    IsAllowed            = false,
                    ErrorCode            = "rate_limit_exceeded_tenant",
                    ErrorMessage         = $"Tenant rate limit exceeded. Try again in {retryAfter} seconds.",
                    RetryAfterSeconds    = retryAfter,
                    UserLimit            = userLimit,
                    UserRemaining        = userRemaining,
                    UserResetTimestamp   = windowEnd,
                    TenantLimit          = tenantLimit,
                    TenantRemaining      = 0,
                    TenantResetTimestamp = windowEnd
                });
            }
        }

        return Task.FromResult(new RateLimitResult
        {
            IsAllowed            = true,
            UserLimit            = userLimit,
            UserRemaining        = userRemaining,
            UserResetTimestamp   = windowEnd,
            TenantLimit          = tenantLimit,
            TenantRemaining      = tenantRemaining,
            TenantResetTimestamp = tenantLimit.HasValue ? windowEnd : null
        });
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private static (bool checkUser, bool checkTenant, bool checkIP) GetLimitsToCheck(string path)
    {
        if (path.StartsWith("/api/search"))
            return (checkUser: true, checkTenant: true, checkIP: false);

        if (path.StartsWith("/api/consultafetch"))
            return (checkUser: false, checkTenant: true, checkIP: false);

        if (path.Contains("/auth/onboarding-token"))
            return (checkUser: false, checkTenant: false, checkIP: true);

        if (path.Contains("/auth/token"))
            return (checkUser: true, checkTenant: true, checkIP: false);

        return (checkUser: false, checkTenant: false, checkIP: false);
    }

    private int GetUserLimit(string path)
    {
        if (path.Contains("/auth"))
            return _config.GetValue<int>("RateLimiting:PerUser:Auth");

        return _config.GetValue<int>("RateLimiting:PerUser:Search");
    }

    private int CalculateTenantLimit(string tenantId, string path)
    {
        if (path.Contains("/search"))
        {
            var activeUsers  = GetActiveUserCount(tenantId);
            var basePerUser  = _config.GetValue<int>("RateLimiting:PerTenant:SearchBasePerUser");
            var maxCap       = _config.GetValue<int>("RateLimiting:PerTenant:SearchMaxCap");
            return Math.Min(activeUsers * basePerUser, maxCap);
        }

        if (path.Contains("/consultafetch"))
            return _config.GetValue<int>("RateLimiting:PerTenant:FetchXml");

        if (path.Contains("/auth"))
            return _config.GetValue<int>("RateLimiting:PerTenant:Auth");

        return int.MaxValue;
    }

    private int IncrementCounter(string key)
    {
        var count = _cache.GetOrCreate(key, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
            return 0;
        });

        count++;
        _cache.Set(key, count, TimeSpan.FromMinutes(1));
        return count;
    }

    private void TrackActiveUser(string tenantId, string uid)
    {
        var set = _activeUsers.GetOrAdd(tenantId, _ => new HashSet<string>());
        lock (set)
        {
            set.Add(uid);
        }
    }

    private int GetActiveUserCount(string tenantId)
    {
        if (_activeUsers.TryGetValue(tenantId, out var set))
        {
            lock (set)
            {
                return Math.Max(set.Count, 1);
            }
        }

        return 1;
    }

    private static int CalculateRetryAfter(string key, DateTimeOffset now)
    {
        // Window is 1 minute; approximate seconds remaining
        var secondsIntoWindow = now.Second % 60;
        return Math.Max(1, 60 - secondsIntoWindow);
    }
}
