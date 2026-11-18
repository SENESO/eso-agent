using System.Text.Json;
using EsoAgent.Core;

namespace EsoAgent.Client;

/// <summary>
/// Fleet registry commands (operator tier): devices, enrollment decisions,
/// groups, per-device token rotation, mesh state, name resolution, enrollment
/// keys, subnet approvals and ACLs. Owner-reserved controls (revoke operator,
/// rotate operator key, owner password) deliberately do NOT exist here.
/// </summary>
public static class FleetCommands
{
    public static async Task<int> RunAsync(FleetApiClient fleet, string command, List<string> rest)
    {
        switch (command)
        {
            case "devices":
            {
                var devices = await fleet.GetDevicesAsync();
                foreach (var d in devices)
                {
                    Console.WriteLine($"{d.Code}\t{(d.Online ? "online " : "offline")}\t{d.State}\t{d.Name}\t{d.Os}\tvip {d.VirtualIp ?? "—"}\tgroups [{string.Join(",", d.Groups)}]\tlast {d.LastSeen:u}");
                }
                Console.WriteLine($"{devices.Count} device(s), {devices.Count(d => d.Online)} online, {devices.Count(d => d.State == "Pending")} pending.");
                return 0;
            }
            case "approve":
            case "reject":
            {
                if (rest.Count < 1) return Usage($"eso {command} <CODE>");
                var r = await fleet.PostRegistryActionAsync(command, new { code = rest[0] });
                Console.WriteLine($"{command}: {r}");
                return 0;
            }
            case "rename":
            {
                if (rest.Count < 2) return Usage("eso rename <CODE> <name>");
                var r = await fleet.PostRegistryActionAsync("rename", new { code = rest[0], name = string.Join(" ", rest.Skip(1)) });
                Console.WriteLine($"renamed: {r}");
                return 0;
            }
            case "groups":
            {
                if (rest.Count < 2) return Usage("eso groups <CODE> <a,b,c>");
                var groups = rest[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                var r = await fleet.PostRegistryActionAsync("groups", new { code = rest[0], groups });
                Console.WriteLine($"groups set: {r}");
                return 0;
            }
            case "rotate-device-token":
            {
                if (rest.Count < 1) return Usage("eso rotate-device-token <CODE>");
                var result = await fleet.PostAdminRelayAsync(fleet.BuildAdminRelay(rest[0], "rotate-token"));
                if (result.Error != null) { Console.Error.WriteLine($"error: {result.Error}"); return 1; }
                // The device's result body carries the new token — show it once.
                var body = string.IsNullOrEmpty(result.BodyBase64) ? "" : System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(result.BodyBase64));
                Console.WriteLine($"device {rest[0]} result: {body}");
                return 0;
            }
            case "queue":
            {
                if (rest.Count < 1) return Usage("eso queue <list|show> ...");
                if (rest[0] == "list")
                {
                    string? device = null;
                    var di = rest.IndexOf("--device");
                    if (di >= 0 && di + 1 < rest.Count) device = rest[di + 1];
                    var records = await fleet.GetQueueAsync(device);
                    if (records.Count == 0) { Console.WriteLine("(queue is empty)"); return 0; }
                    foreach (var q in records)
                        Console.WriteLine($"{q.Id}\t{q.State}\t{q.DeviceCode}\t{q.Method} {q.Path}\tqueued {q.QueuedAt:u}\texpires {DateTimeOffset.FromUnixTimeSeconds(q.ExpiresAt):u}");
                    return 0;
                }
                if (rest[0] == "show" && rest.Count >= 2)
                {
                    var q = await fleet.GetQueuedAsync(rest[1]);
                    Console.WriteLine($"queue id {q.Id} · {q.State} · device {q.DeviceCode}");
                    Console.WriteLine($"command: {q.Method} {q.Path} (operator command {q.CommandId})");
                    Console.WriteLine($"queued {q.QueuedAt:u} · expires {DateTimeOffset.FromUnixTimeSeconds(q.ExpiresAt):u}{(q.DeliveredAt != null ? $" · delivered {q.DeliveredAt:u}" : "")}");
                    if (q.Error != null) Console.WriteLine($"error: {q.Error}");
                    if (!string.IsNullOrEmpty(q.ResultBodyBase64))
                    {
                        var body = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(q.ResultBodyBase64));
                        Console.WriteLine($"result (http {q.HttpStatus}):");
                        // exec/shell results decode to the familiar shape; anything else prints raw.
                        try
                        {
                            var exec = System.Text.Json.JsonSerializer.Deserialize<ExecResponse>(body, EsoJson.Options);
                            if (exec != null && (exec.Stdout.Length > 0 || exec.Stderr.Length > 0 || exec.ExitCode != null))
                            {
                                Console.Write(exec.Stdout);
                                Console.Error.Write(exec.Stderr);
                                Console.WriteLine($"[exit {exec.ExitCode?.ToString() ?? "—"}]");
                            }
                            else Console.WriteLine(body);
                        }
                        catch { Console.WriteLine(body); }
                    }
                    else if (q.State == "queued") Console.WriteLine("(not delivered yet — the device is offline; it will run when the device reconnects, or expire undelivered)");
                    return 0;
                }
                return Usage("eso queue <list [--device <CODE>] | show <id>>");
            }
            case "mesh":
            {
                var mesh = await fleet.GetMeshAsync();
                Console.WriteLine($"mesh range {mesh.Cidr} · relay {(mesh.RelayEndpoint ?? "not configured")}");
                foreach (var d in mesh.Devices)
                    Console.WriteLine($"{d.Code}\t{d.Name}\tvip {d.VirtualIp ?? "—"}\twg {d.WgPublicKey}\tsubnets approved [{string.Join(",", d.ApprovedSubnets)}] advertised [{string.Join(",", d.AdvertisedSubnets)}]");
                Console.WriteLine("ACL rules:");
                foreach (var rule in mesh.AclRules)
                    Console.WriteLine($"  {rule.From} -> {rule.To} ports [{(rule.Ports.Count == 0 ? "all" : string.Join(",", rule.Ports))}]");
                Console.WriteLine("Enrollment keys:");
                foreach (var k in mesh.EnrollmentKeys)
                    Console.WriteLine($"  {k.Id}\tuses {(k.UsesLeft?.ToString() ?? "unlimited")}\tby {k.CreatedBy}\t{k.Note}");
                return 0;
            }
            case "resolve":
            {
                if (rest.Count < 1) return Usage("eso resolve <name>");
                var r = await fleet.ResolveAsync(rest[0]);
                Console.WriteLine(r.ToString());
                return 0;
            }
            case "enroll-key":
            {
                if (rest.Count < 1) return Usage("eso enroll-key <create|revoke> ...");
                if (rest[0] == "create")
                {
                    int? uses = null; var note = "";
                    for (int i = 1; i < rest.Count; i++)
                    {
                        if (rest[i] == "--uses" && i + 1 < rest.Count && int.TryParse(rest[i + 1], out var u)) { uses = u; i++; }
                        else if (rest[i] == "--note" && i + 1 < rest.Count) { note = rest[i + 1]; i++; }
                    }
                    var r = await fleet.PostRegistryActionAsync("enrollment-keys", new { uses, note });
                    Console.WriteLine("Enrollment key created — the key is shown ONCE, copy it now:");
                    Console.WriteLine(r.ToString());
                    return 0;
                }
                if (rest[0] == "revoke" && rest.Count >= 2)
                {
                    var r = await fleet.PostRegistryActionAsync("enrollment-keys/revoke", new { id = rest[1] });
                    Console.WriteLine($"revoked: {r}");
                    return 0;
                }
                return Usage("eso enroll-key <create [--uses N] [--note text] | revoke <id>>");
            }
            case "subnets":
            {
                if (rest.Count < 2) return Usage("eso subnets <CODE> <cidr,cidr,...>");
                var approved = rest[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                var r = await fleet.PostRegistryActionAsync("subnets", new { code = rest[0], approved });
                Console.WriteLine($"subnets approved: {r}");
                return 0;
            }
            case "acl":
            {
                if (rest.Count < 2 || rest[0] != "set") return Usage("eso acl set <rules.json>");
                var rules = JsonSerializer.Deserialize<List<AclRule>>(await File.ReadAllTextAsync(rest[1]), EsoJson.Options) ?? new();
                var r = await fleet.PostRegistryActionAsync("acl", new { rules });
                Console.WriteLine($"ACL replaced: {r}");
                return 0;
            }
            default:
                Console.Error.WriteLine($"error: unknown fleet command '{command}'.");
                return 2;
        }
    }

    private static int Usage(string usage)
    {
        Console.Error.WriteLine($"usage: {usage}");
        return 2;
    }
}
