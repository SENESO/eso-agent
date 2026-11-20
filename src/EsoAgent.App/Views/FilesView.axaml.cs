using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using EsoAgent.App.Services;
using EsoAgent.Client;
using EsoAgent.Core;

namespace EsoAgent.App.Views;

/// <summary>Human file sizes for the file panes and the transfer queue.</summary>
internal static class FileSizeText
{
    public static string Of(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / 1048576.0:F1} MB",
        _ => $"{bytes / 1073741824.0:F2} GB",
    };
}

/// <summary>One row in the remote (device) pane.</summary>
public sealed class FileEntryItem
{
    public required FileEntryDto Entry { get; init; }
    public string Display => Entry.IsDir
        ? $"{Entry.Name}/"
        : $"{Entry.Name}    ({FileSizeText.Of(Entry.Size ?? 0)})";
}

/// <summary>One row in the local (this computer) pane.</summary>
public sealed class LocalEntryItem
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required bool IsDir { get; init; }
    public long Size { get; init; }
    public string Display => IsDir ? $"{Name}/" : $"{Name}    ({FileSizeText.Of(Size)})";
}

/// <summary>View model for one row of the transfer queue dock: mirrors a
/// Core TransferJob, adding a smoothed speed from successive samples.</summary>
public sealed class TransferItemView : INotifyPropertyChanged
{
    private long _lastBytes;
    private long _lastTick;
    private double _speed;

    public TransferItemView(TransferJob job)
    {
        Job = job;
        _lastBytes = job.BytesDone;
        _lastTick = Environment.TickCount64;
        Detail = "";
        Refresh();
    }

    public TransferJob Job { get; }
    public string Title => (Job.Direction == TransferDirection.Upload ? "↑ " : "↓ ") + Job.Name;
    public double Percent => Job.Fraction * 100;
    public bool CanCancel => Job.State is TransferState.Queued or TransferState.Running;
    public string Detail { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Refresh()
    {
        var now = Environment.TickCount64;
        var dt = (now - _lastTick) / 1000.0;
        if (dt >= 0.5)
        {
            _speed = (Job.BytesDone - _lastBytes) / dt;
            _lastBytes = Job.BytesDone;
            _lastTick = now;
        }
        var state = Job.State switch
        {
            TransferState.Queued => Strings.T("TransferQueued"),
            TransferState.Running => Strings.T("TransferRunning"),
            TransferState.Completed => Strings.T("TransferCompleted"),
            TransferState.Cancelled => Strings.T("TransferCancelled"),
            _ => Strings.T("TransferFailed") + (string.IsNullOrEmpty(Job.Error) ? "" : $": {Job.Error}"),
        };
        Detail = Job.TotalBytes > 0
            ? $"{FileSizeText.Of(Job.BytesDone)} {Strings.T("OfBytes")} {FileSizeText.Of(Job.TotalBytes)}"
              + (Job.State == TransferState.Running && _speed > 1 ? $" · {FileSizeText.Of((long)_speed)}/s" : "")
              + $" · {state}"
            : state;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Percent)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Detail)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanCancel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
    }
}

/// <summary>
/// Files tab (Phase 3) — a dual-pane file manager over the EXISTING
/// transfer protocol: this computer on the left, the device on the right,
/// drag &amp; drop both ways (and from the OS onto the device pane), and a
/// transfer queue dock with per-file progress, speed and cancel, driven
/// by Core's TransferQueue/TransferEngine (chunked, resumable, SHA-256
/// verified — a cancelled upload's partial stays on the device and the
/// next attempt resumes it). Remote housekeeping (delete / rename / new
/// folder) is unchanged. F4 is now: select in one pane → one click (or
/// drag) — no OS dialogs.
/// </summary>
public sealed partial class FilesView : UserControl
{
    private const string LocalFormat = "EsoAgent.LocalFiles";
    private const string RemoteFormat = "EsoAgent.RemoteFiles";

    private ClientConfig? _cfg;
    private DeviceItem? _device;
    private string _path = "";
    private string? _parent;
    private string _localPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private readonly TransferQueue _queue = new();
    private readonly ObservableCollection<TransferItemView> _queueViews = new();

    private Point _localPress;
    private Point _remotePress;
    private bool _dragging;

    public FilesView()
    {
        InitializeComponent();
        QueueList.ItemsSource = _queueViews;
        _queue.JobChanged += OnJobChanged;
        WireDrag(LocalList, isLocal: true);
        WireDrag(EntriesList, isLocal: false);
        LoadLocal(_localPath);
        ApplyLanguage();
    }

