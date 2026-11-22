using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace EsoAgent.Core;

// ─────────────────────────────────────────────────────────────────────────────
// Embedded mesh engine (2026-11-20, owner product law: the product must never
// require a separate WireGuard install).
//
// The tunnel data plane is a wireguard-go sidecar (eso-mesh.exe, MIT, built by
// us from pinned source — see scripts/build-mesh-engine.sh) that creates its
// TUN through the official prebuilt wintun.dll (WireGuard LLC Prebuilt
// Binaries License; redistribution alongside software using the Permitted API
// is expressly allowed — see third-party/wintun/). The agent configures and
// supervises the engine over the WireGuard UAPI protocol — the same control
// protocol wg.exe uses. On Windows the UAPI endpoint is the named pipe
// \\.\pipe\ProtectedPrefix\Administrators\WireGuard\<iface>; on Linux it is
// the unix socket /var/run/wireguard/<iface>.sock (the Linux agent still uses
// wg-quick today; the transports below are the drop-in seam for closing that
// dependency later — see MESH-EMBED-REPORT.md).
//
// Everything in this file except the two guarded Windows helper classes at the
// bottom is pure or behind a small interface, and is unit-tested.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>One peer in a structured WireGuard interface configuration.</summary>
public sealed class WgPeerConfig
{
    /// <summary>Fleet display name (config comments only; not sent to the engine).</summary>
    public string Name { get; set; } = "";
    /// <summary>Fleet device code (config comments only; not sent to the engine).</summary>
    public string Code { get; set; } = "";
    public string PublicKeyB64 { get; set; } = "";
    public List<string> AllowedIps { get; set; } = new();
    /// <summary>host:port, or null for a peer with no endpoint (inbound-only).</summary>
    public string? Endpoint { get; set; }
    public int PersistentKeepalive { get; set; } = 25;
}

/// <summary>
/// Structured WireGuard interface configuration — the single model both the
/// wg-quick text renderer (WgConfigBuilder) and the UAPI translator
/// (UapiProtocol) consume, so the two can never drift.
/// </summary>
public sealed class WgInterfaceConfig
{
    public string PrivateKeyB64 { get; set; } = "";
    /// <summary>This device's mesh virtual IP (no prefix length).</summary>
    public string VirtualIp { get; set; } = "";
    public int ListenPort { get; set; } = 51820;
    public List<WgPeerConfig> Peers { get; set; } = new();
}

/// <summary>
/// Effective equality of two interface models: every field the engine
/// can observe, in order — peer order is significant because relay
/// shim ports are assigned by peer index. Display-only fields (a
/// peer's Name/Code) are excluded: they never reach the engine, so a
/// rename alone must not reconfigure it. The mesh idempotency gate
/// (MeshEngineSupervisor, §24) keys on this: a UAPI set is a full
/// peer replacement, so "same model" must mean "the engine already
/// holds exactly this" before a re-apply may be skipped.
/// </summary>
public static class WgConfigEquality
{
    public static bool Equals(WgInterfaceConfig? a, WgInterfaceConfig? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (!string.Equals(a.PrivateKeyB64, b.PrivateKeyB64, StringComparison.Ordinal)
            || !string.Equals(a.VirtualIp, b.VirtualIp, StringComparison.Ordinal)
            || a.ListenPort != b.ListenPort
            || a.Peers.Count != b.Peers.Count) return false;
        for (var i = 0; i < a.Peers.Count; i++)
        {
            var x = a.Peers[i];
            var y = b.Peers[i];
            if (!string.Equals(x.PublicKeyB64, y.PublicKeyB64, StringComparison.Ordinal)
                || !string.Equals(x.Endpoint, y.Endpoint, StringComparison.Ordinal)
                || x.PersistentKeepalive != y.PersistentKeepalive
                || !x.AllowedIps.SequenceEqual(y.AllowedIps, StringComparer.Ordinal)) return false;
        }
        return true;
    }
}

