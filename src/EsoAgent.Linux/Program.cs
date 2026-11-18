using EsoAgent.Core;
using EsoAgent.Linux;
using Microsoft.Extensions.Hosting;

// EsoAgent Linux head: a systemd daemon around EsoAgent.Core.
//
// Local control subcommands (handled BEFORE any host is built — they are the
// owner's on-machine controls and must work even while the daemon is stopped):
//   esoagent disable            create the kill-switch lock file (API then refuses commands)
//   esoagent enable             remove the kill-switch lock file
//   esoagent fingerprint        print the TLS certificate's SHA-256 fingerprint (for client pinning)
//   esoagent set-owner-password set the control-panel owner password (from
//                               $ESOAGENT_OWNER_PASSWORD, or one line read from stdin)
// --data-dir <dir> may appear anywhere in the argument list.

string? dataDir = null;
string? subcommand = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--data-dir" && i + 1 < args.Length)
        dataDir = args[++i];
    else if (!args[i].StartsWith("--", StringComparison.Ordinal))
        subcommand ??= args[i];
}

if (subcommand is "disable" or "enable")
{
    var localCfg = AgentConfig.Load(dataDir);
    var killSwitch = new KillSwitch(localCfg.ResolvePath(localCfg.LockFile));
    if (subcommand == "disable")
    {
        killSwitch.Lock();
        Console.WriteLine($"EsoAgent disabled. Lock file created: {localCfg.ResolvePath(localCfg.LockFile)}");
    }
    else
    {
        killSwitch.Unlock();
        Console.WriteLine($"EsoAgent enabled. Lock file removed: {localCfg.ResolvePath(localCfg.LockFile)}");
    }
    return;
}

if (subcommand == "set-owner-password")
{
    var localCfg = AgentConfig.Load(dataDir);
    // install.sh passes the password via the environment; interactively we read
    // one line from stdin. The password itself is never printed or logged —
    // only its PBKDF2 record (salt + hash) lands in owner.json.
    var password = Environment.GetEnvironmentVariable("ESOAGENT_OWNER_PASSWORD");
    if (string.IsNullOrEmpty(password))
        password = Console.In.ReadLine();
    if (string.IsNullOrEmpty(password))
    {
        Console.Error.WriteLine("No owner password provided (set ESOAGENT_OWNER_PASSWORD or pipe one line on stdin).");
        return;
    }
    var rec = OwnerPassword.Create(password);
    OwnerPassword.SaveRecord(localCfg.ResolvePath("owner.json"), rec);
    Console.WriteLine("Owner password set.");
    return;
}

if (subcommand == "disable-2fa")
{
    // Local recovery (v1.2): the owner, on this machine, turns off the TOTP
    // second factor (e.g. authenticator lost). Local access only — there is
    // deliberately no remote path that does this.
    var localCfg = AgentConfig.Load(dataDir);
    var store = new OwnerAuthStore();
    store.Load(localCfg.ResolvePath("owner.json"));
    if (!store.IsConfigured)
    {
        Console.Error.WriteLine("No owner record found.");
        return;
    }
    store.DisableTotp();
    Console.WriteLine("Two-factor auth disabled. The owner can re-enroll from the panel.");
    return;
}

if (subcommand == "fingerprint")
{
    var localCfg = AgentConfig.Load(dataDir);
    var cert = CertManager.LoadOrCreate(localCfg);
    Console.WriteLine(CertManager.Fingerprint(cert));
    return;
}

var cfg = AgentConfig.Load(dataDir);
var runner = new ProcessRunner();
var platform = new PlatformServices
{
    ProcessRunner = runner,
    SystemInfo = new LinuxSystemInfo(runner),
    PowerControl = new LinuxPowerControl(runner),
    Screenshot = new LinuxScreenshot(runner),
    InteractiveExec = new LinuxInteractiveExec(runner),
    Packages = new LinuxPackageManager(runner),
    Services = new LinuxServiceControl(runner),
    Repair = new LinuxRepair(runner),
    Registry = null, // no registry on Linux — the endpoint answers 501
    DesktopHost = new LinuxDesktopHost(runner),
};

var app = EsoAgentServer.Build(cfg, platform, b => b.Host.UseSystemd());
app.Run();
