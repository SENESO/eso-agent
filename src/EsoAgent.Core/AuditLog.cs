using System.Text;
using System.Text.Json;

namespace EsoAgent.Core;

/// <summary>
/// Append-only audit log: every API call lands here (timestamp, endpoint, summary,
/// result) so the owner can always see exactly what the agent was told to do.
/// Rolls over at a size cap, keeping a bounded number of files. Thread-safe.
/// </summary>
public sealed class AuditLog
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly int _keepFiles;

    public AuditLog(string path, long maxBytes = 5 * 1024 * 1024, int keepFiles = 5)
    {
        _path = path;
        _maxBytes = maxBytes;
        _keepFiles = keepFiles;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    }

    public string FilePath => _path;

    public void Write(string endpoint, string summary, string result)
    {
        var line = JsonSerializer.Serialize(new
        {
            ts = DateTimeOffset.UtcNow,
            endpoint,
            summary,
            result,
        }, EsoJson.Options);
        lock (_gate)
        {
            try
            {
                RotateIfNeeded(Encoding.UTF8.GetByteCount(line) + 1);
                File.AppendAllText(_path, line + "\n");
            }
            catch
            {
                // Auditing must never take the agent down; a failed write is lost, not fatal.
            }
        }
    }

    private void RotateIfNeeded(int incomingBytes)
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length + incomingBytes <= _maxBytes) return;
        for (int i = _keepFiles - 1; i >= 1; i--)
        {
            var src = i == 1 ? _path : $"{_path}.{i - 1}";
            var dst = $"{_path}.{i}";
            if (File.Exists(src)) File.Move(src, dst, overwrite: true);
        }
    }

    /// <summary>
    /// Lines from a zero-based offset (v1.2, for the activity shipper): returns
    /// up to <paramref name="max"/> lines starting at <paramref name="fromOffset"/>
    /// plus the file's current total line count. If the file rotated (total &lt;
    /// offset), the caller resets its offset to 0 and re-reads — re-shipped
    /// lines dedupe server-side by content hash.
    /// </summary>
    public (string[] Lines, int Total) ReadRange(int fromOffset, int max)
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return (Array.Empty<string>(), 0);
            var all = File.ReadAllLines(_path);
            if (fromOffset < 0) fromOffset = 0;
            if (fromOffset >= all.Length) return (Array.Empty<string>(), all.Length);
            var count = Math.Min(max, all.Length - fromOffset);
            return (all[fromOffset..(fromOffset + count)], all.Length);
        }
    }

    /// <summary>Last N lines of the audit log (newest last).</summary>
    public string[] Tail(int lines)
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return Array.Empty<string>();
            return TailFile(_path, lines);
        }
    }

    /// <summary>Tail helper for arbitrary files (the /api/logs/file endpoint).</summary>
    public static string[] TailFile(string path, int lines)
    {
        if (lines <= 0) lines = 100;
        if (lines > 5000) lines = 5000;
        // Files being tailed here are logs; reading whole is acceptable up to a guard size.
        var info = new FileInfo(path);
        if (info.Length > 64 * 1024 * 1024) throw new InvalidOperationException("File too large to tail (64 MB cap).");
        var all = File.ReadAllLines(path);
        return all.Length <= lines ? all : all[^lines..];
    }
}
