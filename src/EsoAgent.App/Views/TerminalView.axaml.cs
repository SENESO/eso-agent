using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using EsoAgent.App.Services;
using EsoAgent.Client;
using EsoAgent.Core;

namespace EsoAgent.App.Views;

/// <summary>
/// Terminal tab (Phase 3): a REAL persistent session. The shell process
/// lives on the AGENT (ShellSessionService, v1.2) — named, long-lived,
/// with cwd/environment continuity — and this view is only an attach
/// client: it creates the session on first use, polls its
/// sequence-numbered output, and posts input lines. Switching devices or
/// tabs unbinds the view; the session state is kept in
/// TerminalSessionStore and the agent-side shell keeps running, so
/// coming back reattaches and catches up from the last seen sequence —
/// nothing is torn down with the view. Devices whose agent has no
/// session service fall back to the one-shot command runner this tab
/// used to be (direct: streamed exec; fleet/cmd: POST /api/shell).
/// </summary>
public sealed partial class TerminalView : UserControl
{
    private static readonly string[] Shells = { "bash", "sh", "powershell", "pwsh", "cmd" };
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(700);

    private ClientConfig? _cfg;
    private DeviceItem? _device;
    private TerminalSessionState? _state;
    private Transport? _transport;
    private DispatcherTimer? _pollTimer;
    private bool _polling;
    private int _pollFailures;
    private int _historyIndex = -1;
    private CancellationTokenSource? _running;   // one-shot fallback only

    public TerminalView()
    {
        InitializeComponent();
        ShellBox.ItemsSource = Shells;
        ShellBox.SelectedItem = OperatingSystem.IsWindows() ? "powershell" : "bash";
    }

    public void Attach(ClientConfig cfg, DeviceItem device)
    {
        Unbind();
        _cfg = cfg;
        _device = device;
        _state = TerminalSessionStore.For(cfg, device);
        ShellBox.SelectedItem = _state.Shell;
        OutputBox.Text = _state.Buffer.Text;
        if (OutputBox.Text.Length > 0) OutputBox.CaretIndex = OutputBox.Text.Length;
        _historyIndex = -1;
        ApplyLanguage();
        UpdateChrome();
        if (!_state.UseFallback) _ = EnsureSessionAsync();
    }

    /// <summary>Called by the main window when it closes: stop polling and
    /// release the transport. The agent-side session keeps running.</summary>
    public void Detach() => Unbind();

    private void Unbind()
    {
        _pollTimer?.Stop();
        _pollTimer = null;
        _transport?.Dispose();
        _transport = null;
        _running?.Cancel();
    }

    public void ApplyLanguage()
    {
        ShellLabel.Text = Strings.T("ShellLabel");
        RestartButton.Content = Strings.T("RestartSession");
        EndButton.Content = Strings.T("EndSession");
        CancelButton.Content = Strings.T("Cancel");
        RunButton.Content = Strings.T("Run");
        UpdateChrome();
    }

    private Transport Transport =>
        _transport ??= EsoAgent.Client.Transport.Create(_cfg!, _device!.IsFleet ? _device.Code : null, null, false);

    // ---------- session lifecycle ----------

    private async Task EnsureSessionAsync()
    {
        var state = _state;
        if (state == null || _cfg == null || _device == null) return;
        if (!state.Created)
        {
            ModeText.Text = Strings.T("SessionStarting");
            try
            {
                var create = await Transport.SendAsync("POST", "/api/sessions",
                    new ShellSessionCreateRequest { Name = state.SessionName, Shell = state.Shell });
                if (create.Count > 0 && create[0].Ok)
                {
                    state.Created = true;
                    state.Running = true;
                }
                else
                {
                    // Maybe the session already exists (created by an earlier
                    // run of the app): list and adopt it. Only when the list
                    // itself fails is the session service truly absent.
                    var list = await Transport.SendAsync("GET", "/api/sessions");
                    if (list.Count > 0 && list[0].Ok)
                    {
                        var sessions = list[0].Json<List<ShellSessionDto>>();
                        var found = sessions.Find(s => s.Name == state.SessionName);
                        if (found != null)
                        {
                            state.Created = true;
                            state.Running = found.Running;
                        }
                        else state.Note = create[0].Error ?? create[0].Text;
                    }
                    else state.UseFallback = true;
                }
            }
            catch (Exception) { state.Note = Strings.T("SessionStartFailedPlain"); }
        }
        if (state.Created && state.Running) StartPolling();
        UpdateChrome();
    }

    private void StartPolling()
    {
        if (_pollTimer == null)
        {
            _pollTimer = new DispatcherTimer { Interval = PollInterval };
            _pollTimer.Tick += async (_, _) => await PollAsync();
        }
        if (!_pollTimer.IsEnabled) _pollTimer.Start();
        _ = PollAsync();
    }

