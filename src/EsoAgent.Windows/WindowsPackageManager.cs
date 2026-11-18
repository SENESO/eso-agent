using EsoAgent.Core;
using Microsoft.Win32;

namespace EsoAgent.Windows;

/// <summary>
/// Windows IPackageManager ("winget+registry"). The registry Uninstall keys are
/// the authoritative installed-software list (they also carry the uninstall
/// strings); winget list output is merged in for entries the registry misses
/// and to fill in winget ids. Installs/uninstalls verify by re-listing —
/// Verified is only true when the post-state actually matches.
/// </summary>
public sealed class WindowsPackageManager : IPackageManager
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private readonly IProcessRunner _runner;

    public WindowsPackageManager(IProcessRunner runner) => _runner = runner;

    public string ManagerName => "winget+registry";

    /// <summary>A registry program: the public DTO plus its uninstall strings (kept internal).</summary>
    private sealed class RegistryEntry
    {
        public required SoftwarePackageDto Dto { get; init; }
        public string? QuietUninstallString { get; init; }
        public string? UninstallString { get; init; }
    }

    // ---------- list ----------

    public async Task<IReadOnlyList<SoftwarePackageDto>> ListAsync(CancellationToken ct = default)
    {
        var registryEntries = ReadRegistryEntries();
        var merged = registryEntries.Select(e => e.Dto).ToList();

        // winget is best-effort: it may be absent or slow; the registry list stands alone.
        IReadOnlyList<SoftwarePackageDto> wingetEntries = Array.Empty<SoftwarePackageDto>();
        try
        {
            var r = await _runner.RunAsync(new ExecRequest
            {
                File = "winget.exe",
                Args = new[] { "list", "--accept-source-agreements", "--disable-interactivity" },
                TimeoutMs = 120_000,
            }, ct);
            if (!string.IsNullOrWhiteSpace(r.Stdout))
                wingetEntries = Parsers.ParseWingetList(r.Stdout);
        }
        catch { /* tolerate failure — registry results are still returned */ }

        var byName = new Dictionary<string, SoftwarePackageDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var dto in merged) byName[dto.Name] = dto;

        foreach (var w in wingetEntries)
        {
            if (byName.TryGetValue(w.Name, out var existing))
            {
                // Matched by name: only fill the Id when the registry has none
                // (a registry subkey name is normally the product code already).
                if (string.IsNullOrEmpty(existing.Id) && !string.IsNullOrEmpty(w.Id))
                    existing.Id = w.Id;
            }
            else
            {
                merged.Add(w);
                byName[w.Name] = w;
            }
        }
        return merged;
    }

    private static List<RegistryEntry> ReadRegistryEntries()
    {
        var entries = new List<RegistryEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Scan(RegistryKey baseKey)
        {
            using (baseKey)
            {
                using var uninstall = baseKey.OpenSubKey(UninstallPath, writable: false);
                if (uninstall == null) return;
                foreach (var subName in uninstall.GetSubKeyNames())
                {
                    using var key = uninstall.OpenSubKey(subName, writable: false);
                    if (key == null) continue;
                    var displayName = key.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(displayName)) continue;
                    if (!seen.Add(subName + "|" + displayName)) continue; // same program via another view

                    long? sizeBytes = null;
                    if (key.GetValue("EstimatedSize") is int estimatedKb) sizeBytes = (long)estimatedKb * 1024;

                    entries.Add(new RegistryEntry
                    {
                        Dto = new SoftwarePackageDto
                        {
                            Name = displayName,
                            Id = subName,
                            Version = key.GetValue("DisplayVersion") as string,
                            Publisher = key.GetValue("Publisher") as string,
                            InstallDate = UninstallStringResolver.FormatInstallDate(key.GetValue("InstallDate") as string),
                            SizeBytes = sizeBytes,
                            Source = "registry",
                        },
                        QuietUninstallString = key.GetValue("QuietUninstallString") as string,
                        UninstallString = key.GetValue("UninstallString") as string,
                    });
                }
            }
        }

        // 64-bit and 32-bit (WOW6432Node) views of HKLM, plus per-user installs in HKCU.
        Scan(RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64));
        Scan(RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32));
        Scan(RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default));
        return entries;
    }

    // ---------- install ----------

    public async Task<OperationResult> InstallAsync(InstallRequest request, CancellationToken ct = default)
    {
        if (string.Equals(request.Kind, "manager", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(request.Id))
                return Fail("Install (manager) requires an Id (the winget package id).");
            var r = await RunWingetAsync(new[]
            {
                "install", "--id", request.Id!, "-e", "--silent",
                "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity",
            }, request.TimeoutMs, ct);
            var verified = await VerifyListedAsync(request.Id, null, ct);
            return new OperationResult
            {
                Success = r.ExitCode == 0,
                ExitCode = r.ExitCode,
                Output = Combine(r),
                Verified = verified,
                Message = r.ExitCode == 0
                    ? (verified ? "Installed and verified in the package list." : "winget reported success but the package was not found on re-listing.")
                    : "winget install failed.",
            };
        }

        if (string.Equals(request.Kind, "file", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(request.Path))
                return Fail("Install (file) requires a Path to a local installer.");
            var path = request.Path!;
            var ext = Path.GetExtension(path).ToLowerInvariant();

            ExecResponse r;
            if (ext == ".msi")
            {
                r = await _runner.RunAsync(new ExecRequest
                {
                    File = "msiexec.exe",
                    Args = new[] { "/i", path, "/qn", "/norestart" },
                    TimeoutMs = request.TimeoutMs,
                }, ct);
            }
            else if (ext == ".exe")
            {
                // With caller-supplied silent args: one attempt using them all.
                // Otherwise try the common silent switches in order until one exits 0.
                if (request.SilentArgs is { Length: > 0 })
                {
                    r = await _runner.RunAsync(new ExecRequest { File = path, Args = request.SilentArgs, TimeoutMs = request.TimeoutMs }, ct);
                }
                else
                {
                    ExecResponse? last = null;
                    foreach (var silentArg in new[] { "/S", "/quiet", "/silent" })
                    {
                        last = await _runner.RunAsync(new ExecRequest { File = path, Args = new[] { silentArg }, TimeoutMs = request.TimeoutMs }, ct);
                        if (last.ExitCode == 0) break;
                    }
                    r = last!;
                }
            }
            else
            {
                return Fail($"Unsupported installer extension '{ext}'. Supported: .msi, .exe.");
            }

            bool verified = false;
            string message;
            if (!string.IsNullOrWhiteSpace(request.Id))
            {
                verified = await VerifyListedAsync(request.Id, null, ct);
                message = verified ? "Installed and verified in the package list." : "Installer exited, but the package was not found on re-listing.";
            }
            else
            {
                message = "No Id supplied, so the install cannot be verified against the package list; only the installer exit code is known.";
            }
            return new OperationResult
            {
                Success = r.ExitCode == 0,
                ExitCode = r.ExitCode,
                Output = Combine(r),
                Verified = verified,
                Message = message,
            };
        }

        return Fail($"Unknown install Kind '{request.Kind}'. Use \"manager\" or \"file\".");
    }

    // ---------- uninstall ----------

    public async Task<OperationResult> UninstallAsync(UninstallRequest request, CancellationToken ct = default)
    {
        var entries = ReadRegistryEntries();
        var match = FindEntry(entries, request.Name);

        if (match != null)
        {
            string file, argsString;
            try
            {
                (file, argsString) = UninstallStringResolver.ResolveSilent(match.QuietUninstallString, match.UninstallString);
            }
            catch (ArgumentException)
            {
                // No uninstall string at all — fall through to the winget fallback below.
                return await WingetUninstallFallbackAsync(request, ct);
            }

            var r = await _runner.RunAsync(new ExecRequest
            {
                File = file,
                Args = SplitArguments(argsString),
                TimeoutMs = request.TimeoutMs,
            }, ct);

            var after = ReadRegistryEntries();
            var stillThere = FindEntry(after, request.Name) != null
                             && after.Any(e => string.Equals(e.Dto.Name, match.Dto.Name, StringComparison.OrdinalIgnoreCase));
            return new OperationResult
            {
                Success = r.ExitCode == 0,
                ExitCode = r.ExitCode,
                Output = Combine(r),
                Verified = !stillThere,
                Message = !stillThere
                    ? "Uninstalled and verified absent from the registry."
                    : "Uninstaller ran but the program is still listed in the registry.",
            };
        }

        return await WingetUninstallFallbackAsync(request, ct);
    }

    private async Task<OperationResult> WingetUninstallFallbackAsync(UninstallRequest request, CancellationToken ct)
    {
        var r = await RunWingetAsync(new[]
        {
            "uninstall", "--id", request.Name, "--silent",
            "--accept-source-agreements", "--disable-interactivity",
        }, request.TimeoutMs, ct);
        var verified = !await VerifyListedAsync(request.Name, request.Name, ct);
        return new OperationResult
        {
            Success = r.ExitCode == 0,
            ExitCode = r.ExitCode,
            Output = Combine(r),
            Verified = verified,
            Message = r.ExitCode == 0
                ? "winget uninstall reported success (program was not in the registry list)."
                : "Program not found in the registry and winget uninstall failed.",
        };
    }

    // ---------- upgrade ----------

    public async Task<OperationResult> UpgradeAsync(UpgradeRequest request, CancellationToken ct = default)
    {
        var args = new List<string> { "upgrade" };
        if (!string.IsNullOrWhiteSpace(request.Id)) { args.Add("--id"); args.Add(request.Id!); args.Add("-e"); }
        else args.Add("--all");
        args.AddRange(new[] { "--silent", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity" });

        var r = await RunWingetAsync(args.ToArray(), request.TimeoutMs, ct);
        return new OperationResult
        {
            Success = r.ExitCode == 0,
            ExitCode = r.ExitCode,
            Output = Combine(r),
            Verified = false,
            Message = r.ExitCode == 0 ? "winget upgrade completed." : "winget upgrade failed.",
        };
    }

    // ---------- helpers ----------

    private Task<ExecResponse> RunWingetAsync(string[] args, int timeoutMs, CancellationToken ct)
        => _runner.RunAsync(new ExecRequest { File = "winget.exe", Args = args, TimeoutMs = timeoutMs }, ct);

    private async Task<bool> VerifyListedAsync(string? id, string? name, CancellationToken ct)
    {
        var list = await ListAsync(ct);
        return list.Any(p =>
            (id != null && string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) ||
            (name != null && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) ||
            (id != null && string.Equals(p.Name, id, StringComparison.OrdinalIgnoreCase)));
    }

    private static RegistryEntry? FindEntry(List<RegistryEntry> entries, string needle)
    {
        if (string.IsNullOrWhiteSpace(needle)) return null;
        // Exact match first (name, id / product code), then a case-insensitive contains.
        var exact = entries.FirstOrDefault(e =>
            string.Equals(e.Dto.Name, needle, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e.Dto.Id, needle, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        return entries.FirstOrDefault(e =>
            e.Dto.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            (e.Dto.Id?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false));
    }

    /// <summary>Splits a registry command-line argument string, honouring double quotes.</summary>
    internal static string[] SplitArguments(string args)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false, hasToken = false;
        foreach (var ch in args)
        {
            if (ch == '"') { inQuotes = !inQuotes; hasToken = true; }
            else if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (hasToken || current.Length > 0) { result.Add(current.ToString()); current.Clear(); hasToken = false; }
            }
            else current.Append(ch);
        }
        if (hasToken || current.Length > 0) result.Add(current.ToString());
        return result.ToArray();
    }

    private static string Combine(ExecResponse r)
        => string.Join("\n", new[] { r.Stdout.Trim(), r.Stderr.Trim() }.Where(s => s.Length > 0));

    private static OperationResult Fail(string message)
        => new() { Success = false, Output = "", Message = message };
}
