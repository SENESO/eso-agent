using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EsoAgent.Core;

/// <summary>Shared runtime state for one agent instance.</summary>
public sealed class AgentState
{
    public required AgentConfig Config { get; init; }
    private byte[] _token = Array.Empty<byte>();

    /// <summary>The current API token. Rotatable at runtime by the OWNER PANEL only —
    /// rotation swaps this reference and rewrites the token file atomically, so the
    /// old token dies instantly. Never exposed through the API itself.</summary>
    public byte[] Token { get => _token; init => _token = value; }

    public required AuditLog Audit { get; init; }
    public required KillSwitch KillSwitch { get; init; }
    public required PlatformServices Platform { get; init; }
    public required FileTransferService Files { get; init; }
    public required UpdateService Update { get; init; }
    public OwnerAuthStore OwnerAuth { get; init; } = new();
    public DeviceIdentity? Device { get; set; }
    public volatile bool FleetConnected;
    /// <summary>Durable jobs (v1.1), created by EsoAgentServer.Build.</summary>
    public JobService? Jobs { get; set; }
    /// <summary>Supervision (v1.1), created by EsoAgentServer.Build.</summary>
    public SupervisorService? Supervisor { get; set; }
    /// <summary>Device scheduled tasks (v1.2), created by EsoAgentServer.Build.</summary>
    public ScheduleService? Scheduler { get; set; }
    /// <summary>Persistent shell sessions (v1.2), created by EsoAgentServer.Build.</summary>
    public ShellSessionService? ShellSessions { get; set; }
    /// <summary>The device panel's owner session store (set by the dashboard
    /// at startup) — lets the tray show the owner's sign-in state.</summary>
    public OwnerSessionStore? PanelSessions { get; set; }
    /// <summary>File-change watches (v1.2), created by EsoAgentServer.Build.</summary>
    public WatchService? Watches { get; set; }

    /// <summary>Delta-perception baselines (v1.2 item 37): the last full
    /// payload per target, so repeat reads ship only structural diffs.</summary>
    public DeltaStore Deltas { get; } = new();
    public ReleaseUpdateState ReleaseUpdates { get; } = new();
    public ReleaseUpdateService? Release { get; set; }
    /// <summary>Trusted signer keys as last delivered by the fleet server's state
    /// frame (v1.2): the update-fetch endpoint verifies rollout signatures
    /// against these. Null until the device is approved.</summary>
    public string? OperatorPublicKeySpki { get; set; }
    public string? PanelPublicKeySpki { get; set; }
    /// <summary>Fleet enrollment state as last reported by a state frame
    /// (Unknown | Pending | Approved | Rejected). The P2P trust gate reads it.</summary>
    public volatile string FleetEnrollmentState = "Unknown";
    /// <summary>The approved-peer set for the P2P gate: device code → base64 SPKI,
    /// as delivered in the state frame (and persisted with the signer keys so a
    /// rebooted device keeps its last set until the next frame). Replaced
    /// atomically per state frame; readers take the snapshot.</summary>
    public volatile IReadOnlyDictionary<string, string> ApprovedPeers =
        new Dictionary<string, string>();
    /// <summary>P2P status line for /api/status (set by the P2P node service).</summary>
    public string P2pStatus { get; set; } = "p2p disabled";
    /// <summary>The live P2P node, once the node service has started it.</summary>
    public P2pNode? P2p { get; set; }
    /// <summary>Desktop session recordings (.esorec) live here (v1.2).</summary>
    public string RecordingsDir => Config.ResolvePath("recordings");
    /// <summary>Latest clock-skew sample vs the fleet server (server − local, seconds),
    /// taken when a fleet state frame arrives (v1.1, doctor check).</summary>
    public double? ServerSkewSeconds { get; set; }
    public DateTimeOffset? ServerSkewSampledAt { get; set; }
    public string CertFingerprint { get; set; } = "";
    public string AppDir { get; init; } = AppContext.BaseDirectory;
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? LastCommandSummary { get; set; }
    /// <summary>Mesh status line for /api/status (set by the mesh applier).</summary>
    public string MeshStatus { get; set; } = "mesh disabled";
    /// <summary>This device's mesh virtual IP once assigned by the fleet server.</summary>
    public string? MeshVirtualIp { get; set; }

