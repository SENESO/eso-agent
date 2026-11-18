using System.Text;
using System.Text.Json;
using EsoAgent.Core;
using Microsoft.Extensions.Hosting;

namespace EsoAgent.Server;

/// <summary>
/// v1.2 health + alerts. Every minute, for each online Approved device, the
/// server fetches /api/doctor + /api/status through the panel-signed relay
/// (the same channel the owner panel uses — no new device surface) and caches
/// a DeviceHealthDto in health.json. The alert evaluator watches transitions:
/// a device offline longer than the owner's threshold, low disk, a supervised
/// process that gave up, an expiring certificate. Alerts are activity events
/// (source "alert"), so they appear in the one timeline everywhere, and the
/// webhook hook fires for them like any other event.
/// </summary>
public sealed class HealthPollerService : BackgroundService
{
    private readonly FleetServerState _state;
    private readonly Dictionary<string, DateTimeOffset> _offlineSince = new();
    private readonly HashSet<string> _alerted = new(); // episode keys: "offline:CODE", "disk:CODE", "sup:CODE:name", "cert:CODE"

    public HealthPollerService(FleetServerState state) => _state = state;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(15), ct); } catch { return; }
        while (!ct.IsCancellationRequested)
        {
            try { await PollOnceAsync(ct); }
            catch (Exception ex) { _state.Audit.Write("fleet/health", "poll failed: " + ex.Message, "error"); }
            try { await Task.Delay(TimeSpan.FromSeconds(60), ct); } catch { return; }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var device in _state.Registry.All().Where(d => d.State == "Approved"))
        {
            var online = _state.Connections.ContainsKey(device.Code);
            if (!online)
            {
                _offlineSince.TryAdd(device.Code, device.LastSeen > DateTimeOffset.MinValue ? device.LastSeen : now);
                var offlineFor = now - _offlineSince[device.Code];
                if (offlineFor.TotalMinutes >= _state.Registry.AlertOfflineMinutes && _alerted.Add("offline:" + device.Code))
                    _state.Activity.AddAlert(
                        $"{device.Name} has been offline for {(int)offlineFor.TotalMinutes} min (threshold {_state.Registry.AlertOfflineMinutes} min).",
                        device.Code, device.Name);
                // Keep a health row so surfaces can show last-seen + offline.
                if (_state.Health.Get(device.Code) is not { Online: true })
                    _state.Health.Set(HealthSummary.Build(device.Code, device.Name, false, device.LastSeen, null, null, now));
                continue;
            }

            _offlineSince.Remove(device.Code);
            _alerted.Remove("offline:" + device.Code);

            DoctorResponse? doctor = null;
            StatusResponse? status = null;
            var doctorResult = await Relay.PanelCommandAsync(_state, device.Code, "GET", "/api/doctor", Array.Empty<byte>(), TimeSpan.FromSeconds(30));
            if (doctorResult.Error == null && doctorResult.HttpStatus == 200)
            {
                try { doctor = JsonSerializer.Deserialize<DoctorResponse>(Encoding.UTF8.GetString(Convert.FromBase64String(doctorResult.BodyBase64)), EsoJson.Options); } catch { }
            }
            var statusResult = await Relay.PanelCommandAsync(_state, device.Code, "GET", "/api/status", Array.Empty<byte>(), TimeSpan.FromSeconds(30));
            if (statusResult.Error == null && statusResult.HttpStatus == 200)
            {
                try { status = JsonSerializer.Deserialize<StatusResponse>(Encoding.UTF8.GetString(Convert.FromBase64String(statusResult.BodyBase64)), EsoJson.Options); } catch { }
            }
            if (doctor == null && status == null) continue;

            var health = HealthSummary.Build(device.Code, device.Name, true, now, doctor, status, now);
            _state.Health.Set(health);
            EvaluateHealthAlerts(device, health);
        }
    }

    private void EvaluateHealthAlerts(DeviceRecord device, DeviceHealthDto health)
    {
        if (health.MinDiskFreePercent is { } free)
        {
            if (free < 10)
            {
                if (_alerted.Add("disk:" + device.Code))
                    _state.Activity.AddAlert(
                        $"{device.Name}: a disk is down to {free:0.#}% free space.",
                        device.Code, device.Name);
            }
            else if (free >= 12) _alerted.Remove("disk:" + device.Code);
        }
        foreach (var sup in health.Supervised)
        {
            var key = $"sup:{device.Code}:{sup.Name}";
            if (sup.State == "gave-up")
            {
                if (_alerted.Add(key))
                    _state.Activity.AddAlert(
                        $"{device.Name}: supervised process '{sup.Name}' exhausted its restarts and gave up.",
                        device.Code, device.Name);
            }
            else _alerted.Remove(key);
        }
        if (health.CertDaysLeft is { } days)
        {
            if (days < 14)
            {
                if (_alerted.Add("cert:" + device.Code))
                    _state.Activity.AddAlert(
                        $"{device.Name}: agent certificate expires in {days:0} day(s).",
                        device.Code, device.Name);
            }
            else if (days >= 20) _alerted.Remove("cert:" + device.Code);
        }
    }
}
