using System.Text.Json;
using EsoAgent.Core;

namespace EsoAgent.Server;

/// <summary>
/// The offline command queue (v1.1), persisted as queue.json in the server data
/// dir. A relayed exec/shell command for an OFFLINE approved device is stored
/// here (with the operator's queued-form signature envelope, untouched) and
/// delivered when the device reconnects. Expired commands are dropped and
/// marked expired — never delivered late. Every transition is audit-logged.
///
/// Delivery is AT-LEAST-ONCE: if the transport fails mid-delivery (device
/// disconnected, result lost), the record stays queued and is retried on the
/// next reconnect — a command can therefore run twice if the device executed
/// it but its result never arrived. Callers who need exactly-once must make
/// the command itself idempotent.
/// </summary>
public sealed class CommandQueueStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly AuditLog _audit;
    private List<QueuedCommandRecord> _records = new();

    private sealed class QueueFile
    {
        public List<QueuedCommandRecord> Commands { get; set; } = new();
    }

    public CommandQueueStore(string dataDir, AuditLog audit)
    {
        _path = Path.Combine(dataDir, "queue.json");
        _audit = audit;
        Load();
    }

    private void Load()
    {
        // Backup-aware (defect-8), same policy as the other stores.
        var status = StateFile.TryReadJson<QueueFile>(_path, out var file);
        if (file != null) _records = file.Commands ?? new();
        else if (status == StateFileRead.Corrupt) StateFile.QuarantineCorrupt(_path);
    }

    private void Save()
    {
        StateFile.WriteText(_path, JsonSerializer.Serialize(new QueueFile { Commands = _records }, EsoJson.Options));
    }

    public QueuedCommandRecord Enqueue(QueuedCommandRecord record)
    {
        lock (_gate)
        {
            _records.Add(record);
            Save();
        }
        _audit.Write("fleet/queue",
            $"queued {record.Method} {record.Path} for offline device {record.DeviceCode} (queue id {record.Id}, expires {DateTimeOffset.FromUnixTimeSeconds(record.ExpiresAt):u})",
            "ok");
        return record;
    }

    public QueuedCommandRecord? Get(string id)
    {
        SweepExpired();
        lock (_gate) return _records.FirstOrDefault(r => r.Id == id);
    }

    public IReadOnlyList<QueuedCommandRecord> List(string? deviceCode = null)
    {
        SweepExpired();
        lock (_gate)
        {
            return _records
                .Where(r => deviceCode == null || r.DeviceCode.Equals(deviceCode, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.QueuedAt)
                .ToList();
        }
    }

    /// <summary>Deliverable records for a device, oldest first.</summary>
    public IReadOnlyList<QueuedCommandRecord> PendingFor(string deviceCode)
    {
        SweepExpired();
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            return _records
                .Where(r => r.DeviceCode.Equals(deviceCode, StringComparison.OrdinalIgnoreCase) && QueuePolicy.IsDeliverable(r, now))
                .OrderBy(r => r.QueuedAt)
                .ToList();
        }
    }

    /// <summary>Marks every queued-but-expired record expired (dropped, never
    /// delivered). Returns how many were newly expired.</summary>
    public int SweepExpired()
    {
        var now = DateTimeOffset.UtcNow;
        var newlyExpired = new List<QueuedCommandRecord>();
        lock (_gate)
        {
            foreach (var record in _records)
            {
                if (record.State == "queued" && QueuePolicy.IsExpired(record, now))
                {
                    record.State = "expired";
                    newlyExpired.Add(record);
                }
            }
            if (newlyExpired.Count > 0) Save();
        }
        foreach (var record in newlyExpired)
            _audit.Write("fleet/queue", $"queue id {record.Id} for {record.DeviceCode} EXPIRED undelivered ({record.Method} {record.Path})", "expired");
        return newlyExpired.Count;
    }

    /// <summary>Records a delivery outcome. Transport-level failures (device
    /// vanished mid-delivery, result lost) leave the record queued for the next
    /// reconnect; a device answer completes it as delivered; a device-side
    /// error completes it as failed.</summary>
    public void CompleteDelivery(QueuedCommandRecord record, FleetResultFrame result)
    {
        var transportFailure = result.Error is "Device disconnected." or "Device reconnected." or "Timed out waiting for the device.";
        lock (_gate)
        {
            if (transportFailure)
            {
                _audit.Write("fleet/queue", $"delivery of queue id {record.Id} to {record.DeviceCode} deferred ({result.Error}) — stays queued", "retry");
                return;
            }
            record.DeliveredAt = DateTimeOffset.UtcNow;
            if (result.Error == null)
            {
                record.State = "delivered";
                record.HttpStatus = result.HttpStatus;
                record.ResultBodyBase64 = result.BodyBase64;
            }
            else
            {
                record.State = "failed";
                record.Error = result.Error;
            }
            Save();
        }
        _audit.Write("fleet/queue",
            record.State == "delivered"
                ? $"queue id {record.Id} delivered to {record.DeviceCode} ({record.Method} {record.Path}) -> http {record.HttpStatus}"
                : $"queue id {record.Id} FAILED on {record.DeviceCode}: {record.Error}",
            record.State == "delivered" ? "ok" : "error");
    }
}

/// <summary>Drains a device's queued commands over its fresh connection.</summary>
public static class CommandQueueDelivery
{
    public static async Task DrainAsync(FleetServerState state, DeviceConnection conn, string code)
    {
        try
        {
            var pending = state.Queue.PendingFor(code);
            foreach (var record in pending)
            {
                // The connection may have been replaced while we drained.
                if (!state.Connections.TryGetValue(code, out var current) || !ReferenceEquals(current, conn)) return;
                var frame = new FleetCommandFrame
                {
                    CommandId = record.CommandId,
                    IssuedAt = record.IssuedAt,
                    ExpiresAt = record.ExpiresAt,
                    Signer = record.Signer,
                    Signature = record.Signature,
                    Method = record.Method,
                    Path = record.Path,
                    BodyBase64 = record.BodyBase64,
                };
                var result = await conn.SendAndWaitAsync(frame, record.CommandId,
                    TimeSpan.FromSeconds(Math.Clamp(record.TimeoutSeconds, 5, 1800)));
                state.Queue.CompleteDelivery(record, result);
                state.Activity.Add("server", "queue",
                    $"Queued command delivered: {record.Method} {record.Path}",
                    result.Error ?? $"http {result.HttpStatus}", code, state.Registry.Find(code)?.Name);
            }
        }
        catch (Exception ex)
        {
            state.Audit.Write("fleet/queue", $"drain for {code} failed: {ex.Message}", "error");
        }
    }
}
