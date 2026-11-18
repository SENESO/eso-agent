using System.IO.Compression;
using EsoAgent.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Xunit;

namespace EsoAgent.Tests;

/// <summary>
/// Regression coverage for the live §F defect (BUILD-REPORT §12): neither
/// Kestrel host raised MaxRequestBodySize, so the ~30 MB default aborted
/// real rollout / self-update package uploads (~41 MB) mid-stream. The
/// fix is one shared helper (NetHelpers.ApplyRequestBodyLimit) called by
/// BOTH hosts' ConfigureKestrel — these tests pin the value both hosts
/// configure and prove the agent-side staging pipeline accepts a package
/// larger than the old default end-to-end. (A live HTTP upload through
/// Kestrel cannot run in this sandbox — .NET loopback TCP is intercepted
/// here; the live proof is the §F re-run on the box, BUILD-REPORT §13.)
/// </summary>
public class PackageUploadLimitTests
{
    // The real v1.2 linux package that failed to upload live (§12 §F).
    private const long RealV12LinuxPackageBytes = 41_199_260;

    [Fact]
    public void SharedLimit_ExceedsKestrelDefaultAndRealPackages()
    {
        var options = new KestrelServerOptions();
        var kestrelDefault = options.Limits.MaxRequestBodySize;
        NetHelpers.ApplyRequestBodyLimit(options);

        Assert.Equal(NetHelpers.MaxRequestBodyBytes, options.Limits.MaxRequestBodySize);
        Assert.Equal(256L * 1024 * 1024, NetHelpers.MaxRequestBodyBytes);
        Assert.True(options.Limits.MaxRequestBodySize > kestrelDefault,
            "the applied limit must exceed Kestrel's ~30 MB default");
        Assert.True(options.Limits.MaxRequestBodySize > RealV12LinuxPackageBytes,
            "the applied limit must admit the real ~41 MB packages");
    }

    [Fact]
    public async Task StageAsync_AcceptsPackageLargerThanOldKestrelDefault()
    {
        var dir = NewTempDir();
        try
        {
            var cfg = AgentConfig.Load(dir);
            var token = new byte[32];
            for (var i = 0; i < token.Length; i++) token[i] = (byte)(i + 1);
            var update = new UpdateService(cfg, new StubProcessRunner());

            // A real-size package: >33 MB of payload (over the old ~30 MB
            // Kestrel default), stamped NEWER than the running version so
            // the in-place version gate lets it stage.
            var zipPath = Path.Combine(dir, "big-package.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var version = zip.CreateEntry("esoagent-version.txt");
                using (var w = new StreamWriter(version.Open())) w.Write("9.9.9");
                var payload = zip.CreateEntry("payload.bin", CompressionLevel.NoCompression);
                using var ps = payload.Open();
                var chunk = new byte[1024 * 1024];
                uint state = 0x12345678; // deterministic LCG — varied, incompressible data
                for (var i = 0; i < chunk.Length; i += 4)
                {
                    state = state * 1664525 + 1013904223;
                    BitConverter.GetBytes(state).CopyTo(chunk, i);
                }
                for (var i = 0; i < 33; i++) ps.Write(chunk, 0, chunk.Length);
            }
            var size = new FileInfo(zipPath).Length;
            Assert.True(size > 30_000_000, $"test package must exceed the old default (is {size})");

            var sha = FileTransferService.HashFile(zipPath);
            var sig = UpdateService.SignPackage(token, sha);
            await using var stream = File.OpenRead(zipPath);
            var verified = await update.StageAsync(stream, sha, sig, token);

            Assert.Equal(sha, verified);
            var staged = Path.Combine(update.StagingDir, "package.zip");
            Assert.True(File.Exists(staged), "staged package must exist after a successful stage");
            Assert.Equal(size, new FileInfo(staged).Length);
        }
        finally { TryDelete(dir); }
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "esoagent-upload-limit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    // StageAsync never calls the runner (only ApplyStaged does).
    private sealed class StubProcessRunner : IProcessRunner
    {
        public Task<ExecResponse> RunAsync(ExecRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ExecResponse> RunStreamingAsync(ExecRequest request, Func<string, string, Task> onData, CancellationToken ct = default) => throw new NotImplementedException();
        public int LaunchDetached(ExecRequest request) => throw new NotImplementedException();
    }
}
