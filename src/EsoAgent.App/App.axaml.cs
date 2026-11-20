using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace EsoAgent.App;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Close-to-tray (item 42): closing the window must not kill the
            // app — only the tray menu's Quit entry shuts it down.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var window = new MainWindow();
            desktop.MainWindow = window;
            AppTray.Initialize(desktop, window);
            // Later launches (which exit immediately) ask us to surface:
            // bring the one window forward instead of starting a copy.
            SingleInstance.StartShowListener(
                () => Dispatcher.UIThread.Post(AppTray.ShowWindow));
        }
        base.OnFrameworkInitializationCompleted();
    }
}
