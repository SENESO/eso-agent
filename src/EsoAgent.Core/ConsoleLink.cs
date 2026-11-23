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
    /// error worth surfacing in a UI. Recovery is not silent underneath,
    /// though: the read goes through <see cref="StateFile"/>, so a torn
    /// main file is healed from the last-good backup instead of blanking
    /// this device's code (the 2026-11-22 incident class).</summary>
    public static ConsoleLink? TryRead(string path)
    {
        var status = StateFile.TryReadJson<ConsoleLink>(path, out var link);
        if (status is not (StateFileRead.Ok or StateFileRead.RecoveredFromBackup) || link == null) return null;
        if (string.IsNullOrWhiteSpace(link.DeviceCode) || string.IsNullOrWhiteSpace(link.FleetUrl)) return null;
        return link;
    }

    /// <summary>Atomic publish through <see cref="StateFile"/>: temp
    /// sibling + replace, content-gated, with a last-good backup kept
    /// beside the file, so a console reading mid-refresh never sees a
    /// torn file and a torn file never costs the device its identity.</summary>
    public static void Write(string dataDir, ConsoleLink link)
    {
        Directory.CreateDirectory(dataDir);
        StateFile.WriteText(PathForDataDir(dataDir), JsonSerializer.Serialize(link, EsoJson.Options));
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

/// <summary>
/// "This device" as a connect target. The fleet registry lists the
/// console's own machine like any other device, but a session to yourself
/// is meaningless — TeamViewer never offers one (owner verdict
/// 2026-11-22). Identity comes from the local agent's console link (the
/// strong source: the agent itself states its code) — never from a name
/// guess. Codes compare case-insensitively; a null code on either side is
/// never "self" (the direct device has no code and stays connectable).
/// </summary>
public static class SelfDevice
{
    public static bool IsSelf(string? deviceCode, string? selfCode)
        => !string.IsNullOrEmpty(deviceCode)
           && !string.IsNullOrEmpty(selfCode)
           && string.Equals(deviceCode, selfCode, StringComparison.OrdinalIgnoreCase);

    /// <summary>The items with this machine removed — device rows, recents,
    /// search results: every connect surface filters through here.</summary>
    public static List<T> ExcludingSelf<T>(IEnumerable<T> items, Func<T, string?> codeOf, string? selfCode)
        => items.Where(item => !IsSelf(codeOf(item), selfCode)).ToList();
}

/// <summary>What the home screen's THIS DEVICE card should say, decided
/// purely so the decision is testable. The 2026-11-22 incident is the
/// regression this pins: the console WAS paired and this machine's agent
/// WAS enrolled, but with the fleet briefly unreachable the card showed
/// "—", claimed the computer "is not itself an enrolled device" and
/// offered Pair / set up. Rules: the local agent's identity (console
/// link) is always shown when it exists, whatever the fleet is doing;
/// "not enrolled" may only be stated when the fleet actually answered;
/// while it hasn't, the state is Reconnecting; Pair appears only when
/// there is genuinely no enrollment of any kind.</summary>
public enum IdentityDetail
{
    /// <summary>The fleet lists this device and it is online.</summary>
    SelfOnline,
    /// <summary>The fleet lists this device and it is offline.</summary>
    SelfOffline,
    /// <summary>The local agent is enrolled (console link) — the adoption
    /// offer when unpaired, a plain enrolled note when paired.</summary>
    LocalAgentEnrolled,
    /// <summary>The console key exists but the fleet has not approved it yet.</summary>
    AwaitingApproval,
    /// <summary>A fleet is configured but has not answered (yet) — the
    /// calm retry state, never an enrollment verdict.</summary>
    Reconnecting,
    /// <summary>No pairing and no local agent: the genuine first run.</summary>
    NotPaired,
    /// <summary>The fleet answered and this computer is not in it: a pure
    /// console. Only reachable on a live answer.</summary>
    ConsoleOnly,
}

public sealed record IdentityCardState(
    string? Code,
    string? Name,
    IdentityDetail Detail,
    bool ShowPair,
    bool ShowUseFleet);

public static class IdentityCard
{
    public static IdentityCardState Resolve(
        bool hasClientConfig,
        ConsoleLink? localLink,
        string? fleetSelfCode,
        string? fleetSelfName,
        bool fleetSelfOnline,
        bool fleetReachable,
        bool awaitingApproval)
    {
        // Pair only for the genuine nothing-here state; the one-click
        // fleet adoption only for an unpaired console with a local agent.
        var showPair = !hasClientConfig && localLink == null && fleetSelfCode == null;
        var showUseFleet = !hasClientConfig && localLink != null;

        // Identity source priority: the fleet's live row for this device,
        // then the local agent's own statement, then nothing.
        var code = fleetSelfCode ?? localLink?.DeviceCode;
        var name = fleetSelfName ?? localLink?.DeviceName;

        IdentityDetail detail;
        if (fleetSelfCode != null)
            detail = fleetSelfOnline ? IdentityDetail.SelfOnline : IdentityDetail.SelfOffline;
        else if (localLink != null)
            detail = hasClientConfig && !fleetReachable && !awaitingApproval
                ? IdentityDetail.Reconnecting
                : IdentityDetail.LocalAgentEnrolled;
        else if (awaitingApproval)
            detail = IdentityDetail.AwaitingApproval;
        else if (!hasClientConfig)
            detail = IdentityDetail.NotPaired;
        else if (!fleetReachable)
            detail = IdentityDetail.Reconnecting;
        else
            detail = IdentityDetail.ConsoleOnly;

        return new IdentityCardState(code, name, detail, showPair, showUseFleet);
    }
}
