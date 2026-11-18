namespace EsoAgent.Core;

// Fleet wire protocol: JSON frames over one persistent outbound WebSocket
// (agent → server). The agent dials out, so enrolled devices need NO inbound
// ports — NAT/firewalls are irrelevant. Frames are discriminated by "type".

public sealed class FleetHelloFrame
{
    public string Type { get; set; } = "hello";
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Os { get; set; } = "";
    public string OsDescription { get; set; } = "";
    public string Version { get; set; } = "";
    public List<string> Ips { get; set; } = new();
    public string PublicKey { get; set; } = "";   // base64 SPKI of the device key
    /// <summary>WireGuard public key — mesh membership is exchanged at enrollment (update 8).</summary>
    public string WgPublicKey { get; set; } = "";
    /// <summary>Direct WireGuard endpoint this device advertises (ip:port). Null = relay-only.</summary>
    public string? WgEndpoint { get; set; }
    /// <summary>LAN subnets this device offers to route (effective only after owner/operator approval).</summary>
    public List<string> AdvertisedSubnets { get; set; } = new();
    /// <summary>Optional pre-approval enrollment key (unattended installs).</summary>
    public string? EnrollmentKey { get; set; }
    /// <summary>MAC addresses of the device's up, non-loopback interfaces (v1.2) —
    /// the fleet's Wake-on-LAN targets.</summary>
    public List<string> Macs { get; set; } = new();
}

/// <summary>
/// Device audit lines shipped to the fleet server for the UNIFIED activity
/// timeline (v1.2). Lines are the agent's own audit-log lines, addressed by
/// their line offset in the current audit file; the server turns them into
/// activity events with a content-hash id, so re-shipping after a reconnect
/// (at-least-once) never duplicates. Shipping is a read of data the device
/// already recorded — it grants the server no power over the device.
/// </summary>
public sealed class FleetActivityFrame
{
    public string Type { get; set; } = "activity";
    public List<FleetActivityLine> Lines { get; set; } = new();
}

public sealed class FleetActivityLine
{
    public long Offset { get; set; }
    public string Line { get; set; } = "";
}

public sealed class FleetChallengeFrame
{
    public string Type { get; set; } = "challenge";
    public string Nonce { get; set; } = "";       // base64, 32 random bytes
}

public sealed class FleetChallengeResponseFrame
{
    public string Type { get; set; } = "challenge-response";
    public string Signature { get; set; } = "";   // device key signature over the nonce bytes
}

public sealed class FleetStateFrame
{
    public string Type { get; set; } = "state";
    /// <summary>Pending | Approved | Rejected</summary>
    public string State { get; set; } = "Pending";
    /// <summary>Trusted OPERATOR key (base64 SPKI), delivered ONLY on approval. Operator tier:
    /// full fleet control incl. operational management — but NOT the owner-reserved actions.</summary>
    public string? OperatorPublicKey { get; set; }
    /// <summary>Trusted OWNER-PANEL key (base64 SPKI). Only this key may sign the owner-reserved
    /// device actions (revoke-operator, set-operator-key).</summary>
    public string? PanelPublicKey { get; set; }
    /// <summary>Server time (unix seconds) when this frame was sent (v1.1) — the
    /// agent samples its clock skew against it for the doctor check.</summary>
    public long? ServerTime { get; set; }
}

