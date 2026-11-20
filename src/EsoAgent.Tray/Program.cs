using System.Threading;
using System.Windows.Forms;

namespace EsoAgent.Tray;

// Tray companion entry point. Runs in the owner's interactive session (the
// EsoAgent service itself runs in session 0 and cannot touch the desktop).
internal static class Program
{
    // Single instance per user session. Every launch path lands here (the
    // logon Run key, a manual start, an installer re-run), and without this
    // guard each one added ANOTHER NotifyIcon and another EsoAgentTrayPipe
    // server — the owner's "a new icon every time I open and close it"
    // report (2026-11-20). A second instance exits quietly: the running
    // copy already shows the icon and serves the pipe.
    private const string InstanceMutexName = @"Local\EsoAgent.Tray.SingleInstance";

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: false, InstanceMutexName);
        bool isFirst;
        try { isFirst = mutex.WaitOne(0, exitContext: false); }
        catch (AbandonedMutexException) { isFirst = true; } // previous copy died holding it — we own it now
        if (!isFirst) return;

        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayContext());
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch { /* exiting anyway */ }
        }
    }
}
