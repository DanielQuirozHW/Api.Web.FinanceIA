// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-31

using Api.Web.FinanceIA.Clases.Time;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// Query-level tests for the two chat-token readers on <see cref="UsageRepository"/>, the
/// numerator of the chat billing quota. In-memory provider, so the real WHERE clauses run.
///
/// <para>
/// The load-bearing test in this file is
/// <see cref="GetChatTokensInPeriodAsync_ExcludesNonChatServices"/>. The pre-existing
/// <c>GetUsageInPeriodAsync</c> filters on <c>Operation != null</c> and sums EVERY service, so
/// reusing it for a chat quota would charge an analyst's chat allowance for document extraction
/// and the scoring narrative. A regression test at the bottom pins that the old method still
/// behaves the old way.
/// </para>
///
/// <para>
/// All three readers now receive their window instead of building it from a year and a month, so
/// the analysis count and the token counts cannot drift onto different periods.
/// <see cref="UtcMonth"/> preserves the UTC-calendar boundaries these cases were written against;
/// <see cref="ArMonth"/> covers the ARGENTINE month a caller actually passes in production.
/// </para>
///
/// <para>
/// TENANT ISOLATION IS PROVEN HERE, not in the service tests — there is no EF global query
/// filter, so these predicates are the only thing separating two tenants' consumption.
/// </para>
/// </summary>
public sealed class UsageRepositoryChatTokenTests
{
    private const string Tenant = "tenant-1";
    private const string Other  = "tenant-2";

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static FinanciaDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"chat-tokens-{Guid.NewGuid()}")
            .Options;
        return new FinanciaDbContext(options);
    }

    private static UsageRepository BuildSut(FinanciaDbContext ctx)
        => new(ctx, NullLogger<UsageRepository>.Instance);

    private static ApiCallLog Call(
        DateTime calledAt,
        string tenantId = Tenant,
        string service = "ClaudeChat",
        string? operation = "Chat",
        int? inputTokens = 100,
        int? outputTokens = 50) => new()
    {
        TenantId     = tenantId,
        Service      = service,
        Endpoint     = "/v1/messages",
        Operation    = operation,
        InputTokens  = inputTokens,
        OutputTokens = outputTokens,
        IsSuccess    = true,
        CalledAt     = calledAt
    };

    private static ChatMessage Turn(
        DateTime createdAt,
        string tenantId = Tenant,
        string role = "assistant",
        int? inputTokens = 100,
        int? outputTokens = 50) => new()
    {
        ConversationId = Guid.NewGuid(),
        TenantId       = tenantId,
        Role           = role,
        Content        = "…",
        InputTokens    = inputTokens,
        OutputTokens   = outputTokens,
        CreatedAt      = createdAt
    };

    private static async Task SeedAsync(FinanciaDbContext ctx, params object[] rows)
    {
        ctx.AddRange(rows);
        ctx.ChangeTracker.DetectChanges();   // AutoDetectChanges is disabled on the context
        await ctx.SaveChangesAsync();
    }

    private static DateTime Utc(int year, int month, int day, int hour = 12)
        => new(year, month, day, hour, 0, 0, DateTimeKind.Utc);

    /// <summary>A half-open UTC calendar month — the window shape these tests were written against.</summary>
    private static (DateTime Start, DateTime End) UtcMonth(int year, int month)
    {
        var start = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        return (start, start.AddMonths(1));
    }

    /// <summary>A half-open ARGENTINE calendar month, as BillingUsageService supplies it.</summary>
    private static (DateTime Start, DateTime End) ArMonth(int year, int month)
    {
        var period = ArgentineTime.PeriodFor(year, month);
        return (period.StartUtc, period.EndUtc);
    }

    private static Task<ChatTokenUsage> ChatTokensInUtcMonth(
        FinanciaDbContext ctx, string tenantId, int year, int month)
    {
        var (start, end) = UtcMonth(year, month);
        return BuildSut(ctx).GetChatTokensInPeriodAsync(tenantId, start, end, CancellationToken.None);
    }

    private static Task<ChatTokenUsage> ChatTokensInArMonth(
        FinanciaDbContext ctx, string tenantId, int year, int month)
    {
        var (start, end) = ArMonth(year, month);
        return BuildSut(ctx).GetChatTokensInPeriodAsync(tenantId, start, end, CancellationToken.None);
    }

    private static Task<ChatTokenUsage> MessageTokensInUtcMonth(
        FinanciaDbContext ctx, string tenantId, int year, int month)
    {
        var (start, end) = UtcMonth(year, month);
        return BuildSut(ctx).GetChatTokensFromMessagesInPeriodAsync(tenantId, start, end, CancellationToken.None);
    }

    private static Task<ChatTokenUsage> MessageTokensInArMonth(
        FinanciaDbContext ctx, string tenantId, int year, int month)
    {
        var (start, end) = ArMonth(year, month);
        return BuildSut(ctx).GetChatTokensFromMessagesInPeriodAsync(tenantId, start, end, CancellationToken.None);
    }

    private static Task<UsageAggregate> UsageInUtcMonth(
        FinanciaDbContext ctx, string tenantId, int year, int month)
    {
        var (start, end) = UtcMonth(year, month);
        return BuildSut(ctx).GetUsageInPeriodAsync(tenantId, start, end, CancellationToken.None);
    }

    private static Task<UsageAggregate> UsageInArMonth(
        FinanciaDbContext ctx, string tenantId, int year, int month)
    {
        var (start, end) = ArMonth(year, month);
        return BuildSut(ctx).GetUsageInPeriodAsync(tenantId, start, end, CancellationToken.None);
    }

    // ── ApiCallLogs reader ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetChatTokensInPeriodAsync_SumsInputAndOutputTokens()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx,
            Call(Utc(2026, 8, 5), inputTokens: 100, outputTokens: 50),
            Call(Utc(2026, 8, 6), inputTokens: 200, outputTokens: 25));

        var usage = await ChatTokensInUtcMonth(ctx, Tenant, 2026, 8);

        Assert.Equal(300L, usage.InputTokens);
        Assert.Equal(75L, usage.OutputTokens);
        Assert.Equal(375L, usage.TotalTokens);
        Assert.Equal(2, usage.Calls);
    }

    /// <summary>
    /// The reason this method exists at all. An extraction call carries a huge token count and
    /// the same non-null Operation the old aggregate keys on; only the Service discriminator
    /// separates it from chat.
    /// </summary>
    [Fact]
    public async Task GetChatTokensInPeriodAsync_ExcludesNonChatServices()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx,
            Call(Utc(2026, 8, 5), service: "ClaudeChat", operation: "Chat", inputTokens: 100, outputTokens: 50),
            Call(Utc(2026, 8, 5), service: "Claude", operation: "Extraction", inputTokens: 900_000, outputTokens: 90_000),
            Call(Utc(2026, 8, 5), service: "Claude", operation: "Narrative", inputTokens: 5_000, outputTokens: 1_000),
            Call(Utc(2026, 8, 5), service: "NOSIS", operation: null, inputTokens: null, outputTokens: null));

        var usage = await ChatTokensInUtcMonth(ctx, Tenant, 2026, 8);

        Assert.Equal(150L, usage.TotalTokens);
        Assert.Equal(1, usage.Calls);
    }

    [Fact]
    public async Task GetChatTokensInPeriodAsync_ExcludesOtherTenants()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx,
            Call(Utc(2026, 8, 5)),
            Call(Utc(2026, 8, 5), tenantId: Other, inputTokens: 9_999, outputTokens: 9_999));

        var usage = await ChatTokensInUtcMonth(ctx, Tenant, 2026, 8);

        Assert.Equal(150L, usage.TotalTokens);
        Assert.Equal(1, usage.Calls);
    }

    /// <summary>
    /// A failed chat call logs a ClaudeChat row with null token counts. It contributes nothing to
    /// the tokens but must still count as a call, because Calls is what tells the service whether
    /// the ledger has anything in it at all.
    /// </summary>
    [Fact]
    public async Task GetChatTokensInPeriodAsync_TreatsNullTokensAsZeroButStillCountsTheCall()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx,
            Call(Utc(2026, 8, 5), inputTokens: null, outputTokens: null),
            Call(Utc(2026, 8, 6), inputTokens: 10, outputTokens: 5));

        var usage = await ChatTokensInUtcMonth(ctx, Tenant, 2026, 8);

        Assert.Equal(15L, usage.TotalTokens);
        Assert.Equal(2, usage.Calls);
    }

    [Fact]
    public async Task GetChatTokensInPeriodAsync_RespectsTheHalfOpenMonthWindow()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx,
            Call(new DateTime(2026, 7, 31, 23, 59, 59, 999, DateTimeKind.Utc), inputTokens: 1, outputTokens: 0),
            Call(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), inputTokens: 10, outputTokens: 0),
            Call(new DateTime(2026, 8, 31, 23, 59, 59, 999, DateTimeKind.Utc), inputTokens: 100, outputTokens: 0),
            Call(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), inputTokens: 1_000, outputTokens: 0));

        var usage = await ChatTokensInUtcMonth(ctx, Tenant, 2026, 8);

        Assert.Equal(110L, usage.TotalTokens);
        Assert.Equal(2, usage.Calls);
    }

    /// <summary>
    /// Zero rows, not zero tokens. This is the state that makes BillingUsageService fall back to
    /// ChatMessages, so the distinction has to survive the repository.
    /// </summary>
    [Fact]
    public async Task GetChatTokensInPeriodAsync_WhenNoRows_ReturnsZeroCalls()
    {
        using var ctx = NewContext();

        var usage = await ChatTokensInUtcMonth(ctx, Tenant, 2026, 8);

        Assert.Equal(0, usage.Calls);
        Assert.Equal(0L, usage.TotalTokens);
    }

    // ── ChatMessages fallback reader ────────────────────────────────────────────

    [Fact]
    public async Task GetChatTokensFromMessagesInPeriodAsync_SumsAssistantTurns()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx,
            Turn(Utc(2026, 8, 5), inputTokens: 100, outputTokens: 50),
            Turn(Utc(2026, 8, 6), inputTokens: 200, outputTokens: 25));

        var usage = await MessageTokensInUtcMonth(ctx, Tenant, 2026, 8);

        Assert.Equal(375L, usage.TotalTokens);
        Assert.Equal(2, usage.Calls);
    }

    /// <summary>
    /// A user turn is persisted with both token columns null, so the token predicate selects the
    /// billable half without depending on the Role string.
    /// </summary>
    [Fact]
    public async Task GetChatTokensFromMessagesInPeriodAsync_IgnoresUserTurnsWhichCarryNoTokens()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx,
            Turn(Utc(2026, 8, 5), role: "user", inputTokens: null, outputTokens: null),
            Turn(Utc(2026, 8, 5), role: "assistant", inputTokens: 100, outputTokens: 50));

        var usage = await MessageTokensInUtcMonth(ctx, Tenant, 2026, 8);

        Assert.Equal(150L, usage.TotalTokens);
        Assert.Equal(1, usage.Calls);
    }

    [Fact]
    public async Task GetChatTokensFromMessagesInPeriodAsync_ExcludesOtherTenants()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx,
            Turn(Utc(2026, 8, 5)),
            Turn(Utc(2026, 8, 5), tenantId: Other, inputTokens: 9_999, outputTokens: 9_999));

        var usage = await MessageTokensInUtcMonth(ctx, Tenant, 2026, 8);

        Assert.Equal(150L, usage.TotalTokens);
        Assert.Equal(1, usage.Calls);
    }

    [Fact]
    public async Task GetChatTokensFromMessagesInPeriodAsync_RespectsTheHalfOpenMonthWindow()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx,
            Turn(new DateTime(2026, 7, 31, 23, 59, 59, 999, DateTimeKind.Utc), inputTokens: 1, outputTokens: 0),
            Turn(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), inputTokens: 10, outputTokens: 0),
            Turn(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), inputTokens: 1_000, outputTokens: 0));

        var usage = await MessageTokensInUtcMonth(ctx, Tenant, 2026, 8);

        Assert.Equal(10L, usage.TotalTokens);
        Assert.Equal(1, usage.Calls);
    }

    // ── Regression on the pre-existing aggregate ────────────────────────────────

    /// <summary>
    /// The old method was NOT narrowed. It still sums every service with a non-null Operation —
    /// which is why /api/usage/monthly reports a much larger token figure than the billing
    /// endpoint, and why that difference is correct rather than a bug.
    /// </summary>
    [Fact]
    public async Task GetUsageInPeriodAsync_StillAggregatesEveryService()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx,
            Call(Utc(2026, 8, 5), service: "ClaudeChat", operation: "Chat", inputTokens: 100, outputTokens: 50),
            Call(Utc(2026, 8, 5), service: "Claude", operation: "Extraction", inputTokens: 900, outputTokens: 90));

        var aggregate = await UsageInUtcMonth(ctx, Tenant, 2026, 8);

        Assert.Equal(1_000L, aggregate.TotalInputTokens);
        Assert.Equal(140L, aggregate.TotalOutputTokens);
        Assert.Equal(2, aggregate.TotalCalls);
    }

    // ── Argentine month boundaries — the window the caller actually passes ──────

    /// <summary>
    /// The three hours the billing period used to get wrong. A chat turn at 22:00 ART on 31 August
    /// is 01:00 UTC on 1 September; a UTC-calendar window charged it to September, resetting the
    /// analyst's allowance three hours before Argentine midnight.
    /// </summary>
    [Fact]
    public async Task GetChatTokensInPeriodAsync_ForACallInTheLastHoursOfAnArgentineMonth_CountsItInThatMonth()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx, Call(Utc(2026, 9, 1, 1), inputTokens: 100, outputTokens: 50));

        Assert.Equal(150L, (await ChatTokensInArMonth(ctx, Tenant, 2026, 8)).TotalTokens);
        Assert.Equal(0L, (await ChatTokensInArMonth(ctx, Tenant, 2026, 9)).TotalTokens);
    }

    /// <summary>The same edge for the ChatMessages fallback, which meters the same quota.</summary>
    [Fact]
    public async Task GetChatTokensFromMessagesInPeriodAsync_ForATurnInTheLastHoursOfAnArgentineMonth_CountsItInThatMonth()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx, Turn(Utc(2026, 9, 1, 1), inputTokens: 100, outputTokens: 50));

        Assert.Equal(150L, (await MessageTokensInArMonth(ctx, Tenant, 2026, 8)).TotalTokens);
        Assert.Equal(0L, (await MessageTokensInArMonth(ctx, Tenant, 2026, 9)).TotalTokens);
    }

    /// <summary>
    /// Consecutive Argentine months tile: every call across the boundary is counted exactly once.
    /// </summary>
    [Fact]
    public async Task GetChatTokensInPeriodAsync_AcrossAnArgentineBoundary_CountsEveryCallExactlyOnce()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx,
            Call(Utc(2026, 9, 1, 2), inputTokens: 1, outputTokens: 0),       // 23:00 ART, 31 Aug
            Call(Utc(2026, 9, 1, 3), inputTokens: 10, outputTokens: 0),      // 00:00 ART, 1 Sep
            Call(Utc(2026, 9, 1, 4), inputTokens: 100, outputTokens: 0));    // 01:00 ART, 1 Sep

        Assert.Equal(1L, (await ChatTokensInArMonth(ctx, Tenant, 2026, 8)).TotalTokens);
        Assert.Equal(110L, (await ChatTokensInArMonth(ctx, Tenant, 2026, 9)).TotalTokens);
    }

    // ── ByDay buckets are Argentine calendar days ───────────────────────────────

    /// <summary>
    /// The per-day breakdown groups on the ARGENTINE date, matching the window that selected the
    /// rows. Grouping on the raw UTC date would file everything a tenant did between 21:00 and
    /// midnight ART under the following day — and, at a month edge, under a day outside the very
    /// period being reported.
    /// </summary>
    [Fact]
    public async Task GetUsageInPeriodAsync_BucketsByDay_UsingTheArgentineDate()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx,
            Call(Utc(2026, 9, 16, 2), inputTokens: 1, outputTokens: 0),    // 23:00 ART, 15 Sep
            Call(Utc(2026, 9, 16, 4), inputTokens: 10, outputTokens: 0));  // 01:00 ART, 16 Sep

        var aggregate = await UsageInArMonth(ctx, Tenant, 2026, 9);

        Assert.Equal(2, aggregate.ByDay.Count);
        Assert.Equal(new DateTime(2026, 9, 15), aggregate.ByDay[0].Day);
        Assert.Equal(1L, aggregate.ByDay[0].InputTokens);
        Assert.Equal(new DateTime(2026, 9, 16), aggregate.ByDay[1].Day);
        Assert.Equal(10L, aggregate.ByDay[1].InputTokens);
    }

    /// <summary>
    /// The first Argentine day of a period is bucketed inside that period, not into the previous
    /// month. Under UTC-day grouping this row would have reported as 1 September while sitting in
    /// the September window — a date the month it belongs to does not contain.
    /// </summary>
    [Fact]
    public async Task GetUsageInPeriodAsync_ByDay_KeepsTheFirstArgentineDayInsideThePeriod()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx, Call(Utc(2026, 9, 1, 4), inputTokens: 5, outputTokens: 0));   // 01:00 ART, 1 Sep

        var aggregate = await UsageInArMonth(ctx, Tenant, 2026, 9);

        Assert.Equal(new DateTime(2026, 9, 1), Assert.Single(aggregate.ByDay).Day);
    }
}
