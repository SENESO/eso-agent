using Avalonia;

namespace EsoAgent.App;

/// <summary>Desktop app entry point (Avalonia classic desktop lifetime).</summary>
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // One App per user session (see SingleInstance): a second launch
        // asks the running copy to show its window and exits, instead of
        // adding another process and another tray icon.
        if (!SingleInstance.TryAcquire()) return;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
