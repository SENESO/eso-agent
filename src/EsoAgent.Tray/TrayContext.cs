using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using EsoAgent.Core;

namespace EsoAgent.Tray;

/// <summary>
/// The owner's tray companion. Three jobs:
///  1. Serve "EsoAgentTrayPipe": screenshot + interactive exec requests from
///     the session-0 service (it cannot capture the screen or run processes
///     in this session itself).
///  2. Drive the kill switch + show live status via "EsoAgentControlPipe".
///  3. Quick links: the audit log and the owner dashboard.
/// All pipe protocol JSON uses Core's EsoJson.Options, same as the service.
/// </summary>
public sealed class TrayContext : ApplicationContext
{
    private const string TrayPipeName = "EsoAgentTrayPipe";
    private const string ControlPipeName = "EsoAgentControlPipe";

    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _lastCommandItem;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly System.Windows.Forms.Timer _timer;

    private volatile bool _stopping;
    private bool _polling;
    private bool _locked;
    private static int _activeSessions;
    // Item 43: the golden-wing brand icons (embedded ICOs rendered by
    // scripts/render-brand.py). State = colour treatment of the same mark:
    // gold online, grey offline, waves-lit controlled (+ pulse frame),
    // slate locked. The drawn circles remain only as a fallback.
    private readonly Icon _iconOnline = LoadBrandIcon("tray-online.ico") ?? MakeStatusIcon(Color.FromArgb(227, 179, 65));
    private readonly Icon _iconOffline = LoadBrandIcon("tray-offline.ico") ?? MakeStatusIcon(Color.FromArgb(110, 118, 129));
    private readonly Icon _iconControlled = LoadBrandIcon("tray-controlled.ico") ?? MakeStatusIcon(Color.FromArgb(232, 134, 46));
    private readonly Icon _iconControlledPulse = LoadBrandIcon("tray-controlled-pulse.ico") ?? MakeStatusIcon(Color.FromArgb(227, 179, 65));
    private readonly Icon _iconLocked = LoadBrandIcon("tray-locked.ico") ?? MakeStatusIcon(Color.FromArgb(88, 101, 242));
    private readonly System.Windows.Forms.Timer _pulseTimer = new() { Interval = 1000 };
    private bool _pulseFrame;

    private static Icon? LoadBrandIcon(string name)
    {
        try
        {
            var asm = typeof(TrayContext).Assembly;
            using var stream = asm.GetManifestResourceStream($"EsoAgent.Tray.Assets.{name}");
            return stream == null ? null : new Icon(stream);
        }
        catch { return null; }
    }
    private readonly ToolStripMenuItem _deviceItem;
    private readonly ToolStripMenuItem _meshItem;
    private readonly ToolStripMenuItem _ownerItem;
    private readonly ToolStripMenuItem _accessCodeItem;
    private readonly ToolStripMenuItem _openAppItem;
    private readonly ToolStripMenuItem _hostItem;
    private readonly ToolStripMenuItem _logItem;
    private readonly ToolStripMenuItem _panelItem;
    private readonly ToolStripMenuItem _languageItem;
    private readonly ToolStripMenuItem _exitItem;
    private HostWindow? _hostWindow;
    private LogViewerForm? _logViewer;
    private string? _fleetPanelUrl;
    private string? _deviceCode;

    /// <summary>True while any desktop session is being served (the host
    /// window's status line reads the same signal as the tray icon).</summary>
    internal static bool IsControlled => _activeSessions > 0;