/// <summary>Parses wg-quick style config text into a <see cref="WgInterfaceConfig"/> (pure).</summary>
public static class WgConfigParser
{
    public static WgInterfaceConfig Parse(string configText)
    {
        var model = new WgInterfaceConfig();
        WgPeerConfig? peer = null;
        var inInterface = false;
        foreach (var rawLine in configText.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
            if (line.Equals("[Interface]", StringComparison.OrdinalIgnoreCase)) { inInterface = true; peer = null; continue; }
            if (line.Equals("[Peer]", StringComparison.OrdinalIgnoreCase))
            {
                inInterface = false;
                peer = new WgPeerConfig();
                model.Peers.Add(peer);
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (inInterface)
            {
                switch (key.ToLowerInvariant())
                {
                    case "privatekey": model.PrivateKeyB64 = value; break;
                    case "address":
                        // First address wins; strip any /prefix.
                        var first = value.Split(',')[0].Trim();
                        var slash = first.IndexOf('/');
                        model.VirtualIp = slash >= 0 ? first[..slash] : first;
                        break;
                    case "listenport":
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)) model.ListenPort = port;
                        break;
                }
            }
            else if (peer != null)
            {
                switch (key.ToLowerInvariant())
                {
                    case "publickey": peer.PublicKeyB64 = value; break;
                    case "allowedips":
                        peer.AllowedIps = value.Split(',')
                            .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                        break;
                    case "endpoint": peer.Endpoint = value; break;
                    case "persistentkeepalive":
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ka)) peer.PersistentKeepalive = ka;
                        break;
                }
            }
        }
        return model;
    }
}

/// <summary>
/// The WireGuard UAPI protocol (the control protocol spoken by wireguard-go
/// and the kernel module alike): text requests of key=value lines terminated
/// by a blank line; keys are lowercase hex (not base64) on the wire.
/// Pure translation/parsing — unit-tested.
/// </summary>
public static class UapiProtocol
{
    public static string BuildGetRequest() => "get=1\n\n";

    /// <summary>
    /// Builds a full-replacement set=1 request from the structured model:
    /// the peer list is replaced wholesale (replace_peers=true) and each
    /// peer's AllowedIPs are replaced (replace_allowed_ips=true), so a set
    /// always converges the engine to exactly the model — the same semantics
    /// the old wireguard.exe service reinstall had.
    /// </summary>
    public static string BuildSetRequest(WgInterfaceConfig model)
    {
        var sb = new StringBuilder();
        sb.Append("set=1\n");
        sb.Append($"private_key={KeyToHex(model.PrivateKeyB64)}\n");
        sb.Append($"listen_port={model.ListenPort}\n");
        sb.Append("replace_peers=true\n");
        foreach (var peer in model.Peers)
        {
            sb.Append($"public_key={KeyToHex(peer.PublicKeyB64)}\n");
            sb.Append("replace_allowed_ips=true\n");
            foreach (var cidr in peer.AllowedIps)
                sb.Append($"allowed_ip={NormalizeCidr(cidr)}\n");
            if (!string.IsNullOrWhiteSpace(peer.Endpoint))
                sb.Append($"endpoint={peer.Endpoint.Trim()}\n");
            sb.Append($"persistent_keepalive_interval={peer.PersistentKeepalive}\n");
        }
        sb.Append('\n');
        return sb.ToString();
    }

