namespace EsoAgent.Core;

/// <summary>One peer as distributed by the fleet server in a mesh state frame.</summary>
public sealed class MeshPeerInfo
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string WgPublicKey { get; set; } = "";
    public string VirtualIp { get; set; } = "";
    /// <summary>Direct endpoint candidate (ip:port) the peer reported, if any.</summary>
    public string? Endpoint { get; set; }
    /// <summary>True when no direct endpoint exists → traffic goes through the server relay shim.</summary>
    public bool Relay { get; set; }
    /// <summary>Owner/operator-approved LAN subnets this peer routes for the fleet.</summary>
    public List<string> ApprovedSubnets { get; set; } = new();
    /// <summary>True when this peer is the fleet's designated exit node (v1.2).</summary>
    public bool ExitNode { get; set; }
}

/// <summary>One compiled inbound-allow rule for a device's mesh firewall.</summary>
public sealed class MeshFirewallRule
{
    public string FromCidr { get; set; } = "";
    public int Port { get; set; } // 0 = any port
    public string Protocol { get; set; } = "tcp";
    public string Note { get; set; } = "";
    /// <summary>
    /// Port range for this rule (e.g. "49152-65535"), when it covers more
    /// than the single <see cref="Port"/>. Renderers use the range when
    /// present; agents that predate the range field ignore it and use
    /// <see cref="Port"/> — so Port is always set to the range's first
    /// port, which keeps an old renderer narrow (fail-closed) instead of
    /// over-broad.
    /// </summary>
    public string? PortRange { get; set; }
    /// <summary>
    /// True when this rule admits REPLY traffic for connections the device
    /// itself initiated (the mesh data-plane fix, §27): on Linux the
    /// renderer emits a conntrack ESTABLISHED,RELATED accept; on Windows
    /// it emits an allow for the dynamic port range (the firewall's own
    /// stateful allowance does not cover the Wintun adapter's inbound
    /// path — field-proven 2026-11-23: a bare RST reply to an outbound
    /// SYN was dropped until an explicit allow existed). Old agents ignore
    /// the flag and apply the single <see cref="Port"/> allow — narrow,
    /// never wider than the rule.
    /// </summary>
    public bool ReturnTraffic { get; set; }
}

/// <summary>Per-device compiled firewall plan (default posture: deny inbound except these).</summary>
public sealed class MeshFirewallPlan
{
    public string DeviceCode { get; set; } = "";
    public List<MeshFirewallRule> AllowInbound { get; set; } = new();
}

/// <summary>Owner/operator-defined ACL rule: From → To (device codes or "group:&lt;name&gt;"), optional ports.</summary>
public sealed class AclRule
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public List<int> Ports { get; set; } = new(); // empty = all ports
}

/// <summary>Mesh state pushed by the server to one approved device (fleet frame type "mesh").</summary>
public sealed class FleetMeshFrame
{
    public string Type { get; set; } = "mesh";
    public string YourVirtualIp { get; set; } = "";
    public List<MeshPeerInfo> Peers { get; set; } = new();
    /// <summary>MagicDNS-lite map: device name → virtual IP (agents sync it into the OS hosts file).</summary>
    public Dictionary<string, string> Hosts { get; set; } = new();
    public MeshFirewallPlan? Firewall { get; set; }
    /// <summary>Public endpoint of the server's UDP relay (host:port), if the server offers one.</summary>
    public string? ServerRelayEndpoint { get; set; }
    /// <summary>Exit node (v1.2): the designated device's code + virtual IP, and
    /// whether THIS device is the exit node (it then enables forwarding + NAT).</summary>
    public string? ExitNodeCode { get; set; }
    public string? ExitNodeVirtualIp { get; set; }
    public bool YouAreExitNode { get; set; }
}

/// <summary>
/// Reads back the mesh frame the applier persists (<c>mesh.json</c> in
/// the data dir) — the one durable record of this device's mesh virtual
/// IP. Used at agent startup so the API can bind the mesh VIP before
/// the first fleet frame of this lifetime arrives (§27): Kestrel binds
/// once at host build, and the frame arrives seconds later, so without
/// the persisted VIP the API would stay unreachable over the mesh
/// until the next agent restart. Tolerant by design: a missing or torn
/// file is null, never an exception (the applier rewrites the file on
/// the next frame anyway).
/// </summary>
public static class MeshFrameStore
{
    public const string FileName = "mesh.json";

    public static string? TryReadVip(string dataDir)
    {
        try
        {
            var path = Path.Combine(dataDir, FileName);
            if (!File.Exists(path)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!prop.Name.Equals("yourVirtualIp", StringComparison.OrdinalIgnoreCase)) continue;
                var value = prop.Value.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }
            return null;
        }
        catch { return null; }
    }
}

/// <summary>An enrollment pre-approval key (unattended installs). Only the hash is stored server-side.</summary>
public sealed class EnrollmentKeyRecord
{
    public string Id { get; set; } = "";
    public string KeyHash { get; set; } = ""; // SHA-256 hex of the key
    /// <summary>Remaining uses; null = multi-use until revoked.</summary>
    public int? UsesLeft { get; set; }
    public string CreatedUtc { get; set; } = "";
    public string CreatedBy { get; set; } = ""; // "owner-panel" | "operator"
    public string Note { get; set; } = "";
}

/// <summary>Full mesh state as served by the fleet server's /fleet/mesh (operator) and /panel/api/mesh (owner).</summary>
public sealed class MeshStateDto
{
    public List<FleetDeviceDto> Devices { get; set; } = new();
    public List<AclRule> AclRules { get; set; } = new();
    public List<EnrollmentKeyView> EnrollmentKeys { get; set; } = new();
    public string? RelayEndpoint { get; set; }
    public string Cidr { get; set; } = "10.77.0.0/16";
}

/// <summary>An enrollment key as listed (never the hash, never the key itself).</summary>
public sealed class EnrollmentKeyView
{
    public string Id { get; set; } = "";
    public int? UsesLeft { get; set; }
    public string CreatedUtc { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public string Note { get; set; } = "";
}

/// <summary>Mesh options in the agent config (esoagent.json → "mesh").</summary>
public sealed class MeshOptions
{
    public bool Enabled { get; set; }
    public int ListenPort { get; set; } = 51820;
    /// <summary>Endpoint this device advertises to peers (e.g. its LAN ip:51820). Null = relay-only.</summary>
    public string? AdvertiseEndpoint { get; set; }
    /// <summary>LAN subnets this device OFFERS to route; they take effect only after owner/operator approval.</summary>
    public List<string> AdvertisedSubnets { get; set; } = new();
    /// <summary>Where the WireGuard config is written on Linux (wg-quick name derives from the file name).</summary>
    public string WgConfigPath { get; set; } = "/etc/wireguard/eso0.conf";
    /// <summary>Windows embedded-engine override: full path of the bundled
    /// eso-mesh.exe sidecar. Null = the copy installed next to the agent
    /// executable (the normal case; the installer stages it there).</summary>
    public string? EnginePath { get; set; }
    /// <summary>First loopback port for per-peer relay shims. Kept clear of
    /// the fleet server's default UDP relay port (51821): when an agent and
    /// the server share a host, equal defaults make the shim's loopback bind
    /// collide with the relay's wildcard bind.</summary>
    public int RelayShimBasePort { get; set; } = 51831;
    /// <summary>Exit node (v1.2): when true AND the fleet has a designated exit
    /// node, this device routes ALL its WireGuard traffic (0.0.0.0/0) via that
    /// node. Off by default — routing your whole traffic through another
    /// device is an explicit per-device opt-in.</summary>
    public bool UseExitNode { get; set; }
}
