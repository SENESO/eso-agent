using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using EsoAgent.App.Services;
using EsoAgent.Client;
using EsoAgent.Core;

namespace EsoAgent.App.Views;

/// <summary>
/// Desktop tab — the TeamViewer-style live view. Connection order: DIRECT to
/// the agent's /api/desktop WebSocket (HMAC + pinned cert) at the device's
/// recorded address, then at the config address, and — for fleet devices —
/// finally through the FLEET RELAY: a signed stream the fleet server pipes
/// to the device, which verifies the operator's signature itself (the relay
/// gains no power). Both paths speak the same Core desktop protocol and the
/// view treats them identically (see SessionHandle). Renders jpeg frames as
/// bitmaps and rawdiff frames by decoding tiles with Core's RawDiffCodec.
/// v1.2 adds: session recording (agent-side .esorec), privacy mode (blank
/// remote screen + input lock), and in-session chat.
///
/// Phase 3 session chrome: a two-row session bar where every action is one
/// click — fullscreen (F11), scaling modes (fit / actual size / stretch,
/// applied locally), named quality presets (Core DesktopPresets; hello-time
/// like every stream parameter, so changing one reconnects), a Send
/// Ctrl+Alt+Del button, live stats measured from frame arrivals (fps,
/// frame size, throughput — the protocol carries no timestamps, so no
/// latency figure is invented), and chat as a slide-over with an unread
/// badge instead of a permanently docked block.
/// </summary>
public sealed partial class DesktopView : UserControl
{
    private enum ScalingMode { Fit, Actual, Stretch }

    private static readonly DesktopQualityPreset[] PresetOrder =
        { DesktopQualityPreset.Auto, DesktopQualityPreset.Balanced, DesktopQualityPreset.Speed, DesktopQualityPreset.Quality };
    private static readonly ScalingMode[] ScalingOrder =
        { ScalingMode.Fit, ScalingMode.Actual, ScalingMode.Stretch };

    private ClientConfig? _cfg;
    private DeviceItem? _device;
    private string _targetHost = "";
    private int _targetPort;

    private SessionHandle? _client;
    private readonly DesktopSessionTracker _session = new();
    private bool _privacyOn;
    private bool _applyingSettings;               // suppresses reconnect while the UI is being populated
    private List<MonitorInfo> _monitors = new();
    private int _monitorIndex;

    private DesktopQualityPreset _preset = DesktopPresets.Default;
    private ScalingMode _scaling = ScalingMode.Fit;
    private readonly DesktopStatsTracker _stats = new();
    private readonly DispatcherTimer _statsTimer;
    private bool _fullscreen;
    private bool _chatOpen;
    private int _chatUnread;

    private Bitmap? _jpegBitmap;
    private WriteableBitmap? _rawBitmap;
    private RawFrame? _prevRaw;
    private long _lastMoveSent;

    /// <summary>Raised when fullscreen is toggled; the main window owns the
    /// window state and the surrounding chrome (header, device list).</summary>
    public event Action<bool>? FullscreenChanged;

    /// <summary>The session lifecycle, as the one source of truth for
    /// session state (defect-2): the shell renders device rows, recents
    /// and its connect affordances from this tracker, which moves only
    /// on real lifecycle events — attempt start, welcome frame, the
    /// operator's disconnect, the client's session-ended event.</summary>
    public DesktopSessionTracker Session => _session;

    /// <summary>Raised after every session phase transition.</summary>
    public event Action? SessionStateChanged;

    /// <summary>Programmatic disconnect for the shell (a device row's
    /// Disconnect action) — the same path as the Disconnect button.</summary>
    public Task DisconnectSessionAsync() => DisconnectCoreAsync();

    public DesktopView()
    {
        InitializeComponent();
        _session.Changed += () =>
        {
            ApplySessionUi();
            SessionStateChanged?.Invoke();
        };
        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _statsTimer.Tick += OnStatsTick;
        _statsTimer.Start();
        ApplyLanguage();
    }

