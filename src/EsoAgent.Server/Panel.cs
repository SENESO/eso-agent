using System.Reflection;
using System.Text;
using System.Text.Json;
using EsoAgent.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EsoAgent.Server;

public sealed class PanelCommandRequest
{
    public string Code { get; set; } = "";
    public string Method { get; set; } = "POST";
    public string Path { get; set; } = "";
    public JsonElement? Body { get; set; }
    /// <summary>Raw body (base64) used verbatim when present — for binary-ish
    /// payloads like file-transfer chunks in the fleet panel's device view.</summary>
    public string? BodyBase64 { get; set; }
}

public sealed class WebhookRequest { public string? Url { get; set; } }
public sealed class PanelRolloutRequest { public FleetTargets Targets { get; set; } = new(); }
public sealed class PanelScriptRunRequest { public List<string> Targets { get; set; } = new(); }
public sealed class TotpDisableRequest { public string CurrentPassword { get; set; } = ""; }

public sealed class RotateOperatorKeyRequest { public string NewPublicKey { get; set; } = ""; }

/// <summary>
/// The fleet OWNER PANEL (served by the server at /panel). Owner tier = everything
/// the operator can do, PLUS the reserved controls that exist only here:
/// revoke/restore the operator, rotate the operator key, change the owner
/// password. Panel-issued device commands are signed with the server's panel
/// key, which devices trust alongside (never instead of) the operator key.
/// </summary>
public static class FleetPanel
{
    private const string CookieName = "eso_owner";
    private const string TotpCookieName = "eso_owner_2fa";

    private const string PasswordForm = """
        <form method="post" action="/panel/login">
          <label for="password" data-i18n="pw">Owner password</label>
          <input id="password" name="password" type="password" autocomplete="current-password" required autofocus>
          <button type="submit" data-i18n="signin">Sign in</button>
        </form>
        """;

    private const string TotpForm = """
        <form method="post" action="/panel/login/totp">
          <p class="sub" data-i18n="codesub">Enter the 6-digit code from your authenticator app.</p>
          <label for="code" data-i18n="code">Authenticator code (6 digits)</label>
          <input id="code" name="code" inputmode="numeric" autocomplete="one-time-code" required autofocus>
          <button type="submit" data-i18n="verify">Verify</button>
        </form>
        """;

