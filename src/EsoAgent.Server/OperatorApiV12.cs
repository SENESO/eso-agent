using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using EsoAgent.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EsoAgent.Server;


/// <summary>
/// The v1.2 OPERATOR API additions: the unified activity timeline (+ export),
/// cached device health, the script library, update-package staging + fleet
/// rollout, Wake-on-LAN, the mesh exit-node switch, and the relayed stream
/// endpoints (desktop / TCP tunnel / UDP tunnel). Same operator-signature
/// authentication as the rest of /fleet; the stream endpoints additionally
/// require the stream-open itself to carry a valid operator signature, which
/// the DEVICE verifies again — the relay gains no power from carrying it.
/// </summary>
public static class FleetOperatorApiV12
{
    public static void MapFleetOperatorApiV12(this IEndpointRouteBuilder app, FleetServerState state)
    {
        // ----- unified activity timeline -----
        app.MapGet("/fleet/activity", async (HttpContext ctx) =>
        {
            var body = await FleetOperatorApi.ReadBodyAsync(ctx);
            if (!FleetOperatorApi.CheckOperator(ctx, state, body, out var error)) return FleetOperatorApi.Denied(error);
            return Results.Json(state.Activity.Query(ParseQuery(ctx)), EsoJson.Options);
        });

        app.MapGet("/fleet/activity/export", async (HttpContext ctx) =>
        {
            var body = await FleetOperatorApi.ReadBodyAsync(ctx);
            if (!FleetOperatorApi.CheckOperator(ctx, state, body, out var error)) return FleetOperatorApi.Denied(error);
            var events = state.Activity.Export(ParseQuery(ctx));
            if (string.Equals(ctx.Request.Query["format"], "csv", StringComparison.OrdinalIgnoreCase))
                return Results.Text(ActivityCsv.ToCsv(events), "text/csv; charset=utf-8");
            return Results.Json(events, EsoJson.Options);
        });

        // ----- health dashboard cache -----
        app.MapGet("/fleet/health", async (HttpContext ctx) =>
        {
            var body = await FleetOperatorApi.ReadBodyAsync(ctx);
            if (!FleetOperatorApi.CheckOperator(ctx, state, body, out var error)) return FleetOperatorApi.Denied(error);
            return Results.Json(state.Health.All(), EsoJson.Options);
        });

        // ----- script library -----
        app.MapGet("/fleet/scripts", async (HttpContext ctx) =>
        {
            var body = await FleetOperatorApi.ReadBodyAsync(ctx);
            if (!FleetOperatorApi.CheckOperator(ctx, state, body, out var error)) return FleetOperatorApi.Denied(error);
            return Results.Json(state.Scripts.List(), EsoJson.Options);
        });

        app.MapGet("/fleet/scripts/{id}", async (HttpContext ctx, string id) =>
        {
            var body = await FleetOperatorApi.ReadBodyAsync(ctx);
            if (!FleetOperatorApi.CheckOperator(ctx, state, body, out var error)) return FleetOperatorApi.Denied(error);
            var script = state.Scripts.Find(id);
            return script == null
                ? Results.Json(new ErrorResponse { Error = "Script not found." }, EsoJson.Options, statusCode: 404)
                : Results.Json(script, EsoJson.Options);
        });

        app.MapPost("/fleet/scripts", async (HttpContext ctx) =>
        {
            var body = await FleetOperatorApi.ReadBodyAsync(ctx);
            if (!FleetOperatorApi.CheckOperator(ctx, state, body, out var error)) return FleetOperatorApi.Denied(error);
            var dto = JsonSerializer.Deserialize<ScriptDto>(body, EsoJson.Options);
            if (dto == null || string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrEmpty(dto.Body))
                return Results.Json(new ErrorResponse { Error = "A script needs a name and a body." }, EsoJson.Options, statusCode: 400);
            dto.CreatedBy = "operator";
            var saved = state.Scripts.Upsert(dto);
            state.Audit.Write("fleet/scripts", $"script '{saved.Name}' saved (operator)", "ok");
            state.Activity.Add("operator", "script", $"Script '{saved.Name}' saved", "ok");
            return Results.Json(saved, EsoJson.Options);
        });

        app.MapDelete("/fleet/scripts/{id}", async (HttpContext ctx, string id) =>
        {
            var body = await FleetOperatorApi.ReadBodyAsync(ctx);
            if (!FleetOperatorApi.CheckOperator(ctx, state, body, out var error)) return FleetOperatorApi.Denied(error);
            if (!state.Scripts.Delete(id))
                return Results.Json(new ErrorResponse { Error = "Script not found." }, EsoJson.Options, statusCode: 404);
            state.Activity.Add("operator", "script", $"Script '{id}' deleted", "ok");
            return Results.Json(new { deleted = id }, EsoJson.Options);
        });

        app.MapPost("/fleet/scripts/{id}/runs", async (HttpContext ctx, string id) =>
        {
            var body = await FleetOperatorApi.ReadBodyAsync(ctx);
            if (!FleetOperatorApi.CheckOperator(ctx, state, body, out var error)) return FleetOperatorApi.Denied(error);
            var script = state.Scripts.Find(id);
            if (script == null) return Results.Json(new ErrorResponse { Error = "Script not found." }, EsoJson.Options, statusCode: 404);
            var req = JsonSerializer.Deserialize<ScriptRunRecordRequest>(body, EsoJson.Options) ?? new();
            // The run itself executes through /fleet/command; this record is the
            // audit anchor: which script, which body hash, which targets, who ran it.
            state.Activity.Add("operator", "script",
                $"Script '{script.Name}' run on [{string.Join(",", req.Targets)}] (body sha256 {req.BodySha256})", "started");
            state.Audit.Write("fleet/scripts", $"script '{script.Name}' run on [{string.Join(",", req.Targets)}] sha256={req.BodySha256} (operator)", "ok");
            return Results.Json(new { recorded = true, script = script.Name }, EsoJson.Options);
        });

        // ----- update package + rollout -----
        app.MapGet("/fleet/update/package", async (HttpContext ctx) =>
        {
            var body = await FleetOperatorApi.ReadBodyAsync(ctx);
            if (!FleetOperatorApi.CheckOperator(ctx, state, body, out var error)) return FleetOperatorApi.Denied(error);
            var info = state.Packages.Current;
            return info == null
                ? Results.Json(new ErrorResponse { Error = "No update package has been uploaded." }, EsoJson.Options, statusCode: 404)
                : Results.Json(info, EsoJson.Options);
        });

        app.MapPut("/fleet/update/package", async (HttpContext ctx) =>
        {
            var body = await FleetOperatorApi.ReadBodyAsync(ctx);
            if (!FleetOperatorApi.CheckOperator(ctx, state, body, out var error)) return FleetOperatorApi.Denied(error);
            if (body.Length == 0) return Results.Json(new ErrorResponse { Error = "Empty package." }, EsoJson.Options, statusCode: 400);
            var info = await state.Packages.SaveAsync(new MemoryStream(body), "operator", ctx.RequestAborted);
            state.Audit.Write("fleet/rollout", $"update package uploaded (operator) sha256={info.Sha256} size={info.Size}", "ok");
            state.Activity.Add("operator", "rollout", $"Update package uploaded (sha256 {info.Sha256[..12]}…, {info.Size} bytes)", "ok");
            return Results.Json(info, EsoJson.Options);
        });

        // The agent's download: authorized by the per-rollout token alone (the
        // agent holds no operator credential; the token is minted per rollout
        // and the update-fetch the agent acts on is itself signed).
        app.MapGet("/fleet/update/download/{rolloutId}/{code}", (HttpContext ctx, string rolloutId, string code) =>
        {
            var record = state.Rollouts.Get(rolloutId);
            var token = ctx.Request.Query["t"].ToString();
            if (record == null || string.IsNullOrEmpty(token) ||
                !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(record.Token)))
                return Results.Json(new ErrorResponse { Error = "Unknown rollout or bad token." }, EsoJson.Options, statusCode: 404);
            if (!record.Dto.Devices.Any(d => d.Code.Equals(code, StringComparison.OrdinalIgnoreCase)))
                return Results.Json(new ErrorResponse { Error = "Device is not a target of this rollout." }, EsoJson.Options, statusCode: 404);
            if (!File.Exists(state.Packages.PackagePath))
                return Results.Json(new ErrorResponse { Error = "Package file missing." }, EsoJson.Options, statusCode: 410);
            return Results.File(state.Packages.PackagePath, "application/zip", "esoagent-update.zip");
        });

