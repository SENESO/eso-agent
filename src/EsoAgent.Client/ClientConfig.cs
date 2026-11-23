using System.Text.Json;
using EsoAgent.Core;

namespace EsoAgent.Client;

/// <summary>
/// Client configuration: which agent to talk to, the pinned cert fingerprint and
/// the shared token. Resolution order: --config &lt;path&gt;, $ESOAGENT_CLIENT_CONFIG,
/// ~/.esoagent/client.json. The token is used for signing only and NEVER printed.
/// </summary>
public sealed class ClientConfig
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 8737;
    public string Fingerprint { get; set; } = "";
    public string Token { get; set; } = "";

    // Fleet mode (optional): the fleet server + this operator's signing key.
    public string? FleetUrl { get; set; }              // e.g. https://100.64.0.5:8740
    public string? FleetFingerprint { get; set; }      // SHA-256 pin of the fleet server cert
    public string? OperatorKeyFile { get; set; }       // path to operator.key (ECDSA PKCS8 PEM)

    [System.Text.Json.Serialization.JsonIgnore]
    public byte[] TokenBytes { get; private set; } = Array.Empty<byte>();

    public string BaseUrl => $"https://{Host}:{Port}";

    /// <summary>A copy pointed at another host/port (direct-by-IP targeting); token + fingerprint carry over.</summary>
    public ClientConfig WithEndpoint(string host, int port)
    {
        var clone = (ClientConfig)MemberwiseClone();
        clone.Host = host;
        clone.Port = port;
        clone.TokenBytes = TokenBytes;
        return clone;
    }

    public static ClientConfig Load(string? explicitPath)
    {
        var path = explicitPath
            ?? Environment.GetEnvironmentVariable("ESOAGENT_CLIENT_CONFIG")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".esoagent", "client.json");
        // Backup-aware (defect-8): a torn client.json recovers from its
        // last-good backup instead of breaking every console operation;
        // only a genuinely missing file is "not found", and a doubly-
        // corrupt one is reported plainly, never silently replaced.
        var status = StateFile.TryReadJson<ClientConfig>(path, out var cfg);
        if (status == StateFileRead.Missing)
            throw new FileNotFoundException($"Client config not found at {path}. Create it with host, port, fingerprint and token.", path);
        if (cfg == null)
            throw new InvalidDataException($"Client config at {path} is unreadable (corrupt, with no good backup).");
        if (string.IsNullOrWhiteSpace(cfg.Host)) throw new InvalidDataException("Client config missing 'host'.");
        cfg.TokenBytes = Convert.FromBase64String(cfg.Token.Trim());
        return cfg;
    }
}
