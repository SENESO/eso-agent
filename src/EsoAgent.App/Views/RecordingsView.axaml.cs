using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using EsoAgent.Client;
using EsoAgent.Core;

namespace EsoAgent.App.Views;

/// <summary>One row in the Recordings list.</summary>
public sealed class RecordingItem
{
    public required RecordingInfoDto Info { get; init; }
    public string Title => Info.Name;
    public string DetailLine =>
        $"{Info.StartedAt.LocalDateTime:yyyy-MM-dd HH:mm} · {FileSizeText.Of(Info.SizeBytes)} · {Info.Codec} {Info.Width}×{Info.Height}";
    public string PlayLabel => Strings.T("Play");
    public string DownloadLabel => Strings.T("Download");
    public string DeleteLabel => Strings.T("Delete");
}

/// <summary>
/// Recordings tab (Phase 4) — the .esorec session recordings stored on the
/// device, finally watchable: Play downloads the file and opens the player
/// window (frames are timestamped JSON lines; jpeg draws directly, rawdiff
/// composites through Core's RawDiffCodec — the same decode the live
/// desktop view uses). Download saves the raw file; Delete removes it on
/// the device.
/// </summary>
public sealed partial class RecordingsView : UserControl
{
    private ClientConfig? _cfg;
    private DeviceItem? _device;

    public RecordingsView()
    {
        InitializeComponent();
        ApplyLanguage();
    }

    public void Attach(ClientConfig cfg, DeviceItem device)
    {
        _cfg = cfg;
        _device = device;
        StateText.Text = "";
        ApplyLanguage();
        _ = LoadAsync();
    }

    public void ApplyLanguage() => RefreshButton.Content = Strings.T("Refresh");

    private Transport NewTransport() =>
        Transport.Create(_cfg!, _device!.IsFleet ? _device.Code : null, null, false);

    private async Task LoadAsync()
    {
        if (_cfg == null || _device == null) return;
        StateText.Text = "Loading…";
        try
        {
            using var transport = NewTransport();
            var reply = (await transport.SendAsync("GET", "/api/recordings"))[0];
            if (!reply.Ok)
            {
                StateText.Text = $"Recordings failed: {reply.Error ?? reply.Text}";
                return;
            }
            var list = reply.Json<List<RecordingInfoDto>>();
            RecordingsList.ItemsSource = list.Select(r => new RecordingItem { Info = r }).ToList();
            EmptyText.IsVisible = list.Count == 0;
            StateText.Text = $"{list.Count} recording(s) · {DateTime.Now:T}";
        }
        catch (Exception ex) { StateText.Text = $"Recordings failed: {ex.Message}"; }
    }

    private async void OnRefreshClick(object? sender, RoutedEventArgs e) => await LoadAsync();

    private async Task<byte[]?> FetchAsync(string name)
    {
        using var transport = NewTransport();
        var reply = (await transport.SendAsync("GET", $"/api/recordings/{Uri.EscapeDataString(name)}", null, 600))[0];
        if (!reply.Ok)
        {
            StateText.Text = $"Fetch failed: {reply.Error ?? reply.Text}";
            return null;
        }
        return reply.Body;
    }

    private async void OnRowPlayClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: RecordingItem item } || _cfg == null) return;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner == null) return;
        StateText.Text = $"Fetching {item.Info.Name}…";
        var bytes = await FetchAsync(item.Info.Name);
        if (bytes == null) return;
        StateText.Text = "";
        var player = new RecordingPlayerWindow(item.Info.Name, bytes);
        player.Show(owner);
    }

    private async void OnRowDownloadClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: RecordingItem item } || _cfg == null) return;
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save recording",
            SuggestedFileName = item.Info.Name,
            FileTypeChoices = new[] { new FilePickerFileType("EsoAgent recording") { Patterns = new[] { "*.esorec" } } },
        });
        if (file == null) return;
        var bytes = await FetchAsync(item.Info.Name);
        if (bytes == null) return;
        await using var stream = await file.OpenWriteAsync();
        await stream.WriteAsync(bytes);
        StateText.Text = $"Saved {item.Info.Name} ({FileSizeText.Of(bytes.Length)}).";
    }

    private async void OnRowDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: RecordingItem item } || _cfg == null) return;
        try
        {
            using var transport = NewTransport();
            var reply = (await transport.SendAsync("DELETE", $"/api/recordings/{Uri.EscapeDataString(item.Info.Name)}"))[0];
            StateText.Text = reply.Ok ? $"Deleted {item.Info.Name}." : $"Delete failed: {reply.Error ?? reply.Text}";
            if (reply.Ok) await LoadAsync();
        }
        catch (Exception ex) { StateText.Text = $"Delete failed: {ex.Message}"; }
    }
}

/// <summary>
/// The .esorec player: play/pause, seek slider, frame-accurate stepping via
/// the timestamps in the file. Rawdiff seeking restarts from the last full
/// frame at or before the target — the same rule the web player uses.
/// </summary>
public sealed class RecordingPlayerWindow : Window
{
    private readonly List<RecordingFrameLine> _frames;
    private readonly Image _image = new() { Stretch = Avalonia.Media.Stretch.Uniform };
    private readonly Button _playButton = new();
    private readonly Slider _seek = new();
    private readonly TextBlock _timeText = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly Stopwatch _clock = new();
    private int _idx = -1;
    private int _rendered = -1;
    private RawFrame? _prevRaw;
    private WriteableBitmap? _rawBitmap;
    private Bitmap? _jpegBitmap;
    private long _baseMs;
    private bool _seeking;

