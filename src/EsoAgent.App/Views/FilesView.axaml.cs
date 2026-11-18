using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using EsoAgent.Client;
using EsoAgent.Core;

namespace EsoAgent.App.Views;

/// <summary>One row in the file manager.</summary>
public sealed class FileEntryItem
{
    public required FileEntryDto Entry { get; init; }
    public string Display => Entry.IsDir
        ? $"{Entry.Name}/"
        : $"{Entry.Name}    ({Entry.Size / 1024.0:F1} KB)";
}

/// <summary>
/// Files tab (v1.2) — a real file manager over the EXISTING transfer layer:
/// directory listing + chunked resumable upload (SHA-256 commit) + ranged
/// download + delete/rename/mkdir, all through the shared Transport, so it
/// works identically direct and via the fleet relay. A human never needs
/// the CLI for files.
/// </summary>
public sealed partial class FilesView : UserControl
{
    private ClientConfig? _cfg;
    private DeviceItem? _device;
    private string _path = "";
    private string? _parent;

    public FilesView() => InitializeComponent();

    public void Attach(ClientConfig cfg, DeviceItem device)
    {
        _cfg = cfg;
        _device = device;
        _path = "";
        StateText.Text = "";
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
    }

    private Transport NewTransport() =>
        Transport.Create(_cfg!, _device!.IsFleet ? _device.Code : null, null, false);

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
        if (EntriesList.SelectedItem is FileEntryItem { Entry.IsDir: true } item)
            _ = LoadAsync(item.Entry.Path);
    }

    private FileEntryItem? Selected => EntriesList.SelectedItem as FileEntryItem;

    private async void OnUploadClick(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null || _cfg == null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Upload to device", AllowMultiple = false });
        var picked = files.FirstOrDefault();
        if (picked == null) return;
        var localPath = picked.TryGetLocalPath();
        if (localPath == null) { StateText.Text = "That file has no local path."; return; }
        var remote = JoinRemote(_path, Path.GetFileName(localPath));
        StateText.Text = $"Uploading {Path.GetFileName(localPath)}…";
        try
        {
            using var transport = NewTransport();
            var size = new FileInfo(localPath).Length;
            var sha = FileTransferService.HashFile(localPath);
            var begin = await transport.SendAsync("POST", "/api/files/begin", new BeginUploadRequest { Path = remote });
            if (begin.Count == 0 || !begin[0].Ok) throw new InvalidOperationException(begin.Count == 0 ? "no reply" : begin[0].Error ?? begin[0].Text);
            var offset = begin[0].Json<BeginUploadResponse>().Received;
            if (offset > size) offset = 0;
            var chunkSize = transport.IsFleet ? 256 * 1024 : 4 * 1024 * 1024;
            await using var fs = File.OpenRead(localPath);
            fs.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[chunkSize];
            var current = offset;
            int read;
            while ((read = await fs.ReadAsync(buffer)) > 0)
            {
                var chunk = read == buffer.Length ? buffer : buffer[..read];
                var rs = await transport.SendRawAsync("PUT", $"/api/files/chunk?path={Uri.EscapeDataString(remote)}&offset={current}", chunk, 300);
                if (rs.Count == 0 || !rs[0].Ok) throw new InvalidOperationException(rs.Count == 0 ? "no reply" : rs[0].Error ?? rs[0].Text);
                current = rs[0].Json<ChunkResponse>().Received;
            }
            var commit = await transport.SendAsync("POST", "/api/files/commit", new CommitUploadRequest { Path = remote, Sha256 = sha });
            if (commit.Count == 0 || !commit[0].Ok) throw new InvalidOperationException(commit.Count == 0 ? "no reply" : commit[0].Error ?? commit[0].Text);
            StateText.Text = $"Uploaded {size} bytes, SHA-256 verified.";
            await LoadAsync(_path);
        }
        catch (Exception ex) { StateText.Text = $"Upload failed: {ex.Message}"; }
    }

    private async void OnDownloadClick(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null || _cfg == null || Selected is not { Entry.IsDir: false } item) { StateText.Text = "Select a file first."; return; }
        var save = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Download from device",
            SuggestedFileName = item.Entry.Name,
        });
        var localPath = save?.TryGetLocalPath();
        if (localPath == null) return;
        StateText.Text = $"Downloading {item.Entry.Name}…";
        try
        {
            using var transport = NewTransport();
            var infoReplies = await transport.SendAsync("GET", $"/api/files/info?path={Uri.EscapeDataString(item.Entry.Path)}");
            var info = infoReplies[0].Json<FileInfoResponse>();
            await using var outFs = new FileStream(localPath, FileMode.Create, FileAccess.Write);
            long offset = 0;
            const int chunk = 1024 * 1024;
            while (offset < info.Size)
            {
                var length = Math.Min(chunk, info.Size - offset);
                var rs = await transport.SendRawAsync("GET", $"/api/files/download?path={Uri.EscapeDataString(item.Entry.Path)}&offset={offset}&length={length}", Array.Empty<byte>(), 300);
                if (rs.Count == 0 || !rs[0].Ok) throw new InvalidOperationException(rs.Count == 0 ? "no reply" : rs[0].Error ?? rs[0].Text);
                await outFs.WriteAsync(rs[0].Body);
                offset += rs[0].Body.Length;
            }
            await outFs.FlushAsync();
            var verified = info.Sha256 == null || string.Equals(info.Sha256, FileTransferService.HashFile(localPath), StringComparison.OrdinalIgnoreCase);
            StateText.Text = $"Downloaded {info.Size} bytes · SHA-256 verified: {verified}";
        }
        catch (Exception ex) { StateText.Text = $"Download failed: {ex.Message}"; }
    }

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
}
