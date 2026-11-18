using System.Text.Json;
using EsoAgent.Core;

namespace EsoAgent.Server;

/// <summary>One enrolled (or pending) device in the fleet registry.</summary>
public sealed class DeviceRecord
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Os { get; set; } = "";
    public string OsDescription { get; set; } = "";
    public string Version { get; set; } = "";
    public List<string> Ips { get; set; } = new();
    public string PublicKey { get; set; } = "";   // base64 SPKI of the device key
    // Mesh (update 8): WireGuard identity + server-assigned virtual IP + subnets.
    public string WgPublicKey { get; set; } = "";
    public string? WgEndpoint { get; set; }
    public string? VirtualIp { get; set; }
    public List<string> AdvertisedSubnets { get; set; } = new();
    public List<string> ApprovedSubnets { get; set; } = new();
    public string State { get; set; } = "Pending"; // Pending | Approved | Rejected
    public List<string> Groups { get; set; } = new();
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    /// <summary>MAC addresses reported at hello (v1.2) — Wake-on-LAN targets.</summary>
    public List<string> Macs { get; set; } = new();
    /// <summary>Designated mesh exit node (v1.2): at most one device fleet-wide.</summary>
    public bool ExitNode { get; set; }
}

public sealed class RegistryFile
{
    public List<DeviceRecord> Devices { get; set; } = new();
}

/// <summary>Mesh coordination data (update 8): ACL rules + enrollment pre-approval keys, in mesh.json.</summary>
public sealed class MeshFile
{
    public List<AclRule> AclRules { get; set; } = new();
    public List<EnrollmentKeyRecord> EnrollmentKeys { get; set; } = new();
}

public sealed class ServerSettings
{
    public bool OperatorEnabled { get; set; } = true;
    /// <summary>Public host:port of this server's UDP relay, advertised to devices (e.g. "203.0.113.5:51821"). Null = relay not offered.</summary>
    public string? RelayEndpoint { get; set; }
    /// <summary>UDP port the DERP-style relay listens on.</summary>
    public int RelayUdpPort { get; set; } = 51821;
    /// <summary>Alert threshold (v1.2): a device offline longer than this raises an alert.</summary>
    public int AlertOfflineMinutes { get; set; } = 10;
    /// <summary>Alert webhook (v1.2): when set, every alert is also POSTed here as
    /// signed JSON (HMAC-SHA256 in X-Eso-Signature, key = WebhookSecret).</summary>
    public string? WebhookUrl { get; set; }
    public string? WebhookSecret { get; set; }
}

