namespace EsoAgent.Core;

/// <summary>Exponential backoff with a cap: 5 s, 10 s, 20 s … 60 s, 60 s.
/// Pure and resettable, so both reconnect loops (the agent's fleet link,
/// the console's fleet sync) share one tested policy.</summary>
public sealed class BackoffPolicy
{
    public BackoffPolicy(TimeSpan initial, TimeSpan max)
    {
        Initial = initial;
        Max = max;
        Current = initial;
    }

    public TimeSpan Initial { get; }
    public TimeSpan Max { get; }
    public TimeSpan Current { get; private set; }

    /// <summary>Returns the delay to wait now, then doubles the next one
    /// (capped). The first failure therefore waits exactly Initial.</summary>
    public TimeSpan Next()
    {
        var wait = Current;
        Current = TimeSpan.FromTicks(Math.Min(Current.Ticks * 2, Max.Ticks));
        return wait;
    }

    public void Reset() => Current = Initial;
}

public enum LinkPhase
{
    Down,
    Connecting,
    Up,
}

/// <summary>
/// An unbounded reconnect loop with capped backoff (defect-6, 2026-11-22:
/// "the owner must never press anything to be connected"). One loop class
/// drives both the agent's fleet link (the attempt is the long-lived
/// connection itself) and the console's fleet sync (the attempt is one
/// short fetch; while Up the loop re-runs it every poll interval so
/// device states stay live without a manual refresh).
///
/// Contract:
///  • The loop NEVER exits on its own: an attempt that throws, returns
///    cleanly, or ends after being Up is always followed by another
///    attempt. Only the caller's cancellation token stops it.
///  • The attempt reports "the link is up" through the markUp callback
///    (agent: the socket connected and the hello went out; console: the
///    fetch succeeded). Marking up resets the backoff.
///  • After an attempt that was Up, the wait is the poll interval when
///    one is configured (console), else the initial backoff (agent: a
///    dropped long-lived connection retries promptly, then backs off if
///    the failures continue).
///  • <see cref="Nudge"/> cuts the current wait short — network-change
///    and power events call it so recovery is immediate instead of
///    waiting out a backoff.
/// The delay is injectable so tests run the loop at full speed with
/// recorded waits instead of real time.
/// </summary>
public sealed class ReconnectLoop
{
    private readonly BackoffPolicy _backoff;
    private readonly TimeSpan? _pollInterval;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private volatile CancellationTokenSource? _wakeCts;

    public ReconnectLoop(TimeSpan initialDelay, TimeSpan maxDelay, TimeSpan? pollInterval = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _backoff = new BackoffPolicy(initialDelay, maxDelay);
        _pollInterval = pollInterval;
        _delayAsync = delayAsync ?? Task.Delay;
    }

    public LinkPhase Phase { get; private set; } = LinkPhase.Down;

    /// <summary>Raised on every phase transition.</summary>
    public event Action<LinkPhase>? PhaseChanged;

    /// <summary>Ends the current wait immediately (thread-safe; a no-op
    /// while an attempt is running — that attempt's outcome decides the
    /// next wait).</summary>
    public void Nudge()
    {
        try { _wakeCts?.Cancel(); } catch { }
    }

    /// <summary>Runs attempts until <paramref name="ct"/> is cancelled.
    /// The attempt receives the same token plus the markUp callback.</summary>
    public async Task RunAsync(Func<CancellationToken, Action, Task> attempt, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            SetPhase(LinkPhase.Connecting);
            var wasUp = false;
            try
            {
                await attempt(ct, () =>
                {
                    wasUp = true;
                    _backoff.Reset();
                    SetPhase(LinkPhase.Up);
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch { /* a failed attempt is a wait, never an exit */ }
            if (ct.IsCancellationRequested) break;

            // While a poll interval is configured (the console), a
            // healthy link STAYS Up across the wait between polls —
            // the phase must not dip to Down every cycle and flicker
            // every surface that renders it. Without a poll interval
            // (the agent), an ended attempt means the link is down.
            if (!wasUp || _pollInterval == null) SetPhase(LinkPhase.Down);
            var wait = wasUp ? (_pollInterval ?? _backoff.Initial) : _backoff.Next();
            using var wake = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _wakeCts = wake;
            try { await _delayAsync(wait, wake.Token); }
            catch (OperationCanceledException) { /* nudged or stopping — the while condition decides */ }
            finally { _wakeCts = null; }
        }
        SetPhase(LinkPhase.Down);
    }

    private void SetPhase(LinkPhase phase)
    {
        if (Phase == phase) return;
        Phase = phase;
        PhaseChanged?.Invoke(phase);
    }
}
