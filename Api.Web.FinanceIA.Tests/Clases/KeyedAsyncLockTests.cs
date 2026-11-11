// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-08

using Api.Web.FinanceIA.Clases.Concurrency;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary><see cref="KeyedAsyncLock"/>: exclusion per key, independence across keys, cleanup, cancellation.</summary>
public sealed class KeyedAsyncLockTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task SameKey_SecondCallerWaitsUntilTheFirstReleases()
    {
        var sut   = new KeyedAsyncLock();
        var first = await sut.AcquireAsync("k", CancellationToken.None);

        var second = sut.AcquireAsync("k", CancellationToken.None);
        await Task.Delay(50);
        Assert.False(second.IsCompleted);

        await first.DisposeAsync();
        await using (await second.WaitAsync(Timeout))
        {
            Assert.Equal(1, sut.Count);
        }

        Assert.Equal(0, sut.Count);
    }

    [Fact]
    public async Task DifferentKeys_DoNotWaitOnEachOther()
    {
        var sut = new KeyedAsyncLock();

        await using var a = await sut.AcquireAsync("a", CancellationToken.None);
        await using var b = await sut.AcquireAsync("b", CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(2, sut.Count);
    }

    [Fact]
    public async Task ManyContenders_NeverOverlap_AndEntriesAreRemoved()
    {
        var sut     = new KeyedAsyncLock();
        var inside  = 0;
        var maxSeen = 0;

        async Task WorkAsync()
        {
            await using (await sut.AcquireAsync("shared", CancellationToken.None))
            {
                var now = Interlocked.Increment(ref inside);
                InterlockedMax(ref maxSeen, now);
                await Task.Yield();
                await Task.Delay(1);
                Interlocked.Decrement(ref inside);
            }
        }

        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(WorkAsync))).WaitAsync(Timeout);

        Assert.Equal(1, maxSeen);
        Assert.Equal(0, sut.Count);
    }

    [Fact]
    public async Task CancelledWait_Throws_ReleasesItsReference_AndTheLockStillWorks()
    {
        var sut    = new KeyedAsyncLock();
        var holder = await sut.AcquireAsync("k", CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var waiting = sut.AcquireAsync("k", cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(1, sut.Count);   // only the holder

        await holder.DisposeAsync();
        Assert.Equal(0, sut.Count);

        await using (await sut.AcquireAsync("k", CancellationToken.None).WaitAsync(Timeout))
        {
            Assert.Equal(1, sut.Count);
        }
    }

    [Fact]
    public async Task AlreadyCancelledToken_ThrowsWithoutHoldingAnything()
    {
        var sut = new KeyedAsyncLock();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.AcquireAsync("k", new CancellationToken(canceled: true)));

        Assert.Equal(0, sut.Count);
    }

    [Fact]
    public async Task DisposingTwice_ReleasesOnce()
    {
        var sut    = new KeyedAsyncLock();
        var handle = await sut.AcquireAsync("k", CancellationToken.None);

        await handle.DisposeAsync();
        await handle.DisposeAsync();

        Assert.Equal(0, sut.Count);

        var again = await sut.AcquireAsync("k", CancellationToken.None).WaitAsync(Timeout);
        var other = sut.AcquireAsync("k", CancellationToken.None);
        await Task.Delay(50);
        Assert.False(other.IsCompleted);   // a double dispose did not leave an extra permit

        await again.DisposeAsync();
        await (await other.WaitAsync(Timeout)).DisposeAsync();
        Assert.Equal(0, sut.Count);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
            if (value <= current)
                return;
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }
}
