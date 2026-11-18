using System.Text.RegularExpressions;

namespace EsoAgent.Core;

/// <summary>First frame the client sends on the /api/logs/follow WebSocket.</summary>
public sealed class LogFollowRequest
{
    public string Path { get; set; } = "";
    public int TailLines { get; set; } = 50;
    /// <summary>Optional line filter: a regular expression when it compiles,
    /// otherwise a literal substring (see LogFollowFilter).</summary>
    public string? Pattern { get; set; }
}

/// <summary>Line filter for log follow — pure, unit-tested.</summary>
public static class LogFollowFilter
{
    public static bool Matches(string line, string? pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return true;
        var regex = TryCompile(pattern);
        return regex != null
            ? regex.IsMatch(line)
            : line.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static Regex? TryCompile(string pattern)
    {
        try { return new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)); }
        catch { return null; }
    }
}