    /// <summary>Converts a base64 WireGuard key (32 bytes) to the lowercase hex form UAPI uses.</summary>
    public static string KeyToHex(string keyB64)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(keyB64.Trim()); }
        catch (FormatException ex) { throw new ArgumentException($"Not a base64 WireGuard key: {ex.Message}", nameof(keyB64)); }
        if (bytes.Length != 32) throw new ArgumentException($"WireGuard keys are 32 bytes, got {bytes.Length}.", nameof(keyB64));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>Normalizes an AllowedIPs entry: a bare IP becomes a /32 host route.</summary>
    public static string NormalizeCidr(string cidr)
    {
        var trimmed = cidr.Trim();
        return trimmed.Contains('/') ? trimmed : trimmed + "/32";
    }

    /// <summary>Extracts the errno from any UAPI response (the last errno= line). Missing = -1.</summary>
    public static int ParseErrno(string response)
    {
        var errno = -1;
        foreach (var line in Lines(response))
        {
            if (line.StartsWith("errno=", StringComparison.Ordinal) &&
                int.TryParse(line.AsSpan(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                errno = n;
        }
        return errno;
    }

    public static bool IsSuccess(string response) => ParseErrno(response) == 0;

    /// <summary>Parses a get=1 response into typed device + peer status.</summary>
    public static UapiDeviceStatus ParseGetResponse(string response)
    {
        var status = new UapiDeviceStatus { Errno = ParseErrno(response) };
        UapiPeerStatus? peer = null;
        foreach (var line in Lines(response))
        {
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line[..eq];
            var value = line[(eq + 1)..];
            if (key == "public_key")
            {
                peer = new UapiPeerStatus { PublicKeyHex = value };
                status.Peers.Add(peer);
                continue;
            }
            if (peer == null)
            {
                switch (key)
                {
                    case "listen_port":
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)) status.ListenPort = p;
                        break;
                    case "fwmark":
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var f)) status.FwMark = f;
                        break;
                }
                continue;
            }
            switch (key)
            {
                case "endpoint": peer.Endpoint = value; break;
                case "last_handshake_time_sec":
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)) peer.LastHandshakeUnixSec = s;
                    break;
                case "tx_bytes":
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tx)) peer.TxBytes = tx;
                    break;
                case "rx_bytes":
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rx)) peer.RxBytes = rx;
                    break;
                case "persistent_keepalive_interval":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ka)) peer.PersistentKeepalive = ka;
                    break;
                case "allowed_ip": peer.AllowedIps.Add(value); break;
            }
        }
        return status;
    }

    private static IEnumerable<string> Lines(string text)
        => text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
}

/// <summary>Typed result of a UAPI get=1 (device section + one entry per peer).</summary>
public sealed class UapiDeviceStatus
{
    public int Errno { get; set; } = -1;
    public int ListenPort { get; set; }
    public int FwMark { get; set; }
    public List<UapiPeerStatus> Peers { get; set; } = new();
    public bool Ok => Errno == 0;

    /// <summary>Compact one-line summary for status surfaces (handshakes + byte counters).</summary>
    public string Summarize()
    {
        if (!Ok) return $"engine status unavailable (errno={Errno})";
        var handshakes = Peers.Count(p => p.LastHandshakeUnixSec > 0);
        var tx = Peers.Sum(p => p.TxBytes);
        var rx = Peers.Sum(p => p.RxBytes);
        return $"{Peers.Count} peer(s), {handshakes} handshake(s), tx {tx} B / rx {rx} B";
    }
}

public sealed class UapiPeerStatus
{
    public string PublicKeyHex { get; set; } = "";
    public string? Endpoint { get; set; }
    /// <summary>Unix seconds of the last completed handshake; 0 = never.</summary>
    public long LastHandshakeUnixSec { get; set; }
    public long TxBytes { get; set; }
    public long RxBytes { get; set; }
    public int PersistentKeepalive { get; set; }
    public List<string> AllowedIps { get; set; } = new();
}

/// <summary>
/// One request/response UAPI channel. Implementations open a fresh connection
/// per request (as wg.exe does); the engine accepts any number of them.
/// </summary>
public interface IUapiTransport : IDisposable
{
    Task<string> RequestAsync(string request, CancellationToken ct = default);
}

/// <summary>Shared UAPI framing: write the request, read until the blank-line terminator.</summary>
internal static class UapiFraming
{
    public static async Task<string> RoundTripAsync(Stream stream, string request, CancellationToken ct)
    {
        var bytes = Encoding.ASCII.GetBytes(request);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
        var buffer = new byte[8192];
        var sb = new StringBuilder();
        while (sb.Length < 1_048_576)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0) break; // engine closed the connection: response complete
            sb.Append(Encoding.ASCII.GetString(buffer, 0, read));
            var text = sb.ToString();
            if (text.EndsWith("\n\n", StringComparison.Ordinal) || text.Contains("\n\n", StringComparison.Ordinal))
                break;
        }
        return sb.ToString();
    }
}

