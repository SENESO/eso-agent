using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using EsoAgent.Client;
using EsoAgent.Core;

namespace EsoAgent.App.Views;

/// <summary>One row in the Tasks list: a schedule definition plus its last-run state.</summary>
public sealed class TaskItem
{
    public required ScheduledTaskConfig Config { get; init; }
    public ScheduleStateDto? State { get; init; }

    public string Title => $"{Config.Name}  ·  {State?.Description ?? SchedulePolicy.Describe(Config)}  ·  {(Config.Enabled ? Strings.T("Enabled") : Strings.T("Disabled"))}";
    public string CommandLine => (Config.Shell != null ? $"[{Config.Shell}] " : "") + Config.Command;
    public string StateLine
    {
        get
        {
            if (State?.LastRunAt == null) return $"last run: {Strings.T("Never")}";
            var last = State.LastRunAt.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
            var result = State.LastError ?? (State.LastExitCode != null ? $"exit {State.LastExitCode}" : "—");
            return $"last run {last} · {result}";
        }
    }

    public string RunLabel => Strings.T("RunNow");
    public string EditLabel => Strings.T("Edit");
    public string DeleteLabel => Strings.T("Delete");
}

/// <summary>
/// Tasks tab (Phase 4) — the device's own scheduled tasks, editable at last:
/// the list is read as configs + state, edited as one document through
/// PUT /api/schedules (validated on the device, persisted to its config,
/// applied live), with Run-now per task. Before this tab the only way to
/// change a schedule was editing esoagent.json on the device by hand.
/// </summary>
public sealed partial class TasksView : UserControl
{
    private ClientConfig? _cfg;
    private DeviceItem? _device;
    private List<ScheduledTaskConfig> _configs = new();

    public TasksView()
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

    public void ApplyLanguage()
    {
        RefreshButton.Content = Strings.T("Refresh");
        NewButton.Content = Strings.T("NewTask");
        Render();
    }

    private Transport NewTransport() =>
        Transport.Create(_cfg!, _device!.IsFleet ? _device.Code : null, null, false);

    private async Task LoadAsync()
    {
        if (_cfg == null || _device == null) return;
        StateText.Text = "Loading…";
        try
        {
            using var transport = NewTransport();
            var cfgReply = (await transport.SendAsync("GET", "/api/schedules/config"))[0];
            if (!cfgReply.Ok)
            {
                StateText.Text = $"Tasks failed: {cfgReply.Error ?? cfgReply.Text}";
                return;
            }
            var stReply = (await transport.SendAsync("GET", "/api/schedules"))[0];
            _configs = cfgReply.Json<List<ScheduledTaskConfig>>();
            var states = stReply.Ok ? stReply.Json<List<ScheduleStateDto>>() : new List<ScheduleStateDto>();
            Render(states);
            StateText.Text = $"{_configs.Count} task(s) · {DateTime.Now:T}";
        }
        catch (Exception ex) { StateText.Text = $"Tasks failed: {ex.Message}"; }
    }

    private List<ScheduleStateDto> _lastStates = new();

    private void Render() => Render(_lastStates);

    private void Render(List<ScheduleStateDto> states)
    {
        _lastStates = states;
        var byName = states.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        TasksList.ItemsSource = _configs
            .Select(c => new TaskItem { Config = c, State = byName.TryGetValue(c.Name, out var s) ? s : null })
            .ToList();
        EmptyText.IsVisible = _configs.Count == 0;
    }

    private async void OnRefreshClick(object? sender, RoutedEventArgs e) => await LoadAsync();

    private async Task<bool> SaveAsync(List<ScheduledTaskConfig> list, string okText)
    {
        try
        {
            using var transport = NewTransport();
            var reply = (await transport.SendAsync("PUT", "/api/schedules", list))[0];
            if (!reply.Ok)
            {
                StateText.Text = $"Save failed: {reply.Error ?? reply.Text}";
                return false;
            }
            _configs = list;
            StateText.Text = okText;
            await LoadAsync();
            return true;
        }
        catch (Exception ex)
        {
            StateText.Text = $"Save failed: {ex.Message}";
            return false;
        }
    }

    private async void OnNewClick(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner == null || _cfg == null) return;
        var created = await new TaskEditorWindow(null).ShowDialog<ScheduledTaskConfig?>(owner);
        if (created == null) return;
        if (_configs.Any(c => string.Equals(c.Name, created.Name, StringComparison.OrdinalIgnoreCase)))
        {
            StateText.Text = $"A task named '{created.Name}' already exists.";
            return;
        }
        await SaveAsync(new List<ScheduledTaskConfig>(_configs) { created }, $"Task '{created.Name}' created.");
    }

    private async void OnRowEditClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TaskItem item }) return;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner == null) return;
        var edited = await new TaskEditorWindow(item.Config).ShowDialog<ScheduledTaskConfig?>(owner);
        if (edited == null) return;
        var list = _configs.Select(c => string.Equals(c.Name, item.Config.Name, StringComparison.OrdinalIgnoreCase) ? edited : c).ToList();
        await SaveAsync(list, $"Task '{edited.Name}' saved.");
    }

    private async void OnRowDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TaskItem item }) return;
        var list = _configs.Where(c => !string.Equals(c.Name, item.Config.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        await SaveAsync(list, $"Task '{item.Config.Name}' deleted.");
    }

    private async void OnRowRunClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TaskItem item } || _cfg == null) return;
        try
        {
            using var transport = NewTransport();
            var reply = (await transport.SendAsync("POST", $"/api/schedules/{Uri.EscapeDataString(item.Config.Name)}/run"))[0];
            StateText.Text = reply.Ok ? $"Task '{item.Config.Name}' started." : $"Run failed: {reply.Error ?? reply.Text}";
            if (reply.Ok)
            {
                await Task.Delay(2000);
                await LoadAsync();
            }
        }
        catch (Exception ex) { StateText.Text = $"Run failed: {ex.Message}"; }
    }
}

