using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Pipes;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using EsoAgent.Core;
using EsoAgent.Tray.MediaFoundation;

namespace EsoAgent.Tray;

/// <summary>
/// Tray-side remote-desktop session: runs on a dedicated tray-pipe connection
/// handed off by TrayContext. Capture: DXGI Desktop Duplication primary, GDI
/// fallback (any DXGI failure switches to GDI for the rest of the session).
/// Encoding: JPEG with unchanged-frame skip (content hash) and simple adaptive
/// quality — or, when the session negotiated "h264", Media Foundation H.264
/// (hardware MFT preferred) fed by the same capture + hash skip, with the
/// quality signal retuning the target bitrate. Input: SendInput. Clipboard: via the banner form's STA thread.
/// A persistent topmost banner ("جلسة تحكم نشطة — Active control session") is
/// shown for the whole session — the person at the machine cannot miss it.
/// Live verification is PENDING FIRST REAL INSTALL.
/// </summary>
public static class DesktopSessionHandler
{
    public static void Run(NamedPipeServerStream server, StreamReader reader, StreamWriter writer, JsonElement root, Func<bool> stopping)
    {
        var hello = root.TryGetProperty("hello", out var h)
            ? h.Deserialize<DesktopHello>(EsoJson.Options) ?? new DesktopHello()
            : new DesktopHello();
        var writeLock = new object();
        void WriteLine(string line)
        {
            lock (writeLock) { writer.WriteLine(line); writer.Flush(); }
        }

        var screens = Screen.AllScreens;
        if (screens.Length == 0)
        {
            WriteLine(new JsonObject { ["ok"] = false, ["error"] = "No displays found in the user session." }.ToJsonString(EsoJson.Options));
            return;
        }
        var monitors = screens.Select((s, i) => new MonitorInfo
        {
            Index = i, Name = s.DeviceName, Width = s.Bounds.Width, Height = s.Bounds.Height, Primary = s.Primary,
        }).ToList();
        var selected = monitors.FirstOrDefault(m => m.Index == hello.Monitor) ?? monitors[0];
        var bounds = screens[selected.Index].Bounds;

        // Negotiated codec (pipe root, chosen by the service from both
        // sides' offers). "h264" is created BEFORE the ok reply: an
        // encoder that will not configure refuses the session here with
        // a clear error, and the service then stops offering h264 for a
        // hold period — a session never starts on a codec the tray
        // cannot actually encode. Anything else (jpeg, or
        // a missing field from an older service) is the jpeg path,
        // unchanged. H.264 wants even dimensions; the encoder is
        // configured for the even-rounded monitor size and a frame that
        // arrives at any other size goes out as jpeg instead.
        var codec = root.TryGetProperty("codec", out var codecProp) ? codecProp.GetString() : null;
        IVideoEncoder? encoder = null;
        var encWidth = bounds.Width & ~1;
        var encHeight = bounds.Height & ~1;
        if (codec == VideoCodecNames.H264)
        {
            var quality0 = Math.Clamp(hello.Quality <= 0 ? 60 : hello.Quality, 10, 95);
            var fps0 = Math.Clamp(hello.MaxFps <= 0 ? 10 : hello.MaxFps, 1, 30);
            encoder = DesktopCapabilities.Factory.Create(encWidth, encHeight,
                H264Policy.BitrateFor(encWidth, encHeight, fps0, quality0), fps0,
                H264Policy.KeyFrameIntervalFrames(fps0));
            if (encoder == null)
            {
                WriteLine(new JsonObject { ["ok"] = false, ["error"] = "H.264 encoding is not available on this device (the encoder probe failed)." }.ToJsonString(EsoJson.Options));
                return;
            }
        }

        WriteLine(new JsonObject
        {
            ["ok"] = true,
            ["monitors"] = JsonSerializer.SerializeToNode(monitors, EsoJson.Options),
        }.ToJsonString(EsoJson.Options));

        // Banner on its own STA thread (also the clipboard thread).
        BannerForm? banner = null;
        var bannerReady = new ManualResetEventSlim();
        var bannerThread = new Thread(() =>
        {
            banner = new BannerForm();
            bannerReady.Set();
            Application.Run(banner);
        }) { IsBackground = true, Name = "EsoAgentDesktopBanner" };
        bannerThread.SetApartmentState(ApartmentState.STA);
        bannerThread.Start();
        bannerReady.Wait(3000);

        var closed = 0;
        var privacyOn = 0;
        // Read loop: input / clipboard / privacy / chat / close from the service.
        var readThread = new Thread(() =>
        {
            try
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    using var doc = JsonDocument.Parse(line);
                    var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
                    switch (type)
                    {
                        case "input":
                            // Privacy mode locks remote input injection too.
                            if (Volatile.Read(ref privacyOn) == 1) break;
                            var input = JsonSerializer.Deserialize<DesktopInput>(line, EsoJson.Options);
                            if (input != null) InputInjector.Inject(input, bounds);
                            break;
                        case "clipboard":
                            var clip = JsonSerializer.Deserialize<DesktopClipboard>(line, EsoJson.Options);
                            if (clip != null && banner != null)
                                banner.Invoke(() => { try { Clipboard.SetText(clip.Text); } catch { } });
                            break;
                        case "privacy":
                            var privacy = JsonSerializer.Deserialize<DesktopPrivacy>(line, EsoJson.Options);
                            if (privacy != null)
                            {
                                Interlocked.Exchange(ref privacyOn, privacy.Enabled ? 1 : 0);
                                banner?.Invoke(() => PrivacyOverlay.Set(privacy.Enabled));
                            }
                            break;
                        case "chat":
                            var chat = JsonSerializer.Deserialize<DesktopChat>(line, EsoJson.Options);
                            if (chat != null && chat.From == "operator" && banner != null)
                            {
                                banner.Invoke(() => ChatFormHost.ShowOperatorMessage(chat.Text, reply =>
                                    WriteLine(JsonSerializer.Serialize(new DesktopChat { Text = reply, From = "device" }, EsoJson.Options))));
                            }
                            break;
                        case "close":
                            Interlocked.Exchange(ref closed, 1);
                            return;
                    }
                }
            }
            catch { }
            Interlocked.Exchange(ref closed, 1);
        }) { IsBackground = true, Name = "EsoAgentDesktopRead" };
        readThread.Start();

        // Capture pump.
        var dxgi = new DxgiCapture(selected.Index);
        var useDxgi = dxgi.Initialize();
        if (useDxgi) dxgi.SetSize(bounds.Width, bounds.Height);
        var quality = Math.Clamp(hello.Quality <= 0 ? 60 : hello.Quality, 10, 95);
        var fps = Math.Clamp(hello.MaxFps <= 0 ? 10 : hello.MaxFps, 1, 30);
        var keyFrames = new KeyFrameScheduler(H264Policy.KeyFrameIntervalFrames(fps));
        var pumpStart = DateTime.UtcNow;
        byte[]? lastHash = null;
        long seq = 0;
        string? lastClipboard = null;
        var lastClipPoll = DateTime.MinValue;
        try
        {
            while (closed == 0 && !stopping())
            {
                var started = DateTime.UtcNow;
                Bitmap? bitmap = null;
                if (useDxgi)
                {
                    if (dxgi.TryCapture(out bitmap)) { /* ok */ }
                    else if (dxgi.LastError != null) { useDxgi = false; bitmap = CaptureGdi(bounds); }
                }
                else bitmap = CaptureGdi(bounds);

                if (bitmap != null)
                {
                    using (bitmap)
                    {
                        var hash = HashBitmap(bitmap);
                        if (lastHash == null || !hash.AsSpan().SequenceEqual(lastHash))
                        {
                            lastHash = hash;
                            if (encoder != null && bitmap.Width == encWidth && bitmap.Height == encHeight)
                            {
                                var (bgra, stride) = ExtractBgra(bitmap);
                                var raw = new RawVideoFrame
                                {
                                    Bgra = bgra, Width = bitmap.Width, Height = bitmap.Height,
                                    Stride = stride,
                                    TimestampMs = (long)(DateTime.UtcNow - pumpStart).TotalMilliseconds,
                                };
                                foreach (var packet in encoder.Encode(raw, keyFrames.NextForceKeyFrame()))
                                {
                                    keyFrames.ObservePacket(packet.IsKeyFrame);
                                    WriteLine(JsonSerializer.Serialize(
                                        H264Policy.ToDesktopFrame(packet, ++seq, bitmap.Width, bitmap.Height),
                                        EsoJson.Options));
                                }
                            }
                            else
                            {
                                var jpeg = EncodeJpeg(bitmap, quality);
                                WriteLine(JsonSerializer.Serialize(new DesktopFrame
                                {
                                    Seq = ++seq, Codec = "jpeg", Full = true,
                                    Width = bitmap.Width, Height = bitmap.Height,
                                    Data = Convert.ToBase64String(jpeg),
                                }, EsoJson.Options));
                            }
                        }
                    }
                }

                // Clipboard: device → client, polled.
                if (banner != null && DateTime.UtcNow - lastClipPoll > TimeSpan.FromSeconds(2))
                {
                    lastClipPoll = DateTime.UtcNow;
                    string? text = null;
                    try { banner.Invoke(() => { try { text = Clipboard.ContainsText() ? Clipboard.GetText() : null; } catch { } }); } catch { }
                    if (text != null && text != lastClipboard)
                    {
                        lastClipboard = text;
                        WriteLine(JsonSerializer.Serialize(new DesktopClipboard { Text = text }, EsoJson.Options));
                    }
                }

                // Adaptive quality: if the loop overruns its frame budget, step quality down.
                var elapsed = DateTime.UtcNow - started;
                var budget = TimeSpan.FromSeconds(1.0 / fps);
                if (elapsed > budget * 1.5 && quality > 30) quality -= 10;
                else if (elapsed < budget * 0.5 && quality < Math.Clamp(hello.Quality, 10, 95)) quality += 5;
                // For h264 the same quality signal retunes the target
                // bitrate (the encoder rebuilds only on a real change).
                if (encoder is IVideoEncoderBitrateControl bitrateControl)
                    bitrateControl.SetBitrate(H264Policy.BitrateFor(encWidth, encHeight, fps, quality));
                var wait = budget - elapsed;
                if (wait > TimeSpan.Zero) Thread.Sleep(wait);
            }
        }
        catch { /* session ends */ }
        finally
        {
            encoder?.Dispose();
            dxgi.Dispose();
            try { banner?.Invoke(() => { PrivacyOverlay.Set(false); ChatFormHost.Close(); banner.Close(); }); } catch { }
            try { server.Disconnect(); } catch { }
        }
    }

    private static Bitmap CaptureGdi(Rectangle bounds)
    {
        var bitmap = new Bitmap(bounds.Width, bounds.Height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        return bitmap;
    }

    private static byte[] HashBitmap(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[Math.Abs(data.Stride) * bitmap.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return SHA256.HashData(bytes);
        }
        finally { bitmap.UnlockBits(data); }
    }

    /// <summary>The frame's BGRA pixels for the video encoder — same
    /// LockBits read as the hash, kept as bytes + stride for Core's
    /// BGRA→NV12 conversion.</summary>
    private static (byte[] Bytes, int Stride) ExtractBgra(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = Math.Abs(data.Stride);
            var bytes = new byte[stride * bitmap.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return (bytes, stride);
        }
        finally { bitmap.UnlockBits(data); }
    }

    private static byte[] EncodeJpeg(Bitmap bitmap, int quality)
    {
        var codec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var ms = new MemoryStream();
        if (codec == null) { bitmap.Save(ms, ImageFormat.Jpeg); return ms.ToArray(); }
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
        bitmap.Save(ms, codec, parameters);
        return ms.ToArray();
    }
}

