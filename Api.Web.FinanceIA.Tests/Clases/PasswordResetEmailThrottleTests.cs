// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-19

using Api.Web.FinanceIA.Clases.PasswordReset;
using Api.Web.FinanceIA.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// The per-(tenant, email) budget of POST api/bff/auth/password-reset, and the tag that keys it.
/// The window is fixed: it opens on the first request and resets when it elapses, driven here by a
/// manual clock so the test does not wait fifteen minutes.
/// </summary>
public sealed class PasswordResetEmailThrottleTests
{
    [Fact]
    public void AllowsTheLimit_ThenRefusesWithARetryAfterInsideTheWindow()
    {
        var sut = NewSut(new ManualClock());

        for (var i = 0; i < 3; i++)
        {
            Assert.True(sut.Register("TenantA", "a@example.com").IsAllowed);
        }

        PasswordResetThrottleDecision refused = sut.Register("TenantA", "a@example.com");
        Assert.False(refused.IsAllowed);
        Assert.InRange(refused.RetryAfterSeconds, 1, 15 * 60);
    }

    [Fact]
    public void KeysAreTenantScoped_AndCaseInsensitiveOnTheAddress()
    {
        var sut = NewSut(new ManualClock());

        for (var i = 0; i < 3; i++)
        {
            sut.Register("TenantA", "a@example.com");
        }

        Assert.False(sut.Register("TenantA", " A@Example.com ").IsAllowed);
        Assert.True(sut.Register("TenantB", "a@example.com").IsAllowed);
        Assert.True(sut.Register("TenantA", "b@example.com").IsAllowed);
    }

    [Fact]
    public void WindowElapses_AndTheBudgetResets()
    {
        var clock = new ManualClock();
        var sut = NewSut(clock);

        for (var i = 0; i < 4; i++)
        {
            sut.Register("TenantA", "a@example.com");
        }

        clock.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));

        Assert.True(sut.Register("TenantA", "a@example.com").IsAllowed);
    }

    [Fact]
    public void EmailHasher_IsStable_CaseInsensitive_AndDoesNotContainTheAddress()
    {
        string tag = EmailHasher.Hash("Analyst@Example.com ");

        Assert.Equal(tag, EmailHasher.Hash("analyst@example.com"));
        Assert.NotEqual(tag, EmailHasher.Hash("other@example.com"));
        Assert.Matches("^[0-9a-f]{16}$", tag);
        Assert.DoesNotContain("example", tag);
    }

    private static PasswordResetEmailThrottle NewSut(ManualClock clock) => new(
        new MemoryCache(new MemoryCacheOptions()),
        Options.Create(new PasswordResetOptions { PerEmailLimit = 3, PerEmailWindowMinutes = 15 }),
        clock);

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
