using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EsoAgent.Core;

namespace EsoAgent.Client;

/// <summary>
/// v1.2 fleet commands: the unified activity timeline, device health,
/// Wake-on-LAN, the script library, fleet-wide update rollout, and the
/// mesh exit-node switch. All go through the operator-signed fleet API —
/// the same data the app and the panel show, because there is only one
/// store behind all three faces.
/// </summary>
public static class FleetCommandsV12
{
    public static async Task<int> RunAsync(FleetApiClient fleet, string command, List<string> rest, FleetTargets targets)
    {
        switch (command)
        {
            case "activity": return await ActivityAsync(fleet, rest);
            case "health": return await HealthAsync(fleet);
            case "wake":
            {
                if (rest.Count < 1) return Usage("eso wake <CODE|name>");
                var results = await fleet.WakeAsync(rest[0]);
                if (results.Count == 0) { Console.WriteLine("No MAC addresses recorded for that device — it has never reported any (it must come online once with v1.2+)."); return 1; }
                foreach (var r in results)
                {
                    Console.WriteLine($"{r.Mac}: sent by [{string.Join(", ", r.SentBy)}]");
                    foreach (var e in r.Errors) Console.Error.WriteLine($"  error: {e}");
                }
                return 0;
            }
            case "exit-node":
            {
                if (rest.Count < 1) return Usage("eso exit-node <CODE|none>");
                var code = rest[0].Equals("none", StringComparison.OrdinalIgnoreCase) ? null : rest[0].ToUpperInvariant();
                await fleet.SetExitNodeAsync(code);
                Console.WriteLine(code == null ? "Mesh exit node cleared." : $"Mesh exit node: {code} (devices pick this up from their next mesh frame; a device routes all traffic through it only when its own mesh options opt in).");
                return 0;
            }
            case "scripts": return await ScriptsAsync(fleet, rest, targets);
            case "rollout": return await RolloutAsync(fleet, rest, targets);
            case "access": return await AccessAsync(fleet, rest);
            default:
                Console.Error.WriteLine($"error: unknown command '{command}'. Run 'eso help'.");
                return 2;
        }
    }

    private static int Usage(string usage)
    {
        Console.Error.WriteLine($"usage: {usage}");
        return 2;
    }

    // ---------- activity ----------

    private static async Task<int> ActivityAsync(FleetApiClient fleet, List<string> rest)
    {
        var query = new ActivityQuery { Limit = 50 };
        string? exportFormat = null, outFile = null;
        for (var i = 0; i < rest.Count; i++)
        {
            switch (rest[i])
            {
                case "--device" when i + 1 < rest.Count: query.Device = rest[++i]; break;
                case "--kind" when i + 1 < rest.Count: query.Kind = rest[++i]; break;
                case "--source" when i + 1 < rest.Count: query.Source = rest[++i]; break;
                case "--limit" when i + 1 < rest.Count: query.Limit = int.TryParse(rest[++i], out var n) ? n : query.Limit; break;
                case "--since" when i + 1 < rest.Count: if (DateTimeOffset.TryParse(rest[++i], out var since)) query.Since = since; break;
                case "--until" when i + 1 < rest.Count: if (DateTimeOffset.TryParse(rest[++i], out var until)) query.Until = until; break;
                case "--export" when i + 1 < rest.Count: exportFormat = rest[++i]; break;
                case "--out" when i + 1 < rest.Count: outFile = rest[++i]; break;
            }
        }
        if (exportFormat != null)
        {
            var text = await fleet.ExportActivityAsync(query, exportFormat);
            if (outFile != null)
            {
                await File.WriteAllTextAsync(outFile, text);
                Console.WriteLine($"Exported to {outFile} ({text.Length} chars, format {exportFormat}).");
            }
            else Console.Write(text);
            return 0;
        }
        var events = await fleet.GetActivityAsync(query);
        if (CliOutput.JsonMode) { Console.WriteLine(JsonSerializer.Serialize(events, new JsonSerializerOptions { WriteIndented = true })); return 0; }
        foreach (var e in events)
        {
            var device = e.DeviceCode != null ? $" [{e.DeviceName ?? e.DeviceCode}]" : "";
            Console.WriteLine($"{e.At:yyyy-MM-dd HH:mm:ss} {e.Source,-8} {e.Kind,-12}{device} {e.Summary} — {e.Result}");
        }
        if (events.Count == 0) Console.WriteLine("(no activity matches)");
        return 0;
    }

