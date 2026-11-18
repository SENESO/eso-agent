using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EsoAgent.Core;

namespace EsoAgent.Client;

/// <summary>
/// eso — the EsoAgent operator CLI. One tool, three ways to reach a device:
///   direct   (default, or --device &lt;IP&gt;)  — HMAC token + pinned cert, straight to the agent
///   by code  (--device &lt;CODE&gt;)            — relayed through the fleet server, operator-signed
///   fan-out  (--group &lt;name&gt; | --all)      — fleet relay to many devices, per-device results
/// The client never needs to know which OS a device runs; the API is identical.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] argv)
    {
        var args = new List<string>(argv);
        if (args.Contains("--version") || args.Contains("version"))
        {
            Console.WriteLine($"EsoAgent client v{ProductInfo.Version}");
            return 0;
        }
        string? configPath = null;
        string? deviceArg = null, groupArg = null;
        var all = false;

        // Global options may appear anywhere; strip them out first.
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--config" when i + 1 < args.Count: configPath = args[i + 1]; args.RemoveRange(i, 2); i--; break;
                case "--device" when i + 1 < args.Count: deviceArg = args[i + 1]; args.RemoveRange(i, 2); i--; break;
                case "--group" when i + 1 < args.Count: groupArg = args[i + 1]; args.RemoveRange(i, 2); i--; break;
                case "--all": all = true; args.RemoveAt(i); i--; break;
                case "--json": CliOutput.JsonMode = true; args.RemoveAt(i); i--; break;
            }
        }

        if (args.Count == 0 || args[0] is "help" or "--help" or "-h")
        {
            PrintUsage();
            return args.Count == 0 ? 2 : 0;
        }

        var command = args[0];
        var rest = args.Skip(1).ToList();

        try
        {
            // Commands that don't need an agent connection.
            if (command == "keygen") return KeyGen(rest);

            var cfg = ClientConfig.Load(configPath);

            // Fleet registry commands (need the fleet server, not a device transport).
            if (command is "devices" or "approve" or "reject" or "rename" or "groups"
                or "rotate-device-token" or "mesh" or "resolve" or "enroll-key" or "subnets" or "acl"
                or "queue")
            {
                using var fleet = CreateFleetClient(cfg);
                return await FleetCommands.RunAsync(fleet, command, rest);
            }
            if (command == "desktop") return await DesktopCommand(cfg, deviceArg, rest);
            if (command == "tunnel") return await TunnelCommand.RunAsync(cfg, rest);
            if (command == "bench") return await BenchCommand.RunAsync(cfg, deviceArg, rest);

            // v1.2 fleet-level commands (activity, health, scripts, rollout, wake, exit node).
            if (command is "activity" or "health" or "wake" or "exit-node" or "scripts" or "rollout" or "access")
            {
                using var fleet = CreateFleetClient(cfg);
                var targets = new FleetTargets();
                if (deviceArg != null) targets.Codes.Add(deviceArg.ToUpperInvariant());
                else if (groupArg != null) targets.Group = groupArg;
                else if (all) targets.All = true;
                return await FleetCommandsV12.RunAsync(fleet, command, rest, targets);
            }

            using var transport = Transport.Create(cfg, deviceArg, groupArg, all);
            if (DeviceCommandsV12.Commands.Contains(command))
                return await DeviceCommandsV12.RunAsync(transport, cfg, command, rest);
            return await DeviceCommands.RunAsync(transport, cfg, command, rest);
        }
        catch (FileNotFoundException ex) { Console.Error.WriteLine($"error: {ex.Message}"); return 2; }
        catch (InvalidDataException ex) { Console.Error.WriteLine($"error: {ex.Message}"); return 2; }
        catch (HttpRequestException ex) { Console.Error.WriteLine($"error: {ex.Message}"); return 1; }
        catch (Exception ex) { Console.Error.WriteLine($"error: {ex.GetType().Name}: {ex.Message}"); return 1; }
    }

    // ---------- keygen ----------

    private static int KeyGen(List<string> rest)
    {
        var outDir = rest.Count >= 2 && rest[0] == "--out" ? rest[1]
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".esoagent");
        Directory.CreateDirectory(outDir);
        var keyPath = Path.Combine(outDir, "operator.key");
        if (File.Exists(keyPath))
        {
            Console.Error.WriteLine($"error: {keyPath} already exists — refusing to overwrite an operator key.");
            return 2;
        }
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        try { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
        var pub = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        Console.WriteLine($"Operator key written to {keyPath} (keep it secret).");
        Console.WriteLine("Public key — give this to the owner to set on the fleet server (panel Access tab or 'set-operator-key'):");
        Console.WriteLine(pub);
        return 0;
    }

    // ---------- desktop ----------

    private static async Task<int> DesktopCommand(ClientConfig cfg, string? deviceArg, List<string> rest)
    {
        string host;
        int port = cfg.Port;
        var target = deviceArg ?? (rest.Count > 0 ? rest[0] : null);
        if (target != null && DeviceIdentity.LooksLikeCode(target))
        {
            using var fleet = CreateFleetClient(cfg);
            var devices = await fleet.GetDevicesAsync();
            var device = devices.FirstOrDefault(d => d.Code.Equals(target, StringComparison.OrdinalIgnoreCase));
            if (device == null) { Console.Error.WriteLine($"error: no device with code {target}."); return 1; }
            if (device.Ips.Count == 0) { Console.Error.WriteLine("error: device has no known IPs yet."); return 1; }
            host = device.Ips[0];
        }
        else if (target != null)
        {
            var parts = target.Split(':');
            host = parts[0];
            if (parts.Length == 2 && int.TryParse(parts[1], out var p)) port = p;
        }
        else host = cfg.Host;

        var url = $"https://{host}:{port}/panel/";
        Console.WriteLine($"Owner panel (Desktop tab) for this device: {url}");
        Console.WriteLine("Log in with the OWNER password, open the Desktop tab, and start the session there.");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* headless — the printed URL is the deliverable */ }
        return 0;
    }

    // ---------- plumbing ----------

    public static FleetApiClient CreateFleetClient(ClientConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.FleetUrl))
            throw new InvalidDataException("Client config has no 'fleetUrl' — this command needs the fleet server.");
        if (string.IsNullOrWhiteSpace(cfg.OperatorKeyFile) || !File.Exists(cfg.OperatorKeyFile))
            throw new InvalidDataException("Client config 'operatorKeyFile' is missing or not found. Run 'eso keygen' first.");
        var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(cfg.OperatorKeyFile));
        return new FleetApiClient(cfg, key);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            eso — EsoAgent operator CLI

            Usage: eso [--config <path>] <command> [options]
            Targeting: --device <CODE|IP[:port]> | --group <name> | --all   (default: the config's host, direct)

            Device commands (identical on Windows and Linux agents):
              status                          OS, CPU/RAM, disks, battery, uptime, users, mesh state
              exec [--stream] [--env K=V] <file> [args]   run any process (unrestricted, as SYSTEM/root)
              shell [--env K=V] <sh> <script> script via powershell|pwsh|cmd|bash|sh
              ps <script>                     PowerShell shortcut
              launch <file> [args]            start a detached process owned by the agent
              launched                        list agent-launched processes
              job run [--shell <sh>] [--cwd <dir>] [--env K=V] <file|script> [args]
                                              start a DURABLE job (survives disconnects + agent restarts)
              job list | show <id> [--tail N] | cancel <id>
              supervise                       supervised processes + restart state
              inventory                       one-call machine snapshot (OS/CPU/RAM/GPU/tools/…)
              doctor                          agent self-checks (cert, disk, fleet link, clock skew, audit errors)
              logs <path> [--lines N] [--pattern X] [--follow]   tail a file; --follow streams (direct mode)
              ps-list                         list all processes
              kill <pid>                      kill a process
              put <local> <remote>            upload (chunked, resumable, SHA-256 verified)
              get <remote> <local>            download (SHA-256 verified)
              shot <out.png>                  screenshot (via the user-session companion)
              interactive <file> [args]       run in the logged-on user's session
              power <reboot|shutdown|sleep>
              tail [--lines N] [path]         agent audit log, or any file
              software list | install <id> | install-file <path> | uninstall <name> | upgrade [id]
              services list | start|stop|restart <name> | startup <name> <Automatic|Manual|Disabled>
              repair list | run <name>
              registry get <path> [name] | set <path> <name> <kind> <value>   (Windows agents)
              update <package.zip>            signed self-update (direct mode only)
              tunnel <localPort> <device> <remoteHost:remotePort> [--udp]   port forwarding (direct or fleet)
              tunnel --socks <localPort> <device>    dynamic SOCKS5 forwarding
              telemetry [--watch]             per-process CPU/RAM/GPU, disk IO, connections
              diagnose                        ONE report: OS errors, crashes, failed services, pressure, doctor
              see [--pixels] [--save-dir <dir>]   PERCEPTION STACK entry: UI trees + window summaries first; pixels only on request
              vision monitors|capture [--delta]|window <t>|region <x> <y> <w> <h>   all-monitors / window / region capture
              windows list|focus <id>|close <id>|wait <title> [secs]       window management
              launch-gui <file> [args] [--wait-title T] [--shot-after S]   GUI launch harness (user session)
              uitree <pid|title> | uitree click <target> <element>         UI structure dump + click-by-element
              watch events [--follow]         file-change events from the device's watches
              app list|presets|run <name> [--param k=v]... | app perceive <name> [--param k=v]... [--id-pass]
                                              app bridges (Blender/Unreal presets) + design-as-data perception
              sessions list|start <name> [shell]|input <name> <text>|read <name>|kill <name>
                                              persistent shell sessions (survive client disconnect)
              patch check|apply [ids...]      OS patch management (apt / Windows Update)
              schedules                       the device's own scheduled tasks + last-run results
              files list|rm|mv|mkdir <...>    file manager operations
              recordings list|get <name> <out>|rm <name>   desktop session recordings (.esorec)
              bench [--out <file>]            measure this deployment (RTT, throughput, desktop fps)
              intent ensure-service <name> <running|stopped> | ensure-software <id> [--absent] |
                     ensure-file <path> [--sha256 <hex>] | ensure-supervised <name>
                                              ONE declarative call, executed + verified agent-side, compact proof back
              events [--follow]               anomaly-only event stream (thresholds, crashes, file changes)

            Fleet commands (operator key):
              devices                         list the fleet (online first, with virtual IPs)
              queue list [--device <CODE>]    offline command queue (queued/delivered/expired)
              queue show <id>                 one queued command + its result once delivered
              approve|reject <CODE>           enrollment decisions
              rename <CODE> <name>            rename a device
              groups <CODE> <a,b,c>           set a device's groups
              rotate-device-token <CODE>      rotate a device's direct-mode token (shown once)
              mesh                            mesh state: virtual IPs, WireGuard keys, ACLs, enrollment keys
              resolve <name>                  name -> virtual IP / addresses
              enroll-key create [--uses N] [--note text] | revoke <id>
              subnets <CODE> <cidr,...>       approve the LAN subnets a device may route
              acl set <rules.json>            replace ACL rules (JSON: [{from,to,ports}])
              desktop [--device <CODE|IP>]    print/open the device panel's Desktop page
              activity [--device <CODE>] [--source S] [--since ISO] [--export json|csv] [--out <file>]
                                              the unified activity timeline (same feed in App + panel)
              health                          per-device health dashboard data (doctor snapshots)
              wake <CODE|name>                Wake-on-LAN via the server + online LAN peers
              exit-node <CODE|none>           designate the mesh exit node
              scripts list|add <name> <file>|show <id>|rm <id>|run <id> [--device|--group|--all]
                                              the fleet script library (audited runs)
              rollout <package.zip> [--device|--group|--all] | rollout status <id>
                                              fleet-wide signed update rollout, per-device results
                                              eso access grant <device> [--minutes N] [--uses N] [--view-only] [--no-exec] [--no-files] [--no-clipboard] [--tunnels] [--no-desktop] | codes | grants | revoke <id> | redeem <code> | status | exec <file> [args] | shell <script>

              keygen [--out <dir>]            generate this operator's signing keypair
            """);
    }
}

/// <summary>One device's reply to a call (direct mode yields exactly one).</summary>
public sealed record DeviceReply(string Label, int Status, byte[] Body, string? Error, bool Queued = false, string? QueueId = null)
{
    public bool Ok => Error == null && Status is >= 200 and < 300;
    public T Json<T>() => JsonSerializer.Deserialize<T>(Body, EsoJson.Options)
                          ?? throw new InvalidOperationException("Empty response.");
    public string Text => Encoding.UTF8.GetString(Body);
}

/// <summary>Transport abstraction: direct HMAC (ApiClient) or fleet relay (operator-signed envelopes).</summary>
/// <summary>Item 40: structured data is the CLI's native tongue too —
/// with --json, device commands print the API's typed JSON verbatim
/// instead of a human rendering of it.</summary>
public static class CliOutput
{
    public static bool JsonMode;
}

public sealed class Transport : IDisposable
{
    private readonly ApiClient? _direct;
    private readonly FleetApiClient? _fleet;
    private readonly FleetTargets? _targets;
    public bool IsFleet => _fleet != null;
    public bool IsMultiTarget => _targets is { All: true } || (_targets?.Group != null);
    public string ModeLabel { get; }

    private Transport(ApiClient? direct, FleetApiClient? fleet, FleetTargets? targets, string label)
    {
        _direct = direct; _fleet = fleet; _targets = targets; ModeLabel = label;
    }

    public static Transport Create(ClientConfig cfg, string? deviceArg, string? groupArg, bool all)
    {
        if (groupArg != null || all)
        {
            var fleet = Program.CreateFleetClient(cfg);
            return new Transport(null, fleet,
                new FleetTargets { Group = groupArg, All = all },
                all ? "fleet (--all)" : $"fleet (group {groupArg})");
        }
        if (deviceArg != null && DeviceIdentity.LooksLikeCode(deviceArg))
        {
            var fleet = Program.CreateFleetClient(cfg);
            return new Transport(null, fleet,
                new FleetTargets { Codes = new List<string> { deviceArg.ToUpperInvariant() } },
                $"fleet (device {deviceArg.ToUpperInvariant()})");
        }
        var directCfg = cfg;
        if (deviceArg != null)
        {
            // Direct by IP/host — the config's token + fingerprint are that device's.
            var parts = deviceArg.Split(':');
            directCfg = cfg.WithEndpoint(parts[0], parts.Length == 2 && int.TryParse(parts[1], out var p) ? p : cfg.Port);
        }
        return new Transport(new ApiClient(directCfg), null, null, $"direct ({directCfg.Host}:{directCfg.Port})");
    }

    public async Task<List<DeviceReply>> SendAsync(string method, string path, object? bodyObj = null, int timeoutSeconds = 300, bool queueIfOffline = false)
    {
        var body = bodyObj == null ? Array.Empty<byte>() : JsonSerializer.SerializeToUtf8Bytes(bodyObj, EsoJson.Options);
        return await SendRawAsync(method, path, body, timeoutSeconds, queueIfOffline);
    }

    public async Task<List<DeviceReply>> SendRawAsync(string method, string path, byte[] body, int timeoutSeconds = 300, bool queueIfOffline = false)
    {
        if (_direct != null)
        {
            var (status, bytes) = await _direct.RawSendAsync(method, path, body);
            return new List<DeviceReply> { new("device", status, bytes, null) };
        }
        var req = _fleet!.BuildCommand(_targets!, method, path, body, timeoutSeconds, queueIfOffline);
        var response = await _fleet.PostCommandAsync(req);
        return response.Results.Select(r => new DeviceReply(
            r.Code,
            r.HttpStatus ?? 0,
            string.IsNullOrEmpty(r.BodyBase64) ? Array.Empty<byte>() : Convert.FromBase64String(r.BodyBase64),
            r.Error,
            r.Queued,
            r.QueueId)).ToList();
    }

    /// <summary>Direct-mode streaming exec (fleet mode falls back to a normal call).</summary>
    public ApiClient? DirectClient => _direct;

    public void Dispose()
    {
        _direct?.Dispose();
        _fleet?.Dispose();
    }
}
