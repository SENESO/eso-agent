using System.Text;
using System.Text.Json;
using EsoAgent.Core;

namespace EsoAgent.Client;

/// <summary>
/// v1.2 device commands (operator pack): telemetry, diagnose, vision,
/// windows, launch-gui, uitree, watch, app/perceive, sessions, patch,
/// schedules, files, recordings. All ride the same Transport as every
/// other device command — direct or fleet-relayed, identical behavior.
/// </summary>
public static class DeviceCommandsV12
{
    public static readonly HashSet<string> Commands = new()
    {
        "telemetry", "diagnose", "vision", "windows", "launch-gui", "uitree", "uitree-click",
        "watch", "app", "sessions", "patch", "schedules", "files", "recordings",
        "intent", "events", "see",
    };

    public static async Task<int> RunAsync(Transport transport, ClientConfig cfg, string command, List<string> rest)
    {
        switch (command)
        {
            case "telemetry": return await ForEach(transport, "GET", "/api/telemetry", null, r => PrintTelemetry(r.Json<TelemetryResponse>()), 120);
            case "diagnose": return await ForEach(transport, "GET", "/api/diagnose", null, r => PrintDiagnose(r.Json<DiagnoseResponse>()), 180);
            case "vision": return await VisionAsync(transport, rest);
            case "windows": return await WindowsAsync(transport, rest);
            case "launch-gui": return await LaunchGuiAsync(transport, rest);
            case "uitree":
            {
                if (rest.Count < 1) return Usage("eso uitree <pid|title-substring>");
                var target = Uri.EscapeDataString(rest[0]);
                return await ForEach(transport, "GET", $"/api/uitree?target={target}", null, r => PrintUiTree(r.Json<UiTreeResponse>()), 120);
            }
            case "uitree-click":
            {
                if (rest.Count < 2) return Usage("eso uitree-click <pid|title> <element name>");
                return await ForEach(transport, "POST", "/api/uitree/click",
                    new UiClickRequest { Target = rest[0], Element = string.Join(" ", rest.Skip(1)) },
                    r => Console.WriteLine(r.Text), 120);
            }
            case "see": return await SeeAsync(transport, rest);
            case "intent": return await IntentAsync(transport, rest);
            case "events": return await EventsAsync(transport, rest);
            case "watch": return await WatchAsync(transport, rest);
            case "app": return await AppAsync(transport, rest);
            case "sessions": return await SessionsAsync(transport, rest);
            case "patch": return await PatchAsync(transport, rest);
            case "schedules": return await ForEach(transport, "GET", "/api/schedules", null, r =>
            {
                var schedules = r.Json<List<ScheduleStateDto>>();
                if (schedules.Count == 0) { Console.WriteLine("(no device schedules configured)"); return; }
                foreach (var s in schedules)
                    Console.WriteLine($"{s.Name}\t{(s.Enabled ? "enabled" : "disabled")}\t{s.Description}\tlast {(s.LastRunAt?.ToString("u") ?? "never")} exit {(s.LastExitCode?.ToString() ?? "—")}{(s.LastError != null ? " error: " + s.LastError : "")}");
            });
            case "files": return await FilesAsync(transport, rest);
            case "recordings": return await RecordingsAsync(transport, rest);
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
                Console.Error.WriteLine($"[{reply.Label}] FAILED: {reply.Error ?? $"HTTP {reply.Status}: {(reply.Text.Length > 400 ? reply.Text[..400] + "…" : reply.Text)}"}");
                allOk = false;
                continue;
            }
            if (CliOutput.JsonMode) { Console.WriteLine(reply.Text); continue; }
            try { print(reply); }
            catch (Exception ex) { Console.Error.WriteLine($"[{reply.Label}] bad response: {ex.Message}"); allOk = false; }
        }
        return allOk ? 0 : 1;
    }

    // ---------- telemetry / diagnose ----------

    private static void PrintTelemetry(TelemetryResponse t)
    {
        Console.WriteLine($"at {t.At:u} · CPU {(t.CpuPercent?.ToString("F1") ?? "?")}% · RAM {(t.RamUsedBytes / 1048576)?.ToString() ?? "?"} / {(t.RamTotalBytes / 1048576)?.ToString() ?? "?"} MB");
        Console.WriteLine("top processes (by RAM):");
        foreach (var p in t.TopProcesses.Take(12))
            Console.WriteLine($"  {p.Pid,7} {p.Name,-28} cpu {p.CpuPercent,5:F1}%  ram {p.RamBytes / 1048576,6} MB{(p.ReadBytes != null ? $"  io r/w {p.ReadBytes / 1048576}/{p.WriteBytes / 1048576} MB" : "")}");
        foreach (var gpu in t.Gpus)
            Console.WriteLine($"GPU {gpu.Name}: util {(gpu.UtilPercent?.ToString("F0") ?? "?")}% · VRAM {(gpu.VramUsedBytes / 1048576)?.ToString() ?? "?"} / {(gpu.VramTotalBytes / 1048576)?.ToString() ?? "?"} MB · {(gpu.TempC?.ToString("F0") ?? "?")}°C");
        Console.WriteLine($"network connections: {t.Connections.Count} (use 'eso telemetry --raw' equivalent: the API returns all)");
        foreach (var note in t.Notes) Console.WriteLine($"note: {note}");
    }

    private static void PrintDiagnose(DiagnoseResponse d)
    {
        Console.WriteLine($"=== DIAGNOSIS {d.At:u} — overall: {d.Overall.ToUpperInvariant()} ===");
        Console.WriteLine($"doctor: {d.Doctor.Overall}");
        foreach (var check in d.Doctor.Checks.Where(c => c.Status != "pass"))
            Console.WriteLine($"  {check.Status}: {check.Name} — {check.Detail}");
        if (d.Pressure.Count > 0) { Console.WriteLine("pressure:"); foreach (var p in d.Pressure) Console.WriteLine($"  {p}"); }
        if (d.FailedServices.Count > 0) { Console.WriteLine("failed/auto services:"); foreach (var s in d.FailedServices) Console.WriteLine($"  {s}"); }
        if (d.Crashes.Count > 0) { Console.WriteLine("recent crashes:"); foreach (var c in d.Crashes) Console.WriteLine($"  {c}"); }
        if (d.OsErrors.Count > 0) { Console.WriteLine($"OS errors (recent, {d.OsErrors.Count}):"); foreach (var e in d.OsErrors.Take(12)) Console.WriteLine($"  {e}"); }
        Console.WriteLine($"agent audit errors (recent window): {d.AuditErrorCount}");
        foreach (var e in d.RecentAuditErrors) Console.WriteLine($"  {e}");
        foreach (var note in d.Notes) Console.WriteLine($"note: {note}");
    }

    // ---------- vision / windows ----------

    private static async Task<int> VisionAsync(Transport transport, List<string> rest)
    {
        if (rest.Count < 1) return Usage("eso vision <monitors|capture|window|region> ...");
        switch (rest[0])
        {
            case "monitors":
                return await ForEach(transport, "GET", "/api/vision/monitors", null, r =>
                {
                    var v = r.Json<VisionResponse>();
                    foreach (var m in v.Monitors) Console.WriteLine($"{m.Index} {m.Name} {m.Width}x{m.Height} at {m.X},{m.Y}{(m.Primary ? " (primary)" : "")}");
                    foreach (var note in v.Notes) Console.WriteLine($"note: {note}");
                }, 120);
            case "capture":
            {
                var outDir = GetFlag(rest, "--out") ?? ".";
                return await ForEach(transport, "GET", "/api/vision/capture", null, r =>
                {
                    var v = r.Json<VisionResponse>();
                    Directory.CreateDirectory(outDir);
                    foreach (var image in v.Images)
                    {
                        var file = Path.Combine(outDir, Sanitize(image.Label) + ".png");
                        File.WriteAllBytes(file, Convert.FromBase64String(image.PngBase64));
                        Console.WriteLine($"{image.Label}: {image.Width}x{image.Height} -> {file}");
                    }
                    foreach (var note in v.Notes) Console.WriteLine($"note: {note}");
                }, 180);
            }
            case "window":
            {
                if (rest.Count < 2) return Usage("eso vision window <id|title> [--out file.png]");
                var outFile = GetFlag(rest, "--out") ?? "window.png";
                var target = Uri.EscapeDataString(rest[1]);
                return await ForEach(transport, "GET", $"/api/vision/window?target={target}", null, r =>
                {
                    var image = r.Json<VisionImageDto>();
                    File.WriteAllBytes(outFile, Convert.FromBase64String(image.PngBase64));
                    Console.WriteLine($"{image.Label}: {image.Width}x{image.Height} -> {outFile}");
                }, 120);
            }
            case "region":
            {
                if (rest.Count < 5 || !int.TryParse(rest[1], out var x) || !int.TryParse(rest[2], out var y) ||
                    !int.TryParse(rest[3], out var w) || !int.TryParse(rest[4], out var h))
                    return Usage("eso vision region <x> <y> <w> <h> [--out file.png]");
                var outFile = GetFlag(rest, "--out") ?? "region.png";
                return await ForEach(transport, "GET", $"/api/vision/region?x={x}&y={y}&w={w}&h={h}", null, r =>
                {
                    var image = r.Json<VisionImageDto>();
                    File.WriteAllBytes(outFile, Convert.FromBase64String(image.PngBase64));
                    Console.WriteLine($"region {w}x{h} at {x},{y} -> {outFile}");
                }, 120);
            }
            default: return Usage("eso vision <monitors|capture|window|region> ...");
        }
    }

    private static async Task<int> WindowsAsync(Transport transport, List<string> rest)
    {
        if (rest.Count < 1) return Usage("eso windows <list|focus|close|wait> ...");
        switch (rest[0])
        {
            case "list":
                return await ForEach(transport, "GET", "/api/windows", null, r =>
                {
                    foreach (var w in r.Json<List<WindowInfoDto>>())
                        Console.WriteLine($"{w.Id}\tpid {(w.Pid?.ToString() ?? "?")} ({w.Process ?? "?"})\t{w.Width}x{w.Height} at {w.X},{w.Y}{(w.Focused ? " [focused]" : "")}\t{w.Title}");
                }, 120);
            case "focus":
                if (rest.Count < 2) return Usage("eso windows focus <id>");
                return await ForEach(transport, "POST", $"/api/windows/{Uri.EscapeDataString(rest[1])}/focus", null, r => Console.WriteLine(r.Text));
            case "close":
                if (rest.Count < 2) return Usage("eso windows close <id>");
                return await ForEach(transport, "POST", $"/api/windows/{Uri.EscapeDataString(rest[1])}/close", null, r => Console.WriteLine(r.Text));
            case "wait":
            {
                if (rest.Count < 2) return Usage("eso windows wait <title-substring> [timeoutSeconds]");
                var timeout = rest.Count > 2 && int.TryParse(rest[2], out var t) ? t : 30;
                var title = Uri.EscapeDataString(rest[1]);
                return await ForEach(transport, "GET", $"/api/windows/wait?title={title}&timeoutSeconds={timeout}", null, r =>
                {
                    var w = r.Json<WindowInfoDto>();
                    Console.WriteLine($"found: {w.Id} pid {w.Pid} '{w.Title}'");
                }, timeout + 60);
            }
            default: return Usage("eso windows <list|focus|close|wait> ...");
        }
    }

    private static async Task<int> LaunchGuiAsync(Transport transport, List<string> rest)
    {
        if (rest.Count < 1) return Usage("eso launch-gui <file> [args...] [--wait-title T] [--shot-after N] [--out shot.png]");
        var request = new LaunchGuiRequest { File = rest[0] };
        var outFile = GetFlag(rest, "--out");
        for (var i = 1; i < rest.Count; i++)
        {
            if (rest[i] == "--wait-title" && i + 1 < rest.Count) request.WaitTitle = rest[++i];
            else if (rest[i] == "--shot-after" && i + 1 < rest.Count && int.TryParse(rest[i + 1], out var s)) { request.ScreenshotAfterSeconds = s; i++; }
            else if (rest[i] == "--out") i++;
            else if (!rest[i].StartsWith("--")) request.Args.Add(rest[i]);
        }
        return await ForEach(transport, "POST", "/api/launch/gui", request, r =>
        {
            var response = r.Json<LaunchGuiResponse>();
            Console.WriteLine($"launched pid {response.Pid}");
            if (response.WindowFound != null) Console.WriteLine($"window found: {response.WindowFound} ({response.WindowTitle})");
            if (response.ScreenshotPngBase64 != null)
            {
                var file = outFile ?? "launch-shot.png";
                File.WriteAllBytes(file, Convert.FromBase64String(response.ScreenshotPngBase64));
                Console.WriteLine($"screenshot -> {file}");
            }
            foreach (var note in response.Notes) Console.WriteLine($"note: {note}");
        }, 300);
    }

    private static void PrintUiTree(UiTreeResponse tree)
    {
        Console.WriteLine($"UI tree of '{tree.Target}' — {tree.Nodes.Count} elements");
        foreach (var node in tree.Nodes)
            Console.WriteLine($"{new string(' ', node.Depth * 2)}[{node.Role}] \"{node.Name}\" @ {node.X},{node.Y} {node.Width}x{node.Height}");
        foreach (var note in tree.Notes) Console.WriteLine($"note: {note}");
    }

    // ---------- watch ----------

    private static async Task<int> WatchAsync(Transport transport, List<string> rest)
    {
        if (rest.Count < 1) return Usage("eso watch <list|events|add> ...");
        switch (rest[0])
        {
            case "list":
                return await ForEach(transport, "GET", "/api/watch", null, r =>
                {
                    using var doc = JsonDocument.Parse(r.Text);
                    foreach (var w in doc.RootElement.GetProperty("watches").EnumerateArray())
                        Console.WriteLine($"{w.GetProperty("name").GetString()}\t{w.GetProperty("path").GetString()}\trecursive={w.GetProperty("recursive").GetBoolean()}");
                });
            case "add":
            {
                if (rest.Count < 3) return Usage("eso watch add <name> <path>");
                return await ForEach(transport, "POST", "/api/watch", new WatchAddRequest { Name = rest[1], Path = rest[2], Recursive = !rest.Contains("--no-recursive") },
                    r => Console.WriteLine(r.Text));
            }
            case "events":
            {
                var follow = rest.Contains("--follow");
                long since = 0;
                var sinceFlag = GetFlag(rest, "--since");
                if (sinceFlag != null) long.TryParse(sinceFlag, out since);
                using var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
                while (!cts.IsCancellationRequested)
                {
                    var replies = await transport.SendAsync("GET", $"/api/watch?since={since}", null, 60);
                    foreach (var reply in replies)
                    {
                        if (!reply.Ok) { Console.Error.WriteLine($"[{reply.Label}] FAILED: {reply.Error ?? ("HTTP " + reply.Status)}"); return 1; }
                        using var doc = JsonDocument.Parse(reply.Text);
                        foreach (var e in doc.RootElement.GetProperty("events").EnumerateArray())
                        {
                            Console.WriteLine($"{e.GetProperty("at").GetString()} [{e.GetProperty("watch").GetString()}] {e.GetProperty("kind").GetString()}: {e.GetProperty("path").GetString()}");
                            since = Math.Max(since, e.GetProperty("seq").GetInt64());
                        }
                    }
                    if (!follow) break;
                    try { await Task.Delay(2000, cts.Token); } catch { break; }
                }
                return 0;
            }
            default: return Usage("eso watch <list|events|add> ...");
        }
    }

    // ---------- perception stack entry point (item 39) ----------

    private static async Task<int> SeeAsync(Transport transport, List<string> rest)
    {
        var pixels = rest.Contains("--pixels");
        var saveDir = GetFlag(rest, "--save-dir");
        return await ForEach(transport, "GET", $"/api/vision/see{(pixels ? "?pixels=1" : "")}", null, r =>
        {
            var see = r.Json<SeeResponse>();
            Console.WriteLine($"SEE — {see.Windows.Count} window(s) · tier {see.Tier} · cost {see.CostBytes} bytes (≈{see.CostBytes / 4} tokens)");
            Console.WriteLine("GLANCE (what a human look answers):");
            Console.WriteLine($"  open: {string.Join(" | ", see.Glance.OpenWindows)}");
            Console.WriteLine($"  focused: {see.Glance.FocusedWindow ?? "(none reported)"}");
            if (see.Glance.LikelyDialogOrError != null) Console.WriteLine($"  dialog/error: {see.Glance.LikelyDialogOrError}");
            foreach (var change in see.Glance.ChangedSinceLastLook) Console.WriteLine($"  changed: {change}");
            if (see.Glance.VisibleTexts.Count > 0) Console.WriteLine($"  text shown: {string.Join(" · ", see.Glance.VisibleTexts.Take(12))}");
            foreach (var w in see.Windows)
            {
                Console.WriteLine($"\n== '{w.Window.Title}' (pid {w.Window.Pid}, {w.Window.Width}x{w.Window.Height}{(w.Window.Focused ? ", focused" : "")}) — tree: {(w.HasTree ? $"{w.ElementCount} elements, {w.TextElementCount} with text" : "none")}");
                if (w.Note != null) Console.WriteLine($"   note: {w.Note}");
                foreach (var n in w.Nodes.Take(60))
                    Console.WriteLine($"   {new string(' ', n.Depth * 2)}[{n.Role}] '{n.Name}' @{n.X},{n.Y} {n.Width}x{n.Height}");
                if (w.Nodes.Count > 60) Console.WriteLine($"   … {w.Nodes.Count - 60} more elements (use eso uitree for the full tree)");
            }
            foreach (var img in see.Images)
            {
                Console.WriteLine($"[pixels] {img.Label}: {img.Width}x{img.Height} (tier 6 — weakest evidence class)");
                if (saveDir != null)
                {
                    Directory.CreateDirectory(saveDir);
                    var path = Path.Combine(saveDir, img.Label.Replace('/', '_') + ".png");
                    File.WriteAllBytes(path, Convert.FromBase64String(img.PngBase64));
                    Console.WriteLine($"   saved {path}");
                }
            }
            foreach (var note in see.Notes) Console.WriteLine($"note: {note}");
        }, 300);
    }

    // ---------- intent-level operations + anomaly events (item 37) ----------

    private static async Task<int> IntentAsync(Transport transport, List<string> rest)
    {
        if (rest.Count < 1) return Usage("eso intent <ensure-service|ensure-software|ensure-file|ensure-supervised> ...");
        var request = new IntentRequest { Verb = rest[0] };
        switch (rest[0])
        {
            case "ensure-service":
                if (rest.Count < 3) return Usage("eso intent ensure-service <name> <running|stopped>");
                request.Name = rest[1]; request.State = rest[2];
                break;
            case "ensure-software":
                if (rest.Count < 2) return Usage("eso intent ensure-software <id> [--absent]");
                request.Id = rest[1]; request.Present = !rest.Contains("--absent");
                break;
            case "ensure-file":
                if (rest.Count < 2) return Usage("eso intent ensure-file <path> [--sha256 <hex>]");
                request.Path = rest[1];
                request.Sha256 = GetFlag(rest, "--sha256");
                break;
            case "ensure-supervised":
                if (rest.Count < 2) return Usage("eso intent ensure-supervised <name>");
                request.Name = rest[1];
                break;
            default: return Usage("eso intent <ensure-service|ensure-software|ensure-file|ensure-supervised> ...");
        }
        return await ForEach(transport, "POST", "/api/intent", request, r =>
        {
            var proof = r.Json<IntentProof>();
            Console.WriteLine($"{proof.Verb} {proof.Target}: satisfied={proof.Satisfied} changed={proof.Changed}");
            Console.WriteLine($"  {proof.Detail}");
            foreach (var (k, v) in proof.Evidence) Console.WriteLine($"  evidence {k} = {v}");
        }, 660);
    }

    private static async Task<int> EventsAsync(Transport transport, List<string> rest)
    {
        var follow = rest.Contains("--follow");
        var cursor = 0;
        Console.WriteLine("Anomaly events (telemetry thresholds, crashes, file changes, supervision, doctor) — silence means nothing happened.");
        while (true)
        {
            var replies = await transport.SendAsync("GET", $"/api/events?since={cursor}", null, 60);
            var reply = replies[0];
            if (!reply.Ok) { Console.Error.WriteLine($"events failed: {reply.Error ?? reply.Text}"); return 1; }
            var page = reply.Json<AgentEventsResponse>();
            foreach (var e in page.Events)
                Console.WriteLine($"{e.At.LocalDateTime:yyyy-MM-dd HH:mm:ss} [{e.Kind}] {e.Summary} ({e.Result})");
            cursor = page.Next;
            if (!follow) return 0;
            await Task.Delay(3000);
        }
    }

    // ---------- app registry + perceive ----------

    private static async Task<int> AppAsync(Transport transport, List<string> rest)
    {
        if (rest.Count < 1) return Usage("eso app <list|presets|run|perceive|verify|detect> ...");
        switch (rest[0])
        {
            case "list":
                return await ForEach(transport, "GET", "/api/app", null, r =>
                {
                    using var doc = JsonDocument.Parse(r.Text);
                    foreach (var a in doc.RootElement.GetProperty("registered").EnumerateArray())
                    {
                        var notes = a.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.String ? $"\t[{n.GetString()}]" : "";
                        Console.WriteLine($"{a.GetProperty("name").GetString()}\tperceive: {(a.GetProperty("hasPerceive").GetBoolean() ? "yes" : "no")}\tverify: {(a.GetProperty("hasVerify").GetBoolean() ? "yes" : "no")}\t{a.GetProperty("description").GetString()}{notes}");
                    }
                    var presets = doc.RootElement.GetProperty("presets");
                    Console.WriteLine($"built-in presets (runnable by name; a registry entry with the same name overrides): {string.Join(", ", presets.EnumerateArray().Select(p => p.GetString()))}");
                });
            case "presets":
                return await ForEach(transport, "GET", "/api/app/presets", null, r =>
                {
                    var presets = r.Json<List<AppCommandConfig>>();
                    Console.WriteLine(JsonSerializer.Serialize(presets, new JsonSerializerOptions { WriteIndented = true }));
                    Console.WriteLine("Presets run by name as-is. To override one, add an entry with the same name under \"appCommands\" in the device's esoagent.json (e.g. with the install's real Executable path).");
                });
            case "run":
            {
                if (rest.Count < 2) return Usage("eso app run <name> [key=value ...]");
                var parameters = ParseParams(rest.Skip(2));
                return await ForEach(transport, "POST", $"/api/app/{Uri.EscapeDataString(rest[1])}", new AppRunRequest { Params = parameters }, r =>
                {
                    var response = r.Json<AppRunResponse>();
                    Console.Write(response.Stdout);
                    Console.Error.Write(response.Stderr);
                    if (response.LogScannedFile != null)
                    {
                        Console.WriteLine($"--- errors from {response.LogScannedFile} ---");
                        foreach (var line in response.LogErrors) Console.WriteLine(line);
                    }
                    Console.WriteLine($"[exit {response.ExitCode}, {response.DurationMs} ms]");
                }, 960);
            }
            case "detect":
            {
                if (rest.Count < 2) return Usage("eso app detect <project-dir-on-device>");
                var path = Uri.EscapeDataString(rest[1]);
                return await ForEach(transport, "GET", $"/api/app/detect?path={path}", null, r =>
                {
                    using var doc = JsonDocument.Parse(r.Text);
                    var detections = doc.RootElement.GetProperty("detections");
                    if (detections.GetArrayLength() == 0) { Console.WriteLine("No known project markers found (looked for .sln/.csproj, package.json, pyproject/requirements, go.mod, Cargo.toml, pom.xml, build.gradle, CMakeLists.txt, Makefile)."); return; }
                    foreach (var d in detections.EnumerateArray())
                    {
                        Console.WriteLine($"{d.GetProperty("kind").GetString()} (marker: {d.GetProperty("marker").GetString()})");
                        Console.WriteLine("  registry entries to add under \"appCommands\" (project = " + rest[1] + "):");
                        Console.WriteLine("  " + d.GetProperty("suggested").GetRawText());
                    }
                });
            }
            case "verify":
            {
                if (rest.Count < 2) return Usage("eso app verify <name> [key=value ...]");
                var parameters = ParseParams(rest.Skip(2));
                return await ForEach(transport, "POST", $"/api/app/{Uri.EscapeDataString(rest[1])}/verify", new AppRunRequest { Params = parameters }, r =>
                {
                    var response = r.Json<AppVerifyResponse>();
                    Console.WriteLine($"{response.App}: {(response.Passed ? "PASS" : "FAIL")} (exit {response.ExitCode}, {response.DurationMs} ms)");
                    if (!string.IsNullOrWhiteSpace(response.OutputTail)) Console.WriteLine(response.OutputTail);
                    if (response.LogScannedFile != null)
                    {
                        Console.WriteLine($"--- errors from {response.LogScannedFile} ---");
                        foreach (var line in response.LogErrors) Console.WriteLine(line);
                    }
                }, 960);
            }
            case "perceive":
            {
                if (rest.Count < 2) return Usage("eso app perceive <name> [key=value ...] [--id-pass] [--save dump.json] [--diff baseline.json]");
                var parameters = ParseParams(rest.Skip(2).Where(a => !a.StartsWith("--")));
                var idPass = rest.Contains("--id-pass");
                var delta = rest.Contains("--delta");
                var save = GetFlag(rest, "--save");
                var diffBase = GetFlag(rest, "--diff");
                return await ForEach(transport, "POST", $"/api/app/{Uri.EscapeDataString(rest[1])}/perceive",
                    new PerceiveRequest { Params = parameters, IdPass = idPass, Delta = delta }, r =>
                    {
                        var response = r.Json<PerceiveResponse>();
                        if (response.IsDelta && response.Diff is { } deltaDiff)
                        {
                            Console.WriteLine($"--- delta vs baseline {response.BaselineAt:u} (only what changed) ---");
                            Console.WriteLine(JsonSerializer.Serialize(deltaDiff, new JsonSerializerOptions { WriteIndented = true }));
                        }
                        if (response.Dump is { } dump)
                        {
                            Console.WriteLine(JsonSerializer.Serialize(dump, new JsonSerializerOptions { WriteIndented = true }));
                            if (save != null)
                            {
                                File.WriteAllText(save, JsonSerializer.Serialize(dump));
                                Console.WriteLine($"dump saved to {save}");
                            }
                            if (diffBase != null && File.Exists(diffBase))
                            {
                                using var baseDoc = JsonDocument.Parse(File.ReadAllText(diffBase));
                                var diff = SceneDiff.Compare(baseDoc.RootElement, dump);
                                Console.WriteLine($"--- diff vs {diffBase}: +{diff.Added.Count} -{diff.Removed.Count} ~{diff.Changed.Count} ={diff.Unchanged}");
                                foreach (var a in diff.Added) Console.WriteLine($"  + {a}");
                                foreach (var a in diff.Removed) Console.WriteLine($"  - {a}");
                                foreach (var c in diff.Changed)
                                {
                                    Console.WriteLine($"  ~ {c.Name}");
                                    foreach (var change in c.Changes) Console.WriteLine($"      {change.Field}: {change.From} -> {change.To}");
                                }
                            }
                        }
                        if (response.IdPass.Count > 0)
                        {
                            Console.WriteLine("--- ID-pass measurements (exact, from the semantic render) ---");
                            foreach (var m in response.IdPass)
                                Console.WriteLine($"  {m.Name}: {m.CoveragePercent:F2}% coverage, bbox {m.BboxW}x{m.BboxH} at {m.BboxX},{m.BboxY} ({m.Pixels} px)");
                        }
                        if (response.Screen.Count > 0)
                        {
                            Console.WriteLine("--- screen-space measurements (projected through the camera) ---");
                            foreach (var m in response.Screen)
                                Console.WriteLine($"  {m.Name}: bbox x={m.X:F3} y={m.Y:F3} w={m.W:F3} h={m.H:F3} ({m.CoveragePercent:F2}% of frame){(m.PartiallyBehind ? " [partially behind camera]" : "")}");
                        }
                        foreach (var note in response.Notes) Console.WriteLine($"note: {note}");
                        if (!string.IsNullOrEmpty(response.StderrTail)) Console.Error.WriteLine($"[app stderr tail] {response.StderrTail}");
                    }, 960);
            }
            default: return Usage("eso app <list|presets|run|perceive> ...");
        }
    }

    // ---------- sessions ----------

    private static async Task<int> SessionsAsync(Transport transport, List<string> rest)
    {
        if (rest.Count < 1) return Usage("eso sessions <new|list|attach|input|kill> ...");
        switch (rest[0])
        {
            case "new":
            {
                if (rest.Count < 2) return Usage("eso sessions new <name> [shell]");
                return await ForEach(transport, "POST", "/api/sessions", new ShellSessionCreateRequest
                {
                    Name = rest[1],
                    Shell = rest.Count > 2 ? rest[2] : "default",
                }, r => Console.WriteLine(r.Text));
            }
            case "list":
                return await ForEach(transport, "GET", "/api/sessions", null, r =>
                {
                    var sessions = r.Json<List<ShellSessionDto>>();
                    if (sessions.Count == 0) { Console.WriteLine("(no shell sessions)"); return; }
                    foreach (var s in sessions)
                        Console.WriteLine($"{s.Name}\t{s.Shell}\tpid {s.Pid}\t{(s.Running ? "running" : "exited")}\tsince {s.StartedAt:u}");
                });
            case "attach":
            {
                if (rest.Count < 2) return Usage("eso sessions attach <name>");
                return await SessionAttachAsync(transport, rest[1]);
            }
            case "input":
            {
                if (rest.Count < 3) return Usage("eso sessions input <name> <text>");
                return await ForEach(transport, "POST", $"/api/sessions/{Uri.EscapeDataString(rest[1])}/input",
                    new ShellSessionInputRequest { Data = string.Join(" ", rest.Skip(2)) + "\n" }, r => Console.WriteLine(r.Text));
            }
            case "kill":
            {
                if (rest.Count < 2) return Usage("eso sessions kill <name>");
                return await ForEach(transport, "DELETE", $"/api/sessions/{Uri.EscapeDataString(rest[1])}", null, r => Console.WriteLine(r.Text));
            }
            default: return Usage("eso sessions <new|list|attach|input|kill> ...");
        }
    }

    private static async Task<int> SessionAttachAsync(Transport transport, string name)
    {
        long since = 0;
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        Console.WriteLine($"[attached to session '{name}' — type lines to send input, Ctrl+C to detach (the session keeps running)]");
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                var line = Console.ReadLine();
                if (line == null) break;
                try { await transport.SendAsync("POST", $"/api/sessions/{Uri.EscapeDataString(name)}/input", new ShellSessionInputRequest { Data = line + "\n" }, 60); }
                catch { break; }
            }
        });
        while (!cts.IsCancellationRequested)
        {
            List<DeviceReply> replies;
            try { replies = await transport.SendAsync("GET", $"/api/sessions/{Uri.EscapeDataString(name)}?since={since}", null, 60); }
            catch (Exception ex) { Console.Error.WriteLine($"error: {ex.Message}"); return 1; }
            var ended = false;
            foreach (var reply in replies)
            {
                if (!reply.Ok) { Console.Error.WriteLine($"[{reply.Label}] FAILED: {reply.Error ?? ("HTTP " + reply.Status)}"); return 1; }
                var output = reply.Json<ShellSessionOutputResponse>();
                foreach (var chunk in output.Chunks)
                {
                    if (chunk.Stream == "stderr") Console.Error.Write(chunk.Text);
                    else Console.Write(chunk.Text);
                    since = Math.Max(since, chunk.Seq);
                }
                if (!output.Running && output.Chunks.Count == 0) ended = true;
            }
            if (ended) { Console.WriteLine("\n[session ended]"); return 0; }
            try { await Task.Delay(600, cts.Token); } catch { break; }
        }
        Console.WriteLine("\n[detached — session keeps running on the device]");
        return 0;
    }

    // ---------- patch / files / recordings ----------

    private static async Task<int> PatchAsync(Transport transport, List<string> rest)
    {
        if (rest.Count < 1) return Usage("eso patch <check|apply> [ids...]");
        switch (rest[0])
        {
            case "check":
                return await ForEach(transport, "GET", "/api/patch", null, r =>
                {
                    var status = r.Json<PatchStatusResponse>();
                    Console.WriteLine($"source: {status.Source} · pending: {status.Pending.Count} · reboot required: {status.RebootRequired}");
                    foreach (var p in status.Pending)
                        Console.WriteLine($"  {p.Id}\t[{p.Kind}]\t{p.Name} {p.Version} {p.SizeText}");
                    if (status.Error != null) Console.WriteLine($"error: {status.Error}");
                }, 300);
            case "apply":
            {
                var ids = rest.Skip(1).ToList();
                return await ForEach(transport, "POST", "/api/patch/apply", new PatchApplyRequest { Ids = ids }, r =>
                {
                    var result = r.Json<PatchApplyResponse>();
                    Console.WriteLine($"{(result.Success ? "OK" : "FAILED")} — remaining pending: {result.Remaining}, reboot required: {result.RebootRequired}");
                    if (!string.IsNullOrWhiteSpace(result.Output)) Console.WriteLine(result.Output.TrimEnd());
                }, 1800);
            }
            default: return Usage("eso patch <check|apply> [ids...]");
        }
    }

    private static async Task<int> FilesAsync(Transport transport, List<string> rest)
    {
        if (rest.Count < 1) return Usage("eso files <ls|rm|mv|mkdir> ...");
        switch (rest[0])
        {
            case "ls":
            {
                var path = rest.Count > 1 ? rest[1] : "";
                var query = string.IsNullOrEmpty(path) ? "" : $"?path={Uri.EscapeDataString(path)}";
                return await ForEach(transport, "GET", $"/api/files/list{query}", null, r =>
                {
                    var list = r.Json<FileListResponse>();
                    Console.WriteLine(list.Path);
                    foreach (var e in list.Entries)
                        Console.WriteLine(e.IsDir ? $"  [dir]  {e.Name}" : $"  {e.Size,12}  {e.Name}");
                });
            }
            case "rm":
                if (rest.Count < 2) return Usage("eso files rm <path>");
                return await ForEach(transport, "POST", "/api/files/delete", new FilePathRequest { Path = rest[1] }, r => Console.WriteLine(r.Text));
            case "mv":
                if (rest.Count < 3) return Usage("eso files mv <from> <to>");
                return await ForEach(transport, "POST", "/api/files/rename", new FileRenameRequest { Path = rest[1], NewPath = rest[2] }, r => Console.WriteLine(r.Text));
            case "mkdir":
                if (rest.Count < 2) return Usage("eso files mkdir <path>");
                return await ForEach(transport, "POST", "/api/files/mkdir", new FilePathRequest { Path = rest[1] }, r => Console.WriteLine(r.Text));
            default: return Usage("eso files <ls|rm|mv|mkdir> ...");
        }
    }

    private static async Task<int> RecordingsAsync(Transport transport, List<string> rest)
    {
        if (rest.Count < 1) return Usage("eso recordings <list|get|delete> ...");
        switch (rest[0])
        {
            case "list":
                return await ForEach(transport, "GET", "/api/recordings", null, r =>
                {
                    var recordings = r.Json<List<RecordingInfoDto>>();
                    if (recordings.Count == 0) { Console.WriteLine("(no recordings)"); return; }
                    foreach (var rec in recordings)
                        Console.WriteLine($"{rec.Name}\t{rec.SizeBytes / 1024} KB\t{rec.StartedAt:u}\t{rec.Codec} {rec.Width}x{rec.Height}");
                });
            case "get":
            {
                if (rest.Count < 3) return Usage("eso recordings get <name> <out.esorec>");
                return await ForEach(transport, "GET", $"/api/recordings/{Uri.EscapeDataString(rest[1])}", null, r =>
                {
                    File.WriteAllBytes(rest[2], r.Body);
                    Console.WriteLine($"downloaded {r.Body.Length} bytes -> {rest[2]}");
                }, 600);
            }
            case "delete":
                if (rest.Count < 2) return Usage("eso recordings delete <name>");
                return await ForEach(transport, "DELETE", $"/api/recordings/{Uri.EscapeDataString(rest[1])}", null, r => Console.WriteLine(r.Text));
            default: return Usage("eso recordings <list|get|delete> ...");
        }
    }

    // ---------- small helpers ----------

    private static string? GetFlag(List<string> args, string flag)
    {
        var at = args.IndexOf(flag);
        return at >= 0 && at + 1 < args.Count ? args[at + 1] : null;
    }

    private static Dictionary<string, string> ParseParams(IEnumerable<string> args)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var arg in args)
        {
            var eq = arg.IndexOf('=');
            if (eq > 0) parameters[arg[..eq]] = arg[(eq + 1)..];
        }
        return parameters;
    }

    private static string Sanitize(string name)
    {
        var chars = name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray();
        return new string(chars).Trim('_');
    }
}
