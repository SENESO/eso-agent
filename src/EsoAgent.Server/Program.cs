using System.Security.Cryptography;
using EsoAgent.Core;
using EsoAgent.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// EsoAgent.Server — the fleet hub. Runs on the owner's always-on machine.
// Holds the device registry, keeps one outbound WebSocket per enrolled agent,
// relays OPERATOR-SIGNED commands (verifying signatures itself, and devices
// verify again — the relay cannot forge), and serves the owner's fleet panel.
//
// Access tiers (see README): OPERATOR (signed key) = full fleet control incl.
// operational management. OWNER (panel password) = operator + the reserved
// controls: revoke/restore the operator, rotate the operator key, change the
// owner password. Reserved actions exist ONLY in the owner panel path.

string? dataDir = null;
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] == "--data-dir") dataDir = args[i + 1];

var cfg = AgentConfig.Load(dataDir);
var dir = cfg.ResolvedDataDir;

// ----- CLI subcommands (local, owner-run; they edit the same files the server uses) -----
var sub = args.FirstOrDefault(a => !a.StartsWith("--") && a != dataDir);
if (sub != null)
{
    var registry = new FleetRegistry(dir);
    switch (sub)
    {
        case "set-owner-password":
        {
            var password = Environment.GetEnvironmentVariable("ESOAGENT_OWNER_PASSWORD") ?? Console.ReadLine();
            if (string.IsNullOrEmpty(password)) { Console.Error.WriteLine("No password provided."); return 1; }
            OwnerPassword.SaveRecord(cfg.ResolvePath("owner.json"), OwnerPassword.Create(password));
            Console.WriteLine("Owner password set.");
            return 0;
        }
        case "fingerprint":
        {
            using var cert = CertManager.LoadOrCreate(cfg);
            Console.WriteLine(CertManager.Fingerprint(cert));
            return 0;
        }
        case "--version":
        case "version":
            Console.WriteLine($"EsoAgent fleet server v{EsoAgent.Core.ProductInfo.Version}");
            return 0;
        case "disable-2fa":
        {
            // Local recovery (v1.2): the owner, on this machine, turns off the
            // TOTP second factor (e.g. authenticator lost). Local access only —
            // there is deliberately no remote path that does this.
            var store = new OwnerAuthStore();
            store.Load(cfg.ResolvePath("owner.json"));
            if (!store.IsConfigured) { Console.Error.WriteLine("No owner record found."); return 1; }
            store.DisableTotp();
            Console.WriteLine("Two-factor auth disabled. The owner can re-enroll from the panel.");
            return 0;
        }
        case "set-operator-key":
        {
            var pub = args.SkipWhile(a => a != "set-operator-key").Skip(1).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(pub)) { Console.Error.WriteLine("Usage: set-operator-key <base64-spki>"); return 1; }
            using var probe = ECDsa.Create();
            probe.ImportSubjectPublicKeyInfo(Convert.FromBase64String(pub), out _);
            File.WriteAllText(Path.Combine(dir, "operator.pub"), pub.Trim() + "\n");
            Console.WriteLine("Operator key set.");
            return 0;
        }
        case "approve":
        case "reject":
        {
            var code = args.SkipWhile(a => a != sub).Skip(1).FirstOrDefault();
            if (code == null || !registry.SetState(code, sub == "approve" ? "Approved" : "Rejected"))
            { Console.Error.WriteLine("Device not found."); return 1; }
            Console.WriteLine($"{code} -> {sub}d (takes effect immediately; connected devices are notified on their next state push — approve again from the panel, or the device reconnects, to push keys now).");
            return 0;
        }
        case "list":
        {
            foreach (var d in registry.All())
                Console.WriteLine($"{d.Code}  {d.State,-9}  {d.Os,-8}  {d.Name}  last seen {d.LastSeen:u}");
            return 0;
        }
        default:
            Console.Error.WriteLine($"Unknown subcommand '{sub}'.");
            return 1;
    }
}

