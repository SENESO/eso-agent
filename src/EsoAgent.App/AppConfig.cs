using System.Text.Json;
using EsoAgent.Core;

namespace EsoAgent.App;

/// <summary>
/// Editable mirror of the shared client config file — the exact JSON the
/// <c>eso</c> CLI reads (~/.esoagent/client.json, or $ESOAGENT_CLIENT_CONFIG).
/// The app never invents a second config format: Settings edits this file,
/// and connections are built by loading it through
/// <see cref="EsoAgent.Client.ClientConfig.Load"/> so the CLI and the app
/// can never drift apart.
/// </summary>
public sealed class AppConfig
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 8737;
    public string Fingerprint { get; set; } = "";
    public string Token { get; set; } = "";
    public string? FleetUrl { get; set; }
    public string? FleetFingerprint { get; set; }
    public string? OperatorKeyFile { get; set; }

    /// <summary>Same resolution order as the CLI: $ESOAGENT_CLIENT_CONFIG, else ~/.esoagent/client.json.</summary>
    public static string DefaultPath()
        => Environment.GetEnvironmentVariable("ESOAGENT_CLIENT_CONFIG")
           ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".esoagent", "client.json");

    /// <summary>Tolerant load for editing: a missing file yields defaults
    /// instead of an error. Backup-aware (defect-8): a torn client.json
    /// opens as its last-good self; a doubly-corrupt one opens as
    /// defaults for editing, with its bytes left on disk untouched
    /// until the user actually saves.</summary>
    public static AppConfig LoadForEdit(string path)
    {
        StateFile.TryReadJson<AppConfig>(path, out var cfg);
        return cfg ?? new AppConfig();
    }

    public void Save(string path)
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions(EsoJson.Options) { WriteIndented = true });
        StateFile.WriteText(path, json + "\n");
    }
}