    /// <summary>Generates a fresh API token, persists it, and activates it. Returns the base64 token (shown to the owner once).</summary>
    public string RotateToken()
    {
        var fresh = HmacAuth.GenerateToken();
        var path = Config.ResolvePath(Config.TokenFile);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, Convert.ToBase64String(fresh) + "\n");
        File.Move(tmp, path, overwrite: true);
        _token = fresh;
        Audit.Write("panel/access", "API token rotated by owner panel (old token invalidated)", "ok");
        return Convert.ToBase64String(fresh);
    }
}

/// <summary>
/// Builds the Kestrel host shared by every head (Windows service, Linux daemon,
/// future macOS launchd). Binding is tailnet + loopback only, TLS with the pinned
/// self-signed cert, HMAC auth on every request, kill-switch gate, audit logging.
/// </summary>
public static class EsoAgentServer
{
    public static WebApplication Build(AgentConfig cfg, PlatformServices platform, Action<WebApplicationBuilder>? configureHost = null, Action<AgentState>? stateReady = null)
    {
        var token = HmacAuth.LoadTokenFile(cfg.ResolvePath(cfg.TokenFile));
        var state = new AgentState
        {
            Config = cfg,
            Token = token,
            Audit = new AuditLog(cfg.ResolvePath(cfg.AuditLogFile)),
            KillSwitch = new KillSwitch(cfg.ResolvePath(cfg.LockFile)),
            Platform = platform,
            Files = new FileTransferService(),
            Update = new UpdateService(cfg, platform.ProcessRunner),
        };

        state.OwnerAuth.Load(cfg.ResolvePath("owner.json"));
        state.Device = DeviceIdentity.LoadOrCreate(cfg);

        // Durable jobs + supervision (v1.1). Job closeout runs NOW, before any
        // traffic: jobs a previous lifetime left "running" become interrupted.
        var jobs = new JobService(cfg.ResolvedDataDir, state.Audit);
        jobs.CloseOutOnStartup();
        state.Jobs = jobs;
        var supervisor = new SupervisorService(state);
        state.Supervisor = supervisor;
        var scheduler = new ScheduleService(state);
        state.Scheduler = scheduler;
        state.ShellSessions = new ShellSessionService(state);
        state.Watches = new WatchService(state);

        stateReady?.Invoke(state);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions());
        builder.Services.AddSingleton(state);
        builder.Services.AddHostedService(_ => supervisor);
        builder.Services.AddHostedService(_ => scheduler);
        builder.Services.AddHostedService(_ => new TelemetryWatchService(state));
        builder.Services.AddHostedService(_ => new CrashWatchService(state));
        builder.Services.AddHostedService(_ => new WindowWatchService(state));
        var release = new ReleaseUpdateService(state);
        state.Release = release;
        builder.Services.AddHostedService(_ => release);
        if (cfg.Fleet is { Enabled: true } && !string.IsNullOrWhiteSpace(cfg.Fleet.ServerUrl))
            builder.Services.AddHostedService(_ => new FleetClientService(state));
        if (cfg.P2p is { Enabled: true } && cfg.P2p.ListenPort > 0)
            builder.Services.AddHostedService(_ => new P2pNodeService(state));
        configureHost?.Invoke(builder);