/// <summary>
/// Windows UAPI transport: the named pipe wireguard-go listens on,
/// \\.\pipe\ProtectedPrefix\Administrators\WireGuard\&lt;iface&gt;
/// (ipc/uapi_windows.go in the wireguard-go source).
/// </summary>
public sealed class NamedPipeUapiTransport : IUapiTransport
{
    private readonly string _pipeName;
    private readonly int _timeoutMs;

    public NamedPipeUapiTransport(string interfaceName, int timeoutMs = 5_000)
    {
        _pipeName = $@"ProtectedPrefix\Administrators\WireGuard\{interfaceName}";
        _timeoutMs = timeoutMs;
    }

    public async Task<string> RequestAsync(string request, CancellationToken ct = default)
    {
        using var pipe = new System.IO.Pipes.NamedPipeClientStream(
            ".", _pipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeoutMs);
        await pipe.ConnectAsync(timeout.Token);
        return await UapiFraming.RoundTripAsync(pipe, request, timeout.Token);
    }

    public void Dispose() { }
}

/// <summary>
/// Linux UAPI transport: the unix socket /var/run/wireguard/&lt;iface&gt;.sock.
/// The Linux agent still drives its kernel tunnel with wg-quick today; this is
/// the ready-made seam for the follow-up that points the same supervisor at a
/// Linux sidecar and retires that dependency too.
/// </summary>
public sealed class UnixSocketUapiTransport : IUapiTransport
{
    private readonly string _socketPath;

    public UnixSocketUapiTransport(string interfaceName, string socketDir = "/var/run/wireguard")
        => _socketPath = Path.Combine(socketDir, interfaceName + ".sock");

    /// <summary>Direct-path constructor (used by tests and non-standard layouts).</summary>
    public UnixSocketUapiTransport(string socketPath) => _socketPath = socketPath;

    public async Task<string> RequestAsync(string request, CancellationToken ct = default)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), ct);
        using var stream = new NetworkStream(socket, ownsSocket: false);
        return await UapiFraming.RoundTripAsync(stream, request, ct);
    }

    public void Dispose() { }
}

/// <summary>
/// Windows addressing plan for the embedded engine (pure; unit-tested). The
/// Wintun adapter wireguard-go creates carries no address until the agent adds
/// one — unlike the old WireGuard service, which read Address= from the .conf.
/// Commands run through the agent's audited process runner; the applier
/// tolerates individual failures (delete-before-add is what makes re-applies
/// idempotent, mirroring the firewall plan's shape).
/// </summary>
public static class WindowsMeshAddressing
{
    public static List<string[]> ApplyCommands(string interfaceName, WgInterfaceConfig model)
    {
        var commands = new List<string[]>
        {
            new[] { "netsh", "interface", "ipv4", "set", "address", $"name={interfaceName}", "source=static", $"address={model.VirtualIp}", "mask=255.255.255.255", "gateway=none" },
        };
        foreach (var cidr in RoutePrefixes(model))
        {
            commands.Add(new[] { "netsh", "interface", "ipv4", "delete", "route", $"prefix={cidr}", $"interface={interfaceName}" });
            commands.Add(new[] { "netsh", "interface", "ipv4", "add", "route", $"prefix={cidr}", $"interface={interfaceName}", "nexthop=0.0.0.0" });
        }
        return commands;
    }

    public static List<string[]> RemoveCommands(string interfaceName, WgInterfaceConfig model)
    {
        var commands = new List<string[]>();
        foreach (var cidr in RoutePrefixes(model))
            commands.Add(new[] { "netsh", "interface", "ipv4", "delete", "route", $"prefix={cidr}", $"interface={interfaceName}" });
        commands.Add(new[] { "netsh", "interface", "ipv4", "delete", "address", $"name={interfaceName}", $"address={model.VirtualIp}" });
        return commands;
    }

    /// <summary>Every distinct route the cryptokey routing table needs: all peer
    /// AllowedIPs except this device's own /32 (already the adapter address).</summary>
    public static List<string> RoutePrefixes(WgInterfaceConfig model)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var routes = new List<string>();
        foreach (var peer in model.Peers)
        foreach (var allowed in peer.AllowedIps)
        {
            var cidr = UapiProtocol.NormalizeCidr(allowed);
            if (cidr.Equals($"{model.VirtualIp}/32", StringComparison.OrdinalIgnoreCase)) continue;
            if (seen.Add(cidr)) routes.Add(cidr);
        }
        return routes;
    }
}

