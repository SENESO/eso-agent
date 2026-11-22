using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace EsoAgent.Tray;

/// <summary>
/// The host window (UI Phase 5, gap G16): what the person AT the device
/// sees — TeamViewer shows the host a big code, a status and a way to stop
/// everything; EsoAgent's host had a tray menu and nothing else. One
/// window: the device code at reading size with a Copy button, the device
/// name, the mesh address (copyable), live status, the last command, the
/// kill switch, and the pending update with an Apply button (the local
/// control pipe's apply-update action — the same path the API uses).
/// Everything refreshes from the control pipe's status on a 3 s timer,
/// the tray's own cadence.
/// </summary>
public sealed class HostWindow : Form
{
    private readonly Func<Task<JsonObject?>> _statusProvider;
    private readonly Func<Task<JsonObject?>> _applyUpdate;
    private readonly Action _toggleLock;
    private readonly Func<bool> _isControlled;

    private readonly Label _codeLabel = new();
    private readonly Label _nameValue = new();
    private readonly Label _meshValue = new();
    private readonly Label _statusValue = new();
    private readonly Label _lastValue = new();
    private readonly Label _updateLabel = new();
    private readonly Button _applyButton = new();
    private readonly Button _killButton = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 3000 };
    private string? _deviceCode;
    private string? _meshIp;
    private bool _locked;
    private bool _applying;

    public HostWindow(
        Func<Task<JsonObject?>> statusProvider,
        Func<Task<JsonObject?>> applyUpdate,
        Action toggleLock,
        Func<bool> isControlled)
    {
        _statusProvider = statusProvider;
        _applyUpdate = applyUpdate;
        _toggleLock = toggleLock;
        _isControlled = isControlled;

        Text = TrayStrings.HostTitle;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Width = 540;
        Height = 470;
        RightToLeft = TrayStrings.IsArabic ? RightToLeft.Yes : RightToLeft.No;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(18),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        root.Controls.Add(new Label
        {
            Text = TrayStrings.DeviceCodeLabel,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 2),
        });
        _codeLabel.Font = new Font(FontFamily.GenericSansSerif, 30, FontStyle.Bold);
        _codeLabel.AutoSize = true;
        _codeLabel.Text = "…";
        root.Controls.Add(_codeLabel);

        var codeRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 2, 0, 10) };
        var copyCode = new Button { Text = TrayStrings.Copy, AutoSize = true };
        copyCode.Click += (_, _) => { if (_deviceCode != null) Clipboard.SetText(_deviceCode); };
        var copyMesh = new Button { Text = TrayStrings.Copy, AutoSize = true };
        copyMesh.Click += (_, _) => { if (_meshIp != null) Clipboard.SetText(_meshIp); };
        codeRow.Controls.Add(copyCode);
        root.Controls.Add(codeRow);

        root.Controls.Add(FieldRow(TrayStrings.DeviceNameLabel, _nameValue));
        var meshRow = FieldRow(TrayStrings.MeshLabel, _meshValue);
        meshRow.Controls.Add(copyMesh);
        root.Controls.Add(meshRow);
        root.Controls.Add(FieldRow(TrayStrings.StatusLabel, _statusValue));
        root.Controls.Add(FieldRow(TrayStrings.LastCommand("").Split(':')[0], _lastValue));

        var updateRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 12, 0, 0) };
        _updateLabel.AutoSize = true;
        _updateLabel.Margin = new Padding(0, 6, 10, 0);
        _applyButton.AutoSize = true;
        _applyButton.Click += async (_, _) => await ApplyAsync();
        updateRow.Controls.Add(_updateLabel);
        updateRow.Controls.Add(_applyButton);
        root.Controls.Add(updateRow);

        var buttonRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 14, 0, 0) };
        _killButton.AutoSize = true;
        _killButton.Click += (_, _) => _toggleLock();
        var closeButton = new Button { Text = TrayStrings.Close, AutoSize = true };
        closeButton.Click += (_, _) => Close();
        buttonRow.Controls.Add(_killButton);
        buttonRow.Controls.Add(closeButton);
        root.Controls.Add(buttonRow);

        _timer.Tick += async (_, _) => await RefreshAsync();
        Shown += async (_, _) => { await RefreshAsync(); _timer.Start(); };
        FormClosed += (_, _) => _timer.Dispose();
    }

    private static FlowLayoutPanel FieldRow(string label, Label value)
    {
        var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 3, 0, 0) };
        row.Controls.Add(new Label { Text = label + ":", AutoSize = true, Margin = new Padding(0, 0, 8, 0) });
        value.AutoSize = true;
        row.Controls.Add(value);
        return row;
    }

    private async Task RefreshAsync()
    {
        JsonObject? reply;
        try { reply = await _statusProvider(); }
        catch { reply = null; }
        if (reply == null)
        {
            _statusValue.Text = TrayStrings.StatusServiceDown;
            return;
        }
        _locked = reply["locked"]?.GetValue<bool>() ?? false;
        _deviceCode = reply["deviceCode"]?.GetValue<string>();
        _meshIp = reply["meshIp"]?.GetValue<string>();
        var fleetConnected = reply["fleetConnected"]?.GetValue<bool>() ?? false;
        var update = reply["updateAvailable"]?.GetValue<string>();

        _codeLabel.Text = _deviceCode ?? "—";
        _nameValue.Text = reply["deviceName"]?.GetValue<string>() ?? "—";
        _meshValue.Text = _meshIp ?? "—";
        _statusValue.Text = _isControlled() ? TrayStrings.StatusControlled
            : _locked ? TrayStrings.StatusLocked
            : fleetConnected ? TrayStrings.StatusConnected
            : TrayStrings.StatusStandalone;
        _lastValue.Text = reply["lastCommand"]?.GetValue<string>() ?? "—";
        _killButton.Text = _locked ? TrayStrings.Resume : TrayStrings.LockNow;
        if (!_applying)
        {
            _updateLabel.Text = update != null ? TrayStrings.UpdateReady(update) : TrayStrings.NoUpdate;
            _applyButton.Text = TrayStrings.ApplyUpdate;
            _applyButton.Enabled = update != null;
        }
    }

    private async Task ApplyAsync()
    {
        if (_applying) return;
        _applying = true;
        _applyButton.Enabled = false;
        _applyButton.Text = TrayStrings.Applying;
        try
        {
            var reply = await _applyUpdate();
            var message = reply?["message"]?.GetValue<string>()
                          ?? reply?["error"]?.GetValue<string>()
                          ?? TrayStrings.ServiceUnreachable;
            _updateLabel.Text = message;
        }
        catch { _updateLabel.Text = TrayStrings.ServiceUnreachable; }
        finally
        {
            _applying = false;
            await RefreshAsync();
        }
    }
}
