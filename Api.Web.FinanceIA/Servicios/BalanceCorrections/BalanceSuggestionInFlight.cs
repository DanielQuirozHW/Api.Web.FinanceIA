// Project : Api.Web.FinanceIA
// Created : 2026-11-09

namespace Api.Web.FinanceIA.Servicios.BalanceCorrections;

/// <summary>
/// Single-flight for "Sugerir corrección": callers of the SAME suggestion key
/// (<see cref="BalanceSuggestionCache.Key"/> — tenant, result, SHA-256 of the stored BalanceJson) share ONE
/// computation, so two openings at once pay for one model call. Registered as a singleton.
/// </summary>
/// <remarks>
/// <para>
/// The first caller (the owner) runs the work; a caller that arrives while it runs (a joiner) awaits the same
/// outcome — a 200, a refusal or a 502 alike. Nothing here caches: the entry is removed the moment the work
/// ends, so a failure is never served to a LATER caller (the 200 is cached by the service, as before).
/// </para>
/// <para>
/// Cancellation is per participant. The work runs on a token of its own that is cancelled only when EVERY
/// participant has cancelled: one caller leaving never cancels the work for another, and a lone caller that
/// leaves still aborts the model call, as before. The owner awaits the work to its end even after its own
/// token fired — the work runs on the owner's request-scoped services, which must outlive it — and then
/// throws <see cref="OperationCanceledException"/>.
/// </para>
/// <para>
/// SCOPE: ONE process, like <see cref="BalanceSuggestionCache"/>. Two instances can still each make a call.
/// </para>
/// </remarks>
public sealed class BalanceSuggestionInFlight
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>Number of keys being computed. For tests.</summary>
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

    /// <summary>Callers currently waiting on <paramref name="key"/>'s run (0 when none). For tests.</summary>
    internal int ParticipantsOf(string key)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(key, out var entry) ? entry.Participants : 0;
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> for <paramref name="key"/>, or joins the run already in flight for it.
    /// </summary>
    /// <param name="key">The suggestion key.</param>
    /// <param name="work">The computation; it receives the SHARED token, never the caller's.</param>
    /// <param name="cancellationToken">This caller's token: cancelling it stops this caller waiting.</param>
    /// <returns>The outcome, and whether this caller joined a run another caller started.</returns>
    /// <exception cref="OperationCanceledException">This caller's token fired (or every participant left).</exception>
    public async Task<(BalanceCorrectionSuggestionOutcome Outcome, bool Joined)> RunAsync(
        string key,
        Func<CancellationToken, Task<BalanceCorrectionSuggestionOutcome>> work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(work);
        cancellationToken.ThrowIfCancellationRequested();

        Entry entry;
        bool  isOwner;
        lock (_gate)
        {
            isOwner = !_entries.TryGetValue(key, out entry!);
            if (isOwner)
            {
                entry = new Entry();
                _entries[key] = entry;
            }

            entry.Participants++;
        }

        var participant = new Participant(this, entry);
        using var registration = cancellationToken.Register(static state => ((Participant)state!).Leave(), participant);

        if (!isOwner)
        {
            try
            {
                var joined = await entry.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return (joined, true);
            }
            finally
            {
                participant.Leave();
            }
        }

        try
        {
            var outcome = await work(entry.Cts.Token).ConfigureAwait(false);
            Remove(key, entry);
            entry.Completion.TrySetResult(outcome);
        }
        catch (OperationCanceledException ex)
        {
            Remove(key, entry);
            entry.Completion.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            Remove(key, entry);
            entry.Completion.TrySetException(ex);
        }
        finally
        {
            participant.Leave();
        }

        // The owner's own client may have left while a joiner kept the work alive: it gets no answer.
        cancellationToken.ThrowIfCancellationRequested();
        return (await entry.Completion.Task.ConfigureAwait(false), false);
    }

    // Removed BEFORE the outcome is published: a caller arriving after it starts afresh (and, after a 200,
    // finds the service's cache), never a finished entry.
    private void Remove(string key, Entry entry)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                _entries.Remove(key);
        }
    }

    private void Leave(Entry entry)
    {
        bool cancel;
        lock (_gate)
        {
            entry.Participants--;
            cancel = entry.Participants == 0 && !entry.Completion.Task.IsCompleted;
        }

        // Outside the gate: Cancel runs the work's callbacks synchronously. The source is never disposed (it
        // holds no timer or wait handle), so a late Cancel is always safe.
        if (cancel)
            entry.Cts.Cancel();
    }

    private sealed class Entry
    {
        public CancellationTokenSource Cts { get; } = new();

        public TaskCompletionSource<BalanceCorrectionSuggestionOutcome> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Participants { get; set; }
    }

    // One caller's membership of an entry; leaves exactly once (cancellation or the end of its wait).
    private sealed class Participant(BalanceSuggestionInFlight owner, Entry entry)
    {
        private int _left;

        public void Leave()
        {
            if (Interlocked.Exchange(ref _left, 1) == 0)
                owner.Leave(entry);
        }
    }
}