    public void Attach(ClientConfig cfg, DeviceItem device)
    {
        _cfg = cfg;
        _device = device;
        _path = "";
        StateText.Text = "";
        RemoteLabel.Text = device.Title;
        ApplyLanguage();
        _ = LoadAsync("");
    }

    public void ApplyLanguage()
    {
        RefreshButton.Content = Strings.T("Refresh");
        UploadButton.Content = Strings.T("Upload");
        DownloadButton.Content = Strings.T("Download");
        DeleteButton.Content = Strings.T("Delete");
        RenameButton.Content = Strings.T("Rename");
        MkdirButton.Content = Strings.T("NewFolder");
        UpButton.Content = Strings.T("Up");
        LocalUpButton.Content = Strings.T("Up");
        LocalLabel.Text = Strings.T("ThisComputer");
        TransfersLabel.Text = Strings.T("Transfers");
        ClearFinishedButton.Content = Strings.T("ClearFinished");
        foreach (var vm in _queueViews) vm.Refresh();
    }

    private Transport NewTransport() =>
        Transport.Create(_cfg!, _device!.IsFleet ? _device.Code : null, null, false);

    /// <summary>A channel factory bound to the CURRENTLY attached device —
    /// captured per enqueue, so a queued job keeps talking to the device it
    /// was started for even if the view is re-attached meanwhile.</summary>
    private Func<ITransferChannel> ChannelFactory()
    {
        var cfg = _cfg!;
        var device = _device!;
        return () => new TransportChannelAdapter(
            Transport.Create(cfg, device.IsFleet ? device.Code : null, null, false));
    }

    // ---------- remote pane ----------

    private async Task LoadAsync(string path)
    {
        if (_cfg == null || _device == null) return;
        StateText.Text = "Loading…";
        try
        {
            using var transport = NewTransport();
            var replies = await transport.SendAsync("GET", $"/api/files/list?path={Uri.EscapeDataString(path)}");
            var reply = replies[0];
            if (!reply.Ok) { StateText.Text = $"List failed: {reply.Error ?? reply.Text}"; return; }
            var list = reply.Json<FileListResponse>();
            _path = list.Path;
            _parent = list.Parent;
            PathBox.Text = _path;
            EntriesList.ItemsSource = list.Entries.Select(e => new FileEntryItem { Entry = e }).ToList();
            StateText.Text = $"{list.Entries.Count} item(s) · {DateTime.Now:T}";
        }
        catch (Exception ex) { StateText.Text = $"List failed: {ex.Message}"; }
    }

    private async void OnRefreshClick(object? sender, RoutedEventArgs e) => await LoadAsync(_path);

    private async void OnUpClick(object? sender, RoutedEventArgs e)
    {
        if (_parent != null) await LoadAsync(_parent);
    }