    /// <summary>Item 42: the tray icon IS the status — a filled circle drawn
    /// in code (online green / offline gray / being-controlled orange /
    /// locked indigo). No icon assets to ship or lose.</summary>
    private static Icon MakeStatusIcon(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 3, 3, 26, 26);
            using var pen = new Pen(Color.FromArgb(180, 0, 0, 0), 2);
            g.DrawEllipse(pen, 3, 3, 26, 26);
        }
        var handle = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public TrayContext()
    {
        TrayStrings.Load(DataDir);
        _statusItem = new ToolStripMenuItem(TrayStrings.StatusUnknown) { Enabled = false };
        _lastCommandItem = new ToolStripMenuItem(TrayStrings.LastCommand(null)) { Enabled = false };
        _deviceItem = new ToolStripMenuItem("…");
        _deviceItem.Click += (_, _) =>
        {
            // Click copies the device code — the handover starts here.
            if (_deviceCode != null) Clipboard.SetText(_deviceCode);
        };
        _meshItem = new ToolStripMenuItem(TrayStrings.MeshLine(null));
        _meshItem.Click += (_, _) =>
        {
            var text = _meshItem.Tag as string;
            if (text != null) Clipboard.SetText(text);
        };
        _ownerItem = new ToolStripMenuItem(TrayStrings.OwnerIdle) { Enabled = false };
        _accessCodeItem = new ToolStripMenuItem(TrayStrings.CreateAccessCode);
        _accessCodeItem.Click += (_, _) =>
        {
            // Minting happens in the fleet panel (owner sign-in required) —
            // the tray never mints on its own authority.
            if (_fleetPanelUrl != null)
                Process.Start(new ProcessStartInfo(_fleetPanelUrl) { UseShellExecute = true });
        };
        _hostItem = new ToolStripMenuItem(TrayStrings.OpenHostWindow);
        _hostItem.Click += (_, _) => OpenHostWindow();
        _openAppItem = new ToolStripMenuItem(TrayStrings.OpenApp);
        _openAppItem.Click += (_, _) => OpenApp();
        _toggleItem = new ToolStripMenuItem(TrayStrings.LockNow);
        _toggleItem.Click += (_, _) => ToggleLock();

        _logItem = new ToolStripMenuItem(TrayStrings.ViewLog);
        _logItem.Click += (_, _) => OpenAuditLog();

        _panelItem = new ToolStripMenuItem(TrayStrings.OpenDashboard);
        _panelItem.Click += (_, _) => OpenDashboard();

        _languageItem = new ToolStripMenuItem(TrayStrings.OtherLanguageName);
        _languageItem.Click += (_, _) =>
        {
            TrayStrings.ToggleLanguage();
            if (_languageItem.GetCurrentParent() is ContextMenuStrip menu)
                menu.RightToLeft = TrayStrings.IsArabic ? RightToLeft.Yes : RightToLeft.No;
            RefreshStaticTexts();
            PollStatus();
        };

        _exitItem = new ToolStripMenuItem(TrayStrings.Exit);

        _menu = new ContextMenuStrip { RightToLeft = TrayStrings.IsArabic ? RightToLeft.Yes : RightToLeft.No };
        _menu.Items.AddRange(new ToolStripItem[]
        {
            _statusItem,
            _deviceItem,
            _meshItem,
            _ownerItem,
            _lastCommandItem,
            new ToolStripSeparator(),
            _hostItem,
            _accessCodeItem,
            _openAppItem,
            _logItem,
            _panelItem,
            _toggleItem,
            new ToolStripSeparator(),
            _languageItem,
            _exitItem,
        });

        _icon = new NotifyIcon
        {
            Icon = _iconOffline,
            Text = "EsoAgent",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        // Double-clicking the tray icon opens the host window (Phase 5) —
        // the at-the-device person's home, like TeamViewer's main window.
        _icon.DoubleClick += (_, _) => OpenHostWindow();

        _exitItem.Click += (_, _) =>
        {
            _icon.Visible = false;
            ExitThread();
        };

        // The tray-pipe server runs on its own background thread (blocking API).
        var pipeThread = new Thread(PipeServerLoop) { IsBackground = true, Name = "EsoAgentTrayPipe" };
        pipeThread.Start();

        _pulseTimer.Tick += (_, _) =>
        {
            // Being-controlled pulse (item 43): alternate the waves-lit frame.
            if (_activeSessions > 0)
            {
                _pulseFrame = !_pulseFrame;
                _icon.Icon = _pulseFrame ? _iconControlledPulse : _iconControlled;
            }
        };

        // Poll the control pipe every 3 s for status / kill-switch state.
        _timer = new System.Windows.Forms.Timer { Interval = 3000 };
        _timer.Tick += (_, _) => PollStatus();
        _timer.Start();
        PollStatus();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stopping = true;
            _timer.Dispose();
            _icon.Visible = false;
            _icon.Dispose();
            _menu.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---------- data dir / config helpers ----------

    private static string DataDir =>
        Environment.GetEnvironmentVariable("ESOAGENT_HOME") is { Length: > 0 } env
            ? env
            : @"C:\Program Files\EsoAgent\data";

    /// <summary>Dashboard port from esoagent.json ("port"), default 8737.</summary>
    private static int ReadPort()
    {
        try
        {
            var path = Path.Combine(DataDir, "esoagent.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var name in new[] { "port", "Port" })
                {
                    if (doc.RootElement.TryGetProperty(name, out var p) && p.TryGetInt32(out var value) && value > 0)
                        return value;
                }
            }
        }
        catch { /* fall through to the default */ }
        return 8737;
    }

    // ---------- menu actions ----------

    /// <summary>Re-applies the static menu labels after a language switch
    /// (the dynamic lines are re-rendered by the next PollStatus).</summary>
    private void RefreshStaticTexts()
    {
        _accessCodeItem.Text = TrayStrings.CreateAccessCode;
        _hostItem.Text = TrayStrings.OpenHostWindow;
        _openAppItem.Text = TrayStrings.OpenApp;
        _logItem.Text = TrayStrings.ViewLog;
        _panelItem.Text = TrayStrings.OpenDashboard;
        _languageItem.Text = TrayStrings.OtherLanguageName;
        _exitItem.Text = TrayStrings.Exit;
    }

    /// <summary>The host window (Phase 5): one instance, brought forward.</summary>
    private void OpenHostWindow()
    {
        if (_hostWindow is { IsDisposed: false })
        {
            _hostWindow.Activate();
            return;
        }
        _hostWindow = new HostWindow(
            () => SendControlAsync("status"),
            () => SendControlAsync("apply-update", null, TimeSpan.FromMinutes(5)),
            ToggleLock,
            () => IsControlled);
        _hostWindow.FormClosed += (_, _) => _hostWindow = null;
        _hostWindow.Show();
    }

    private void OpenAuditLog()
    {
        var path = Path.Combine(DataDir, "logs", "audit.log");
        if (!File.Exists(path))
        {
            MessageBox.Show(TrayStrings.LogMissing(path), "EsoAgent",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        // Phase 5: the in-app viewer replaced the Notepad hand-off (G18) —
        // it refreshes and follows new lines while open.
        if (_logViewer is { IsDisposed: false })
        {
            _logViewer.Activate();
            return;
        }
        _logViewer = new LogViewerForm(path);
        _logViewer.FormClosed += (_, _) => _logViewer = null;
        _logViewer.Show();
    }

    private void OpenDashboard()
    {
        // The dashboard is served by the agent itself over its pinned TLS cert
        // on loopback; owner login happens in the browser.
        var url = $"https://localhost:{ReadPort()}/panel/";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private async void ToggleLock()
    {
        try
        {
            if (_locked)
            {
                // Resuming re-enables remote access — an owner act (item 42):
                // the service verifies the owner password before unlocking.
                var password = PasswordPrompt.Show();
                if (password == null) return; // cancelled
                var reply = await SendControlAsync("unlock", password);
                if (reply?["ok"]?.GetValue<bool>() != true)
                {
                    MessageBox.Show(TrayStrings.WrongPassword, "EsoAgent",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            else
            {
                var reply = await SendControlAsync("lock");
                if (reply == null) throw new InvalidOperationException("No reply from the control pipe.");
            }
            PollStatus(); // refresh the status line immediately
        }
        catch
        {
            MessageBox.Show(TrayStrings.ServiceUnreachable, "EsoAgent",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Opens the desktop app when it is installed beside the tray
    /// or in the standard install dir; the menu item disables itself when
    /// no app binary exists (item 42).</summary>
    private void OpenApp()
    {
        var path = FindAppExe();
        if (path == null) return;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static string? FindAppExe()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "EsoAgent.App.exe");
        if (File.Exists(beside)) return beside;
        var installed = @"C:\Program Files\EsoAgent\EsoAgent.App.exe";
        return File.Exists(installed) ? installed : null;
    }

    // ---------- control pipe (client): status polling + kill switch ----------

    private async void PollStatus()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var reply = await SendControlAsync("status");
            if (reply == null) throw new InvalidOperationException("No reply from the control pipe.");

            _locked = reply["locked"]?.GetValue<bool>() ?? false;
            var lastCommand = reply["lastCommand"]?.GetValue<string>();
            var fleetConnected = reply["fleetConnected"]?.GetValue<bool>() ?? false;
            _deviceCode = reply["deviceCode"]?.GetValue<string>();
            _fleetPanelUrl = reply["fleetPanelUrl"]?.GetValue<string>();
            var deviceName = reply["deviceName"]?.GetValue<string>() ?? "—";
            var meshIp = reply["meshIp"]?.GetValue<string>();
            var ownerActive = reply["ownerSessionActive"]?.GetValue<bool>() ?? false;
            var updateAvailable = reply["updateAvailable"]?.GetValue<string>();
            var controlled = _activeSessions > 0;

            _statusItem.Text = controlled ? TrayStrings.StatusControlled : _locked ? TrayStrings.StatusLocked : fleetConnected ? TrayStrings.StatusConnected : TrayStrings.StatusStandalone;
            if (!string.IsNullOrWhiteSpace(updateAvailable))
                _statusItem.Text += TrayStrings.UpdateAvailableSuffix(updateAvailable);
            _deviceItem.Text = TrayStrings.DeviceLine(deviceName, _deviceCode);
            _meshItem.Text = TrayStrings.MeshLine(meshIp);
            _meshItem.Tag = meshIp;
            _meshItem.Enabled = meshIp != null;
            _ownerItem.Text = ownerActive ? TrayStrings.OwnerActive : TrayStrings.OwnerIdle;
            _accessCodeItem.Enabled = _fleetPanelUrl != null;
            _openAppItem.Enabled = FindAppExe() != null;
            _lastCommandItem.Text = TrayStrings.LastCommand(lastCommand);
            _toggleItem.Text = _locked ? TrayStrings.Resume : TrayStrings.LockNow;
            // The icon IS the status (item 42): controlled > locked > online > offline.
            _icon.Icon = controlled ? _iconControlled : _locked ? _iconLocked : _iconOnline;
            if (controlled) _pulseTimer.Start(); else { _pulseTimer.Stop(); _pulseFrame = false; }
            _icon.Text = controlled ? TrayStrings.TooltipControlled
                : _locked ? TrayStrings.TooltipLocked
                : fleetConnected ? TrayStrings.TooltipConnected : TrayStrings.TooltipStandalone;
        }
        catch
        {
            _statusItem.Text = TrayStrings.StatusServiceDown;
            _lastCommandItem.Text = TrayStrings.LastCommand(null);
            _pulseTimer.Stop();
            _icon.Icon = _iconOffline;
            _icon.Text = TrayStrings.TooltipDown;
        }
        finally
        {
            _polling = false;
        }
    }

    /// <summary>Sends one {"action":...} line to the service's control pipe and parses the reply.</summary>
    internal static Task<JsonObject?> SendControlAsync(string action) => SendControlAsync(action, null);

    internal static async Task<JsonObject?> SendControlAsync(string action, string? password, TimeSpan? limit = null)
    {
        // apply-update downloads a package — it gets minutes, not the 2 s
        // status budget.
        using var timeout = new CancellationTokenSource(limit ?? TimeSpan.FromSeconds(2));
        using var pipe = new NamedPipeClientStream(".", ControlPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);

        using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true) { AutoFlush = true };

        var request = new JsonObject { ["action"] = action };
        if (password != null) request["password"] = password;
        await writer.WriteLineAsync(request.ToJsonString(EsoJson.Options).AsMemory(), timeout.Token);
        var line = await reader.ReadLineAsync(timeout.Token);
        return line == null ? null : JsonNode.Parse(line) as JsonObject;
    }

    // ---------- tray pipe (server): screenshot + interactive exec ----------

    private void PipeServerLoop()
    {
        var runner = new ProcessRunner();
        while (!_stopping)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    TrayPipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.None);
                server.WaitForConnection();
                HandlePipeConnection(server, runner);
            }
            catch
            {
                // A broken connection must not kill the server loop.
                if (_stopping) break;
                Thread.Sleep(250);
            }
            finally
            {
                server?.Dispose();
            }
        }
    }

    private void HandlePipeConnection(NamedPipeServerStream server, IProcessRunner runner)
    {
        using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        using var writer = new StreamWriter(server, new UTF8Encoding(false), bufferSize: 4096, leaveOpen: true) { AutoFlush = true };

        var line = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(line)) return;

        string? reply;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var action = root.TryGetProperty("action", out var a) ? a.GetString() : null;

            switch (action)
            {
                case "screenshot":
                    reply = CaptureScreenshotReply();
                    break;
                case "exec":
                {
                    var request = root.GetProperty("request").Deserialize<ExecRequest>(EsoJson.Options)
                                  ?? new ExecRequest();
                    var response = runner.RunAsync(request).GetAwaiter().GetResult();
                    reply = JsonSerializer.Serialize(response, EsoJson.Options);
                    break;
                }
                case "desktop-open":
                {
                    // Remote desktop: this connection turns into a bidirectional
                    // stream owned by the session handler until the session ends.
                    // (Screenshot/exec requests queue meanwhile — documented v1 tradeoff.)
                    // Item 42: the tray icon shows "being controlled" while
                    // ANY desktop session is served from this process.
                    Interlocked.Increment(ref _activeSessions);
                    try { DesktopSessionHandler.Run(server, reader, writer, root, () => _stopping); }
                    finally { Interlocked.Decrement(ref _activeSessions); }
                    reply = null; // the handler already spoke the protocol
                    break;
                }
                case "consent":
                {
                    // Attended-session consent (v1.2): prompt the local user with
                    // a countdown; no answer = NO. Blocks this pipe connection
                    // until answered or timed out (requests queue meanwhile).
                    // Consent v2 (Phase 5): the payload also names the
                    // requester and the clipboard permission when the service
                    // provides them; older payloads prompt as before.
                    var mode = root.TryGetProperty("mode", out var m) ? m.GetString() ?? "control" : "control";
                    var timeoutSeconds = root.TryGetProperty("timeoutSeconds", out var ts2) && ts2.TryGetInt32(out var tv) ? tv : 30;
                    var requester = root.TryGetProperty("requester", out var rq) ? rq.GetString() : null;
                    var clipboard = root.TryGetProperty("clipboard", out var cb) && cb.ValueKind == JsonValueKind.True;
                    var allowed = ConsentPrompt.Show(mode, Math.Clamp(timeoutSeconds, 5, 300), requester, clipboard);
                    reply = new JsonObject { ["ok"] = allowed }.ToJsonString(EsoJson.Options);
                    break;
                }
                default:
                    reply = ErrorReply($"Unknown action '{action}'.");
                    break;
            }
        }
        catch (Exception ex)
        {
            reply = ErrorReply(ex.Message);
        }

        if (reply != null) writer.WriteLine(reply);
        try { server.Disconnect(); } catch { /* client may already be gone */ }
    }

    private string CaptureScreenshotReply()
    {
        try
        {
            var screen = Screen.PrimaryScreen
                         ?? throw new InvalidOperationException("No primary screen is available.");
            var bounds = screen.Bounds;
            using var bitmap = new Bitmap(bounds.Width, bounds.Height);
            using (var graphics = Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            using var ms = new MemoryStream();
            bitmap.Save(ms, ImageFormat.Png);
            var ok = new JsonObject
            {
                ["ok"] = true,
                ["pngBase64"] = Convert.ToBase64String(ms.ToArray()),
            };
            return ok.ToJsonString(EsoJson.Options);
        }
        catch (Exception ex)
        {
            return ErrorReply(ex.Message);
        }
    }

    private static string ErrorReply(string message)
        => new JsonObject { ["ok"] = false, ["error"] = message }.ToJsonString(EsoJson.Options);
}

/// <summary>
/// Attended-session consent prompt (v1.2): a topmost Allow/Deny dialog with a
/// live countdown, shown on its own STA thread. Timeout, dismissal, or any
/// failure to show it resolves to NO — consent is opt-in or it does not happen.
/// </summary>
public static class ConsentPrompt
{
    public static bool Show(string mode, int timeoutSeconds, string? requester = null, bool clipboard = false)
    {
        var result = 0;
        using var done = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new ConsentForm(mode, timeoutSeconds, requester, clipboard, allowed =>
                {
                    result = allowed ? 1 : 0;
                    done.Set();
                });
                Application.Run(form);
            }
            catch { done.Set(); }
        }) { IsBackground = true, Name = "EsoAgentConsent" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        done.Wait(TimeSpan.FromSeconds(timeoutSeconds + 10));
        return result == 1;
    }
}

