using System.IO.Compression;
using EsoAgent.Core;
using Xunit;

namespace EsoAgent.Tests;

/// <summary>
/// The apply script that ships INSIDE an update package is the one that
/// applies it (installer diet, BUILD-REPORT §19): the package's layout
/// and its script change together — the folder payload of v1.2.9+ would
/// be stranded by an older installed script that swaps only two exes.
/// The installed copy remains the fallback for packages without one.
/// </summary>
public class UpdateApplyScriptTests
{
    private static string ScriptName => OperatingSystem.IsWindows() ? "update-apply.ps1" : "update-apply.sh";

    [Fact]
    public void ApplyStaged_PrefersTheScriptInsideThePackage()
    {
        var (update, appDir, runner) = Setup(withPackageScript: true, withInstalledScript: true);
        try
        {
            var result = update.ApplyStaged(appDir);

            Assert.True(result.Success, result.Message);
            var launched = Assert.Single(runner.Launched);
            var scriptArg = launched.Args.First(a => a.EndsWith(ScriptName));
            Assert.StartsWith(Path.Combine(update.StagingDir, "extracted"), scriptArg);
        }
        finally { TryDelete(update.StagingDir); TryDelete(appDir); }
    }

    [Fact]
    public void ApplyStaged_FallsBackToTheInstalledScript()
    {
        var (update, appDir, runner) = Setup(withPackageScript: false, withInstalledScript: true);
        try
        {
            var result = update.ApplyStaged(appDir);

            Assert.True(result.Success, result.Message);
            var launched = Assert.Single(runner.Launched);
            var scriptArg = launched.Args.First(a => a.EndsWith(ScriptName));
            Assert.Equal(Path.Combine(appDir, ScriptName), scriptArg);
        }
        finally { TryDelete(update.StagingDir); TryDelete(appDir); }
    }

    [Fact]
    public void ApplyStaged_NoScriptAnywhere_RefusesWithoutLaunching()
    {
        var (update, appDir, runner) = Setup(withPackageScript: false, withInstalledScript: false);
        try
        {
            var result = update.ApplyStaged(appDir);

            Assert.False(result.Success);
            Assert.Empty(runner.Launched);
        }
        finally { TryDelete(update.StagingDir); TryDelete(appDir); }
    }

    private static (UpdateService Update, string AppDir, CapturingRunner Runner) Setup(bool withPackageScript, bool withInstalledScript)
    {
        var dataDir = NewTempDir("esoagent-apply-data-");
        var appDir = NewTempDir("esoagent-apply-app-");
        var cfg = AgentConfig.Load(dataDir);
        var runner = new CapturingRunner();
        var update = new UpdateService(cfg, runner);
        Directory.CreateDirectory(update.StagingDir);
        var zipPath = Path.Combine(update.StagingDir, "package.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            if (withPackageScript)
            {
                var entry = zip.CreateEntry(ScriptName);
                using var w = new StreamWriter(entry.Open());
                w.Write("# package apply script");
            }
            var payload = zip.CreateEntry("EsoAgent.Core.dll");
            using var s = payload.Open();
            s.WriteByte(0);
        }
        if (withInstalledScript)
            File.WriteAllText(Path.Combine(appDir, ScriptName), "# installed apply script");
        return (update, appDir, runner);
    }

    private static string NewTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    private sealed class CapturingRunner : IProcessRunner
    {
        public List<ExecRequest> Launched { get; } = new();
        public Task<ExecResponse> RunAsync(ExecRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ExecResponse> RunStreamingAsync(ExecRequest request, Func<string, string, Task> onData, CancellationToken ct = default) => throw new NotImplementedException();
        public int LaunchDetached(ExecRequest request)
        {
            Launched.Add(request);
            return 4242;
        }
    }
}