    /// <summary>Every session-chrome state derives from the tracker phase
    /// here — no scattered IsEnabled writes, no optimism: Connect is
    /// available only in Idle, Disconnect cancels a Connecting attempt
    /// and ends a Connected session, and the session-only actions exist
    /// only while Connected. The idle screen explains itself.</summary>
    private void ApplySessionUi()
    {
        var phase = _session.Phase;
        var live = phase == DesktopSessionPhase.Connected;
        ConnectButton.IsEnabled = phase == DesktopSessionPhase.Idle && _cfg != null && _device != null;
        DisconnectButton.IsEnabled = phase is DesktopSessionPhase.Connecting or DesktopSessionPhase.Connected;
        PasteButton.IsEnabled = live;
        CadButton.IsEnabled = live;
        PrivacyButton.IsEnabled = live;
        ChatButton.IsEnabled = live;
        ChatInput.IsEnabled = live;
        ChatSendButton.IsEnabled = live;
        NoSessionText.IsVisible = phase == DesktopSessionPhase.Idle;
        if (!live)
        {
            PrivacyButton.Content = Strings.T("PrivacyOff");
            _privacyOn = false;
            RecText.IsVisible = false;
            SetChatOpen(false);
            _chatUnread = 0;
            ChatBadge.IsVisible = false;
            StatsText.Text = "";
            ScreenImage.Source = null;
            _jpegBitmap?.Dispose();
            _jpegBitmap = null;
            _rawBitmap?.Dispose();
            _rawBitmap = null;
            _prevRaw = null;
        }
    }

    public void ApplyLanguage()
    {
        ConnectButton.Content = Strings.T("Connect");
        DisconnectButton.Content = Strings.T("Disconnect");
        ControlCheck.Content = Strings.T("Control");
        RecordCheck.Content = Strings.T("RecordSession");
        PasteButton.Content = Strings.T("PasteClipboard");
        ChatButton.Content = Strings.T("Chat");
        ChatLabel.Text = Strings.T("Chat");
        ChatSendButton.Content = Strings.T("Send");
        ToolTip.SetTip(ChatCloseButton, Strings.T("CloseChat"));
        FullscreenButton.Content = Strings.T(_fullscreen ? "ExitFullscreen" : "Fullscreen");
        ViewLabel.Text = Strings.T("View");
        QualityLabel.Text = Strings.T("Quality");
        MonitorLabel.Text = Strings.T("Monitor");
        CadButton.Content = Strings.T("SendCad");
        ChatInput.Watermark = Strings.T("ChatWatermark");
        NoSessionText.Text = Strings.T("DesktopIdleHint");
        PopulateCombos();
        ApplySessionUi();
    }

    private void PopulateCombos()
    {
        _applyingSettings = true;
        var presetIndex = Math.Max(0, Array.IndexOf(PresetOrder, _preset));
        PresetBox.ItemsSource = PresetOrder.Select(p => p switch
        {
            DesktopQualityPreset.Auto => Strings.T("PresetAuto"),
            DesktopQualityPreset.Balanced => Strings.T("PresetBalanced"),
            DesktopQualityPreset.Speed => Strings.T("PresetSpeed"),
            _ => Strings.T("Quality"),
        }).ToList();
        PresetBox.SelectedIndex = presetIndex;
        var scalingIndex = Math.Max(0, Array.IndexOf(ScalingOrder, _scaling));
        ScalingBox.ItemsSource = ScalingOrder.Select(s => s switch
        {
            ScalingMode.Fit => Strings.T("Fit"),
            ScalingMode.Actual => Strings.T("ActualSize"),
            _ => Strings.T("Stretch"),
        }).ToList();
        ScalingBox.SelectedIndex = scalingIndex;
        _applyingSettings = false;
    }

    public void Attach(ClientConfig cfg, DeviceItem device)
    {
        _cfg = cfg;
        _device = device;
        if (_fullscreen) SetFullscreen(false);
        SetChatOpen(false);
        _ = DisconnectCoreAsync();
        _targetHost = device.IsFleet
            ? device.Ips.FirstOrDefault() ?? device.VirtualIp ?? cfg.Host
            : cfg.Host;
        _targetPort = cfg.Port;
        // No addresses in the UI (defect-9): the target lives in the
        // diagnostics; the idle screen carries the plain hint instead.
        StateText.Text = "";
        ApplySessionUi();
    }

    /// <summary>Called by the main window when the selection changes or the window closes.</summary>
    public void Detach()
    {
        if (_fullscreen) SetFullscreen(false);
        _ = DisconnectCoreAsync();
    }

    /// <summary>Programmatic session start for the shell's one-click Connect
    /// (device rows, connect-by-code, recents, double-click). The exact same
    /// path as the Connect button — Attach must have selected the device first.</summary>
    public Task StartSessionAsync() => ConnectAsync();