/// <summary>
/// Consent v2 (UI Phase 5): the prompt names WHO is asking and lists what
/// the session can do — screen always, mouse+keyboard only in control
/// mode, clipboard only when the session syncs it — in the tray's
/// language. The v1 prompt ("a remote operator is asking…") trained
/// blind Allow clicks; this one makes the person at the device read.
/// Timeout, dismissal, or any failure to show it still resolves to NO.
/// </summary>
public sealed class ConsentForm : Form
{
    private readonly Action<bool> _answer;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Label _countdown;
    private int _remaining;
    private bool _answered;

    public ConsentForm(string mode, int timeoutSeconds, string? requester, bool clipboard, Action<bool> answer)
    {
        _answer = answer;
        _remaining = timeoutSeconds;
        var control = string.Equals(mode, "control", StringComparison.OrdinalIgnoreCase);
        Text = TrayStrings.ConsentTitle;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        RightToLeft = TrayStrings.IsArabic ? RightToLeft.Yes : RightToLeft.No;
        Width = 470; Height = 264;
        var question = new Label
        {
            Dock = DockStyle.Top,
            Height = 56,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(FontFamily.GenericSansSerif, 11, FontStyle.Bold),
            Text = TrayStrings.ConsentQuestion(requester ?? "a remote operator", control),
        };
        var perms = new Label
        {
            Dock = DockStyle.Top,
            Height = 66,
            TextAlign = ContentAlignment.MiddleCenter,
            Text = string.Join("\n",
                TrayStrings.PermScreen,
                control ? TrayStrings.PermInputYes : TrayStrings.PermInputNo,
                clipboard ? TrayStrings.PermClipboardYes : TrayStrings.PermClipboardNo),
        };
        _countdown = new Label
        {
            Dock = DockStyle.Top,
            Height = 24,
            TextAlign = ContentAlignment.MiddleCenter,
            Text = TrayStrings.ConsentExpires(_remaining),
        };
        var allow = new Button { Text = TrayStrings.Allow, Width = 120, Height = 36, Location = new Point(85, 196) };
        var deny = new Button { Text = TrayStrings.Deny, Width = 120, Height = 36, Location = new Point(265, 196) };
        allow.Click += (_, _) => Answer(true);
        deny.Click += (_, _) => Answer(false);
        // Keyboard pass (Phase 6): Esc is the CancelButton, and cancel here
        // means DENY — the safe direction, same as closing the form.
        CancelButton = deny;
        Controls.Add(allow);
        Controls.Add(deny);
        Controls.Add(_countdown);
        Controls.Add(perms);
        Controls.Add(question);
        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) =>
        {
            _remaining--;
            if (_remaining <= 0) { Answer(false); return; }
            _countdown.Text = TrayStrings.ConsentExpires(_remaining);
        };
        _timer.Start();
        FormClosed += (_, _) => { if (!_answered) { _answered = true; _answer(false); } };
    }

    private void Answer(bool allowed)
    {
        if (_answered) return;
        _answered = true;
        _timer.Stop();
        _answer(allowed);
        Close();
    }
}