/// <summary>
/// Privacy-mode overlay (v1.2): one borderless black form per screen, topmost,
/// shown while privacy is on. The overlay holds the input focus (local mouse
/// clicks land on it and do nothing; the banner announces why), the remote
/// stream keeps flowing underneath. Set() must be called on a UI thread.
/// </summary>
public static class PrivacyOverlay
{
    private static readonly List<Form> Forms = new();

    public static void Set(bool enabled)
    {
        if (!enabled)
        {
            foreach (var form in Forms) { try { form.Close(); form.Dispose(); } catch { } }
            Forms.Clear();
            return;
        }
        if (Forms.Count > 0) return;
        foreach (var screen in Screen.AllScreens)
        {
            var form = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                Bounds = screen.Bounds,
                TopMost = true,
                ShowInTaskbar = false,
                BackColor = Color.Black,
            };
            var label = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 28,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.Gray,
                BackColor = Color.Black,
                Text = "Privacy mode — this screen is hidden by the operator · وضع الخصوصية",
            };
            form.Controls.Add(label);
            form.Show();
            Forms.Add(form);
        }
    }
}

/// <summary>
/// In-session chat window (v1.2): the operator's messages appear here; the
/// person at the device can type a reply, which the handler sends back over
/// the session channel. Lives on the banner's UI thread.
/// </summary>
public static class ChatFormHost
{
    private static ChatForm? _form;
    private static Action<string>? _reply;

