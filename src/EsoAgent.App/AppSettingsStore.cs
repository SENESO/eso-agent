using System.Text.Json.Nodes;

namespace EsoAgent.App;

/// <summary>
/// Merge read/write access to app-settings.json — the App-local settings
/// file that lives beside the client config (the shell already keeps the
/// language and the tray-hint flag there). Phase 2 adds the home shell's
/// own state: favourite device codes, recent connections, and the console
/// name from the pairing wizard. Every write merges, so independent keys
/// never clobber each other; every failure is swallowed — a broken
/// settings file must never block startup or a connection.
/// </summary>
public static class AppSettingsStore
{
    /// <summary>The settings path for a given client-config path (same rule as the shell).</summary>
    public static string PathFor(string configPath)
        => Path.Combine(Path.GetDirectoryName(configPath)!, "app-settings.json");

    public static JsonObject Load(string settingsPath)
    {
        try
        {
            if (File.Exists(settingsPath)
                && JsonNode.Parse(File.ReadAllText(settingsPath)) is JsonObject obj)
                return obj;
        }
        catch { /* fall through to empty */ }
        return new JsonObject();
    }

    public static void Update(string settingsPath, Action<JsonObject> edit)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            var obj = Load(settingsPath);
            edit(obj);
            File.WriteAllText(settingsPath, obj.ToJsonString());
        }
        catch { }
    }

    public static string? GetString(JsonObject obj, string key)
        => obj.TryGetPropertyValue(key, out var node) && node is JsonValue v && v.TryGetValue<string>(out var s)
            ? s
            : null;

    public static List<string> GetStringList(JsonObject obj, string key)
    {
        var list = new List<string>();
        if (obj.TryGetPropertyValue(key, out var node) && node is JsonArray arr)
            foreach (var item in arr)
                if (item is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s))
                    list.Add(s);
        return list;
    }

    public static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var arr = new JsonArray();
        foreach (var v in values) arr.Add(v);
        return arr;
    }
}

/// <summary>One recent connection on the home screen (fleet devices only — the direct device is always listed).</summary>
public sealed class RecentEntry
{
    public required string Code { get; init; }
    public required string Name { get; init; }
    public DateTimeOffset At { get; init; }

    public static List<RecentEntry> LoadList(JsonObject obj)
    {
        var list = new List<RecentEntry>();
        if (obj.TryGetPropertyValue("recents", out var node) && node is JsonArray arr)
            foreach (var item in arr)
            {
                if (item is not JsonObject o) continue;
                var code = AppSettingsStore.GetString(o, "code");
                if (string.IsNullOrEmpty(code)) continue;
                var at = AppSettingsStore.GetString(o, "at");
                list.Add(new RecentEntry
                {
                    Code = code,
                    Name = AppSettingsStore.GetString(o, "name") ?? code,
                    At = at != null && DateTimeOffset.TryParse(at, out var parsed) ? parsed : DateTimeOffset.MinValue,
                });
            }
        return list;
    }

    public static JsonArray ToJsonArray(IEnumerable<RecentEntry> entries)
    {
        var arr = new JsonArray();
        foreach (var e in entries)
            arr.Add(new JsonObject
            {
                ["code"] = e.Code,
                ["name"] = e.Name,
                ["at"] = e.At.ToString("O"),
            });
        return arr;
    }
}
