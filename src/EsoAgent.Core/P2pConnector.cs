using System.Text.Json.Nodes;

namespace EsoAgent.Core;

/// <summary>Which path a peer session ended up on. Fleet is not a value here:
/// it is the caller's existing path, taken when this connector reports None.</summary>
public enum P2pPath
{
    Direct,
    PeerRelay,
    None,
}

public sealed record P2pAttempt(P2pPath Path, string? ViaCode, string? Error);

public sealed class P2pConnectOutcome
{
    public P2pSessionChannel? Channel { get; init; }
    public P2pPath Path { get; init; } = P2pPath.None;
    public string? ViaCode { get; init; }
    public P2pPeerInfo? Target { get; init; }
    public List<P2pAttempt> Attempts { get; init; } = new();
    public bool Ok => Channel != null;
}

/// <summary>
/// Path selection for peer sessions, in the product's fixed order:
/// direct peer first, peer relay second, and — when both fail — None,
/// which is the caller's signal to take the fleet path (the existing
/// hub relay) instead. Every attempt and the chosen path are written to
/// the audit sink: the path is an attribute of the session in the one
/// timeline, never a separate world (unification law).
/// </summary>
public sealed class P2pConnector
{
    private readonly P2pNode _node;
    private readonly Action<string, string, string>? _audit;

    public P2pConnector(P2pNode node, Action<string, string, string>? audit = null)
    {
        _node = node;
        _audit = audit;
    }

    /// <summary>Opens a session to a device code over the best peer path available.</summary>
    public async Task<P2pConnectOutcome> ConnectAsync(string targetCode, string purpose, CancellationToken ct = default)
    {
        var attempts = new List<P2pAttempt>();
        var target = await _node.ResolveAsync(targetCode);
        if (target == null)
        {
            attempts.Add(new P2pAttempt(P2pPath.None, null, "target code not resolvable in the DHT"));
            _audit?.Invoke("p2p", $"connect {targetCode}: no path (unresolvable) — fleet fallback", "warn");
            return new P2pConnectOutcome { Attempts = attempts };
        }
        var targetSpki = Convert.FromBase64String(target.SpkiB64);

        // 1) Direct.
        try
        {
            var ch = await P2pSessionChannel.OpenAsync(_node.Transport, _node.Identity, target, targetSpki, null, purpose);
            attempts.Add(new P2pAttempt(P2pPath.Direct, null, null));
            _audit?.Invoke("p2p", $"connect {targetCode}: direct peer path", "ok");
            return new P2pConnectOutcome { Channel = ch, Path = P2pPath.Direct, Target = target, Attempts = attempts };
        }
        catch (Exception ex)
        {
            attempts.Add(new P2pAttempt(P2pPath.Direct, null, ex.Message));
        }

        // 2) Peer relay: approved peers from the routing table, closest to the target first.
        var targetId = P2pHex.Parse(target.IdHex);
        var relays = _node.Table.Closest(targetId, P2pRoutingTable.K)
            .Where(p => p.Code != targetCode && _node.Trust.IsApprovedPeer(p.Code, p.SpkiB64))
            .Take(2)
            .ToList();
        foreach (var relay in relays)
        {
            try
            {
                var ch = await P2pSessionChannel.OpenAsync(_node.Transport, _node.Identity, target, targetSpki, relay, purpose);
                attempts.Add(new P2pAttempt(P2pPath.PeerRelay, relay.Code, null));
                _audit?.Invoke("p2p", $"connect {targetCode}: peer-relay path via {relay.Code}", "ok");
                return new P2pConnectOutcome { Channel = ch, Path = P2pPath.PeerRelay, ViaCode = relay.Code, Target = target, Attempts = attempts };
            }
            catch (Exception ex)
            {
                attempts.Add(new P2pAttempt(P2pPath.PeerRelay, relay.Code, ex.Message));
            }
        }

        // 3) No peer path — the caller takes the fleet path.
        _audit?.Invoke("p2p", $"connect {targetCode}: no peer path ({attempts.Count} attempts) — fleet fallback", "warn");
        return new P2pConnectOutcome { Target = target, Attempts = attempts };
    }

    /// <summary>Opens a session and runs one verified stream transfer over it.
    /// Returns the send result plus the path used; when no peer path exists the
    /// result's error says so and Path is None (fleet fallback is the caller's).</summary>
    public async Task<(JsonObject Result, P2pPath Path, string? ViaCode)> TransferAsync(
        string targetCode, byte[] payload, int paceMs = 0, CancellationToken ct = default)
    {
        var outcome = await ConnectAsync(targetCode, "transfer", ct);
        if (!outcome.Ok)
        {
            return (new JsonObject
            {
                ["ok"] = false,
                ["error"] = "no peer path: " + string.Join("; ", outcome.Attempts.Select(a => $"{a.Path}: {a.Error}")),
                ["fallback"] = "fleet",
            }, P2pPath.None, null);
        }
        using var ch = outcome.Channel!;
        try
        {
            var result = await P2pTransfer.SendStreamAsync(ch, _node.Identity, payload, paceMs, ct);
            result["path"] = outcome.Path.ToString();
            if (outcome.ViaCode != null) result["via"] = outcome.ViaCode;
            return (result, outcome.Path, outcome.ViaCode);
        }
        catch (Exception ex)
        {
            // A path that dies mid-transfer (the relay was killed, the peer
            // slept) is a failed transfer, not a crashed caller: report it
            // in the same shape as a refused one. The session is over either
            // way — a retry re-runs path selection and can land on another relay.
            _audit?.Invoke("p2p", $"transfer {targetCode} over {outcome.Path} failed mid-flight: {ex.Message}", "error");
            return (new JsonObject
            {
                ["ok"] = false,
                ["error"] = $"transfer over {outcome.Path} failed: {ex.Message}",
                ["path"] = outcome.Path.ToString(),
            }, outcome.Path, outcome.ViaCode);
        }
    }
}
