using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EsoAgent.Core;

namespace EsoAgent.Server;

/// <summary>
/// The UNIFIED activity timeline's server side (v1.2): an append-only
/// activity.jsonl in the server data dir + an in-memory index. Every face —
/// the app's Activity tab, the fleet panel, `eso activity` — reads THIS store,
/// so they can never disagree. Sources: device audit events shipped by agents
/// (deduped by content-hash id), server events, operator actions, owner
/// logins, queue/job/supervision transitions and alerts.
/// </summary>
public sealed class ActivityStore
{
    private const int MemoryCap = 100_000;
    private readonly object _gate = new();
    private readonly string _path;
    private readonly List<ActivityEvent> _events = new();
    private readonly HashSet<string> _ids = new();

    /// <summary>Raised for every NEW event (never for deduped re-ships). The
    /// server wires alert webhooks here.</summary>
    public Action<ActivityEvent>? OnEvent { get; set; }

    public ActivityStore(string dataDir)
    {
        _path = Path.Combine(dataDir, "activity.jsonl");
        try
        {
            if (File.Exists(_path))
            {
                foreach (var line in File.ReadLines(_path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var e = JsonSerializer.Deserialize<ActivityEvent>(line, EsoJson.Options);
                        if (e != null && _ids.Add(e.Id)) _events.Add(e);
                    }
                    catch { /* a torn line is skipped */ }
                }
                if (_events.Count > MemoryCap) _events.RemoveRange(0, _events.Count - MemoryCap);
            }
        }
        catch { }
    }

    /// <summary>Records an event; returns false when it was a duplicate (a
    /// re-shipped device audit line) and nothing was stored.</summary>
    public bool Record(ActivityEvent e)
    {
        lock (_gate)
        {
            if (!_ids.Add(e.Id)) return false;
            _events.Add(e);
            if (_events.Count > MemoryCap) _events.RemoveAt(0);
            try { File.AppendAllText(_path, JsonSerializer.Serialize(e, EsoJson.Options) + "\n"); }
            catch { /* the timeline must never take the server down */ }
        }
        try { OnEvent?.Invoke(e); } catch { }
        return true;
    }

    /// <summary>Convenience recorder for server-side events (fresh random id).</summary>
    public ActivityEvent Add(string source, string kind, string summary, string result,
        string? deviceCode = null, string? deviceName = null)
    {
        var e = new ActivityEvent
        {
            Id = Guid.NewGuid().ToString("N"),
            At = DateTimeOffset.UtcNow,
            Source = source,
            Kind = kind,
            Summary = summary,
            Result = result,
            DeviceCode = deviceCode,
            DeviceName = deviceName,
        };
        Record(e);
        return e;
    }

    /// <summary>Records an ALERT (also fires the webhook via OnEvent).</summary>
    public ActivityEvent AddAlert(string summary, string? deviceCode = null, string? deviceName = null)
        => Add("alert", "alert", summary, "alert", deviceCode, deviceName);

    public List<ActivityEvent> Query(ActivityQuery query)
    {
        lock (_gate) return ActivityQuery.Apply(_events, query);
    }

    /// <summary>Export set: everything in the range, oldest first, uncapped by the query limit.</summary>
    public List<ActivityEvent> Export(ActivityQuery query)
    {
        lock (_gate)
        {
            return _events.Where(query.Matches).OrderBy(e => e.At).ToList();
        }
    }

    public int CountAlerts(DateTimeOffset since)
    {
        lock (_gate) return _events.Count(e => e.Source == "alert" && e.At >= since);
    }
}

/// <summary>
/// Alert webhooks (v1.2): every alert is also POSTed to the owner-configured
/// URL as JSON, HMAC-SHA256 signed (header X-Eso-Signature over the raw body,
/// key = the webhook secret the panel generated). Best-effort: failures are
/// audit-logged and never block the alert pipeline.
/// </summary>
public static class AlertWebhook
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static void Fire(FleetServerState state, ActivityEvent e)
    {
        if (e.Source != "alert") return;
        var (url, secret) = state.Registry.Webhook;
        if (string.IsNullOrWhiteSpace(url)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var payload = JsonSerializer.Serialize(new
                {
                    type = "esoagent-alert",
                    at = e.At,
                    deviceCode = e.DeviceCode,
                    deviceName = e.DeviceName,
                    summary = e.Summary,
                }, EsoJson.Options);
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                if (!string.IsNullOrEmpty(secret))
                {
                    using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
                    var sig = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
                    req.Headers.Add("X-Eso-Signature", sig);
                }
                using var res = await Http.SendAsync(req);
                if (!res.IsSuccessStatusCode)
                    state.Audit.Write("webhook", $"alert webhook returned {(int)res.StatusCode} for '{e.Summary}'", "error");
            }
            catch (Exception ex)
            {
                state.Audit.Write("webhook", $"alert webhook failed: {ex.Message}", "error");
            }
        });
    }
}
