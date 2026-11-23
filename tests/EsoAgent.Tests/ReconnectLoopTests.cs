using EsoAgent.Core;
using Xunit;

namespace EsoAgent.Tests;

/// <summary>
/// Defect-6 (2026-11-22): automatic connection. The reconnect loop must
/// survive every failure class — exceptions, clean-but-failed attempts,
/// drops after being up — and keep retrying with capped backoff until
/// cancelled; marking up resets the backoff; a nudge (network/power
/// event) cuts the current wait short. Delays are injected so the policy
/// is verified at full speed with recorded waits.
/// </summary>
public class ReconnectLoopTests
{
    private sealed class Recorder
    {
        public List<TimeSpan> Waits { get; } = new();
        public Func<TimeSpan, CancellationToken, Task> Delay => (wait, _) =>
        {
            lock (Waits) Waits.Add(wait);
            return Task.CompletedTask;
        };
    }

    [Fact]
    public void BackoffPolicy_DoublesToTheCap_AndResets()
    {
        var policy = new BackoffPolicy(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(250));
        Assert.Equal(TimeSpan.FromMilliseconds(100), policy.Next());
        Assert.Equal(TimeSpan.FromMilliseconds(200), policy.Next());
        Assert.Equal(TimeSpan.FromMilliseconds(250), policy.Next());
        Assert.Equal(TimeSpan.FromMilliseconds(250), policy.Next());
        policy.Reset();
        Assert.Equal(TimeSpan.FromMilliseconds(100), policy.Next());
    }

    [Fact]
    public async Task Loop_SurvivesExceptionsAndCleanFailures_ThenReachesUp()
    {
        var recorder = new Recorder();
        var loop = new ReconnectLoop(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(60),
            pollInterval: TimeSpan.FromMilliseconds(700), delayAsync: recorder.Delay);
        using var cts = new CancellationTokenSource();
        var attempts = 0;
        var phases = new List<LinkPhase>();
        loop.PhaseChanged += p => { lock (phases) phases.Add(p); };

        await loop.RunAsync((ct, markUp) =>
        {
            attempts++;
            if (attempts == 1) throw new InvalidOperationException("server down"); // exception class
            if (attempts == 2) return Task.CompletedTask;                            // clean return, never up
            markUp();                                                                 // third attempt connects
            if (attempts >= 4) cts.Cancel();
            return Task.CompletedTask;
        }, cts.Token);

        Assert.True(attempts >= 4);
        Assert.Contains(LinkPhase.Up, phases);
        // Waits: failure backoff 100, 200 — then the poll interval while up.
        Assert.Equal(TimeSpan.FromMilliseconds(100), recorder.Waits[0]);
        Assert.Equal(TimeSpan.FromMilliseconds(200), recorder.Waits[1]);
        Assert.Equal(TimeSpan.FromMilliseconds(700), recorder.Waits[2]);
    }

    [Fact]
    public async Task Loop_MarkUpResetsTheBackoff()
    {
        var recorder = new Recorder();
        var loop = new ReconnectLoop(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(60),
            pollInterval: null, delayAsync: recorder.Delay);
        using var cts = new CancellationTokenSource();
        var attempts = 0;

        await loop.RunAsync((ct, markUp) =>
        {
            attempts++;
            if (attempts == 3) markUp(); // one good connection…
            if (attempts >= 5) cts.Cancel();
            return Task.CompletedTask;   // …then it drops and fails again
        }, cts.Token);

        // fail(100) → fail(200) → up: wait = initial (no poll configured)
        // → fail: backoff was reset by markUp, so 100 again — not 400.
        Assert.Equal(
            new[] { 100, 200, 100, 100 }.Select(ms => TimeSpan.FromMilliseconds(ms)).ToArray(),
            recorder.Waits.Take(4).ToArray());
    }

    [Fact]
    public async Task Loop_NudgeCutsTheWaitShort()
    {
        var loop = new ReconnectLoop(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)); // real delays
        using var cts = new CancellationTokenSource();
        var secondAttempt = new TaskCompletionSource();
        var attempts = 0;

        var run = loop.RunAsync((ct, markUp) =>
        {
            if (++attempts == 2) secondAttempt.TrySetResult();
            return Task.CompletedTask; // always fails → the loop waits out the 30 s backoff
        }, cts.Token);

        await Task.Delay(150); // first attempt done, loop is inside the long wait
        loop.Nudge();
        var finished = await Task.WhenAny(secondAttempt.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(secondAttempt.Task, finished); // reconnected immediately, not after 30 s
        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task Loop_CancellationDuringAttempt_StopsPromptly()
    {
        var recorder = new Recorder();
        var loop = new ReconnectLoop(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(60),
            delayAsync: recorder.Delay);
        using var cts = new CancellationTokenSource();

        await loop.RunAsync(async (ct, _) =>
        {
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, ct); // throws OCE from the caller's token
        }, cts.Token);

        Assert.Equal(LinkPhase.Down, loop.Phase);
        Assert.Empty(recorder.Waits); // no wait was scheduled after cancellation
    }
}
