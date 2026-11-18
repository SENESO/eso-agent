using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using EsoAgent.App.Services;
using EsoAgent.Client;
using EsoAgent.Core;

namespace EsoAgent.App.Views;

/// <summary>
/// Terminal tab: runs shell scripts on the device through the real API.
/// Direct device → live-streamed over /api/exec/stream (the shell is resolved
/// with Core's own ProcessRunner.ResolveShell, exactly as the agent does for
/// /api/shell). Fleet device (or the cmd shell, which needs a server-side temp
/// file) → POST /api/shell through the shared transport, output shown whole.
/// </summary>
public sealed partial class TerminalView : UserControl
{
    private static readonly string[] Shells = { "bash", "sh", "powershell", "pwsh", "cmd" };

    private ClientConfig? _cfg;
    private DeviceItem? _device;
    private CancellationTokenSource? _running;

    public TerminalView()
    {
        InitializeComponent();
        ShellBox.ItemsSource = Shells;
        ShellBox.SelectedItem = OperatingSystem.IsWindows() ? "powershell" : "bash";
    }

    public void Attach(ClientConfig cfg, DeviceItem device)
    {
        _cfg = cfg;
        _device = device;
        ModeText.Text = device.IsFleet
            ? $"via fleet relay (device {device.Code}) — output appears when the command finishes"
            : "direct — output streams live";
        if (device.Os.Equals("Windows", StringComparison.OrdinalIgnoreCase)) ShellBox.SelectedItem = "powershell";
        else if (device.Os.Equals("Linux", StringComparison.OrdinalIgnoreCase)) ShellBox.SelectedItem = "bash";
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Run(); e.Handled = true; }
    }

    private void OnRunClick(object? sender, RoutedEventArgs e) => Run();

    private void OnCancelClick(object? sender, RoutedEventArgs e) => _running?.Cancel();

    private async void Run()
    {
        if (_cfg == null || _device == null || _running != null) return;
        var script = InputBox.Text?.Trim();
        if (string.IsNullOrEmpty(script)) return;
        var shell = ShellBox.SelectedItem as string ?? "bash";
        InputBox.Text = "";
        Append($"\n$ {script}\n");
        SetRunning(true);
        _running = new CancellationTokenSource();
        try
        {
            if (!_device.IsFleet && shell != "cmd")
            {
                var execReq = ProcessRunner.ResolveShell(new ShellRequest
                {
                    Shell = shell,
                    Script = script,
                    TimeoutMs = 120_000,
                });
                var exit = await ExecStreamClient.RunAsync(_cfg, execReq,
                    (stream, data) => Dispatcher.UIThread.Post(() => Append(data)), _running.Token);
                Append($"\n[exit {exit}]\n");
            }
            else
            {
                using var transport = Transport.Create(_cfg, _device.IsFleet ? _device.Code : null, null, false);
                var replies = await transport.SendAsync("POST", "/api/shell",
                    new ShellRequest { Shell = shell, Script = script, TimeoutMs = 120_000 });
                var reply = replies[0];
                if (!reply.Ok)
                {
                    Append($"[failed: {reply.Error ?? $"HTTP {reply.Status}: {reply.Text}"}]\n");
                }
                else
                {
                    var res = reply.Json<ExecResponse>();
                    Append(res.Stdout);
                    Append(res.Stderr);
                    if (res.TimedOut) Append("[timed out]\n");
                    if (res.Truncated) Append("[output truncated at capture cap]\n");
                    Append($"[exit {res.ExitCode?.ToString() ?? "?"}]\n");
                }
            }
        }
        catch (OperationCanceledException) { Append("\n[cancelled]\n"); }
        catch (Exception ex) { Append($"\n[error: {ex.Message}]\n"); }
        finally
        {
            _running.Dispose();
            _running = null;
            SetRunning(false);
        }
    }

    private void SetRunning(bool running)
    {
        RunButton.IsEnabled = !running;
        InputBox.IsEnabled = !running;
        CancelButton.IsVisible = running && _device is { IsFleet: false };
    }

    private void Append(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        OutputBox.Text = (OutputBox.Text ?? "") + text;
        OutputBox.CaretIndex = OutputBox.Text.Length;
    }
}
