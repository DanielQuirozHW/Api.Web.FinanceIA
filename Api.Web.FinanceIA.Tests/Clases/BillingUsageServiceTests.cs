// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-31

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Clases.Time;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// Covers <see cref="BillingUsageService"/>: the arithmetic of the snapshot, the ApiCallLogs →
/// ChatMessages fallback, and — the part that carries the whole fail-open policy — that a failure
/// reading one number degrades only that number.
///
/// <para>
/// Hand-rolled stubs per the repo's no-mock-library convention. Tenant isolation is deliberately
/// NOT tested here: these stubs return whatever they are handed, so only the repository tests can
/// prove a query honours its TenantId predicate.
/// </para>
/// </summary>
public sealed class BillingUsageServiceTests
{
    private const string Tenant = "tenant-1";

    // ── Stubs ───────────────────────────────────────────────────────────────────

    private sealed class StubBillingConfigService : ITenantBillingConfigService
    {
        public BillingPlanResolution Resolution { get; set; } =
            new(new BillingPlan("100 análisis", 100, 4_000_000L, 60_000, ChatModelCatalog.AllIds), BillingPlanSource.Configured);

        public Task<BillingPlanResolution> GetEffectiveAsync(string tenantId, CancellationToken ct)
            => Task.FromResult(Resolution);

        // BillingUsageService only reads. Present to satisfy the interface; a call here would mean
        // the usage path had started writing billing configuration, which it must never do.
        public Task<TenantBillingConfig> UpdateAsync(
            string tenantId, BillingPlan plan, bool isActive, string updatedBy, CancellationToken ct)
            => throw new NotSupportedException("BillingUsageService must not write billing configuration.");
    }

    private sealed class StubAnalysisRepository : IAnalysisRepository
    {
        public Task<BalanceBaseFiguresProjection?> GetBalanceBaseFiguresAsync(Guid analysisId, string tenantId, CancellationToken ct)
            => Task.FromResult<BalanceBaseFiguresProjection?>(null);

        public Task<IReadOnlyDictionary<Guid, EntityLatestScoreProjection>> GetLatestScoreByEntityIdsAsync(
            IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct) => throw new NotImplementedException();

        // The quorum-crossing write (DDL 067). Throws because no test in this suite confirms a
        // pre-approval, so an unexpected call must fail loudly rather than pass silently.
        public Task<bool> TryConfirmFromPreApprovedAsync(
            Guid analysisId, string tenantId, Guid reviewedBy, DateTime reviewedAt,
            decimal? decisionAmount, string? decisionCurrency, string? confirmedOperationsJson,
            string? reviewNotes, CancellationToken ct)
            => throw new NotImplementedException();
        public int CountToReturn { get; set; }
        public Exception? ThrowOnCount { get; set; }
        public int CallCount { get; private set; }

        /// <summary>The window the service last asked for, so the tests can assert it is the AR one.</summary>
        public (DateTime StartUtc, DateTime EndUtc)? LastWindow { get; private set; }

        public Task<int> CountByTenantInPeriodAsync(
            string tenantId, DateTime periodStartUtc, DateTime periodEndUtc, CancellationToken ct)
        {
            CallCount++;
            LastWindow = (periodStartUtc, periodEndUtc);
            if (ThrowOnCount is not null) return Task.FromException<int>(ThrowOnCount);
            return Task.FromResult(CountToReturn);
        }

