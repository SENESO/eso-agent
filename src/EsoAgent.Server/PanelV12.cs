using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EsoAgent.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EsoAgent.Server;

/// <summary>
/// The v1.2 fleet-panel API: the same unified activity timeline, health cache,
/// script library, rollout and wake the operator API exposes — behind the
/// owner's panel session instead of the operator signature — plus the panel's
/// own extras: 2FA enrollment, the alert-webhook settings, and the relayed
/// desktop WebSocket the browser viewer uses. One web face: from the device
/// list the owner opens any device (status / desktop / files / activity)
/// without a second sign-in.
/// </summary>
public static class FleetPanelV12
{
    private const string CookieName = "eso_owner";

    public static void MapFleetPanelV12(this IEndpointRouteBuilder app, FleetServerState state)
    {
        OwnerSession? Session(HttpContext ctx) => state.Sessions.Validate(ctx.Request.Cookies[CookieName]);
        IResult NeedLogin() => Results.Json(new ErrorResponse { Error = "Owner login required." }, EsoJson.Options, statusCode: 401);
        bool CsrfOk(HttpContext ctx, OwnerSession s) => OwnerSessionStore.CheckCsrf(s, ctx.Request.Headers["X-CSRF"].ToString());
        IResult NeedCsrf() => Results.Json(new ErrorResponse { Error = "Missing or invalid CSRF token." }, EsoJson.Options, statusCode: 403);

        // ----- unified activity (same store the operator API and the app read) -----
        app.MapGet("/panel/api/activity", (HttpContext ctx) =>
        {
            if (Session(ctx) == null) return NeedLogin();
            return Results.Json(state.Activity.Query(FleetOperatorApiV12.ParseQuery(ctx)), EsoJson.Options);
        });

        app.MapGet("/panel/api/activity/export", (HttpContext ctx) =>
        {
            if (Session(ctx) == null) return NeedLogin();
            var events = state.Activity.Export(FleetOperatorApiV12.ParseQuery(ctx));
            if (string.Equals(ctx.Request.Query["format"], "csv", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.Headers.ContentDisposition = "attachment; filename=esoagent-activity.csv";
                return Results.Text(ActivityCsv.ToCsv(events), "text/csv; charset=utf-8");
            }
            ctx.Response.Headers.ContentDisposition = "attachment; filename=esoagent-activity.json";
            return Results.Json(events, EsoJson.Options);
        });

        // ----- health dashboard cache -----
        app.MapGet("/panel/api/health", (HttpContext ctx) =>
        {
            if (Session(ctx) == null) return NeedLogin();
            return Results.Json(state.Health.All(), EsoJson.Options);
        });

        // ----- script library (mirror of the operator API, owner session) -----
        app.MapGet("/panel/api/scripts", (HttpContext ctx) =>
        {
            if (Session(ctx) == null) return NeedLogin();
            return Results.Json(state.Scripts.List(), EsoJson.Options);
        });

        app.MapPost("/panel/api/scripts", async (HttpContext ctx) =>
        {
            var s = Session(ctx); if (s == null) return NeedLogin();
            if (!CsrfOk(ctx, s)) return NeedCsrf();
            var dto = await ctx.Request.ReadFromJsonAsync<ScriptDto>(EsoJson.Options);
            if (dto == null || string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrEmpty(dto.Body))
                return Results.Json(new ErrorResponse { Error = "A script needs a name and a body." }, EsoJson.Options, statusCode: 400);
            dto.CreatedBy = "owner-panel";
            var saved = state.Scripts.Upsert(dto);
            state.Activity.Add("owner", "script", $"Script '{saved.Name}' saved", "ok");
            return Results.Json(saved, EsoJson.Options);
        });

        app.MapDelete("/panel/api/scripts/{id}", (HttpContext ctx, string id) =>
        {
            var s = Session(ctx); if (s == null) return NeedLogin();
            if (!CsrfOk(ctx, s)) return NeedCsrf();
            if (!state.Scripts.Delete(id))
                return Results.Json(new ErrorResponse { Error = "Script not found." }, EsoJson.Options, statusCode: 404);
            state.Activity.Add("owner", "script", $"Script '{id}' deleted", "ok");
            return Results.Json(new { deleted = id }, EsoJson.Options);
        });

        app.MapPost("/panel/api/scripts/{id}/run", async (HttpContext ctx, string id) =>
        {
            var s = Session(ctx); if (s == null) return NeedLogin();
            if (!CsrfOk(ctx, s)) return NeedCsrf();
            var script = state.Scripts.Find(id);
            if (script == null) return Results.Json(new ErrorResponse { Error = "Script not found." }, EsoJson.Options, statusCode: 404);
            var req = await ctx.Request.ReadFromJsonAsync<PanelScriptRunRequest>(EsoJson.Options) ?? new();
            var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script.Body))).ToLowerInvariant();
            var shellBody = JsonSerializer.SerializeToUtf8Bytes(new ShellRequest
            {
                Shell = script.Shell == "default" ? "" : script.Shell,
                Script = script.Body,
                TimeoutMs = 300_000,
            }, EsoJson.Options);
            var results = new List<object>();
            foreach (var code in req.Targets)
            {
                var record = state.Registry.Find(code);
                if (record == null || record.State != "Approved")
                {
                    results.Add(new { code, ok = false, error = "Unknown or unapproved device." });
                    continue;
                }
                var result = await Relay.PanelCommandAsync(state, record.Code, "POST", "/api/shell", shellBody, TimeSpan.FromSeconds(330));
                object? bodyObj = null;
                if (!string.IsNullOrEmpty(result.BodyBase64))
                {
                    var text = Encoding.UTF8.GetString(Convert.FromBase64String(result.BodyBase64));
                    try { bodyObj = JsonSerializer.Deserialize<JsonElement>(text); } catch { bodyObj = text; }
                }
                results.Add(new { code = record.Code, ok = result.Error == null && result.HttpStatus is >= 200 and < 300, httpStatus = result.HttpStatus, body = bodyObj, error = result.Error });
            }
            state.Audit.Write("panel/scripts", $"script '{script.Name}' run on [{string.Join(",", req.Targets)}] sha256={sha} (owner panel)", "ok");
            state.Activity.Add("owner", "script",
                $"Script '{script.Name}' run on [{string.Join(",", req.Targets)}] (body sha256 {sha})", "done");
            return Results.Json(new { script = script.Name, bodySha256 = sha, results }, EsoJson.Options);
        });

        // ----- update package + rollout (owner path; panel key dispatches) -----
        app.MapGet("/panel/api/update/package", (HttpContext ctx) =>
        {
            if (Session(ctx) == null) return NeedLogin();
            var info = state.Packages.Current;
            return info == null
                ? Results.Json(new ErrorResponse { Error = "No update package has been uploaded." }, EsoJson.Options, statusCode: 404)
                : Results.Json(info, EsoJson.Options);
        });

        app.MapPost("/panel/api/update/package", async (HttpContext ctx) =>
        {
            var s = Session(ctx); if (s == null) return NeedLogin();
            if (!CsrfOk(ctx, s)) return NeedCsrf();
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms);
            if (ms.Length == 0) return Results.Json(new ErrorResponse { Error = "Empty package." }, EsoJson.Options, statusCode: 400);
            ms.Position = 0;
            var info = await state.Packages.SaveAsync(ms, "owner-panel", ctx.RequestAborted);
            state.Activity.Add("owner", "rollout", $"Update package uploaded (sha256 {info.Sha256[..12]}…, {info.Size} bytes)", "ok");
            return Results.Json(info, EsoJson.Options);
        });

        app.MapGet("/panel/api/rollout", (HttpContext ctx) =>
        {
            if (Session(ctx) == null) return NeedLogin();
            return Results.Json(state.Rollouts.List(), EsoJson.Options);
        });

        app.MapPost("/panel/api/rollout", async (HttpContext ctx) =>
        {
            var s = Session(ctx); if (s == null) return NeedLogin();
            if (!CsrfOk(ctx, s)) return NeedCsrf();
            var req = await ctx.Request.ReadFromJsonAsync<PanelRolloutRequest>(EsoJson.Options);
            if (req == null) return Results.Json(new ErrorResponse { Error = "Bad rollout request." }, EsoJson.Options, statusCode: 400);
            if (state.Packages.Current == null)
                return Results.Json(new ErrorResponse { Error = "Upload an update package first." }, EsoJson.Options, statusCode: 409);
            var dto = FleetOperatorApiV12.CreateRollout(state, req.Targets, "owner-panel");
            await FleetOperatorApiV12.DispatchPanelRolloutAsync(state, dto);
            return Results.Json(state.Rollouts.Get(dto.Id)!.Dto, EsoJson.Options);
        });

        // ----- wake + exit node -----
        app.MapPost("/panel/api/wake", async (HttpContext ctx) =>
        {
            var s = Session(ctx); if (s == null) return NeedLogin();
            if (!CsrfOk(ctx, s)) return NeedCsrf();
            var req = await ctx.Request.ReadFromJsonAsync<WakeCodeRequest>(EsoJson.Options);
            if (req == null) return Results.Json(new ErrorResponse { Error = "Bad wake request." }, EsoJson.Options, statusCode: 400);
            var results = await FleetOperatorApiV12.WakeAsync(state, req.Code, "owner");
            return results == null
                ? Results.Json(new ErrorResponse { Error = "Device not found." }, EsoJson.Options, statusCode: 404)
                : Results.Json(results, EsoJson.Options);
        });

        // ----- access codes (item 41): owner mints / lists / revokes -----
        app.MapPost("/panel/api/access/codes", async (HttpContext ctx) =>
        {
            var s = Session(ctx); if (s == null) return NeedLogin();
            if (!CsrfOk(ctx, s)) return NeedCsrf();
            var body = await ctx.Request.ReadFromJsonAsync<AccessCodeCreateRequest>(EsoJson.Options);
            return FleetAccessApi.CreateCode(state, body, "owner");
        });

        app.MapGet("/panel/api/access/grants", (HttpContext ctx) =>
        {
            var s = Session(ctx); if (s == null) return NeedLogin();
            var device = ctx.Request.Query["device"].ToString();
            var filter = string.IsNullOrEmpty(device) ? null : device;
            return Results.Json(new
            {
                codes = state.Access.Codes(filter),
                grants = state.Access.Grants(filter),
            }, EsoJson.Options);
        });

        app.MapPost("/panel/api/access/revoke", async (HttpContext ctx) =>
        {
            var s = Session(ctx); if (s == null) return NeedLogin();
            if (!CsrfOk(ctx, s)) return NeedCsrf();
            var body = await ctx.Request.ReadFromJsonAsync<AccessRevokeRequest>(EsoJson.Options);
            return FleetAccessApi.Revoke(state, body, "owner");
        });

        app.MapPost("/panel/api/mesh/exit-node", async (HttpContext ctx) =>
        {
            var s = Session(ctx); if (s == null) return NeedLogin();
            if (!CsrfOk(ctx, s)) return NeedCsrf();
            var req = await ctx.Request.ReadFromJsonAsync<ExitNodeRequest>(EsoJson.Options);
            var code = string.IsNullOrWhiteSpace(req?.Code) ? null : req.Code;
            if (code != null && state.Registry.Find(code) == null)
                return Results.Json(new ErrorResponse { Error = "Device not found." }, EsoJson.Options, statusCode: 404);
            state.Registry.SetExitNode(code);
            await FleetMesh.PushToAllAsync(state);
            state.Activity.Add("owner", "mesh", code == null ? "Mesh exit node cleared" : $"Mesh exit node set to {code}", "ok", code);
            return Results.Json(new { exitNode = code }, EsoJson.Options);
        });

        // ----- 2FA enrollment (owner-only, from the panel) -----
        app.MapPost("/panel/api/access/2fa/begin", (HttpContext ctx) =>
        {
            var s = Session(ctx); if (s == null) return NeedLogin();
            if (!CsrfOk(ctx, s)) return NeedCsrf();
            var secret = state.OwnerAuth.BeginTotpEnrollment();
            return Results.Json(new { secret, otpauthUri = Totp.OtpauthUri(secret, "EsoAgent Fleet") }, EsoJson.Options);
        });

        app.MapPost("/panel/api/access/2fa/confirm", async (HttpContext ctx) =>
        {
            var s = Session(ctx); if (s == null) return NeedLogin();
            if (!CsrfOk(ctx, s)) return NeedCsrf();
            var body = await ctx.Request.ReadFromJsonAsync<TotpCodeRequest>(EsoJson.Options);
            if (body == null || !state.OwnerAuth.ConfirmTotpEnrollment(body.Code))
                return Results.Json(new ErrorResponse { Error = "That code did not verify. Check the authenticator and try again." }, EsoJson.Options, statusCode: 400);
            state.Audit.Write("panel/access", "owner 2FA enabled (fleet panel)", "ok");
            state.Activity.Add("owner", "access", "Two-factor auth enabled for the fleet panel", "ok");
            return Results.Json(new { totpEnabled = true }, EsoJson.Options);
        });

        app.MapPost("/panel/api/access/2fa/disable", async (HttpContext ctx) =>
        {
            var s = Session(ctx); if (s == null) return NeedLogin();
            if (!CsrfOk(ctx, s)) return NeedCsrf();
            var body = await ctx.Request.ReadFromJsonAsync<TotpDisableRequest>(EsoJson.Options);
            if (body == null || !state.OwnerAuth.Verify(body.CurrentPassword))
                return Results.Json(new ErrorResponse { Error = "Current password is wrong." }, EsoJson.Options, statusCode: 403);
            state.OwnerAuth.DisableTotp();
            state.Audit.Write("panel/access", "owner 2FA disabled (fleet panel)", "ok");
            state.Activity.Add("owner", "access", "Two-factor auth disabled for the fleet panel", "ok");
            return Results.Json(new { totpEnabled = false }, EsoJson.Options);
        });

        // ----- alert webhook settings -----
        app.MapPost("/panel/api/access/webhook", async (HttpContext ctx) =>
        {
            var s = Session(ctx); if (s == null) return NeedLogin();
            if (!CsrfOk(ctx, s)) return NeedCsrf();
            var body = await ctx.Request.ReadFromJsonAsync<WebhookRequest>(EsoJson.Options);
            var url = string.IsNullOrWhiteSpace(body?.Url) ? null : body!.Url!.Trim();
            if (url != null && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")))
                return Results.Json(new ErrorResponse { Error = "Webhook URL must be an absolute http(s) URL." }, EsoJson.Options, statusCode: 400);
            var (_, existingSecret) = state.Registry.Webhook;
            string? shownSecret = null;
            var secret = existingSecret;
            if (url != null && string.IsNullOrEmpty(secret))
            {
                secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
                shownSecret = secret; // shown exactly once, at generation time
            }
            state.Registry.SetWebhook(url, secret);
            state.Audit.Write("panel/access", $"alert webhook {(url == null ? "cleared" : "set")}", "ok");
            return Results.Json(new { webhookUrl = url, webhookSecret = shownSecret, webhookSecretSet = !string.IsNullOrEmpty(secret) }, EsoJson.Options);
        });

        // ----- raw file download for the device view's file manager -----
        // /panel/api/command JSON-encodes bodies (binary would corrupt), so
        // ranged downloads ride this endpoint and return the bytes verbatim.
        app.MapGet("/panel/api/download", async (HttpContext ctx) =>
        {
            if (Session(ctx) == null) { ctx.Response.StatusCode = 401; return; }
            var code = ctx.Request.Query["code"].ToString();
            var path = ctx.Request.Query["path"].ToString();
            var offset = long.TryParse(ctx.Request.Query["offset"], out var o) ? o : 0;
            var length = int.TryParse(ctx.Request.Query["length"], out var l) ? Math.Clamp(l, 1, 4 * 1024 * 1024) : 1024 * 1024;
            var record = state.Registry.Find(code);
            if (record == null || record.State != "Approved") { ctx.Response.StatusCode = 404; return; }
            var result = await Relay.PanelCommandAsync(state, record.Code, "GET",
                $"/api/files/download?path={Uri.EscapeDataString(path)}&offset={offset}&length={length}",
                Array.Empty<byte>(), TimeSpan.FromSeconds(120));
            if (result.Error != null || result.HttpStatus is not (>= 200 and < 300))
            {
                ctx.Response.StatusCode = 502;
                await ctx.Response.WriteAsync(result.Error ?? $"device http {result.HttpStatus}");
                return;
            }
            var bytes = string.IsNullOrEmpty(result.BodyBase64) ? Array.Empty<byte>() : Convert.FromBase64String(result.BodyBase64);
            ctx.Response.ContentType = "application/octet-stream";
            await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
        });

        // ----- relayed desktop for the browser viewer -----
        // Session-cookie auth (a browser WebSocket cannot carry custom headers).
        // The server builds the stream-open and signs it with the PANEL key; the
        // device verifies that signature itself before streaming.
        app.MapGet("/panel/desktop-relay", async (HttpContext ctx) =>
        {
            if (Session(ctx) == null) { ctx.Response.StatusCode = 401; return; }
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            var code = ctx.Request.Query["code"].ToString();
            var record = state.Registry.Find(code);
            if (record == null || record.State != "Approved") { ctx.Response.StatusCode = 404; return; }
            if (!state.Connections.TryGetValue(record.Code, out var conn)) { ctx.Response.StatusCode = 409; return; }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var hello = await StreamRelay.ReceiveTextAsync(ws, ctx.RequestAborted);
            if (hello == null) return;
            var streamId = Guid.NewGuid().ToString("N");
            var issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var open = new FleetStreamOpenFrame
            {
                StreamId = streamId,
                Kind = "desktop",
                IssuedAt = issuedAt,
                Signer = "owner-panel",
                PayloadJson = hello,
                Signature = OperatorSigning.SignStream(state.PanelKey, streamId, issuedAt, "desktop", hello),
            };
            state.Audit.Write("panel/desktop", $"relayed desktop -> {record.Code} (owner panel)", "ok");
            state.Activity.Add("owner", "stream", $"Desktop session to {record.Name} from the fleet panel", "ok", record.Code, record.Name);
            await StreamRelay.BridgeAsync(state, ws, conn, open, StreamRelay.Flavor.Text, forwardOpened: false, ctx.RequestAborted);
        });
    }
}
