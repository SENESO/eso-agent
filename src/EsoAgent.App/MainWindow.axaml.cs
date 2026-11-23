using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
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
    // ----- local-agent discovery (v1.2.7): the link file this machine's own
    // agent publishes in its data dir, and the console-key approval state.
    private ConsoleLink? _localLink;
    private bool _awaitingApproval;
    private string? _consolePublicKey;
    private bool _buildingGroups;
    private bool _wizardAutoShown;

    // ----- App correctness lane (2026-11-22) -----
    // This device as the fleet itself reported it on the last sync (before
    // self-exclusion): the identity card's live source, and the fallback
    // self identity when no console link exists.
    private string? _fleetSelfCode;
    private string? _fleetSelfName;
    private bool _fleetSelfOnline;
    // The automatic fleet connection (defect-6): a ReconnectLoop drives
    // syncs — immediately on launch, every 30 s while up (live states,
    // no manual refresh), and with capped backoff while down.
    private ReconnectLoop? _fleetLoop;
    private CancellationTokenSource? _fleetLoopCts;
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private string _lastSummary = "";

    /// <summary>This machine's fleet code: the local agent's own statement
    /// (console link) first, the fleet's row for this device second.
    /// Null = self unknown, and nothing is excluded (defect-1).</summary>
    private string? SelfCode => _localLink?.DeviceCode ?? _fleetSelfCode;

    public MainWindow()
    {
        InitializeComponent();
        // The title is product + version only — never an address, never
        // a device name (defect-9).
        Title = $"EsoAgent v{ProductInfo.Version}";
        VersionText.Text = "v" + ProductInfo.Version;
        LoadLanguage();
            LoadTrayHint();
        LoadHomeState();
        // Defect-7: the local agent's link is read BEFORE the first
        // render and before any network call, so the identity card
        // shows this device's real code from the first frame — a fleet
        // outage later can never blank it.
        _localLink = ConsoleLink.TryRead(ConsoleLink.PathForDataDir(AgentConfig.DefaultDataDir()));
        ApplyLanguage();
        UpdateGroupFilter();
        RenderRecents();
        Opened += async (_, _) =>
        {
            ReloadConfig();   // starts the fleet loop (defect-6): the first sync is immediate
            if (_cfg == null && _localLink == null && !_wizardAutoShown)
            {
                // First run: the pairing wizard is the front door now — the
                // raw config editor is behind its Advanced entry.
                _wizardAutoShown = true;
                await OpenWizardAsync();
            }
        };
        Closed += (_, _) =>
        {
            _fleetLoopCts?.Cancel();
            DesktopTab.Detach();
            TerminalTab.Detach();
        };
        // Phase 3 session fullscreen: the Desktop view raises it, the shell
        // owns the window state and hides its own chrome (header, home
        // column, status bar) so the session gets the whole window.
        DesktopTab.FullscreenChanged += fs =>
        {
            WindowState = fs ? WindowState.FullScreen : WindowState.Normal;
            HeaderBar.IsVisible = !fs;
            HomePanel.IsVisible = !fs;
            StatusBar.IsVisible = !fs;
        };
        // Session state drives the device rows and recents (defect-2).
        DesktopTab.SessionStateChanged += SyncRowSessionStates;
        // F11 toggles fullscreen whenever the Desktop tab is active,
        // whatever control has focus (the view's own handler covers the
        // focused-screen case and marks the key handled, so the two
        // never double-toggle).
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F11 && MainTabs.SelectedItem == DesktopTabItem)
            {
                DesktopTab.ToggleFullscreen();
                e.Handled = true;
            }
        };
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
    /// settings (language, tray hint) never clobber each other. Delegates
    /// to AppSettingsStore — one writer for this file, through StateFile
    /// (atomic, backed up; defect-8).</summary>
    private void UpdateSettings(Action<System.Text.Json.Nodes.JsonObject> edit)
        => AppSettingsStore.Update(AppSettingsPath, edit);

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
        TasksTabItem.Header = Strings.T("Tasks");
        TunnelsTabItem.Header = Strings.T("Tunnels");
        RecordingsTabItem.Header = Strings.T("Recordings");
        ActivityTabItem.Header = Strings.T("Activity");
        AccessTabItem.Header = Strings.T("Access");
        PairButton.Content = Strings.T("PairDevice");
        UseFleetButton.Content = Strings.T("UseThisFleet");
        CopyKeyButton.Content = Strings.T("Copy");
        AwaitingApprovalText.Text = Strings.T("AwaitingApproval");
        ThisDeviceLabel.Text = Strings.T("ThisDevice");
        ConnectByCodeLabel.Text = Strings.T("ConnectByCode");
        CodeConnectButton.Content = Strings.T("Connect");
        CodeBox.Watermark = Strings.T("CodeWatermark");
        SearchBox.Watermark = Strings.T("SearchWatermark");
        RecentLabel.Text = Strings.T("Recent");
        FlowDirection = Strings.IsArabic ? Avalonia.Media.FlowDirection.RightToLeft : Avalonia.Media.FlowDirection.LeftToRight;
        FilesTab.ApplyLanguage();
        DesktopTab.ApplyLanguage();
        TerminalTab.ApplyLanguage();
        ActivityTab.ApplyLanguage();
        TasksTab.ApplyLanguage();
        TunnelsTab.ApplyLanguage();
        RecordingsTab.ApplyLanguage();
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
        catch { StatusBarText.Text = Strings.T("BrowserOpenFailed"); }
    }

    private void ReloadConfig()
    {
        // Local-agent discovery first: the link file decides what the
        // unpaired states look like (real code + one-click offer vs the
        // plain pairing state).
        _localLink = ConsoleLink.TryRead(ConsoleLink.PathForDataDir(AgentConfig.DefaultDataDir()));
        // The fleet's picture of this device belongs to the previous
        // config until the next sync replaces it.
        _fleetSelfCode = null;
        _fleetSelfName = null;
        _fleetSelfOnline = false;
        try
        {
            _cfg = ClientConfig.Load(_configPath);
            _configError = null;
            _consolePublicKey = null;
            if (!string.IsNullOrWhiteSpace(_cfg.OperatorKeyFile) && File.Exists(_cfg.OperatorKeyFile))
            {
                try { _consolePublicKey = PairingSupport.EnsureOperatorKey(_cfg.OperatorKeyFile).PublicKeyBase64; }
                catch { /* the key panel is a convenience; a bad key file must not block startup */ }
            }
        }
        catch (FileNotFoundException)
        {
            // A missing client.json is the normal unpaired STATE, not an
            // error: the exception's text (a raw path plus a field list)
            // must never reach the footer (owner order, 2026-11-20).
            _cfg = null;
            _consolePublicKey = null;
            _configError = null;
        }
        catch (Exception)
        {
            // A config that exists but is broken is a real problem — said
            // plainly, never as the raw exception (defect-4d). The file
            // itself is preserved (StateFile never overwrites on a read).
            _cfg = null;
            _consolePublicKey = null;
            _configError = Strings.T("ConfigUnreadable");
        }
        UpdateHeaderState();
        UpdateIdentityCard();
        UpdateFooter();
        StartFleetLoop();
    }

    private string? _configError;

    /// <summary>The header's state line: mode + named state only. Network
    /// addresses never render here (defect-9) — they belong to Settings
    /// (where they are edited) and the diagnostics.</summary>
    private void UpdateHeaderState()
    {
        if (ConfigStateText == null) return;
        if (_cfg == null)
            ConfigStateText.Text = _localLink != null
                ? $"{_localLink.DeviceName} · {_localLink.DeviceCode}"
                : Strings.T("NotPairedHint");
        else if (!string.IsNullOrWhiteSpace(_cfg.FleetUrl))
            ConfigStateText.Text = _awaitingApproval
                ? Strings.T("AwaitingApprovalStatus")
                : Strings.T(_fleetReachable ? "HeaderFleetUp" : "HeaderFleetDown");
        else
            ConfigStateText.Text = Strings.T("DirectDevice");
    }

    /// <summary>The footer is a product status line, rendered from state
    /// in exactly one place: approval beats connectivity, a live fleet
    /// shows its summary, a down fleet shows the calm reconnecting line
    /// (never an exception, never an address).</summary>
    private void UpdateFooter()
    {
        if (StatusBarText == null) return;
        if (_awaitingApproval)
            StatusBarText.Text = Strings.T("AwaitingApprovalStatus");
        else if (_cfg != null && !string.IsNullOrWhiteSpace(_cfg.FleetUrl))
            StatusBarText.Text = _fleetReachable ? _lastSummary : Strings.T("Reconnecting");
        else if (_cfg != null)
            StatusBarText.Text = _lastSummary != "" ? _lastSummary : Strings.T("DirectModeLine");
        else
            StatusBarText.Text = _configError ?? "";
    }

    /// <summary>(Re)starts the automatic fleet connection for the current
    /// config (defect-6): the loop syncs immediately, polls every 30 s
    /// while up so device states stay live without a manual refresh, and
    /// retries with capped backoff while down — the owner never presses
    /// anything to be connected. Direct-only configs get one sync.</summary>
    private void StartFleetLoop()
    {
        // Cancel and drop (never Dispose: the old loop may still be
        // unwinding against its token; the GC reclaims the source).
        _fleetLoopCts?.Cancel();
        _fleetLoop = null;
        _fleetLoopCts = null;
        if (_cfg == null || string.IsNullOrWhiteSpace(_cfg.FleetUrl))
        {
            if (_cfg != null) _ = RefreshDevicesAsync();
            return;
        }
        var loop = new ReconnectLoop(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30));
        var cts = new CancellationTokenSource();
        _fleetLoop = loop;
        _fleetLoopCts = cts;
        loop.PhaseChanged += phase => Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_fleetLoop, loop)) return;   // a replaced loop must not repaint
            AppTray.SetOnline(phase == LinkPhase.Up);
            UpdateHeaderState();
            UpdateFooter();
        });
        _ = loop.RunAsync(async (ct, markUp) =>
        {
            if (await SyncFleetOnceAsync()) markUp();
        }, cts.Token);
    }

    private async void OnRefreshClick(object? sender, RoutedEventArgs e) => await RefreshDevicesAsync();

    /// <summary>One immediate sync (manual refresh, wizard/settings
    /// returns). The automatic loop runs the same path on its schedule;
    /// the gate keeps the two from overlapping.</summary>
    private async Task RefreshDevicesAsync()
    {
        var reachable = await SyncFleetOnceAsync();
        AppTray.SetOnline(reachable);
        UpdateHeaderState();
        UpdateFooter();
    }

    private async Task<bool> SyncFleetOnceAsync()
    {
        await _syncGate.WaitAsync();
        try { return await SyncFleetCoreAsync(); }
        finally { _syncGate.Release(); }
    }

    /// <summary>One fleet sync. Returns true when the fleet ANSWERED
    /// (device list fetched, or the approval state) — the reconnect
    /// loop's up signal. All state text is rendered by UpdateFooter /
    /// UpdateHeaderState from the fields set here; no exception text
    /// ever reaches the UI (defect-4d).</summary>
    private async Task<bool> SyncFleetCoreAsync()
    {
        // The link is re-read every sync (defect-7): an agent that
        // enrolls — or re-enrolls — after this app started is picked
        // up without reopening anything, and the identity card follows.
        _localLink = ConsoleLink.TryRead(ConsoleLink.PathForDataDir(AgentConfig.DefaultDataDir()));

        if (_cfg == null)
        {
            _allDevices = new List<DeviceItem>();
            DevicesList.ItemsSource = Array.Empty<DeviceItem>();
            UpdateIdentityCard();
            return false;
        }

        if (string.IsNullOrWhiteSpace(_cfg.FleetUrl))
        {
            _fleetReachable = false;
            _awaitingApproval = false;
            _lastSummary = "";
            CommitDevices(new List<DeviceItem> { DeviceItem.Direct(_cfg.Host) });
            return false;
        }

        try
        {
            using var fleet = EsoAgent.Client.Program.CreateFleetClient(_cfg);
            var devices = await fleet.GetDevicesAsync();
            _fleetReachable = true;
            _awaitingApproval = false;
            var items = devices.Select(DeviceItem.FromFleet).ToList();
            var alerts = 0;
            try
            {
                var health = await fleet.GetHealthAsync();
                foreach (var item in items)
                {
                    var h = health.FirstOrDefault(x => x.Code == item.Code);
                    if (h != null) item.HealthOverall = h.Overall;
                }
                var recent = await fleet.GetActivityAsync(new ActivityQuery
                {
                    Source = "alert",
                    Since = DateTimeOffset.UtcNow.AddHours(-24),
                    Limit = 500,
                });
                alerts = recent.Count;
            }
            catch { /* health/alerts are additive — never block the device list */ }

            var total = items.Count;
            var online = items.Count(d => d.Online);
            var pending = items.Count(d => d.IsPending);
            CaptureFleetSelf(items);
            // Defect-1: this machine is never its own connect target.
            items = SelfDevice.ExcludingSelf(items, d => d.Code, SelfCode);
            _lastSummary = $"{total} {Strings.T("DeviceCountWord")} · {online} {Strings.T("Online")} · {pending} {Strings.T("PendingWord")}"
                + (alerts > 0 ? $" · ⚠ {alerts} {Strings.T("Alerts24h")}" : "");
            CommitDevices(items);
            return true;
        }
        catch (Exception ex)
        {
            if (IsAuthFailure(ex))
            {
                // The server ANSWERED — it just does not trust this
                // console's operator key yet. That is the calm approval
                // state, not a connectivity failure, and the loop keeps
                // polling: approval takes effect on its own.
                _fleetReachable = true;
                _awaitingApproval = true;
                if (_allDevices.Count == 0)
                    CommitDevices(new List<DeviceItem> { DeviceItem.Direct(_cfg.Host) });
                else
                    UpdateIdentityCard();
                return true;
            }
            _fleetReachable = false;
            _awaitingApproval = false;
            // Keep the last-good list under the calm Reconnecting footer
            // instead of collapsing it; the direct fallback is only for
            // a console that has never seen the fleet.
            if (_allDevices.Count == 0)
                CommitDevices(new List<DeviceItem> { DeviceItem.Direct(_cfg.Host) });
            else
                UpdateIdentityCard();
            return false;
        }
    }

    /// <summary>Remembers this device as the fleet reported it (before
    /// self-exclusion) for the identity card and the SelfCode fallback.</summary>
    private void CaptureFleetSelf(List<DeviceItem> fleetItems)
    {
        var self = PairingSupport.FindThisDevice(fleetItems);
        _fleetSelfCode = self?.Code;
        _fleetSelfName = self?.Name;
        _fleetSelfOnline = self?.Online ?? false;
    }

    private void CommitDevices(List<DeviceItem> items)
    {
        foreach (var item in items)
        {
            item.IsFavourite = item.Code != null && _favourites.Contains(item.Code);
            item.SessionPhase = DesktopTab.Session.IsActiveFor(item.Code)
                ? DesktopTab.Session.Phase
                : DesktopSessionPhase.Idle;
        }
        var previousCode = (DevicesList.SelectedItem as DeviceItem)?.Code;
        _allDevices = items;
        UpdateGroupFilter();
        UpdateIdentityCard();
        ApplyDeviceFilter(previousCode);
        UpdateHeaderState();
    }

    private void OnDeviceSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_cfg == null || DevicesList.SelectedItem is not DeviceItem device) return;
        StatusTab.Attach(_cfg, device);
        TerminalTab.Attach(_cfg, device);
        DesktopTab.Attach(_cfg, device);
        FilesTab.Attach(_cfg, device);
        TasksTab.Attach(_cfg, device);
        TunnelsTab.Attach(_cfg, device);
        RecordingsTab.Attach(_cfg, device);
        ActivityTab.Attach(_cfg, device);
        AccessTab.Attach(_cfg, device);
        OpenBrowserButton.IsEnabled = true;
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
        if (DevicesEmptyText != null)
        {
            DevicesEmptyText.IsVisible = ordered.Count == 0;
            DevicesEmptyText.Text = query.Length > 0 || group != null
                ? Strings.T("NoSearchMatches")
                : Strings.T("NoDevicesYet");
        }
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

    /// <summary>The identity card, decided by Core's IdentityCard from the
    /// facts (pairing, console link, the fleet's row for this device,
    /// reachability) — defects 4 + 7: the local agent's identity shows
    /// regardless of fleet reachability, a fleet outage renders as
    /// Reconnecting (never as unenrollment), and Pair appears only for
    /// a genuine first run. No addresses render here (defect-9).</summary>
    private void UpdateIdentityCard()
    {
        if (IdentityNameText == null) return;
        var state = IdentityCard.Resolve(
            hasClientConfig: _cfg != null,
            localLink: _localLink,
            fleetSelfCode: _fleetSelfCode,
            fleetSelfName: _fleetSelfName,
            fleetSelfOnline: _fleetSelfOnline,
            fleetReachable: _fleetReachable,
            awaitingApproval: _awaitingApproval);

        IdentityNameText.Text = !string.IsNullOrWhiteSpace(_consoleName)
            ? _consoleName!
            : !string.IsNullOrWhiteSpace(state.Name) ? state.Name! : Environment.MachineName;
        IdentityCodeText.Text = state.Code ?? "—";
        IdentityDetailText.Text = state.Detail switch
        {
            IdentityDetail.SelfOnline => Strings.T("Online"),
            IdentityDetail.SelfOffline => Strings.T("Offline"),
            IdentityDetail.LocalAgentEnrolled => _cfg == null ? Strings.T("LocalAgentFound") : Strings.T("LocalAgentEnrolled"),
            IdentityDetail.AwaitingApproval => Strings.T("AwaitingApprovalStatus"),
            IdentityDetail.Reconnecting => Strings.T("Reconnecting"),
            IdentityDetail.NotPaired => Strings.T("NotPairedHint"),
            _ => Strings.T("ThisPcNotEnrolled"),   // ConsoleOnly — stated only on a live fleet answer
        };
        PairButton.IsVisible = state.ShowPair;
        UseFleetButton.IsVisible = state.ShowUseFleet;
        AwaitingApprovalPanel.IsVisible = _awaitingApproval && _consolePublicKey != null;
        if (_consolePublicKey != null) ConsoleKeyBox.Text = _consolePublicKey;
    }

    /// <summary>The one session-start path for every home affordance: select the
    /// device (which attaches every tab), show the Desktop tab, start the
    /// session — one click from a row, a recent, a double-click, or a code.</summary>
    private async Task ConnectToDeviceAsync(DeviceItem device)
    {
        // Defect-1, defence in depth: self is filtered from every list,
        // and a direct attempt still lands on the plain message.
        if (SelfDevice.IsSelf(device.Code, SelfCode))
        {
            CodeStateText.Text = Strings.T("ThisIsThisDevice");
            return;
        }
        if (!device.CanConnect) return;
        if (DesktopTab.Session.IsActiveFor(device.Code))
        {
            // Already in session with this device: just go to it.
            DevicesList.SelectedItem = device;
            MainTabs.SelectedItem = DesktopTabItem;
            return;
        }
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
            // The row action follows the real session state (defect-2):
            // for the session's own device it IS the disconnect/cancel.
            if (DesktopTab.Session.IsActiveFor(device.Code)
                && DesktopTab.Session.Phase is DesktopSessionPhase.Connecting or DesktopSessionPhase.Connected)
            {
                if (!ReferenceEquals(DevicesList.SelectedItem, device)) DevicesList.SelectedItem = device;
                MainTabs.SelectedItem = DesktopTabItem;
                await DesktopTab.DisconnectSessionAsync();
                return;
            }
            await ConnectToDeviceAsync(device);
        }
    }

    /// <summary>Mirrors the session tracker's phase onto the device rows
    /// and recents on every transition (defect-2): the row for the
    /// session's device shows Connecting… / Disconnect, every other row
    /// stays at Connect.</summary>
    private void SyncRowSessionStates()
    {
        var tracker = DesktopTab.Session;
        foreach (var item in _allDevices)
            item.SessionPhase = tracker.IsActiveFor(item.Code) ? tracker.Phase : DesktopSessionPhase.Idle;
        ApplyDeviceFilter();
        RenderRecents();
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
            StatusBarText.Text = $"{device.Name} ({device.Code}) — {Strings.T("ApprovedWord")}";
            await RefreshDevicesAsync();
        }
        catch { StatusBarText.Text = Strings.T("ApproveFailedPlain"); }
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
        if (SelfDevice.IsSelf(device.Code, SelfCode)) return;   // self is never a connect target (defect-1)
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
        var visible = 0;
        foreach (var recent in _recents)
        {
            // Recents persisted before the self-exclusion fix can still
            // hold this machine's own code — never render it (defect-1).
            if (SelfDevice.IsSelf(recent.Code, SelfCode)) continue;
            visible++;
            var live = DesktopTab.Session.IsActiveFor(recent.Code);
            var button = new Button
            {
                Content = $"{recent.Name} · {recent.Code}" + (live ? $" · {Strings.T("ConnectedWord")}" : ""),
                Tag = recent.Code,
                Margin = new Avalonia.Thickness(0, 0, 6, 6),
            };
            button.Click += async (_, _) =>
            {
                var device = _allDevices.FirstOrDefault(d => string.Equals(d.Code, recent.Code, StringComparison.OrdinalIgnoreCase));
                if (device == null) { CodeStateText.Text = Strings.T("NoDeviceWithCode") + recent.Code; return; }
                CodeStateText.Text = "";
                await ConnectToDeviceAsync(device);
            };
            RecentsPanel.Children.Add(button);
        }
        RecentsBlock.IsVisible = visible > 0;
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
        if (SelfDevice.IsSelf(code, SelfCode))
        {
            CodeStateText.Text = Strings.T("ThisIsThisDevice");
            return;
        }
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
                ? $"{device.Name} ({device.Code}) {Strings.T("PendingSuffix")}"
                : $"{device.Name} ({device.Code}) {Strings.T("CannotConnectSuffix")}";
            return;
        }
        CodeStateText.Text = "";
        await ConnectToDeviceAsync(device);
    }

    private async void OnPairClick(object? sender, RoutedEventArgs e) => await OpenWizardAsync();

    /// <summary>The fleet client surfaces HTTP failures as an
    /// HttpRequestException carrying the status code ("Fleet server
    /// returned 401: ..."). A 401/403 from the fleet API means the server
    /// was REACHED but this console's operator key is not trusted yet —
    /// the approval state, not a connectivity failure.</summary>
    private static bool IsAuthFailure(Exception ex)
        => ex is HttpRequestException
           && (ex.Message.Contains("returned 401") || ex.Message.Contains("returned 403"));

    /// <summary>One-click adoption of this machine's own fleet (v1.2.7):
    /// everything the pairing wizard does for the fleet path, sourced
    /// from the local agent's console link instead of typed input —
    /// ensure the operator key (never overwritten), take the fleet
    /// address + certificate pin from the link (probing the pin only when
    /// the link carries none), write client.json, then try the fleet.
    /// An untrusted key lands in the calm approval state with the public
    /// key ready to copy; success is claimed only when the fleet answers.</summary>
    private async void OnUseFleetClick(object? sender, RoutedEventArgs e)
    {
        var link = _localLink;
        if (link == null) return;
        UseFleetButton.IsEnabled = false;
        try
        {
            var keyPath = PairingSupport.DefaultOperatorKeyPath();
            var (_, publicKey) = PairingSupport.EnsureOperatorKey(keyPath);
            _consolePublicKey = publicKey;
            var fingerprint = link.FleetFingerprint;
            if (string.IsNullOrWhiteSpace(fingerprint))
                fingerprint = await PairingSupport.ProbeFingerprintAsync(link.FleetUrl);
            var cfg = AppConfig.LoadForEdit(_configPath);
            if (Uri.TryCreate(link.FleetUrl, UriKind.Absolute, out var fleetUri))
                cfg.Host = fleetUri.Host; // ClientConfig.Load requires a host; fleet-only installs point it at the fleet host
            cfg.FleetUrl = link.FleetUrl;
            cfg.FleetFingerprint = fingerprint;
            cfg.OperatorKeyFile = keyPath;
            cfg.Save(_configPath);
            ReloadConfig();
            await RefreshDevicesAsync(); // success pairs fully; a 401 sets the approval state — neither is faked
        }
        catch
        {
            StatusBarText.Text = Strings.T("ProbeFailedPlain");
        }
        finally
        {
            UseFleetButton.IsEnabled = true;
            UpdateIdentityCard();
        }
    }

    private async void OnCopyKeyClick(object? sender, RoutedEventArgs e)
    {
        if (_consolePublicKey == null) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null) await clipboard.SetTextAsync(_consolePublicKey);
        StatusBarText.Text = Strings.T("Copied");
    }

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