// ----- server state -----
var state = new FleetServerState
{
    Config = cfg,
    Registry = new FleetRegistry(dir),
    Audit = new AuditLog(cfg.ResolvePath("logs/audit.log")),
    OwnerAuth = new OwnerAuthStore(),
};
state.OwnerAuth.Load(cfg.ResolvePath("owner.json"));
state.PanelKey = LoadOrCreatePanelKey(dir);
state.ReloadOperatorKey();
state.Queue = new CommandQueueStore(dir, state.Audit);
state.Activity = new ActivityStore(dir);
state.Activity.OnEvent = e => AlertWebhook.Fire(state, e);
state.Scripts = new ScriptStore(dir);
state.Rollouts = new RolloutStore(dir);
state.Packages = new UpdatePackageStore(dir);
state.Health = new HealthCache(dir);
state.Access = new AccessStore(dir);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions());
builder.Services.AddSingleton(state);
builder.Services.AddHostedService(_ => new UdpRelayService(state)); // DERP-style mesh relay (UDP; idles quietly where UDP is unavailable)
builder.Services.AddHostedService(_ => new HealthPollerService(state)); // v1.2: health snapshots + alert evaluation
builder.Services.AddHostedService(_ => new StateFrameResyncService(state)); // v1.2.8: re-push state frames so a frame lost to hibernation heals without a restart
var serverCert = CertManager.LoadOrCreate(cfg);
state.CertFingerprint = CertManager.Fingerprint(serverCert);
builder.WebHost.ConfigureKestrel(kestrel =>
{
    NetHelpers.ApplyRequestBodyLimit(kestrel); // rollout package upload carries ~41 MB packages (NetHelpers.MaxRequestBodyBytes)
    foreach (var addr in NetHelpers.ResolveBindAddresses(cfg))
        if (System.Net.IPAddress.TryParse(addr, out var ip))
            kestrel.Listen(ip, cfg.Port, lo => lo.UseHttps(serverCert));
});
builder.WebHost.UseUrls();

var app = builder.Build();
app.UseWebSockets();
app.MapFleetConnect(state);
app.MapFleetOperatorApi(state);
app.MapFleetOperatorApiV12(state);
app.MapFleetPanel(state);
app.MapFleetPanelV12(state);
FleetAccessApi.MapFleetAccess(app, state);
state.Audit.Write("server", $"fleet server started on port {cfg.Port}", "ok");
Console.WriteLine($"EsoAgent fleet server listening on port {cfg.Port} (tailnet + loopback). Data dir: {dir}");
app.Run();
return 0;

static ECDsa LoadOrCreatePanelKey(string dir)
{
    var path = Path.Combine(dir, "panel.key");
    var key = ECDsa.Create();
    if (File.Exists(path)) { key.ImportFromPem(File.ReadAllText(path)); return key; }
    key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    File.WriteAllText(path, key.ExportPkcs8PrivateKeyPem());
    try { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
    return key;
}

/// <summary>Shared state for the fleet server.</summary>
public sealed class FleetServerState
{
    public required AgentConfig Config { get; init; }
    public required FleetRegistry Registry { get; init; }
    public required AuditLog Audit { get; init; }
    public required OwnerAuthStore OwnerAuth { get; init; }
    public OwnerSessionStore Sessions { get; } = new();
    public LoginRateLimiter Limiter { get; } = new();
    public ECDsa PanelKey { get; set; } = null!;
    /// <summary>Offline command queue (v1.1), initialized at startup.</summary>
    public CommandQueueStore Queue { get; set; } = null!;
    // v1.2 stores, initialized at startup.
    public ActivityStore Activity { get; set; } = null!;
    public ScriptStore Scripts { get; set; } = null!;
    public RolloutStore Rollouts { get; set; } = null!;
    public UpdatePackageStore Packages { get; set; } = null!;
    public HealthCache Health { get; set; } = null!;
    public AccessStore Access { get; set; } = null!;
    public string PanelPublicKey => Convert.ToBase64String(PanelKey.ExportSubjectPublicKeyInfo());
    public string? OperatorPublicKey { get; private set; }
    public string CertFingerprint { get; set; } = "";
    public System.Collections.Concurrent.ConcurrentDictionary<string, DeviceConnection> Connections { get; } = new();
    /// <summary>Relayed tunnel streams open right now (live mirror for the panel; never persisted).</summary>
    public ActiveTunnelStore Tunnels { get; } = new();

    public void ReloadOperatorKey()
    {
        var path = Path.Combine(Config.ResolvedDataDir, "operator.pub");
        OperatorPublicKey = File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    public void SetOperatorKey(string base64Spki)
    {
        File.WriteAllText(Path.Combine(Config.ResolvedDataDir, "operator.pub"), base64Spki.Trim() + "\n");
        OperatorPublicKey = base64Spki.Trim();
    }
}
