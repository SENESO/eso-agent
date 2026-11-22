namespace EsoAgent.Core;

/// <summary>
/// Shipped bootstrap addresses for the P2P swarm. A bootstrap is only a
/// first-contact door: any ONE of these (or a cached peer, or an invite
/// from a device already inside) is enough to join, and none of them
/// holds the registry or sees session plaintext. The box appears twice —
/// two routes (tailnet, mesh) to the same anchor device — because it is
/// the always-on machine today; it is one entry among several by
/// design, not a load-bearing server. Add more always-on devices here
/// (or per-device in esoagent.json) as the fleet grows.
/// </summary>
public static class P2pDefaults
{
    /// <summary>Default P2P listen port (agent API is 8737, the fleet hub 8740).</summary>
    public const int ListenPort = 8742;

    public static readonly string[] Bootstrap = { "100.98.165.106:8742", "10.77.0.2:8742" };
}

/// <summary>One explicitly trusted peer for standalone mode (fleet disabled):
/// the owner lists the devices this device may hold P2P sessions with.
/// Code and public key must BOTH match — the code alone is not a secret,
/// the key is what the peer proves possession of.</summary>
public sealed class P2pTrustedPeer
{
    public string Code { get; set; } = "";
    public string PublicKey { get; set; } = "";   // base64 SPKI of the device key
}

/// <summary>
/// Peer-to-peer layer options (esoagent.json "p2p"). The swarm carries
/// device discovery (signed code → endpoint records in a Kademlia DHT)
/// and end-to-end encrypted device-to-device sessions with peer relay,
/// so the fleet hub stops being a single point for either job; it stays
/// as bootstrap + fallback. Sessions are gated by fleet approval (see
/// P2pNodeService): discovery is public, connecting is not.
/// </summary>
public sealed class P2pOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>TCP port the P2P node listens on. 0 = do not listen (announce/lookup only via outbound RPCs is not supported — a node that cannot be reached cannot be a peer; 0 effectively disables the node).</summary>
    public int ListenPort { get; set; } = P2pDefaults.ListenPort;
    /// <summary>host:port entries tried in order at startup when the peer cache yields nobody. Any one suffices.</summary>
    public List<string> Bootstrap { get; set; } = new(P2pDefaults.Bootstrap);
    /// <summary>Host name/IP this device advertises in its DHT record. Null = auto:
    /// mesh virtual IP, else the first tailnet (100.64.0.0/10) address, else the
    /// first non-loopback IPv4.</summary>
    public string? AdvertiseHost { get; set; }
    /// <summary>Port advertised in the DHT record when it differs from ListenPort
    /// (e.g. behind a port forward). 0 = advertise ListenPort.</summary>
    public int AdvertisePort { get; set; }
    /// <summary>Standalone mode (fleet disabled): the explicit trusted-peer list.
    /// Ignored while the fleet is enabled — there the approved set arrives in
    /// the fleet state frame.</summary>
    public List<P2pTrustedPeer> TrustedPeers { get; set; } = new();
    /// <summary>Diagnostics: when set, bytes this device forwards as a relay are
    /// also written to this file (they are ciphertext; the file exists so the
    /// secrecy property can be audited, exactly as the lab audited it). Off by default.</summary>
    public string? RelayCaptureFile { get; set; }
}
