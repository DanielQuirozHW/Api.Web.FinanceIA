// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-03

using System.Security.Claims;
using Api.Web.FinanceIA.Servicios.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Guards the rate limits on the MFA backup-code endpoints.
///
/// <para>
/// <see cref="RateLimitService"/> dispatches on request path and its final branch returns "check
/// nothing", so an endpoint without its own branch is not loosely limited but <em>completely
/// unlimited</em>. On a validate endpoint that answers "is this the right recovery code?", that
/// silent default is an offline brute force with a network hop.
/// </para>
/// <para>
/// The second property pinned here is the in-code fallback: <c>GetValue&lt;int&gt;</c> yields 0 for
/// a missing key and the service tests <c>count &gt; limit</c>, so a configuration without the key
/// would reject every request — locking users out of the recovery path they only reach when
/// something has already gone wrong.
/// </para>
/// <para>
/// The third is that validation carries BOTH budgets. The per-user branch runs only when a
/// tenantId and a uid claim are both present and does nothing at all when either is missing, so
/// without the per-IP backstop an attacker could strip a claim and rate-limit themselves out of
/// the limit.
/// </para>
/// </summary>
public sealed class RateLimitServiceMfaBackupCodesTests
{
    private const string ValidatePath  = "/api/mfa/backup-codes/validate";
    private const string GeneratePath  = "/api/mfa/backup-codes/generate";
    private const string RemainingPath = "/api/mfa/backup-codes/remaining";

    private const string RedeemPath    = "/api/mfa/backup-codes/redeem";

    private const string BffValidatePath  = "/api/bff/mfa/backup-codes/validate";
    private const string BffGeneratePath  = "/api/bff/mfa/backup-codes/generate";
    private const string BffRemainingPath = "/api/bff/mfa/backup-codes/remaining";
    private const string BffRedeemPath    = "/api/bff/mfa/backup-codes/redeem";

    private const string Tenant = "SgrOneClickSandboxFB2";
    private const string Uid    = "firebase-uid-a";

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static RateLimitService BuildSut(params (string Key, string Value)[] settings)
    {
        var values = new Dictionary<string, string?>();
        foreach (var (key, value) in settings)
        {
            values[key] = value;
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        return new RateLimitService(
            new MemoryCache(new MemoryCacheOptions()),
            config,
            NullLogger<RateLimitService>.Instance);
    }

    private static DefaultHttpContext BuildContext(
        string  path,
        string  ip     = "203.0.113.7",
        string? uid    = Uid,
        string? tenant = Tenant)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ip);

        var claims = new List<Claim>();
        if (uid is not null)    claims.Add(new Claim("uid", uid));
        if (tenant is not null) claims.Add(new Claim("tenantId", tenant));

