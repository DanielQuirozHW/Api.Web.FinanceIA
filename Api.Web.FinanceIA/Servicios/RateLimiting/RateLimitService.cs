// Project : Api.Web.FinanceIA
// Created : 2026-04-30
// Modified: 2026-10-25

using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace Api.Web.FinanceIA.Servicios.RateLimiting;

/// <summary>
/// In-memory rate limit service using <see cref="IMemoryCache"/> for counters
/// and a <see cref="ConcurrentDictionary{TKey,TValue}"/> for active-user tracking.
/// Checks per-user, per-tenant and per-IP limits based on request path.
/// </summary>
public sealed class RateLimitService : IRateLimitService
{
    /// <summary>
    /// Fallback per-IP budget for POST /api/errorlog/client when
    /// <c>RateLimiting:PerIP:ClientErrorLog</c> is absent from configuration.
    /// </summary>
    private const int DefaultClientErrorLogIpLimit = 10;

    /// <summary>
    /// Fallback budgets for the MFA backup-code endpoints. Every one of them has an in-code
    /// default for the same reason the error-log budget does: <c>GetValue&lt;int&gt;</c> yields 0
    /// for a missing key and the caller tests <c>count &gt; limit</c>, so a key absent from a
    /// deployed configuration would reject every request and lock users out of their own
    /// recovery codes.
    /// </summary>
    /// <remarks>
    /// Validation is the brute-force target and gets the tightest budget: 5 attempts a minute
    /// against 40-bit codes leaves an attacker nowhere, while a user typing one code by hand
    /// never comes close. Generation is equally tight — nobody needs to re-issue a set more than
    /// a handful of times a minute — and the remaining-count read is a cheap UI poll.
    /// </remarks>
    private const int DefaultMfaValidateUserLimit  = 5;
    private const int DefaultMfaValidateIpLimit    = 20;
    private const int DefaultMfaGenerateUserLimit  = 5;
    private const int DefaultMfaRemainingUserLimit = 30;

    /// <summary>
    /// Fallback per-IP budget for POST /api/mfa/backup-codes/redeem.
    /// </summary>
    /// <remarks>
    /// Tighter than the authenticated /validate budget of 20, because this endpoint is anonymous
    /// and takes a password: per-IP is the ONLY gate it has, where /validate has a per-user
    /// budget doing the real work behind it. 10/min still leaves a legitimate user, who is
    /// reading one code off a piece of paper, plenty of room to fumble it.
    /// </remarks>
    private const int DefaultMfaRedeemIpLimit = 10;

    /// <summary>
    /// Fallback per-IP budget for POST /api/bff/auth/password-reset.
    /// </summary>
    /// <remarks>
    /// Anonymous and it sends an email, so per-IP is the only gate the middleware can apply (the
    /// per-email budget lives in the controller, which is the only layer that reads the body).
    /// 5/min matches the onboarding-token budget: a person who mistypes their address retries a
    /// couple of times, a script spraying addresses does not get far.
    /// </remarks>
    private const int DefaultPasswordResetIpLimit = 5;

