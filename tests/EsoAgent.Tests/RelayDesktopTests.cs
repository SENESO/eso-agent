using System.Net.WebSockets;
using System.Text.Json;
using EsoAgent.Core;
using Xunit;

namespace EsoAgent.Tests;

/// <summary>
/// Regression tests for the relayed-desktop hello deadlock, live-found on
/// the Simular box (BUILD-REPORT §11, live script §B): on the relay path
/// the desktop hello travels ONLY inside the signed stream-open
/// PayloadJson, while DesktopStreamRunner reads the hello as the first
/// message on its channel. Relay clients never re-send it as a message,
/// so without the device seeding the channel from the verified payload,
/// the client waits for a welcome while the runner waits for a hello —
/// a permanent deadlock (proven live: a second manual hello produced an
/// instant welcome + frames). These tests pin the delivery step.
/// </summary>
public class RelayDesktopTests
{
    [Fact]
    public async Task RelayOpen_HelloIsSeededAsFirstChannelMessage()
    {
        var dir = NewTempDir();
        try
        {
            var state = BuildState(dir, new FakeDesktopHost());
            var svc = new FleetClientService(state);
            var hello = new DesktopHello { Mode = "View", Codecs = new() { "jpeg" }, Quality = 60 };
            var payload = JsonSerializer.Serialize(hello, EsoJson.Options);
            var open = new FleetStreamOpenFrame
            {
                StreamId = "seed-test", Kind = "desktop", Signer = "operator", PayloadJson = payload,
            };
            using var ws = new ClientWebSocket();
            var channel = svc.CreateDesktopChannel(ws, open);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var first = await channel.ReceiveTextAsync(cts.Token);
            Assert.Equal(payload, first);
            var parsed = JsonSerializer.Deserialize<DesktopHello>(first!, EsoJson.Options);
            Assert.NotNull(parsed);
            Assert.Equal("hello", parsed!.Type);
            Assert.Equal("View", parsed.Mode);
            Assert.Equal(new[] { "jpeg" }, parsed.Codecs);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task RelayOpen_RunnerConsumesSeededHelloAndOpensSession()
    {
        var dir = NewTempDir();
        try
        {
            var host = new FakeDesktopHost();
            var state = BuildState(dir, host);
            var svc = new FleetClientService(state);
            var hello = new DesktopHello { Mode = "View", Codecs = new() { "jpeg" } };
            var payload = JsonSerializer.Serialize(hello, EsoJson.Options);
            var open = new FleetStreamOpenFrame
            {
                StreamId = "runner-test", Kind = "desktop", Signer = "owner-panel", PayloadJson = payload,
            };
            using var ws = new ClientWebSocket();
            var channel = svc.CreateDesktopChannel(ws, open);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var run = DesktopStreamRunner.RunAsync(channel, state, "fleet", cts.Token);

            // Before the fix this wait NEVER completed — the runner blocked
            // on the channel read forever (the §B deadlock). The host being
            // asked to open a session proves the runner consumed the hello
            // from the seeded payload and proceeded to the welcome step.
            var winner = await Task.WhenAny(host.Opened.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.Same(host.Opened.Task, winner);
            var seen = await host.Opened.Task;
            Assert.Equal("View", seen.Mode);
            Assert.Contains("jpeg", seen.Codecs);

            // The welcome send rides the (unconnected) test socket and
            // fails, which ends the runner cleanly; cancel as a backstop.
            cts.Cancel();
            await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5)));
        }
        finally { TryDelete(dir); }
    }