/// <summary>The task editor: name (new only), shell, command, interval-or-daily
/// schedule, enabled. Mirrors the web panels' form and the device's own
/// validation rules, so what it accepts is what the device accepts.</summary>
public sealed class TaskEditorWindow : Window
{
    private readonly TextBox _nameBox = new();
    private readonly ComboBox _shellBox = new();
    private readonly TextBox _commandBox = new();
    private readonly ComboBox _kindBox = new();
    private readonly TextBox _intervalBox = new();
    private readonly TextBox _dailyBox = new();
    private readonly CheckBox _enabledBox = new();
    private readonly TextBlock _errorText = new();
    private readonly ScheduledTaskConfig? _existing;

    public TaskEditorWindow(ScheduledTaskConfig? existing)
    {
        _existing = existing;
        Title = Strings.T(existing == null ? "TaskEditorNew" : "TaskEditorEdit");
        Width = 460;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var c = existing ?? new ScheduledTaskConfig
        {
            Enabled = true, Shell = "bash", Command = "", IntervalMinutes = 60, TimeoutMs = 600_000,
        };

        _nameBox.Text = c.Name;
        _nameBox.Watermark = Strings.T("Name");
        _nameBox.IsEnabled = existing == null;
        _shellBox.ItemsSource = new[] { "", "powershell", "pwsh", "cmd", "bash", "sh" };
        _shellBox.SelectedItem = c.Shell ?? "";
        _commandBox.Text = c.Command;
        _commandBox.AcceptsReturn = true;
        _commandBox.Height = 90;
        _kindBox.ItemsSource = new[] { Strings.T("EveryMinutes"), Strings.T("DailyAt") };
        _kindBox.SelectedIndex = c.DailyAt != null ? 1 : 0;
        _intervalBox.Text = (c.IntervalMinutes ?? 60).ToString();
        _dailyBox.Text = c.DailyAt ?? "07:30";
        _enabledBox.Content = Strings.T("Enabled");
        _enabledBox.IsChecked = c.Enabled;
        _errorText.Foreground = Avalonia.Media.Brushes.OrangeRed;

        var save = new Button { Content = Strings.T("Save"), HorizontalAlignment = HorizontalAlignment.Right };
        save.Click += OnSaveClick;
        var cancel = new Button { Content = Strings.T("Cancel"), HorizontalAlignment = HorizontalAlignment.Right };
        cancel.Click += (_, _) => Close(null);

        Content = new StackPanel
        {
            Margin = new Thickness(14),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = Strings.T("Name") }, _nameBox,
                new TextBlock { Text = Strings.T("ShellLabel") }, _shellBox,
                new TextBlock { Text = Strings.T("CommandLabel") }, _commandBox,
                new TextBlock { Text = Strings.T("ScheduleLabel") }, _kindBox,
                new TextBlock { Text = Strings.T("EveryMinutes") }, _intervalBox,
                new TextBlock { Text = Strings.T("DailyAt") }, _dailyBox,
                _enabledBox,
                _errorText,
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, save },
                },
            },
        };
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var daily = _kindBox.SelectedIndex == 1;
        var built = new ScheduledTaskConfig
        {
            Name = (_nameBox.Text ?? "").Trim(),
            Enabled = _enabledBox.IsChecked == true,
            Shell = string.IsNullOrEmpty(_shellBox.SelectedItem as string) ? null : _shellBox.SelectedItem as string,
            Command = (_commandBox.Text ?? "").Trim(),
            Args = _existing?.Args ?? Array.Empty<string>(),
            Cwd = _existing?.Cwd,
            Env = _existing?.Env,
            IntervalMinutes = daily ? null : int.TryParse(_intervalBox.Text, out var i) ? i : null,
            DailyAt = daily ? (_dailyBox.Text ?? "").Trim() : null,
            TimeoutMs = _existing?.TimeoutMs ?? 600_000,
        };
        if (!daily && built.IntervalMinutes == null)
        {
            _errorText.Text = "Interval must be a whole number of minutes.";
            return;
        }
        if (ScheduleValidation.ValidateOne(built) is { } error)
        {
            _errorText.Text = error;
            return;
        }
        Close(built);
    }
}