        public Task<Analysis> CreateAsync(Analysis analysis, CancellationToken ct) => throw new NotImplementedException();
        public Task<Analysis?> GetByIdAsync(Guid analysisId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Analysis>> GetByIdsAsync(IEnumerable<Guid> analysisIds, CancellationToken ct) => throw new NotImplementedException();
        public Task<Analysis?> GetLatestByDocumentIdAsync(Guid documentId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<Analysis> UpdateAsync(Analysis analysis, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, string>> GetLatestStatusByEntityIdsAsync(IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class StubUsageRepository : IUsageRepository
    {
        public ChatTokenUsage LedgerUsage   { get; set; } = ChatTokenUsage.Empty;
        public ChatTokenUsage MessagesUsage { get; set; } = ChatTokenUsage.Empty;
        public Exception? ThrowOnLedger { get; set; }
        public int LedgerCallCount   { get; private set; }
        public int MessagesCallCount { get; private set; }

        /// <summary>The window the service last asked for, so the tests can assert it is the AR one.</summary>
        public (DateTime StartUtc, DateTime EndUtc)? LastWindow { get; private set; }

        public Task<ChatTokenUsage> GetChatTokensInPeriodAsync(
            string tenantId, DateTime periodStartUtc, DateTime periodEndUtc, CancellationToken ct)
        {
            LedgerCallCount++;
            LastWindow = (periodStartUtc, periodEndUtc);
            if (ThrowOnLedger is not null) return Task.FromException<ChatTokenUsage>(ThrowOnLedger);
            return Task.FromResult(LedgerUsage);
        }

        public Task<ChatTokenUsage> GetChatTokensFromMessagesInPeriodAsync(
            string tenantId, DateTime periodStartUtc, DateTime periodEndUtc, CancellationToken ct)
        {
            MessagesCallCount++;
            return Task.FromResult(MessagesUsage);
        }

        public Task<UsageAggregate> GetUsageInPeriodAsync(
            string tenantId, DateTime periodStartUtc, DateTime periodEndUtc, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private static BillingUsageService BuildSut(
        StubBillingConfigService? config = null,
        StubAnalysisRepository? analyses = null,
        StubUsageRepository? usage = null)
        => new(
            config   ?? new StubBillingConfigService(),
            analyses ?? new StubAnalysisRepository(),
            usage    ?? new StubUsageRepository(),
            NullLogger<BillingUsageService>.Instance);

    // ── Composition ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetSnapshotAsync_ComposesThePlanAndBothCounters()
    {
        var sut = BuildSut(
            analyses: new StubAnalysisRepository { CountToReturn = 42 },
            usage:    new StubUsageRepository { LedgerUsage = new ChatTokenUsage(1_000L, 500L, 7) });

        var snapshot = await sut.GetSnapshotAsync(Tenant, CancellationToken.None);

        Assert.Equal("100 análisis", snapshot.PlanName);
        Assert.Equal(42, snapshot.AnalysesUsed);
        Assert.Equal(100, snapshot.AnalysesLimit);
        Assert.Equal(1_500L, snapshot.ChatTokensUsed);
        Assert.Equal(4_000_000L, snapshot.ChatTokensLimit);
        Assert.False(snapshot.IsDegraded);
        Assert.Null(snapshot.DegradedReason);
    }

    /// <summary>
    /// The period is the ARGENTINE calendar month, not the UTC one. Between 21:00 and midnight ART
    /// on the last day of a month these two disagree, and the snapshot used to follow UTC — resetting
    /// a tenant's counter three hours early. Expressed against ArgentineTime rather than a literal
    /// so the assertion holds whenever CI happens to run.
    /// </summary>
    [Fact]
    public async Task GetSnapshotAsync_PeriodIsTheCurrentHalfOpenArgentineMonth()
    {
        var expected = ArgentineTime.CurrentPeriod(DateTime.UtcNow);

        var snapshot = await BuildSut().GetSnapshotAsync(Tenant, CancellationToken.None);

        Assert.Equal(expected.StartUtc, snapshot.PeriodStart);
        Assert.Equal(expected.EndUtc, snapshot.PeriodEnd);
        Assert.Equal(DateTimeKind.Utc, snapshot.PeriodStart.Kind);
        Assert.Equal(DateTimeKind.Utc, snapshot.PeriodEnd.Kind);

        // 00:00 ART on the 1st is 03:00 UTC on the 1st — never midnight UTC, which was the bug.
        Assert.Equal(1, snapshot.PeriodStart.Day);
        Assert.Equal(3, snapshot.PeriodStart.Hour);
    }

    /// <summary>
    /// Both counters must be metered over the SAME window as the one the snapshot reports. They
    /// used to be rebuilt independently inside each repository from a year and a month, so a
    /// divergence would not have shown up in either repository's own tests.
    /// </summary>
    [Fact]
    public async Task GetSnapshotAsync_MetersBothCountersOverThePeriodItReports()
    {
        var analyses = new StubAnalysisRepository();
        var usage = new StubUsageRepository();

        var snapshot = await BuildSut(analyses: analyses, usage: usage)
            .GetSnapshotAsync(Tenant, CancellationToken.None);

        Assert.NotNull(analyses.LastWindow);
        Assert.Equal(snapshot.PeriodStart, analyses.LastWindow!.Value.StartUtc);
        Assert.Equal(snapshot.PeriodEnd, analyses.LastWindow!.Value.EndUtc);

        Assert.NotNull(usage.LastWindow);
        Assert.Equal(snapshot.PeriodStart, usage.LastWindow!.Value.StartUtc);
        Assert.Equal(snapshot.PeriodEnd, usage.LastWindow!.Value.EndUtc);
    }

    [Fact]
    public async Task GetSnapshotAsync_WithABlankTenantId_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => BuildSut().GetSnapshotAsync("   ", CancellationToken.None));
    }

    // ── Derived arithmetic ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 100, 100)]
    [InlineData(40, 100, 60)]
    [InlineData(100, 100, 0)]
    [InlineData(140, 100, 0)]   // over the limit is out of quota, not owed a negative allowance
    public async Task GetSnapshotAsync_AnalysesRemaining_IsClampedAtZero(int used, int limit, int expected)
    {
        var config = new StubBillingConfigService
        {
            Resolution = new BillingPlanResolution(new BillingPlan("p", limit, 1_000L, 60_000, ChatModelCatalog.AllIds), BillingPlanSource.Configured)
        };
        var sut = BuildSut(config, new StubAnalysisRepository { CountToReturn = used });

        var snapshot = await sut.GetSnapshotAsync(Tenant, CancellationToken.None);

        Assert.Equal(expected, snapshot.AnalysesRemaining);
    }

    /// <summary>
    /// Percent is NOT clamped, unlike remaining. A tenant that got to 120% — fail-open let some
    /// requests through during an outage — deserves to see 120, not a comforting 100.
    /// </summary>
    [Theory]
    [InlineData(0, 100, 0)]
    [InlineData(40, 100, 40)]
    [InlineData(50, 100, 50)]
    [InlineData(100, 100, 100)]
    [InlineData(120, 100, 120)]
    public async Task GetSnapshotAsync_AnalysesPercent_IsUsedOverLimitAndIsNotCappedAtHundred(
        int used, int limit, int expectedPercent)
    {
        var config = new StubBillingConfigService
        {
            Resolution = new BillingPlanResolution(new BillingPlan("p", limit, 1_000L, 60_000, ChatModelCatalog.AllIds), BillingPlanSource.Configured)
        };
        var sut = BuildSut(config, new StubAnalysisRepository { CountToReturn = used });

        var snapshot = await sut.GetSnapshotAsync(Tenant, CancellationToken.None);

        Assert.Equal((decimal)expectedPercent, snapshot.AnalysesPercent);
    }

    /// <summary>
    /// A zero limit is representable — the column carries no CHECK — and must not divide by zero
    /// on the path that gates every analysis.
    /// </summary>
    [Fact]
    public async Task GetSnapshotAsync_WhenLimitIsZero_PercentIsZeroRatherThanThrowing()
    {
        var config = new StubBillingConfigService
        {
            Resolution = new BillingPlanResolution(new BillingPlan("p", 0, 0L, 60_000, ChatModelCatalog.AllIds), BillingPlanSource.Configured)
        };
        var sut = BuildSut(config, new StubAnalysisRepository { CountToReturn = 5 });

        var snapshot = await sut.GetSnapshotAsync(Tenant, CancellationToken.None);

        Assert.Equal(0m, snapshot.AnalysesPercent);
        Assert.Equal(0m, snapshot.ChatTokensPercent);
        Assert.Equal(0, snapshot.AnalysesRemaining);
    }

    [Fact]
    public async Task GetSnapshotAsync_Percent_IsRoundedToTwoDecimals()
    {
        var config = new StubBillingConfigService
        {
            Resolution = new BillingPlanResolution(new BillingPlan("p", 3, 1_000L, 60_000, ChatModelCatalog.AllIds), BillingPlanSource.Configured)
        };
        var sut = BuildSut(config, new StubAnalysisRepository { CountToReturn = 1 });

        var snapshot = await sut.GetSnapshotAsync(Tenant, CancellationToken.None);

        Assert.Equal(33.33m, snapshot.AnalysesPercent);
    }

    /// <summary>
    /// The context budget rides on the snapshot straight off the resolved plan, with no read and no
    /// arithmetic: it is a ceiling on ONE conversation's prompt, so there is no monthly numerator to
    /// divide it by and it gets no Remaining or Percent of its own.
    /// </summary>
    [Fact]
    public async Task GetSnapshotAsync_CarriesThePlansContextBudgetThrough()
    {
        var config = new StubBillingConfigService
        {
            Resolution = new BillingPlanResolution(
                new BillingPlan("Plan negociado", 137, 5_500_000L, 55_000, ChatModelCatalog.AllIds), BillingPlanSource.Configured)
        };

        var snapshot = await BuildSut(config).GetSnapshotAsync(Tenant, CancellationToken.None);

        Assert.Equal(55_000, snapshot.ChatContextBudgetTokens);
    }

    /// <summary>
    /// A tenant with no configuration gets the minimum plan's budget, like every other limit — the
    /// fallback is the whole plan, not a subset of it.
    /// </summary>
    [Fact]
    public async Task GetSnapshotAsync_WhenUnconfigured_ReportsTheMinimumPlansContextBudget()
    {
        var config = new StubBillingConfigService
        {
            Resolution = new BillingPlanResolution(BillingPlans.Minimum, BillingPlanSource.NoConfiguration)
        };

        var snapshot = await BuildSut(config).GetSnapshotAsync(Tenant, CancellationToken.None);

        Assert.Equal(BillingPlans.Minimum.ChatContextBudgetTokens, snapshot.ChatContextBudgetTokens);
    }

    // ── Chat token source selection ─────────────────────────────────────────────

    [Fact]
    public async Task GetSnapshotAsync_WhenTheLedgerHasRows_UsesItAndDoesNotReadChatMessages()
    {
        var usage = new StubUsageRepository
        {
            LedgerUsage   = new ChatTokenUsage(100L, 50L, 3),
            MessagesUsage = new ChatTokenUsage(999L, 999L, 9)
        };

        var snapshot = await BuildSut(usage: usage).GetSnapshotAsync(Tenant, CancellationToken.None);

        Assert.Equal(150L, snapshot.ChatTokensUsed);
        Assert.Equal(ChatTokenSource.ApiCallLogs, snapshot.ChatTokensSource);
        Assert.Equal(0, usage.MessagesCallCount);
    }

    /// <summary>
    /// The ApiCallLogs write is fire-and-forget with its failures swallowed, and script 043 — which
    /// repairs the column type that rejects every one of those writes — has not run. Zero ROWS is
    /// therefore "nothing was recorded", and the transcript is the better witness.
    /// </summary>
    [Fact]
    public async Task GetSnapshotAsync_WhenTheLedgerHasNoRows_FallsBackToChatMessages()
    {
        var usage = new StubUsageRepository
        {
            LedgerUsage   = ChatTokenUsage.Empty,
            MessagesUsage = new ChatTokenUsage(400L, 100L, 4)
        };

        var snapshot = await BuildSut(usage: usage).GetSnapshotAsync(Tenant, CancellationToken.None);

        Assert.Equal(500L, snapshot.ChatTokensUsed);
        Assert.Equal(ChatTokenSource.ChatMessages, snapshot.ChatTokensSource);
        Assert.Equal(1, usage.MessagesCallCount);
    }

    /// <summary>
    /// The switch is on the row count, never the token total. A tenant with real ledger rows that
    /// happen to sum to zero genuinely did not spend tokens, and that answer must not be
    /// second-guessed by reading another table.
    /// </summary>
    [Fact]
    public async Task GetSnapshotAsync_WhenTheLedgerHasRowsSummingToZero_DoesNotFallBack()
    {
        var usage = new StubUsageRepository
        {
            LedgerUsage   = new ChatTokenUsage(0L, 0L, 2),
            MessagesUsage = new ChatTokenUsage(999L, 999L, 9)
        };

        var snapshot = await BuildSut(usage: usage).GetSnapshotAsync(Tenant, CancellationToken.None);

        Assert.Equal(0L, snapshot.ChatTokensUsed);
        Assert.Equal(ChatTokenSource.ApiCallLogs, snapshot.ChatTokensSource);
        Assert.Equal(0, usage.MessagesCallCount);
    }

    [Fact]
    public async Task GetSnapshotAsync_WhenNeitherSourceHasRows_ReportsZeroAgainstTheLedger()
    {
        var snapshot = await BuildSut().GetSnapshotAsync(Tenant, CancellationToken.None);

        Assert.Equal(0L, snapshot.ChatTokensUsed);
        Assert.Equal(ChatTokenSource.ApiCallLogs, snapshot.ChatTokensSource);
        Assert.False(snapshot.ChatTokensReadFailed);
    }

    // ── Fault isolation — the fail-open policy ──────────────────────────────────

    [Fact]
    public async Task GetSnapshotAsync_WhenThePlanReadFailed_MarksBothQuotasUnreliable()
    {
        var config = new StubBillingConfigService
        {
            Resolution = new BillingPlanResolution(BillingPlans.Minimum, BillingPlanSource.ReadFailed)
        };

        var snapshot = await BuildSut(config).GetSnapshotAsync(Tenant, CancellationToken.None);

        Assert.True(snapshot.AnalysisQuotaIsUnreliable);
        Assert.True(snapshot.ChatQuotaIsUnreliable);
        Assert.Equal("plan_read_failed", snapshot.DegradedReason);
    }

    /// <summary>
    /// The isolation that matters most. Until script 043 runs, ApiCallLogs.TenantId is a
    /// UNIQUEIDENTIFIER receiving Firebase slugs, so the chat read can fail on every request — and
    /// one shared catch would have quietly disabled the analysis quota for every tenant.
    /// </summary>
    [Fact]
    public async Task GetSnapshotAsync_WhenTheChatReadThrows_OnlyTheChatQuotaBecomesUnreliable()
    {
        var usage = new StubUsageRepository
        {
            ThrowOnLedger = new InvalidOperationException(
                "Conversion failed when converting from a character string to uniqueidentifier.")
        };
        var sut = BuildSut(analyses: new StubAnalysisRepository { CountToReturn = 7 }, usage: usage);

        var snapshot = await sut.GetSnapshotAsync(Tenant, CancellationToken.None);

        Assert.True(snapshot.ChatQuotaIsUnreliable);
        Assert.False(snapshot.AnalysisQuotaIsUnreliable);   // the analysis gate keeps working
        Assert.Equal(7, snapshot.AnalysesUsed);
        Assert.Equal("chat_tokens_read_failed", snapshot.DegradedReason);
    }

    [Fact]
    public async Task GetSnapshotAsync_WhenTheAnalysisCountThrows_OnlyTheAnalysisQuotaBecomesUnreliable()
    {
        var analyses = new StubAnalysisRepository { ThrowOnCount = new TimeoutException("transient") };
        var usage    = new StubUsageRepository { LedgerUsage = new ChatTokenUsage(10L, 5L, 1) };

        var snapshot = await BuildSut(analyses: analyses, usage: usage).GetSnapshotAsync(Tenant, CancellationToken.None);

        Assert.True(snapshot.AnalysisQuotaIsUnreliable);
        Assert.False(snapshot.ChatQuotaIsUnreliable);
        Assert.Equal(15L, snapshot.ChatTokensUsed);
        Assert.Equal("analyses_read_failed", snapshot.DegradedReason);
    }

    [Fact]
    public async Task GetSnapshotAsync_WhenBothCountsThrow_ReportsBothAndStillDoesNotPropagate()
    {
        var analyses = new StubAnalysisRepository { ThrowOnCount = new TimeoutException("transient") };
        var usage    = new StubUsageRepository { ThrowOnLedger = new TimeoutException("transient") };

        var snapshot = await BuildSut(analyses: analyses, usage: usage).GetSnapshotAsync(Tenant, CancellationToken.None);

        Assert.True(snapshot.AnalysesReadFailed);
        Assert.True(snapshot.ChatTokensReadFailed);
        Assert.Equal("analyses_and_chat_tokens_read_failed", snapshot.DegradedReason);
        Assert.Equal(0, snapshot.AnalysesUsed);
        Assert.Equal(0L, snapshot.ChatTokensUsed);
    }

    // ── No memoization ──────────────────────────────────────────────────────────

    /// <summary>
    /// Deliberately NOT cached, unlike the plan. A count changes within a request — the gate reads
    /// before the analysis is written — so memoizing it would make a second read return a stale
    /// number. This test is what should fail if someone "optimises" a dictionary in here.
    /// </summary>
    [Fact]
    public async Task GetSnapshotAsync_CalledTwice_RecountsBothSources()
    {
        var analyses = new StubAnalysisRepository { CountToReturn = 1 };
        var usage    = new StubUsageRepository { LedgerUsage = new ChatTokenUsage(1L, 1L, 1) };
        var sut      = BuildSut(analyses: analyses, usage: usage);

        _ = await sut.GetSnapshotAsync(Tenant, CancellationToken.None);
        _ = await sut.GetSnapshotAsync(Tenant, CancellationToken.None);

        Assert.Equal(2, analyses.CallCount);
        Assert.Equal(2, usage.LedgerCallCount);
    }
}
