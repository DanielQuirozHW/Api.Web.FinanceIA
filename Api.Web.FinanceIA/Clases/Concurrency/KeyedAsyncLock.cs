// Project : Api.Web.FinanceIA
// Created : 2026-11-08

namespace Api.Web.FinanceIA.Clases.Concurrency;

/// <summary>
/// An in-process async mutex per string key: callers holding different keys never wait on each other,
/// callers of the same key run one at a time. Registered as a singleton.
///
/// <para>
/// Each key gets a <see cref="SemaphoreSlim"/> that is reference-counted by every holder AND waiter, and the
/// entry is removed (and the semaphore disposed) when the last of them leaves, so the map holds only keys
/// in use. A wait cancelled through its <see cref="CancellationToken"/> gives its reference back before the
/// <see cref="OperationCanceledException"/> propagates.
/// </para>
///
/// <para>
/// SCOPE: ONE process. With several App Service instances two requests can land on different instances and
/// both pass; whatever this lock guards must stay correct (if less tidy) without it — for the company upload,
/// the Portal's double-submit guard is the other half.
/// </para>
/// </summary>
public sealed class KeyedAsyncLock
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>Number of keys currently held or waited on. For tests.</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Waits for the lock of <paramref name="key"/> and returns the handle that releases it. Dispose the
    /// handle exactly once (an <c>await using</c>), which also runs when the guarded code throws.
    /// </summary>
    /// <exception cref="OperationCanceledException">The wait was cancelled; nothing is held.</exception>
    public async Task<IAsyncDisposable> AcquireAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);

        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                _entries[key] = entry;
            }

            entry.References++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Return(key, entry, release: false);
            throw;
        }

        return new Releaser(this, key, entry);
    }

    private void Return(string key, Entry entry, bool release)
    {
        if (release)
            entry.Semaphore.Release();

        lock (_gate)
        {
            entry.References--;
            if (entry.References == 0)
            {
                _entries.Remove(key);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int References { get; set; }
    }

    private sealed class Releaser(KeyedAsyncLock owner, string key, Entry entry) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Return(key, entry, release: true);

            return ValueTask.CompletedTask;
        }
    }
}