    public static void MapFleetPanel(this IEndpointRouteBuilder app, FleetServerState state)
    {
        var loginHtml = LoadResource("FleetLogin.html");
        var spaHtml = LoadResource("Fleet.html");
        var pendingTotp = new PendingTotpStore();

        OwnerSession? Session(HttpContext ctx) => state.Sessions.Validate(ctx.Request.Cookies[CookieName]);
        IResult NeedLogin() => Results.Json(new ErrorResponse { Error = "Owner login required." }, EsoJson.Options, statusCode: 401);
        bool CsrfOk(HttpContext ctx, OwnerSession s) => OwnerSessionStore.CheckCsrf(s, ctx.Request.Headers["X-CSRF"].ToString());
        IResult NeedCsrf() => Results.Json(new ErrorResponse { Error = "Missing or invalid CSRF token." }, EsoJson.Options, statusCode: 403);

        string LoginPage(HttpContext ctx, string? error)
        {
            var totpMode = pendingTotp.Validate(ctx.Request.Cookies[TotpCookieName]);
            var page = loginHtml.Replace("<!--FORM-->", totpMode ? TotpForm : PasswordForm);
            return error == null ? page : LoginPageWithError(page, error);
        }

        void StartSession(HttpContext ctx)
        {
            var session = state.Sessions.Create();
            ctx.Response.Cookies.Append(CookieName, session.Id, new CookieOptions
            {
                HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/panel", Expires = session.ExpiresAt,
            });
            ctx.Response.Cookies.Delete(TotpCookieName, new CookieOptions { Path = "/panel" });
        }

        void FailedLogin(string source, string what)
        {
            state.Limiter.RecordFailure(source);
            state.Audit.Write("panel/login", $"failed {what} from {source}", "401");
            state.Activity.Add("owner", "login", $"Failed {what} on the fleet panel from {source}", "401");
            // Alert once per lockout episode (throttled to one alert per hour).
            if (state.Limiter.IsLockedOut(source, out _) &&
                !state.Activity.Query(new ActivityQuery { Source = "alert", Limit = 100 })
                    .Any(e => e.Summary.Contains("failed owner logins") && e.At > DateTimeOffset.UtcNow.AddHours(-1)))
                state.Activity.AddAlert($"Repeated failed owner logins on the fleet panel from {source} — the login is temporarily locked.");
        }

        app.MapGet("/panel/login", (HttpContext ctx) => Results.Content(LoginPage(ctx, null), "text/html; charset=utf-8"));

        app.MapPost("/panel/login", async (HttpContext ctx) =>
        {
            if (!state.OwnerAuth.IsConfigured)
                return Results.Content(LoginPage(ctx, "Owner password is not set on this server. Run: eso-server set-owner-password"), "text/html; charset=utf-8", statusCode: 503);
            var source = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (state.Limiter.IsLockedOut(source, out var remaining))
                return Results.Content(LoginPage(ctx, $"Too many failed attempts. Try again in {Math.Ceiling(remaining.TotalSeconds)}s."), "text/html; charset=utf-8", statusCode: 429);
            var form = await ctx.Request.ReadFormAsync();
            if (!state.OwnerAuth.Verify(form["password"].ToString()))
            {
                FailedLogin(source, "owner login");
                return Results.Content(LoginPage(ctx, "Wrong password."), "text/html; charset=utf-8", statusCode: 401);
            }
            state.Limiter.RecordSuccess(source);
            // Second factor (v1.2): identical two-step flow to the device panel.
            if (state.OwnerAuth.TotpEnabled)
            {
                var token = pendingTotp.Create();
                ctx.Response.Cookies.Append(TotpCookieName, token, new CookieOptions
                {
                    HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/panel",
                    Expires = DateTimeOffset.UtcNow.Add(PendingTotpStore.Lifetime),
                });
                state.Audit.Write("panel/login", $"owner password accepted from {source}; awaiting TOTP", "ok");
                return Results.Redirect("/panel/login");
            }
            StartSession(ctx);
            state.Audit.Write("panel/login", $"owner login from {source}", "ok");
            state.Activity.Add("owner", "login", $"Owner signed in to the fleet panel from {source}", "ok");
            return Results.Redirect("/panel/");
        });

        app.MapPost("/panel/login/totp", async (HttpContext ctx) =>
        {
            var source = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var token = ctx.Request.Cookies[TotpCookieName];
            if (!pendingTotp.Validate(token))
                return Results.Redirect("/panel/login");
            if (state.Limiter.IsLockedOut(source, out var remaining))
                return Results.Content(LoginPage(ctx, $"Too many failed attempts. Try again in {Math.Ceiling(remaining.TotalSeconds)}s."), "text/html; charset=utf-8", statusCode: 429);
            var form = await ctx.Request.ReadFormAsync();
            if (!state.OwnerAuth.VerifyTotp(form["code"].ToString()))
            {
                FailedLogin(source, "TOTP code");
                return Results.Content(LoginPage(ctx, "Wrong code."), "text/html; charset=utf-8", statusCode: 401);
            }
            state.Limiter.RecordSuccess(source);
            pendingTotp.Consume(token);
            StartSession(ctx);
            state.Audit.Write("panel/login", $"owner login (2FA) from {source}", "ok");
            state.Activity.Add("owner", "login", $"Owner signed in to the fleet panel (2FA) from {source}", "ok");
            return Results.Redirect("/panel/");
        });

        app.MapPost("/panel/logout", (HttpContext ctx) =>
        {
            state.Sessions.Revoke(ctx.Request.Cookies[CookieName]);
            ctx.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/panel" });
            return Results.Redirect("/panel/login");
        });

        // One registration serves both /panel and /panel/ (endpoint routing
        // matches the trailing-slash variant too); registering both is ambiguous.
        app.MapGet("/panel", (HttpContext ctx) =>
            Session(ctx) == null ? Results.Redirect("/panel/login") : Results.Content(spaHtml, "text/html; charset=utf-8"));

        app.MapGet("/panel/api/session", (HttpContext ctx) =>
        {
            var s = Session(ctx);
            if (s == null) return NeedLogin();
            return Results.Json(new { csrf = s.CsrfToken }, EsoJson.Options);
        });

        app.MapGet("/panel/api/devices", (HttpContext ctx) =>
        {
            if (Session(ctx) == null) return NeedLogin();
            return Results.Json(FleetOperatorApi.ListDevices(state), EsoJson.Options);
        });

        async Task<IResult> DeviceAdminAsync(HttpContext ctx, string action)
        {
            var s = Session(ctx); if (s == null) return NeedLogin();
            if (!CsrfOk(ctx, s)) return NeedCsrf();
            switch (action)
            {
                case "approve":
                case "reject":
                {
                    var req = await ctx.Request.ReadFromJsonAsync<CodeRequest>(EsoJson.Options);
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
                        await FleetMesh.PushToAllAsync(state);
                    }
                    state.Audit.Write("panel/devices", $"{action} {record.Code} (owner panel)", "ok");
                    return Results.Json(new { code = record.Code, state = record.State }, EsoJson.Options);
                }
                case "rename":
                {
                    var req = await ctx.Request.ReadFromJsonAsync<RenameRequest>(EsoJson.Options);
                    if (req == null || !state.Registry.Rename(req.Code, req.Name))
                        return Results.Json(new ErrorResponse { Error = "Device not found." }, EsoJson.Options, statusCode: 404);
                    state.Audit.Write("panel/devices", $"rename {req.Code} -> '{req.Name}' (owner panel)", "ok");
                    await FleetMesh.PushToAllAsync(state);
                    return Results.Json(new { code = req.Code, name = req.Name }, EsoJson.Options);
                }
                case "groups":
                {
                    var req = await ctx.Request.ReadFromJsonAsync<GroupsRequest>(EsoJson.Options);
                    if (req == null || !state.Registry.SetGroups(req.Code, req.Groups))
                        return Results.Json(new ErrorResponse { Error = "Device not found." }, EsoJson.Options, statusCode: 404);
                    state.Audit.Write("panel/devices", $"groups {req.Code} = [{string.Join(",", req.Groups)}] (owner panel)", "ok");
                    await FleetMesh.PushToAllAsync(state);
                    return Results.Json(new { code = req.Code, groups = req.Groups }, EsoJson.Options);
                }
                default:
                    return Results.Json(new ErrorResponse { Error = "Unknown action." }, EsoJson.Options, statusCode: 400);
            }
        }

