using System.Text.Json;

namespace EsoAgent.Core;

/// <summary>How a state-file read turned out. <see cref="Corrupt"/> means
/// bytes exist but neither the file nor its backup parses — the caller
/// must preserve them (see <see cref="StateFile.QuarantineCorrupt"/>) and
/// must never overwrite the file with defaults on the back of this read.</summary>
public enum StateFileRead
{
    Missing,
    Ok,
    RecoveredFromBackup,
    Corrupt,
}

/// <summary>
/// The one way state files are written and read (defect-8 hardening,
/// 2026-11-22). Before this helper the product's JSON state was a
/// patchwork: some writers used temp-file + replace, several overwrote in
/// place (a crash, kill or laptop sleep mid-write leaves a torn file),
/// and nearly every reader treated a parse failure as "empty / not
/// enrolled / not configured" and let the next save cement the loss.
///
/// Rules enforced here:
///  • Writes are atomic (temp sibling + replace) and content-gated (an
///    unchanged payload is not rewritten — the §24 pattern, generalized).
///  • Every write also refreshes a <c>.bak</c> sibling holding the same
///    content: the last successfully written state.
///  • Reads try main, then the backup. A backup recovery restores the
///    main file from the backup bytes (self-heal) and says so.
///  • When both are unreadable the result is Corrupt and the bytes are
///    left exactly as found — recovery is a human decision, aided by
///    <see cref="QuarantineCorrupt"/>.
/// Credential files (private keys, the API token) deliberately do NOT get
/// backups: their writers use the atomic path with keepBackup: false so
/// rotation never leaves a second copy of a secret behind.
/// </summary>
public static class StateFile
{
    public static string BackupPath(string path) => path + ".bak";

    /// <summary>Atomic, content-gated text write. Returns true when the
    /// main file actually changed. Throws on IO failure — callers that
    /// treat persistence as best-effort keep their own try/catch, as
    /// before; what they gain is that a failed write can no longer tear
    /// the previous good file.</summary>
    public static bool WriteText(string path, string content, bool keepBackup = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (File.Exists(path) && File.ReadAllText(path) == content)
        {
            if (keepBackup) RefreshBackup(path, content);
            return false;
        }
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
        if (keepBackup) RefreshBackup(path, content);
        return true;
    }

    private static void RefreshBackup(string path, string content)
    {
        try
        {
            var bak = BackupPath(path);
            if (File.Exists(bak) && File.ReadAllText(bak) == content) return;
            var tmp = bak + ".tmp";
            File.WriteAllText(tmp, content);
            File.Move(tmp, bak, overwrite: true);
        }
        catch { /* a backup failure never fails the write it shadows */ }
    }

    /// <summary>Serializes and writes through <see cref="WriteText"/>.</summary>
    public static bool WriteJson<T>(string path, T value, JsonSerializerOptions? options = null, bool keepBackup = true)
        => WriteText(path, JsonSerializer.Serialize(value, options ?? EsoJson.Options), keepBackup);

    /// <summary>Text read with backup fallback: the main file's text when
    /// it exists and is non-empty, else the backup's, else null.</summary>
    public static string? ReadText(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path);
                if (!string.IsNullOrEmpty(text)) return text;
            }
            var bak = BackupPath(path);
            if (File.Exists(bak))
            {
                var text = File.ReadAllText(bak);
                if (!string.IsNullOrEmpty(text)) return text;
            }
        }
        catch { }
        return null;
    }

    /// <summary>JSON read with backup recovery, per the enum contract.
    /// A backup recovery also rewrites the main file from the backup
    /// bytes so the next read is a clean <see cref="StateFileRead.Ok"/>.</summary>
    public static StateFileRead TryReadJson<T>(string path, out T? value, JsonSerializerOptions? options = null)
    {
        value = default;
        options ??= EsoJson.Options;
        if (File.Exists(path))
        {
            try
            {
                value = JsonSerializer.Deserialize<T>(File.ReadAllText(path), options);
                if (value != null) return StateFileRead.Ok;
            }
            catch { /* fall through to the backup */ }
            return TryBackup(path, options, out value)
                ? StateFileRead.RecoveredFromBackup
                : StateFileRead.Corrupt;
        }
        if (TryBackup(path, options, out value)) return StateFileRead.RecoveredFromBackup;
        return File.Exists(BackupPath(path)) ? StateFileRead.Corrupt : StateFileRead.Missing;
    }

    private static bool TryBackup<T>(string path, JsonSerializerOptions options, out T? value)
    {
        value = default;
        var bak = BackupPath(path);
        if (!File.Exists(bak)) return false;
        string bytes;
        try
        {
            bytes = File.ReadAllText(bak);
            value = JsonSerializer.Deserialize<T>(bytes, options);
        }
        catch { value = default; return false; }
        if (value == null) return false;
        // Self-heal: the main file returns to the last good bytes. The
        // backup itself is left untouched (it IS the good copy).
        try { WriteText(path, bytes, keepBackup: false); } catch { }
        return true;
    }

    /// <summary>Moves an unrecoverable state file (and its backup, if also
    /// unreadable) aside to <c>&lt;name&gt;.corrupt-&lt;utc timestamp&gt;</c>
    /// so a store can start clean without destroying the evidence. Returns
    /// the quarantine path, or null when there was nothing to move.</summary>
    public static string? QuarantineCorrupt(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
            var dest = $"{path}.corrupt-{stamp}";
            File.Move(path, dest);
            return dest;
        }
        catch { return null; }
    }
}
