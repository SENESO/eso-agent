using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
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
/// </summary>
public sealed partial class DesktopView : UserControl
{
    private ClientConfig? _cfg;
    private DeviceItem? _device;
    private string _targetHost = "";
    private int _targetPort;

    private SessionHandle? _client;
    private bool _connecting;
    private bool _privacyOn;
    private bool _applyingSettings;               // suppresses reconnect while the UI is being populated
    private List<MonitorInfo> _monitors = new();
    private int _monitorIndex;

    private Bitmap? _jpegBitmap;
    private WriteableBitmap? _rawBitmap;
    private RawFrame? _prevRaw;
    private long _lastMoveSent;

    public DesktopView() => InitializeComponent();

    public void Attach(ClientConfig cfg, DeviceItem device)
    {
        _cfg = cfg;
        _device = device;
        _ = DisconnectCoreAsync();
        _targetHost = device.IsFleet
            ? device.Ips.FirstOrDefault() ?? device.VirtualIp ?? cfg.Host
            : cfg.Host;
        _targetPort = cfg.Port;
        StateText.Text = device.IsFleet
            ? $"Desktop target: {_targetHost}:{_targetPort} direct, fleet relay as fallback"
            : $"Desktop target: {_targetHost}:{_targetPort}";
    }

    /// <summary>Called by the main window when the selection changes or the window closes.</summary>
    public void Detach() => _ = DisconnectCoreAsync();

    // ---------- connect / disconnect ----------

    private async void OnConnectClick(object? sender, RoutedEventArgs e) => await ConnectAsync();

    private async void OnDisconnectClick(object? sender, RoutedEventArgs e) => await DisconnectCoreAsync();

    private async void OnSessionSettingChanged(object? sender, RoutedEventArgs e)
    {
        // Mode / quality / monitor are hello-time settings: changing one with a
        // live session reconnects with the new values (the honest behaviour —
        // the protocol has no mid-session renegotiation).
        if (_applyingSettings || _client == null) return;
        _monitorIndex = Math.Max(0, MonitorBox.SelectedIndex);
        await ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        if (_cfg == null || _device == null || _connecting) return;
        _connecting = true;
        ConnectButton.IsEnabled = false;
        StateText.Text = "Connecting…";
        await DisconnectCoreAsync(updateUi: false);
        var errors = new List<string>();
        try
        {
            await OpenDirectAsync(_targetHost, _targetPort, "");
            return;
        }
        catch (Exception ex) { errors.Add($"direct {_targetHost}:{_targetPort} — {ex.Message}"); }

        // A fleet device's recorded address can be stale or unbound (an empty
        // IP list falls back to the mesh VIP, which the agent does not listen
        // on in every deployment). When the config device's own address
        // differs, retry there — the pinned fingerprint still guarantees we
        // only ever talk to the intended device.
        if (_targetHost != _cfg.Host || _targetPort != _cfg.Port)
        {
            try
            {
                await OpenDirectAsync(_cfg.Host, _cfg.Port,
                    $" · via {_cfg.Host}:{_cfg.Port} (the device address was unreachable)");
                return;
            }
            catch (Exception ex) { errors.Add($"config address — {ex.Message}"); }
        }

        // Fleet relay: works even when NO direct address is routable — the
        // device verifies the operator's stream signature itself.
        if (_device.IsFleet && !string.IsNullOrEmpty(_device.Code))
        {
            StateText.Text = "Direct paths failed — trying the fleet relay…";
            try
            {
                await OpenRelayAsync();
                return;
            }
            catch (Exception ex) { errors.Add($"fleet relay — {ex.Message}"); }
        }

        StateText.Text = "Connect failed: " + string.Join(" · ", errors);
        ConnectButton.IsEnabled = true;
        _connecting = false;
    }

    private DesktopHello BuildHello() => new()
    {
        Mode = ControlCheck.IsChecked == true ? "Control" : "View",
        Codecs = new List<string> { "jpeg", "rawdiff" },
        Quality = (int)(QualityBox.Value ?? 60),
        Monitor = _monitorIndex,
        MaxFps = 12,
        Record = RecordCheck.IsChecked == true,
    };

    private async Task OpenDirectAsync(string host, int port, string note)
    {
        var client = new DesktopClient();
        var handle = new SessionHandle(client);
        WireSession(handle);
        DesktopWelcome welcome;
        try { welcome = await client.ConnectAsync(_cfg!, host, port, BuildHello()); }
        catch { await handle.DisposeAsync(); throw; }
        _client = handle;
        FinishConnect(welcome, note);
        _connecting = false;
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
        FinishConnect(welcome, " · via fleet relay");
        _connecting = false;
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
            ChatLog.Text = (ChatLog.Text ?? "") + $"device: {text}\n");
        handle.PrivacyReceived += on => Dispatcher.UIThread.Post(() =>
        {
            _privacyOn = on;
            PrivacyButton.Content = on ? "Privacy: ON" : "Privacy: off";
        });
        handle.SessionEnded += reason => Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_client, handle)) return;   // a stale session must not touch the new one's UI
            _ = DisconnectCoreAsync();
            StateText.Text = reason;
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
        _privacyOn = false;
        PrivacyButton.Content = "Privacy: off";
        RecText.IsVisible = !string.IsNullOrEmpty(welcome.Recording);
        StateText.Text = $"Connected · {welcome.Mode} · {welcome.Codec} · {welcome.Width}×{welcome.Height}"
            + (string.IsNullOrEmpty(welcome.Recording) ? "" : $" · recording to {welcome.Recording}")
            + note;
        DisconnectButton.IsEnabled = true;
        PasteButton.IsEnabled = true;
        PrivacyButton.IsEnabled = true;
        ChatInput.IsEnabled = true;
        ChatSendButton.IsEnabled = true;
        ScreenBorder.Focus();
    }

    private async Task DisconnectCoreAsync(bool updateUi = true)
    {
        var client = _client;
        _client = null;
        if (client != null) await client.DisposeAsync();
        if (updateUi)
        {
            ConnectButton.IsEnabled = _cfg != null;
            DisconnectButton.IsEnabled = false;
            PasteButton.IsEnabled = false;
            PrivacyButton.IsEnabled = false;
            PrivacyButton.Content = "Privacy: off";
            _privacyOn = false;
            RecText.IsVisible = false;
            ChatInput.IsEnabled = false;
            ChatSendButton.IsEnabled = false;
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

    /// <summary>Pointer position normalized 0..1 over the actually-rendered image rect (letterbox-aware).</summary>
    private (double X, double Y)? Normalize(PointerEventArgs e)
    {
        var size = (ScreenImage.Source as Bitmap)?.PixelSize
                   ?? (_rawBitmap != null ? _rawBitmap.PixelSize : default);
        if (size.Width == 0 || size.Height == 0) return null;
        var bounds = ScreenImage.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return null;
        var scale = Math.Min(bounds.Width / size.Width, bounds.Height / size.Height);
        var w = size.Width * scale;
        var h = size.Height * scale;
        var pos = e.GetPosition(ScreenImage);
        var x = (pos.X - (bounds.Width - w) / 2) / w;
        var y = (pos.Y - (bounds.Height - h) / 2) / h;
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
        Key.F9 => "F9", Key.F10 => "F10", Key.F11 => "F11", Key.F12 => "F12",
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