    public RecordingPlayerWindow(string name, byte[] bytes)
    {
        Title = $"{Strings.T("PlayerTitle")} — {name}";
        Width = 980;
        Height = 640;

        var tmp = Path.Combine(Path.GetTempPath(), "eso-play-" + Guid.NewGuid().ToString("N") + ".esorec");
        File.WriteAllBytes(tmp, bytes);
        try { _frames = SessionRecorder.Parse(tmp).Frames; }
        finally { try { File.Delete(tmp); } catch { } }

        _seek.Minimum = 0;
        _seek.Maximum = Math.Max(0, _frames.Count - 1);
        _seek.Width = 480;
        _playButton.Content = Strings.T("Pause");
        _playButton.Click += OnPlayPauseClick;
        _seek.ValueChanged += OnSeekChanged;
        _timer.Tick += OnTick;

        var bar = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(0, 8, 0, 0),
            Children = { _playButton, _seek, _timeText },
        };
        DockPanel.SetDock(bar, Dock.Bottom);
        Content = new DockPanel
        {
            Margin = new Thickness(10),
            Children = { bar, _image },
        };

        Opened += (_, _) =>
        {
            if (_frames.Count == 0)
            {
                _timeText.Text = "This recording has no playable frames.";
                _playButton.IsEnabled = false;
                return;
            }
            Render(0);
            _baseMs = _frames[0].AtMs;
            _clock.Start();
            _timer.Start();
        };
        Closed += (_, _) => { _timer.Stop(); _clock.Stop(); };
    }

    private static string Fmt(long ms)
    {
        var s = ms / 1000;
        return $"{s / 60}:{s % 60:D2}";
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_frames.Count == 0) return;
        var target = _baseMs + _clock.ElapsedMilliseconds;
        var next = _idx;
        while (next + 1 < _frames.Count && _frames[next + 1].AtMs <= target) next++;
        if (next != _idx) Render(next);
        if (_idx >= _frames.Count - 1)
        {
            _timer.Stop();
            _clock.Stop();
            _playButton.Content = Strings.T("Play");
        }
    }

    private void OnPlayPauseClick(object? sender, RoutedEventArgs e)
    {
        if (_timer.IsEnabled)
        {
            _timer.Stop();
            _clock.Stop();
            _playButton.Content = Strings.T("Play");
        }
        else
        {
            if (_idx >= _frames.Count - 1)
            {
                _rendered = -1;
                _prevRaw = null;
                Render(0);
            }
            _baseMs = _frames[Math.Max(0, _idx)].AtMs;
            _clock.Restart();
            _timer.Start();
            _playButton.Content = Strings.T("Pause");
        }
    }

    private void OnSeekChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_seeking || _frames.Count == 0) return;
        _timer.Stop();
        _clock.Stop();
        _playButton.Content = Strings.T("Play");
        Render((int)e.NewValue);
    }

    private void Render(int i)
    {
        if (i < 0 || i >= _frames.Count) return;
        int from;
        if (_rendered < i && _frames[i].Codec != "rawdiff") from = i;
        else if (_rendered < i && _rendered >= 0 && i - _rendered <= 90) from = _rendered + 1;
        else
        {
            from = i;
            while (from > 0 && !_frames[from].Full) from--;
            _rendered = from - 1;
            _prevRaw = null;
        }
        for (var k = from; k <= i; k++)
        {
            DrawFrame(_frames[k]);
            _rendered = k;
        }
        _idx = i;
        _seeking = true;
        _seek.Value = i;
        _seeking = false;
        _timeText.Text = $"{Fmt(_frames[i].AtMs)} / {Fmt(_frames[^1].AtMs)}";
    }

    private void DrawFrame(RecordingFrameLine frame)
    {
        try
        {
            var data = Convert.FromBase64String(frame.Data);
            if (frame.Codec == "jpeg")
            {
                var bitmap = new Bitmap(new MemoryStream(data));
                var old = _jpegBitmap;
                _jpegBitmap = bitmap;
                _image.Source = bitmap;
                old?.Dispose();
            }
            else if (frame.Codec == "rawdiff")
            {
                var decoded = RawDiffCodec.Decode(data, frame.Full ? null : _prevRaw);
                _prevRaw = decoded;
                if (_rawBitmap == null || _rawBitmap.PixelSize.Width != decoded.Width || _rawBitmap.PixelSize.Height != decoded.Height)
                {
                    _rawBitmap?.Dispose();
                    _rawBitmap = new WriteableBitmap(
                        new PixelSize(decoded.Width, decoded.Height), new Vector(96, 96),
                        PixelFormat.Bgra8888, AlphaFormat.Premul);
                    _image.Source = _rawBitmap;
                }
                using var fb = _rawBitmap.Lock();
                var rowBytes = decoded.Width * 4;
                for (var y = 0; y < decoded.Height; y++)
                    Marshal.Copy(decoded.Bgra, y * rowBytes, fb.Address + y * fb.RowBytes, rowBytes);
                _image.InvalidateVisual();
            }
        }
        catch { /* a torn frame is dropped; the next keyframe repairs the picture */ }
    }
}