    /// <summary>
    /// Fallback per-IP budget for POST /api/bff/setup/first-admin.
    /// </summary>
    /// <remarks>
    /// Anonymous, guarded by a shared key, and it sends an email: per-IP is its only gate, and the
    /// fall-through in <see cref="GetLimitsToCheck"/> would otherwise ship it unlimited. An operator
    /// makes one or two calls per client, so 5/min costs nobody anything and caps key guessing.
    /// </remarks>
    private const int DefaultFirstAdminSetupIpLimit = 5;

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
            // Never X-Forwarded-For: its left-most entry is client-written. UseForwardedHeaders
            // (ClientIpForwarding) has already put the resolved client IP here.
            var ipAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            var ipLimit = GetIpLimit(path);
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
                    ErrorMessage     = $"Se superó el límite de solicitudes por IP. El límite se restablece en {retryAfter} segundos.",
                    RetryAfterSeconds = retryAfter,
                    UserLimit         = 0,
                    UserRemaining     = 0,
                    UserResetTimestamp = windowEnd
                });
            }

            // Allowed by IP. A path that ALSO carries a per-user budget must not stop here —
            // returning at this point is what previously made "per-IP and per-user on the same
            // endpoint" impossible to express. A denial above still returns immediately.
            if (!checkUser)
            {
                return Task.FromResult(new RateLimitResult
                {
                    IsAllowed          = true,
                    UserLimit          = ipLimit,
                    UserRemaining      = Math.Max(0, ipLimit - ipCount),
                    UserResetTimestamp = windowEnd
                });
            }
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
                    ErrorMessage       = $"Se superó el límite de solicitudes del usuario. El límite se restablece en {retryAfter} segundos.",
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
                    ErrorMessage         = $"Se superó el límite de solicitudes del tenant. El límite se restablece en {retryAfter} segundos.",
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

        // Anonymous password-reset request. Per-IP only — there is no identity — and without this
        // branch the fall-through below would leave an email-sending endpoint unlimited.
        if (IsPasswordResetPath(path))
            return (checkUser: false, checkTenant: false, checkIP: true);

        // First-admin bootstrap (POST /api/bff/setup/first-admin). Anonymous and key-guarded: per-IP is
        // the only identity there is.
        if (IsFirstAdminSetupPath(path))
            return (checkUser: false, checkTenant: false, checkIP: true);

        // Anonymous browser error reports (POST /api/errorlog/client). Per-IP because the endpoint
        // takes no identity by design. Without this branch the fall-through below would leave it
        // completely unlimited — an unrecognised path is checked against nothing.
        if (IsClientErrorLogPath(path))
            return (checkUser: false, checkTenant: false, checkIP: true);

        // MFA backup codes. Mostly authenticated, so the per-user budget is the real gate — but
        // validation also carries a per-IP one, because the per-user branch below applies only
        // when BOTH a tenantId and a uid claim are present and silently does nothing when either
        // is missing. On the endpoint an attacker would brute-force, that silence needs a
        // backstop. No tenant budget: a backup code belongs to a user, not to a tenant's pool.
        //
        // /redeem is the anonymous login-recovery route: it has NO uid claim by construction, so
        // the per-user branch can never fire for it and per-IP is its only limit. Leaving it out
        // of the IP set would not merely weaken it — the fall-through at the bottom of this
        // method checks an unrecognised path against nothing, so it would ship unlimited.
        if (IsMfaBackupCodePath(path))
            return (checkUser: true, checkTenant: false,
                    checkIP: IsMfaValidatePath(path) || IsMfaRedeemPath(path));

        if (path.Contains("/auth/token"))
            return (checkUser: true, checkTenant: true, checkIP: false);

        return (checkUser: false, checkTenant: false, checkIP: false);
    }

    /// <summary>
    /// Resolves the per-IP, per-minute budget for an anonymous endpoint.
    /// </summary>
    /// <remarks>
    /// The error-log budget carries an in-code default on purpose: <c>GetValue&lt;int&gt;</c> yields 0
    /// for a missing key and the caller tests <c>count &gt; limit</c>, so a key absent from a deployed
    /// configuration would reject every single request. The onboarding read is left exactly as it was
    /// so this path change alters no existing limit.
    /// </remarks>
    private int GetIpLimit(string path)
    {
        if (IsClientErrorLogPath(path))
            return _config.GetValue("RateLimiting:PerIP:ClientErrorLog", DefaultClientErrorLogIpLimit);

        if (IsMfaValidatePath(path))
            return _config.GetValue("RateLimiting:PerIP:MfaBackupCodeValidate", DefaultMfaValidateIpLimit);

        if (IsMfaRedeemPath(path))
            return _config.GetValue("RateLimiting:PerIP:MfaBackupCodeRedeem", DefaultMfaRedeemIpLimit);

        if (IsPasswordResetPath(path))
            return _config.GetValue("RateLimiting:PerIP:PasswordReset", DefaultPasswordResetIpLimit);

        if (IsFirstAdminSetupPath(path))
            return _config.GetValue("RateLimiting:PerIP:FirstAdminSetup", DefaultFirstAdminSetupIpLimit);

        return _config.GetValue<int>("RateLimiting:PerIP:OnboardingToken");
    }

    private static bool IsPasswordResetPath(string path)
        => path.StartsWith("/api/bff/auth/password-reset");

    private static bool IsFirstAdminSetupPath(string path)
        => path.StartsWith("/api/bff/setup/first-admin");

    private static bool IsClientErrorLogPath(string path)
        => path.StartsWith("/api/errorlog") || path.StartsWith("/api/bff/errorlog");

    /// <summary>
    /// True for every MFA backup-code route, under both the /api and /api/bff prefixes the
    /// controller registers.
    /// </summary>
    private static bool IsMfaBackupCodePath(string path)
        => path.StartsWith("/api/mfa/backup-codes")
           || path.StartsWith("/api/bff/mfa/backup-codes");

    private static bool IsMfaValidatePath(string path)
        => IsMfaBackupCodePath(path) && path.EndsWith("/validate");

    private static bool IsMfaGeneratePath(string path)
        => IsMfaBackupCodePath(path) && path.EndsWith("/generate");

    /// <summary>
    /// True for the anonymous backup-code login route, under both prefixes.
    /// </summary>
    private static bool IsMfaRedeemPath(string path)
        => IsMfaBackupCodePath(path) && path.EndsWith("/redeem");

    private int GetUserLimit(string path)
    {
        // Checked before the "/auth" branch so the intent is explicit, even though no MFA route
        // contains that segment today — a later rename must not silently reassign these budgets.
        // /redeem is anonymous, so this branch normally cannot fire — the per-user check needs a
        // uid claim. It is here anyway because it CAN: the Internal handler reads the bff_session
        // cookie and populates User even on an [AllowAnonymous] action, so a caller who still
        // holds a session gets a uid. Without this line such a request would land on the
        // 30/min "remaining" budget, six times looser than the code-guessing endpoints, purely
        // because of where it sits in this if-chain. Give it the validate budget instead.
        if (IsMfaValidatePath(path) || IsMfaRedeemPath(path))
            return _config.GetValue("RateLimiting:PerUser:MfaBackupCodeValidate", DefaultMfaValidateUserLimit);

        if (IsMfaGeneratePath(path))
            return _config.GetValue("RateLimiting:PerUser:MfaBackupCodeGenerate", DefaultMfaGenerateUserLimit);

        if (IsMfaBackupCodePath(path))
            return _config.GetValue("RateLimiting:PerUser:MfaBackupCodeRemaining", DefaultMfaRemainingUserLimit);

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
