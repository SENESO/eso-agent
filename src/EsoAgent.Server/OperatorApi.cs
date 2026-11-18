using System.Text;
using System.Text.Json;
using EsoAgent.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EsoAgent.Server;

public sealed class CodeRequest { public string Code { get; set; } = ""; }
public sealed class RenameRequest { public string Code { get; set; } = ""; public string Name { get; set; } = ""; }
public sealed class GroupsRequest { public string Code { get; set; } = ""; public List<string> Groups { get; set; } = new(); }
public sealed class SubnetsRequest { public string Code { get; set; } = ""; public List<string> Approved { get; set; } = new(); }
public sealed class AclRequest { public List<AclRule> Rules { get; set; } = new(); }
public sealed class EnrollmentKeyCreateRequest { public int? Uses { get; set; } public string Note { get; set; } = ""; }
public sealed class IdRequest { public string Id { get; set; } = ""; }

/// <summary>Relays signed command/admin frames to a connected device and awaits the result.</summary>
public static class Relay
{
    public static async Task<FleetResultFrame> PanelCommandAsync(FleetServerState state, string code, string method, string path, byte[] body, TimeSpan timeout)
    {
        if (!state.Connections.TryGetValue(code, out var conn))
            return new FleetResultFrame { CommandId = "", HttpStatus = 0, Error = "Device is offline." };
        var commandId = Guid.NewGuid().ToString("N");
        var issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = OperatorSigning.SignCommand(state.PanelKey, commandId, issuedAt, method, path, body);
        var frame = new FleetCommandFrame
        {
            CommandId = commandId, IssuedAt = issuedAt, Signer = "owner-panel", Signature = signature,
            Method = method, Path = path, BodyBase64 = Convert.ToBase64String(body),
        };
        return await conn.SendAndWaitAsync(frame, commandId, timeout);
    }

    public static async Task<FleetResultFrame> PanelAdminAsync(FleetServerState state, string code, string action, string payloadJson, TimeSpan timeout)
    {
        if (!state.Connections.TryGetValue(code, out var conn))
            return new FleetResultFrame { CommandId = "", HttpStatus = 0, Error = "Device is offline." };
        var commandId = Guid.NewGuid().ToString("N");
        var issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = OperatorSigning.SignAdmin(state.PanelKey, action, commandId, issuedAt, payloadJson);
        var frame = new FleetAdminFrame
        {
            Action = action, CommandId = commandId, IssuedAt = issuedAt,
            Signer = "owner-panel", Signature = signature, PayloadJson = payloadJson,
        };
        return await conn.SendAndWaitAsync(frame, commandId, timeout);
    }
}

