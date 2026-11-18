using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using EsoAgent.Client;
using EsoAgent.Core;

namespace EsoAgent.App.Views;

/// <summary>
/// Activity tab (v1.2) — the SAME unified timeline the fleet panel and
/// `eso activity` show: device audit events shipped by agents, fleet-server
/// events, operator actions, owner logins, queue/job/supervision events and
/// alerts, merged by the fleet server. Without a fleet, it falls back to the
/// direct device's own audit log (the same lines, before aggregation).
/// </summary>
public sealed partial class ActivityView : UserControl
{
    private ClientConfig? _cfg;
    private DeviceItem? _device;
    private List<ActivityEvent> _events = new();

    public ActivityView() => InitializeComponent();

    public void Attach(ClientConfig cfg, DeviceItem device)
    {
        _cfg = cfg;
        _device = device;
        BodyText.Text = "Press “Refresh”.";
        StateText.Text = "";
        ApplyLanguage();
    }

    public void ApplyLanguage()
    {
        RefreshButton.Content = Strings.T("Refresh");
        ExportButton.Content = Strings.T("ExportJson");
    }

    private async void OnRefreshClick(object? sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        if (_cfg == null || _device == null) return;
        RefreshButton.IsEnabled = false;
        StateText.Text = "Loading…";
        try
        {
            if (!string.IsNullOrWhiteSpace(_cfg.FleetUrl))
            {
                using var fleet = EsoAgent.Client.Program.CreateFleetClient(_cfg);
                _events = await fleet.GetActivityAsync(new ActivityQuery
                {
                    Device = _device.IsFleet ? _device.Code : null,
                    Limit = 300,
                });
                var sb = new StringBuilder();
                foreach (var ev in _events.AsEnumerable().Reverse())
                    sb.AppendLine($"{ev.At.LocalDateTime:yyyy-MM-dd HH:mm:ss}  [{ev.Source}]  {ev.DeviceName ?? ev.DeviceCode ?? "—"}  {ev.Kind}: {ev.Summary}  ({ev.Result})");
                BodyText.Text = _events.Count == 0 ? "(no activity yet)" : sb.ToString();
                StateText.Text = _device.IsFleet
                    ? $"{_events.Count} event(s) for {_device.Name} · updated {DateTime.Now:T}"
                    : $"{_events.Count} fleet-wide event(s) · updated {DateTime.Now:T}";
            }
            else
            {
                using var transport = Transport.Create(_cfg, null, null, false);
                var replies = await transport.SendAsync("GET", "/api/logs/agent?lines=300");
                var reply = replies[0];
                if (!reply.Ok) { BodyText.Text = $"Activity failed: {reply.Error ?? reply.Text}"; StateText.Text = "Failed"; return; }
                using var doc = JsonDocument.Parse(reply.Body);
                var lines = doc.RootElement.GetProperty("lines").EnumerateArray().Select(x => x.GetString() ?? "").ToList();
                _events = new List<ActivityEvent>();
                BodyText.Text = lines.Count == 0 ? "(agent audit log is empty)" : string.Join("\n", lines.AsEnumerable().Reverse());
                StateText.Text = $"Direct device audit log · {lines.Count} line(s) · updated {DateTime.Now:T}";
            }
        }
        catch (Exception ex)
        {
            BodyText.Text = $"Activity failed: {ex.Message}";
            StateText.Text = "Failed";
        }
        finally { RefreshButton.IsEnabled = true; }
    }

    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export activity (JSON)",
            SuggestedFileName = $"eso-activity-{DateTime.Now:yyyyMMdd-HHmm}.json",
            FileTypeChoices = new[] { new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } } },
        });
        if (file == null) return;
        await using var stream = await file.OpenWriteAsync();
        await JsonSerializer.SerializeAsync(stream, _events, new JsonSerializerOptions { WriteIndented = true });
        StateText.Text = $"Exported {_events.Count} event(s) to {file.Name}";
    }
}
