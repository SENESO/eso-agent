using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using EsoAgent.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace EsoAgent.Server;

/// <summary>Server-side store for access codes + grants (item 41), persisted
/// as access.json. Codes are stored hashed; grants are the live truth that
/// every grant use is checked against.</summary>
public sealed class AccessStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private List<AccessCodeRecord> _codes = new();
    private List<GrantRecord> _grants = new();

    private sealed class AccessFile
    {
        public List<AccessCodeRecord> Codes { get; set; } = new();
        public List<GrantRecord> Grants { get; set; } = new();
    }

    public AccessStore(string dataDir)
    {
        _path = Path.Combine(dataDir, "access.json");
        try
        {
            if (File.Exists(_path))
            {
                var file = JsonSerializer.Deserialize<AccessFile>(File.ReadAllText(_path), EsoJson.Options);
                _codes = file?.Codes ?? new();
                _grants = file?.Grants ?? new();
            }
        }
        catch { _codes = new(); _grants = new(); }
    }

    private void Save()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new AccessFile { Codes = _codes, Grants = _grants }, EsoJson.Options));
        File.Move(tmp, _path, overwrite: true);
    }

    public AccessCodeRecord AddCode(AccessCodeRecord code) { lock (_gate) { _codes.Add(code); Save(); return code; } }
    public AccessCodeRecord? FindCodeByHash(string hash) { lock (_gate) { return _codes.FirstOrDefault(c => c.CodeHash == hash); } }
    public AccessCodeRecord? FindCode(string id) { lock (_gate) { return _codes.FirstOrDefault(c => c.Id == id); } }
    public GrantRecord? FindGrant(string id) { lock (_gate) { return _grants.FirstOrDefault(g => g.Id == id); } }

    public List<AccessCodeDto> Codes(string? device)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            return _codes
                .Where(c => device == null || c.DeviceCode == device)
                .OrderByDescending(c => c.CreatedAt)
                .Select(c => new AccessCodeDto
                {
                    Id = c.Id, DeviceCode = c.DeviceCode, Scope = c.Scope, CreatedBy = c.CreatedBy,
                    CreatedAt = c.CreatedAt, ExpiresAt = c.ExpiresAt, MaxUses = c.MaxUses,
                    Uses = c.Uses, Revoked = c.Revoked, Expired = now >= c.ExpiresAt,
                }).ToList();
        }
    }

    public List<GrantDto> Grants(string? device)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            return _grants
                .Where(g => device == null || g.DeviceCode == device)
                .OrderByDescending(g => g.IssuedAt)
                .Select(g => new GrantDto
                {
                    Id = g.Id, CodeId = g.CodeId, DeviceCode = g.DeviceCode, Scope = g.Scope,
                    IssuedAt = g.IssuedAt, ExpiresAt = g.ExpiresAt, RedeemedFrom = g.RedeemedFrom,
                    Revoked = g.Revoked, Active = !g.Revoked && now < g.ExpiresAt, Actions = g.Actions,
                }).ToList();
        }
    }

    /// <summary>Atomically consumes one use of the code and records the grant.</summary>
    public GrantRecord? Redeem(AccessCodeRecord code, string fromIp)
    {
        lock (_gate)
        {
            var (ok, _) = AccessCodePolicy.CanRedeem(code, DateTimeOffset.UtcNow);
            if (!ok) return null;
            code.Uses++;
            var grant = new GrantRecord
            {
                Id = Guid.NewGuid().ToString("N")[..16],
                CodeId = code.Id, DeviceCode = code.DeviceCode, Scope = code.Scope,
                IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = code.ExpiresAt,
                RedeemedFrom = fromIp,
            };
            _grants.Add(grant);
            Save();
            return grant;
        }
    }

    /// <summary>Revoke a code (blocks redemption; existing grants keep their
    /// own state) or a grant (kills that token immediately). Revoking a code
    /// ALSO revokes its outstanding grants — the owner's intent is "stop
    /// this access", not "stop future access only".</summary>
    public (bool Found, string What) Revoke(string id)
    {
        lock (_gate)
        {
            var code = _codes.FirstOrDefault(c => c.Id == id);
            if (code != null)
            {
                code.Revoked = true;
                foreach (var g in _grants.Where(g => g.CodeId == id)) g.Revoked = true;
                Save();
                return (true, "code");
            }
            var grant = _grants.FirstOrDefault(g => g.Id == id);
            if (grant != null)
            {
                grant.Revoked = true;
                Save();
                return (true, "grant");
            }
            return (false, "");
        }
    }

    public void NoteAction(string grantId)
    {
        lock (_gate)
        {
            var g = _grants.FirstOrDefault(x => x.Id == grantId);
            if (g != null) { g.Actions++; Save(); }
        }
    }
}