        app.MapPost("/fleet/rollout", async (HttpContext ctx) =>
        {
            var body = await FleetOperatorApi.ReadBodyAsync(ctx);
            if (!FleetOperatorApi.CheckOperator(ctx, state, body, out var error)) return FleetOperatorApi.Denied(error);
            var req = JsonSerializer.Deserialize<RolloutCreateRequest>(body, EsoJson.Options);
            if (req == null) return Results.Json(new ErrorResponse { Error = "Bad rollout request." }, EsoJson.Options, statusCode: 400);
            if (state.Packages.Current == null)
                return Results.Json(new ErrorResponse { Error = "No update package has been uploaded yet (PUT /fleet/update/package first)." }, EsoJson.Options, statusCode: 409);
            var dto = CreateRollout(state, req.Targets, "operator");
            return Results.Json(new RolloutCreatedResponse { Rollout = dto, Token = state.Rollouts.Get(dto.Id)!.Token }, EsoJson.Options);
        });

        app.MapGet("/fleet/rollout", async (HttpContext ctx) =>
        {
            var body = await FleetOperatorApi.ReadBodyAsync(ctx);
            if (!FleetOperatorApi.CheckOperator(ctx, state, body, out var error)) return FleetOperatorApi.Denied(error);
            return Results.Json(state.Rollouts.List(), EsoJson.Options);
        });

