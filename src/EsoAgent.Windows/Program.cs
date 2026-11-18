using EsoAgent.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EsoAgent.Windows;

// Windows head entry point. The executable runs as the "EsoAgent" Windows
// service (LocalSystem, session 0). All API/protocol logic lives in Core;
// this head only supplies the Windows platform implementations and hosts
// the control pipe the tray uses for the owner's kill switch + status.
internal static class Program
{
    private static int Main(string[] args)
    {
        // Optional: --data-dir <dir> (otherwise ESOAGENT_HOME / platform default).
        string? dataDir = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--data-dir", StringComparison.OrdinalIgnoreCase))
                dataDir = args[i + 1];
        }

        var cfg = AgentConfig.Load(dataDir);

        // Installer mode: --set-owner-password sets the owner-dashboard password
        // and exits BEFORE the host starts. The password comes from the
        // ESOAGENT_OWNER_PASSWORD env var when set, else one line from stdin.
        // It is never printed or logged — only its salted hash record is saved.
        if (args.Any(a => string.Equals(a, "--set-owner-password", StringComparison.OrdinalIgnoreCase)))
        {
            var password = Environment.GetEnvironmentVariable("ESOAGENT_OWNER_PASSWORD");
            if (string.IsNullOrEmpty(password))
                password = Console.In.ReadLine();
            if (string.IsNullOrEmpty(password))
            {
                Console.Error.WriteLine("No owner password supplied (ESOAGENT_OWNER_PASSWORD env var or one stdin line).");
                return 1;
            }
            var record = OwnerPassword.Create(password);
            OwnerPassword.SaveRecord(cfg.ResolvePath("owner.json"), record);
            Console.WriteLine("Owner password set.");
            return 0;
        }

        // Local recovery (v1.2): --disable-2fa turns off the TOTP second factor
        // (e.g. authenticator lost). Local access only — there is deliberately
        // no remote path that does this.
        if (args.Any(a => string.Equals(a, "--disable-2fa", StringComparison.OrdinalIgnoreCase)))
        {
            var store = new OwnerAuthStore();
            store.Load(cfg.ResolvePath("owner.json"));
            if (!store.IsConfigured)
            {
                Console.Error.WriteLine("No owner record found.");
                return 1;
            }
            store.DisableTotp();
            Console.WriteLine("Two-factor auth disabled. The owner can re-enroll from the panel.");
            return 0;
        }

        var runner = new ProcessRunner();
        // One forwarder instance serves both screenshot and interactive exec:
        // both are forwarded to the tray companion in the user's session.
        var trayForwarder = new TrayPipeForwarder();

        var platform = new PlatformServices
        {
            ProcessRunner = runner,
            SystemInfo = new WindowsSystemInfo(runner),
            PowerControl = new WindowsPowerControl(runner),
            Screenshot = trayForwarder,
            InteractiveExec = trayForwarder,
            Packages = new WindowsPackageManager(runner),
            Services = new WindowsServiceControl(runner),
            Repair = new WindowsRepair(runner),
            Registry = new WindowsRegistryAccess(),
            DesktopHost = new TrayDesktopHost(),
        };

        // Captured by stateReady (invoked inside Build before the host is
        // configured), then used by configureHost to register the control pipe.
        AgentState? state = null;

        var app = EsoAgentServer.Build(
            cfg,
            platform,
            configureHost: builder =>
            {
                builder.Host.UseWindowsService(options => options.ServiceName = "EsoAgent");
                builder.Services.AddHostedService(_ => new ControlPipeService(state!));
            },
            stateReady: s => state = s);

        app.Run();
        return 0;
    }
}
