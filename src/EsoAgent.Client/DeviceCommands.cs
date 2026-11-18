using System.Text;
using System.Text.Json;
using EsoAgent.Core;

namespace EsoAgent.Client;

/// <summary>Device commands: identical behavior in direct and fleet mode — the
/// transport decides how the call reaches the agent. Fan-out prints a section
/// per device and never drops a failure silently.</summary>
public static class DeviceCommands
{
    public static async Task<int> RunAsync(Transport transport, ClientConfig cfg, string command, List<string> rest)
    {
        switch (command)
        {
            case "status": return await ForEach(transport, "GET", "/api/status", null, r => PrintStatus(r.Json<StatusResponse>()));
            case "exec": return await ExecAsync(transport, rest);
            case "shell": return await ShellAsync(transport, rest, shellArg: true);
            case "ps": return await ShellAsync(transport, rest, shellArg: false);
            case "launch": return await LaunchAsync(transport, rest);
            case "launched": return await ForEach(transport, "GET", "/api/launched", null, r =>
            {
                foreach (var p in r.Json<List<LaunchedProcessDto>>())
                    Console.WriteLine($"{p.Pid}\t{(p.StillRunning ? "running" : "exited")}\t{p.StartedAt:u}\t{p.File}");
            });
            case "ps-list": return await ForEach(transport, "GET", "/api/processes", null, r =>
            {
                foreach (var p in r.Json<List<ProcessDto>>().OrderBy(p => p.Pid))
                    Console.WriteLine($"{p.Pid}\t{p.ParentPid}\ts{p.SessionId}\t{p.WorkingSetBytes / 1048576} MB\t{p.Name}");
            });
            case "kill":
            {
                if (rest.Count < 1 || !int.TryParse(rest[0], out var pid)) return Usage("eso kill <pid>");
                return await ForEach(transport, "POST", $"/api/processes/{pid}/kill", null, r => PrintOp(r.Json<OperationResult>()));
            }
            case "put": return await PutAsync(transport, rest);
            case "get": return await GetAsync(transport, rest);
            case "shot": return await ShotAsync(transport, rest);
            case "interactive": return await InteractiveAsync(transport, rest);
            case "power":
            {
                if (rest.Count < 1 || rest[0] is not ("reboot" or "shutdown" or "sleep")) return Usage("eso power <reboot|shutdown|sleep>");
                return await ForEach(transport, "POST", $"/api/power/{rest[0]}", null, r => PrintOp(r.Json<OperationResult>()));
            }
            case "tail": return await TailAsync(transport, rest);
            case "job": return await JobAsync(transport, rest);
            case "supervise": return await ForEach(transport, "GET", "/api/supervised", null, r =>
            {
                var entries = r.Json<List<SupervisedStatusDto>>();
                if (entries.Count == 0) { Console.WriteLine("(no supervised processes configured)"); return; }
                foreach (var s in entries)
                    Console.WriteLine($"{s.Name}\t{s.State}\tpid {(s.Pid?.ToString() ?? "—")}\trestarts {s.Restarts}\tpolicy {s.Policy}\tlast exit {(s.LastExitCode?.ToString() ?? "—")}");
            });
            case "inventory": return await ForEach(transport, "GET", "/api/inventory", null, r => PrintInventory(r.Json<InventoryResponse>()), 120);
            case "doctor": return await DoctorAsync(transport);
            case "logs": return await LogsAsync(transport, rest);
            case "software": return await SoftwareAsync(transport, rest);
            case "services": return await ServicesAsync(transport, rest);
            case "repair": return await RepairAsync(transport, rest);
            case "registry": return await RegistryAsync(transport, rest);
            case "update": return await UpdateAsync(transport, cfg, rest);
            default:
                Console.Error.WriteLine($"error: unknown command '{command}'. Run 'eso help'.");
                return 2;
        }
    }

    // ---------- helpers ----------

    private static int Usage(string usage)
    {
        Console.Error.WriteLine($"usage: {usage}");
        return 2;
    }

