using EsoAgent.Core;

namespace EsoAgent.Linux;

/// <summary>
/// Distro package management over the native tools: apt/dpkg on Debian-family,
/// dnf/rpm on RPM-family, pacman on Arch-family. Every mutation is verified by
/// re-listing installed packages afterwards — Verified is never assumed.
/// </summary>
public sealed class LinuxPackageManager : IPackageManager
{
    private readonly IProcessRunner _runner;
    private readonly DistroFamily _family;

    public LinuxPackageManager(IProcessRunner runner)
    {
        _runner = runner;
        _family = LinuxDistro.Detect();
    }

    public string ManagerName => _family switch
    {
        DistroFamily.Debian => "apt/dpkg",
        DistroFamily.Rpm => "dnf/rpm",
        DistroFamily.Pacman => "pacman",
        _ => "unknown",
    };

    private const string NoManagerMessage =
        "No supported package manager found on this machine (looked for dpkg, rpm, pacman).";

    // ---------- list ----------

    public async Task<IReadOnlyList<SoftwarePackageDto>> ListAsync(CancellationToken ct = default)
    {
        switch (_family)
        {
            case DistroFamily.Debian:
            {
                var r = await RunAsync("dpkg-query", 120_000, ct,
                    "-W", "-f=${Package}\\t${Version}\\t${Installed-Size}\\t${Status}\\n");
                return r.ExitCode == 0 ? Parsers.ParseDpkgList(r.Stdout) : Array.Empty<SoftwarePackageDto>();
            }
            case DistroFamily.Rpm:
            {
                var r = await RunAsync("rpm", 120_000, ct,
                    "-qa", "--qf", "%{NAME}\\t%{VERSION}-%{RELEASE}\\t%{SIZE}\\t%{SUMMARY}\\n");
                return r.ExitCode == 0 ? Parsers.ParseRpmList(r.Stdout) : Array.Empty<SoftwarePackageDto>();
            }
            case DistroFamily.Pacman:
            {
                var r = await RunAsync("pacman", 120_000, ct, "-Q");
                return r.ExitCode == 0 ? Parsers.ParsePacmanList(r.Stdout) : Array.Empty<SoftwarePackageDto>();
            }
            default:
                return Array.Empty<SoftwarePackageDto>();
        }
    }

    // ---------- install ----------

    public async Task<OperationResult> InstallAsync(InstallRequest request, CancellationToken ct = default)
    {
        if (_family == DistroFamily.Unknown)
            return new OperationResult { Success = false, Message = NoManagerMessage };

        return request.Kind.Trim().ToLowerInvariant() switch
        {
            "manager" => await InstallFromManagerAsync(request, ct),
            "file" => await InstallFromFileAsync(request, ct),
            _ => new OperationResult { Success = false, Message = $"Unknown install kind '{request.Kind}'. Use \"manager\" or \"file\"." },
        };
    }

