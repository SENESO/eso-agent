using System.Threading;

namespace EsoAgent.App;

/// <summary>Single instance per user session. The App is close-to-tray
/// (item 42): closing the window hides it and the process keeps running,
/// so without this guard every fresh launch — shortcut, or the tray
/// companion's "open app" item — started ANOTHER process with ANOTHER
/// tray icon while the "closed" copies piled up hidden (owner report,
/// 2026-11-20). The first instance holds the mutex for its whole
/// lifetime; a later launch only raises the show-window event and exits,
/// and the first instance brings its window forward.</summary>
internal static class SingleInstance
{
    private const string MutexName = @"Local\EsoAgent.App.SingleInstance";
    private const string ShowEventName = @"Local\EsoAgent.App.ShowWindow";

    // Held for the process lifetime: the static reference keeps the handle
    // (and therefore the mutex ownership) alive until exit.
    private static Mutex? _mutex;
    private static EventWaitHandle? _showEvent;

    /// <summary>True when this process is the session's one App instance
    /// and should run normally. False when another instance already runs —
    /// it has been asked to show its window, and this process must exit
    /// quietly. Platforms without named synchronization primitives fail
    /// open (run normally): the duplication this guards against is a
    /// Windows tray behaviour.</summary>
    public static bool TryAcquire()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: false, MutexName);
            bool acquired;
            try { acquired = _mutex.WaitOne(0, exitContext: false); }
            catch (AbandonedMutexException) { acquired = true; } // previous copy died holding it — we own it now
            if (acquired) return true;
            _mutex.Dispose();
            _mutex = null;
            SignalShowExisting();
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            _mutex = null;
            return true;
        }
    }

    private static void SignalShowExisting()
    {
        if (!OperatingSystem.IsWindows()) return; // named wait handles are Windows-only
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var existing))
            {
                existing.Set();
                existing.Dispose();
            }
        }
        catch { /* the first instance may still be starting; exiting quietly is enough */ }
    }

    /// <summary>Called by the first instance once its window exists:
    /// listens on a background thread for later launches and invokes
    /// onShow for each. onShow must marshal to the UI thread itself.</summary>
    public static void StartShowListener(Action onShow)
    {
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var thread = new Thread(() =>
        {
            while (true)
            {
                try { _showEvent.WaitOne(); }
                catch { return; } // handle disposed during shutdown
                onShow();
            }
        })
        { IsBackground = true, Name = "EsoAgentAppShowListener" };
        thread.Start();
    }
}
