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
    private string? _fleetPanelUrl;
    private string? _deviceCode;

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
        _statusItem = new ToolStripMenuItem("الحالة: …") { Enabled = false };
        _lastCommandItem = new ToolStripMenuItem("آخر أمر: —") { Enabled = false };
        _deviceItem = new ToolStripMenuItem("الجهاز: …");
        _deviceItem.Click += (_, _) =>
        {
            // Click copies the device code — the handover starts here.
            if (_deviceCode != null) Clipboard.SetText(_deviceCode);
        };
        _meshItem = new ToolStripMenuItem("عنوان الشبكة: —");
        _meshItem.Click += (_, _) =>
        {
            var text = _meshItem.Tag as string;
            if (text != null) Clipboard.SetText(text);
        };
        _ownerItem = new ToolStripMenuItem("المالك: —") { Enabled = false };
        _accessCodeItem = new ToolStripMenuItem("إنشاء كود وصول…");
        _accessCodeItem.Click += (_, _) =>
        {
            // Minting happens in the fleet panel (owner sign-in required) —
            // the tray never mints on its own authority.
            if (_fleetPanelUrl != null)
                Process.Start(new ProcessStartInfo(_fleetPanelUrl) { UseShellExecute = true });
        };
        _openAppItem = new ToolStripMenuItem("فتح التطبيق");
        _openAppItem.Click += (_, _) => OpenApp();
        _toggleItem = new ToolStripMenuItem("قطع الاتصال");
        _toggleItem.Click += (_, _) => ToggleLock();

        var logItem = new ToolStripMenuItem("عرض السجل");
        logItem.Click += (_, _) => OpenAuditLog();

        var panelItem = new ToolStripMenuItem("فتح لوحة التحكم");
        panelItem.Click += (_, _) => OpenDashboard();

        var exitItem = new ToolStripMenuItem("خروج");

        _menu = new ContextMenuStrip { RightToLeft = RightToLeft.Yes };
        _menu.Items.AddRange(new ToolStripItem[]
        {
            _statusItem,
            _deviceItem,
            _meshItem,
            _ownerItem,
            _lastCommandItem,
            new ToolStripSeparator(),
            _accessCodeItem,
            _openAppItem,
            logItem,
            panelItem,
            _toggleItem,
            new ToolStripSeparator(),
            exitItem,
        });

        _icon = new NotifyIcon
        {
            Icon = _iconOffline,
            Text = "EsoAgent",
            ContextMenuStrip = _menu,
            Visible = true,
        };

        exitItem.Click += (_, _) =>
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

    private void OpenAuditLog()
    {
        var path = Path.Combine(DataDir, "logs", "audit.log");
        if (!File.Exists(path))
        {
            MessageBox.Show($"ملف السجل غير موجود:\n{path}", "EsoAgent",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Process.Start(new ProcessStartInfo
        {
            FileName = "notepad.exe",
            Arguments = $"\"{path}\"",
            UseShellExecute = true,
        });
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
                    MessageBox.Show("كلمة مرور المالك غير صحيحة — لم يتم الاستئناف.", "EsoAgent",
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
            MessageBox.Show("تعذر الاتصال بخدمة EsoAgent.", "EsoAgent",
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

            _statusItem.Text = controlled ? "الحالة: يتم التحكم الآن" : _locked ? "الحالة: مقفول (kill switch)" : fleetConnected ? "الحالة: متصل" : "الحالة: يعمل (بدون أسطول)";
            if (!string.IsNullOrWhiteSpace(updateAvailable))
                _statusItem.Text += $" — تحديث متاح: v{updateAvailable}";
            _deviceItem.Text = _deviceCode != null ? $"الجهاز: {deviceName} ({_deviceCode}) — انسخ الكود" : $"الجهاز: {deviceName}";
            _meshItem.Text = meshIp != null ? $"عنوان الشبكة: {meshIp} — انسخ" : "عنوان الشبكة: —";
            _meshItem.Tag = meshIp;
            _meshItem.Enabled = meshIp != null;
            _ownerItem.Text = ownerActive ? "المالك: جلسة لوحة نشطة" : "المالك: لا جلسة نشطة";
            _accessCodeItem.Enabled = _fleetPanelUrl != null;
            _openAppItem.Enabled = FindAppExe() != null;
            _lastCommandItem.Text = lastCommand != null ? $"آخر أمر: {lastCommand}" : "آخر أمر: —";
            _toggleItem.Text = _locked ? "استئناف الاتصال" : "قطع الاتصال";
            // The icon IS the status (item 42): controlled > locked > online > offline.
            _icon.Icon = controlled ? _iconControlled : _locked ? _iconLocked : _iconOnline;
            if (controlled) _pulseTimer.Start(); else { _pulseTimer.Stop(); _pulseFrame = false; }
            _icon.Text = controlled ? "EsoAgent — يتم التحكم في هذا الجهاز الآن"
                : _locked ? "EsoAgent — مقفول (kill switch)"
                : fleetConnected ? "EsoAgent — متصل" : "EsoAgent — يعمل";
        }
        catch
        {
            _statusItem.Text = "الخدمة غير متصلة";
            _lastCommandItem.Text = "آخر أمر: —";
            _pulseTimer.Stop();
            _icon.Icon = _iconOffline;
            _icon.Text = "EsoAgent — الخدمة غير متصلة";
        }
        finally
        {
            _polling = false;
        }
    }

    /// <summary>Sends one {"action":...} line to the service's control pipe and parses the reply.</summary>
    private static Task<JsonObject?> SendControlAsync(string action) => SendControlAsync(action, null);

    private static async Task<JsonObject?> SendControlAsync(string action, string? password)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
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
                    var mode = root.TryGetProperty("mode", out var m) ? m.GetString() ?? "control" : "control";
                    var timeoutSeconds = root.TryGetProperty("timeoutSeconds", out var ts2) && ts2.TryGetInt32(out var tv) ? tv : 30;
                    var allowed = ConsentPrompt.Show(mode, Math.Clamp(timeoutSeconds, 5, 300));
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
    public static bool Show(string mode, int timeoutSeconds)
    {
        var result = 0;
        using var done = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new ConsentForm(mode, timeoutSeconds, allowed =>
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

public sealed class ConsentForm : Form
{
    private readonly Action<bool> _answer;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Label _countdown;
    private int _remaining;
    private bool _answered;

    public ConsentForm(string mode, int timeoutSeconds, Action<bool> answer)
    {
        _answer = answer;
        _remaining = timeoutSeconds;
        Text = "EsoAgent — remote session request";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        Width = 440; Height = 190;
        var question = new Label
        {
            Dock = DockStyle.Top,
            Height = 70,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(FontFamily.GenericSansSerif, 11, FontStyle.Bold),
            Text = mode == "control"
                ? "A remote operator is asking to CONTROL this computer."
                : "A remote operator is asking to VIEW this computer's screen.",
        };
        _countdown = new Label
        {
            Dock = DockStyle.Top,
            Height = 24,
            TextAlign = ContentAlignment.MiddleCenter,
            Text = $"This request expires in {_remaining}s — no answer means No.",
        };
        var allow = new Button { Text = "Allow", Width = 120, Height = 36, Location = new Point(70, 118) };
        var deny = new Button { Text = "Deny", Width = 120, Height = 36, Location = new Point(240, 118) };
        allow.Click += (_, _) => Answer(true);
        deny.Click += (_, _) => Answer(false);
        Controls.Add(allow);
        Controls.Add(deny);
        Controls.Add(_countdown);
        Controls.Add(question);
        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) =>
        {
            _remaining--;
            if (_remaining <= 0) { Answer(false); return; }
            _countdown.Text = $"This request expires in {_remaining}s — no answer means No.";
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
            Text = "EsoAgent — Owner password",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            TopMost = true,
            StartPosition = FormStartPosition.CenterScreen,
            Width = 380,
            Height = 170,
            RightToLeft = RightToLeft.Yes,
        };
        var label = new Label
        {
            Dock = DockStyle.Top,
            Height = 44,
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "استئناف الاتصال يتطلب كلمة مرور المالك:",
        };
        var box = new TextBox
        {
            Dock = DockStyle.Top,
            UseSystemPasswordChar = true,
            Font = new Font(FontFamily.GenericSansSerif, 11),
        };
        var ok = new Button { Text = "استئناف", DialogResult = DialogResult.OK, Dock = DockStyle.Bottom, Height = 36 };
        form.Controls.Add(ok);
        form.Controls.Add(box);
        form.Controls.Add(label);
        form.AcceptButton = ok;
        return form.ShowDialog() == DialogResult.OK ? box.Text : null;
    }
}
