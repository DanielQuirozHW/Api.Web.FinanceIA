// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-21

using Api.Web.FinanceIA.Clases.EmailVerification;
using Api.Web.FinanceIA.Clases.PasswordReset;
using Api.Web.FinanceIA.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// The per-(tenant, user) budget of POST api/bff/auth/email-verification: 3 per 15 minutes by
/// default, a fixed window driven here by a manual clock.
/// </summary>
public sealed class EmailVerificationThrottleTests
{
    private static readonly Guid UserA = Guid.NewGuid();
    private static readonly Guid UserB = Guid.NewGuid();

    [Fact]
    public void AllowsTheLimit_ThenRefusesWithARetryAfterInsideTheWindow()
    {
        var sut = NewSut(new ManualClock());

        for (var i = 0; i < 3; i++)
        {
            Assert.True(sut.Register("TenantA", UserA).IsAllowed);
        }

        PasswordResetThrottleDecision refused = sut.Register("TenantA", UserA);
        Assert.False(refused.IsAllowed);
        Assert.InRange(refused.RetryAfterSeconds, 1, 15 * 60);
    }

    [Fact]
    public void KeysAreTenantAndUserScoped()
    {
        var sut = NewSut(new ManualClock());

        for (var i = 0; i < 3; i++)
        {
            sut.Register("TenantA", UserA);
        }

        Assert.False(sut.Register("TenantA", UserA).IsAllowed);
        Assert.True(sut.Register("TenantB", UserA).IsAllowed);
        Assert.True(sut.Register("TenantA", UserB).IsAllowed);
    }

    [Fact]
    public void WindowElapses_AndTheBudgetResets()
    {
        var clock = new ManualClock();
        var sut = NewSut(clock);

        for (var i = 0; i < 4; i++)
        {
            sut.Register("TenantA", UserA);
        }

        clock.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));

        Assert.True(sut.Register("TenantA", UserA).IsAllowed);
    }

    private static EmailVerificationThrottle NewSut(ManualClock clock) => new(
        new MemoryCache(new MemoryCacheOptions()),
        Options.Create(new EmailVerificationOptions { PerUserLimit = 3, PerUserWindowMinutes = 15 }),
        clock);

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
