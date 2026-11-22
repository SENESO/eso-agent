using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EsoAgent.Core;

/// <summary>
/// The full API surface — identical on every head. exec/shell are an UNRESTRICTED
/// remote shell at the service's privilege level (LocalSystem/root), exactly
/// SSH's power; the structured endpoints are conveniences on top, never gates.
/// </summary>
public static class Endpoints
{
    public static void MapEsoAgentEndpoints(this IEndpointRouteBuilder app, AgentState state)
    {
        var runner = state.Platform.ProcessRunner;

        app.MapGet("/api/status", async () => Results.Json(await BuildStatusAsync(state), EsoJson.Options));

        // ----- unrestricted shell -----
        app.MapPost("/api/exec", async (HttpRequest req) =>
        {
            var body = await req.ReadFromJsonAsync<ExecRequest>(EsoJson.Options);
            if (body == null || string.IsNullOrWhiteSpace(body.File)) return Bad("Missing 'file'.");
            return Results.Json(await runner.RunAsync(body), EsoJson.Options);
        });

        app.MapPost("/api/shell", async (HttpRequest req) =>
        {
            var body = await req.ReadFromJsonAsync<ShellRequest>(EsoJson.Options);
            if (body == null) return Bad("Missing shell request.");
            try { return Results.Json(await runner.RunAsync(ProcessRunner.ResolveShell(body)), EsoJson.Options); }
            catch (ArgumentException ex) { return Bad(ex.Message); }
        });

        app.Map("/api/exec/stream", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
            using var ws = await context.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[64 * 1024];
            var first = await ws.ReceiveAsync(buffer, CancellationToken.None);
            var json = Encoding.UTF8.GetString(buffer, 0, first.Count);
            var execReq = JsonSerializer.Deserialize<ExecRequest>(json, EsoJson.Options);
            if (execReq == null) { await ws.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "bad request", CancellationToken.None); return; }

            async Task Send(object frame)
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, EsoJson.Options);
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            var result = await runner.RunStreamingAsync(execReq,
                async (stream, text) => await Send(new { type = "data", stream, data = text }),
                context.RequestAborted);
            await Send(new { type = "exit", exitCode = result.ExitCode, timedOut = result.TimedOut, durationMs = result.DurationMs });
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        });

        // Remote desktop (operator path; the owner panel has its own session-authed twin).
        app.Map("/api/desktop", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
            using var ws = await context.WebSockets.AcceptWebSocketAsync();
            await DesktopStreamRunner.RunAsync(ws, state, "api", context.RequestAborted);
        });

        app.MapPost("/api/launch", async (HttpRequest req) =>
        {
            var body = await req.ReadFromJsonAsync<ExecRequest>(EsoJson.Options);
            if (body == null || string.IsNullOrWhiteSpace(body.File)) return Bad("Missing 'file'.");
            var pid = runner.LaunchDetached(body);
            return Results.Json(new LaunchResponse { Pid = pid }, EsoJson.Options);
        });

        app.MapGet("/api/launched", () => Results.Json(LaunchedProcesses.List(), EsoJson.Options));

        // ----- durable jobs (v1.1) -----
        app.MapPost("/api/jobs", async (HttpRequest req) =>
        {
            if (state.Jobs == null) return NotSupportedPlatform();
            var body = await req.ReadFromJsonAsync<JobStartRequest>(EsoJson.Options);
            if (body == null || string.IsNullOrWhiteSpace(body.Command)) return Bad("Missing 'command'.");
            try { return Results.Json(state.Jobs.Start(body), EsoJson.Options); }
            catch (Exception ex) { return Bad($"Job failed to start: {ex.Message}"); }
        });

        app.MapGet("/api/jobs", () =>
            state.Jobs == null ? NotSupportedPlatform() : Results.Json(state.Jobs.List(), EsoJson.Options));

        app.MapGet("/api/jobs/{id}", (string id, int? tail) =>
        {
            if (state.Jobs == null) return NotSupportedPlatform();
            var record = state.Jobs.Get(id, tail ?? 100);
            return record == null
                ? Results.Json(new ErrorResponse { Error = "Job not found." }, EsoJson.Options, statusCode: 404)
                : Results.Json(record, EsoJson.Options);
        });

        app.MapPost("/api/jobs/{id}/cancel", (string id) =>
        {
            if (state.Jobs == null) return NotSupportedPlatform();
            var record = state.Jobs.Cancel(id);
            return record == null
                ? Results.Json(new ErrorResponse { Error = "Job not found." }, EsoJson.Options, statusCode: 404)
                : Results.Json(record, EsoJson.Options);
        });

        // ----- supervision (v1.1) -----
        app.MapGet("/api/supervised", () =>
            Results.Json(state.Supervisor?.Snapshot() ?? new List<SupervisedStatusDto>(), EsoJson.Options));

        // ----- inventory + doctor (v1.1) -----
        app.MapGet("/api/inventory", async (HttpRequest req) =>
        {
            var inventory = await InventoryBuilder.BuildAsync(state);
            if (req.Query["delta"] != "1") return Results.Json(inventory, EsoJson.Options);
            var json = JsonSerializer.Serialize(inventory, EsoJson.Options);
            return Results.Json(DeltaEnvelope.Build(state.Deltas, "inventory", json, deltaRequested: true), EsoJson.Options);
        });

        // ----- efficiency layer (v1.2 item 37): intents + anomaly events -----
        app.MapPost("/api/intent", async (HttpRequest req) =>
        {
            var body = await req.ReadFromJsonAsync<IntentRequest>(EsoJson.Options);
            if (body == null) return Bad("Missing intent.");
            return Results.Json(await IntentService.ExecuteAsync(state, body, req.HttpContext.RequestAborted), EsoJson.Options);
        });

        // Anomaly-only event stream: standing watches (telemetry thresholds,
        // crash watch, file-change watch, supervision, doctor) write audit
        // lines ONLY when something happens; this reads them with a cursor.
        app.MapGet("/api/events", (int? since) =>
        {
            var (lines, total) = state.Audit.ReadRange(since ?? 0, 5000);
            return Results.Json(AgentEvents.FromAuditLines(lines, total, since ?? 0), EsoJson.Options);
        });
        app.MapGet("/api/doctor", async () => Results.Json(await Doctor.BuildAsync(state), EsoJson.Options));

        // ----- processes -----
        app.MapGet("/api/processes", () => Results.Json(ProcessListing.List(), EsoJson.Options));

        app.MapPost("/api/processes/{pid:int}/kill", (int pid, HttpRequest req) =>
        {
            var tree = req.Query.TryGetValue("tree", out var t) && t.ToString().Equals("true", StringComparison.OrdinalIgnoreCase);
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill(entireProcessTree: tree);
                return Results.Json(new OperationResult { Success = true, Verified = true, Message = $"Killed pid {pid}." }, EsoJson.Options);
            }
            catch (Exception ex) { return Results.Json(new OperationResult { Success = false, Message = ex.Message }, EsoJson.Options, statusCode: 500); }
        });

        // ----- files -----
        app.MapGet("/api/files/info", (string path) => Results.Json(state.Files.Info(path), EsoJson.Options));

        app.MapPost("/api/files/begin", async (HttpRequest req) =>
        {
            var body = await req.ReadFromJsonAsync<BeginUploadRequest>(EsoJson.Options);
            if (body == null || string.IsNullOrWhiteSpace(body.Path)) return Bad("Missing 'path'.");
            return Results.Json(new BeginUploadResponse { Received = state.Files.BeginUpload(body.Path) }, EsoJson.Options);
        });

        app.MapPut("/api/files/chunk", async (HttpRequest req) =>
        {
            var path = req.Query["path"].ToString();
            if (string.IsNullOrWhiteSpace(path) || !long.TryParse(req.Query["offset"].ToString(), out var offset))
                return Bad("Need ?path= and ?offset=.");
            using var ms = new MemoryStream();
            await req.Body.CopyToAsync(ms);
            if (ms.Length > 32 * 1024 * 1024) return Bad("Chunk too large (32 MB cap).");
            try
            {
                var received = state.Files.AppendChunk(path, offset, ms.ToArray());
                return Results.Json(new ChunkResponse { Received = received }, EsoJson.Options);
            }
            catch (ChunkOffsetException ex)
            {
                return Results.Json(new ErrorResponse { Error = ex.Message }, EsoJson.Options, statusCode: 409);
            }
        });

        app.MapPost("/api/files/commit", async (HttpRequest req) =>
        {
            var body = await req.ReadFromJsonAsync<CommitUploadRequest>(EsoJson.Options);
            if (body == null) return Bad("Missing commit request.");
            try
            {
                var hash = state.Files.CommitUpload(body.Path, body.Sha256);
                return Results.Json(new FileInfoResponse { Exists = true, Size = new FileInfo(body.Path).Length, Sha256 = hash }, EsoJson.Options);
            }
            catch (Exception ex) { return Bad(ex.Message); }
        });

        app.MapGet("/api/files/download", async (HttpRequest req, HttpResponse res) =>
        {
            var path = req.Query["path"].ToString();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) { res.StatusCode = 404; return; }
            long offset = long.TryParse(req.Query["offset"].ToString(), out var o) ? o : 0;
            long length = long.TryParse(req.Query["length"].ToString(), out var l) ? l : -1;
            var total = new FileInfo(path).Length;
            res.Headers["X-Total-Size"] = total.ToString();
            res.ContentType = "application/octet-stream";
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(offset, SeekOrigin.Begin);
            var remaining = length < 0 ? total - offset : Math.Min(length, total - offset);
            var buf = new byte[64 * 1024];
            while (remaining > 0)
            {
                var n = await fs.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, remaining)));
                if (n == 0) break;
                await res.Body.WriteAsync(buf.AsMemory(0, n));
                remaining -= n;
            }
        });

        // ----- file manager (v1.2): browse + mutate, same transfer layer below -----
        app.MapGet("/api/files/list", (string? path) =>
        {
            var dir = string.IsNullOrWhiteSpace(path)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : path;
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                return Results.Json(new ErrorResponse { Error = $"Directory not found: {dir}" }, EsoJson.Options, statusCode: 404);
            var entries = new List<FileEntryDto>();
            foreach (var d in Directory.EnumerateDirectories(dir))
            {
                try { entries.Add(new FileEntryDto { Name = Path.GetFileName(d), Path = d, IsDir = true }); } catch { }
            }
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                try { var fi = new FileInfo(f); entries.Add(new FileEntryDto { Name = fi.Name, Path = f, IsDir = false, Size = fi.Length }); } catch { }
            }
            entries.Sort((a, b) => a.IsDir != b.IsDir ? (a.IsDir ? -1 : 1) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return Results.Json(new FileListResponse
            {
                Path = dir,
                Parent = Directory.GetParent(dir)?.FullName,
                Entries = entries,
            }, EsoJson.Options);
        });

        app.MapPost("/api/files/delete", async (HttpRequest req) =>
        {
            var body = await req.ReadFromJsonAsync<FilePathRequest>(EsoJson.Options);
            if (body == null || string.IsNullOrWhiteSpace(body.Path)) return Bad("Missing 'path'.");
            try
            {
                if (Directory.Exists(body.Path)) Directory.Delete(body.Path, recursive: true);
                else if (File.Exists(body.Path)) File.Delete(body.Path);
                else return Results.Json(new ErrorResponse { Error = $"Not found: {body.Path}" }, EsoJson.Options, statusCode: 404);
                state.Audit.Write("/api/files/delete", $"deleted {body.Path}", "ok");
                return Results.Json(new OperationResult { Success = true, Verified = !File.Exists(body.Path) && !Directory.Exists(body.Path), Message = $"Deleted {body.Path}." }, EsoJson.Options);
            }
            catch (Exception ex) { return Bad(ex.Message); }
        });

        app.MapPost("/api/files/rename", async (HttpRequest req) =>
        {
            var body = await req.ReadFromJsonAsync<FileRenameRequest>(EsoJson.Options);
            if (body == null || string.IsNullOrWhiteSpace(body.Path) || string.IsNullOrWhiteSpace(body.NewPath)) return Bad("Missing 'path'/'newPath'.");
            try
            {
                if (Directory.Exists(body.Path)) Directory.Move(body.Path, body.NewPath);
                else if (File.Exists(body.Path)) File.Move(body.Path, body.NewPath);
                else return Results.Json(new ErrorResponse { Error = $"Not found: {body.Path}" }, EsoJson.Options, statusCode: 404);
                state.Audit.Write("/api/files/rename", $"renamed {body.Path} -> {body.NewPath}", "ok");
                return Results.Json(new OperationResult { Success = true, Verified = File.Exists(body.NewPath) || Directory.Exists(body.NewPath), Message = $"Renamed to {body.NewPath}." }, EsoJson.Options);
            }
            catch (Exception ex) { return Bad(ex.Message); }
        });

        app.MapPost("/api/files/mkdir", async (HttpRequest req) =>
        {
            var body = await req.ReadFromJsonAsync<FilePathRequest>(EsoJson.Options);
            if (body == null || string.IsNullOrWhiteSpace(body.Path)) return Bad("Missing 'path'.");
            try
            {
                Directory.CreateDirectory(body.Path);
                state.Audit.Write("/api/files/mkdir", $"created directory {body.Path}", "ok");
                return Results.Json(new OperationResult { Success = true, Verified = Directory.Exists(body.Path), Message = $"Created {body.Path}." }, EsoJson.Options);
            }
            catch (Exception ex) { return Bad(ex.Message); }
        });

        // ----- desktop session recordings (v1.2) -----
        app.MapGet("/api/recordings", () => Results.Json(SessionRecorder.List(state.RecordingsDir), EsoJson.Options));

        app.MapGet("/api/recordings/{name}", async (HttpContext ctx, string name) =>
        {
            // Names are generated by the recorder; refuse anything path-like outright.
            if (name.Contains('/') || name.Contains('\\') || !name.EndsWith(".esorec", StringComparison.OrdinalIgnoreCase))
            { ctx.Response.StatusCode = 400; return; }
            var full = Path.Combine(state.RecordingsDir, name);
            if (!File.Exists(full)) { ctx.Response.StatusCode = 404; return; }
            ctx.Response.ContentType = "application/octet-stream";
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{name}\"";
            await using var fs = File.OpenRead(full);
            await fs.CopyToAsync(ctx.Response.Body);
        });

        app.MapDelete("/api/recordings/{name}", (string name) =>
        {
            if (name.Contains('/') || name.Contains('\\') || !name.EndsWith(".esorec", StringComparison.OrdinalIgnoreCase))
                return Bad("Bad recording name.");
            var full = Path.Combine(state.RecordingsDir, name);
            if (!File.Exists(full)) return Results.Json(new ErrorResponse { Error = "Recording not found." }, EsoJson.Options, statusCode: 404);
            File.Delete(full);
            state.Audit.Write("/api/recordings", $"recording deleted {name}", "ok");
            return Results.Json(new OperationResult { Success = true, Verified = !File.Exists(full), Message = $"Deleted {name}." }, EsoJson.Options);
        });

        // ----- Wake-on-LAN (v1.2): this device sends a magic packet on its LANs -----
        app.MapPost("/api/wake", async (HttpRequest req) =>
        {
            var body = await req.ReadFromJsonAsync<WakeRequest>(EsoJson.Options);
            if (body == null || !MagicPacket.TryParseMac(body.Mac, out _)) return Bad("Missing or invalid 'mac'.");
            try
            {
                var sent = await WakeSender.SendAsync(body.Mac, req.HttpContext.RequestAborted);
                state.Audit.Write("/api/wake", $"magic packet for {MagicPacket.Normalize(body.Mac)} sent via [{string.Join(", ", sent)}]", "ok");
                return Results.Json(new { mac = MagicPacket.Normalize(body.Mac), sentVia = sent }, EsoJson.Options);
            }
            catch (Exception ex) { return Bad(ex.Message); }
        });

        // ----- device scheduled tasks (v1.2; write path added with the UI overhaul) -----
        app.MapGet("/api/schedules", () =>
            Results.Json(state.Scheduler?.Snapshot() ?? new List<ScheduleStateDto>(), EsoJson.Options));

        app.MapGet("/api/schedules/config", () =>
            Results.Json(state.Scheduler?.Configs() ?? new List<ScheduledTaskConfig>(), EsoJson.Options));

        // Replace the whole set: the Tasks UIs edit the list as one document.
        // Validated by ScheduleValidation, persisted to esoagent.json like the
        // owner policy, and applied live — no agent restart.
        app.MapPut("/api/schedules", async (HttpRequest req) =>
        {
            if (state.Scheduler == null) return Bad("schedule service not running");
            var list = await req.ReadFromJsonAsync<List<ScheduledTaskConfig>>(EsoJson.Options);
            if (list == null) return Bad("Bad schedule list.");
            if (ScheduleValidation.ValidateList(list) is { } error) return Bad(error);
            state.Config.Schedules = list;
            state.Scheduler.ReplaceSchedules(list);
            Dashboard.SaveConfig(state);
            state.Audit.Write("/api/schedules", $"schedules replaced ({list.Count} task(s)) via API", "ok");
            return Results.Json(state.Scheduler.Snapshot(), EsoJson.Options);
        });

        app.MapPost("/api/schedules/{name}/run", async (string name) =>
        {
            if (state.Scheduler == null) return Bad("schedule service not running");
            var started = await state.Scheduler.RunNowAsync(name);
            if (!started)
                return Results.Json(new ErrorResponse { Error = $"No idle schedule named '{name}'." }, EsoJson.Options, statusCode: 404);
            state.Audit.Write("/api/schedules", $"schedule '{name}' run on demand via API", "ok");
            return Results.Json(new { started = name }, EsoJson.Options);
        });

        // ----- OS patch management (v1.2) -----
        app.MapGet("/api/patch", async (HttpRequest req) =>
            Results.Json(await PatchService.CheckAsync(state, req.HttpContext.RequestAborted), EsoJson.Options));

        app.MapPost("/api/patch/apply", async (HttpRequest req) =>
        {
            var body = await req.ReadFromJsonAsync<PatchApplyRequest>(EsoJson.Options) ?? new PatchApplyRequest();
            return Results.Json(await PatchService.ApplyAsync(state, body, req.HttpContext.RequestAborted), EsoJson.Options);
        });

        // ----- persistent shell sessions (v1.2) -----
        app.MapGet("/api/sessions", () =>
            state.ShellSessions == null ? NotSupportedPlatform() : Results.Json(state.ShellSessions.List(), EsoJson.Options));

        app.MapPost("/api/sessions", async (HttpRequest req) =>
        {
            if (state.ShellSessions == null) return NotSupportedPlatform();
            var body = await req.ReadFromJsonAsync<ShellSessionCreateRequest>(EsoJson.Options);
            if (body == null || string.IsNullOrWhiteSpace(body.Name)) return Bad("Missing 'name'.");
            try { return Results.Json(state.ShellSessions.Create(body), EsoJson.Options); }
            catch (Exception ex) { return Bad(ex.Message); }
        });

        app.MapGet("/api/sessions/{name}", (string name, long? since) =>
        {
            if (state.ShellSessions == null) return NotSupportedPlatform();
            var output = state.ShellSessions.GetOutput(name, since ?? 0);
            return output == null
                ? Results.Json(new ErrorResponse { Error = "Session not found." }, EsoJson.Options, statusCode: 404)
                : Results.Json(output, EsoJson.Options);
        });

        app.MapPost("/api/sessions/{name}/input", async (string name, HttpRequest req) =>
        {
            if (state.ShellSessions == null) return NotSupportedPlatform();
            var body = await req.ReadFromJsonAsync<ShellSessionInputRequest>(EsoJson.Options);
            if (body == null) return Bad("Missing input.");
            return state.ShellSessions.SendInput(name, body.Data)
                ? Results.Json(new OperationResult { Success = true, Message = "Input sent." }, EsoJson.Options)
                : Results.Json(new ErrorResponse { Error = "Session not found or no longer running." }, EsoJson.Options, statusCode: 404);
        });

        app.MapDelete("/api/sessions/{name}", (string name) =>
        {
            if (state.ShellSessions == null) return NotSupportedPlatform();
            return state.ShellSessions.Kill(name)
                ? Results.Json(new OperationResult { Success = true, Message = $"Session '{name}' killed." }, EsoJson.Options)
                : Results.Json(new ErrorResponse { Error = "Session not found." }, EsoJson.Options, statusCode: 404);
        });

        // ----- TCP tunnel (v1.2): WS upgrade, first frame = TunnelTarget JSON,
        // then raw binary frames piped to a TCP connection FROM this device.
        // The fleet-relayed twin lives in FleetClientService (stream kind "tunnel").
        app.Map("/api/tunnel", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
            using var ws = await context.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[16 * 1024];
            var first = await ws.ReceiveAsync(buffer, context.RequestAborted);
            if (first.MessageType == WebSocketMessageType.Close) return;
            TunnelTarget? target;
            try { target = JsonSerializer.Deserialize<TunnelTarget>(Encoding.UTF8.GetString(buffer, 0, first.Count), EsoJson.Options); }
            catch { target = null; }
            if (target == null || string.IsNullOrWhiteSpace(target.Host) || target.Port is < 1 or > 65535)
            {
                await ws.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "bad tunnel target", CancellationToken.None);
                return;
            }
            // UDP mode (?udp=1): one WS binary message = one datagram, both ways.
            if (context.Request.Query["udp"] == "1")
            {
                using var udp = new System.Net.Sockets.UdpClient();
                try { udp.Connect(target.Host, target.Port); }
                catch (Exception ex)
                {
                    state.Audit.Write("/api/tunnel", $"udp tunnel to {target} failed: {ex.Message}", "error");
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, $"connect failed: {ex.Message}", CancellationToken.None);
                    return;
                }
                state.Audit.Write("/api/tunnel", $"udp tunnel opened to {target}", "ok");
                using var udpCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                var udpToWs = Task.Run(async () =>
                {
                    try
                    {
                        while (!udpCts.Token.IsCancellationRequested)
                        {
                            var datagram = await udp.ReceiveAsync(udpCts.Token);
                            await ws.SendAsync(datagram.Buffer, WebSocketMessageType.Binary, true, udpCts.Token);
                        }
                    }
                    catch { }
                    udpCts.Cancel();
                });
                try
                {
                    var buf = new byte[64 * 1024];
                    while (!udpCts.Token.IsCancellationRequested && ws.State == WebSocketState.Open)
                    {
                        using var ms = new MemoryStream();
                        WebSocketReceiveResult frame;
                        do
                        {
                            frame = await ws.ReceiveAsync(buf, udpCts.Token);
                            if (frame.MessageType == WebSocketMessageType.Close) break;
                            ms.Write(buf, 0, frame.Count);
                        } while (!frame.EndOfMessage);
                        if (frame.MessageType == WebSocketMessageType.Close) break;
                        await udp.SendAsync(ms.ToArray(), udpCts.Token);
                    }
                }
                catch { }
                udpCts.Cancel();
                try { await udpToWs; } catch { }
                state.Audit.Write("/api/tunnel", $"udp tunnel to {target} closed", "ok");
                return;
            }
            using var tcp = new System.Net.Sockets.TcpClient();
            try { await tcp.ConnectAsync(target.Host, target.Port, context.RequestAborted); }
            catch (Exception ex)
            {
                state.Audit.Write("/api/tunnel", $"tunnel to {target} failed: {ex.Message}", "error");
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, $"connect failed: {ex.Message}", CancellationToken.None);
                return;
            }
            state.Audit.Write("/api/tunnel", $"tunnel opened to {target}", "ok");
            var stream = tcp.GetStream();
            using var tunnelCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            var socketToWs = Task.Run(async () =>
            {
                var buf = new byte[TunnelPump.ChunkBytes];
                try
                {
                    while (!tunnelCts.Token.IsCancellationRequested)
                    {
                        var read = await stream.ReadAsync(buf, tunnelCts.Token);
                        if (read <= 0) break;
                        await ws.SendAsync(buf.AsMemory(0, read), WebSocketMessageType.Binary, true, tunnelCts.Token);
                    }
                }
                catch { }
                tunnelCts.Cancel();
            });
            try
            {
                var buf = new byte[TunnelPump.ChunkBytes];
                while (!tunnelCts.Token.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    var frame = await ws.ReceiveAsync(buf, tunnelCts.Token);
                    if (frame.MessageType == WebSocketMessageType.Close) break;
                    if (frame.Count > 0) await stream.WriteAsync(buf.AsMemory(0, frame.Count), tunnelCts.Token);
                }
            }
            catch { }
            tunnelCts.Cancel();
            try { await socketToWs; } catch { }
            state.Audit.Write("/api/tunnel", $"tunnel to {target} closed", "ok");
        });

        // ----- logs -----
        app.MapGet("/api/logs/agent", (int? lines) =>
            Results.Json(new { lines = state.Audit.Tail(lines ?? 100) }, EsoJson.Options));

        app.MapGet("/api/logs/file", (string path, int? lines) =>
        {
            try { return Results.Json(new { lines = AuditLog.TailFile(path, lines ?? 100) }, EsoJson.Options); }
            catch (Exception ex) { return Results.Json(new ErrorResponse { Error = ex.Message }, EsoJson.Options, statusCode: 500); }
        });

        // Log follow (v1.1): same WS shape as /api/exec/stream. The client sends
        // one JSON frame {path, tailLines, pattern}; the server answers with the
        // tail, then streams appended lines as {type:"line"} frames until the
        // client disconnects. Any readable path — the operator is unrestricted.
        app.Map("/api/logs/follow", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
            using var ws = await context.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[64 * 1024];
            var first = await ws.ReceiveAsync(buffer, context.RequestAborted);
            var followReq = JsonSerializer.Deserialize<LogFollowRequest>(
                Encoding.UTF8.GetString(buffer, 0, first.Count), EsoJson.Options);
            if (followReq == null || string.IsNullOrWhiteSpace(followReq.Path))
            {
                await ws.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "bad request", CancellationToken.None);
                return;
            }

            async Task Send(object frame)
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, EsoJson.Options);
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            }

            if (!File.Exists(followReq.Path))
            {
                await Send(new { type = "error", error = $"File not found: {followReq.Path}" });
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
                return;
            }

            // Watch for the client going away (close frame) to end the follow.
            using var followCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            var watchClose = Task.Run(async () =>
            {
                try
                {
                    var buf = new byte[1024];
                    while (ws.State == WebSocketState.Open)
                    {
                        var r = await ws.ReceiveAsync(buf, CancellationToken.None);
                        if (r.MessageType == WebSocketMessageType.Close) break;
                    }
                }
                catch { }
                followCts.Cancel();
            });

            try
            {
                var tailLines = Math.Clamp(followReq.TailLines, 0, 5000);
                if (tailLines > 0)
                    foreach (var line in AuditLog.TailFile(followReq.Path, tailLines))
                        if (LogFollowFilter.Matches(line, followReq.Pattern))
                            await Send(new { type = "line", line });

                long offset = new FileInfo(followReq.Path).Length;
                var leftover = "";
                var buf = new byte[32 * 1024];
                while (!followCts.Token.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    await Task.Delay(500, followCts.Token);
                    long length;
                    try { length = new FileInfo(followReq.Path).Length; }
                    catch { continue; } // file briefly missing (rotation) — keep following
                    if (length < offset)
                    {
                        offset = 0; leftover = "";
                        await Send(new { type = "reset", error = "file truncated/rotated — following from the start" });
                    }
                    if (length == offset) continue;
                    await using var fs = new FileStream(followReq.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    fs.Seek(offset, SeekOrigin.Begin);
                    int read;
                    var pending = leftover;
                    while ((read = await fs.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, length - offset)), followCts.Token)) > 0)
                    {
                        offset += read;
                        pending += Encoding.UTF8.GetString(buf, 0, read);
                        var parts = pending.Split('\n');
                        pending = parts[^1];
                        for (int i = 0; i < parts.Length - 1; i++)
                        {
                            var line = parts[i].TrimEnd('\r');
                            if (LogFollowFilter.Matches(line, followReq.Pattern))
                                await Send(new { type = "line", line });
                        }
                    }
                    leftover = pending;
                }
                if (leftover.Length > 0 && LogFollowFilter.Matches(leftover, followReq.Pattern))
                    await Send(new { type = "line", line = leftover });
            }
            catch (OperationCanceledException) { /* client went away */ }
            catch (Exception ex)
            {
                try { await Send(new { type = "error", error = ex.Message }); } catch { }
            }
            finally
            {
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { }
                _ = watchClose;
            }
        });

        // ----- power -----
        app.MapPost("/api/power/reboot", async () => Results.Json(await state.Platform.PowerControl.RebootAsync(), EsoJson.Options));
        app.MapPost("/api/power/shutdown", async () => Results.Json(await state.Platform.PowerControl.ShutdownAsync(), EsoJson.Options));
        app.MapPost("/api/power/sleep", async () => Results.Json(await state.Platform.PowerControl.SleepAsync(), EsoJson.Options));

        // ----- session-dependent -----
        app.MapGet("/api/screenshot", async () =>
        {
            var (png, error) = await state.Platform.Screenshot.CaptureAsync();
            if (png == null) return Results.Json(new ErrorResponse { Error = error ?? "Screenshot not available." }, EsoJson.Options, statusCode: 501);
            return Results.File(png, "image/png");
        });

        app.MapPost("/api/interactive/exec", async (HttpRequest req) =>
        {
            var body = await req.ReadFromJsonAsync<ExecRequest>(EsoJson.Options);
            if (body == null) return Bad("Missing exec request.");
            return Results.Json(await state.Platform.InteractiveExec.RunInUserSessionAsync(body), EsoJson.Options);
        });

        // ----- software -----
        app.MapGet("/api/software", async () => Results.Json(await state.Platform.Packages.ListAsync(), EsoJson.Options));
        app.MapPost("/api/software/install", async (HttpRequest req) =>
        {
            var body = await req.ReadFromJsonAsync<InstallRequest>(EsoJson.Options);
            if (body == null) return Bad("Missing install request.");
            return Results.Json(await state.Platform.Packages.InstallAsync(body), EsoJson.Options);
        });
        app.MapPost("/api/software/uninstall", async (HttpRequest req) =>
        {
            var body = await req.ReadFromJsonAsync<UninstallRequest>(EsoJson.Options);
            if (body == null) return Bad("Missing uninstall request.");
            return Results.Json(await state.Platform.Packages.UninstallAsync(body), EsoJson.Options);
        });
        app.MapPost("/api/software/upgrade", async (HttpRequest req) =>
        {
            var body = await req.ReadFromJsonAsync<UpgradeRequest>(EsoJson.Options) ?? new UpgradeRequest();
            return Results.Json(await state.Platform.Packages.UpgradeAsync(body), EsoJson.Options);
        });

        // ----- services -----
        app.MapGet("/api/services", async () => Results.Json(await state.Platform.Services.ListAsync(), EsoJson.Options));
        app.MapPost("/api/services/{name}/start", async (string name) => Results.Json(await state.Platform.Services.StartAsync(name), EsoJson.Options));
        app.MapPost("/api/services/{name}/stop", async (string name) => Results.Json(await state.Platform.Services.StopAsync(name), EsoJson.Options));
        app.MapPost("/api/services/{name}/restart", async (string name) => Results.Json(await state.Platform.Services.RestartAsync(name), EsoJson.Options));
        app.MapPut("/api/services/{name}/startup", async (string name, HttpRequest req) =>
        {
            var body = await req.ReadFromJsonAsync<StartupModeRequest>(EsoJson.Options);
            if (body == null) return Bad("Missing startup mode.");
            return Results.Json(await state.Platform.Services.SetStartupModeAsync(name, body.Mode), EsoJson.Options);
        });

        // ----- repair -----
        app.MapGet("/api/repair", () => Results.Json(state.Platform.Repair.Available, EsoJson.Options));
        app.MapPost("/api/repair/{name}", async (string name) => Results.Json(await state.Platform.Repair.RunAsync(name), EsoJson.Options));

        // ----- registry (Windows only) -----
        app.MapGet("/api/registry", (string path, string? name) =>
        {
            if (state.Platform.Registry == null) return NotSupportedPlatform();
            try { return Results.Json(state.Platform.Registry.Get(path, name ?? ""), EsoJson.Options); }
            catch (Exception ex) { return Bad(ex.Message); }
        });
        app.MapPut("/api/registry", async (HttpRequest req) =>
        {
            if (state.Platform.Registry == null) return NotSupportedPlatform();
            var body = await req.ReadFromJsonAsync<RegistrySetRequest>(EsoJson.Options);
            if (body == null) return Bad("Missing registry set request.");
            try
            {
                var (before, after) = state.Platform.Registry.Set(body);
                // Registry writes get an explicit audit line: key, value name, old -> new.
                state.Audit.Write("/api/registry", $"SET {body.Path} :: {body.Name} : {before.Value ?? "<none>"} -> {after.Value}", "ok");
                return Results.Json(after, EsoJson.Options);
            }
            catch (Exception ex) { return Bad(ex.Message); }
        });

        // ----- self-update -----
        app.MapPut("/api/update/package", async (HttpRequest req) =>
        {
            var sha = req.Headers["X-Package-Sha256"].ToString();
            var sig = req.Headers["X-Package-Signature"].ToString();
            if (string.IsNullOrWhiteSpace(sha) || string.IsNullOrWhiteSpace(sig)) return Bad("Missing X-Package-Sha256 / X-Package-Signature headers.");
            try
            {
                var verified = await state.Update.StageAsync(req.Body, sha, sig, state.Token, req.HttpContext.RequestAborted);
                return Results.Json(new { staged = true, sha256 = verified }, EsoJson.Options);
            }
            catch (UnauthorizedAccessException ex) { return Results.Json(new ErrorResponse { Error = ex.Message }, EsoJson.Options, statusCode: 401); }
            catch (Exception ex) { return Bad(ex.Message); }
        });
        app.MapPost("/api/update/apply", () =>
        {
            var result = state.Update.ApplyStaged(state.AppDir);
            return Results.Json(result, EsoJson.Options, statusCode: result.Success ? 200 : 400);
        });

        // Fleet rollout fetch (v1.2): the body is an UpdateFetchRequest SIGNED by
        // the operator or the fleet panel key. Verified against the trusted keys
        // the fleet server delivered at approval; the package is then downloaded
        // from the fleet server itself (path is server-relative — it cannot point
        // elsewhere), hash-checked, staged and applied with the usual rollback.
        app.MapPost("/api/update/fetch", async (HttpRequest req) =>
        {
            var body = await req.ReadFromJsonAsync<UpdateFetchRequest>(EsoJson.Options);
            if (body == null || string.IsNullOrWhiteSpace(body.Path) || string.IsNullOrWhiteSpace(body.Sha256))
                return Bad("Missing fetch request.");
            var key = body.Signer == "owner-panel" ? state.PanelPublicKeySpki : state.OperatorPublicKeySpki;
            if (key == null)
                return Results.Json(new ErrorResponse { Error = $"Signer '{body.Signer}' is not trusted by this device (not enrolled/approved, or revoked)." }, EsoJson.Options, statusCode: 403);
            if (!OperatorSigning.VerifyUpdateFetch(key, body.Signature, body.Path, body.Sha256))
            {
                state.Audit.Write("/api/update/fetch", $"REJECTED rollout fetch (signer {body.Signer}): signature invalid", "refused");
                return Results.Json(new ErrorResponse { Error = "Update fetch signature verification failed on device." }, EsoJson.Options, statusCode: 403);
            }
            var fleet = state.Config.Fleet;
            if (string.IsNullOrWhiteSpace(fleet.ServerUrl))
                return Bad("Device is not fleet-enrolled; rollout fetch has no server to download from.");
            var baseUri = new Uri(fleet.ServerUrl);
            var downloadUri = new Uri(baseUri, body.Path + (body.Path.Contains('?') ? "&" : "?") + "t=" + Uri.EscapeDataString(body.Token));
            try
            {
                using var handler = new HttpClientHandler { UseProxy = false };
                if (!string.IsNullOrWhiteSpace(fleet.ServerFingerprint))
                {
                    var fp = fleet.ServerFingerprint.Trim();
                    handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
                        cert != null && string.Equals(
                            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new System.Security.Cryptography.X509Certificates.X509Certificate2(cert).GetRawCertData())).ToLowerInvariant(),
                            fp, StringComparison.OrdinalIgnoreCase);
                }
                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
                await using var stream = await http.GetStreamAsync(downloadUri, req.HttpContext.RequestAborted);
                var verified = await state.Update.StageVerifiedAsync(stream, body.Sha256, req.HttpContext.RequestAborted);
                state.Audit.Write("/api/update/fetch", $"rollout package staged (sha256 {verified[..12]}…, signer {body.Signer})", "ok");
                var result = state.Update.ApplyStaged(state.AppDir);
                return Results.Json(result, EsoJson.Options, statusCode: result.Success ? 200 : 400);
            }
            catch (Exception ex) { return Bad($"Rollout fetch failed: {ex.Message}"); }
        });

        // ----- release channel (item 45): check now / apply a verified release -----
        app.MapPost("/api/update/check", async () =>
        {
            if (state.Release == null) return Results.Json(new { ok = false, message = "release service not running" });
            var outcome = await state.Release.CheckOnceAsync();
            return Results.Json(new { ok = true, message = outcome, updateAvailable = state.ReleaseUpdates.AvailableVersion, mode = state.Config.ReleaseUpdate.Mode });
        });
        app.MapPost("/api/update/apply-release", async () =>
        {
            // Shared with the tray's host window via the control pipe (Phase 5).
            var (ok, message, version) = await ReleaseApply.ApplyAvailableAsync(state);
            return Results.Json(new { ok, message, version });
        });
    }

    /// <summary>Status snapshot shared by /api/status and the owner panel overview.</summary>
    public static async Task<StatusResponse> BuildStatusAsync(AgentState state)
    {
        var info = state.Platform.SystemInfo;
        var (used, total) = info.GetMemory();
        var disks = new List<DiskDto>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try { if (d.IsReady && d.DriveType == DriveType.Fixed)
                    disks.Add(new DiskDto { Name = d.Name, TotalBytes = d.TotalSize, FreeBytes = d.AvailableFreeSpace }); }
            catch { }
        }
        return new StatusResponse
        {
            Os = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux",
            OsDescription = RuntimeInformation.OSDescription,
            Hostname = Environment.MachineName,
            AgentVersion = ProductInfo.Version,
            UptimeSeconds = GetSystemUptimeSeconds(),
            CpuPercent = await info.GetCpuPercentAsync(),
            RamUsedBytes = used,
            RamTotalBytes = total,
            Disks = disks,
            Battery = info.GetBattery(),
            TailnetIps = NetHelpers.DiscoverTailnetIps().ToList(),
            LoggedOnUsers = info.GetLoggedOnUsers().ToList(),
            Locked = state.KillSwitch.IsLocked,
            ServerTime = DateTimeOffset.UtcNow,
            DeviceCode = state.Device?.Code ?? "",
            DeviceName = state.Config.ResolvedDeviceName,
            FleetMode = state.Config.Fleet is { Enabled: true } ? "Fleet" : "Standalone",
            FleetConnected = state.FleetConnected,
            MeshEnabled = state.Config.Mesh.Enabled,
            MeshVirtualIp = state.MeshVirtualIp,
            MeshStatus = state.MeshStatus,
            P2pStatus = state.P2pStatus,
            Supervised = state.Supervisor?.Snapshot().ToList() ?? new List<SupervisedStatusDto>(),
            JobsRunning = state.Jobs?.RunningCount ?? 0,
            Capabilities = state.Config.Capabilities,
            UpdateAvailable = state.ReleaseUpdates.AvailableVersion,
            ReleaseUpdateMode = state.Config.ReleaseUpdate.Mode,
            DesktopPolicy = state.Config.DesktopPolicy,
        };
    }

    private static IResult Bad(string message)
        => Results.Json(new ErrorResponse { Error = message }, EsoJson.Options, statusCode: 400);

    private static IResult NotSupportedPlatform()
        => Results.Json(new ErrorResponse { Error = "Not supported on this platform." }, EsoJson.Options, statusCode: 501);

    private static long GetSystemUptimeSeconds()
    {
        try
        {
            if (OperatingSystem.IsLinux() && File.Exists("/proc/uptime"))
            {
                var text = File.ReadAllText("/proc/uptime").Split(' ')[0];
                if (double.TryParse(text, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var s)) return (long)s;
            }
        }
        catch { }
        return Environment.TickCount64 / 1000;
    }
}

