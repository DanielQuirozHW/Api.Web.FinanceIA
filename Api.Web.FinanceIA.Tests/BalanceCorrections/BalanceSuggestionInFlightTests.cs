// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-09

using Api.Web.FinanceIA.Servicios.BalanceCorrections;
using Xunit;

namespace Api.Web.FinanceIA.Tests.BalanceCorrections;

/// <summary>
/// <see cref="BalanceSuggestionInFlight"/> on its own: one run per key, joiners share its outcome, a run's
/// failure reaches only the callers already waiting, and the shared token is cancelled only when every caller left.
/// </summary>
public sealed class BalanceSuggestionInFlightTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly BalanceSuggestionInFlight _inFlight = new();

    [Fact]
    public async Task A_joiner_shares_the_owners_run()
    {
        var work = new HeldWork();

        var owner  = _inFlight.RunAsync("k", work.RunAsync, CancellationToken.None);
        await work.Started.Task.WaitAsync(Timeout);
        var joiner = _inFlight.RunAsync("k", _ => throw new InvalidOperationException("must not run"), CancellationToken.None);
        Assert.Equal(2, _inFlight.ParticipantsOf("k"));

        work.Release.SetResult(BalanceCorrectionSuggestionOutcome.Refused(502, "x"));

        var (ownerOutcome, ownerJoined)   = await owner;
        var (joinerOutcome, joinerJoined) = await joiner;
        Assert.False(ownerJoined);
        Assert.True(joinerJoined);
        Assert.Same(ownerOutcome, joinerOutcome);
        Assert.Equal(1, work.Runs);
        Assert.Equal(0, _inFlight.Count);
    }

    [Fact]
    public async Task Different_keys_never_wait_on_each_other()
    {
        var a = new HeldWork();
        var b = new HeldWork();

        var first  = _inFlight.RunAsync("a", a.RunAsync, CancellationToken.None);
        var second = _inFlight.RunAsync("b", b.RunAsync, CancellationToken.None);
        await Task.WhenAll(a.Started.Task, b.Started.Task).WaitAsync(Timeout);

        Assert.Equal(2, _inFlight.Count);
        a.Release.SetResult(BalanceCorrectionSuggestionOutcome.NotFound());
        b.Release.SetResult(BalanceCorrectionSuggestionOutcome.NotFound());
        await Task.WhenAll(first, second);
        Assert.Equal(0, _inFlight.Count);
    }

    [Fact]
    public async Task A_finished_run_is_never_served_to_a_later_caller()
    {
        var failing = new HeldWork();
        var owner   = _inFlight.RunAsync("k", failing.RunAsync, CancellationToken.None);
        await failing.Started.Task.WaitAsync(Timeout);
        failing.Release.SetResult(BalanceCorrectionSuggestionOutcome.Refused(502, "suggestion_unavailable"));
        Assert.Equal(502, (await owner).Outcome.StatusCode);

        var (later, joined) = await _inFlight.RunAsync(
            "k", _ => Task.FromResult(BalanceCorrectionSuggestionOutcome.NotFound()), CancellationToken.None);

        Assert.False(joined);
        Assert.Equal(404, later.StatusCode);
    }

    [Fact]
    public async Task A_thrown_failure_reaches_every_waiting_caller_and_frees_the_key()
    {
        var work   = new HeldWork();
        var owner  = _inFlight.RunAsync("k", work.RunAsync, CancellationToken.None);
        await work.Started.Task.WaitAsync(Timeout);
        var joiner = _inFlight.RunAsync("k", work.RunAsync, CancellationToken.None);

        work.Release.SetException(new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => owner);
        await Assert.ThrowsAsync<InvalidOperationException>(() => joiner);
        Assert.Equal(0, _inFlight.Count);
    }

    [Fact]
    public async Task The_owner_leaving_keeps_the_run_alive_for_a_joiner_and_then_throws()
    {
        var work = new HeldWork();
        using var ownerCts = new CancellationTokenSource();

        var owner  = _inFlight.RunAsync("k", work.RunAsync, ownerCts.Token);
        await work.Started.Task.WaitAsync(Timeout);
        var joiner = _inFlight.RunAsync("k", work.RunAsync, CancellationToken.None);

        ownerCts.Cancel();
        Assert.Equal(1, _inFlight.ParticipantsOf("k"));
        Assert.False(work.Token.IsCancellationRequested);
        Assert.False(owner.IsCompleted);   // it waits for the run: its scoped services must outlive it

        work.Release.SetResult(BalanceCorrectionSuggestionOutcome.NotFound());

        Assert.Equal(404, (await joiner).Outcome.StatusCode);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner);
        Assert.Equal(0, _inFlight.Count);
    }

    [Fact]
    public async Task The_shared_token_is_cancelled_when_the_last_caller_leaves()
    {
        var work = new HeldWork();
        using var ownerCts  = new CancellationTokenSource();
        using var joinerCts = new CancellationTokenSource();

        var owner  = _inFlight.RunAsync("k", work.RunAsync, ownerCts.Token);
        await work.Started.Task.WaitAsync(Timeout);
        var joiner = _inFlight.RunAsync("k", work.RunAsync, joinerCts.Token);

        joinerCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => joiner);
        Assert.False(work.Token.IsCancellationRequested);

        ownerCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner);
        Assert.True(work.Token.IsCancellationRequested);
        Assert.Equal(0, _inFlight.Count);
    }

    [Fact]
    public async Task An_already_cancelled_caller_starts_nothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var runs = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _inFlight.RunAsync(
            "k", _ => { runs++; return Task.FromResult(BalanceCorrectionSuggestionOutcome.NotFound()); }, cts.Token));

        Assert.Equal(0, runs);
        Assert.Equal(0, _inFlight.Count);
    }

    // A run held open until Release; it observes the shared token it was handed.
    private sealed class HeldWork
    {
        private int _runs;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<BalanceCorrectionSuggestionOutcome> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken Token { get; private set; }

        public int Runs => Volatile.Read(ref _runs);

        public async Task<BalanceCorrectionSuggestionOutcome> RunAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _runs);
            Token = ct;
            Started.TrySetResult();
            return await Release.Task.WaitAsync(ct).ConfigureAwait(false);
        }
    }
}