    // ---------- state + fakes ----------

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "esoagent-relay-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    private static AgentState BuildState(string dataDir, IDesktopHost desktopHost)
    {
        var cfg = AgentConfig.Load(dataDir);
        var platform = new PlatformServices
        {
            ProcessRunner = new StubProcessRunner(),
            SystemInfo = new StubSystemInfo(),
            PowerControl = new StubPowerControl(),
            Screenshot = new StubScreenshot(),
            InteractiveExec = new StubInteractiveExec(),
            Packages = new StubPackages(),
            Services = new StubServices(),
            Repair = new StubRepair(),
            DesktopHost = desktopHost,
        };
        return new AgentState
        {
            Config = cfg,
            Token = new byte[32],
            Audit = new AuditLog(cfg.ResolvePath(cfg.AuditLogFile)),
            KillSwitch = new KillSwitch(cfg.ResolvePath(cfg.LockFile)),
            Platform = platform,
            Files = new FileTransferService(),
            Update = new UpdateService(cfg, platform.ProcessRunner),
        };
    }

    private sealed class FakeDesktopHost : IDesktopHost
    {
        public IReadOnlyCollection<string> SupportedCodecs => new[] { "jpeg" };
        public TaskCompletionSource<DesktopHello> Opened { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<(IDesktopSession? Session, string? Error)> OpenAsync(DesktopHello hello, string codec, CancellationToken ct = default)
        {
            Opened.TrySetResult(hello);
            return Task.FromResult<(IDesktopSession?, string?)>((new FakeDesktopSession(), null));
        }
    }

    private sealed class FakeDesktopSession : IDesktopSession
    {
        private static readonly MonitorInfo FakeMonitor =
            new() { Index = 0, Name = "Fake display", Width = 1280, Height = 800, Primary = true };

        public MonitorInfo Monitor => FakeMonitor;
        public IReadOnlyList<MonitorInfo> AllMonitors => new[] { FakeMonitor };
        public string Codec => "jpeg";

        public async Task<DesktopFrame?> NextFrameAsync(CancellationToken ct = default)
        {
            await Task.Delay(25, ct);
            return null; // unchanged frame — the runner idles until cancelled
        }

        public Task SendInputAsync(DesktopInput input, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetClipboardAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task SetClipboardAsync(string text, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // Platform stubs: present so PlatformServices can be built; the desktop
    // runner never calls them.
    private sealed class StubProcessRunner : IProcessRunner
    {
        public Task<ExecResponse> RunAsync(ExecRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ExecResponse> RunStreamingAsync(ExecRequest request, Func<string, string, Task> onData, CancellationToken ct = default) => throw new NotImplementedException();
        public int LaunchDetached(ExecRequest request) => throw new NotImplementedException();
    }

    private sealed class StubSystemInfo : ISystemInfo
    {
        public (long? Used, long? Total) GetMemory() => (null, null);
        public Task<double?> GetCpuPercentAsync(CancellationToken ct = default) => Task.FromResult<double?>(null);
        public BatteryDto? GetBattery() => null;
        public IReadOnlyList<string> GetLoggedOnUsers() => Array.Empty<string>();
    }

    private sealed class StubPowerControl : IPowerControl
    {
        public Task<OperationResult> RebootAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<OperationResult> ShutdownAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<OperationResult> SleepAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class StubScreenshot : IScreenshotProvider
    {
        public Task<(byte[]? Png, string? Error)> CaptureAsync(CancellationToken ct = default)
            => Task.FromResult<(byte[]?, string?)>((null, "not available in tests"));
    }

    private sealed class StubInteractiveExec : IInteractiveExec
    {
        public Task<ExecResponse> RunInUserSessionAsync(ExecRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class StubPackages : IPackageManager
    {
        public string ManagerName => "stub";
        public Task<IReadOnlyList<SoftwarePackageDto>> ListAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<OperationResult> InstallAsync(InstallRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<OperationResult> UninstallAsync(UninstallRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<OperationResult> UpgradeAsync(UpgradeRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class StubServices : IServiceControl
    {
        public Task<IReadOnlyList<ServiceDto>> ListAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<OperationResult> StartAsync(string name, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<OperationResult> StopAsync(string name, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<OperationResult> RestartAsync(string name, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<OperationResult> SetStartupModeAsync(string name, string mode, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class StubRepair : ISystemRepair
    {
        public IReadOnlyList<RepairInfoDto> Available => Array.Empty<RepairInfoDto>();
        public Task<OperationResult> RunAsync(string name, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