    // ---------- connect / disconnect ----------

    private async void OnConnectClick(object? sender, RoutedEventArgs e) => await ConnectAsync();

    private async void OnDisconnectClick(object? sender, RoutedEventArgs e) => await DisconnectCoreAsync();

    private async void OnSessionSettingChanged(object? sender, RoutedEventArgs e) => await ReconnectForSettingsAsync();

    private async void OnPresetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_applyingSettings) return;
        if (PresetBox.SelectedIndex >= 0 && PresetBox.SelectedIndex < PresetOrder.Length)
            _preset = PresetOrder[PresetBox.SelectedIndex];
        await ReconnectForSettingsAsync();
    }

    private async Task ReconnectForSettingsAsync()
    {
        // Mode / quality / monitor are hello-time settings: changing one with a
        // live session reconnects with the new values (the honest behaviour —
        // the protocol has no mid-session renegotiation).
        if (_applyingSettings || _client == null) return;
        _monitorIndex = Math.Max(0, MonitorBox.SelectedIndex);
        await DisconnectCoreAsync();
        await ConnectAsync();
    }

    private void OnScalingChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_applyingSettings) return;
        if (ScalingBox.SelectedIndex >= 0 && ScalingBox.SelectedIndex < ScalingOrder.Length)
            _scaling = ScalingOrder[ScalingBox.SelectedIndex];
        // Scaling is purely local — no reconnect, the same frames are
        // simply presented differently.
        ScreenImage.Stretch = _scaling switch
        {
            ScalingMode.Actual => Stretch.None,
            ScalingMode.Stretch => Stretch.Fill,
            _ => Stretch.Uniform,
        };
    }

    private async Task ConnectAsync()
    {
        if (_cfg == null || _device == null) return;
        // The tracker is the reentrancy guard AND the state the whole UI
        // renders: a second start while a session is in flight is refused.
        if (!_session.BeginConnect(_device.Code)) return;
        StateText.Text = Strings.T("Connecting");
        await DisposeClientAsync();   // a zombie handle from a replaced session; the phase stays Connecting
        try
        {
            await OpenDirectAsync(_targetHost, _targetPort, "");
            return;
        }
        catch { /* fall through to the next path — failures are reported plainly, never as raw exception text */ }

        // A fleet device's recorded address can be stale or unbound (an empty
        // IP list falls back to the mesh VIP, which the agent does not listen
        // on in every deployment). When the config device's own address
        // differs, retry there — the pinned fingerprint still guarantees we
        // only ever talk to the intended device.
        if (_targetHost != _cfg.Host || _targetPort != _cfg.Port)
        {
            try
            {
                await OpenDirectAsync(_cfg.Host, _cfg.Port, $" · {Strings.T("ViaConfiguredAddress")}");
                return;
            }
            catch { /* fall through */ }
        }

        // Fleet relay: works even when NO direct address is routable — the
        // device verifies the operator's stream signature itself.
        if (_device.IsFleet && !string.IsNullOrEmpty(_device.Code))
        {
            StateText.Text = Strings.T("TryingRelay");
            try
            {
                await OpenRelayAsync();
                return;
            }
            catch { /* fall through */ }
        }

        _session.MarkConnectFailed();
        StateText.Text = Strings.T("ConnectFailedPlain");
    }

    private DesktopHello BuildHello()
    {
        var (quality, maxFps) = DesktopPresets.For(_preset);
        return new DesktopHello
        {
            Mode = ControlCheck.IsChecked == true ? "Control" : "View",
            Codecs = new List<string> { "jpeg", "rawdiff" },
            Quality = quality,
            Monitor = _monitorIndex,
            MaxFps = maxFps,
            Record = RecordCheck.IsChecked == true,
        };
    }

    private async Task OpenDirectAsync(string host, int port, string note)
    {
        var client = new DesktopClient();
        var handle = new SessionHandle(client);
        WireSession(handle);
        DesktopWelcome welcome;
        try { welcome = await client.ConnectAsync(_cfg!, host, port, BuildHello()); }
        catch { await handle.DisposeAsync(); throw; }
        _client = handle;
        if (!_session.MarkConnected())
        {
            // The operator cancelled while this attempt was in flight —
            // the session must not come up behind their back.
            _client = null;
            await handle.DisposeAsync();
            return;
        }
        FinishConnect(welcome, note);
    }

    private async Task OpenRelayAsync()
    {
        var fleet = EsoAgent.Client.Program.CreateFleetClient(_cfg!);
        var client = new RelayDesktopClient();
        var handle = new SessionHandle(client, fleet);
        WireSession(handle);
        DesktopWelcome welcome;
        try { welcome = await client.ConnectAsync(fleet, _device!.Code ?? "", BuildHello()); }
        catch { await handle.DisposeAsync(); throw; }
        _client = handle;
        if (!_session.MarkConnected())
        {
            _client = null;
            await handle.DisposeAsync();
            return;
        }
        FinishConnect(welcome, $" · {Strings.T("ViaFleetRelay")}");
    }

    private void WireSession(SessionHandle handle)
    {
        handle.FrameReceived += OnFrameReceived;
        handle.ClipboardReceived += text => Dispatcher.UIThread.Post(async () =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null) await clipboard.SetTextAsync(text);
        });
        handle.ChatReceived += text => Dispatcher.UIThread.Post(() =>
        {
            ChatLog.Text = (ChatLog.Text ?? "") + $"device: {text}\n";
            if (!_chatOpen)
            {
                _chatUnread++;
                ChatBadgeText.Text = _chatUnread.ToString();
                ChatBadge.IsVisible = true;
            }
        });
        handle.PrivacyReceived += on => Dispatcher.UIThread.Post(() =>
        {
            _privacyOn = on;
            PrivacyButton.Content = Strings.T(on ? "PrivacyOn" : "PrivacyOff");
        });
        handle.SessionEnded += reason => Dispatcher.UIThread.Post(async () =>
        {
            if (!ReferenceEquals(_client, handle)) return;   // a stale session must not touch the new one's UI
            await DisposeClientAsync();
            if (!_session.MarkSessionEnded()) _session.MarkDisconnected();
            // Plain language only: the clients' raw "Session error: …"
            // form sanitizes to null and the generic line shows instead.
            StateText.Text = DesktopSessionText.SanitizeEndReason(reason) ?? Strings.T("DesktopSessionEnded");
        });
    }

    private void FinishConnect(DesktopWelcome welcome, string note)
    {
        _applyingSettings = true;
        _monitors = welcome.Monitors;
        MonitorBox.ItemsSource = _monitors.Select(m => $"{m.Index} — {m.Name} ({m.Width}×{m.Height})").ToList();
        var active = _monitors.FindIndex(m => m.Width == welcome.Width && m.Height == welcome.Height);
        MonitorBox.SelectedIndex = active >= 0 ? active : 0;
        _monitorIndex = MonitorBox.SelectedIndex;
        _applyingSettings = false;

        _prevRaw = null;
        _stats.Reset();
        _privacyOn = false;
        PrivacyButton.Content = Strings.T("PrivacyOff");
        RecText.IsVisible = !string.IsNullOrEmpty(welcome.Recording);
        StateText.Text = $"{Strings.T("ConnectedWord")} · {welcome.Mode} · {welcome.Codec} · {welcome.Width}×{welcome.Height}"
            + (string.IsNullOrEmpty(welcome.Recording) ? "" : $" · {Strings.T("RecordingWord")}")
            + note;
        // Button states come from ApplySessionUi (the MarkConnected
        // transition already ran it) — never from scattered writes here.
        ScreenBorder.Focus();
    }

    /// <summary>Disposes the live handle without moving the phase —
    /// ConnectAsync clears a replaced session's zombie this way while
    /// its own attempt keeps the Connecting phase.</summary>
    private async Task DisposeClientAsync()
    {
        var client = _client;
        _client = null;
        if (client != null) await client.DisposeAsync();
    }

    private async Task DisconnectCoreAsync()
    {
        _session.BeginDisconnect();
        await DisposeClientAsync();
        _session.MarkDisconnected();
    }

    // ---------- session chrome: fullscreen, stats, CAD, chat ----------

    private void OnFullscreenClick(object? sender, RoutedEventArgs e) => SetFullscreen(!_fullscreen);

    /// <summary>The fullscreen toggle, also driven by the shell's
    /// window-level F11 (which works whatever control has focus while
    /// the Desktop tab is active — the border-local handler alone made
    /// F11 depend on where the click focus happened to be).</summary>
    public void ToggleFullscreen() => SetFullscreen(!_fullscreen);

    private void SetFullscreen(bool on)
    {
        if (_fullscreen == on) return;
        _fullscreen = on;
        FullscreenButton.Content = Strings.T(on ? "ExitFullscreen" : "Fullscreen");
        FullscreenChanged?.Invoke(on);
    }

    private void OnStatsTick(object? sender, EventArgs e)
    {
        if (_client is not { IsConnected: true })
        {
            if (!string.IsNullOrEmpty(StatsText.Text)) StatsText.Text = "";
            return;
        }
        var (fps, bps, avg) = _stats.Snapshot(Environment.TickCount64);
        StatsText.Text = $"{fps:F0} fps · {_stats.Width}×{_stats.Height} · {avg / 1024:F0} KB/f · {bps / 1024:F0} KB/s";
    }

    private void OnCadClick(object? sender, RoutedEventArgs e)
    {
        if (_client == null) return;
        _ = _client.SendInputAsync(new DesktopInput { Kind = "key", Key = "ctrl+alt+delete" });
    }

    private void OnChatToggleClick(object? sender, RoutedEventArgs e) => SetChatOpen(!_chatOpen);

    private void SetChatOpen(bool open)
    {
        _chatOpen = open;
        ChatPanel.IsVisible = open;
        if (open)
        {
            _chatUnread = 0;
            ChatBadge.IsVisible = false;
            ChatInput.Focus();
        }
    }

    // ---------- privacy + chat (v1.2) ----------

    private async void OnPrivacyClick(object? sender, RoutedEventArgs e)
    {
        if (_client == null) return;
        await _client.SendPrivacyAsync(!_privacyOn);
        // The button label updates from the device's authoritative echo (PrivacyReceived).
    }

    private async void OnChatSendClick(object? sender, RoutedEventArgs e) => await SendChatAsync();

    private async void OnChatInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { await SendChatAsync(); e.Handled = true; }
    }

    private async Task SendChatAsync()
    {
        if (_client == null) return;
        var text = ChatInput.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        ChatInput.Text = "";
        ChatLog.Text = (ChatLog.Text ?? "") + $"you: {text}\n";
        await _client.SendChatAsync(text);
    }

    /// <summary>One live desktop session, direct or relayed — identical surface either way.</summary>
    private sealed class SessionHandle : IAsyncDisposable
    {
        private readonly DesktopClient? _direct;
        private readonly RelayDesktopClient? _relay;
        private readonly FleetApiClient? _fleetOwner;

        public event Action<DesktopFrame>? FrameReceived;
        public event Action<string>? ClipboardReceived;
        public event Action<string>? ChatReceived;
        public event Action<bool>? PrivacyReceived;
        public event Action<string>? SessionEnded;

        public SessionHandle(DesktopClient direct)
        {
            _direct = direct;
            direct.FrameReceived += f => FrameReceived?.Invoke(f);
            direct.ClipboardReceived += t => ClipboardReceived?.Invoke(t);
            direct.ChatReceived += t => ChatReceived?.Invoke(t);
            direct.PrivacyReceived += b => PrivacyReceived?.Invoke(b);
            direct.SessionEnded += r => SessionEnded?.Invoke(r);
        }

        public SessionHandle(RelayDesktopClient relay, FleetApiClient fleetOwner)
        {
            _relay = relay;
            _fleetOwner = fleetOwner;
            relay.FrameReceived += f => FrameReceived?.Invoke(f);
            relay.ClipboardReceived += t => ClipboardReceived?.Invoke(t);
            relay.ChatReceived += t => ChatReceived?.Invoke(t);
            relay.PrivacyReceived += b => PrivacyReceived?.Invoke(b);
            relay.SessionEnded += r => SessionEnded?.Invoke(r);
        }

        public bool IsConnected => _direct?.IsConnected ?? _relay?.IsConnected ?? false;

        public Task SendInputAsync(DesktopInput input) =>
            _direct != null ? _direct.SendInputAsync(input) : _relay!.SendInputAsync(input);

        public Task SendClipboardAsync(string text) =>
            _direct != null ? _direct.SendClipboardAsync(text) : _relay!.SendClipboardAsync(text);

        public Task SendPrivacyAsync(bool enabled) =>
            _direct != null ? _direct.SendPrivacyAsync(enabled) : _relay!.SendPrivacyAsync(enabled);

        public Task SendChatAsync(string text) =>
            _direct != null ? _direct.SendChatAsync(text) : _relay!.SendChatAsync(text);

        public async ValueTask DisposeAsync()
        {
            if (_direct != null) await _direct.DisposeAsync();
            if (_relay != null) await _relay.DisposeAsync();
            _fleetOwner?.Dispose();
        }
    }

    // ---------- frames ----------

    private void OnFrameReceived(DesktopFrame frame)
    {
        var data = Convert.FromBase64String(frame.Data);
        _stats.AddFrame(data.Length, frame.Width, frame.Height, frame.Codec, Environment.TickCount64);
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (frame.Codec == "jpeg")
                {
                    var bitmap = new Bitmap(new MemoryStream(data));
                    var old = _jpegBitmap;
                    _jpegBitmap = bitmap;
                    ScreenImage.Source = bitmap;
                    old?.Dispose();
                }
                else if (frame.Codec == "rawdiff")
                {
                    var decoded = RawDiffCodec.Decode(data, frame.Full ? null : _prevRaw);
                    _prevRaw = decoded;
                    RenderRaw(decoded);
                }
            }
            catch { /* a torn frame is dropped; the next keyframe repairs the picture */ }
        });
    }

    private void RenderRaw(RawFrame frame)
    {
        if (_rawBitmap == null || _rawBitmap.PixelSize.Width != frame.Width || _rawBitmap.PixelSize.Height != frame.Height)
        {
            _rawBitmap?.Dispose();
            _rawBitmap = new WriteableBitmap(
                new PixelSize(frame.Width, frame.Height), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Premul);
            ScreenImage.Source = _rawBitmap;
        }
        using var fb = _rawBitmap.Lock();
        var rowBytes = frame.Width * 4;
        for (var y = 0; y < frame.Height; y++)
            Marshal.Copy(frame.Bgra, y * rowBytes, fb.Address + y * fb.RowBytes, rowBytes);
        ScreenImage.InvalidateVisual();
    }

    // ---------- input ----------

    private bool CanControl => _client is { IsConnected: true } && ControlCheck.IsChecked == true;

    /// <summary>Pointer position normalized 0..1 over the actually-rendered
    /// image rect — letterbox-aware in Fit, centered-native in Actual size,
    /// direct in Stretch.</summary>
    private (double X, double Y)? Normalize(PointerEventArgs e)
    {
        var size = (ScreenImage.Source as Bitmap)?.PixelSize
                   ?? (_rawBitmap != null ? _rawBitmap.PixelSize : default);
        if (size.Width == 0 || size.Height == 0) return null;
        var bounds = ScreenImage.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return null;
        var pos = e.GetPosition(ScreenImage);
        double x, y;
        switch (_scaling)
        {
            case ScalingMode.Stretch:
                x = pos.X / bounds.Width;
                y = pos.Y / bounds.Height;
                break;
            case ScalingMode.Actual:
                x = (pos.X - (bounds.Width - size.Width) / 2) / size.Width;
                y = (pos.Y - (bounds.Height - size.Height) / 2) / size.Height;
                break;
            default:
                var scale = Math.Min(bounds.Width / size.Width, bounds.Height / size.Height);
                var w = size.Width * scale;
                var h = size.Height * scale;
                x = (pos.X - (bounds.Width - w) / 2) / w;
                y = (pos.Y - (bounds.Height - h) / 2) / h;
                break;
        }
        return x is >= 0 and <= 1 && y is >= 0 and <= 1 ? (x, y) : null;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!CanControl) return;
        var now = Environment.TickCount64;
        if (now - _lastMoveSent < 33) return;   // ~30 moves/s is plenty; intermediates are worthless
        if (Normalize(e) is not { } p) return;
        _lastMoveSent = now;
        _ = _client!.SendInputAsync(new DesktopInput { Kind = "mouseMove", X = p.X, Y = p.Y });
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        ScreenBorder.Focus();
        if (!CanControl) return;
        if (Normalize(e) is not { } p) return;
        var button = ButtonName(e.GetCurrentPoint(ScreenBorder).Properties.PointerUpdateKind);
        if (button == null) return;
        _ = _client!.SendInputAsync(new DesktopInput { Kind = "mouseButton", X = p.X, Y = p.Y, Button = button, Down = true });
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!CanControl) return;
        if (Normalize(e) is not { } p) return;
        var button = e.InitialPressMouseButton switch
        {
            MouseButton.Left => "left",
            MouseButton.Right => "right",
            MouseButton.Middle => "middle",
            _ => null,
        };
        if (button == null) return;
        _ = _client!.SendInputAsync(new DesktopInput { Kind = "mouseButton", X = p.X, Y = p.Y, Button = button, Down = false });
        e.Handled = true;
    }

    private void OnPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!CanControl) return;
        if (Normalize(e) is not { } p) return;
        var delta = (int)(e.Delta.Y * 120);
        if (delta == 0) return;
        _ = _client!.SendInputAsync(new DesktopInput { Kind = "wheel", X = p.X, Y = p.Y, Delta = delta });
        e.Handled = true;
    }

    private static string? ButtonName(PointerUpdateKind kind) => kind switch
    {
        PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => "left",
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => "right",
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => "middle",
        _ => null,
    };

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Local chrome keys first: F11 is the fullscreen toggle (the plan's
        // session-bar convention), Esc closes the chat slide-over, and Esc
        // ALWAYS leaves fullscreen (owner order, 2026-11-22) — in Control
        // mode Esc therefore no longer reaches the remote machine while
        // fullscreen; the session bar's Exit fullscreen button and F11
        // are the same exits.
        if (e.Key == Key.F11) { ToggleFullscreen(); e.Handled = true; return; }
        if (e.Key == Key.Escape && _chatOpen) { SetChatOpen(false); e.Handled = true; return; }
        if (e.Key == Key.Escape && _fullscreen) { SetFullscreen(false); e.Handled = true; return; }
        if (!CanControl) return;
        var mods = e.KeyModifiers;
        var ctrl = mods.HasFlag(KeyModifiers.Control);
        var alt = mods.HasFlag(KeyModifiers.Alt);
        var meta = mods.HasFlag(KeyModifiers.Meta);

        if (ctrl && alt && e.Key == Key.Delete)
        {
            _ = _client!.SendInputAsync(new DesktopInput { Kind = "key", Key = "ctrl+alt+delete" });
            e.Handled = true;
            return;
        }

        var special = SpecialKeyName(e.Key);
        if (ctrl || alt || meta)
        {
            // Combos never reach TextInput: send key name + modifier flags.
            var name = special ?? (e.Key is >= Key.A and <= Key.Z
                ? ((char)('a' + (e.Key - Key.A))).ToString()
                : e.Key is >= Key.D0 and <= Key.D9
                    ? ((char)('0' + (e.Key - Key.D0))).ToString()
                    : null);
            if (name == null) return;
            _ = _client!.SendInputAsync(new DesktopInput
            {
                Kind = "key", Key = name, Ctrl = ctrl, Alt = alt, Meta = meta,
                Shift = mods.HasFlag(KeyModifiers.Shift),
            });
            e.Handled = true;
            return;
        }

        if (special != null)
        {
            _ = _client!.SendInputAsync(new DesktopInput
            {
                Kind = "key", Key = special, Shift = mods.HasFlag(KeyModifiers.Shift),
            });
            e.Handled = true;
        }
        // Plain printable keys fall through to TextInput, which carries the real character.
    }

    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (!CanControl || string.IsNullOrEmpty(e.Text)) return;
        foreach (var ch in e.Text)
            _ = _client!.SendInputAsync(new DesktopInput { Kind = "key", Key = ch.ToString() });
        e.Handled = true;
    }

    private static string? SpecialKeyName(Key key) => key switch
    {
        Key.Enter => "Enter",
        Key.Escape => "Escape",
        Key.Tab => "Tab",
        Key.Back => "Backspace",
        Key.Delete => "Delete",
        Key.Home => "Home",
        Key.End => "End",
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        Key.Up => "ArrowUp",
        Key.Down => "ArrowDown",
        Key.Left => "ArrowLeft",
        Key.Right => "ArrowRight",
        Key.Insert => "Insert",
        Key.F1 => "F1", Key.F2 => "F2", Key.F3 => "F3", Key.F4 => "F4",
        Key.F5 => "F5", Key.F6 => "F6", Key.F7 => "F7", Key.F8 => "F8",
        Key.F9 => "F9", Key.F10 => "F10", Key.F12 => "F12",
        // F11 is the local fullscreen toggle (handled in OnKeyDown before this).
        _ => null,
    };

    // ---------- clipboard ----------

    private async void OnPasteClick(object? sender, RoutedEventArgs e)
    {
        if (_client == null) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        var text = clipboard == null ? null : await clipboard.TryGetTextAsync();
        if (!string.IsNullOrEmpty(text)) await _client.SendClipboardAsync(text);
    }
}
