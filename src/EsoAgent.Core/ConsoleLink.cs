using System.Text.Json;

namespace EsoAgent.Core;

/// <summary>
/// The local console link: a small NON-SECRET file the agent publishes in
/// its own data directory so the desktop app on the same machine can
/// discover it. It carries only what anyone standing at the machine can
/// already see — the device's fleet code and name, the fleet address, the
/// fleet certificate fingerprint (a public pin, not a credential) and the
/// agent version. No tokens, no keys: pairing the console still requires
/// the operator-key approval on the server, exactly as before. The file
/// exists so a first-run app shows THIS device's real code and a one-click
/// pairing instead of "not paired yet" plus a raw config-file error
/// (owner report, 2026-11-20).
/// </summary>
public sealed class ConsoleLink
{
    public string DeviceCode { get; set; } = "";
    public string DeviceName { get; set; } = "";
    /// <summary>The fleet server's HTTPS base URL (e.g. https://host:8740) — the form the console config uses.</summary>
    public string FleetUrl { get; set; } = "";
    /// <summary>SHA-256 fingerprint of the fleet server's certificate, lowercase hex (the pin format). May be empty when the agent config pins none.</summary>
    public string FleetFingerprint { get; set; } = "";
    public string AgentVersion { get; set; } = "";

    public const string FileName = "console-link.json";

    public static string PathForDataDir(string dataDir) => Path.Combine(dataDir, FileName);

    /// <summary>Derives the console's HTTPS fleet base URL from the agent's
    /// WSS connect URL (wss://host:8740/fleet/connect → https://host:8740).
    /// Null when the input is not an absolute URL.</summary>
    public static string? FleetUrlFromServerUrl(string? serverUrl)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)) return null;
        if (!Uri.TryCreate(serverUrl.Trim(), UriKind.Absolute, out var uri)) return null;
        var scheme = uri.Scheme.ToLowerInvariant() switch
        {
            "wss" => "https",
            "ws" => "http",
            var s => s,
        };
        return $"{scheme}://{uri.Authority}";
    }

    /// <summary>Tolerant read for discovery: a missing, malformed or
    /// incomplete link file is simply "no local agent found" — never an
    /// error worth surfacing in a UI.</summary>
    public static ConsoleLink? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var link = JsonSerializer.Deserialize<ConsoleLink>(File.ReadAllText(path), EsoJson.Options);
            if (link == null) return null;
            if (string.IsNullOrWhiteSpace(link.DeviceCode) || string.IsNullOrWhiteSpace(link.FleetUrl)) return null;
            return link;
        }
        catch { return null; }
    }

    /// <summary>Atomic-ish publish: write a temp sibling, then move over the
    /// old file, so a console reading mid-refresh never sees a torn file.</summary>
    public static void Write(string dataDir, ConsoleLink link)
    {
        Directory.CreateDirectory(dataDir);
        var path = PathForDataDir(dataDir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(link, EsoJson.Options));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>How the console on this machine stands, decided from two facts:
/// whether a usable client.json pairing exists, and whether a local
/// agent's console link was found. Pure so the decision is testable
/// without a UI harness.</summary>
public enum ConsolePairingState
{
    /// <summary>A client.json pairing exists — the console works as configured.</summary>
    Paired,
    /// <summary>No console pairing yet, but this machine's own agent is fleet-enrolled: offer the one-click adoption.</summary>
    LocalAgentFound,
    /// <summary>Neither: the genuine first-run state (pairing wizard).</summary>
    Unpaired,
}

public static class ConsoleDiscovery
{
    public static ConsolePairingState Decide(bool hasClientConfig, ConsoleLink? localLink)
        => hasClientConfig ? ConsolePairingState.Paired
         : localLink != null ? ConsolePairingState.LocalAgentFound
         : ConsolePairingState.Unpaired;
}