    // ---------- health ----------

    private static async Task<int> HealthAsync(FleetApiClient fleet)
    {
        var health = await fleet.GetHealthAsync();
        if (CliOutput.JsonMode) { Console.WriteLine(JsonSerializer.Serialize(health, new JsonSerializerOptions { WriteIndented = true })); return 0; }
        if (health.Count == 0) { Console.WriteLine("(no health snapshots yet — the poller runs every minute)"); return 0; }
        foreach (var h in health)
        {
            var badge = h.Overall switch { "pass" => "OK ", "warn" => "WARN", "fail" => "FAIL", _ => "?   " };
            var disk = h.MinDiskFreePercent is { } d ? $"disk {d:0.#}% free" : "disk ?";
            var cert = h.CertDaysLeft is { } c ? $"cert {c:0}d" : "cert ?";
            Console.WriteLine($"{badge} {h.Name} ({h.Code}) — {(h.Online ? "online" : "OFFLINE, last seen " + h.LastSeen.ToString("u"))} · {disk} · {cert} · polled {h.PolledAt:HH:mm:ss}");
            foreach (var check in h.Checks.Where(c => c.Status != "pass"))
                Console.WriteLine($"      {check.Status}: {check.Name} — {check.Detail}");
            foreach (var sup in h.Supervised.Where(s => s.State == "gave-up"))
                Console.WriteLine($"      FAIL: supervised '{sup.Name}' gave up after {sup.Restarts} restarts");
        }
        return 0;
    }

    // ---------- scripts ----------