/// <summary>
/// The device registry, persisted as devices.json in the server data dir.
/// Reloads automatically when the file changes on disk, so the server CLI
/// (approve/reject/list run as separate processes) stays in sync with the
/// running server. All mutations are serialized and saved atomically.
/// </summary>
public sealed class FleetRegistry
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _settingsPath;
    private readonly string _meshPath;
    private RegistryFile _data = new();
    private ServerSettings _settings = new();
    private MeshFile _mesh = new();
    private DateTime _loadedMtime = DateTime.MinValue;
    private DateTime _loadedSettingsMtime = DateTime.MinValue;
    private DateTime _loadedMeshMtime = DateTime.MinValue;

    public FleetRegistry(string dataDir)
    {
        _path = Path.Combine(dataDir, "devices.json");
        _settingsPath = Path.Combine(dataDir, "server-settings.json");
        _meshPath = Path.Combine(dataDir, "mesh.json");
        ReloadIfChanged(force: true);
    }

    private void ReloadIfChanged(bool force = false)
    {
        lock (_gate)
        {
            if (File.Exists(_path))
            {
                var mtime = File.GetLastWriteTimeUtc(_path);
                if (force || mtime != _loadedMtime)
                {
                    try { _data = JsonSerializer.Deserialize<RegistryFile>(File.ReadAllText(_path), EsoJson.Options) ?? new(); }
                    catch { _data = new(); }
                    _loadedMtime = mtime;
                }
            }
            if (File.Exists(_settingsPath))
            {
                var mtime = File.GetLastWriteTimeUtc(_settingsPath);
                if (force || mtime != _loadedSettingsMtime)
                {
                    try { _settings = JsonSerializer.Deserialize<ServerSettings>(File.ReadAllText(_settingsPath), EsoJson.Options) ?? new(); }
                    catch { _settings = new(); }
                    _loadedSettingsMtime = mtime;
                }
            }
            if (File.Exists(_meshPath))
            {
                var mtime = File.GetLastWriteTimeUtc(_meshPath);
                if (force || mtime != _loadedMeshMtime)
                {
                    try { _mesh = JsonSerializer.Deserialize<MeshFile>(File.ReadAllText(_meshPath), EsoJson.Options) ?? new(); }
                    catch { _mesh = new(); }
                    _loadedMeshMtime = mtime;
                }
            }
        }
    }

    private void SaveMesh()
    {
        var tmp = _meshPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_mesh, EsoJson.Options));
        File.Move(tmp, _meshPath, overwrite: true);
        _loadedMeshMtime = File.GetLastWriteTimeUtc(_meshPath);
    }

    private void Save()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_data, EsoJson.Options));
        File.Move(tmp, _path, overwrite: true);
        _loadedMtime = File.GetLastWriteTimeUtc(_path);
    }

    private void SaveSettings()
    {
        var tmp = _settingsPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_settings, EsoJson.Options));
        File.Move(tmp, _settingsPath, overwrite: true);
        _loadedSettingsMtime = File.GetLastWriteTimeUtc(_settingsPath);
    }

    public bool OperatorEnabled
    {
        get { ReloadIfChanged(); lock (_gate) return _settings.OperatorEnabled; }
        set { lock (_gate) { _settings.OperatorEnabled = value; SaveSettings(); } }
    }

    public string? RelayEndpoint
    {
        get { ReloadIfChanged(); lock (_gate) return _settings.RelayEndpoint; }
    }

    public int RelayUdpPort
    {
        get { ReloadIfChanged(); lock (_gate) return _settings.RelayUdpPort; }
    }

    public IReadOnlyList<DeviceRecord> All()
    {
        ReloadIfChanged();
        lock (_gate) return _data.Devices.ToList();
    }

    public DeviceRecord? Find(string code)
    {
        ReloadIfChanged();
        lock (_gate) return _data.Devices.FirstOrDefault(d => d.Code.Equals(code.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Creates or updates a device from its hello frame. Returns the record, or null
    /// if the code is already bound to a DIFFERENT public key (possible forgery — refuse).</summary>
    public DeviceRecord? UpsertHello(FleetHelloFrame hello)
    {
        lock (_gate)
        {
            ReloadIfChanged();
            var existing = _data.Devices.FirstOrDefault(d => d.Code.Equals(hello.Code, StringComparison.OrdinalIgnoreCase));
            if (existing != null && !string.Equals(existing.PublicKey, hello.PublicKey, StringComparison.Ordinal))
                return null;
            if (existing == null)
            {
                existing = new DeviceRecord
                {
                    Code = hello.Code,
                    PublicKey = hello.PublicKey,
                    State = "Pending",
                    FirstSeen = DateTimeOffset.UtcNow,
                };
                _data.Devices.Add(existing);
            }
            existing.Name = hello.Name;
            existing.Os = hello.Os;
            existing.OsDescription = hello.OsDescription;
            existing.Version = hello.Version;
            existing.Ips = hello.Ips;
            if (!string.IsNullOrWhiteSpace(hello.WgPublicKey)) existing.WgPublicKey = hello.WgPublicKey;
            existing.WgEndpoint = hello.WgEndpoint;
            existing.AdvertisedSubnets = hello.AdvertisedSubnets;
            if (hello.Macs.Count > 0) existing.Macs = hello.Macs;
            existing.LastSeen = DateTimeOffset.UtcNow;
            Save();
            return existing;
        }
    }

    public void Touch(string code)
    {
        lock (_gate)
        {
            var d = _data.Devices.FirstOrDefault(x => x.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
            if (d != null) { d.LastSeen = DateTimeOffset.UtcNow; Save(); }
        }
    }

    public bool SetState(string code, string state)
    {
        lock (_gate)
        {
            ReloadIfChanged();
            var d = _data.Devices.FirstOrDefault(x => x.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
            if (d == null) return false;
            d.State = state;
            Save();
            return true;
        }
    }

    public bool Rename(string code, string name)
    {
        lock (_gate)
        {
            ReloadIfChanged();
            var d = _data.Devices.FirstOrDefault(x => x.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
            if (d == null) return false;
            d.Name = name;
            Save();
            return true;
        }
    }

    public bool SetGroups(string code, List<string> groups)
    {
        lock (_gate)
        {
            ReloadIfChanged();
            var d = _data.Devices.FirstOrDefault(x => x.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
            if (d == null) return false;
            d.Groups = groups;
            Save();
            return true;
        }
    }

    /// <summary>Designates the mesh exit node (v1.2): exactly one device, or none
    /// when code is null. Returns false when the named device does not exist.</summary>
    public bool SetExitNode(string? code)
    {
        lock (_gate)
        {
            ReloadIfChanged();
            if (code != null && !_data.Devices.Any(x => x.Code.Equals(code, StringComparison.OrdinalIgnoreCase)))
                return false;
            foreach (var d in _data.Devices)
                d.ExitNode = code != null && d.Code.Equals(code, StringComparison.OrdinalIgnoreCase);
            Save();
            return true;
        }
    }

    public string? ExitNodeCode
    {
        get { ReloadIfChanged(); lock (_gate) return _data.Devices.FirstOrDefault(d => d.ExitNode)?.Code; }
    }

    /// <summary>Alert settings accessors (v1.2) — read live so panel changes apply without a restart.</summary>
    public int AlertOfflineMinutes
    {
        get { ReloadIfChanged(); lock (_gate) return _settings.AlertOfflineMinutes; }
    }

    public (string? Url, string? Secret) Webhook
    {
        get { ReloadIfChanged(); lock (_gate) return (_settings.WebhookUrl, _settings.WebhookSecret); }
    }

    public void SetWebhook(string? url, string? secret)
    {
        lock (_gate)
        {
            ReloadIfChanged();
            _settings.WebhookUrl = url;
            if (secret != null) _settings.WebhookSecret = secret;
            SaveSettings();
        }
    }

    // ----- mesh coordination (update 8) -----

    /// <summary>Assigns a stable virtual IP on first approval; returns it (null if device unknown).</summary>
    public string? EnsureVirtualIp(string code)
    {
        lock (_gate)
        {
            ReloadIfChanged();
            var d = _data.Devices.FirstOrDefault(x => x.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
            if (d == null) return null;
            if (string.IsNullOrWhiteSpace(d.VirtualIp))
            {
                var used = _data.Devices.Where(x => !string.IsNullOrWhiteSpace(x.VirtualIp)).Select(x => x.VirtualIp!).ToList();
                d.VirtualIp = IpAllocator.Allocate(used);
                Save();
            }
            return d.VirtualIp;
        }
    }

    public bool SetApprovedSubnets(string code, List<string> subnets)
    {
        lock (_gate)
        {
            ReloadIfChanged();
            var d = _data.Devices.FirstOrDefault(x => x.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
            if (d == null) return false;
            d.ApprovedSubnets = subnets;
            Save();
            return true;
        }
    }

    public IReadOnlyList<AclRule> AclRules()
    {
        ReloadIfChanged();
        lock (_gate) return _mesh.AclRules.ToList();
    }

    public void SetAclRules(List<AclRule> rules)
    {
        lock (_gate) { ReloadIfChanged(); _mesh.AclRules = rules; SaveMesh(); }
    }

    public IReadOnlyList<EnrollmentKeyRecord> EnrollmentKeys()
    {
        ReloadIfChanged();
        lock (_gate) return _mesh.EnrollmentKeys.ToList();
    }

    public void AddEnrollmentKey(EnrollmentKeyRecord record)
    {
        lock (_gate) { ReloadIfChanged(); _mesh.EnrollmentKeys.Add(record); SaveMesh(); }
    }

    public bool RevokeEnrollmentKey(string id)
    {
        lock (_gate)
        {
            ReloadIfChanged();
            var removed = _mesh.EnrollmentKeys.RemoveAll(k => k.Id == id);
            if (removed == 0) return false;
            SaveMesh();
            return true;
        }
    }

    /// <summary>Validates a presented enrollment key; consumes one use on success.</summary>
    public bool TryConsumeEnrollmentKey(string? presented)
    {
        if (string.IsNullOrWhiteSpace(presented)) return false;
        lock (_gate)
        {
            ReloadIfChanged();
            var hash = EsoAgent.Core.EnrollmentKeys.Hash(presented);
            var record = _mesh.EnrollmentKeys.FirstOrDefault(k => k.KeyHash == hash && (k.UsesLeft == null || k.UsesLeft > 0));
            if (record == null) return false;
            if (record.UsesLeft != null) record.UsesLeft--;
            SaveMesh();
            return true;
        }
    }
}