        // NOTE: statement-bodied lambdas with an explicit return are required here —
        // an expression-bodied `=> await Helper(...)` registration silently drops the
        // helper's IResult on this SDK (verified by repro + smoke test: 200/empty responses).
        app.MapPost("/panel/api/devices/approve", async (HttpContext ctx) => { return await DeviceAdminAsync(ctx, "approve"); });
        app.MapPost("/panel/api/devices/reject", async (HttpContext ctx) => { return await DeviceAdminAsync(ctx, "reject"); });
        app.MapPost("/panel/api/devices/rename", async (HttpContext ctx) => { return await DeviceAdminAsync(ctx, "rename"); });
        app.MapPost("/panel/api/devices/groups", async (HttpContext ctx) => { return await DeviceAdminAsync(ctx, "groups"); });

        // Device drill-in: panel-signed command relay to one device's own API.
        app.MapPost("/panel/api/command", async (HttpContext ctx) =>
        {
            var s = Session(ctx); if (s == null) return NeedLogin();
            if (!CsrfOk(ctx, s)) return NeedCsrf();
            var req = await ctx.Request.ReadFromJsonAsync<PanelCommandRequest>(EsoJson.Options);
            if (req == null) return Results.Json(new ErrorResponse { Error = "Bad command." }, EsoJson.Options, statusCode: 400);
            var record = state.Registry.Find(req.Code);
            if (record == null) return Results.Json(new ErrorResponse { Error = "Device not found." }, EsoJson.Options, statusCode: 404);
            if (record.State != "Approved") return Results.Json(new ErrorResponse { Error = $"Device is {record.State}, not Approved." }, EsoJson.Options, statusCode: 409);
            byte[] bodyBytes;
            if (!string.IsNullOrEmpty(req.BodyBase64))
            {
                try { bodyBytes = Convert.FromBase64String(req.BodyBase64); }
                catch { return Results.Json(new ErrorResponse { Error = "BodyBase64 is not valid base64." }, EsoJson.Options, statusCode: 400); }
            }
            else bodyBytes = req.Body.HasValue ? JsonSerializer.SerializeToUtf8Bytes(req.Body.Value, EsoJson.Options) : Array.Empty<byte>();
            var result = await Relay.PanelCommandAsync(state, record.Code, req.Method, req.Path, bodyBytes, TimeSpan.FromSeconds(120));
            state.Audit.Write("panel/command", $"{req.Method} {req.Path} -> {record.Code} (owner panel)", result.Error ?? $"http {result.HttpStatus}");
            object? bodyObj = null;
            if (!string.IsNullOrEmpty(result.BodyBase64))
            {
                var text = Encoding.UTF8.GetString(Convert.FromBase64String(result.BodyBase64));
                try { bodyObj = JsonSerializer.Deserialize<JsonElement>(text); } catch { bodyObj = text; }
            }
            return Results.Json(new { httpStatus = result.HttpStatus, body = bodyObj, error = result.Error }, EsoJson.Options);
        });

