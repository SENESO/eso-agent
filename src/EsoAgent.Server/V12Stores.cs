using System.Text.Json;
using EsoAgent.Core;

namespace EsoAgent.Server;

/// <summary>Script library storage (v1.2): scripts.json in the server data dir.</summary>
public sealed class ScriptStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private List<ScriptDto> _scripts = new();

    private sealed class ScriptFile { public List<ScriptDto> Scripts { get; set; } = new(); }

    public ScriptStore(string dataDir)
    {
        _path = Path.Combine(dataDir, "scripts.json");
        try
        {
            if (File.Exists(_path))
                _scripts = JsonSerializer.Deserialize<ScriptFile>(File.ReadAllText(_path), EsoJson.Options)?.Scripts ?? new();
        }
        catch { _scripts = new(); }
    }

    private void Save()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new ScriptFile { Scripts = _scripts }, EsoJson.Options));
        File.Move(tmp, _path, overwrite: true);
    }

    public IReadOnlyList<ScriptDto> List()
    {
        lock (_gate) return _scripts.OrderBy(s => s.Name).ToList();
    }

    public ScriptDto? Find(string idOrName)
    {
        lock (_gate)
        {
            return _scripts.FirstOrDefault(s => s.Id == idOrName)
                ?? _scripts.FirstOrDefault(s => s.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));
        }
    }

    public ScriptDto Upsert(ScriptDto dto)
    {
        lock (_gate)
        {
            var existing = _scripts.FirstOrDefault(s => s.Id == dto.Id)
                ?? _scripts.FirstOrDefault(s => s.Name.Equals(dto.Name, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                dto.Id = Guid.NewGuid().ToString("N")[..12];
                dto.CreatedAt = DateTimeOffset.UtcNow;
                dto.UpdatedAt = dto.CreatedAt;
                _scripts.Add(dto);
            }
            else
            {
                existing.Name = dto.Name;
                existing.Description = dto.Description;
                existing.Shell = dto.Shell;
                existing.Body = dto.Body;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                dto = existing;
            }
            Save();
            return dto;
        }
    }

    public bool Delete(string idOrName)
    {
        lock (_gate)
        {
            var found = Find(idOrName);
            if (found == null) return false;
            _scripts.Remove(found);
            Save();
            return true;
        }
    }
}

/// <summary>The staged update package the fleet server holds for rollouts (v1.2).</summary>
public sealed class UpdatePackageStore
{
    private readonly string _dir;

    public sealed class PackageInfo
    {
        public string Sha256 { get; set; } = "";
        public long Size { get; set; }
        public DateTimeOffset UploadedAt { get; set; }
        public string UploadedBy { get; set; } = "";
    }

    public UpdatePackageStore(string dataDir) => _dir = Path.Combine(dataDir, "updates");

    public string PackagePath => Path.Combine(_dir, "package.zip");
    private string InfoPath => Path.Combine(_dir, "package.json");

    public PackageInfo? Current
    {
        get
        {
            try
            {
                return File.Exists(InfoPath) && File.Exists(PackagePath)
                    ? JsonSerializer.Deserialize<PackageInfo>(File.ReadAllText(InfoPath), EsoJson.Options)
                    : null;
            }
            catch { return null; }
        }
    }

    public async Task<PackageInfo> SaveAsync(Stream body, string uploadedBy, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_dir);
        var tmp = PackagePath + ".tmp";
        await using (var fs = File.Create(tmp))
            await body.CopyToAsync(fs, ct);
        var sha = FileTransferService.HashFile(tmp);
        File.Move(tmp, PackagePath, overwrite: true);
        var info = new PackageInfo
        {
            Sha256 = sha,
            Size = new FileInfo(PackagePath).Length,
            UploadedAt = DateTimeOffset.UtcNow,
            UploadedBy = uploadedBy,
        };
        File.WriteAllText(InfoPath, JsonSerializer.Serialize(info, EsoJson.Options));
        return info;
    }
}