        if (claims.Count > 0)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Internal"));
        }

        return context;
    }

    // ── Coverage: every route is limited, under both prefixes ───────────────────

    [Theory]
    [InlineData(ValidatePath)]
    [InlineData(GeneratePath)]
    [InlineData(RemainingPath)]
    [InlineData(RedeemPath)]
    [InlineData(BffValidatePath)]
    [InlineData(BffGeneratePath)]
    [InlineData(BffRemainingPath)]
    [InlineData(BffRedeemPath)]
    public async Task EveryBackupCodeRoute_IsRateLimited(string path)
    {
        var sut = BuildSut();

        // Far past any of the in-code defaults (5, 5, 30). An unbranched path would allow all of
        // these, because the fall-through checks nothing at all.
        RateLimitResult? result = null;
        for (var i = 0; i < 40; i++)
        {
            result = await sut.CheckRateLimitAsync(BuildContext(path));
        }

        Assert.NotNull(result);
        Assert.False(result!.IsAllowed);
    }

    // ── Per-user budgets ────────────────────────────────────────────────────────

    [Fact]
    public async Task Validate_AllowsUpToTheConfiguredPerUserLimitThenRejects()
    {
        var sut = BuildSut(("RateLimiting:PerUser:MfaBackupCodeValidate", "3"));

        for (var i = 0; i < 3; i++)
        {
            var allowed = await sut.CheckRateLimitAsync(BuildContext(ValidatePath));
            Assert.True(allowed.IsAllowed, $"Request {i + 1} of 3 should have been allowed.");
        }

        var denied = await sut.CheckRateLimitAsync(BuildContext(ValidatePath));

        Assert.False(denied.IsAllowed);
        Assert.Equal("rate_limit_exceeded_user", denied.ErrorCode);
    }

    [Fact]
    public async Task Validate_FallsBackToTheInCodeLimitWhenTheKeyIsMissing()
    {
        var sut = BuildSut();

        // Five is the in-code default. Without it a missing key would mean a limit of 0 and a
        // rejection on the very first attempt.
        for (var i = 0; i < 5; i++)
        {
            var allowed = await sut.CheckRateLimitAsync(BuildContext(ValidatePath));
            Assert.True(allowed.IsAllowed, $"Request {i + 1} of 5 should have been allowed.");
        }

        Assert.False((await sut.CheckRateLimitAsync(BuildContext(ValidatePath))).IsAllowed);
    }

    [Fact]
    public async Task Generate_FallsBackToTheInCodeLimitWhenTheKeyIsMissing()
    {
        var sut = BuildSut();

        for (var i = 0; i < 5; i++)
        {
            Assert.True((await sut.CheckRateLimitAsync(BuildContext(GeneratePath))).IsAllowed);
        }

        Assert.False((await sut.CheckRateLimitAsync(BuildContext(GeneratePath))).IsAllowed);
    }

    [Fact]
    public async Task Remaining_FallsBackToTheLoosestInCodeLimitWhenTheKeyIsMissing()
    {
        var sut = BuildSut();

        // The remaining-count read is a cheap UI poll, so its budget is deliberately the loosest
        // of the three — 30, not the 5 the two write paths get.
        for (var i = 0; i < 30; i++)
        {
            Assert.True((await sut.CheckRateLimitAsync(BuildContext(RemainingPath))).IsAllowed);
        }

        Assert.False((await sut.CheckRateLimitAsync(BuildContext(RemainingPath))).IsAllowed);
    }

    [Fact]
    public async Task BackupCodeRoutes_DoNotShareABudgetWithEachOther()
    {
        var sut = BuildSut();

        for (var i = 0; i < 5; i++)
        {
            await sut.CheckRateLimitAsync(BuildContext(ValidatePath));
        }

        Assert.False((await sut.CheckRateLimitAsync(BuildContext(ValidatePath))).IsAllowed);

        // Exhausting validation must not lock the user out of seeing how many codes they have.
        Assert.True((await sut.CheckRateLimitAsync(BuildContext(RemainingPath))).IsAllowed);
    }

    [Fact]
    public async Task Validate_BudgetsUsersSeparately()
    {
        var sut = BuildSut();

        for (var i = 0; i < 6; i++)
        {
            await sut.CheckRateLimitAsync(BuildContext(ValidatePath, uid: "attacker"));
        }

        var victim = await sut.CheckRateLimitAsync(
            BuildContext(ValidatePath, ip: "198.51.100.4", uid: "victim"));

        Assert.True(victim.IsAllowed);
    }

    // ── Per-IP backstop on validate ─────────────────────────────────────────────

    [Fact]
    public async Task Validate_IsAlsoLimitedPerIpWhenTheUserBranchCannotApply()
    {
        var sut = BuildSut(("RateLimiting:PerIP:MfaBackupCodeValidate", "4"));

        // No claims at all: the per-user branch requires both a uid and a tenantId and silently
        // does nothing without them. The per-IP budget is the only thing left.
        for (var i = 0; i < 4; i++)
        {
            var allowed = await sut.CheckRateLimitAsync(
                BuildContext(ValidatePath, uid: null, tenant: null));
            Assert.True(allowed.IsAllowed, $"Request {i + 1} of 4 should have been allowed.");
        }

        var denied = await sut.CheckRateLimitAsync(
            BuildContext(ValidatePath, uid: null, tenant: null));

        Assert.False(denied.IsAllowed);
        Assert.Equal("rate_limit_exceeded_ip", denied.ErrorCode);
    }

    [Fact]
    public async Task Validate_AppliesTheUserBudgetEvenWhenTheIpBudgetIsFarFromSpent()
    {
        // The per-IP allow must NOT short-circuit the per-user check: a generous IP budget with a
        // tight user budget has to end in a user-level rejection, not an allow.
        var sut = BuildSut(
            ("RateLimiting:PerIP:MfaBackupCodeValidate", "1000"),
            ("RateLimiting:PerUser:MfaBackupCodeValidate", "2"));

        Assert.True((await sut.CheckRateLimitAsync(BuildContext(ValidatePath))).IsAllowed);
        Assert.True((await sut.CheckRateLimitAsync(BuildContext(ValidatePath))).IsAllowed);

        var denied = await sut.CheckRateLimitAsync(BuildContext(ValidatePath));

        Assert.False(denied.IsAllowed);
        Assert.Equal("rate_limit_exceeded_user", denied.ErrorCode);
    }

    [Fact]
    public async Task Validate_RejectsOnIpEvenWhenDifferentUsersShareIt()
    {
        // One host cycling through uids must still hit the IP wall.
        var sut = BuildSut(
            ("RateLimiting:PerIP:MfaBackupCodeValidate", "3"),
            ("RateLimiting:PerUser:MfaBackupCodeValidate", "1000"));

        for (var i = 0; i < 3; i++)
        {
            var allowed = await sut.CheckRateLimitAsync(
                BuildContext(ValidatePath, uid: $"uid-{i}"));
            Assert.True(allowed.IsAllowed);
        }

        var denied = await sut.CheckRateLimitAsync(BuildContext(ValidatePath, uid: "uid-99"));

        Assert.False(denied.IsAllowed);
        Assert.Equal("rate_limit_exceeded_ip", denied.ErrorCode);
    }

    [Fact]
    public async Task GenerateAndRemaining_AreNotIpLimited()
    {
        // Only validation carries the per-IP budget. Generation and the count read are ordinary
        // authenticated calls, and an office behind one NAT address must not throttle itself.
        var sut = BuildSut(("RateLimiting:PerUser:MfaBackupCodeGenerate", "1000"));

        for (var i = 0; i < 40; i++)
        {
            var result = await sut.CheckRateLimitAsync(
                BuildContext(GeneratePath, uid: $"uid-{i}"));

            Assert.True(result.IsAllowed);
        }
    }

    // ── Per-IP gate on redeem (the anonymous login-recovery route) ──────────────

    [Theory]
    [InlineData(RedeemPath)]
    [InlineData(BffRedeemPath)]
    public async Task Redeem_IsIpLimitedWithNoClaimsAtAll(string path)
    {
        // This is the load-bearing test for the endpoint. /redeem is [AllowAnonymous] by
        // construction — the caller is mid-sign-in and has no token — so the per-user branch can
        // never fire and per-IP is the ONLY limit standing between an attacker and unlimited
        // password + backup-code guesses. Before /redeem had its own branch it fell through to
        // "check nothing" and was completely unlimited.
        var sut = BuildSut(("RateLimiting:PerIP:MfaBackupCodeRedeem", "3"));

        for (var i = 0; i < 3; i++)
        {
            var allowed = await sut.CheckRateLimitAsync(
                BuildContext(path, uid: null, tenant: null));
            Assert.True(allowed.IsAllowed, $"Request {i + 1} of 3 should have been allowed.");
        }

        var denied = await sut.CheckRateLimitAsync(BuildContext(path, uid: null, tenant: null));

        Assert.False(denied.IsAllowed);
        Assert.Equal("rate_limit_exceeded_ip", denied.ErrorCode);
    }

    [Fact]
    public async Task Redeem_FallsBackToTheInCodeIpLimitWhenTheKeyIsMissing()
    {
        // No configuration at all. Without the in-code default GetValue<int> yields 0 and the
        // count > limit test rejects the very first request, locking every user out of the
        // recovery path they only reach after already losing their authenticator.
        var sut = BuildSut();

        for (var i = 0; i < 10; i++)
        {
            var allowed = await sut.CheckRateLimitAsync(
                BuildContext(RedeemPath, uid: null, tenant: null));
            Assert.True(allowed.IsAllowed, $"Request {i + 1} of 10 should have been allowed.");
        }

        var denied = await sut.CheckRateLimitAsync(
            BuildContext(RedeemPath, uid: null, tenant: null));

        Assert.False(denied.IsAllowed);
    }

    [Fact]
    public async Task Redeem_DoesNotShareItsBudgetWithValidate()
    {
        // Spending the redeem budget must not lock an authenticated user out of /validate, and
        // vice versa — they are different endpoints with different threat models.
        var sut = BuildSut(
            ("RateLimiting:PerIP:MfaBackupCodeRedeem", "1"),
            ("RateLimiting:PerIP:MfaBackupCodeValidate", "5"),
            ("RateLimiting:PerUser:MfaBackupCodeValidate", "5"));

        Assert.True((await sut.CheckRateLimitAsync(
            BuildContext(RedeemPath, uid: null, tenant: null))).IsAllowed);

        Assert.False((await sut.CheckRateLimitAsync(
            BuildContext(RedeemPath, uid: null, tenant: null))).IsAllowed);

        // /validate is untouched by the exhausted redeem budget.
        Assert.True((await sut.CheckRateLimitAsync(BuildContext(ValidatePath))).IsAllowed);
    }

    [Fact]
    public async Task Redeem_GetsTheValidateUserBudgetNotTheLooseRemainingOne()
    {
        // A caller can reach /redeem while still holding a session: the Internal handler reads
        // the bff_session cookie and populates User even on an [AllowAnonymous] action. Without
        // an explicit branch such a request would inherit the 30/min "remaining" budget purely
        // from its position in the if-chain — six times looser than the code-guessing endpoints.
        var sut = BuildSut(
            ("RateLimiting:PerIP:MfaBackupCodeRedeem", "1000"),
            ("RateLimiting:PerUser:MfaBackupCodeValidate", "2"));

        Assert.True((await sut.CheckRateLimitAsync(BuildContext(RedeemPath))).IsAllowed);
        Assert.True((await sut.CheckRateLimitAsync(BuildContext(RedeemPath))).IsAllowed);

        var denied = await sut.CheckRateLimitAsync(BuildContext(RedeemPath));

        Assert.False(denied.IsAllowed);
        Assert.Equal("rate_limit_exceeded_user", denied.ErrorCode);
    }

    // ── No regression on the paths that were already branched ───────────────────

    [Fact]
    public async Task ClientErrorLog_StillReturnsOnTheIpBudgetAlone()
    {
        // The per-IP block gained an "unless this path also has a user budget" condition. The
        // error-log endpoint has none, so its behaviour must be exactly what it was.
        var sut = BuildSut(("RateLimiting:PerIP:ClientErrorLog", "2"));

        Assert.True((await sut.CheckRateLimitAsync(BuildContext("/api/errorlog/client"))).IsAllowed);
        Assert.True((await sut.CheckRateLimitAsync(BuildContext("/api/errorlog/client"))).IsAllowed);

        var denied = await sut.CheckRateLimitAsync(BuildContext("/api/errorlog/client"));

        Assert.False(denied.IsAllowed);
        Assert.Equal("rate_limit_exceeded_ip", denied.ErrorCode);
    }
}