        // ----- mesh (update 8): devices' virtual IPs / WireGuard keys, subnet approvals, ACLs, enrollment keys -----
        app.MapGet("/panel/api/mesh", (HttpContext ctx) =>
        {
            if (Session(ctx) == null) return NeedLogin();
            return Results.Json(FleetOperatorApi.BuildMeshState(state), EsoJson.Options);
        });

        app.MapPost("/panel/api/mesh/enrollment-keys", async (HttpContext ctx) =>
        {
            var s = Session(ctx); if (s == null) return NeedLogin();
            if (!CsrfOk(ctx, s)) return NeedCsrf();
            var req = await ctx.Request.ReadFromJsonAsync<EnrollmentKeyCreateRequest>(EsoJson.Options) ?? new();
            var key = EnrollmentKeys.Generate();
            var record = new EnrollmentKeyRecord
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                KeyHash = EnrollmentKeys.Hash(key),
                UsesLeft = req.Uses,
                CreatedUtc = DateTimeOffset.UtcNow.ToString("O"),
                CreatedBy = "owner-panel",
                Note = req.Note,
            };
            state.Registry.AddEnrollmentKey(record);
            state.Audit.Write("panel/mesh", $"enrollment key created id={record.Id} uses={(req.Uses?.ToString() ?? "unlimited")}", "ok");
            return Results.Json(new { id = record.Id, key, usesLeft = record.UsesLeft }, EsoJson.Options); // plaintext shown once
        });

        app.MapPost("/panel/api/mesh/enrollment-keys/revoke", async (HttpContext ctx) =>
        {
            var s = Session(ctx); if (s == null) return NeedLogin();
            if (!CsrfOk(ctx, s)) return NeedCsrf();
            var req = await ctx.Request.ReadFromJsonAsync<IdRequest>(EsoJson.Options);
            if (req == null || !state.Registry.RevokeEnrollmentKey(req.Id))
                return Results.Json(new ErrorResponse { Error = "Enrollment key not found." }, EsoJson.Options, statusCode: 404);
            state.Audit.Write("panel/mesh", $"enrollment key revoked id={req.Id}", "ok");
            return Results.Json(new { revoked = req.Id }, EsoJson.Options);
        });

        app.MapPost("/panel/api/mesh/subnets", async (HttpContext ctx) =>
        {
            var s = Session(ctx); if (s == null) return NeedLogin();
            if (!CsrfOk(ctx, s)) return NeedCsrf();
            var req = await ctx.Request.ReadFromJsonAsync<SubnetsRequest>(EsoJson.Options);
            if (req == null || !state.Registry.SetApprovedSubnets(req.Code, req.Approved))
                return Results.Json(new ErrorResponse { Error = "Device not found." }, EsoJson.Options, statusCode: 404);
            state.Audit.Write("panel/mesh", $"approved subnets {req.Code} = [{string.Join(",", req.Approved)}]", "ok");
            await FleetMesh.PushToAllAsync(state);
            return Results.Json(new { code = req.Code, approvedSubnets = req.Approved }, EsoJson.Options);
        });

        app.MapPost("/panel/api/mesh/acl", async (HttpContext ctx) =>
        {
            var s = Session(ctx); if (s == null) return NeedLogin();
            if (!CsrfOk(ctx, s)) return NeedCsrf();
            var req = await ctx.Request.ReadFromJsonAsync<AclRequest>(EsoJson.Options);
            if (req == null) return Results.Json(new ErrorResponse { Error = "Bad ACL request." }, EsoJson.Options, statusCode: 400);
            state.Registry.SetAclRules(req.Rules);
            state.Audit.Write("panel/mesh", $"ACL rules replaced: {req.Rules.Count} rule(s)", "ok");
            await FleetMesh.PushToAllAsync(state);
            return Results.Json(new { rules = req.Rules.Count }, EsoJson.Options);
        });

        app.MapGet("/panel/api/audit", (HttpContext ctx, int? lines) =>
        {
            if (Session(ctx) == null) return NeedLogin();
            return Results.Json(new { lines = state.Audit.Tail(lines ?? 200) }, EsoJson.Options);
        });

        // ----- owner-reserved access controls -----
        app.MapGet("/panel/api/access", (HttpContext ctx) =>
        {
            if (Session(ctx) == null) return NeedLogin();
            var (webhookUrl, webhookSecret) = state.Registry.Webhook;
            return Results.Json(new
            {
                operatorEnabled = state.Registry.OperatorEnabled,
                operatorKeySet = state.OperatorPublicKey != null,
                certFingerprint = state.CertFingerprint,
                totpEnabled = state.OwnerAuth.TotpEnabled,
                webhookUrl,
                webhookSecretSet = !string.IsNullOrEmpty(webhookSecret),
                alertOfflineMinutes = state.Registry.AlertOfflineMinutes,
                alertsLast24h = state.Activity.CountAlerts(DateTimeOffset.UtcNow.AddHours(-24)),
            }, EsoJson.Options);
        });

        app.MapPost("/panel/api/access/revoke-operator", async (HttpContext ctx) =>
        {
            var s = Session(ctx); if (s == null) return NeedLogin();
            if (!CsrfOk(ctx, s)) return NeedCsrf();
            state.Registry.OperatorEnabled = false;
            int pushed = 0;
            foreach (var d in state.Registry.All().Where(d => d.State == "Approved"))
            {
                var r = await Relay.PanelAdminAsync(state, d.Code, "revoke-operator", "{}", TimeSpan.FromSeconds(15));
                if (r.Error == null) pushed++;
            }
            state.Audit.Write("panel/access", $"operator REVOKED by owner (devices notified: {pushed})", "ok");
            return Results.Json(new { operatorEnabled = false, devicesNotified = pushed }, EsoJson.Options);
        });

        app.MapPost("/panel/api/access/restore-operator", async (HttpContext ctx) =>
        {
            var s = Session(ctx); if (s == null) return NeedLogin();
            if (!CsrfOk(ctx, s)) return NeedCsrf();
            state.Registry.OperatorEnabled = true;
            foreach (var d in state.Registry.All().Where(d => d.State == "Approved"))
                if (state.Connections.TryGetValue(d.Code, out var conn))
                    await FleetConnectEndpoint.PushStateAsync(state, conn, d);
            state.Audit.Write("panel/access", "operator access restored by owner", "ok");
            return Results.Json(new { operatorEnabled = true }, EsoJson.Options);
        });

        app.MapPost("/panel/api/access/rotate-operator-key", async (HttpContext ctx) =>
        {
            var s = Session(ctx); if (s == null) return NeedLogin();
            if (!CsrfOk(ctx, s)) return NeedCsrf();
            var req = await ctx.Request.ReadFromJsonAsync<RotateOperatorKeyRequest>(EsoJson.Options);
            if (req == null || string.IsNullOrWhiteSpace(req.NewPublicKey))
                return Results.Json(new ErrorResponse { Error = "Missing newPublicKey." }, EsoJson.Options, statusCode: 400);
            try
            {
                using var probe = System.Security.Cryptography.ECDsa.Create();
                probe.ImportSubjectPublicKeyInfo(Convert.FromBase64String(req.NewPublicKey), out _);
            }
            catch { return Results.Json(new ErrorResponse { Error = "newPublicKey is not a valid ECDSA public key (base64 SPKI)." }, EsoJson.Options, statusCode: 400); }
            state.SetOperatorKey(req.NewPublicKey);
            int pushed = 0;
            if (state.Registry.OperatorEnabled)
            {
                var payload = JsonSerializer.Serialize(new { newPublicKey = req.NewPublicKey.Trim() }, EsoJson.Options);
                foreach (var d in state.Registry.All().Where(d => d.State == "Approved"))
                {
                    var r = await Relay.PanelAdminAsync(state, d.Code, "set-operator-key", payload, TimeSpan.FromSeconds(15));
                    if (r.Error == null) pushed++;
                }
            }
            state.Audit.Write("panel/access", $"operator key rotated by owner (devices notified: {pushed})", "ok");
            return Results.Json(new { rotated = true, devicesNotified = pushed }, EsoJson.Options);
        });

        app.MapPost("/panel/api/access/change-password", async (HttpContext ctx) =>
        {
            var s = Session(ctx); if (s == null) return NeedLogin();
            if (!CsrfOk(ctx, s)) return NeedCsrf();
            var body = await ctx.Request.ReadFromJsonAsync<ChangePasswordRequest>(EsoJson.Options);
            if (body == null || string.IsNullOrEmpty(body.NewPassword) || body.NewPassword.Length < 8)
                return Results.Json(new ErrorResponse { Error = "New password must be at least 8 characters." }, EsoJson.Options, statusCode: 400);
            if (!state.OwnerAuth.Verify(body.CurrentPassword ?? ""))
                return Results.Json(new ErrorResponse { Error = "Current password is wrong." }, EsoJson.Options, statusCode: 403);
            var record = OwnerPassword.Create(body.NewPassword);
            state.OwnerAuth.CarryTotpTo(record);
            OwnerPassword.SaveRecord(state.Config.ResolvePath("owner.json"), record);
            state.OwnerAuth.SetRecord(record);
            state.Audit.Write("panel/access", "owner password changed (fleet panel)", "ok");
            return Results.Json(new { changed = true }, EsoJson.Options);
        });
    }

    private static string LoginPageWithError(string html, string error)
        => html.Replace("<!--ERROR-->", $"<div class=\"error\">{System.Net.WebUtility.HtmlEncode(error)}</div>");

    private static string LoadResource(string name)
    {
        var asm = typeof(FleetPanel).Assembly;
        var full = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(name, StringComparison.Ordinal))
                   ?? throw new InvalidOperationException($"Embedded resource {name} not found.");
        using var stream = asm.GetManifestResourceStream(full)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