    public static void ShowOperatorMessage(string text, Action<string> reply)
    {
        _reply = reply;
        if (_form == null || _form.IsDisposed)
        {
            _form = new ChatForm(t => _reply?.Invoke(t));
            _form.Show();
        }
        _form.Append("Operator", text);
        _form.BringToFront();
    }

    public static void Close()
    {
        try { _form?.Close(); } catch { }
        _form = null;
    }
}

public sealed class ChatForm : Form
{
    private readonly RichTextBox _log;
    private readonly TextBox _input;
    private readonly Action<string> _onSend;

    public ChatForm(Action<string> onSend)
    {
        _onSend = onSend;
        Text = "EsoAgent — session chat";
        Width = 380; Height = 300;
        StartPosition = FormStartPosition.Manual;
        var screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 800, 600);
        Location = new Point(screen.Right - Width - 12, screen.Bottom - Height - 12);
        _log = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true };
        _input = new TextBox { Dock = DockStyle.Bottom };
        var send = new Button { Text = "Send", Dock = DockStyle.Bottom, Height = 30 };
        send.Click += (_, _) => Send();
        _input.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { Send(); e.SuppressKeyPress = true; } };
        Controls.Add(_log);
        Controls.Add(send);
        Controls.Add(_input);
    }

    private void Send()
    {
        var text = _input.Text.Trim();
        if (text.Length == 0) return;
        _input.Clear();
        Append("You", text);
        _onSend(text);
    }

    public void Append(string who, string text)
    {
        _log.AppendText($"{who}: {text}{Environment.NewLine}");
    }
}