/// <summary>
/// The OPERATOR API: every call is authenticated by the operator key's ECDSA
/// signature (headers X-Eso-Operator-Timestamp / X-Eso-Operator-Signature).
/// Operator tier = full fleet control INCLUDING operational management
/// (approve/reject/rename/groups/rotate-device-token). Owner-reserved actions
/// are refused here by design — they exist only in the owner panel.
/// </summary>
public static class FleetOperatorApi
{
    public static void MapFleetOperatorApi(this IEndpointRouteBuilder app, FleetServerState state)
    {
        app.MapGet("/fleet/devices", async (HttpContext ctx) =>
        {
            var body = await ReadBodyAsync(ctx);
            if (!CheckOperator(ctx, state, body, out var error)) return Denied(error);
            return Results.Json(ListDevices(state), EsoJson.Options);
        });

        app.MapPost("/fleet/command", async (HttpContext ctx) =>
        {
            var body = await ReadBodyAsync(ctx);
            if (!CheckOperator(ctx, state, body, out var error)) return Denied(error);
            var req = JsonSerializer.Deserialize<FleetCommandRequest>(body, EsoJson.Options);
            if (req == null) return Results.Json(new ErrorResponse { Error = "Bad command request." }, EsoJson.Options, statusCode: 400);
            return Results.Json(await ExecuteOperatorCommandAsync(state, req), EsoJson.Options);
        });

        // ----- offline command queue (v1.1): receipts + results -----
        app.MapGet("/fleet/queue", async (HttpContext ctx) =>
        {
            var body = await ReadBodyAsync(ctx);
            if (!CheckOperator(ctx, state, body, out var error)) return Denied(error);
            var device = ctx.Request.Query["device"].ToString();
            return Results.Json(state.Queue.List(string.IsNullOrWhiteSpace(device) ? null : device), EsoJson.Options);
        });

        app.MapGet("/fleet/queue/{id}", async (HttpContext ctx, string id) =>
        {
            var body = await ReadBodyAsync(ctx);
            if (!CheckOperator(ctx, state, body, out var error)) return Denied(error);
            var record = state.Queue.Get(id);
            return record == null
                ? Results.Json(new ErrorResponse { Error = "Queued command not found." }, EsoJson.Options, statusCode: 404)
                : Results.Json(record, EsoJson.Options);
        });

        // ----- mesh coordination (update 8): all operational, so operator-tier (update 9) -----
        app.MapGet("/fleet/mesh", async (HttpContext ctx) =>
        {
            var body = await ReadBodyAsync(ctx);
            if (!CheckOperator(ctx, state, body, out var error)) return Denied(error);
            return Results.Json(BuildMeshState(state), EsoJson.Options);
        });

        app.MapGet("/fleet/resolve", async (HttpContext ctx) =>
        {
            var body = await ReadBodyAsync(ctx);
            if (!CheckOperator(ctx, state, body, out var error)) return Denied(error);
            var name = ctx.Request.Query["name"].ToString();
            var device = state.Registry.All().FirstOrDefault(d =>
                d.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || d.Code.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (device == null) return Results.Json(new ErrorResponse { Error = $"No device named '{name}'." }, EsoJson.Options, statusCode: 404);
            return Results.Json(new { name = device.Name, code = device.Code, virtualIp = device.VirtualIp, ips = device.Ips, online = state.Connections.ContainsKey(device.Code) }, EsoJson.Options);
        });

        app.MapPost("/fleet/admin/enrollment-keys", async (HttpContext ctx) =>
        {
            var body = await ReadBodyAsync(ctx);
            if (!CheckOperator(ctx, state, body, out var error)) return Denied(error);
            var req = JsonSerializer.Deserialize<EnrollmentKeyCreateRequest>(body, EsoJson.Options) ?? new();
            var key = EnrollmentKeys.Generate();
            var record = new EnrollmentKeyRecord
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                KeyHash = EnrollmentKeys.Hash(key),
                UsesLeft = req.Uses,
                CreatedUtc = DateTimeOffset.UtcNow.ToString("O"),
                CreatedBy = "operator",
                Note = req.Note,
            };
            state.Registry.AddEnrollmentKey(record);
            state.Audit.Write("fleet/mesh", $"enrollment key created (operator) id={record.Id} uses={(req.Uses?.ToString() ?? "unlimited")}", "ok");
            // The plaintext key is returned ONCE, here — only its hash is stored.
            return Results.Json(new { id = record.Id, key, usesLeft = record.UsesLeft }, EsoJson.Options);
        });

        app.MapPost("/fleet/admin/enrollment-keys/revoke", async (HttpContext ctx) =>
        {
            var body = await ReadBodyAsync(ctx);
            if (!CheckOperator(ctx, state, body, out var error)) return Denied(error);
            var req = JsonSerializer.Deserialize<IdRequest>(body, EsoJson.Options);
            if (req == null || !state.Registry.RevokeEnrollmentKey(req.Id))
                return Results.Json(new ErrorResponse { Error = "Enrollment key not found." }, EsoJson.Options, statusCode: 404);
            state.Audit.Write("fleet/mesh", $"enrollment key revoked (operator) id={req.Id}", "ok");
            return Results.Json(new { revoked = req.Id }, EsoJson.Options);
        });

        app.MapPost("/fleet/admin/subnets", async (HttpContext ctx) =>
        {
            var body = await ReadBodyAsync(ctx);
            if (!CheckOperator(ctx, state, body, out var error)) return Denied(error);
            var req = JsonSerializer.Deserialize<SubnetsRequest>(body, EsoJson.Options);
            if (req == null || !state.Registry.SetApprovedSubnets(req.Code, req.Approved))
                return Results.Json(new ErrorResponse { Error = "Device not found." }, EsoJson.Options, statusCode: 404);
            state.Audit.Write("fleet/mesh", $"approved subnets {req.Code} = [{string.Join(",", req.Approved)}] (operator)", "ok");
            await FleetMesh.PushToAllAsync(state);
            return Results.Json(new { code = req.Code, approvedSubnets = req.Approved }, EsoJson.Options);
        });

        app.MapPost("/fleet/admin/acl", async (HttpContext ctx) =>
        {
            var body = await ReadBodyAsync(ctx);
            if (!CheckOperator(ctx, state, body, out var error)) return Denied(error);
            var req = JsonSerializer.Deserialize<AclRequest>(body, EsoJson.Options);
            if (req == null) return Results.Json(new ErrorResponse { Error = "Bad ACL request." }, EsoJson.Options, statusCode: 400);
            state.Registry.SetAclRules(req.Rules);
            state.Audit.Write("fleet/mesh", $"ACL rules replaced (operator): {req.Rules.Count} rule(s)", "ok");
            await FleetMesh.PushToAllAsync(state);
            return Results.Json(new { rules = req.Rules.Count }, EsoJson.Options);
        });

        // NOTE: statement-bodied lambdas with an explicit return are required here —
        // an expression-bodied `=> await Helper(...)` registration silently drops the
        // helper's IResult on this SDK (verified by repro + smoke test: 200/empty responses).
        app.MapPost("/fleet/admin/approve", async (HttpContext ctx) => { return await RegistryActionAsync(ctx, state, "approve"); });
        app.MapPost("/fleet/admin/reject", async (HttpContext ctx) => { return await RegistryActionAsync(ctx, state, "reject"); });
        app.MapPost("/fleet/admin/rename", async (HttpContext ctx) => { return await RegistryActionAsync(ctx, state, "rename"); });
        app.MapPost("/fleet/admin/groups", async (HttpContext ctx) => { return await RegistryActionAsync(ctx, state, "groups"); });

        app.MapPost("/fleet/admin", async (HttpContext ctx) =>
        {
            var body = await ReadBodyAsync(ctx);
            if (!CheckOperator(ctx, state, body, out var error)) return Denied(error);
            var req = JsonSerializer.Deserialize<FleetAdminRelayRequest>(body, EsoJson.Options);
            if (req == null) return Results.Json(new ErrorResponse { Error = "Bad admin request." }, EsoJson.Options, statusCode: 400);

            // Owner-reserved actions are refused on the operator path, explicitly.
            if (req.Action is "revoke-operator" or "set-operator-key")
            {
                state.Audit.Write("fleet/admin", $"operator attempted owner-reserved action '{req.Action}'", "403");
                return Results.Json(new ErrorResponse { Error = $"'{req.Action}' is owner-reserved and only available in the owner panel." }, EsoJson.Options, statusCode: 403);
            }
            if (req.Action != "rotate-token")
                return Results.Json(new ErrorResponse { Error = $"Action '{req.Action}' is not available on the operator admin path." }, EsoJson.Options, statusCode: 400);

            var record = state.Registry.Find(req.Code);
            if (record == null) return Results.Json(new ErrorResponse { Error = "Device not found." }, EsoJson.Options, statusCode: 404);
            if (record.State != "Approved") return Results.Json(new ErrorResponse { Error = $"Device is {record.State}, not Approved." }, EsoJson.Options, statusCode: 409);
            if (!OperatorSigning.VerifyAdmin(state.OperatorPublicKey!, req.Signature, req.Action, req.CommandId, req.IssuedAt, req.PayloadJson, DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
                return Denied("Admin signature invalid.");

            if (!state.Connections.TryGetValue(record.Code, out var conn))
                return Results.Json(new FleetResultFrame { CommandId = req.CommandId, HttpStatus = 0, Error = "Device is offline." }, EsoJson.Options);
            var frame = new FleetAdminFrame
            {
                Action = req.Action, CommandId = req.CommandId, IssuedAt = req.IssuedAt,
                Signer = "operator", Signature = req.Signature, PayloadJson = req.PayloadJson,
            };
            var result = await conn.SendAndWaitAsync(frame, req.CommandId, TimeSpan.FromSeconds(60), ctx.RequestAborted);
            state.Audit.Write("fleet/admin", $"{req.Action} -> {record.Code} (operator)", result.Error ?? $"http {result.HttpStatus}");
            return Results.Json(result, EsoJson.Options);
        });
    }

    public static List<FleetDeviceDto> ListDevices(FleetServerState state)
        => state.Registry.All()
            .Select(d => new FleetDeviceDto
            {
                Code = d.Code, Name = d.Name, Os = d.Os, Version = d.Version, Ips = d.Ips,
                State = d.State, Groups = d.Groups,
                Online = state.Connections.ContainsKey(d.Code),
                FirstSeen = d.FirstSeen, LastSeen = d.LastSeen,
                VirtualIp = d.VirtualIp, WgPublicKey = d.WgPublicKey,
                AdvertisedSubnets = d.AdvertisedSubnets, ApprovedSubnets = d.ApprovedSubnets,
                Macs = d.Macs, ExitNode = d.ExitNode,
            })
            .OrderByDescending(d => d.Online).ThenBy(d => d.Code)
            .ToList();

    public static MeshStateDto BuildMeshState(FleetServerState state)
        => new()
        {
            Devices = ListDevices(state),
            AclRules = state.Registry.AclRules().ToList(),
            EnrollmentKeys = state.Registry.EnrollmentKeys().Select(k => new EnrollmentKeyView
            {
                Id = k.Id, UsesLeft = k.UsesLeft, CreatedUtc = k.CreatedUtc, CreatedBy = k.CreatedBy, Note = k.Note,
            }).ToList(),
            RelayEndpoint = state.Registry.RelayEndpoint,
        };

    private static async Task<IResult> RegistryActionAsync(HttpContext ctx, FleetServerState state, string action)
    {
        var body = await ReadBodyAsync(ctx);
        if (!CheckOperator(ctx, state, body, out var error)) return Denied(error);
        switch (action)
        {
            case "approve":
            case "reject":
            {
                var req = JsonSerializer.Deserialize<CodeRequest>(body, EsoJson.Options);
                if (req == null || !state.Registry.SetState(req.Code, action == "approve" ? "Approved" : "Rejected"))
                    return Results.Json(new ErrorResponse { Error = "Device not found." }, EsoJson.Options, statusCode: 404);
                var record = state.Registry.Find(req.Code)!;
                if (action == "approve")
                {
                    await FleetConnectEndpoint.OnApprovedAsync(state, record.Code);
                }
                else
                {
                    if (state.Connections.TryGetValue(record.Code, out var conn))
                        await FleetConnectEndpoint.PushStateAsync(state, conn, record);
                    await FleetMesh.PushToAllAsync(state); // peer set changed
                }
                state.Audit.Write("fleet/admin", $"{action} {record.Code} (operator)", "ok");
                state.Activity.Add("operator", action, $"{action} {record.Code} ({record.Name})", "ok", record.Code, record.Name);
                return Results.Json(new { code = record.Code, state = record.State }, EsoJson.Options);
            }
            case "rename":
            {
                var req = JsonSerializer.Deserialize<RenameRequest>(body, EsoJson.Options);
                if (req == null || !state.Registry.Rename(req.Code, req.Name))
                    return Results.Json(new ErrorResponse { Error = "Device not found." }, EsoJson.Options, statusCode: 404);
                state.Audit.Write("fleet/admin", $"rename {req.Code} -> '{req.Name}' (operator)", "ok");
                state.Activity.Add("operator", "rename", $"renamed {req.Code} to '{req.Name}'", "ok", req.Code, req.Name);
                await FleetMesh.PushToAllAsync(state); // hosts map (name → virtual IP) changed
                return Results.Json(new { code = req.Code, name = req.Name }, EsoJson.Options);
            }
            case "groups":
            {
                var req = JsonSerializer.Deserialize<GroupsRequest>(body, EsoJson.Options);
                if (req == null || !state.Registry.SetGroups(req.Code, req.Groups))
                    return Results.Json(new ErrorResponse { Error = "Device not found." }, EsoJson.Options, statusCode: 404);
                state.Audit.Write("fleet/admin", $"groups {req.Code} = [{string.Join(",", req.Groups)}] (operator)", "ok");
                state.Activity.Add("operator", "groups", $"groups {req.Code} = [{string.Join(",", req.Groups)}]", "ok", req.Code);
                await FleetMesh.PushToAllAsync(state); // ACL group expansion changed
                return Results.Json(new { code = req.Code, groups = req.Groups }, EsoJson.Options);
            }
            default:
                return Results.Json(new ErrorResponse { Error = "Unknown action." }, EsoJson.Options, statusCode: 400);
        }
    }

    private static async Task<FleetCommandResponse> ExecuteOperatorCommandAsync(FleetServerState state, FleetCommandRequest req)
    {
        var response = new FleetCommandResponse { CommandId = req.CommandId };
        byte[] cmdBody;
        try { cmdBody = Convert.FromBase64String(req.BodyBase64); }
        catch { cmdBody = Array.Empty<byte>(); }

        // The operator signed the command itself; the server verifies before relaying,
        // and each device verifies the same signature again before executing.
        // Queueable commands (ExpiresAt set) verify against the queued canonical form.
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signatureOk = req.ExpiresAt is { } expiresAt
            ? OperatorSigning.VerifyQueuedCommand(state.OperatorPublicKey!, req.Signature, req.CommandId, req.IssuedAt, expiresAt, req.Method, req.Path, cmdBody, now)
            : OperatorSigning.VerifyCommand(state.OperatorPublicKey!, req.Signature, req.CommandId, req.IssuedAt, req.Method, req.Path, cmdBody, now);
        if (!signatureOk)
        {
            foreach (var code in req.Targets.Codes)
                response.Results.Add(new FleetDeviceResult { Code = code, Ok = false, Error = "Command signature invalid." });
            if (response.Results.Count == 0)
                response.Results.Add(new FleetDeviceResult { Code = "*", Ok = false, Error = "Command signature invalid." });
            return response;
        }

        var targets = ResolveTargets(state, req.Targets, response);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(req.TimeoutSeconds, 5, 600));
        var queueable = req.QueueIfOffline && req.ExpiresAt != null && (req.Path is "/api/exec" or "/api/shell");
        var sends = targets.Select(async record =>
        {
            if (!state.Connections.TryGetValue(record.Code, out var conn))
            {
                // Offline queue (v1.1): persist the signed envelope for delivery
                // on reconnect and hand the caller a fetchable receipt.
                if (queueable)
                {
                    var queued = state.Queue.Enqueue(new QueuedCommandRecord
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        CommandId = req.CommandId,
                        DeviceCode = record.Code,
                        Method = req.Method,
                        Path = req.Path,
                        BodyBase64 = req.BodyBase64,
                        Signature = req.Signature,
                        Signer = "operator",
                        IssuedAt = req.IssuedAt,
                        ExpiresAt = req.ExpiresAt!.Value,
                        TimeoutSeconds = Math.Clamp(req.TimeoutSeconds, 5, 1800),
                        QueuedAt = DateTimeOffset.UtcNow,
                    });
                    return new FleetDeviceResult
                    {
                        Code = record.Code,
                        Ok = false,
                        Queued = true,
                        QueueId = queued.Id,
                        Error = $"Device is offline — command queued for delivery on reconnect (queue id {queued.Id}).",
                    };
                }
                return new FleetDeviceResult { Code = record.Code, Ok = false, Error = "Device is offline." };
            }
            var frame = new FleetCommandFrame
            {
                CommandId = req.CommandId, IssuedAt = req.IssuedAt, Signer = "operator", Signature = req.Signature,
                Method = req.Method, Path = req.Path, BodyBase64 = req.BodyBase64,
                ExpiresAt = req.ExpiresAt,
            };
            var result = await conn.SendAndWaitAsync(frame, req.CommandId, timeout);
            return new FleetDeviceResult
            {
                Code = record.Code,
                Ok = result.Error == null && result.HttpStatus is >= 200 and < 300,
                HttpStatus = result.HttpStatus == 0 ? null : result.HttpStatus,
                BodyBase64 = result.BodyBase64,
                Error = result.Error,
            };
        });
        response.Results.AddRange(await Task.WhenAll(sends));
        var okCount = response.Results.Count(r => r.Ok);
        var queuedCount = response.Results.Count(r => r.Queued);
        state.Audit.Write("fleet/command",
            $"{req.Method} {req.Path} -> [{string.Join(",", response.Results.Select(r => r.Code))}] (operator)",
            $"{okCount}/{response.Results.Count} ok" + (queuedCount > 0 ? $" · {queuedCount} queued" : ""));
        state.Activity.Add("operator", "command",
            $"{req.Method} {req.Path} -> [{string.Join(",", response.Results.Select(r => r.Code))}]",
            $"{okCount}/{response.Results.Count} ok" + (queuedCount > 0 ? $" · {queuedCount} queued" : ""));
        return response;
    }