    private async void OnPathKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { await LoadAsync(PathBox.Text ?? ""); e.Handled = true; }
    }

    private void OnEntryDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (EntriesList.SelectedItem is not FileEntryItem item) return;
        if (item.Entry.IsDir) _ = LoadAsync(item.Entry.Path);
        else EnqueueDownload(item.Entry);
    }

    private FileEntryItem? Selected => EntriesList.SelectedItem as FileEntryItem;

    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (_cfg == null || Selected is not { } item) { StateText.Text = "Select an item first."; return; }
        try
        {
            using var transport = NewTransport();
            var replies = await transport.SendAsync("POST", "/api/files/delete", new FilePathRequest { Path = item.Entry.Path });
            StateText.Text = replies[0].Ok ? $"Deleted {item.Entry.Name}." : $"Delete failed: {replies[0].Error ?? replies[0].Text}";
            if (replies[0].Ok) await LoadAsync(_path);
        }
        catch (Exception ex) { StateText.Text = $"Delete failed: {ex.Message}"; }
    }

    private async void OnRenameClick(object? sender, RoutedEventArgs e)
    {
        if (_cfg == null || Selected is not { } item) { StateText.Text = "Select an item first."; return; }
        var name = await PromptAsync("Rename", "New name:", item.Entry.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        var target = JoinRemote(_path, name.Trim());
        try
        {
            using var transport = NewTransport();
            var replies = await transport.SendAsync("POST", "/api/files/rename", new FileRenameRequest { Path = item.Entry.Path, NewPath = target });
            StateText.Text = replies[0].Ok ? $"Renamed to {name}." : $"Rename failed: {replies[0].Error ?? replies[0].Text}";
            if (replies[0].Ok) await LoadAsync(_path);
        }
        catch (Exception ex) { StateText.Text = $"Rename failed: {ex.Message}"; }
    }

    private async void OnMkdirClick(object? sender, RoutedEventArgs e)
    {
        if (_cfg == null) return;
        var name = await PromptAsync("New folder", "Folder name:", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            using var transport = NewTransport();
            var replies = await transport.SendAsync("POST", "/api/files/mkdir", new FilePathRequest { Path = JoinRemote(_path, name.Trim()) });
            StateText.Text = replies[0].Ok ? $"Created folder {name}." : $"New folder failed: {replies[0].Error ?? replies[0].Text}";
            if (replies[0].Ok) await LoadAsync(_path);
        }
        catch (Exception ex) { StateText.Text = $"New folder failed: {ex.Message}"; }
    }

    private static string JoinRemote(string dir, string name)
    {
        if (string.IsNullOrEmpty(dir)) return name;
        var sep = dir.Contains('\\') && !dir.EndsWith('\\') ? "\\" : dir.EndsWith('/') || dir.EndsWith('\\') ? "" : "/";
        return dir + sep + name;
    }

    /// <summary>Minimal one-field prompt dialog (name entry for rename/mkdir).</summary>
    private async Task<string?> PromptAsync(string title, string label, string initial)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner == null) return null;
        var box = new TextBox { Text = initial, MinWidth = 320 };
        var dialog = new Window
        {
            Title = title,
            Width = 380,
            Height = 170,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(14),
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = label },
                    box,
                },
            },
        };
        var ok = new Button { Content = "OK", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        ok.Click += (_, _) => dialog.Close(box.Text);
        ((StackPanel)dialog.Content).Children.Add(ok);
        return await dialog.ShowDialog<string?>(owner);
    }

    // ---------- local pane ----------

    private void LoadLocal(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            if (!info.Exists) { StateText.Text = $"No such folder: {path}"; return; }
            _localPath = info.FullName;
            LocalPathBox.Text = _localPath;
            var items = new List<LocalEntryItem>();
            foreach (var d in info.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
                items.Add(new LocalEntryItem { Name = d.Name, FullPath = d.FullName, IsDir = true });
            foreach (var f in info.EnumerateFiles().OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
                items.Add(new LocalEntryItem { Name = f.Name, FullPath = f.FullName, IsDir = false, Size = f.Length });
            LocalList.ItemsSource = items;
        }
        catch (Exception ex) { StateText.Text = $"Local list failed: {ex.Message}"; }
    }

    private void OnLocalUpClick(object? sender, RoutedEventArgs e)
    {
        var parent = Directory.GetParent(_localPath);
        if (parent != null) LoadLocal(parent.FullName);
    }

    private void OnLocalPathKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { LoadLocal(LocalPathBox.Text ?? ""); e.Handled = true; }
    }

    private void OnLocalDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (LocalList.SelectedItem is not LocalEntryItem item) return;
        if (item.IsDir) LoadLocal(item.FullPath);
        else EnqueueUpload(item.FullPath);
    }

    // ---------- transfers ----------

    private void EnqueueUpload(string localPath)
    {
        if (_cfg == null || _device == null) { StateText.Text = "Select a device first."; return; }
        var name = Path.GetFileName(localPath);
        _queue.Enqueue(TransferDirection.Upload, name, localPath, JoinRemote(_path, name), ChannelFactory());
    }

    private void EnqueueDownload(FileEntryDto entry) => EnqueueDownload(entry.Path, entry.Name);

    private void EnqueueDownload(string remotePath, string? name = null)
    {
        if (_cfg == null || _device == null) { StateText.Text = "Select a device first."; return; }
        name ??= remotePath.Split('/', '\\')[^1];
        _queue.Enqueue(TransferDirection.Download, name, Path.Combine(_localPath, name), remotePath, ChannelFactory());
    }

    private void OnUploadClick(object? sender, RoutedEventArgs e)
    {
        var files = (LocalList.SelectedItems ?? Array.Empty<object>()).OfType<LocalEntryItem>()
            .Where(i => !i.IsDir).ToList();
        if (files.Count == 0) { StateText.Text = "Select local file(s) first."; return; }
        foreach (var f in files) EnqueueUpload(f.FullPath);
    }

    private void OnDownloadClick(object? sender, RoutedEventArgs e)
    {
        var files = (EntriesList.SelectedItems ?? Array.Empty<object>()).OfType<FileEntryItem>()
            .Where(i => !i.Entry.IsDir).ToList();
        if (files.Count == 0) { StateText.Text = "Select device file(s) first."; return; }
        foreach (var f in files) EnqueueDownload(f.Entry);
    }

    private void OnJobChanged(TransferJob job)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var vm = _queueViews.FirstOrDefault(v => v.Job.Id == job.Id);
            if (vm == null)
            {
                vm = new TransferItemView(job);
                _queueViews.Add(vm);
            }
            vm.Refresh();
            if (job.State == TransferState.Completed)
            {
                // Show the result where it landed.
                if (job.Direction == TransferDirection.Upload) _ = LoadAsync(_path);
                else LoadLocal(_localPath);
            }
        });
    }

    private void OnQueueCancelClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TransferItemView vm }) vm.Job.Cancel();
    }

    private void OnClearFinishedClick(object? sender, RoutedEventArgs e)
    {
        _queue.ClearFinished();
        var live = _queue.Jobs.Select(j => j.Id).ToHashSet();
        for (var i = _queueViews.Count - 1; i >= 0; i--)
            if (!live.Contains(_queueViews[i].Job.Id)) _queueViews.RemoveAt(i);
    }

    // ---------- drag & drop ----------

    private static readonly DataFormat<string> LocalFilesFormat =
        DataFormat.CreateStringApplicationFormat(LocalFormat);
    private static readonly DataFormat<string> RemoteFilesFormat =
        DataFormat.CreateStringApplicationFormat(RemoteFormat);

    private void WireDrag(ListBox list, bool isLocal)
    {
        DragDrop.SetAllowDrop(list, true);
        list.AddHandler(DragDrop.DragOverEvent, (object? _, DragEventArgs e) => OnPaneDragOver(e, isLocal));
        list.AddHandler(DragDrop.DropEvent, (object? _, DragEventArgs e) => OnPaneDrop(e, isLocal));
        list.PointerPressed += (_, e) =>
        {
            if (isLocal) _localPress = e.GetPosition(list);
            else _remotePress = e.GetPosition(list);
        };
        list.PointerMoved += async (_, e) => await MaybeStartDragAsync(e, list, isLocal);
    }

    private async Task MaybeStartDragAsync(PointerEventArgs e, ListBox list, bool isLocal)
    {
        if (_dragging || !e.GetCurrentPoint(list).Properties.IsLeftButtonPressed) return;
        var start = isLocal ? _localPress : _remotePress;
        var pos = e.GetPosition(list);
        if (Math.Abs(pos.X - start.X) < 6 && Math.Abs(pos.Y - start.Y) < 6) return;
        var selected = (list.SelectedItems ?? Array.Empty<object>()).Cast<object>().ToList();
        if (selected.Count == 0) return;
        var transfer = new DataTransfer();
        if (isLocal)
        {
            var paths = selected.OfType<LocalEntryItem>().Where(i => !i.IsDir).Select(i => i.FullPath).ToList();
            if (paths.Count == 0) return;
            transfer.Add(DataTransferItem.Create(LocalFilesFormat, string.Join("\n", paths)));
        }
        else
        {
            var paths = selected.OfType<FileEntryItem>().Where(i => !i.Entry.IsDir).Select(i => i.Entry.Path).ToList();
            if (paths.Count == 0) return;
            transfer.Add(DataTransferItem.Create(RemoteFilesFormat, string.Join("\n", paths)));
        }
        _dragging = true;
        // The transfer is NOT disposed here — the drag source owns it and
        // disposes it when the operation completes (IDataTransfer contract).
        try { await DragDrop.DoDragDropAsync(e, transfer, DragDropEffects.Copy); }
        catch { /* a cancelled drag is not an error */ }
        finally { _dragging = false; }
    }

    private void OnPaneDragOver(DragEventArgs e, bool isLocal)
    {
        var ok = isLocal
            ? e.DataTransfer.Formats.Contains(RemoteFilesFormat)
            : e.DataTransfer.Formats.Contains(LocalFilesFormat) || e.DataTransfer.Formats.Contains(DataFormat.File);
        e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnPaneDrop(DragEventArgs e, bool isLocal)
    {
        if (isLocal)
        {
            if (e.DataTransfer.TryGetValue(RemoteFilesFormat) is { } s)
                foreach (var remotePath in s.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    EnqueueDownload(remotePath);
        }
        else
        {
            if (e.DataTransfer.TryGetValue(LocalFilesFormat) is { } s)
            {
                foreach (var localPath in s.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    EnqueueUpload(localPath);
            }
            else
            {
                foreach (var item in e.DataTransfer.Items)
                {
                    if (item.TryGetValue(DataFormat.File) is { } file)
                    {
                        var p = file.TryGetLocalPath();
                        if (p != null && File.Exists(p)) EnqueueUpload(p);
                    }
                }
            }
        }
        e.Handled = true;
    }
}