/// <summary>Process listing with parent PIDs (Linux: /proc; Windows: NtQueryInformationProcess).</summary>
public static class ProcessListing
{
    public static IReadOnlyList<ProcessDto> List()
    {
        var result = new List<ProcessDto>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    result.Add(new ProcessDto
                    {
                        Pid = p.Id,
                        ParentPid = GetParentPid(p.Id),
                        Name = p.ProcessName,
                        SessionId = p.SessionId,
                        WorkingSetBytes = p.WorkingSet64,
                    });
                }
                catch { /* process exited mid-listing */ }
            }
        }
        return result.OrderBy(x => x.Pid).ToList();
    }

    private static int GetParentPid(int pid)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var stat = File.ReadAllText($"/proc/{pid}/stat");
                var afterComm = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                // after comm: state ppid ...
                return int.Parse(afterComm[1]);
            }
            if (OperatingSystem.IsWindows())
            {
                using var p = Process.GetProcessById(pid);
                var info = new ProcessBasicInformation();
                int retLen;
                if (NtQueryInformationProcess(p.Handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out retLen) == 0)
                    return info.InheritedFromUniqueProcessId.ToInt32();
            }
        }
        catch { }
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass,
        ref ProcessBasicInformation processInformation, int processInformationLength, out int returnLength);
}
