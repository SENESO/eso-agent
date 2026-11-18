using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using EsoAgent.App.Views;
using EsoAgent.Client;
using EsoAgent.Core;

namespace EsoAgent.App;

/// <summary>
/// Shell window: config state + device list on the left, the selected
/// device's workspace (Status / Desktop / Terminal) on the right. Devices
/// come from the operator-signed fleet API when a fleet is configured
/// (online first, Pending badged); otherwise the single direct device from
/// the config is shown — the CLI's two worlds, one list.
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>Item 42 (close-to-tray): set ONLY by the tray menu's Quit
    /// entry. While false, closing the window hides it to the tray and the
    /// app keeps running; the agent service is unaffected either way.</summary>
    public static bool ReallyQuit;
    private bool _trayHintShown;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!ReallyQuit)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                SaveTrayHintShown();
                _ = ShowTrayHintAsync();
            }
        }
        base.OnClosing(e);
    }

    private async Task ShowTrayHintAsync()
    {
        var dialog = new Window
        {
            Title = "EsoAgent",
            Width = 430,
            Height = 170,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            CanResize = false,
        };
        var ok = new Button { Content = "OK", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, Margin = new Avalonia.Thickness(0, 12, 0, 0) };
        ok.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(18),
            Children =
            {
                new TextBlock
                {
                    Text = "EsoAgent is still running in the tray.\nThe window is hidden, not closed — reopen it from the tray icon. To quit fully, use the tray menu's “Quit EsoAgent”.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
                ok,
            },
        };
        await dialog.ShowDialog(this);
    }

    private readonly string _configPath = AppConfig.DefaultPath();
    private ClientConfig? _cfg;

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = "v" + ProductInfo.Version;
        LoadLanguage();
            LoadTrayHint();
        ApplyLanguage();
        Opened += async (_, _) =>
        {
            ReloadConfig();
            await RefreshDevicesAsync();
        };
        Closed += (_, _) => DesktopTab.Detach();
    }

    // ---------- language (EN/AR, persisted beside the client config) ----------

    private string AppSettingsPath => Path.Combine(Path.GetDirectoryName(_configPath)!, "app-settings.json");

    private void LoadLanguage()
    {
        try
        {
            if (!File.Exists(AppSettingsPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(AppSettingsPath));
            if (doc.RootElement.TryGetProperty("language", out var lang) && lang.GetString() == "ar")
                Strings.Language = "ar";
        }
        catch { /* a broken settings file must never block startup */ }
    }

    private void SaveLanguage() => UpdateSettings(obj => obj["language"] = Strings.Language);

    /// <summary>Merge-writes one key into app-settings.json so independent
    /// settings (language, tray hint) never clobber each other.</summary>
    private void UpdateSettings(Action<System.Text.Json.Nodes.JsonObject> edit)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AppSettingsPath)!);
            System.Text.Json.Nodes.JsonObject obj = new();
            if (File.Exists(AppSettingsPath))
            {
                try { obj = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(AppSettingsPath)) as System.Text.Json.Nodes.JsonObject ?? new(); }
                catch { obj = new(); }
            }
            edit(obj);
            File.WriteAllText(AppSettingsPath, obj.ToJsonString());
        }
        catch { }
    }

    private void LoadTrayHint()
    {
        try
        {
            if (!File.Exists(AppSettingsPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(AppSettingsPath));
            if (doc.RootElement.TryGetProperty("trayHintShown", out var v) && v.GetBoolean())
                _trayHintShown = true;
        }
        catch { }
    }

    private void SaveTrayHintShown() => UpdateSettings(obj => obj["trayHintShown"] = true);

    private void ApplyLanguage()
    {
        RefreshDevicesButton.Content = Strings.T("RefreshDevices");
        SettingsButton.Content = Strings.T("Settings");
        OpenBrowserButton.Content = Strings.T("OpenInBrowser");
        LanguageButton.Content = Strings.T("Language");
        StatusTabItem.Header = Strings.T("Status");
        DesktopTabItem.Header = Strings.T("Desktop");
        TerminalTabItem.Header = Strings.T("Terminal");
        FilesTabItem.Header = Strings.T("Files");
        ActivityTabItem.Header = Strings.T("Activity");
        AccessTabItem.Header = Strings.T("Access");
        FlowDirection = Strings.IsArabic ? Avalonia.Media.FlowDirection.RightToLeft : Avalonia.Media.FlowDirection.LeftToRight;
        FilesTab.ApplyLanguage();
        ActivityTab.ApplyLanguage();
    }

    private void OnLanguageClick(object? sender, RoutedEventArgs e)
    {
        Strings.Language = Strings.IsArabic ? "en" : "ar";
        SaveLanguage();
        ApplyLanguage();
    }

    // ---------- open in browser (the unified fleet panel / the device panel) ----------

    private void OnOpenBrowserClick(object? sender, RoutedEventArgs e)
    {
        if (_cfg == null || DevicesList.SelectedItem is not DeviceItem device) return;
        var url = device.IsFleet && !string.IsNullOrWhiteSpace(_cfg.FleetUrl)
            ? $"{_cfg.FleetUrl.TrimEnd('/')}/panel/?device={device.Code}"
            : $"https://{_cfg.Host}:{_cfg.Port}/panel/";
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { StatusBarText.Text = $"Open in browser failed: {ex.Message} — URL: {url}"; }
    }

    private void ReloadConfig()
    {
        try
        {
            _cfg = ClientConfig.Load(_configPath);
            ConfigStateText.Text = $"Direct device: {_cfg.Host}:{_cfg.Port}"
                + (string.IsNullOrWhiteSpace(_cfg.FleetUrl) ? " · no fleet configured" : $" · fleet: {_cfg.FleetUrl}");
            StatusBarText.Text = $"Config: {_configPath}";
        }
        catch (Exception ex)
        {
            _cfg = null;
            ConfigStateText.Text = "No usable client config — open Settings to create one.";
            StatusBarText.Text = ex.Message;
        }
    }

    private async void OnRefreshClick(object? sender, RoutedEventArgs e) => await RefreshDevicesAsync();

    private async Task RefreshDevicesAsync()
    {
        if (_cfg == null)
        {
            DevicesList.ItemsSource = Array.Empty<DeviceItem>();
            return;
        }

        List<DeviceItem> items;
        if (!string.IsNullOrWhiteSpace(_cfg.FleetUrl))
        {
            try
            {
                using var fleet = EsoAgent.Client.Program.CreateFleetClient(_cfg);
                var devices = await fleet.GetDevicesAsync();
                items = devices.Select(DeviceItem.FromFleet)
                    .OrderByDescending(d => d.Online)
                    .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var badge = "";
                try
                {
                    var health = await fleet.GetHealthAsync();
                    foreach (var item in items)
                    {
                        var h = health.FirstOrDefault(x => x.Code == item.Code);
                        if (h != null) item.HealthOverall = h.Overall;
                    }
                    var alerts = await fleet.GetActivityAsync(new ActivityQuery
                    {
                        Source = "alert",
                        Since = DateTimeOffset.UtcNow.AddHours(-24),
                        Limit = 500,
                    });
                    if (alerts.Count > 0) badge = $" · ⚠ {alerts.Count} alert(s) in the last 24 h (see Activity)";
                }
                catch { /* health/alerts are additive — never block the device list */ }
                StatusBarText.Text = $"Fleet: {items.Count} device(s), {items.Count(d => d.Online)} online, {items.Count(d => d.IsPending)} pending.{badge}";
                AppTray.SetOnline(true);
            }
            catch (Exception ex)
            {
                items = new List<DeviceItem> { DeviceItem.Direct(_cfg.Host) };
                StatusBarText.Text = $"Fleet unreachable ({ex.Message}) — showing the direct device only.";
                AppTray.SetOnline(false);
            }
        }
        else
        {
            items = new List<DeviceItem> { DeviceItem.Direct(_cfg.Host) };
            StatusBarText.Text = "Direct mode: one configured device (add a fleet URL in Settings to see the whole fleet).";
        }

        var previousCode = (DevicesList.SelectedItem as DeviceItem)?.Code;
        DevicesList.ItemsSource = items;
        DevicesList.SelectedItem = items.FirstOrDefault(d => d.Code == previousCode) ?? items.FirstOrDefault();
    }

    private void OnDeviceSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_cfg == null || DevicesList.SelectedItem is not DeviceItem device) return;
        StatusTab.Attach(_cfg, device);
        TerminalTab.Attach(_cfg, device);
        DesktopTab.Attach(_cfg, device);
        FilesTab.Attach(_cfg, device);
        ActivityTab.Attach(_cfg, device);
        AccessTab.Attach(_cfg, device);
        OpenBrowserButton.IsEnabled = true;
        Title = $"EsoAgent — {device.Name}";
    }

    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_configPath, AppConfig.LoadForEdit(_configPath));
        var saved = await dialog.ShowDialog<bool>(this);
        if (saved)
        {
            ReloadConfig();
            await RefreshDevicesAsync();
        }
    }
}