        app.MapGet("/fleet/rollout/{id}", async (HttpContext ctx, string id) =>
        {
            var body = await FleetOperatorApi.ReadBodyAsync(ctx);
            if (!FleetOperatorApi.CheckOperator(ctx, state, body, out var error)) return FleetOperatorApi.Denied(error);
            var record = state.Rollouts.Get(id);
            return record == null
                ? Results.Json(new ErrorResponse { Error = "Rollout not found." }, EsoJson.Options, statusCode: 404)
                : Results.Json(record.Dto, EsoJson.Options);
        });

        app.MapPost("/fleet/rollout/{id}/device", async (HttpContext ctx, string id) =>
        {
            var body = await FleetOperatorApi.ReadBodyAsync(ctx);
            if (!FleetOperatorApi.CheckOperator(ctx, state, body, out var error)) return FleetOperatorApi.Denied(error);
            var req = JsonSerializer.Deserialize<RolloutDeviceStateRequest>(body, EsoJson.Options);
            if (req == null || !state.Rollouts.SetDeviceState(id, req.Code, req.State, req.Error))
                return Results.Json(new ErrorResponse { Error = "Rollout/device not found, or the state transition is not allowed." }, EsoJson.Options, statusCode: 400);
            return Results.Json(state.Rollouts.Get(id)!.Dto, EsoJson.Options);
        });

        // ----- Wake-on-LAN -----
        app.MapPost("/fleet/wake", async (HttpContext ctx) =>
        {
            var body = await FleetOperatorApi.ReadBodyAsync(ctx);
            if (!FleetOperatorApi.CheckOperator(ctx, state, body, out var error)) return FleetOperatorApi.Denied(error);
            var req = JsonSerializer.Deserialize<WakeCodeRequest>(body, EsoJson.Options);
            if (req == null) return Results.Json(new ErrorResponse { Error = "Bad wake request." }, EsoJson.Options, statusCode: 400);
            var results = await WakeAsync(state, req.Code, "operator");
            return results == null
                ? Results.Json(new ErrorResponse { Error = "Device not found." }, EsoJson.Options, statusCode: 404)
                : Results.Json(results, EsoJson.Options);
        });