    /// <summary>Runs one call over the transport; prints each device's section; exit 0 iff every device answered OK.</summary>
    private static async Task<int> ForEach(Transport transport, string method, string path, object? body, Action<DeviceReply> print, int timeoutSeconds = 300)
    {
        var replies = await transport.SendAsync(method, path, body, timeoutSeconds);
        var multi = replies.Count > 1;
        var allOk = replies.Count > 0;
        foreach (var reply in replies)
        {
            if (multi) Console.WriteLine($"===== {reply.Label} =====");
            if (!reply.Ok)
            {
                Console.Error.WriteLine($"[{reply.Label}] FAILED: {reply.Error ?? $"HTTP {reply.Status}: {Truncate(reply.Text, 400)}"}");
                allOk = false;
                continue;
            }
            if (CliOutput.JsonMode) { Console.WriteLine(reply.Text); continue; }
            try { print(reply); }
            catch (Exception ex) { Console.Error.WriteLine($"[{reply.Label}] bad response: {ex.Message}"); allOk = false; }
        }
        return allOk ? 0 : 1;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static void PrintOp(OperationResult op)
    {
        Console.WriteLine($"{(op.Success ? "OK" : "FAILED")} (verified: {op.Verified}) {op.Message}");
        if (!string.IsNullOrWhiteSpace(op.Output)) Console.WriteLine(op.Output.TrimEnd());
    }

    private static int PrintExec(ExecResponse res)
    {
        Console.Write(res.Stdout);
        Console.Error.Write(res.Stderr);
        if (res.TimedOut) Console.Error.WriteLine("[timed out]");
        if (res.Truncated) Console.Error.WriteLine("[output truncated at capture cap]");
        return res.ExitCode ?? 1;
    }

    private static void PrintStatus(StatusResponse s)
    {
        Console.WriteLine($"{s.DeviceName} ({s.DeviceCode}) — {s.Os} · {s.OsDescription}");
        Console.WriteLine($"agent {s.AgentVersion} · uptime {TimeSpan.FromSeconds(s.UptimeSeconds)} · host {s.Hostname} · locked: {s.Locked}");
        if (s.CpuPercent != null) Console.WriteLine($"CPU {s.CpuPercent:F1}%");
        if (s.RamTotalBytes > 0) Console.WriteLine($"RAM {s.RamUsedBytes / 1048576} / {s.RamTotalBytes / 1048576} MB");
        foreach (var d in s.Disks) Console.WriteLine($"disk {d.Name}: {d.FreeBytes / 1073741824} GB free / {d.TotalBytes / 1073741824} GB");
        if (s.Battery is { Present: true }) Console.WriteLine($"battery {s.Battery.Percent}% · AC: {s.Battery.OnAc}");
        if (s.LoggedOnUsers.Count > 0) Console.WriteLine($"users: {string.Join(", ", s.LoggedOnUsers)}");
        if (s.TailnetIps.Count > 0) Console.WriteLine($"tailnet: {string.Join(", ", s.TailnetIps)}");
        Console.WriteLine($"fleet: {s.FleetMode}{(s.FleetMode == "Fleet" ? $" (connected: {s.FleetConnected})" : "")}");
        if (s.MeshEnabled) Console.WriteLine($"mesh: vip {s.MeshVirtualIp ?? "—"} · {s.MeshStatus}");
        foreach (var sup in s.Supervised)
            Console.WriteLine($"supervised {sup.Name}: {sup.State} (restarts {sup.Restarts}, policy {sup.Policy})");
        if (s.JobsRunning > 0) Console.WriteLine($"jobs running: {s.JobsRunning}");
    }

    // ---------- exec family ----------

    /// <summary>Parses KEY=VALUE pairs for --env (values may contain '=').</summary>
    private static bool TryParseEnv(string pair, Dictionary<string, string> env)
    {
        var eq = pair.IndexOf('=');
        if (eq <= 0) return false;
        env[pair[..eq]] = pair[(eq + 1)..];
        return true;
    }

    private static async Task<int> ExecAsync(Transport transport, List<string> rest)
    {
        var stream = false; string? cwd = null; var timeoutMs = 60_000;
        var env = new Dictionary<string, string>();
        var i = 0;
        while (i < rest.Count && rest[i].StartsWith("--"))
        {
            switch (rest[i])
            {
                case "--stream": stream = true; i++; break;
                case "--cwd" when i + 1 < rest.Count: cwd = rest[i + 1]; i += 2; break;
                case "--timeout" when i + 1 < rest.Count && int.TryParse(rest[i + 1], out var sec): timeoutMs = sec * 1000; i += 2; break;
                case "--env" when i + 1 < rest.Count && TryParseEnv(rest[i + 1], env): i += 2; break;
                default: return Usage("eso exec [--stream] [--cwd <dir>] [--timeout <sec>] [--env K=V] <file> [args...]");
            }
        }
        if (i >= rest.Count) return Usage("eso exec [--stream] [--cwd <dir>] [--timeout <sec>] [--env K=V] <file> [args...]");
        var req = new ExecRequest
        {
            File = rest[i],
            Args = rest.Skip(i + 1).ToArray(),
            Cwd = cwd,
            Env = env.Count > 0 ? env : null,
            TimeoutMs = timeoutMs,
        };

        if (stream)
        {
            if (transport.DirectClient == null)
            {
                Console.Error.WriteLine("note: --stream is direct-mode only; running non-streamed over the fleet relay.");
            }
            else return await transport.DirectClient.ExecStreamAsync(req);
        }
        var replies = await transport.SendAsync("POST", "/api/exec", req, timeoutMs / 1000 + 30, queueIfOffline: true);
        return PrintExecReplies(replies);
    }

    private static async Task<int> ShellAsync(Transport transport, List<string> rest, bool shellArg)
    {
        var env = new Dictionary<string, string>();
        var i = 0;
        while (i + 1 < rest.Count && rest[i] == "--env" && TryParseEnv(rest[i + 1], env)) i += 2;
        rest = rest.Skip(i).ToList();
        string shell; string script;
        if (shellArg)
        {
            if (rest.Count < 2) return Usage("eso shell [--env K=V] <powershell|pwsh|cmd|bash|sh> <script>");
            shell = rest[0]; script = string.Join(" ", rest.Skip(1));
        }
        else
        {
            if (rest.Count < 1) return Usage("eso ps [--env K=V] <script>");
            shell = "powershell"; script = string.Join(" ", rest);
        }
        var replies = await transport.SendAsync("POST", "/api/shell",
            new ShellRequest { Shell = shell, Script = script, Env = env.Count > 0 ? env : null }, 330, queueIfOffline: true);
        return PrintExecReplies(replies);
    }

    private static int PrintExecReplies(List<DeviceReply> replies)
    {
        var multi = replies.Count > 1;
        var exit = 0;
        foreach (var reply in replies)
        {
            if (multi) Console.WriteLine($"===== {reply.Label} =====");
            if (reply.Queued)
            {
                // Accepted for later delivery — a successful hand-off, not a run.
                Console.WriteLine($"[{reply.Label}] QUEUED (device offline) — queue id {reply.QueueId}");
                Console.WriteLine($"  result later: eso queue show {reply.QueueId}");
                continue;
            }
            if (!reply.Ok)
            {
                Console.Error.WriteLine($"[{reply.Label}] FAILED: {reply.Error ?? $"HTTP {reply.Status}: {Truncate(reply.Text, 400)}"}");
                exit = 1;
                continue;
            }
            var code = PrintExec(reply.Json<ExecResponse>());
            if (multi && code != 0) exit = 1;
            if (!multi) exit = code;
        }
        return exit;
    }

    private static async Task<int> LaunchAsync(Transport transport, List<string> rest)
    {
        if (rest.Count < 1) return Usage("eso launch <file> [args...]");
        var req = new ExecRequest { File = rest[0], Args = rest.Skip(1).ToArray() };
        return await ForEach(transport, "POST", "/api/launch", req, r =>
            Console.WriteLine($"launched pid {r.Json<LaunchResponse>().Pid}"));
    }

    private static async Task<int> InteractiveAsync(Transport transport, List<string> rest)
    {
        if (rest.Count < 1) return Usage("eso interactive <file> [args...]");
        var req = new ExecRequest { File = rest[0], Args = rest.Skip(1).ToArray() };
        var replies = await transport.SendAsync("POST", "/api/interactive/exec", req, 330);
        return PrintExecReplies(replies);
    }

    // ---------- files ----------

    private static async Task<int> PutAsync(Transport transport, List<string> rest)
    {
        if (rest.Count < 2) return Usage("eso put <local> <remote>");
        if (transport.IsMultiTarget)
        {
            Console.Error.WriteLine("error: 'put' targets one device at a time (chunked upload state is per-device). Use --device <CODE|IP>.");
            return 2;
        }
        var (local, remote) = (rest[0], rest[1]);
        if (!File.Exists(local)) { Console.Error.WriteLine($"error: local file not found: {local}"); return 2; }
        var chunkSize = transport.IsFleet ? 256 * 1024 : 4 * 1024 * 1024;
        var size = new FileInfo(local).Length;
        var sha = FileTransferService.HashFile(local);

        var replies = await transport.SendAsync("POST", "/api/files/begin", new BeginUploadRequest { Path = remote });
        var failures = 0;
        foreach (var reply in replies)
        {
            if (!reply.Ok) { Console.Error.WriteLine($"[{reply.Label}] begin FAILED: {reply.Error ?? reply.Text}"); failures++; continue; }
            // NOTE: fan-out upload sends the same stream to every target; each reply's
            // resume offset is honored per device in turn.
            var offset = reply.Json<BeginUploadResponse>().Received;
            if (offset > size) offset = 0; // stale partial from another file — restart (server will reject mismatched offsets; begin again not possible, so report)
            try
            {
                await using var fs = File.OpenRead(local);
                fs.Seek(offset, SeekOrigin.Begin);
                var buffer = new byte[chunkSize];
                var current = offset;
                int read;
                while ((read = await fs.ReadAsync(buffer)) > 0)
                {
                    var chunk = read == buffer.Length ? buffer : buffer[..read];
                    var path = $"/api/files/chunk?path={Uri.EscapeDataString(remote)}&offset={current}";
                    var rs = transport.IsFleet || replies.Count > 1
                        ? await transport.SendRawAsync("PUT", path, chunk, 120)
                        : await transport.SendRawAsync("PUT", path, chunk, 120);
                    var cr = rs.FirstOrDefault(x => x.Label == reply.Label) ?? rs[0];
                    if (!cr.Ok) throw new InvalidOperationException($"chunk at {current} failed: {cr.Error ?? cr.Text}");
                    current = cr.Json<ChunkResponse>().Received;
                }
                var commit = await transport.SendAsync("POST", "/api/files/commit", new CommitUploadRequest { Path = remote, Sha256 = sha });
                var cres = commit.FirstOrDefault(x => x.Label == reply.Label) ?? commit[0];
                if (!cres.Ok) throw new InvalidOperationException($"commit failed: {cres.Error ?? cres.Text}");
                var info = cres.Json<FileInfoResponse>();
                Console.WriteLine($"[{reply.Label}] uploaded {info.Size} bytes, sha256 verified: {info.Sha256 == sha}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[{reply.Label}] upload FAILED: {ex.Message}");
                failures++;
            }
        }
        return failures == 0 ? 0 : 1;
    }

    private static async Task<int> GetAsync(Transport transport, List<string> rest)
    {
        if (rest.Count < 2) return Usage("eso get <remote> <local>");
        if (transport.IsFleet) { Console.Error.WriteLine("note: 'get' downloads from the first target device."); }
        var (remote, local) = (rest[0], rest[1]);
        var infoReplies = await transport.SendAsync("GET", $"/api/files/info?path={Uri.EscapeDataString(remote)}", null);
        var infoReply = infoReplies.FirstOrDefault(r => r.Ok);
        if (infoReply == null) { Console.Error.WriteLine($"error: cannot stat {remote} on the target."); return 1; }
        var info = infoReply.Json<FileInfoResponse>();
        if (!info.Exists) { Console.Error.WriteLine($"error: {remote} does not exist on the device."); return 1; }

        const int chunk = 4 * 1024 * 1024;
        await using (var outFs = new FileStream(local, FileMode.Create, FileAccess.Write))
        {
            long offset = 0;
            while (offset < info.Size)
            {
                var length = Math.Min(chunk, info.Size - offset);
                var path = $"/api/files/download?path={Uri.EscapeDataString(remote)}&offset={offset}&length={length}";
                var rs = await transport.SendRawAsync("GET", path, Array.Empty<byte>(), 300);
                var r = rs.FirstOrDefault(x => x.Label == infoReply.Label) ?? rs[0];
                if (!r.Ok) { Console.Error.WriteLine($"error: download failed at {offset}: {r.Error ?? r.Text}"); return 1; }
                await outFs.WriteAsync(r.Body);
                offset += r.Body.Length;
            }
        }
        var sha = FileTransferService.HashFile(local);
        var verified = info.Sha256 == null || string.Equals(info.Sha256, sha, StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($"downloaded {info.Size} bytes -> {local}; sha256 verified: {verified}");
        return verified ? 0 : 1;
    }

    private static async Task<int> ShotAsync(Transport transport, List<string> rest)
    {
        if (rest.Count < 1) return Usage("eso shot <out.png>");
        var replies = await transport.SendRawAsync("GET", "/api/screenshot", Array.Empty<byte>(), 60);
        var reply = replies[0];
        if (!reply.Ok) { Console.Error.WriteLine($"error: screenshot failed: {reply.Error ?? Truncate(reply.Text, 300)}"); return 1; }
        await File.WriteAllBytesAsync(rest[0], reply.Body);
        Console.WriteLine($"saved {reply.Body.Length} bytes -> {rest[0]}");
        return 0;
    }

    // ---------- logs / software / services / repair / registry / update ----------

    private static async Task<int> TailAsync(Transport transport, List<string> rest)
    {
        var lines = 100; string? path = null;
        for (int i = 0; i < rest.Count; i++)
        {
            if (rest[i] == "--lines" && i + 1 < rest.Count && int.TryParse(rest[i + 1], out var n)) { lines = n; i++; }
            else path = rest[i];
        }
        var url = path == null
            ? $"/api/logs/agent?lines={lines}"
            : $"/api/logs/file?path={Uri.EscapeDataString(path)}&lines={lines}";
        return await ForEach(transport, "GET", url, null, r =>
        {
            using var doc = JsonDocument.Parse(r.Body);
            foreach (var line in doc.RootElement.GetProperty("lines").EnumerateArray())
                Console.WriteLine(line.GetString());
        });
    }

    // ---------- jobs / doctor / logs / inventory (v1.1) ----------

    private static async Task<int> JobAsync(Transport transport, List<string> rest)
    {
        if (rest.Count < 1) return Usage("eso job <run|list|show|cancel> ...");
        switch (rest[0])
        {
            case "run":
            {
                string? shell = null, cwd = null;
                var env = new Dictionary<string, string>();
                var i = 1;
                while (i < rest.Count && rest[i].StartsWith("--"))
                {
                    switch (rest[i])
                    {
                        case "--shell" when i + 1 < rest.Count: shell = rest[i + 1]; i += 2; break;
                        case "--cwd" when i + 1 < rest.Count: cwd = rest[i + 1]; i += 2; break;
                        case "--env" when i + 1 < rest.Count && TryParseEnv(rest[i + 1], env): i += 2; break;
                        default: return Usage("eso job run [--shell <sh>] [--cwd <dir>] [--env K=V] <file|script> [args...]");
                    }
                }
                if (i >= rest.Count) return Usage("eso job run [--shell <sh>] [--cwd <dir>] [--env K=V] <file|script> [args...]");
                var req = shell != null
                    ? new JobStartRequest { Shell = shell, Command = string.Join(" ", rest.Skip(i)), Cwd = cwd, Env = env.Count > 0 ? env : null }
                    : new JobStartRequest { Command = rest[i], Args = rest.Skip(i + 1).ToArray(), Cwd = cwd, Env = env.Count > 0 ? env : null };
                return await ForEach(transport, "POST", "/api/jobs", req, r =>
                {
                    var j = r.Json<JobRecord>();
                    Console.WriteLine($"job {j.Id} started · state {j.State} · pid {j.Pid}");
                    Console.WriteLine($"  follow: eso job show {j.Id}");
                }, 120);
            }
            case "list":
                return await ForEach(transport, "GET", "/api/jobs", null, r =>
                {
                    var jobs = r.Json<List<JobRecord>>();
                    if (jobs.Count == 0) { Console.WriteLine("(no jobs)"); return; }
                    foreach (var j in jobs)
                        Console.WriteLine($"{j.Id}\t{j.State}\tpid {j.Pid}\t{j.StartedAt:u}\t{(j.Shell != null ? $"[{j.Shell}] " : "")}{j.Command}{(j.ExitCode != null ? $"  (exit {j.ExitCode})" : "")}");
                });
            case "show":
            {
                if (rest.Count < 2) return Usage("eso job show <id> [--tail N]");
                var tail = 100;
                var ti = rest.IndexOf("--tail");
                if (ti >= 0 && ti + 1 < rest.Count && int.TryParse(rest[ti + 1], out var n)) tail = n;
                return await ForEach(transport, "GET", $"/api/jobs/{rest[1]}?tail={tail}", null, r =>
                {
                    var j = r.Json<JobRecord>();
                    Console.WriteLine($"job {j.Id} · {j.State} · pid {j.Pid}");
                    Console.WriteLine($"command: {(j.Shell != null ? $"[{j.Shell}] " : "")}{j.Command} {string.Join(" ", j.Args)}");
                    Console.WriteLine($"started {j.StartedAt:u}{(j.EndedAt != null ? $" · ended {j.EndedAt:u}" : "")}{(j.ExitCode != null ? $" · exit {j.ExitCode}" : "")}");
                    if (j.EnvKeys.Count > 0) Console.WriteLine($"env keys: {string.Join(", ", j.EnvKeys)} (values never stored)");
                    if (!string.IsNullOrEmpty(j.StdoutTail)) { Console.WriteLine("--- stdout (tail) ---"); Console.WriteLine(j.StdoutTail); }
                    if (!string.IsNullOrEmpty(j.StderrTail)) { Console.WriteLine("--- stderr (tail) ---"); Console.WriteLine(j.StderrTail); }
                });
            }
            case "cancel":
            {
                if (rest.Count < 2) return Usage("eso job cancel <id>");
                return await ForEach(transport, "POST", $"/api/jobs/{rest[1]}/cancel", null, r =>
                {
                    var j = r.Json<JobRecord>();
                    Console.WriteLine($"job {j.Id} -> {j.State}");
                });
            }
            default: return Usage("eso job <run|list|show|cancel> ...");
        }
    }

    private static async Task<int> DoctorAsync(Transport transport)
    {
        var replies = await transport.SendAsync("GET", "/api/doctor", null, 120);
        var multi = replies.Count > 1;
        var exit = 0;
        foreach (var reply in replies)
        {
            if (multi) Console.WriteLine($"===== {reply.Label} =====");
            if (!reply.Ok)
            {
                Console.Error.WriteLine($"[{reply.Label}] FAILED: {reply.Error ?? $"HTTP {reply.Status}: {Truncate(reply.Text, 400)}"}");
                exit = 1;
                continue;
            }
            var doc = reply.Json<DoctorResponse>();
            Console.WriteLine($"overall: {doc.Overall.ToUpperInvariant()}");
            foreach (var check in doc.Checks)
                Console.WriteLine($"  [{check.Status.ToUpperInvariant(),-4}] {check.Name} — {check.Detail}");
            if (doc.Overall == "fail") exit = 1;
        }
        return exit;
    }

    private static async Task<int> LogsAsync(Transport transport, List<string> rest)
    {
        string? path = null, pattern = null;
        var lines = 100;
        var follow = false;
        for (int i = 0; i < rest.Count; i++)
        {
            switch (rest[i])
            {
                case "--follow": follow = true; break;
                case "--lines" when i + 1 < rest.Count && int.TryParse(rest[i + 1], out var n): lines = n; i++; break;
                case "--pattern" when i + 1 < rest.Count: pattern = rest[i + 1]; i++; break;
                default: path ??= rest[i]; break;
            }
        }
        if (path == null) return Usage("eso logs <path> [--lines N] [--pattern X] [--follow]");

        if (follow)
        {
            if (transport.DirectClient == null)
            {
                Console.Error.WriteLine("error: --follow is direct-mode only (the fleet relay has no stream channel). Target the device by IP: eso --device <IP> logs <path> --follow");
                return 2;
            }
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            Console.Error.WriteLine($"following {path} (Ctrl+C to stop)…");
            return await transport.DirectClient.FollowLogAsync(
                new LogFollowRequest { Path = path, TailLines = lines, Pattern = pattern }, cts.Token);
        }

        var url = $"/api/logs/file?path={Uri.EscapeDataString(path)}&lines={lines}";
        return await ForEach(transport, "GET", url, null, r =>
        {
            using var doc = JsonDocument.Parse(r.Body);
            foreach (var lineEl in doc.RootElement.GetProperty("lines").EnumerateArray())
            {
                var line = lineEl.GetString() ?? "";
                if (LogFollowFilter.Matches(line, pattern)) Console.WriteLine(line);
            }
        });
    }

    private static void PrintInventory(InventoryResponse inv)
    {
        Console.WriteLine($"{inv.Hostname} — {inv.Os} · {inv.OsDescription}");
        if (inv.CpuModel != null) Console.WriteLine($"CPU {inv.CpuModel} · {inv.CpuCores} cores");
        if (inv.RamTotalBytes > 0) Console.WriteLine($"RAM {inv.RamFreeBytes / 1073741824.0:F1} GB free / {inv.RamTotalBytes / 1073741824.0:F1} GB");
        foreach (var v in inv.Volumes) Console.WriteLine($"volume {v.Name}: {v.FreeBytes / 1073741824.0:F1} GB free / {v.TotalBytes / 1073741824.0:F1} GB");
        Console.WriteLine($"GPU: {inv.Gpu ?? "—"}");
        foreach (var nic in inv.NetworkInterfaces)
            Console.WriteLine($"net {nic.Name}{(nic.IsLoopback ? " (loopback)" : "")}: {string.Join(", ", nic.Ips)}");
        Console.WriteLine("tools:");
        foreach (var t in inv.Tools)
            Console.WriteLine($"  {t.Name}: {(t.Present ? t.Version ?? "present (version unknown)" : "absent")}");
        Console.WriteLine($"agent {inv.AgentVersion} · agent uptime {TimeSpan.FromSeconds(inv.AgentUptimeSeconds)} · system uptime {TimeSpan.FromSeconds(inv.SystemUptimeSeconds)}");
        if (inv.InteractiveSessions.Count > 0) Console.WriteLine($"sessions: {string.Join(", ", inv.InteractiveSessions)}");
    }

    private static async Task<int> SoftwareAsync(Transport transport, List<string> rest)
    {
        if (rest.Count < 1) return Usage("eso software <list|install|install-file|uninstall|upgrade> ...");
        switch (rest[0])
        {
            case "list": return await ForEach(transport, "GET", "/api/software", null, r =>
            {
                foreach (var p in r.Json<List<SoftwarePackageDto>>())
                    Console.WriteLine($"{p.Name}\t{p.Version ?? ""}\t{p.Publisher ?? ""}\t{p.Id ?? ""}\t[{p.Source}]");
            }, 300);
            case "install":
                if (rest.Count < 2) return Usage("eso software install <winget-id|package>");
                return await ForEach(transport, "POST", "/api/software/install",
                    new InstallRequest { Kind = "manager", Id = rest[1] }, r => PrintOp(r.Json<OperationResult>()), 900);
            case "install-file":
            {
                if (rest.Count < 2) return Usage("eso software install-file <remote-path> [--args \"/qn ...\"]");
                string[]? silent = null;
                var ai = rest.IndexOf("--args");
                if (ai >= 0 && ai + 1 < rest.Count) silent = rest[ai + 1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return await ForEach(transport, "POST", "/api/software/install",
                    new InstallRequest { Kind = "file", Path = rest[1], SilentArgs = silent }, r => PrintOp(r.Json<OperationResult>()), 900);
            }
            case "uninstall":
                if (rest.Count < 2) return Usage("eso software uninstall <name|id|product-code>");
                return await ForEach(transport, "POST", "/api/software/uninstall",
                    new UninstallRequest { Name = string.Join(" ", rest.Skip(1)) }, r => PrintOp(r.Json<OperationResult>()), 900);
            case "upgrade":
                return await ForEach(transport, "POST", "/api/software/upgrade",
                    new UpgradeRequest { Id = rest.Count > 1 ? rest[1] : null }, r => PrintOp(r.Json<OperationResult>()), 1200);
            default: return Usage("eso software <list|install|install-file|uninstall|upgrade> ...");
        }
    }

    private static async Task<int> ServicesAsync(Transport transport, List<string> rest)
    {
        if (rest.Count < 1) return Usage("eso services <list|start|stop|restart|startup> ...");
        switch (rest[0])
        {
            case "list": return await ForEach(transport, "GET", "/api/services", null, r =>
            {
                foreach (var s in r.Json<List<ServiceDto>>())
                    Console.WriteLine($"{s.Name}\t{s.Status}\t{s.StartupMode ?? ""}\t{s.DisplayName ?? ""}");
            }, 300);
            case "start": case "stop": case "restart":
                if (rest.Count < 2) return Usage($"eso services {rest[0]} <name>");
                return await ForEach(transport, "POST", $"/api/services/{Uri.EscapeDataString(rest[1])}/{rest[0]}", null,
                    r => PrintOp(r.Json<OperationResult>()), 300);
            case "startup":
                if (rest.Count < 3) return Usage("eso services startup <name> <Automatic|Manual|Disabled>");
                return await ForEach(transport, "PUT", $"/api/services/{Uri.EscapeDataString(rest[1])}/startup",
                    new StartupModeRequest { Mode = rest[2] }, r => PrintOp(r.Json<OperationResult>()), 300);
            default: return Usage("eso services <list|start|stop|restart|startup> ...");
        }
    }

    private static async Task<int> RepairAsync(Transport transport, List<string> rest)
    {
        if (rest.Count < 1) return Usage("eso repair <list|run> ...");
        switch (rest[0])
        {
            case "list": return await ForEach(transport, "GET", "/api/repair", null, r =>
            {
                foreach (var item in r.Json<List<RepairInfoDto>>())
                    Console.WriteLine($"{item.Name}\t{item.Description}");
            });
            case "run":
                if (rest.Count < 2) return Usage("eso repair run <name>");
                return await ForEach(transport, "POST", $"/api/repair/{Uri.EscapeDataString(rest[1])}", null,
                    r => PrintOp(r.Json<OperationResult>()), 1900);
            default: return Usage("eso repair <list|run> ...");
        }
    }

    private static async Task<int> RegistryAsync(Transport transport, List<string> rest)
    {
        if (rest.Count < 2) return Usage("eso registry <get|set> <path> ...");
        switch (rest[0])
        {
            case "get":
            {
                var name = rest.Count > 2 ? rest[2] : "";
                var url = $"/api/registry?path={Uri.EscapeDataString(rest[1])}&name={Uri.EscapeDataString(name)}";
                return await ForEach(transport, "GET", url, null, r =>
                {
                    var v = r.Json<RegistryGetResponse>();
                    Console.WriteLine(v.ValueExists ? $"{v.Kind}: {v.Value}" : "(value not present)");
                });
            }
            case "set":
                if (rest.Count < 5) return Usage("eso registry set <path> <name> <kind> <value>");
                return await ForEach(transport, "PUT", "/api/registry",
                    new RegistrySetRequest { Path = rest[1], Name = rest[2], Kind = rest[3], Value = rest[4] },
                    r =>
                    {
                        var v = r.Json<RegistryGetResponse>();
                        Console.WriteLine($"set -> {v.Kind}: {v.Value}");
                    });
            default: return Usage("eso registry <get|set> <path> ...");
        }
    }

    private static async Task<int> UpdateAsync(Transport transport, ClientConfig cfg, List<string> rest)
    {
        if (rest.Count < 1) return Usage("eso update <package.zip>");
        if (transport.DirectClient == null)
        {
            Console.Error.WriteLine("error: self-update is direct-mode only (it is an operator-tier action against one device).");
            return 2;
        }
        var sha = await transport.DirectClient.PutUpdatePackageAsync(rest[0]);
        Console.WriteLine($"package staged (sha256 {sha}); applying…");
        var replies = await transport.SendAsync("POST", "/api/update/apply", null, 300);
        return PrintExecRepliesStatus(replies);
    }

    private static int PrintExecRepliesStatus(List<DeviceReply> replies)
    {
        var ok = true;
        foreach (var reply in replies)
        {
            Console.WriteLine($"[{reply.Label}] {(reply.Ok ? "update applied" : "FAILED")}: {Truncate(reply.Text, 300)}");
            ok &= reply.Ok;
        }
        return ok ? 0 : 1;
    }
}