    private async Task PollAsync()
    {
        var state = _state;
        if (_polling || state is not { Created: true } || _cfg == null) return;
        _polling = true;
        try
        {
            var replies = await Transport.SendAsync("GET",
                $"/api/sessions/{Uri.EscapeDataString(state.SessionName)}?since={state.Buffer.LastSeq}",
                null, timeoutSeconds: 30);
            if (replies.Count > 0 && replies[0].Ok)
            {
                _pollFailures = 0;
                if (state.Note != null) { state.Note = null; UpdateChrome(); }
                var output = replies[0].Json<ShellSessionOutputResponse>();
                state.Running = output.Running;
                if (state.Buffer.Append(output.Chunks) > 0 && ReferenceEquals(_state, state))
                {
                    OutputBox.Text = state.Buffer.Text;
                    OutputBox.CaretIndex = OutputBox.Text.Length;
                }
                if (!output.Running)
                {
                    _pollTimer?.Stop();
                    UpdateChrome();
                }
            }
            else if (++_pollFailures >= 3)
            {
                _pollTimer?.Stop();
                state.Note = replies.Count > 0 ? replies[0].Error ?? replies[0].Text : "no reply";
                UpdateChrome();
            }
        }
        catch { if (++_pollFailures >= 3) { _pollTimer?.Stop(); UpdateChrome(); } }
        finally { _polling = false; }
    }

    private async void OnEndClick(object? sender, RoutedEventArgs e)
    {
        var state = _state;
        if (state is not { Created: true }) return;
        _pollTimer?.Stop();
        try
        {
            await Transport.SendAsync("DELETE", $"/api/sessions/{Uri.EscapeDataString(state.SessionName)}");
        }
        catch { /* the session state below is the local truth either way */ }
        state.Created = false;
        state.Running = false;
        UpdateChrome();
    }

    private async void OnRestartClick(object? sender, RoutedEventArgs e)
    {
        var state = _state;
        if (state == null) return;
        _pollTimer?.Stop();
        _pollTimer = null;
        try
        {
            await Transport.SendAsync("DELETE", $"/api/sessions/{Uri.EscapeDataString(state.SessionName)}");
        }
        catch { /* restarting anyway */ }
        state.Created = false;
        state.Running = false;
        state.Buffer.Clear();
        OutputBox.Text = "";
        await EnsureSessionAsync();
    }

    private void OnShellChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_state == null) return;
        if (ShellBox.SelectedItem is string shell) _state.Shell = shell;
    }

    private void UpdateChrome()
    {
        var state = _state;
        if (state == null) return;
        if (state.UseFallback)
        {
            ModeText.Text = Strings.T("SessionUnavailable");
            EndButton.IsEnabled = false;
            RestartButton.IsEnabled = false;
            return;
        }
        EndButton.IsEnabled = state.Created;
        RestartButton.IsEnabled = state.Created;
        ModeText.Text = state.Note
            ?? (state.Running ? Strings.T("SessionLive")
                : state.Created ? Strings.T("SessionEnded")
                : Strings.T("SessionStarting"));
    }

    // ---------- input ----------

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        var state = _state;
        if (state != null && state is { UseFallback: false })
        {
            if (e.Key == Key.Up && state.History.Count > 0)
            {
                _historyIndex = _historyIndex < 0 ? state.History.Count - 1 : Math.Max(0, _historyIndex - 1);
                InputBox.Text = state.History[_historyIndex];
                InputBox.CaretIndex = InputBox.Text.Length;
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Down && _historyIndex >= 0)
            {
                _historyIndex = _historyIndex + 1 >= state.History.Count ? -1 : _historyIndex + 1;
                InputBox.Text = _historyIndex < 0 ? "" : state.History[_historyIndex];
                InputBox.CaretIndex = InputBox.Text.Length;
                e.Handled = true;
                return;
            }
        }
        if (e.Key == Key.Enter) { Run(); e.Handled = true; }
    }

    private void OnRunClick(object? sender, RoutedEventArgs e) => Run();

    private void OnCancelClick(object? sender, RoutedEventArgs e) => _running?.Cancel();

    private void Run()
    {
        var state = _state;
        if (_cfg == null || _device == null || state == null) return;
        var line = InputBox.Text ?? "";
        if (state is { UseFallback: false, Created: true })
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            InputBox.Text = "";
            _historyIndex = -1;
            if (state.History.Count == 0 || state.History[^1] != line) state.History.Add(line);
            _ = SendInputAsync(state, line);
        }
        else if (state.UseFallback)
        {
            _ = RunOneShotAsync(line.Trim());
        }
        else
        {
            // Session still starting (or failed to start): try once more.
            _ = EnsureSessionAsync();
        }
    }

    private async Task SendInputAsync(TerminalSessionState state, string line)
    {
        try
        {
            var replies = await Transport.SendAsync("POST",
                $"/api/sessions/{Uri.EscapeDataString(state.SessionName)}/input",
                new ShellSessionInputRequest { Data = line + "\n" }, timeoutSeconds: 30);
            if (replies.Count == 0 || !replies[0].Ok)
            {
                state.Note = replies.Count > 0 ? replies[0].Error ?? replies[0].Text : "no reply";
                UpdateChrome();
            }
            else _ = PollAsync();   // don't wait for the next tick to show the echo
        }
        catch (Exception) { state.Note = Strings.T("SessionStartFailedPlain"); UpdateChrome(); }
    }

    // ---------- one-shot fallback (the pre-Phase-3 behaviour) ----------

    private async Task RunOneShotAsync(string script)
    {
        if (_cfg == null || _device == null || _running != null || string.IsNullOrEmpty(script)) return;
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
                using var transport = EsoAgent.Client.Transport.Create(_cfg, _device.IsFleet ? _device.Code : null, null, false);
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
        catch (Exception) { Append("\n[error: command failed to run]\n"); }
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
