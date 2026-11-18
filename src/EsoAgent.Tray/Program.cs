using System.Windows.Forms;

namespace EsoAgent.Tray;

// Tray companion entry point. Runs in the owner's interactive session (the
// EsoAgent service itself runs in session 0 and cannot touch the desktop).
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayContext());
    }
}
