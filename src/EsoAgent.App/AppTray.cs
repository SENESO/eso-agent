using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;

namespace EsoAgent.App;

/// <summary>Item 42: the App's tray presence + close-to-tray. The window's
/// X hides to the tray; the app keeps running; the ONLY full quit is this
/// tray menu's Quit entry. Icon state mirrors fleet reachability (green =
/// the fleet answered, gray = not yet / unreachable). The agent SERVICE is
/// unaffected either way — it always runs.</summary>
public static class AppTray
{
    private static TrayIcon? _icon;
    private static WindowIcon? _green;
    private static WindowIcon? _gray;
    private static IClassicDesktopStyleApplicationLifetime? _desktop;
    private static MainWindow? _window;

    public static void Initialize(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window)
    {
        _desktop = desktop;
        _window = window;
        _green = LoadIcon("tray-online.png");
        _gray = LoadIcon("tray-offline.png");
        var open = new NativeMenuItem("Open EsoAgent");
        open.Click += (_, _) => ShowWindow();
        var quit = new NativeMenuItem("Quit EsoAgent");
        quit.Click += (_, _) =>
        {
            MainWindow.ReallyQuit = true;
            _desktop?.Shutdown();
        };
        _icon = new TrayIcon
        {
            Icon = _gray,
            ToolTipText = "EsoAgent",
            Menu = new NativeMenu { Items = { open, quit } },
        };
        _icon.Clicked += (_, _) => ShowWindow();
        TrayIcon.SetIcons(Application.Current!, new TrayIcons { _icon });
    }

    private static WindowIcon LoadIcon(string name)
    {
        using var stream = AssetLoader.Open(new Uri($"avares://EsoAgent.App/Assets/{name}"));
        return new WindowIcon(stream);
    }

    public static void ShowWindow()
    {
        if (_window == null) return;
        _window.Show();
        _window.Activate();
    }

    /// <summary>Fleet reachability drives the icon: the gold wing when the
    /// last device refresh reached the fleet server, grey otherwise
    /// (brand state treatments, item 43).</summary>
    public static void SetOnline(bool online)
    {
        if (_icon == null) return;
        _icon.Icon = online ? _green : _gray;
        _icon.ToolTipText = online ? "EsoAgent — fleet reachable" : "EsoAgent — fleet not reached yet";
    }
}