    private static async Task<int> ScriptsAsync(FleetApiClient fleet, List<string> rest, FleetTargets targets)
    {
        if (rest.Count < 1) return Usage("eso scripts <list|show|add|remove|run> ...");
        switch (rest[0])
        {
            case "list":
            {
                var scripts = await fleet.GetScriptsAsync();
                foreach (var s in scripts) Console.WriteLine($"{s.Name}\t[{s.Shell}]\tby {s.CreatedBy}\t{s.Description}");
                if (scripts.Count == 0) Console.WriteLine("(script library is empty)");
                return 0;
            }
            case "show":
            {
                if (rest.Count < 2) return Usage("eso scripts show <name>");
                var script = (await fleet.GetScriptsAsync()).FirstOrDefault(s => s.Name.Equals(rest[1], StringComparison.OrdinalIgnoreCase) || s.Id == rest[1]);
                if (script == null) { Console.Error.WriteLine($"error: no script named '{rest[1]}'."); return 1; }
                Console.WriteLine($"# {script.Name} [{script.Shell}] by {script.CreatedBy}");
                if (script.Description.Length > 0) Console.WriteLine($"# {script.Description}");
                Console.WriteLine(script.Body);
                return 0;
            }
            case "add":
            {
                // eso scripts add <name> --shell <sh> --file <path> [--desc text]
                if (rest.Count < 2) return Usage("eso scripts add <name> --file <path> [--shell sh] [--desc text]");
                string? file = null, shell = "default", desc = "";
                for (var i = 2; i < rest.Count; i++)
                {
                    if (rest[i] == "--file" && i + 1 < rest.Count) file = rest[++i];
                    else if (rest[i] == "--shell" && i + 1 < rest.Count) shell = rest[++i];
                    else if (rest[i] == "--desc" && i + 1 < rest.Count) desc = rest[++i];
                }
                if (file == null || !File.Exists(file)) { Console.Error.WriteLine("error: --file <path> is required and must exist."); return 2; }
                var saved = await fleet.UpsertScriptAsync(new ScriptDto
                {
                    Name = rest[1], Shell = shell, Description = desc,
                    Body = await File.ReadAllTextAsync(file),
                });
                Console.WriteLine($"Saved script '{saved.Name}' (id {saved.Id}).");
                return 0;
            }
            case "remove":
            {
                if (rest.Count < 2) return Usage("eso scripts remove <name|id>");
                await fleet.DeleteScriptAsync(rest[1]);
                Console.WriteLine($"Removed '{rest[1]}'.");
                return 0;
            }
            case "run":
            {
                if (rest.Count < 2) return Usage("eso scripts run <name> (--device <CODE> | --group <g> | --all)");
                var script = (await fleet.GetScriptsAsync()).FirstOrDefault(s => s.Name.Equals(rest[1], StringComparison.OrdinalIgnoreCase) || s.Id == rest[1]);
                if (script == null) { Console.Error.WriteLine($"error: no script named '{rest[1]}'."); return 1; }
                if (targets.Codes.Count == 0 && targets.Group == null && !targets.All)
                {
                    Console.Error.WriteLine("error: choose target(s): --device <CODE>, --group <name> or --all.");
                    return 2;
                }
                var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script.Body))).ToLowerInvariant();
                var targetLabels = targets.All ? new List<string> { "(all)" } : targets.Group != null ? new List<string> { $"group:{targets.Group}" } : targets.Codes;
                await fleet.RecordScriptRunAsync(script.Id, new ScriptRunRecordRequest { Targets = targetLabels, BodySha256 = sha });
                var body = JsonSerializer.SerializeToUtf8Bytes(new ShellRequest
                {
                    Shell = script.Shell == "default" ? "" : script.Shell,
                    Script = script.Body,
                    TimeoutMs = 300_000,
                }, EsoJson.Options);
                var req = fleet.BuildCommand(targets, "POST", "/api/shell", body, timeoutSeconds: 330);
                var response = await fleet.PostCommandAsync(req);
                var allOk = true;
                foreach (var r in response.Results)
                {
                    Console.WriteLine($"===== {r.Code} =====");
                    if (r.Error != null || r.HttpStatus is not (>= 200 and < 300))
                    {
                        Console.Error.WriteLine($"FAILED: {r.Error ?? $"http {r.HttpStatus}"}");
                        allOk = false;
                        continue;
                    }
                    var exec = JsonSerializer.Deserialize<ExecResponse>(Convert.FromBase64String(r.BodyBase64), EsoJson.Options);
                    if (exec != null)
                    {
                        Console.Write(exec.Stdout);
                        Console.Error.Write(exec.Stderr);
                        if (exec.ExitCode != 0) allOk = false;
                    }
                }
                return allOk ? 0 : 1;
            }
            default:
                return Usage("eso scripts <list|show|add|remove|run> ...");
        }
    }

    // ---------- rollout ----------

    private static async Task<int> RolloutAsync(FleetApiClient fleet, List<string> rest, FleetTargets targets)
    {
        if (rest.Count < 1) return Usage("eso rollout <upload|start|list|show> ...");
        switch (rest[0])
        {
            case "upload":
            {
                if (rest.Count < 2 || !File.Exists(rest[1])) return Usage("eso rollout upload <package.zip>");
                // Chunked (v1.3.2): the file streams off disk in 256 KiB signed
                // chunks — no 70 MB in memory, and a slow link can take as long
                // as it needs. Progress throttled to 5% steps so a 70 MB upload
                // prints ~20 lines, not ~280.
                long lastPct = -1;
                var progress = new Progress<(long Sent, long Total)>(p =>
                {
                    var pct = p.Total == 0 ? 100 : p.Sent * 100 / p.Total;
                    if (pct != lastPct && (pct % 5 == 0 || pct == 100))
                    {
                        Console.WriteLine($"  {p.Sent}/{p.Total} bytes ({pct}%)");
                        lastPct = pct;
                    }
                });
                var info = await fleet.UploadUpdatePackageAsync(rest[1], progress);
                Console.WriteLine($"Package staged on the fleet server: sha256 {info.GetProperty("sha256").GetString()}, {info.GetProperty("size").GetInt64()} bytes.");
                Console.WriteLine("Start the rollout with: eso rollout start --device <CODE> | --group <g> | --all");
                return 0;
            }
            case "start":
            {
                if (targets.Codes.Count == 0 && targets.Group == null && !targets.All)
                {
                    Console.Error.WriteLine("error: choose target(s): --device <CODE>, --group <name> or --all.");
                    return 2;
                }
                var created = await fleet.CreateRolloutAsync(targets);
                var dto = created.Rollout;
                Console.WriteLine($"Rollout {dto.Id} created (package sha256 {dto.PackageSha256[..12]}…).");
                foreach (var device in dto.Devices)
                {
                    if (device.State == RolloutStates.Offline)
                    {
                        Console.WriteLine($"  {device.Code}: OFFLINE — not queued; re-run the rollout when it returns.");
                        continue;
                    }
                    if (device.State != RolloutStates.Queued) { Console.WriteLine($"  {device.Code}: {device.State}"); continue; }
                    var path = $"/fleet/update/download/{dto.Id}/{device.Code}";
                    var fetch = new UpdateFetchRequest
                    {
                        Path = path,
                        Token = created.Token,
                        Sha256 = dto.PackageSha256,
                        Signer = "operator",
                        Signature = OperatorSigning.SignUpdateFetch(fleet.OperatorKey, path, dto.PackageSha256),
                    };
                    await fleet.SetRolloutDeviceStateAsync(dto.Id, device.Code, RolloutStates.Sent);
                    var body = JsonSerializer.SerializeToUtf8Bytes(fetch, EsoJson.Options);
                    var req = fleet.BuildCommand(new FleetTargets { Codes = new List<string> { device.Code } }, "POST", "/api/update/fetch", body, timeoutSeconds: 180);
                    var response = await fleet.PostCommandAsync(req);
                    var result = response.Results.FirstOrDefault();
                    if (result is { Error: null } && result.HttpStatus is >= 200 and < 300)
                    {
                        await fleet.SetRolloutDeviceStateAsync(dto.Id, device.Code, RolloutStates.Applying);
                        Console.WriteLine($"  {device.Code}: update accepted — the device downloads, verifies, stages and applies it, then reconnects on the new version (watch: eso rollout show {dto.Id}).");
                    }
                    else
                    {
                        var error = result?.Error ?? $"http {result?.HttpStatus}";
                        await fleet.SetRolloutDeviceStateAsync(dto.Id, device.Code, RolloutStates.Failed, error);
                        Console.WriteLine($"  {device.Code}: FAILED — {error}");
                    }
                }
                return 0;
            }
            case "list":
            {
                var rollouts = await fleet.GetRolloutsAsync();
                foreach (var r in rollouts)
                    Console.WriteLine($"{r.Id}  {r.CreatedAt:u}  by {r.CreatedBy}  [{string.Join(", ", r.Devices.Select(d => $"{d.Code}={d.State}"))}]");
                if (rollouts.Count == 0) Console.WriteLine("(no rollouts yet)");
                return 0;
            }
            case "show":
            {
                if (rest.Count < 2) return Usage("eso rollout show <id>");
                var dto = await fleet.GetRolloutAsync(rest[1]);
                Console.WriteLine($"Rollout {dto.Id} — package {dto.PackageSha256}, by {dto.CreatedBy}, {dto.CreatedAt:u}");
                foreach (var d in dto.Devices)
                    Console.WriteLine($"  {d.Code}: {d.FromVersion} -> {d.ToVersion ?? "?"}  [{d.State}]{(d.Error != null ? " — " + d.Error : "")}");
                return 0;
            }
            default:
                return Usage("eso rollout <upload|start|list|show> ...");
        }
    }
    // ---------- access codes / grants (item 41) ----------

    private static string GrantFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".esoagent", "grant.json");

    private static async Task<int> AccessAsync(FleetApiClient fleet, List<string> rest)
    {
        if (rest.Count < 1)
            return Usage("eso access <grant|codes|grants|revoke|redeem|status|exec|shell> ...");
        switch (rest[0])
        {
            case "grant":
            {
                if (rest.Count < 2) return Usage("eso access grant <deviceCode> [--minutes N] [--uses N] [--view-only] [--no-exec] [--no-files] [--no-clipboard] [--tunnels] [--no-desktop]");
                var scope = new GrantScope();
                var minutes = 60; int? uses = null;
                for (int i = 2; i < rest.Count; i++)
                {
                    switch (rest[i])
                    {
                        case "--minutes" when i + 1 < rest.Count: minutes = int.TryParse(rest[++i], out var m) ? m : 60; break;
                        case "--uses" when i + 1 < rest.Count: uses = int.TryParse(rest[++i], out var u) ? u : null; break;
                        case "--view-only": scope.DesktopViewOnly = true; break;
                        case "--no-exec": scope.Exec = false; break;
                        case "--no-files": scope.Files = false; break;
                        case "--no-clipboard": scope.Clipboard = false; break;
                        case "--tunnels": scope.Tunnels = true; break;
                        case "--no-desktop": scope.DesktopControl = false; break;
                    }
                }
                var created = await fleet.CreateAccessCodeAsync(new AccessCodeCreateRequest
                {
                    DeviceCode = rest[1], Scope = scope, ExpiresMinutes = minutes, MaxUses = uses,
                });
                Console.WriteLine($"Access code for {created.DeviceCode}:  {created.Code}");
                Console.WriteLine($"Expires {created.ExpiresAt:u} · {created.Scope.Describe()} · shown ONCE — hand it over now.");
                return 0;
            }
            case "codes":
            {
                var device = rest.Count > 1 ? rest[1] : null;
                foreach (var c in await fleet.GetAccessCodesAsync(device))
                    Console.WriteLine($"{c.Id[..8]}… {c.DeviceCode} by {c.CreatedBy} · exp {c.ExpiresAt:u} · used {c.Uses}{(c.MaxUses != null ? "/" + c.MaxUses : "")}{(c.Revoked ? " REVOKED" : "")}{(c.Expired ? " EXPIRED" : "")} · {c.Scope.Describe()}");
                return 0;
            }
            case "grants":
            {
                var device = rest.Count > 1 ? rest[1] : null;
                foreach (var g in await fleet.GetAccessGrantsAsync(device))
                    Console.WriteLine($"grant {g.Id[..8]}… → {g.DeviceCode} · from {g.RedeemedFrom} · {g.Actions} action(s) · exp {g.ExpiresAt:u} · {(g.Revoked ? "REVOKED" : g.Active ? "ACTIVE" : "expired")} · {g.Scope.Describe()}");
                return 0;
            }
            case "revoke":
            {
                if (rest.Count < 2) return Usage("eso access revoke <code-or-grant-id>");
                await fleet.RevokeAccessAsync(rest[1]);
                Console.WriteLine($"Revoked {rest[1]} — effective immediately.");
                return 0;
            }
            case "redeem":
            {
                if (rest.Count < 2) return Usage("eso access redeem <code>");
                var redeemed = await fleet.RedeemAccessCodeAsync(rest[1]);
                Directory.CreateDirectory(Path.GetDirectoryName(GrantFile)!);
                await File.WriteAllTextAsync(GrantFile, JsonSerializer.Serialize(redeemed.Token, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"Grant for {redeemed.DeviceName} ({redeemed.DeviceCode}) saved to {GrantFile}");
                Console.WriteLine($"Scope: {redeemed.Token.Scope.Describe()} · expires {DateTimeOffset.FromUnixTimeSeconds(redeemed.Token.ExpiresAtUnix):u}");
                Console.WriteLine("Use it with: eso access exec <file> [args] · eso access shell <script> · eso access status");
                return 0;
            }
            case "status":
            {
                var token = LoadGrant();
                if (token == null) { Console.WriteLine("No saved grant. Redeem one: eso access redeem <code>"); return 1; }
                Console.WriteLine($"Grant {token.GrantId[..8]}… → device {token.DeviceCode}");
                Console.WriteLine($"Scope: {token.Scope.Describe()}");
                Console.WriteLine($"Expires {DateTimeOffset.FromUnixTimeSeconds(token.ExpiresAtUnix):u} ({(DateTimeOffset.FromUnixTimeSeconds(token.ExpiresAtUnix) > DateTimeOffset.UtcNow ? "still valid by the clock — the server has the final say" : "EXPIRED")})");
                return 0;
            }
            case "exec":
            case "shell":
            {
                var token = LoadGrant();
                if (token == null) { Console.Error.WriteLine("No saved grant. Run: eso access redeem <code>"); return 1; }
                object body;
                string path;
                if (rest[0] == "exec")
                {
                    if (rest.Count < 2) return Usage("eso access exec <file> [args...]");
                    body = new ExecRequest { File = rest[1], Args = rest.Skip(2).ToArray() };
                    path = "/api/exec";
                }
                else
                {
                    if (rest.Count < 2) return Usage("eso access shell <script>");
                    body = new ShellRequest { Shell = "", Script = string.Join(" ", rest.Skip(1)) };
                    path = "/api/shell";
                }
                var result = await fleet.GrantCommandAsync(token, "POST", path, body, 600);
                if (result.Error != null) { Console.Error.WriteLine($"grant command failed: {result.Error}"); return 1; }
                var exec = JsonSerializer.Deserialize<ExecResponse>(Convert.FromBase64String(result.BodyBase64), EsoJson.Options);
                if (exec != null)
                {
                    Console.Write(exec.Stdout);
                    Console.Error.Write(exec.Stderr);
                    return exec.ExitCode ?? 1;
                }
                return 0;
            }
            default: return Usage("eso access <grant|codes|grants|revoke|redeem|status|exec|shell> ...");
        }
    }

    private static GrantTokenDto? LoadGrant()
    {
        try
        {
            return File.Exists(GrantFile)
                ? JsonSerializer.Deserialize<GrantTokenDto>(File.ReadAllText(GrantFile), EsoJson.Options)
                : null;
        }
        catch { return null; }
    }

}