    private async Task<OperationResult> InstallFromManagerAsync(InstallRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Id))
            return new OperationResult { Success = false, Message = "Missing package id." };
        var id = request.Id.Trim();

        ExecResponse result;
        switch (_family)
        {
            case DistroFamily.Debian:
                await AptUpdateAsync(request.TimeoutMs, ct); // best-effort; failure ignored
                result = await RunAsync("apt-get", request.TimeoutMs, ct, Apt(), "install", "-y", id);
                break;
            case DistroFamily.Rpm:
                result = await RunAsync("dnf", request.TimeoutMs, ct, "install", "-y", id);
                break;
            case DistroFamily.Pacman:
                result = await RunAsync("pacman", request.TimeoutMs, ct, "-S", "--noconfirm", id);
                break;
            default:
                return new OperationResult { Success = false, Message = NoManagerMessage };
        }
        return await VerifyInstalledAsync(result, id, $"install '{id}'", ct);
    }

    private async Task<OperationResult> InstallFromFileAsync(InstallRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
            return new OperationResult { Success = false, Message = "Missing installer file path." };
        var absPath = Path.GetFullPath(request.Path);
        if (!File.Exists(absPath))
            return new OperationResult { Success = false, Message = $"Installer file not found: {absPath}" };

        ExecResponse result;
        string? verifyName;
        if (absPath.EndsWith(".deb", StringComparison.OrdinalIgnoreCase))
        {
            if (_family != DistroFamily.Debian)
                return new OperationResult { Success = false, Message = ".deb install requires a dpkg-based distro." };
            // The package name (not the file name) is what the installed list carries.
            var nameResult = await RunAsync("dpkg-deb", 30_000, ct, "-f", absPath, "Package");
            verifyName = nameResult.ExitCode == 0 ? nameResult.Stdout.Trim() : null;
            if (string.IsNullOrWhiteSpace(verifyName)) verifyName = null;

            result = await RunAsync("apt-get", request.TimeoutMs, ct, Apt(), "install", "-y", absPath);
            if (result.ExitCode != 0)
            {
                // apt refuses some local files (unsigned, dependency order) — dpkg is the fallback.
                var fallback = await RunAsync("dpkg", request.TimeoutMs, ct, "-i", absPath);
                if (fallback.ExitCode == 0) result = fallback;
            }
        }
        else if (absPath.EndsWith(".rpm", StringComparison.OrdinalIgnoreCase))
        {
            if (_family != DistroFamily.Rpm)
                return new OperationResult { Success = false, Message = ".rpm install requires an rpm-based distro." };
            var nameResult = await RunAsync("rpm", 30_000, ct, "-qp", "--qf", "%{NAME}", absPath);
            verifyName = nameResult.ExitCode == 0 ? nameResult.Stdout.Trim() : null;
            if (string.IsNullOrWhiteSpace(verifyName)) verifyName = null;
            result = await RunAsync("dnf", request.TimeoutMs, ct, "install", "-y", absPath);
        }
        else if (absPath.Contains(".pkg.tar.", StringComparison.OrdinalIgnoreCase))
        {
            if (_family != DistroFamily.Pacman)
                return new OperationResult { Success = false, Message = "pacman package files require an Arch-based distro." };
            var nameResult = await RunAsync("pacman", 30_000, ct, "-Qp", absPath);
            // pacman -Qp prints "name version".
            verifyName = nameResult.ExitCode == 0 ? Parsers.ParsePacmanList(nameResult.Stdout)
                .Select(p => p.Name).FirstOrDefault() : null;
            result = await RunAsync("pacman", request.TimeoutMs, ct, "-U", "--noconfirm", absPath);
        }
        else
        {
            return new OperationResult
            {
                Success = false,
                Message = $"Unsupported installer file '{absPath}'. Expected .deb, .rpm or .pkg.tar.zst.",
            };
        }

        if (verifyName == null)
        {
            return new OperationResult
            {
                Success = result.ExitCode == 0,
                ExitCode = result.ExitCode,
                Output = RunnerHelpers.Combined(result),
                Verified = false,
                Message = result.ExitCode == 0
                    ? "Install command succeeded, but the package name could not be determined, so presence is unverified."
                    : "Install command failed and presence is unverified.",
            };
        }
        return await VerifyInstalledAsync(result, verifyName, $"install '{verifyName}' from file", ct);
    }

    // ---------- uninstall ----------

    public async Task<OperationResult> UninstallAsync(UninstallRequest request, CancellationToken ct = default)
    {
        if (_family == DistroFamily.Unknown)
            return new OperationResult { Success = false, Message = NoManagerMessage };
        if (string.IsNullOrWhiteSpace(request.Name))
            return new OperationResult { Success = false, Message = "Missing package name." };
        var name = request.Name.Trim();

        var result = _family switch
        {
            DistroFamily.Debian => await RunAsync("apt-get", request.TimeoutMs, ct, Apt(), "remove", "-y", name),
            DistroFamily.Rpm => await RunAsync("dnf", request.TimeoutMs, ct, "remove", "-y", name),
            DistroFamily.Pacman => await RunAsync("pacman", request.TimeoutMs, ct, "-R", "--noconfirm", name),
            _ => throw new InvalidOperationException("unreachable"),
        };

        var after = await ListAsync(ct);
        bool absent = !after.Any(p => Matches(p, name));
        return new OperationResult
        {
            Success = result.ExitCode == 0 && absent,
            ExitCode = result.ExitCode,
            Output = RunnerHelpers.Combined(result),
            Verified = absent,
            Message = absent
                ? (result.ExitCode == 0 ? $"Uninstalled '{name}' (verified absent)." : $"Package '{name}' is absent, but the remove command exited {result.ExitCode}.")
                : $"Package '{name}' is still installed after the remove command.",
        };
    }

    // ---------- upgrade ----------

    public async Task<OperationResult> UpgradeAsync(UpgradeRequest request, CancellationToken ct = default)
    {
        if (_family == DistroFamily.Unknown)
            return new OperationResult { Success = false, Message = NoManagerMessage };

        var id = string.IsNullOrWhiteSpace(request.Id) ? null : request.Id.Trim();
        ExecResponse result;
        switch (_family)
        {
            case DistroFamily.Debian:
                await AptUpdateAsync(request.TimeoutMs, ct); // best-effort; failure ignored
                result = id != null
                    ? await RunAsync("apt-get", request.TimeoutMs, ct, Apt(), "install", "-y", "--only-upgrade", id)
                    : await RunAsync("apt-get", request.TimeoutMs, ct, Apt(), "upgrade", "-y");
                break;
            case DistroFamily.Rpm:
                result = id != null
                    ? await RunAsync("dnf", request.TimeoutMs, ct, "upgrade", "-y", id)
                    : await RunAsync("dnf", request.TimeoutMs, ct, "upgrade", "-y");
                break;
            case DistroFamily.Pacman:
                result = id != null
                    ? await RunAsync("pacman", request.TimeoutMs, ct, "-S", "--noconfirm", id)
                    : await RunAsync("pacman", request.TimeoutMs, ct, "-Syu", "--noconfirm");
                break;
            default:
                return new OperationResult { Success = false, Message = NoManagerMessage };
        }

        if (id == null)
        {
            // A full-system upgrade has no single post-state to re-query, so
            // Verified stays false (it is only set when a package was re-checked).
            return new OperationResult
            {
                Success = result.ExitCode == 0,
                ExitCode = result.ExitCode,
                Output = RunnerHelpers.Combined(result),
                Verified = false,
                Message = result.ExitCode == 0 ? "Full upgrade completed." : "Full upgrade failed.",
            };
        }
        return await VerifyInstalledAsync(result, id, $"upgrade '{id}'", ct);
    }

    // ---------- helpers ----------

    private async Task<OperationResult> VerifyInstalledAsync(ExecResponse result, string name, string what, CancellationToken ct)
    {
        var after = await ListAsync(ct);
        bool present = after.Any(p => Matches(p, name));
        return new OperationResult
        {
            Success = result.ExitCode == 0 && present,
            ExitCode = result.ExitCode,
            Output = RunnerHelpers.Combined(result),
            Verified = present,
            Message = present
                ? (result.ExitCode == 0 ? $"Completed {what} (verified installed)." : $"Package is installed, but the command for {what} exited {result.ExitCode}.")
                : $"Package '{name}' not found in the installed list after {what}.",
        };
    }

    private static bool Matches(SoftwarePackageDto p, string name)
        => string.Equals(p.Id, name, StringComparison.OrdinalIgnoreCase)
        || string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase);

    private async Task AptUpdateAsync(int timeoutMs, CancellationToken ct)
    {
        // Refreshing metadata is best-effort: a stale-but-usable cache must not
        // block an install, so the result is deliberately ignored.
        _ = await RunAsync("apt-get", timeoutMs, ct, Apt(), "update", "-qq");
    }

    private static Dictionary<string, string> Apt() => RunnerHelpers.AptEnv();

    private Task<ExecResponse> RunAsync(string file, int timeoutMs, CancellationToken ct, params string[] args)
        => RunnerHelpers.RunSafeAsync(_runner, RunnerHelpers.Cmd(file, timeoutMs, args), ct);

    private Task<ExecResponse> RunAsync(string file, int timeoutMs, CancellationToken ct, Dictionary<string, string> env, params string[] args)
        => RunnerHelpers.RunSafeAsync(_runner, RunnerHelpers.Cmd(file, timeoutMs, env, args), ct);
}