/// <summary>The unmissable on-screen indicator shown for the whole session.</summary>
public sealed class BannerForm : Form
{
    public BannerForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(180, 30, 30);
        var screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 800, 600);
        Width = 520; Height = 44;
        Location = new Point(screen.Left + (screen.Width - Width) / 2, screen.Top + 8);
        var label = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.White,
            Font = new Font(FontFamily.GenericSansSerif, 13, FontStyle.Bold),
            Text = "جلسة تحكم نشطة — Active control session",
        };
        Controls.Add(label);
    }
}

/// <summary>Mouse/keyboard injection via SendInput (+ SetCursorPos for absolute moves).</summary>
public static class InputInjector
{
    public static void Inject(DesktopInput input, Rectangle bounds)
    {
        switch (input.Kind)
        {
            case "mouseMove":
                SetCursorPos(bounds.Left + (int)(input.X * bounds.Width), bounds.Top + (int)(input.Y * bounds.Height));
                break;
            case "mouseButton":
            {
                SetCursorPos(bounds.Left + (int)(input.X * bounds.Width), bounds.Top + (int)(input.Y * bounds.Height));
                var flag = (input.Button, input.Down) switch
                {
                    ("right", true) => MOUSEEVENTF_RIGHTDOWN,
                    ("right", false) => MOUSEEVENTF_RIGHTUP,
                    ("middle", true) => MOUSEEVENTF_MIDDLEDOWN,
                    ("middle", false) => MOUSEEVENTF_MIDDLEUP,
                    (_, true) => MOUSEEVENTF_LEFTDOWN,
                    (_, false) => MOUSEEVENTF_LEFTUP,
                };
                SendMouse(flag, 0);
                break;
            }
            case "wheel":
                SendMouse(MOUSEEVENTF_WHEEL, input.Delta);
                break;
            case "key":
                InjectKey(input);
                break;
        }
    }