    /// <summary>Resolves targets to Approved device records; explicitly-targeted but
    /// unapproved devices get an explicit failure entry (never silently dropped).</summary>
    private static List<DeviceRecord> ResolveTargets(FleetServerState state, FleetTargets targets, FleetCommandResponse response)
    {
        var all = state.Registry.All();
        var result = new List<DeviceRecord>();
        if (targets.All) result.AddRange(all.Where(d => d.State == "Approved"));
        if (!string.IsNullOrWhiteSpace(targets.Group))
            result.AddRange(all.Where(d => d.State == "Approved" && d.Groups.Contains(targets.Group, StringComparer.OrdinalIgnoreCase)));
        foreach (var code in targets.Codes)
        {
            var d = all.FirstOrDefault(x => x.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
            if (d == null) response.Results.Add(new FleetDeviceResult { Code = code, Ok = false, Error = "Unknown device code." });
            else if (d.State != "Approved") response.Results.Add(new FleetDeviceResult { Code = d.Code, Ok = false, Error = $"Device is {d.State}, not Approved — it receives nothing." });
            else if (!result.Contains(d)) result.Add(d);
        }
        return result;
    }

    internal static bool CheckOperator(HttpContext ctx, FleetServerState state, byte[] body, out string error)
    {
        error = "";
        if (!state.Registry.OperatorEnabled) { error = "Operator access has been revoked by the owner."; return false; }
        if (state.OperatorPublicKey == null) { error = "No operator key is configured on this server."; return false; }
        if (!ctx.Request.Headers.TryGetValue("X-Eso-Operator-Timestamp", out var tsRaw) || !long.TryParse(tsRaw.ToString(), out var ts))
        { error = "Missing operator timestamp."; return false; }
        if (!ctx.Request.Headers.TryGetValue("X-Eso-Operator-Signature", out var sig))
        { error = "Missing operator signature."; return false; }
        var pathAndQuery = ctx.Request.Path + ctx.Request.QueryString;
        if (!OperatorSigning.VerifyRequest(state.OperatorPublicKey, sig.ToString(), ctx.Request.Method, pathAndQuery, ts, body, DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
        { error = "Operator signature invalid."; return false; }
        return true;
    }

    internal static async Task<byte[]> ReadBodyAsync(HttpContext ctx)
    {
        ctx.Request.EnableBuffering();
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        ctx.Request.Body.Position = 0;
        return ms.ToArray();
    }

    internal static IResult Denied(string error)
        => Results.Json(new ErrorResponse { Error = error }, EsoJson.Options,
            statusCode: error.Contains("revoked") ? 403 : 401);
}