        var cert = CertManager.LoadOrCreate(cfg);
        state.CertFingerprint = CertManager.Fingerprint(cert);
        // Mesh VIP (§27): the applier persists the assigned VIP in
        // mesh.json on every frame; bind the API to it now, before the
        // first frame of this lifetime arrives — Kestrel binds once at
        // host build, and a VIP assigned mid-lifetime binds at the next
        // agent restart (the applier audits that when it happens).
        // Clients pin the cert by fingerprint, so no SAN change is needed.
        var meshVip = MeshFrameStore.TryReadVip(cfg.DataDir);
        if (meshVip != null) state.MeshVirtualIp = meshVip;
        var bindAddresses = NetHelpers.ResolveBindAddresses(cfg, meshVip != null ? new[] { meshVip } : null);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            NetHelpers.ApplyRequestBodyLimit(kestrel); // PUT /api/update/package carries ~41 MB packages (NetHelpers.MaxRequestBodyBytes)
            foreach (var addr in bindAddresses)
            {
                if (IPAddress.TryParse(addr, out var ip))
                    kestrel.Listen(ip, cfg.Port, lo => lo.UseHttps(cert));
            }
        });
        builder.WebHost.UseUrls(); // clear any default urls; Kestrel.Listen above is authoritative

        var app = builder.Build();
        app.UseWebSockets();
        app.Use(async (context, next) =>
        {
            // The owner panel (/panel) has its OWN auth (owner password + session
            // cookie + CSRF) and outranks the API token: it is not HMAC-gated and
            // is NOT blocked by the kill switch — the panel is how the owner
            // lifts the pause and rotates the token.
            if (context.Request.Path.StartsWithSegments("/panel"))
            {
                await next(context);
                return;
            }
            var started = DateTimeOffset.UtcNow;
            var ok = await AuthenticateAsync(context, state);
            if (!ok)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new ErrorResponse { Error = "Unauthorized: bad or missing HMAC signature." }, EsoJson.Options);
                state.Audit.Write(context.Request.Path, "auth-failed", "401");
                return;
            }
            if (state.KillSwitch.IsLocked && !context.Request.Path.StartsWithSegments("/api/status"))
            {
                context.Response.StatusCode = 423;
                await context.Response.WriteAsJsonAsync(new ErrorResponse { Error = "Agent is locked by the owner's kill switch. Unlock locally on the machine." }, EsoJson.Options);
                state.Audit.Write(context.Request.Path, "refused-while-locked", "423");
                return;
            }
            // Capability policy (v1.2): owner toggles enforced here so EVERY
            // caller path — direct, fleet relay (which dispatches through this
            // same loopback pipeline) and the CLI/app — is gated identically.
            // Desktop control + clipboard are gated inside the desktop runner.
            var capabilityOff =
                (context.Request.Path.StartsWithSegments("/api/files") && !state.Config.Capabilities.Files) ? "file transfer" :
                (context.Request.Path.StartsWithSegments("/api/tunnel") && !state.Config.Capabilities.Tunnels) ? "tunnels" : null;
            if (capabilityOff != null)
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsJsonAsync(new ErrorResponse { Error = $"The owner has disabled {capabilityOff} on this device (capability policy)." }, EsoJson.Options);
                state.Audit.Write(context.Request.Path, $"refused: {capabilityOff} capability off", "403");
                return;
            }
            await next(context);
            var summary = $"{context.Request.Method} {context.Request.Path}";
            state.LastCommandSummary = summary;
            state.Audit.Write(context.Request.Path, summary,
                $"{context.Response.StatusCode} in {(DateTimeOffset.UtcNow - started).TotalMilliseconds:F0}ms");
        });
        app.MapEsoAgentEndpoints(state);
        app.MapOperatorPack(state);
        app.MapEsoPanel(state);
        return app;
    }

    private static async Task<bool> AuthenticateAsync(HttpContext context, AgentState state)
    {
        var req = context.Request;
        if (!req.Headers.TryGetValue(HmacAuth.TimestampHeader, out var tsRaw) ||
            !long.TryParse(tsRaw.ToString(), out var ts)) return false;
        if (!req.Headers.TryGetValue(HmacAuth.SignatureHeader, out var sig)) return false;

        var pathAndQuery = req.Path + req.QueryString;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // The update-package upload is streamed (too large to buffer for the body
        // hash); its signature instead covers the declared package hash, and the
        // handler verifies the real bytes against that hash before accepting.
        if (req.Path.StartsWithSegments("/api/update/package"))
        {
            if (!req.Headers.TryGetValue("X-Package-Sha256", out var pkgHash)) return false;
            var pseudoBody = System.Text.Encoding.UTF8.GetBytes(pkgHash.ToString().Trim().ToLowerInvariant());
            return HmacAuth.Verify(state.Token, req.Method, pathAndQuery, ts, pseudoBody, sig.ToString(), now);
        }

        req.EnableBuffering();
        byte[] body;
        using (var ms = new MemoryStream())
        {
            await req.Body.CopyToAsync(ms);
            body = ms.ToArray();
        }
        req.Body.Position = 0;
        return HmacAuth.Verify(state.Token, req.Method, pathAndQuery, ts, body, sig.ToString(), now);
    }
}
