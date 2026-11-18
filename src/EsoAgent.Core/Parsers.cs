using System.Globalization;
using System.Text.RegularExpressions;

namespace EsoAgent.Core;

/// <summary>Output parsers for package managers. Pure functions — unit-tested.</summary>
public static class Parsers
{
    // ---------- winget list ----------
    // winget prints a fixed-width table:
    // Name                  Id                    Version      Available  Source
    // --------------------  --------------------  -----------  ---------  ------
    // Some Program          Publisher.Program     1.2.3                   winget
    // Column positions are taken from the header line; the dashes line confirms the start of data.

    public static IReadOnlyList<SoftwarePackageDto> ParseWingetList(string output)
    {
        var result = new List<SoftwarePackageDto>();
        var lines = output.Replace("\r\n", "\n").Split('\n');
        int headerIdx = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("Name") && lines[i].Contains("Id") && lines[i].Contains("Version")) { headerIdx = i; break; }
        }
        if (headerIdx < 0) return result;
        var header = lines[headerIdx];
        int idCol = header.IndexOf("Id", StringComparison.Ordinal);
        int versionCol = header.IndexOf("Version", StringComparison.Ordinal);
        int availableCol = header.IndexOf("Available", StringComparison.Ordinal);
        int sourceCol = header.IndexOf("Source", StringComparison.Ordinal);
        int versionEnd = availableCol > versionCol ? availableCol : sourceCol;

        string Slice(string line, int from, int to)
        {
            if (from >= line.Length) return "";
            var end = to < 0 || to > line.Length ? line.Length : to;
            return line.Substring(from, Math.Max(0, end - from)).Trim();
        }

        for (int i = headerIdx + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.TrimStart().StartsWith("---")) continue; // separator row
            var name = Slice(line, 0, idCol);
            var id = Slice(line, idCol, versionCol);
            var version = Slice(line, versionCol, versionEnd);
            if (name.Length == 0 && id.Length == 0) continue;
            result.Add(new SoftwarePackageDto
            {
                Name = name.Length > 0 ? name : id,
                Id = id.Length > 0 ? id : null,
                Version = version.Length > 0 ? version : null,
                Source = "winget",
            });
        }
        return result;
    }

    // ---------- dpkg-query ----------
    // Expected format (from dpkg-query -W -f='${Package}\t${Version}\t${Installed-Size}\t${Status}\n'):
    // package<TAB>version<TAB>sizeKiB<TAB>status fields...

    public static IReadOnlyList<SoftwarePackageDto> ParseDpkgList(string output)
    {
        var result = new List<SoftwarePackageDto>();
        foreach (var raw in output.Replace("\r\n", "\n").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var parts = raw.Split('\t');
            if (parts.Length < 2) continue;
            // Only fully installed packages: status contains "install ok installed".
            if (parts.Length >= 4 && !parts[3].Contains("installed")) continue;
            long? size = null;
            if (parts.Length >= 3 && long.TryParse(parts[2], out var kib)) size = kib * 1024;
            result.Add(new SoftwarePackageDto
            {
                Name = parts[0],
                Id = parts[0],
                Version = parts[1],
                SizeBytes = size,
                Source = "dpkg",
            });
        }
        return result;
    }

    // ---------- rpm ----------
    // rpm -qa --qf '%{NAME}\t%{VERSION}-%{RELEASE}\t%{SIZE}\t%{SUMMARY}\n'

    public static IReadOnlyList<SoftwarePackageDto> ParseRpmList(string output)
    {
        var result = new List<SoftwarePackageDto>();
        foreach (var raw in output.Replace("\r\n", "\n").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var parts = raw.Split('\t');
            if (parts.Length < 2) continue;
            long? size = null;
            if (parts.Length >= 3 && long.TryParse(parts[2], out var b)) size = b;
            result.Add(new SoftwarePackageDto
            {
                Name = parts[0],
                Id = parts[0],
                Version = parts[1],
                SizeBytes = size,
                Source = "rpm",
            });
        }
        return result;
    }

    // ---------- pacman ----------
    // pacman -Q output: "name version" per line.

    public static IReadOnlyList<SoftwarePackageDto> ParsePacmanList(string output)
    {
        var result = new List<SoftwarePackageDto>();
        foreach (var raw in output.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var idx = line.LastIndexOf(' ');
            if (idx <= 0) continue;
            result.Add(new SoftwarePackageDto
            {
                Name = line[..idx],
                Id = line[..idx],
                Version = line[(idx + 1)..],
                Source = "pacman",
            });
        }
        return result;
    }
}

/// <summary>
/// Resolves a registry UninstallString / QuietUninstallString into (file, args) and
/// makes sure silent switches are present. Pure and unit-tested.
/// </summary>
public static class UninstallStringResolver
{
    public static (string File, string Args) SplitCommandLine(string commandLine)
    {
        var s = commandLine.Trim();
        if (s.Length == 0) return ("", "");
        if (s[0] == '"')
        {
            var end = s.IndexOf('"', 1);
            if (end > 0) return (s[1..end], s[(end + 1)..].Trim());
        }
        // Unquoted: the exe path ends at .exe (optionally followed by args).
        var m = Regex.Match(s, @"^(.*\.exe)\s*(.*)$", RegexOptions.IgnoreCase);
        if (m.Success) return (m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim());
        var firstSpace = s.IndexOf(' ');
        return firstSpace < 0 ? (s, "") : (s[..firstSpace], s[(firstSpace + 1)..].Trim());
    }

    /// <summary>
    /// Given the registry values, produce the silent uninstall command.
    /// Prefers QuietUninstallString verbatim; otherwise adds the right silent
    /// switches for MSI (msiexec /X {code} /qn /norestart) or generic EXEs.
    /// </summary>
    public static (string File, string Args) ResolveSilent(string? quietUninstallString, string? uninstallString)
    {
        if (!string.IsNullOrWhiteSpace(quietUninstallString))
            return SplitCommandLine(quietUninstallString!);

        if (string.IsNullOrWhiteSpace(uninstallString))
            throw new ArgumentException("No uninstall string available for this program.");

        var (file, args) = SplitCommandLine(uninstallString!);
        var fileName = Path.GetFileName(file);
        if (fileName.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("msiexec", StringComparison.OrdinalIgnoreCase))
        {
            // /I -> /X, ensure quiet + no restart.
            args = Regex.Replace(args, @"/[iI](?=\{)", "/X");
            if (!args.Contains("/qn", StringComparison.OrdinalIgnoreCase) && !args.Contains("/quiet", StringComparison.OrdinalIgnoreCase))
                args += " /qn";
            if (!args.Contains("/norestart", StringComparison.OrdinalIgnoreCase))
                args += " /norestart";
            return (file, args.Trim());
        }
        if (!Regex.IsMatch(args, @"(/S\b|/silent|/quiet|/qn\b|/verysilent)", RegexOptions.IgnoreCase))
            args += " /S";
        return (file, args.Trim());
    }

    public static bool LooksLikeProductCode(string value)
        => Regex.IsMatch(value.Trim(), @"^\{[0-9A-Fa-f-]{36}\}$");

    public static string? FormatInstallDate(string? yyyymmdd)
    {
        if (string.IsNullOrWhiteSpace(yyyymmdd)) return null;
        if (DateTime.TryParseExact(yyyymmdd.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d.ToString("yyyy-MM-dd");
        return yyyymmdd.Trim();
    }
}