/// <summary>Item 41 endpoints: owner/operator management, public redemption,
/// and the grant-authenticated command + desktop channels.</summary>
public static class FleetAccessApi
{
    public static void MapFleetAccess(WebApplication app, FleetServerState state)
    {
        // ----- management (operator-signed; the panel mirrors these) -----
        app.MapPost("/fleet/access/codes", async (HttpContext ctx) =>
        {
            var body = await FleetOperatorApi.ReadBodyAsync(ctx);
            if (!FleetOperatorApi.CheckOperator(ctx, state, body, out var error)) return FleetOperatorApi.Denied(error);
            var req = JsonSerializer.Deserialize<AccessCodeCreateRequest>(body, EsoJson.Options);
            return CreateCode(state, req, "operator");
        });

        app.MapGet("/fleet/access/codes", async (HttpContext ctx) =>
        {
            var body = await FleetOperatorApi.ReadBodyAsync(ctx);
            if (!FleetOperatorApi.CheckOperator(ctx, state, body, out var error)) return FleetOperatorApi.Denied(error);
            var device = ctx.Request.Query["device"].ToString();
            return Results.Json(state.Access.Codes(string.IsNullOrEmpty(device) ? null : device), EsoJson.Options);
        });

        app.MapGet("/fleet/access/grants", async (HttpContext ctx) =>
        {
            var body = await FleetOperatorApi.ReadBodyAsync(ctx);
            if (!FleetOperatorApi.CheckOperator(ctx, state, body, out var error)) return FleetOperatorApi.Denied(error);
            var device = ctx.Request.Query["device"].ToString();
            return Results.Json(state.Access.Grants(string.IsNullOrEmpty(device) ? null : device), EsoJson.Options);
        });

        app.MapPost("/fleet/access/revoke", async (HttpContext ctx) =>
        {
            var body = await FleetOperatorApi.ReadBodyAsync(ctx);
            if (!FleetOperatorApi.CheckOperator(ctx, state, body, out var error)) return FleetOperatorApi.Denied(error);
            var req = JsonSerializer.Deserialize<AccessRevokeRequest>(body, EsoJson.Options);
            return Revoke(state, req, "operator");
        });

        // ----- redemption (the code IS the credential; rate-limited) -----
        app.MapPost("/fleet/access/redeem", async (HttpContext ctx) =>
        {
            var source = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (state.Limiter.IsLockedOut(source, out var remaining))
                return Results.Json(new ErrorResponse { Error = $"Too many attempts. Try again in {Math.Ceiling(remaining.TotalSeconds)}s." }, EsoJson.Options, statusCode: 429);
            var body = await FleetOperatorApi.ReadBodyAsync(ctx);
            var req = JsonSerializer.Deserialize<AccessRedeemRequest>(body, EsoJson.Options);
            var record = req == null ? null : state.Access.FindCodeByHash(AccessCodePolicy.Hash(req.Code));
            if (record == null)
            {
                state.Limiter.RecordFailure(source);
                return Results.Json(new ErrorResponse { Error = "That access code was not recognized." }, EsoJson.Options, statusCode: 404);
            }
            var (can, reason) = AccessCodePolicy.CanRedeem(record, DateTimeOffset.UtcNow);
            if (!can)
            {
                state.Limiter.RecordFailure(source);
                return Results.Json(new ErrorResponse { Error = reason }, EsoJson.Options, statusCode: 403);
            }
            var device = state.Registry.Find(record.DeviceCode);
            if (device == null || device.State != "Approved")
                return Results.Json(new ErrorResponse { Error = "The device for this code is not part of the fleet." }, EsoJson.Options, statusCode: 404);
            var grant = state.Access.Redeem(record, source);
            if (grant == null)
                return Results.Json(new ErrorResponse { Error = "This access code can no longer be redeemed." }, EsoJson.Options, statusCode: 403);
            state.Limiter.RecordSuccess(source);
            var expires = grant.ExpiresAt.ToUnixTimeSeconds();
            var token = new GrantTokenDto
            {
                GrantId = grant.Id, DeviceCode = grant.DeviceCode, Scope = grant.Scope,
                ExpiresAtUnix = expires,
                Signature = GrantSigning.Sign(state.PanelKey, grant.Id, grant.DeviceCode, expires, grant.Scope),
            };
            state.Audit.Write("fleet/access", $"grant {grant.Id} redeemed for {grant.DeviceCode} from {source} (scope {grant.Scope.Describe()})", "ok");
            state.Activity.Add("grant", "redeem", $"Access grant redeemed for {device.Name} (grant {grant.Id[..8]}…, {grant.Scope.Describe()})", "ok", grant.DeviceCode, device.Name);
            return Results.Json(new AccessRedeemResponse { Token = token, DeviceCode = device.Code, DeviceName = device.Name }, EsoJson.Options);
        });

        // ----- grant-authenticated command channel -----
        app.MapPost("/fleet/access/command", async (HttpContext ctx) =>
        {
            var grant = ValidateGrant(ctx, state, out var error);
            if (grant == null) return Results.Json(new ErrorResponse { Error = error }, EsoJson.Options, statusCode: 403);
            var body = await FleetOperatorApi.ReadBodyAsync(ctx);
            FleetCommandRequest? req;
            try { req = JsonSerializer.Deserialize<FleetCommandRequest>(body, EsoJson.Options); } catch { req = null; }
            if (req == null) return Results.Json(new ErrorResponse { Error = "Bad command body." }, EsoJson.Options, statusCode: 400);
            if (!grant.Scope.Allows(req.Path))
            {
                state.Activity.Add("grant", "denied", $"Grant {grant.Id[..8]}… tried {req.Method} {req.Path} — outside its scope", "denied", grant.DeviceCode);
                return Results.Json(new ErrorResponse { Error = $"This grant's scope does not include {req.Path}." }, EsoJson.Options, statusCode: 403);
            }
            byte[] commandBody;
            try { commandBody = Convert.FromBase64String(req.BodyBase64); } catch { commandBody = Array.Empty<byte>(); }
            var result = await Relay.PanelCommandAsync(state, grant.DeviceCode, req.Method, req.Path, commandBody,
                TimeSpan.FromSeconds(Math.Clamp(req.TimeoutSeconds, 5, 900)));
            state.Access.NoteAction(grant.Id);
            var device = state.Registry.Find(grant.DeviceCode);
            state.Activity.Add("grant", "command", $"Grant {grant.Id[..8]}… ran {req.Method} {req.Path} on {device?.Name ?? grant.DeviceCode}", result.Error == null ? "ok" : "error", grant.DeviceCode, device?.Name);
            return Results.Json(result, EsoJson.Options);
        });

        // ----- grant-authenticated desktop (view-only enforced at the relay) -----
        app.MapGet("/fleet/access/desktop", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            var grant = ValidateGrant(ctx, state, out _);
            if (grant == null || !grant.Scope.DesktopControl) { ctx.Response.StatusCode = 403; return; }
            if (!state.Connections.TryGetValue(grant.DeviceCode, out var conn)) { ctx.Response.StatusCode = 404; return; }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var first = await StreamRelay.ReceiveTextAsync(ws, ctx.RequestAborted);
            if (first == null) return;
            FleetStreamOpenRequest? req;
            try { req = JsonSerializer.Deserialize<FleetStreamOpenRequest>(first, EsoJson.Options); } catch { return; }
            if (req == null || req.Open.Kind != "desktop") return;
            // The server re-issues the open under the PANEL key — the device
            // trusts the panel, and the grant's limits are applied here:
            // view-only forces View mode, recording is never granted.
            DesktopHello hello;
            try { hello = JsonSerializer.Deserialize<DesktopHello>(req.Open.PayloadJson, EsoJson.Options) ?? new(); }
            catch { hello = new(); }
            if (grant.Scope.DesktopViewOnly) hello.Mode = "View";
            hello.Record = false;
            var streamId = Guid.NewGuid().ToString("N");
            var issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var payloadJson = JsonSerializer.Serialize(hello, EsoJson.Options);
            var open = new FleetStreamOpenFrame
            {
                StreamId = streamId, Kind = "desktop", IssuedAt = issuedAt, Signer = "owner-panel",
                PayloadJson = payloadJson,
                Signature = OperatorSigning.SignStream(state.PanelKey, streamId, issuedAt, "desktop", payloadJson),
            };
            state.Activity.Add("grant", "stream", $"Grant {grant.Id[..8]}… opened a desktop stream to {grant.DeviceCode} ({(grant.Scope.DesktopViewOnly ? "view-only" : "control")})", "ok", grant.DeviceCode);
            state.Access.NoteAction(grant.Id);
            var viewOnly = grant.Scope.DesktopViewOnly;
            var clipboard = grant.Scope.Clipboard;
            await StreamRelay.BridgeAsync(state, ws, conn, open, StreamRelay.Flavor.Text, forwardOpened: true, ctx.RequestAborted,
                clientFrameFilter: json => GrantFrameAllowed(json, viewOnly, clipboard, fromDevice: false),
                deviceFrameFilter: json => GrantFrameAllowed(json, viewOnly, clipboard, fromDevice: true));
        });
    }

    /// <summary>Relay-side grant enforcement for desktop frames: view-only
    /// drops input + privacy from the grantee; clipboard off drops clipboard
    /// frames in BOTH directions; device frames are otherwise untouched.</summary>
    internal static bool GrantFrameAllowed(string json, bool viewOnly, bool clipboard, bool fromDevice)
    {
        string? type = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("type", out var t)) type = t.GetString();
        }
        catch { return true; }
        if (type == "clipboard") return clipboard;
        if (fromDevice) return true;
        if (type == "privacy") return false;           // privacy is owner/operator-only
        if (type == "input") return !viewOnly;
        return true;
    }

    internal static IResult CreateCode(FleetServerState state, AccessCodeCreateRequest? req, string actor)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.DeviceCode))
            return Results.Json(new ErrorResponse { Error = "Missing deviceCode." }, EsoJson.Options, statusCode: 400);
        var device = state.Registry.Find(req.DeviceCode);
        if (device == null || device.State != "Approved")
            return Results.Json(new ErrorResponse { Error = "Device not found or not approved." }, EsoJson.Options, statusCode: 404);
        var minutes = Math.Clamp(req.ExpiresMinutes, 1, 60 * 24 * 30);
        var plain = AccessCodePolicy.Generate();
        var record = new AccessCodeRecord
        {
            Id = Guid.NewGuid().ToString("N")[..16],
            CodeHash = AccessCodePolicy.Hash(plain),
            DeviceCode = device.Code,
            Scope = req.Scope ?? new GrantScope(),
            CreatedBy = actor,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(minutes),
            MaxUses = req.MaxUses is > 0 ? req.MaxUses : null,
        };
        state.Access.AddCode(record);
        state.Audit.Write("fleet/access", $"access code {record.Id} created for {device.Code} by {actor} (expires {record.ExpiresAt:u}, scope {record.Scope.Describe()})", "ok");
        state.Activity.Add(actor == "owner" ? "owner" : "operator", "access",
            $"Access code created for {device.Name} (expires {record.ExpiresAt:HH:mm}, {record.Scope.Describe()})", "ok", device.Code, device.Name);
        return Results.Json(new AccessCodeCreatedResponse
        {
            Id = record.Id, Code = AccessCodePolicy.Display(plain), DeviceCode = device.Code,
            ExpiresAt = record.ExpiresAt, Scope = record.Scope,
        }, EsoJson.Options);
    }

    internal static IResult Revoke(FleetServerState state, AccessRevokeRequest? req, string actor)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.Id))
            return Results.Json(new ErrorResponse { Error = "Missing id." }, EsoJson.Options, statusCode: 400);
        var (found, what) = state.Access.Revoke(req.Id.Trim());
        if (!found) return Results.Json(new ErrorResponse { Error = "No such code or grant." }, EsoJson.Options, statusCode: 404);
        state.Audit.Write("fleet/access", $"{what} {req.Id} revoked by {actor}", "ok");
        state.Activity.Add(actor == "owner" ? "owner" : "operator", "access", $"Access {what} {req.Id[..Math.Min(8, req.Id.Length)]}… revoked", "ok");
        return Results.Json(new { revoked = true, what }, EsoJson.Options);
    }

    /// <summary>Validates the X-Eso-Grant header against the signed token AND
    /// the live grant record. Both must agree; the record wins.</summary>
    internal static GrantRecord? ValidateGrant(HttpContext ctx, FleetServerState state, out string error)
    {
        error = "";
        var header = ctx.Request.Headers["X-Eso-Grant"].ToString();
        if (string.IsNullOrWhiteSpace(header)) { error = "Missing X-Eso-Grant header."; return null; }
        GrantTokenDto? token;
        try { token = JsonSerializer.Deserialize<GrantTokenDto>(Encoding.UTF8.GetString(Convert.FromBase64String(header)), EsoJson.Options); }
        catch { error = "Grant token is malformed."; return null; }
        if (token == null || !GrantSigning.Verify(state.PanelPublicKey, token)) { error = "Grant token signature is invalid."; return null; }
        var record = state.Access.FindGrant(token.GrantId);
        if (record == null) { error = "Grant not found."; return null; }
        if (record.DeviceCode != token.DeviceCode || record.Scope.Canonical() != token.Scope.Canonical())
        { error = "Grant token does not match the grant record."; return null; }
        var (ok, reason) = AccessCodePolicy.GrantUsable(record, DateTimeOffset.UtcNow);
        if (!ok) { error = reason; return null; }
        return record;
    }
}
