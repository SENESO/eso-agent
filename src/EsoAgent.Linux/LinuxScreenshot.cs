using EsoAgent.Core;

namespace EsoAgent.Linux;

/// <summary>
/// Desktop capture using whichever screenshot tool the machine already has —
/// grim (Wayland), scrot (X11) or ImageMagick's import (X11). The daemon runs
/// as root outside the user's session, so without one of these tools (or a
/// display to point them at) capture honestly reports unavailable; it never
/// fabricates an image.
/// </summary>
public sealed class LinuxScreenshot : IScreenshotProvider
{
    private readonly IProcessRunner _runner;

    public LinuxScreenshot(IProcessRunner runner) => _runner = runner;

    public async Task<(byte[]? Png, string? Error)> CaptureAsync(CancellationToken ct = default)
    {
        var tool = FindTool();
        if (tool == null)
            return (null, "No screenshot tool available (looked for grim, scrot, import).");

        var tempPath = Path.Combine(Path.GetTempPath(), $"eso-screenshot-{Guid.NewGuid():N}.png");
        try
        {
            string[] args = Path.GetFileName(tool) switch
            {
                "grim" => new[] { tempPath },
                "scrot" => new[] { tempPath },
                _ => new[] { "-window", "root", tempPath }, // import
            };

            // The daemon's environment normally has no DISPLAY; the owner's
            // desktop is virtually always :0.
            Dictionary<string, string>? env = null;
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
                env = new Dictionary<string, string> { ["DISPLAY"] = ":0" };

            var result = await RunnerHelpers.RunSafeAsync(_runner, new ExecRequest
            {
                File = tool,
                Args = args,
                Env = env,
                TimeoutMs = 15_000,
            }, ct);

            if (result.ExitCode != 0 || !File.Exists(tempPath))
            {
                var error = RunnerHelpers.Combined(result);
                return (null, error.Length > 0 ? error : $"Screenshot tool '{Path.GetFileName(tool)}' produced no image.");
            }
            var bytes = await File.ReadAllBytesAsync(tempPath, ct);
            return bytes.Length > 0 ? (bytes, null) : (null, $"Screenshot tool '{Path.GetFileName(tool)}' produced an empty image.");
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    /// <summary>First of grim / scrot / import found as an executable file on PATH.</summary>
    private static string? FindTool()
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        var dirs = pathVar.Split(':', StringSplitOptions.RemoveEmptyEntries);
        foreach (var name in new[] { "grim", "scrot", "import" })
        {
            foreach (var dir in dirs)
            {
                var candidate = Path.Combine(dir, name);
                if (IsExecutable(candidate)) return candidate;
            }
        }
        return null;
    }

    private static bool IsExecutable(string path)
    {
        if (!OperatingSystem.IsLinux()) return false;
        try
        {
            if (!File.Exists(path)) return false;
            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch { return false; }
    }
}