        // ----- mesh exit node -----
        app.MapPost("/fleet/admin/exit-node", async (HttpContext ctx) =>
        {
            var body = await FleetOperatorApi.ReadBodyAsync(ctx);
            if (!FleetOperatorApi.CheckOperator(ctx, state, body, out var error)) return FleetOperatorApi.Denied(error);
            var req = JsonSerializer.Deserialize<ExitNodeRequest>(body, EsoJson.Options);
            var code = string.IsNullOrWhiteSpace(req?.Code) ? null : req.Code;
            if (code != null && state.Registry.Find(code) == null)
                return Results.Json(new ErrorResponse { Error = "Device not found." }, EsoJson.Options, statusCode: 404);
            state.Registry.SetExitNode(code);
            await FleetMesh.PushToAllAsync(state);
            state.Audit.Write("fleet/mesh", $"exit node = {code ?? "(none)"} (operator)", "ok");
            state.Activity.Add("operator", "mesh", code == null ? "Mesh exit node cleared" : $"Mesh exit node set to {code}", "ok", code);
            return Results.Json(new { exitNode = code }, EsoJson.Options);
        });

        // ----- relayed streams (desktop / TCP tunnel / UDP tunnel) -----
        app.MapGet("/fleet/desktop", async (HttpContext ctx) => { await StreamEndpointAsync(ctx, state, "desktop", StreamRelay.Flavor.Text); });
        app.MapGet("/fleet/tunnel", async (HttpContext ctx) => { await StreamEndpointAsync(ctx, state, "tunnel", StreamRelay.Flavor.Binary); });
        app.MapGet("/fleet/tunnel-udp", async (HttpContext ctx) => { await StreamEndpointAsync(ctx, state, "tunnel-udp", StreamRelay.Flavor.Datagram); });
    }

    internal static ActivityQuery ParseQuery(HttpContext ctx)
    {
        var q = ctx.Request.Query;
        var query = new ActivityQuery
        {
            Device = NullIfEmpty(q["device"].ToString()),
            Kind = NullIfEmpty(q["kind"].ToString()),
            Source = NullIfEmpty(q["source"].ToString()),
            Limit = int.TryParse(q["limit"], out var limit) ? Math.Clamp(limit, 1, 5000) : 200,
        };
        if (DateTimeOffset.TryParse(q["since"], out var since)) query.Since = since;
        if (DateTimeOffset.TryParse(q["until"], out var until)) query.Until = until;
        return query;
    }

    private static string? NullIfEmpty(string v) => string.IsNullOrWhiteSpace(v) ? null : v;

    /// <summary>Creates a rollout record for the current package. Online targets
    /// start Queued (the caller dispatches the signed update-fetch per device —
    /// operator path — or the panel dispatches with its own key); offline
    /// targets are marked Offline immediately and are never surprise-updated.</summary>
    internal static RolloutDto CreateRollout(FleetServerState state, FleetTargets targets, string createdBy)
    {
        var package = state.Packages.Current
            ?? throw new InvalidOperationException("No update package has been uploaded.");
        var all = state.Registry.All();
        var chosen = new List<DeviceRecord>();
        if (targets.All) chosen.AddRange(all.Where(d => d.State == "Approved"));
        if (!string.IsNullOrWhiteSpace(targets.Group))
            chosen.AddRange(all.Where(d => d.State == "Approved" && d.Groups.Contains(targets.Group, StringComparer.OrdinalIgnoreCase)));
        foreach (var code in targets.Codes)
        {
            var d = all.FirstOrDefault(x => x.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
            if (d != null && d.State == "Approved" && !chosen.Contains(d)) chosen.Add(d);
        }
        var record = state.Rollouts.Create(package.Sha256, createdBy, chosen.Select(d => (d.Code, d.Version)));
        foreach (var d in chosen)
        {
            if (!state.Connections.ContainsKey(d.Code))
                state.Rollouts.SetDeviceState(record.Dto.Id, d.Code, RolloutStates.Offline, "Device was offline when the rollout started.");
        }
        state.Audit.Write("fleet/rollout", $"rollout {record.Dto.Id} created ({createdBy}) -> [{string.Join(",", chosen.Select(d => d.Code))}]", "ok");
        state.Activity.Add(createdBy == "operator" ? "operator" : "owner", "rollout",
            $"Rollout {record.Dto.Id} started for [{string.Join(",", chosen.Select(d => d.Code))}]", "ok");
        return state.Rollouts.Get(record.Dto.Id)!.Dto;
    }

    /// <summary>Server-side rollout dispatch for the OWNER PANEL path: the panel
    /// key signs both the relayed command and the update-fetch, and the agent
    /// verifies both against the panel key it learned from its state frames.</summary>
    internal static async Task DispatchPanelRolloutAsync(FleetServerState state, RolloutDto dto)
    {
        var record = state.Rollouts.Get(dto.Id)!;
        foreach (var device in dto.Devices.Where(d => d.State == RolloutStates.Queued))
        {
            var path = $"/fleet/update/download/{dto.Id}/{device.Code}";
            var signature = OperatorSigning.SignUpdateFetch(state.PanelKey, path, dto.PackageSha256);
            var fetch = new UpdateFetchRequest
            {
                Path = path, Token = record.Token, Sha256 = dto.PackageSha256,
                Signer = "owner-panel", Signature = signature,
            };
            var body = JsonSerializer.SerializeToUtf8Bytes(fetch, EsoJson.Options);
            state.Rollouts.SetDeviceState(dto.Id, device.Code, RolloutStates.Sent);
            var result = await Relay.PanelCommandAsync(state, device.Code, "POST", "/api/update/fetch", body, TimeSpan.FromSeconds(120));
            if (result.Error == null && result.HttpStatus is >= 200 and < 300)
                state.Rollouts.SetDeviceState(dto.Id, device.Code, RolloutStates.Applying);
            else
                state.Rollouts.SetDeviceState(dto.Id, device.Code, RolloutStates.Failed,
                    result.Error ?? $"device answered http {result.HttpStatus}");
        }
    }

    internal static async Task<List<WakeResultDto>?> WakeAsync(FleetServerState state, string codeOrName, string actor)
    {
        var target = state.Registry.All().FirstOrDefault(d =>
            d.Code.Equals(codeOrName, StringComparison.OrdinalIgnoreCase) ||
            d.Name.Equals(codeOrName, StringComparison.OrdinalIgnoreCase));
        if (target == null) return null;
        var results = new List<WakeResultDto>();
        foreach (var mac in target.Macs.Count > 0 ? target.Macs : new List<string>())
        {
            var result = new WakeResultDto { Code = target.Code, Mac = MagicPacket.Normalize(mac) };
            try
            {
                var via = await WakeSender.SendAsync(mac);
                foreach (var v in via) result.SentBy.Add($"fleet-server ({v})");
            }
            catch (Exception ex) { result.Errors.Add($"fleet-server: {ex.Message}"); }
            // Every OTHER online device on the target's LANs takes a shot too.
            foreach (var other in state.Registry.All().Where(d => d.State == "Approved" && d.Code != target.Code))
            {
                if (!state.Connections.ContainsKey(other.Code)) continue;
                var body = JsonSerializer.SerializeToUtf8Bytes(new { mac }, EsoJson.Options);
                var relayed = await Relay.PanelCommandAsync(state, other.Code, "POST", "/api/wake", body, TimeSpan.FromSeconds(20));
                if (relayed.Error == null && relayed.HttpStatus == 200)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(Convert.FromBase64String(relayed.BodyBase64));
                        if (doc.RootElement.TryGetProperty("sentVia", out var sentVia))
                            foreach (var v in sentVia.EnumerateArray()) result.SentBy.Add($"{other.Name} ({v.GetString()})");
                    }
                    catch { result.SentBy.Add(other.Name); }
                }
            }
            results.Add(result);
        }
        state.Audit.Write("fleet/wake", $"wake {target.Code} macs=[{string.Join(",", target.Macs)}] ({actor})",
            string.Join("; ", results.Select(r => $"{r.Mac}: sent by [{string.Join(",", r.SentBy)}] errors [{string.Join(",", r.Errors)}]")));
        state.Activity.Add(actor == "operator" ? "operator" : "owner", "wake",
            $"Wake-on-LAN sent for {target.Name} ({target.Code})",
            results.Count == 0 ? "no MAC addresses recorded for this device" : $"{results.Sum(r => r.SentBy.Count)} send(s)", target.Code, target.Name);
        return results;
    }

    private static async Task StreamEndpointAsync(HttpContext ctx, FleetServerState state, string expectedKind, StreamRelay.Flavor flavor)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
        var body = await FleetOperatorApi.ReadBodyAsync(ctx);
        if (!FleetOperatorApi.CheckOperator(ctx, state, body, out _)) { ctx.Response.StatusCode = 401; return; }
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        var first = await StreamRelay.ReceiveTextAsync(ws, ctx.RequestAborted);
        if (first == null) return;
        FleetStreamOpenRequest? req;
        try { req = JsonSerializer.Deserialize<FleetStreamOpenRequest>(first, EsoJson.Options); }
        catch { return; }
        if (req == null || req.Open.Kind != expectedKind) return;
        var open = req.Open;
        if (open.Signer != "operator" || state.OperatorPublicKey == null ||
            !OperatorSigning.VerifyStream(state.OperatorPublicKey, open.Signature, open.StreamId, open.IssuedAt, open.Kind, open.PayloadJson, DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
        {
            // Tell the caller plainly, then close: a bad stream signature never reaches a device.
            var denied = JsonSerializer.SerializeToUtf8Bytes(new FleetStreamOpenedFrame { StreamId = open.StreamId, Ok = false, Error = "Stream signature invalid." }, EsoJson.Options);
            try { await ws.SendAsync(denied, WebSocketMessageType.Text, true, ctx.RequestAborted); } catch { }
            return;
        }
        var record = state.Registry.Find(req.Code);
        if (record == null || record.State != "Approved") return;
        if (!state.Connections.TryGetValue(record.Code, out var conn)) return;
        state.Audit.Write("fleet/stream", $"{open.Kind} stream {open.StreamId} -> {record.Code} (operator)", "ok");
        state.Activity.Add("operator", "stream", $"{open.Kind} stream opened to {record.Name}", "ok", record.Code, record.Name);
        // Live tunnel mirror for the panel (Phase 4): relayed tunnels only —
        // a tunnel opened directly to a device never passes through here.
        var isTunnel = expectedKind is "tunnel" or "tunnel-udp";
        if (isTunnel)
        {
            var target = "";
            try { target = JsonSerializer.Deserialize<TunnelTarget>(open.PayloadJson, EsoJson.Options)?.ToString() ?? ""; }
            catch { /* the target stays blank rather than blocking the stream */ }
            state.Tunnels.Open(new ActiveTunnelDto
            {
                StreamId = open.StreamId,
                Code = record.Code,
                DeviceName = record.Name,
                Protocol = expectedKind == "tunnel-udp" ? "udp" : "tcp",
                Target = target,
                Signer = open.Signer,
                OpenedAt = DateTimeOffset.UtcNow,
            });
        }
        try
        {
            await StreamRelay.BridgeAsync(state, ws, conn, open, flavor, forwardOpened: true, ctx.RequestAborted);
        }
        finally
        {
            if (isTunnel) state.Tunnels.Close(open.StreamId);
        }
    }
}
