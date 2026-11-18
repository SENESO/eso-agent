using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using EsoAgent.Client;
using EsoAgent.Core;

namespace EsoAgent.App.Views;

/// <summary>Device status tab: GET /api/status via the shared CLI transport (direct or fleet relay).</summary>
public sealed partial class StatusView : UserControl
{
    private ClientConfig? _cfg;
    private DeviceItem? _device;

    public StatusView() => InitializeComponent();

    public void Attach(ClientConfig cfg, DeviceItem device)
    {
        _cfg = cfg;
        _device = device;
        BodyText.Text = "Press “Refresh status”.";
        StateText.Text = "";
    }

    private async void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        if (_cfg == null || _device == null) return;
        RefreshButton.IsEnabled = false;
        StateText.Text = "Loading…";
        try
        {
            using var transport = Transport.Create(_cfg, _device.IsFleet ? _device.Code : null, null, false);
            var replies = await transport.SendAsync("GET", "/api/status");
            var reply = replies[0];
            if (!reply.Ok)
            {
                BodyText.Text = $"Status failed: {reply.Error ?? $"HTTP {reply.Status}: {reply.Text}"}";
                StateText.Text = "Failed";
                return;
            }
            var status = reply.Json<StatusResponse>();
            BodyText.Text = Format(status);
            ApplyUpdateButton.IsVisible = !string.IsNullOrWhiteSpace(status.UpdateAvailable);
            if (ApplyUpdateButton.IsVisible)
                ApplyUpdateButton.Content = $"⬆ Apply update v{status.UpdateAvailable}";
            StateText.Text = $"Updated {DateTime.Now:T}";
        }
        catch (Exception ex)
        {
            BodyText.Text = $"Status failed: {ex.Message}";
            StateText.Text = "Failed";
        }
        finally { RefreshButton.IsEnabled = true; }
    }

    private async void OnApplyUpdateClick(object? sender, RoutedEventArgs e)
    {
        if (_device == null || _cfg == null) return;
        ApplyUpdateButton.IsEnabled = false;
        try
        {
            using var transport = Transport.Create(_cfg, _device.IsFleet ? _device.Code : null, null, false);
            var replies = await transport.SendAsync("POST", "/api/update/apply-release");
            var reply = replies[0];
            StateText.Text = reply.Ok ? "Update staged — the device restarts into the new version"
                                      : $"Update failed: {reply.Error ?? reply.Text}";
        }
        catch (Exception ex) { StateText.Text = $"Update failed: {ex.Message}"; }
        finally { ApplyUpdateButton.IsEnabled = true; }
    }

    private async void OnDiagnoseClick(object? sender, RoutedEventArgs e)
    {
        if (_cfg == null || _device == null) return;
        DiagnoseButton.IsEnabled = false;
        StateText.Text = "Diagnosing…";
        try
        {
            using var transport = Transport.Create(_cfg, _device.IsFleet ? _device.Code : null, null, false);
            var replies = await transport.SendAsync("GET", "/api/diagnose", null, 180);
            var reply = replies[0];
            if (!reply.Ok)
            {
                BodyText.Text = $"Diagnose failed: {reply.Error ?? $"HTTP {reply.Status}: {reply.Text}"}";
                StateText.Text = "Failed";
                return;
            }
            BodyText.Text = FormatDiagnose(reply.Json<DiagnoseResponse>());
            StateText.Text = $"Diagnosed {DateTime.Now:T}";
        }
        catch (Exception ex)
        {
            BodyText.Text = $"Diagnose failed: {ex.Message}";
            StateText.Text = "Failed";
        }
        finally { DiagnoseButton.IsEnabled = true; }
    }

    private static string FormatDiagnose(DiagnoseResponse d)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"DIAGNOSIS — overall: {d.Overall.ToUpperInvariant()}   ({d.At.LocalDateTime:yyyy-MM-dd HH:mm})");
        sb.AppendLine($"Doctor: {d.Doctor.Overall}");
        foreach (var check in d.Doctor.Checks) sb.AppendLine($"  [{check.Status}] {check.Name}: {check.Detail}");
        void Section(string title, List<string> lines)
        {
            sb.AppendLine();
            sb.AppendLine($"{title} ({lines.Count}):");
            if (lines.Count == 0) sb.AppendLine("  (none)");
            foreach (var line in lines.Take(15)) sb.AppendLine($"  {line}");
        }
        Section("Resource pressure", d.Pressure);
        Section("Failed services", d.FailedServices);
        Section("Recent crashes", d.Crashes);
        Section("OS error events", d.OsErrors);
        sb.AppendLine();
        sb.AppendLine($"Agent audit errors: {d.AuditErrorCount} in the recent window");
        foreach (var line in d.RecentAuditErrors.Take(10)) sb.AppendLine($"  {line}");
        foreach (var note in d.Notes) sb.AppendLine($"note: {note}");
        return sb.ToString();
    }

    /// <summary>Same facts, same order as the CLI's status printer.</summary>
    private static string Format(StatusResponse s)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{s.DeviceName} ({s.DeviceCode}) — {s.Os} · {s.OsDescription}");
        sb.AppendLine($"agent {s.AgentVersion} · uptime {TimeSpan.FromSeconds(s.UptimeSeconds)} · host {s.Hostname} · locked: {s.Locked}");
        if (s.CpuPercent != null) sb.AppendLine($"CPU {s.CpuPercent:F1}%");
        if (s.RamTotalBytes > 0) sb.AppendLine($"RAM {s.RamUsedBytes / 1048576} / {s.RamTotalBytes / 1048576} MB");
        foreach (var d in s.Disks) sb.AppendLine($"disk {d.Name}: {d.FreeBytes / 1073741824} GB free / {d.TotalBytes / 1073741824} GB");
        if (s.Battery is { Present: true }) sb.AppendLine($"battery {s.Battery.Percent}% · AC: {s.Battery.OnAc}");
        if (s.LoggedOnUsers.Count > 0) sb.AppendLine($"users: {string.Join(", ", s.LoggedOnUsers)}");
        if (s.TailnetIps.Count > 0) sb.AppendLine($"tailnet: {string.Join(", ", s.TailnetIps)}");
        sb.AppendLine($"fleet: {s.FleetMode}{(s.FleetMode == "Fleet" ? $" (connected: {s.FleetConnected})" : "")}");
        if (s.MeshEnabled) sb.AppendLine($"mesh: vip {s.MeshVirtualIp ?? "—"} · {s.MeshStatus}");
        sb.AppendLine($"agent time: {s.ServerTime:u}");
        return sb.ToString();
    }
}