    private static void InjectKey(DesktopInput input)
    {
        var key = input.Key ?? "";
        if (key.Contains('+'))
        {
            var parts = key.Split('+').Select(p => p.Trim()).ToArray();
            if (parts.Any(p => p.Equals("delete", StringComparison.OrdinalIgnoreCase))
                && parts.Any(p => p.Equals("ctrl", StringComparison.OrdinalIgnoreCase))
                && parts.Any(p => p.Equals("alt", StringComparison.OrdinalIgnoreCase)))
            {
                // Secure Attention Sequence: only a SYSTEM service can normally
                // send it (group policy "Software SAS generation"). The tray is a
                // user process, so this attempt usually no-ops — documented limit.
                try { SendSAS(false); } catch { }
            }
            var vks = parts.Select(VirtualKey).Where(v => v != 0).ToArray();
            if (input.Down) foreach (var vk in vks) SendKey(vk, keyUp: false);
            else foreach (var vk in Enumerable.Reverse(vks)) SendKey(vk, keyUp: true);
            return;
        }
        var single = VirtualKey(key);
        if (single == 0 && key.Length == 1)
        {
            var scan = VkKeyScan(key[0]);
            if (scan >= 0) single = (ushort)(scan & 0xFF);
        }
        if (single != 0) SendKey(single, keyUp: !input.Down);
    }

    private static ushort VirtualKey(string name)
    {
        switch (name)
        {
            case "Enter": return 0x0D;
            case "Escape": return 0x1B;
            case "Tab": return 0x09;
            case "Backspace": return 0x08;
            case "Delete": return 0x2E;
            case "Insert": return 0x2D;
            case "Home": return 0x24;
            case "End": return 0x23;
            case "PageUp": return 0x21;
            case "PageDown": return 0x22;
            case "ArrowLeft": return 0x25;
            case "ArrowUp": return 0x26;
            case "ArrowRight": return 0x27;
            case "ArrowDown": return 0x28;
            case " ": return 0x20;
            case "Shift": return 0x10;
            case "Control": return 0x11;
            case "Alt": return 0x12;
            case "Meta": return 0x5B;
            case "CapsLock": return 0x14;
            case "PrintScreen": return 0x2C;
        }
        if (name.Length == 2 && name[0] == 'F' && char.IsDigit(name[1]))
            return (ushort)(0x70 + (name[1] - '1')); // F1..F9
        if (name.Length == 3 && name[0] == 'F' && name[1] == '1' && char.IsDigit(name[2]))
            return (ushort)(0x79 + (name[2] - '0')); // F10..F12
        if (name.Length == 1 && char.IsLetter(name[0]))
            return (ushort)char.ToUpperInvariant(name[0]);
        if (name.Length == 1 && char.IsDigit(name[0]))
            return (ushort)name[0];
        return 0;
    }

    private static void SendMouse(uint flags, int data)
    {
        var input = new INPUT
        {
            Type = INPUT_MOUSE,
            Union = new InputUnion { Mouse = new MOUSEINPUT { Dx = 0, Dy = 0, MouseData = data, Flags = flags, Time = 0, ExtraInfo = IntPtr.Zero } },
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static void SendKey(ushort vk, bool keyUp)
    {
        var input = new INPUT
        {
            Type = INPUT_KEYBOARD,
            Union = new InputUnion { Keyboard = new KEYBDINPUT { Vk = vk, Scan = 0, Flags = keyUp ? KEYEVENTF_KEYUP : 0, Time = 0, ExtraInfo = IntPtr.Zero } },
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int Dx; public int Dy; public int MouseData; public uint Flags; public uint Time; public IntPtr ExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort Vk; public ushort Scan; public uint Flags; public uint Time; public IntPtr ExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint Type; public InputUnion Union; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern short VkKeyScan(char ch);

    [DllImport("sas.dll")]
    private static extern void SendSAS(bool asUser);
}