/// <summary>Rollout records (v1.2): rollouts.json. Each rollout carries a random
/// download token; devices fetch the package with it (the fetch INSTRUCTION is
/// what's signed — the token only gates the download endpoint).</summary>
public sealed class RolloutStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private List<RolloutRecord> _rollouts = new();

    public sealed class RolloutRecord
    {
        public RolloutDto Dto { get; set; } = new();
        public string Token { get; set; } = "";
    }

    private sealed class RolloutFile { public List<RolloutRecord> Rollouts { get; set; } = new(); }

    public RolloutStore(string dataDir)
    {
        _path = Path.Combine(dataDir, "rollouts.json");
        try
        {
            if (File.Exists(_path))
                _rollouts = JsonSerializer.Deserialize<RolloutFile>(File.ReadAllText(_path), EsoJson.Options)?.Rollouts ?? new();
        }
        catch { _rollouts = new(); }
    }

    private void Save()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new RolloutFile { Rollouts = _rollouts }, EsoJson.Options));
        File.Move(tmp, _path, overwrite: true);
    }

    public RolloutRecord Create(string packageSha256, string createdBy, IEnumerable<(string Code, string Version)> targets)
    {
        var record = new RolloutRecord
        {
            Token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant(),
            Dto = new RolloutDto
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                PackageSha256 = packageSha256,
                CreatedBy = createdBy,
                CreatedAt = DateTimeOffset.UtcNow,
                Devices = targets.Select(t => new RolloutDeviceDto
                {
                    Code = t.Code, State = RolloutStates.Queued, FromVersion = t.Version, UpdatedAt = DateTimeOffset.UtcNow,
                }).ToList(),
            },
        };
        lock (_gate) { _rollouts.Add(record); Save(); }
        return record;
    }

    public RolloutRecord? Get(string id)
    {
        lock (_gate) return _rollouts.FirstOrDefault(r => r.Dto.Id == id);
    }

    public IReadOnlyList<RolloutDto> List()
    {
        lock (_gate) return _rollouts.Select(r => r.Dto).OrderByDescending(r => r.CreatedAt).ToList();
    }

    /// <summary>Moves one device in a rollout to a new state (guarded by the
    /// RolloutStates machine). Returns false on an illegal transition.</summary>
    public bool SetDeviceState(string rolloutId, string code, string newState, string? error = null, string? toVersion = null)
    {
        lock (_gate)
        {
            var record = _rollouts.FirstOrDefault(r => r.Dto.Id == rolloutId);
            var device = record?.Dto.Devices.FirstOrDefault(d => d.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
            if (device == null) return false;
            if (!RolloutStates.CanTransition(device.State, newState)) return false;
            device.State = newState;
            if (error != null) device.Error = error;
            if (toVersion != null) device.ToVersion = toVersion;
            device.UpdatedAt = DateTimeOffset.UtcNow;
            Save();
            return true;
        }
    }

    /// <summary>Rollouts with a device still mid-flight (sent/applying) — used on
    /// device reconnect to settle applied/failed from the reported version.</summary>
    public IReadOnlyList<RolloutRecord> InFlightFor(string code)
    {
        lock (_gate)
        {
            return _rollouts
                .Where(r => r.Dto.Devices.Any(d => d.Code.Equals(code, StringComparison.OrdinalIgnoreCase)
                                                   && d.State is RolloutStates.Sent or RolloutStates.Applying))
                .ToList();
        }
    }
}

/// <summary>Latest per-device health snapshots (v1.2): health.json, written by
/// the health poller, read by the panel/app health surfaces.</summary>
public sealed class HealthCache
{
    private readonly object _gate = new();
    private readonly string _path;
    private Dictionary<string, DeviceHealthDto> _byCode = new();

    private sealed class HealthFile { public Dictionary<string, DeviceHealthDto> Devices { get; set; } = new(); }

    public HealthCache(string dataDir)
    {
        _path = Path.Combine(dataDir, "health.json");
        try
        {
            if (File.Exists(_path))
                _byCode = JsonSerializer.Deserialize<HealthFile>(File.ReadAllText(_path), EsoJson.Options)?.Devices ?? new();
        }
        catch { _byCode = new(); }
    }

    public DeviceHealthDto? Get(string code)
    {
        lock (_gate) return _byCode.TryGetValue(code, out var dto) ? dto : null;
    }

    public void Set(DeviceHealthDto dto)
    {
        lock (_gate)
        {
            _byCode[dto.Code] = dto;
            try
            {
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(new HealthFile { Devices = _byCode }, EsoJson.Options));
                File.Move(tmp, _path, overwrite: true);
            }
            catch { }
        }
    }

    public IReadOnlyList<DeviceHealthDto> All()
    {
        lock (_gate) return _byCode.Values.OrderBy(d => d.Code).ToList();
    }
}

/// <summary>One relayed tunnel stream that is open RIGHT NOW (UI overhaul
/// Phase 4). Tunnels are ephemeral operator-side listeners; the fleet server
/// only ever sees the ones relayed through it, so this registry is the live
/// mirror the panel shows — nothing is persisted.</summary>
public sealed class ActiveTunnelDto
{
    public string StreamId { get; set; } = "";
    public string Code { get; set; } = "";
    public string DeviceName { get; set; } = "";
    /// <summary>tcp | udp</summary>
    public string Protocol { get; set; } = "tcp";
    /// <summary>host:port on the device side.</summary>
    public string Target { get; set; } = "";
    public string Signer { get; set; } = "";
    public DateTimeOffset OpenedAt { get; set; }
}

public sealed class ActiveTunnelStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ActiveTunnelDto> _open = new();

    public void Open(ActiveTunnelDto dto) => _open[dto.StreamId] = dto;

    public void Close(string streamId) => _open.TryRemove(streamId, out _);

    public IReadOnlyList<ActiveTunnelDto> List() =>
        _open.Values.OrderByDescending(t => t.OpenedAt).ToList();
}