/// <summary>
/// A fleet admin action relayed to a device. Actions:
///   rotate-token     (operator or panel signed) — rotate the device's direct-mode HMAC token; result carries the new token.
///   revoke-operator  (PANEL signed only, owner-reserved) — device drops the operator key from its trusted set.
///   set-operator-key (PANEL signed only, owner-reserved) — device replaces its trusted operator key.
/// </summary>
public sealed class FleetAdminFrame
{
    public string Type { get; set; } = "admin";
    public string Action { get; set; } = "";
    public string CommandId { get; set; } = "";
    public long IssuedAt { get; set; }
    public string Signer { get; set; } = "";      // "operator" | "owner-panel"
    public string Signature { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
}

public sealed class FleetHeartbeatFrame
{
    public string Type { get; set; } = "heartbeat";
    public DateTimeOffset At { get; set; }
}

public sealed class FleetCommandFrame
{
    public string Type { get; set; } = "command";
    public string CommandId { get; set; } = "";
    public long IssuedAt { get; set; }            // unix seconds
    public string Signer { get; set; } = "";      // "operator" | "owner-panel"
    public string Signature { get; set; } = "";   // operator signature over the canonical command
    public string Method { get; set; } = "POST";
    public string Path { get; set; } = "";        // e.g. /api/exec
    public string BodyBase64 { get; set; } = "";
    /// <summary>Queued-command validity window (v1.1): when set, the signature is
    /// over the queued canonical form and the device verifies against this
    /// expiry instead of the ±120 s immediate window. Null = immediate command.</summary>
    public long? ExpiresAt { get; set; }
}

public sealed class FleetResultFrame
{
    public string Type { get; set; } = "result";
    public string CommandId { get; set; } = "";
    public int HttpStatus { get; set; }
    public string BodyBase64 { get; set; } = "";
    public string? Error { get; set; }
}

// ----- server REST API shapes -----

public sealed class FleetDeviceDto
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Os { get; set; } = "";
    public string Version { get; set; } = "";
    public List<string> Ips { get; set; } = new();
    public string State { get; set; } = "Pending";   // Pending | Approved | Rejected
    public List<string> Groups { get; set; } = new();
    public bool Online { get; set; }
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    /// <summary>Mesh virtual IP (10.77.0.0/16), assigned by the server on approval.</summary>
    public string? VirtualIp { get; set; }
    public string WgPublicKey { get; set; } = "";
    public List<string> AdvertisedSubnets { get; set; } = new();
    public List<string> ApprovedSubnets { get; set; } = new();
    /// <summary>Recorded MAC addresses (v1.2) — Wake-on-LAN targets.</summary>
    public List<string> Macs { get; set; } = new();
    /// <summary>True when this device is the designated mesh exit node (v1.2).</summary>
    public bool ExitNode { get; set; }
}

public sealed class FleetTargets
{
    public List<string> Codes { get; set; } = new();
    public string? Group { get; set; }
    public bool All { get; set; }
}

/// <summary>An operator-signed command submission to the fleet server. The operator
/// signs the command (commandId + issuedAt + method + path + body); the server
/// verifies, relays the same signature, and the device verifies again.</summary>
public sealed class FleetCommandRequest
{
    public FleetTargets Targets { get; set; } = new();
    public string CommandId { get; set; } = "";
    public long IssuedAt { get; set; }
    public string Signature { get; set; } = "";
    public string Method { get; set; } = "POST";
    public string Path { get; set; } = "";
    public string BodyBase64 { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 120;
    /// <summary>Offline queueing (v1.1): when true AND ExpiresAt is set (the
    /// signature is then over the queued canonical form), an exec/shell command
    /// for an OFFLINE approved device is persisted in the server's queue and
    /// delivered when the device reconnects, instead of failing as offline.</summary>
    public bool QueueIfOffline { get; set; }
    /// <summary>Queued-form validity deadline (unix seconds). Set exactly when
    /// the signature is over the queued canonical form.</summary>
    public long? ExpiresAt { get; set; }
}

/// <summary>An operator-signed admin action relayed by the server to one device
/// (e.g. rotate-token). Owner-reserved actions are refused on this path.</summary>
public sealed class FleetAdminRelayRequest
{
    public string Code { get; set; } = "";
    public string Action { get; set; } = "";
    public string CommandId { get; set; } = "";
    public long IssuedAt { get; set; }
    public string Signature { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
}

public sealed class FleetDeviceResult
{
    public string Code { get; set; } = "";
    public bool Ok { get; set; }
    public int? HttpStatus { get; set; }
    public string BodyBase64 { get; set; } = "";
    public string? Error { get; set; }   // offline / not approved / timeout / device-side error — never silently dropped
    /// <summary>Offline queue receipt (v1.1): the command was persisted in the
    /// server's queue for delivery on reconnect. QueueId is the fetch handle.</summary>
    public bool Queued { get; set; }
    public string? QueueId { get; set; }
}

public sealed class FleetCommandResponse
{
    public string CommandId { get; set; } = "";
    public List<FleetDeviceResult> Results { get; set; } = new();
}