/// <summary>Item 42: the owner-password prompt for tray Resume. Returns the
/// password, or null when cancelled. The password travels only over the
/// local control pipe to the service, which verifies it — the tray never
/// stores or checks it itself.</summary>
public static class PasswordPrompt
{
    public static string? Show()
    {
        using var form = new Form
        {
            Text = TrayStrings.PasswordPromptTitle,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            TopMost = true,
            StartPosition = FormStartPosition.CenterScreen,
            Width = 380,
            Height = 170,
            RightToLeft = TrayStrings.IsArabic ? RightToLeft.Yes : RightToLeft.No,
        };
        var label = new Label
        {
            Dock = DockStyle.Top,
            Height = 44,
            TextAlign = ContentAlignment.MiddleCenter,
            Text = TrayStrings.PasswordPromptLabel,
        };
        var box = new TextBox
        {
            Dock = DockStyle.Top,
            UseSystemPasswordChar = true,
            Font = new Font(FontFamily.GenericSansSerif, 11),
        };
        var ok = new Button { Text = TrayStrings.PasswordPromptButton, DialogResult = DialogResult.OK, Dock = DockStyle.Bottom, Height = 36 };
        form.Controls.Add(ok);
        form.Controls.Add(box);
        form.Controls.Add(label);
        form.AcceptButton = ok;
        return form.ShowDialog() == DialogResult.OK ? box.Text : null;
    }
}
