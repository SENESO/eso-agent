using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EsoAgent.Core;

public sealed class ChangePasswordRequest
{
    public string? CurrentPassword { get; set; }
    public string? NewPassword { get; set; }
}

public sealed class TotpCodeRequest
{
    public string? Code { get; set; }
}

public sealed class PolicyUpdateRequest
{
    public CapabilityPolicy? Capabilities { get; set; }
    public DesktopPolicy? DesktopPolicy { get; set; }
}

/// <summary>Owner-dashboard schedule write bodies (Phase 4): the whole list
/// is replaced as one document, exactly like the operator API's PUT.</summary>
public sealed class SchedulesReplaceRequest
{
    public List<ScheduledTaskConfig>? Schedules { get; set; }
}

public sealed class ScheduleRunRequest
{
    public string Name { get; set; } = "";
}

/// <summary>Request body for the panel's Run page: either a direct exec or a shell script.</summary>
public sealed class PanelRunRequest
{
    public string Mode { get; set; } = "shell";   // "shell" | "exec"
    public string? Shell { get; set; }
    public string? Script { get; set; }
    public string? File { get; set; }
    public string[]? Args { get; set; }
    public int TimeoutMs { get; set; } = 120_000;
}

/// <summary>
/// The OWNER CONTROL PANEL — a web dashboard served by the agent itself on the
/// same tailnet+loopback HTTPS listener, implemented here in Core so every head
/// serves the identical panel. Embedded static HTML/CSS/JS (no npm, no external
/// assets). Auth is the OWNER PASSWORD (PBKDF2) + session cookie + CSRF — fully
/// separate from, and higher than, the API token:
///   * the panel is never blocked by the kill switch (it lifts it),
///   * token rotation and pause/resume exist ONLY here, never via the API.
/// Panel mutations are written to the audit log like everything else.
/// </summary>
public static class Dashboard
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

    public static void MapEsoPanel(this IEndpointRouteBuilder app, AgentState state)
    {
        var sessions = new OwnerSessionStore();
        state.PanelSessions = sessions;
        var pendingTotp = new PendingTotpStore();
        var limiter = new LoginRateLimiter();
        var loginHtml = LoadResource("DashboardLogin.html");
        var spaHtml = LoadResource("Dashboard.html");

        string LoginPage(HttpContext ctx, string? error)
        {
            var totpMode = pendingTotp.Validate(ctx.Request.Cookies[TotpCookieName]);
            var page = loginHtml.Replace("<!--FORM-->", totpMode ? TotpForm : PasswordForm);
            return error == null ? page : LoginPageWithError(page, error);
        }

        void StartSession(HttpContext ctx)
        {
            var session = sessions.Create();
            ctx.Response.Cookies.Append(CookieName, session.Id, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Strict,
                Path = "/panel",
                Expires = session.ExpiresAt,
            });
            ctx.Response.Cookies.Delete(TotpCookieName, new CookieOptions { Path = "/panel" });
        }

        OwnerSession? Session(HttpContext ctx)
            => sessions.Validate(ctx.Request.Cookies[CookieName]);

        IResult NeedLogin(HttpContext ctx)
            => Results.Json(new ErrorResponse { Error = "Owner login required." }, EsoJson.Options, statusCode: 401);

        bool CsrfOk(HttpContext ctx, OwnerSession s)
            => OwnerSessionStore.CheckCsrf(s, ctx.Request.Headers["X-CSRF"].ToString());

        IResult NeedCsrf()
            => Results.Json(new ErrorResponse { Error = "Missing or invalid CSRF token." }, EsoJson.Options, statusCode: 403);

        // ----- login / logout -----
        app.MapGet("/panel/login", (HttpContext ctx) => Results.Content(LoginPage(ctx, null), "text/html; charset=utf-8"));

        app.MapPost("/panel/login", async (HttpContext ctx) =>
        {
            if (!state.OwnerAuth.IsConfigured)
                return Results.Content(LoginPage(ctx, "Owner password is not set on this agent. Set it at install time (set-owner-password)."), "text/html; charset=utf-8", statusCode: 503);
            var source = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (limiter.IsLockedOut(source, out var remaining))
                return Results.Content(LoginPage(ctx, $"Too many failed attempts. Try again in {Math.Ceiling(remaining.TotalSeconds)}s."), "text/html; charset=utf-8", statusCode: 429);
            var form = await ctx.Request.ReadFormAsync();
            var password = form["password"].ToString();
            if (!state.OwnerAuth.Verify(password))
            {
                limiter.RecordFailure(source);
                state.Audit.Write("panel/login", $"failed owner login from {source}", "401");
                return Results.Content(LoginPage(ctx, "Wrong password."), "text/html; charset=utf-8", statusCode: 401);
            }
            limiter.RecordSuccess(source);
            // Second factor (v1.2): a correct password mints a 5-minute pre-auth
            // token; the session itself is only created after the TOTP step.
            if (state.OwnerAuth.TotpEnabled)
            {
                var token = pendingTotp.Create();
                ctx.Response.Cookies.Append(TotpCookieName, token, new CookieOptions
                {
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.Strict,
                    Path = "/panel",
                    Expires = DateTimeOffset.UtcNow.Add(PendingTotpStore.Lifetime),
                });
                state.Audit.Write("panel/login", $"owner password accepted from {source}; awaiting TOTP", "ok");
                return Results.Redirect("/panel/login");
            }
            StartSession(ctx);
            state.Audit.Write("panel/login", $"owner login from {source}", "ok");
            return Results.Redirect("/panel/");
        });

        app.MapPost("/panel/login/totp", async (HttpContext ctx) =>
        {
            var source = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var token = ctx.Request.Cookies[TotpCookieName];
            if (!pendingTotp.Validate(token))
                return Results.Redirect("/panel/login");
            if (limiter.IsLockedOut(source, out var remaining))
                return Results.Content(LoginPage(ctx, $"Too many failed attempts. Try again in {Math.Ceiling(remaining.TotalSeconds)}s."), "text/html; charset=utf-8", statusCode: 429);
            var form = await ctx.Request.ReadFormAsync();
            if (!state.OwnerAuth.VerifyTotp(form["code"].ToString()))
            {
                limiter.RecordFailure(source);
                state.Audit.Write("panel/login", $"failed TOTP from {source}", "401");
                return Results.Content(LoginPage(ctx, "Wrong code."), "text/html; charset=utf-8", statusCode: 401);
            }
            limiter.RecordSuccess(source);
            pendingTotp.Consume(token);
            StartSession(ctx);
            state.Audit.Write("panel/login", $"owner login (2FA) from {source}", "ok");
            return Results.Redirect("/panel/");
        });

        app.MapPost("/panel/logout", (HttpContext ctx) =>
        {
            sessions.Revoke(ctx.Request.Cookies[CookieName]);
            ctx.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/panel" });
            return Results.Redirect("/panel/login");
        });

        // One registration serves both /panel and /panel/ (endpoint routing
        // matches the trailing-slash variant too); registering both is ambiguous.
        app.MapGet("/panel", (HttpContext ctx) =>
            Session(ctx) == null ? Results.Redirect("/panel/login") : Results.Content(spaHtml, "text/html; charset=utf-8"));

        // ----- panel API (session + CSRF) -----
        app.MapGet("/panel/api/session", (HttpContext ctx) =>
        {
            var s = Session(ctx);
            if (s == null) return NeedLogin(ctx);
            return Results.Json(new { csrf = s.CsrfToken, locked = state.KillSwitch.IsLocked }, EsoJson.Options);
        });

        app.MapGet("/panel/api/status", async (HttpContext ctx) =>
        {
            if (Session(ctx) == null) return NeedLogin(ctx);
            return Results.Json(await Endpoints.BuildStatusAsync(state), EsoJson.Options);
        });

        app.MapGet("/panel/api/processes", (HttpContext ctx) =>
        {
            if (Session(ctx) == null) return NeedLogin(ctx);
            return Results.Json(ProcessListing.List(), EsoJson.Options);
        });

        app.MapPost("/panel/api/processes/{pid:int}/kill", (HttpContext ctx, int pid) =>
        {
            var s = Session(ctx); if (s == null) return NeedLogin(ctx);
            if (!CsrfOk(ctx, s)) return NeedCsrf();
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(pid);
                p.Kill(entireProcessTree: false);
                state.Audit.Write("panel/processes", $"owner panel killed pid {pid}", "ok");
                return Results.Json(new OperationResult { Success = true, Verified = true, Message = $"Killed pid {pid}." }, EsoJson.Options);
            }
            catch (Exception ex) { return Results.Json(new OperationResult { Success = false, Message = ex.Message }, EsoJson.Options, statusCode: 500); }
        });

        app.MapGet("/panel/api/software", async (HttpContext ctx) =>
        {
            if (Session(ctx) == null) return NeedLogin(ctx);
            return Results.Json(await state.Platform.Packages.ListAsync(), EsoJson.Options);
        });

        app.MapPost("/panel/api/software/install", async (HttpContext ctx) =>
        {
            var s = Session(ctx); if (s == null) return NeedLogin(ctx);
            if (!CsrfOk(ctx, s)) return NeedCsrf();
            var body = await ctx.Request.ReadFromJsonAsync<InstallRequest>(EsoJson.Options);
            if (body == null) return Results.Json(new ErrorResponse { Error = "Missing install request." }, EsoJson.Options, statusCode: 400);
            var result = await state.Platform.Packages.InstallAsync(body);
            state.Audit.Write("panel/software", $"install {body.Kind} {body.Id ?? body.Path}", result.Success ? "ok" : "failed");
            return Results.Json(result, EsoJson.Options);
        });

        app.MapPost("/panel/api/software/uninstall", async (HttpContext ctx) =>
        {
            var s = Session(ctx); if (s == null) return NeedLogin(ctx);
            if (!CsrfOk(ctx, s)) return NeedCsrf();
            var body = await ctx.Request.ReadFromJsonAsync<UninstallRequest>(EsoJson.Options);
            if (body == null) return Results.Json(new ErrorResponse { Error = "Missing uninstall request." }, EsoJson.Options, statusCode: 400);
            var result = await state.Platform.Packages.UninstallAsync(body);
            state.Audit.Write("panel/software", $"uninstall {body.Name}", result.Success ? "ok" : "failed");
            return Results.Json(result, EsoJson.Options);
        });

        app.MapPost("/panel/api/software/upgrade", async (HttpContext ctx) =>
        {
            var s = Session(ctx); if (s == null) return NeedLogin(ctx);
            if (!CsrfOk(ctx, s)) return NeedCsrf();
            var body = await ctx.Request.ReadFromJsonAsync<UpgradeRequest>(EsoJson.Options) ?? new UpgradeRequest();
            var result = await state.Platform.Packages.UpgradeAsync(body);
            state.Audit.Write("panel/software", $"upgrade {body.Id ?? "<all>"}", result.Success ? "ok" : "failed");
            return Results.Json(result, EsoJson.Options);
        });

        app.MapGet("/panel/api/services", async (HttpContext ctx) =>
        {
            if (Session(ctx) == null) return NeedLogin(ctx);
            return Results.Json(await state.Platform.Services.ListAsync(), EsoJson.Options);
        });

        foreach (var verb in new[] { "start", "stop", "restart" })
        {
            app.MapPost($"/panel/api/services/{{name}}/{verb}", async (HttpContext ctx, string name) =>
            {
                var s = Session(ctx); if (s == null) return NeedLogin(ctx);
                if (!CsrfOk(ctx, s)) return NeedCsrf();
                var result = verb switch
                {
                    "start" => await state.Platform.Services.StartAsync(name),
                    "stop" => await state.Platform.Services.StopAsync(name),
                    _ => await state.Platform.Services.RestartAsync(name),
                };
                state.Audit.Write("panel/services", $"{verb} {name}", result.Success ? "ok" : "failed");
                return Results.Json(result, EsoJson.Options);
            });
        }

        app.MapPost("/panel/api/run", async (HttpContext ctx) =>
        {
            var s = Session(ctx); if (s == null) return NeedLogin(ctx);
            if (!CsrfOk(ctx, s)) return NeedCsrf();
            var body = await ctx.Request.ReadFromJsonAsync<PanelRunRequest>(EsoJson.Options);
            if (body == null) return Results.Json(new ErrorResponse { Error = "Missing run request." }, EsoJson.Options, statusCode: 400);
            try
            {
                ExecResponse result;
                if (body.Mode == "exec")
                {
                    if (string.IsNullOrWhiteSpace(body.File)) return Results.Json(new ErrorResponse { Error = "Missing 'file'." }, EsoJson.Options, statusCode: 400);
                    result = await state.Platform.ProcessRunner.RunAsync(new ExecRequest
                    { File = body.File, Args = body.Args ?? Array.Empty<string>(), TimeoutMs = body.TimeoutMs });
                }
                else
                {
                    result = await state.Platform.ProcessRunner.RunAsync(ProcessRunner.ResolveShell(new ShellRequest
                    { Shell = body.Shell ?? "default", Script = body.Script ?? "", TimeoutMs = body.TimeoutMs }));
                }
                state.Audit.Write("panel/run", body.Mode == "exec" ? $"exec {body.File}" : $"shell {body.Shell}", $"exit {result.ExitCode}");
                return Results.Json(result, EsoJson.Options);
            }
            catch (Exception ex) { return Results.Json(new ErrorResponse { Error = ex.Message }, EsoJson.Options, statusCode: 400); }
        });

        // ----- files -----
        app.MapGet("/panel/api/files/list", (HttpContext ctx, string? path) =>
        {
            if (Session(ctx) == null) return NeedLogin(ctx);
            var dir = string.IsNullOrWhiteSpace(path)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : path;
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                return Results.Json(new ErrorResponse { Error = $"Directory not found: {dir}" }, EsoJson.Options, statusCode: 404);
            var entries = new List<object>();
            var parent = Directory.GetParent(dir)?.FullName;
            foreach (var d in Directory.EnumerateDirectories(dir))
            {
                try { entries.Add(new { name = Path.GetFileName(d), path = d, isDir = true, size = (long?)null }); } catch { }
            }
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                try { var fi = new FileInfo(f); entries.Add(new { name = fi.Name, path = f, isDir = false, size = (long?)fi.Length }); } catch { }
            }
            return Results.Json(new { path = dir, parent, entries }, EsoJson.Options);
        });

        app.MapGet("/panel/api/files/download", async (HttpContext ctx, string path) =>
        {
            if (Session(ctx) == null) return NeedLogin(ctx);
            if (!File.Exists(path)) return Results.NotFound();
            ctx.Response.ContentType = "application/octet-stream";
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{Path.GetFileName(path)}\"";
            await using var fs = File.OpenRead(path);
            await fs.CopyToAsync(ctx.Response.Body);
            return Results.Empty;
        });

        app.MapPost("/panel/api/files/begin", async (HttpContext ctx) =>
        {
            var s = Session(ctx); if (s == null) return NeedLogin(ctx);
            if (!CsrfOk(ctx, s)) return NeedCsrf();
            var body = await ctx.Request.ReadFromJsonAsync<BeginUploadRequest>(EsoJson.Options);
            if (body == null) return Results.Json(new ErrorResponse { Error = "Missing path." }, EsoJson.Options, statusCode: 400);
            return Results.Json(new BeginUploadResponse { Received = state.Files.BeginUpload(body.Path) }, EsoJson.Options);
        });

        app.MapPut("/panel/api/files/chunk", async (HttpContext ctx) =>
        {
            var s = Session(ctx); if (s == null) return NeedLogin(ctx);
            if (!CsrfOk(ctx, s)) return NeedCsrf();
            var path = ctx.Request.Query["path"].ToString();
            if (string.IsNullOrWhiteSpace(path) || !long.TryParse(ctx.Request.Query["offset"].ToString(), out var offset))
                return Results.Json(new ErrorResponse { Error = "Need ?path= and ?offset=." }, EsoJson.Options, statusCode: 400);
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms);
            try { return Results.Json(new ChunkResponse { Received = state.Files.AppendChunk(path, offset, ms.ToArray()) }, EsoJson.Options); }
            catch (ChunkOffsetException ex) { return Results.Json(new ErrorResponse { Error = ex.Message }, EsoJson.Options, statusCode: 409); }
        });

        app.MapPost("/panel/api/files/commit", async (HttpContext ctx) =>
        {
            var s = Session(ctx); if (s == null) return NeedLogin(ctx);
            if (!CsrfOk(ctx, s)) return NeedCsrf();
            var body = await ctx.Request.ReadFromJsonAsync<CommitUploadRequest>(EsoJson.Options);
            if (body == null) return Results.Json(new ErrorResponse { Error = "Missing commit request." }, EsoJson.Options, statusCode: 400);
            try
            {
                var hash = state.Files.CommitUpload(body.Path, body.Sha256);
                state.Audit.Write("panel/files", $"upload committed {body.Path}", "ok");
                return Results.Json(new FileInfoResponse { Exists = true, Size = new FileInfo(body.Path).Length, Sha256 = hash }, EsoJson.Options);
            }
            catch (Exception ex) { return Results.Json(new ErrorResponse { Error = ex.Message }, EsoJson.Options, statusCode: 400); }
        });

        // ----- audit log -----
        app.MapGet("/panel/api/logs", (HttpContext ctx, int? lines, string? filter) =>
        {
            if (Session(ctx) == null) return NeedLogin(ctx);
            var tail = state.Audit.Tail(lines ?? 200);
            if (!string.IsNullOrWhiteSpace(filter))
                tail = tail.Where(l => l.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
            return Results.Json(new { lines = tail }, EsoJson.Options);
        });

        // ----- session recordings (v1.2) -----
        app.MapGet("/panel/api/recordings", (HttpContext ctx) =>
        {
            if (Session(ctx) == null) return NeedLogin(ctx);
            return Results.Json(SessionRecorder.List(state.RecordingsDir), EsoJson.Options);
        });

        app.MapGet("/panel/api/recordings/{name}", async (HttpContext ctx, string name) =>
        {
            if (Session(ctx) == null) return NeedLogin(ctx);
            if (name.Contains('/') || name.Contains('\\') || !name.EndsWith(".esorec", StringComparison.OrdinalIgnoreCase))
                return Results.NotFound();
            var full = Path.Combine(state.RecordingsDir, name);
            if (!File.Exists(full)) return Results.NotFound();
            ctx.Response.ContentType = "application/octet-stream";
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{name}\"";
            await using var fs = File.OpenRead(full);
            await fs.CopyToAsync(ctx.Response.Body);
            return Results.Empty;
        });

        app.MapPost("/panel/api/recordings/{name}/delete", (HttpContext ctx, string name) =>
        {
            var s = Session(ctx); if (s == null) return NeedLogin(ctx);
            if (!CsrfOk(ctx, s)) return NeedCsrf();
            if (name.Contains('/') || name.Contains('\\') || !name.EndsWith(".esorec", StringComparison.OrdinalIgnoreCase))
                return Results.Json(new ErrorResponse { Error = "Bad recording name." }, EsoJson.Options, statusCode: 400);
            var full = Path.Combine(state.RecordingsDir, name);
            if (!File.Exists(full)) return Results.Json(new ErrorResponse { Error = "Recording not found." }, EsoJson.Options, statusCode: 404);
            File.Delete(full);
            state.Audit.Write("panel/recordings", $"owner deleted recording {name}", "ok");
            return Results.Json(new { deleted = name }, EsoJson.Options);
        });

        // ----- device scheduled tasks (owner path; same write rules as the API) -----
        app.MapGet("/panel/api/schedules", (HttpContext ctx) =>
        {
            if (Session(ctx) == null) return NeedLogin(ctx);
            return Results.Json(new
            {
                configs = state.Scheduler?.Configs() ?? new List<ScheduledTaskConfig>(),
                states = state.Scheduler?.Snapshot() ?? new List<ScheduleStateDto>(),
            }, EsoJson.Options);
        });

        app.MapPost("/panel/api/schedules", async (HttpContext ctx) =>
        {
            var s = Session(ctx); if (s == null) return NeedLogin(ctx);
            if (!CsrfOk(ctx, s)) return NeedCsrf();
            if (state.Scheduler == null) return Results.Json(new ErrorResponse { Error = "Schedule service not running." }, EsoJson.Options, statusCode: 503);
            var body = await ctx.Request.ReadFromJsonAsync<SchedulesReplaceRequest>(EsoJson.Options);
            if (body?.Schedules == null) return Results.Json(new ErrorResponse { Error = "Bad schedule list." }, EsoJson.Options, statusCode: 400);
            if (ScheduleValidation.ValidateList(body.Schedules) is { } error)
                return Results.Json(new ErrorResponse { Error = error }, EsoJson.Options, statusCode: 400);
            state.Config.Schedules = body.Schedules;
            state.Scheduler.ReplaceSchedules(body.Schedules);
            SaveConfig(state);
            state.Audit.Write("panel/schedules", $"schedules replaced ({body.Schedules.Count} task(s)) from the owner dashboard", "ok");
            return Results.Json(new { saved = body.Schedules.Count }, EsoJson.Options);
        });

        app.MapPost("/panel/api/schedules/run", async (HttpContext ctx) =>
        {
            var s = Session(ctx); if (s == null) return NeedLogin(ctx);
            if (!CsrfOk(ctx, s)) return NeedCsrf();
            if (state.Scheduler == null) return Results.Json(new ErrorResponse { Error = "Schedule service not running." }, EsoJson.Options, statusCode: 503);
            var body = await ctx.Request.ReadFromJsonAsync<ScheduleRunRequest>(EsoJson.Options);
            if (body == null || string.IsNullOrWhiteSpace(body.Name))
                return Results.Json(new ErrorResponse { Error = "Bad run request." }, EsoJson.Options, statusCode: 400);
            if (!await state.Scheduler.RunNowAsync(body.Name))
                return Results.Json(new ErrorResponse { Error = $"No idle schedule named '{body.Name}'." }, EsoJson.Options, statusCode: 404);
            state.Audit.Write("panel/schedules", $"schedule '{body.Name}' run on demand from the owner dashboard", "ok");
            return Results.Json(new { started = body.Name }, EsoJson.Options);
        });

        // Remote desktop (owner path): session-cookie auth, same runner as /api/desktop.
        app.Map("/panel/desktop", async (HttpContext ctx) =>
        {
            if (Session(ctx) == null) { ctx.Response.StatusCode = 401; return; }
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await DesktopStreamRunner.RunAsync(ws, state, "panel", ctx.RequestAborted);
        });

        // ----- access: the owner's master controls (owner-only, never via API token) -----
        app.MapGet("/panel/api/access", (HttpContext ctx) =>
        {
            if (Session(ctx) == null) return NeedLogin(ctx);
            return Results.Json(new
            {
                locked = state.KillSwitch.IsLocked,
                fingerprint = state.CertFingerprint,
                version = ProductInfo.Version,
                os = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux",
                totpEnabled = state.OwnerAuth.TotpEnabled,
                capabilities = state.Config.Capabilities,
                desktopPolicy = state.Config.DesktopPolicy,
            }, EsoJson.Options);
        });

        // ----- two-factor auth (v1.2): enrollment is owner-only, from this panel -----
        app.MapPost("/panel/api/access/2fa/begin", (HttpContext ctx) =>
        {
            var s = Session(ctx); if (s == null) return NeedLogin(ctx);
            if (!CsrfOk(ctx, s)) return NeedCsrf();
            var secret = state.OwnerAuth.BeginTotpEnrollment();
            state.Audit.Write("panel/access", "owner started 2FA enrollment", "ok");
            return Results.Json(new { secret, otpauthUri = Totp.OtpauthUri(secret, state.Config.ResolvedDeviceName) }, EsoJson.Options);
        });

        app.MapPost("/panel/api/access/2fa/confirm", async (HttpContext ctx) =>
        {
            var s = Session(ctx); if (s == null) return NeedLogin(ctx);
            if (!CsrfOk(ctx, s)) return NeedCsrf();
            var body = await ctx.Request.ReadFromJsonAsync<TotpCodeRequest>(EsoJson.Options);
            if (body == null || !state.OwnerAuth.ConfirmTotpEnrollment(body.Code))
                return Results.Json(new ErrorResponse { Error = "That code did not match — check the authenticator entry and try again." }, EsoJson.Options, statusCode: 400);
            state.Audit.Write("panel/access", "owner ENABLED 2FA (TOTP)", "ok");
            return Results.Json(new { enabled = true }, EsoJson.Options);
        });

        app.MapPost("/panel/api/access/2fa/disable", async (HttpContext ctx) =>
        {
            var s = Session(ctx); if (s == null) return NeedLogin(ctx);
            if (!CsrfOk(ctx, s)) return NeedCsrf();
            var body = await ctx.Request.ReadFromJsonAsync<ChangePasswordRequest>(EsoJson.Options);
            if (body == null || !state.OwnerAuth.Verify(body.CurrentPassword ?? ""))
                return Results.Json(new ErrorResponse { Error = "Current password is wrong." }, EsoJson.Options, statusCode: 403);
            state.OwnerAuth.DisableTotp();
            state.Audit.Write("panel/access", "owner DISABLED 2FA", "ok");
            return Results.Json(new { enabled = false }, EsoJson.Options);
        });

        // ----- owner policy (v1.2): capability toggles + desktop consent -----
        app.MapPost("/panel/api/access/policy", async (HttpContext ctx) =>
        {
            var s = Session(ctx); if (s == null) return NeedLogin(ctx);
            if (!CsrfOk(ctx, s)) return NeedCsrf();
            var body = await ctx.Request.ReadFromJsonAsync<PolicyUpdateRequest>(EsoJson.Options);
            if (body == null) return Results.Json(new ErrorResponse { Error = "Missing policy." }, EsoJson.Options, statusCode: 400);
            if (body.Capabilities != null) state.Config.Capabilities = body.Capabilities;
            if (body.DesktopPolicy != null) state.Config.DesktopPolicy = body.DesktopPolicy;
            SaveConfig(state);
            state.Audit.Write("panel/access",
                $"owner policy updated: files={state.Config.Capabilities.Files} clipboard={state.Config.Capabilities.Clipboard} tunnels={state.Config.Capabilities.Tunnels} desktopControl={state.Config.Capabilities.DesktopControl} controlConsent={state.Config.DesktopPolicy.ControlConsent} viewConsent={state.Config.DesktopPolicy.ViewConsent}",
                "ok");
            return Results.Json(new { capabilities = state.Config.Capabilities, desktopPolicy = state.Config.DesktopPolicy }, EsoJson.Options);
        });

        app.MapPost("/panel/api/access/pause", (HttpContext ctx) =>
        {
            var s = Session(ctx); if (s == null) return NeedLogin(ctx);
            if (!CsrfOk(ctx, s)) return NeedCsrf();
            state.KillSwitch.Lock();
            state.Audit.Write("panel/access", "owner PAUSED api access (kill switch on)", "ok");
            return Results.Json(new { locked = true }, EsoJson.Options);
        });

        app.MapPost("/panel/api/access/resume", (HttpContext ctx) =>
        {
            var s = Session(ctx); if (s == null) return NeedLogin(ctx);
            if (!CsrfOk(ctx, s)) return NeedCsrf();
            state.KillSwitch.Unlock();
            state.Audit.Write("panel/access", "owner RESUMED api access (kill switch off)", "ok");
            return Results.Json(new { locked = false }, EsoJson.Options);
        });

        app.MapPost("/panel/api/access/rotate-token", (HttpContext ctx) =>
        {
            var s = Session(ctx); if (s == null) return NeedLogin(ctx);
            if (!CsrfOk(ctx, s)) return NeedCsrf();
            var token = state.RotateToken();
            // Returned exactly once, to the owner, over the panel's TLS session.
            return Results.Json(new { token }, EsoJson.Options);
        });

        // Owner-reserved: change the owner password itself. Requires the current
        // password; exists ONLY in this owner-authenticated panel path.
        app.MapPost("/panel/api/access/change-password", async (HttpContext ctx) =>
        {
            var s = Session(ctx); if (s == null) return NeedLogin(ctx);
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
            state.Audit.Write("panel/access", "owner password changed", "ok");
            return Results.Json(new { changed = true }, EsoJson.Options);
        });
    }

    /// <summary>Persists the agent config (owner policy changes) back to
    /// esoagent.json in the data dir. All config sections are modeled types,
    /// so the round-trip preserves them.</summary>
    internal static void SaveConfig(AgentState state)
    {
        var path = Path.Combine(state.Config.ResolvedDataDir, "esoagent.json");
        var json = System.Text.Json.JsonSerializer.Serialize(state.Config,
            new System.Text.Json.JsonSerializerOptions(EsoJson.Options) { WriteIndented = true });
        StateFile.WriteText(path, json + "\n");
    }

    private static string LoginPageWithError(string html, string error)
        => html.Replace("<!--ERROR-->", $"<div class=\"error\">{System.Net.WebUtility.HtmlEncode(error)}</div>");

    private static string LoadResource(string name)
    {
        var asm = typeof(Dashboard).Assembly;
        var full = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(name, StringComparison.Ordinal))
                   ?? throw new InvalidOperationException($"Embedded resource {name} not found.");
        using var stream = asm.GetManifestResourceStream(full)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
