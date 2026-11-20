using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
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

    // ----- Phase 2 home state (app-local, persisted in app-settings.json) -----
    private List<DeviceItem> _allDevices = new();
    private readonly HashSet<string> _favourites = new(StringComparer.OrdinalIgnoreCase);
    private List<RecentEntry> _recents = new();
    private string? _consoleName;
    private bool _fleetReachable;
    private bool _buildingGroups;
    private bool _wizardAutoShown;

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = "v" + ProductInfo.Version;
        LoadLanguage();
            LoadTrayHint();
        LoadHomeState();
        ApplyLanguage();
        UpdateGroupFilter();
        RenderRecents();
        Opened += async (_, _) =>
        {
            ReloadConfig();
            if (_cfg == null && !_wizardAutoShown)
            {
                // First run: the pairing wizard is the front door now — the
                // raw config editor is behind its Advanced entry.
                _wizardAutoShown = true;
                await OpenWizardAsync();
            }
            await RefreshDevicesAsync();
        };
        Closed += (_, _) => DesktopTab.Detach();
    }

    private void LoadHomeState()
    {
        var settings = AppSettingsStore.Load(AppSettingsPath);
        _favourites.Clear();
        foreach (var code in AppSettingsStore.GetStringList(settings, "favourites")) _favourites.Add(code);
        _recents = RecentEntry.LoadList(settings);
        _consoleName = AppSettingsStore.GetString(settings, "consoleName");
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
        PairButton.Content = Strings.T("PairDevice");
        ThisDeviceLabel.Text = Strings.T("ThisDevice");
        ConnectByCodeLabel.Text = Strings.T("ConnectByCode");
        CodeConnectButton.Content = Strings.T("Connect");
        CodeBox.Watermark = Strings.T("CodeWatermark");
        SearchBox.Watermark = Strings.T("SearchWatermark");
        RecentLabel.Text = Strings.T("Recent");
        FlowDirection = Strings.IsArabic ? Avalonia.Media.FlowDirection.RightToLeft : Avalonia.Media.FlowDirection.LeftToRight;
        FilesTab.ApplyLanguage();
        ActivityTab.ApplyLanguage();
        UpdateGroupFilter();
        UpdateIdentityCard();
        if (_allDevices.Count > 0) ApplyDeviceFilter();   // re-render rows so their action labels follow the language
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
            ConfigStateText.Text = Strings.T("NotPairedHint");
            StatusBarText.Text = ex.Message;
        }
        UpdateIdentityCard();
    }

    private async void OnRefreshClick(object? sender, RoutedEventArgs e) => await RefreshDevicesAsync();

    private async Task RefreshDevicesAsync()
    {
        if (_cfg == null)
        {
            _allDevices = new List<DeviceItem>();
            DevicesList.ItemsSource = Array.Empty<DeviceItem>();
            UpdateIdentityCard();
            return;
        }

        List<DeviceItem> items;
        if (!string.IsNullOrWhiteSpace(_cfg.FleetUrl))
        {
            try
            {
                using var fleet = EsoAgent.Client.Program.CreateFleetClient(_cfg);
                var devices = await fleet.GetDevicesAsync();
                _fleetReachable = true;
                items = devices.Select(DeviceItem.FromFleet).ToList();
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
                _fleetReachable = false;
                items = new List<DeviceItem> { DeviceItem.Direct(_cfg.Host) };
                StatusBarText.Text = $"Fleet unreachable ({ex.Message}) — showing the direct device only.";
                AppTray.SetOnline(false);
            }
        }
        else
        {
            _fleetReachable = false;
            items = new List<DeviceItem> { DeviceItem.Direct(_cfg.Host) };
            StatusBarText.Text = "Direct mode: one configured device (add a fleet URL in Settings to see the whole fleet).";
        }

        foreach (var item in items)
            item.IsFavourite = item.Code != null && _favourites.Contains(item.Code);
        var previousCode = (DevicesList.SelectedItem as DeviceItem)?.Code;
        _allDevices = items;
        UpdateGroupFilter();
        UpdateIdentityCard();
        ApplyDeviceFilter(previousCode);
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

    // ---------- Phase 2 home: filter, identity, one-click connect ----------

    /// <summary>Rebuilds the visible list from the full device set: search text
    /// and group filter applied, favourites first, then online, then name.
    /// Selection is preserved by device code across rebuilds.</summary>
    private void ApplyDeviceFilter(string? selectCode = null)
    {
        var query = SearchBox.Text?.Trim() ?? "";
        var group = GroupFilterBox.SelectedIndex > 0 ? GroupFilterBox.SelectedItem as string : null;
        IEnumerable<DeviceItem> view = _allDevices;
        if (query.Length > 0)
            view = view.Where(d =>
                d.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (d.Code?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                || d.Os.Contains(query, StringComparison.OrdinalIgnoreCase)
                || d.Groups.Any(g => g.Contains(query, StringComparison.OrdinalIgnoreCase)));
        if (group != null)
            view = view.Where(d => d.Groups.Contains(group, StringComparer.OrdinalIgnoreCase));
        var ordered = view
            .OrderByDescending(d => d.IsFavourite)
            .ThenByDescending(d => d.Online)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var current = selectCode ?? (DevicesList.SelectedItem as DeviceItem)?.Code;
        DevicesList.ItemsSource = ordered;
        DevicesList.SelectedItem = ordered.FirstOrDefault(d => d.Code == current) ?? ordered.FirstOrDefault();
    }

    private void UpdateGroupFilter()
    {
        if (GroupFilterBox == null) return;
        var groups = _allDevices.SelectMany(d => d.Groups)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var selected = GroupFilterBox.SelectedIndex > 0 ? GroupFilterBox.SelectedItem as string : null;
        _buildingGroups = true;
        var items = new List<string> { Strings.T("AllGroups") };
        items.AddRange(groups);
        GroupFilterBox.ItemsSource = items;
        GroupFilterBox.SelectedIndex = selected != null
            ? Math.Max(0, items.FindIndex(i => string.Equals(i, selected, StringComparison.OrdinalIgnoreCase)))
            : 0;
        _buildingGroups = false;
    }

    private void OnSearchChanged(object? sender, TextChangedEventArgs e) => ApplyDeviceFilter();

    private void OnGroupFilterChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_buildingGroups) return;
        ApplyDeviceFilter();
    }

    /// <summary>The identity card: this computer's name always; its device code
    /// in large type when this computer's own agent is enrolled in the fleet
    /// (found by local-IP or machine-name match); otherwise the pairing state.</summary>
    private void UpdateIdentityCard()
    {
        if (IdentityNameText == null) return;
        IdentityNameText.Text = !string.IsNullOrWhiteSpace(_consoleName) ? _consoleName! : Environment.MachineName;
        var self = PairingSupport.FindThisDevice(_allDevices);
        if (self != null)
        {
            IdentityCodeText.Text = self.Code;
            var parts = new List<string> { self.Online ? "Online" : "Offline" };
            if (self.VirtualIp != null) parts.Add($"mesh {self.VirtualIp}");
            IdentityDetailText.Text = string.Join(" · ", parts);
        }
        else if (_cfg == null)
        {
            IdentityCodeText.Text = "—";
            IdentityDetailText.Text = Strings.T("NotPairedHint");
        }
        else
        {
            IdentityCodeText.Text = "—";
            IdentityDetailText.Text = !string.IsNullOrWhiteSpace(_cfg.FleetUrl)
                ? $"{_cfg.FleetUrl} · {Strings.T(_fleetReachable ? "FleetReachable" : "FleetNotReached")} · {Strings.T("ThisPcNotEnrolled")}"
                : $"{Strings.T("DirectMode")}: {_cfg.Host}:{_cfg.Port}";
        }
    }

    /// <summary>The one session-start path for every home affordance: select the
    /// device (which attaches every tab), show the Desktop tab, start the
    /// session — one click from a row, a recent, a double-click, or a code.</summary>
    private async Task ConnectToDeviceAsync(DeviceItem device)
    {
        if (!device.CanConnect) return;
        if (DevicesList.ItemsSource is List<DeviceItem> visible && !visible.Contains(device))
        {
            SearchBox.Text = "";
            GroupFilterBox.SelectedIndex = 0;
            ApplyDeviceFilter(device.Code);
        }
        DevicesList.SelectedItem = device;
        MainTabs.SelectedItem = DesktopTabItem;
        RecordRecent(device);
        await DesktopTab.StartSessionAsync();
    }

    private async void OnRowConnectClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: DeviceItem device })
        {
            e.Handled = true;
            await ConnectToDeviceAsync(device);
        }
    }

    private async void OnDeviceDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DevicesList.SelectedItem is DeviceItem device && device.CanConnect)
            await ConnectToDeviceAsync(device);
    }

    private async void OnRowApproveClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: DeviceItem device } || _cfg == null || device.Code == null) return;
        e.Handled = true;
        try
        {
            using var fleet = EsoAgent.Client.Program.CreateFleetClient(_cfg);
            await fleet.PostRegistryActionAsync("approve", new { code = device.Code });
            StatusBarText.Text = $"Approved {device.Name} ({device.Code}).";
            await RefreshDevicesAsync();
        }
        catch (Exception ex) { StatusBarText.Text = $"Approve failed: {ex.Message}"; }
    }

    private void OnRowStarClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: DeviceItem device } || device.Code == null) return;
        e.Handled = true;
        device.IsFavourite = !device.IsFavourite;
        if (device.IsFavourite) _favourites.Add(device.Code);
        else _favourites.Remove(device.Code);
        UpdateSettings(obj => obj["favourites"] = AppSettingsStore.ToJsonArray(_favourites.OrderBy(c => c, StringComparer.OrdinalIgnoreCase)));
        ApplyDeviceFilter(device.Code);
    }

    private void RecordRecent(DeviceItem device)
    {
        if (device.Code == null) return;
        _recents.RemoveAll(r => string.Equals(r.Code, device.Code, StringComparison.OrdinalIgnoreCase));
        _recents.Insert(0, new RecentEntry { Code = device.Code, Name = device.Name, At = DateTimeOffset.UtcNow });
        _recents = _recents.Take(8).ToList();
        UpdateSettings(obj => obj["recents"] = RecentEntry.ToJsonArray(_recents));
        RenderRecents();
    }

    private void RenderRecents()
    {
        if (RecentsPanel == null) return;
        RecentsPanel.Children.Clear();
        foreach (var recent in _recents)
        {
            var button = new Button { Content = $"{recent.Name} · {recent.Code}", Tag = recent.Code, Margin = new Avalonia.Thickness(0, 0, 6, 6) };
            button.Click += async (_, _) =>
            {
                var device = _allDevices.FirstOrDefault(d => string.Equals(d.Code, recent.Code, StringComparison.OrdinalIgnoreCase));
                if (device == null) { CodeStateText.Text = Strings.T("NoDeviceWithCode") + recent.Code; return; }
                CodeStateText.Text = "";
                await ConnectToDeviceAsync(device);
            };
            RecentsPanel.Children.Add(button);
        }
        RecentsBlock.IsVisible = _recents.Count > 0;
    }

    private async void OnCodeConnectClick(object? sender, RoutedEventArgs e) => await ConnectByCodeAsync();

    private async void OnCodeBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await ConnectByCodeAsync();
            e.Handled = true;
        }
    }

    private async Task ConnectByCodeAsync()
    {
        var raw = CodeBox.Text?.Trim() ?? "";
        if (!DeviceIdentity.LooksLikeCode(raw))
        {
            CodeStateText.Text = Strings.T("CodeInvalid");
            return;
        }
        var code = raw.ToUpperInvariant();
        var device = _allDevices.FirstOrDefault(d => string.Equals(d.Code, code, StringComparison.OrdinalIgnoreCase));
        if (device == null && _cfg != null && !string.IsNullOrWhiteSpace(_cfg.FleetUrl))
        {
            await RefreshDevicesAsync();   // the list may simply be stale
            device = _allDevices.FirstOrDefault(d => string.Equals(d.Code, code, StringComparison.OrdinalIgnoreCase));
        }
        if (device == null)
        {
            CodeStateText.Text = Strings.T("NoDeviceWithCode") + code;
            return;
        }
        if (!device.CanConnect)
        {
            CodeStateText.Text = device.IsPending
                ? $"{device.Name} ({device.Code}) is pending approval."
                : $"{device.Name} ({device.Code}) cannot be connected to.";
            return;
        }
        CodeStateText.Text = "";
        await ConnectToDeviceAsync(device);
    }

    private async void OnPairClick(object? sender, RoutedEventArgs e) => await OpenWizardAsync();

    private async Task OpenWizardAsync()
    {
        var wizard = new PairingWizard(_configPath, AppConfig.LoadForEdit(_configPath));
        var changed = await wizard.ShowDialog<bool>(this);
        if (changed)
        {
            LoadHomeState();
            ReloadConfig();
            await RefreshDevicesAsync();
        }
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