/// <summary>
/// Deletes a leftover Wintun adapter by name via wintun.dll's Permitted API
/// (WintunOpenAdapter / WintunDeleteAdapter / WintunCloseAdapter). Used on
/// mesh teardown so disabling the mesh leaves no orphaned adapter behind if
/// the engine process died ungracefully. Windows-only; every failure is a
/// quiet false (best-effort cleanup, never a crash path).
/// </summary>
public static class WintunAdapter
{
    private static readonly object Gate = new();
    private static string? _loadedFrom;

    /// <summary>
    /// Ensures wintun.dll is loaded from the given directory (the install dir,
    /// where the installer stages it next to the engine). Once loaded by full
    /// path, the name-based P/Invokes below bind to that module.
    /// </summary>
    public static bool TryLoadLibrary(string directory)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
        lock (Gate)
        {
            if (_loadedFrom == directory) return true;
            var path = Path.Combine(directory, "wintun.dll");
            if (!File.Exists(path)) return false;
            try
            {
                if (!NativeLibrary.TryLoad(path, out _)) return false;
                _loadedFrom = directory;
                return true;
            }
            catch { return false; }
        }
    }

    public static bool TryDelete(string adapterName, string libraryDirectory)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
        if (!TryLoadLibrary(libraryDirectory)) return false;
        IntPtr adapter = IntPtr.Zero;
        try
        {
            adapter = WintunOpenAdapter(adapterName);
            if (adapter == IntPtr.Zero) return false; // no such adapter: nothing to clean
            return WintunDeleteAdapter(adapter, false, out _);
        }
        catch { return false; }
        finally
        {
            if (adapter != IntPtr.Zero)
            {
                try { WintunCloseAdapter(adapter); } catch { /* best-effort */ }
            }
        }
    }

    [DllImport("wintun.dll", EntryPoint = "WintunOpenAdapter", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr WintunOpenAdapter(string name);

    [DllImport("wintun.dll", EntryPoint = "WintunDeleteAdapter", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WintunDeleteAdapter(IntPtr adapter, [MarshalAs(UnmanagedType.Bool)] bool forceCloseSessions, [MarshalAs(UnmanagedType.Bool)] out bool rebootRequired);

    [DllImport("wintun.dll", EntryPoint = "WintunCloseAdapter")]
    private static extern void WintunCloseAdapter(IntPtr adapter);
}

/// <summary>
/// A Windows job object with KILL_ON_JOB_CLOSE holding the engine process:
/// when the agent process exits — cleanly or not — the OS terminates the
/// engine with it, so an agent crash/restart can never leave an orphaned
/// tunnel holding the adapter and the UAPI pipe name. Windows-only; failures
/// degrade to "no job" silently (supervision still works, adoption covers
/// the restart case).
/// </summary>
public sealed class KillOnCloseJob : IDisposable
{
    private IntPtr _job;

    private KillOnCloseJob(IntPtr job) => _job = job;

    public static KillOnCloseJob? TryCreateAndAssign(Process process)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return null;
        try
        {
            var limits = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose,
                },
            };
            var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(limits, ptr, false);
                if (!SetInformationJobObject(job, JobObjectInfoClass.ExtendedLimitInformation, ptr, (uint)size))
                    return Fail(job);
            }
            finally { Marshal.FreeHGlobal(ptr); }
            if (!AssignProcessToJobObject(job, process.Handle)) return Fail(job);
            return new KillOnCloseJob(job);
        }
        catch { return Fail(job); }

        static KillOnCloseJob? Fail(IntPtr handle)
        {
            try { CloseHandle(handle); } catch { /* best-effort */ }
            return null;
        }
    }

    public void Dispose()
    {
        if (_job == IntPtr.Zero) return;
        try { CloseHandle(_job); } catch { /* best-effort */ }
        _job = IntPtr.Zero;
    }

    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    private enum JobObjectInfoClass { ExtendedLimitInformation = 9 }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, JobObjectInfoClass infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
