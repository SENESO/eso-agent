using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace EsoAgent.App;

/// <summary>
/// Client-side plumbing for the pairing wizard — every mechanism here
/// already exists in the product; the wizard only gives it a face:
///   · the fleet fingerprint is LEARNED by opening one TLS connection to
///     the address the user typed and hashing the certificate the server
///     presents (the same SHA-256 the fleet panel shows). The user never
///     types a hash — they compare a short verification code, and the
///     learned fingerprint is pinned in the config exactly as the CLI's
///     hand-written client.json pins it today.
///   · the operator key is the `eso keygen` operation (ECDSA P-256,
///     PKCS#8 PEM at ~/.esoagent/operator.key, never overwritten). Only
///     the PUBLIC key ever leaves this machine — the fleet owner adds it
///     on the server, which is what authorises this console.
/// No new server calls are invented: the connection test is the ordinary
/// signed GetDevicesAsync through FleetApiClient.
/// </summary>
public static class PairingSupport
{
    /// <summary>Where `eso keygen` puts the operator key by default.</summary>
    public static string DefaultOperatorKeyPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".esoagent", "operator.key");

    /// <summary>Accepts a full URL or a bare host[:port]; the fleet server is HTTPS on 8740 by default.</summary>
    public static string? NormalizeFleetUrl(string? input)
    {
        var s = input?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        if (!s.Contains("://", StringComparison.Ordinal)) s = "https://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != "https" && uri.Scheme != "http") return null;
        var port = uri.IsDefaultPort ? 8740 : uri.Port;
        return $"{uri.Scheme}://{uri.Host}:{port}";
    }

    /// <summary>One TLS handshake against the fleet address; returns the server's
    /// certificate SHA-256 (lowercase hex — the config's pin format). Any HTTP
    /// response (even an error status) proves the handshake happened; only a
    /// transport failure throws.</summary>
    public static async Task<string> ProbeFingerprintAsync(string fleetUrl)
    {
        string? fingerprint = null;
        var handler = new HttpClientHandler { UseProxy = false };
        handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
        {
            if (cert != null)
                fingerprint = Convert.ToHexString(SHA256.HashData(cert.GetRawCertData())).ToLowerInvariant();
            return true; // probe only: the pin is what the user confirms next, never this callback
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        using var res = await http.GetAsync(fleetUrl + "/", HttpCompletionOption.ResponseHeadersRead);
        return fingerprint ?? throw new InvalidOperationException("The server presented no certificate.");
    }

    /// <summary>The human comparison form of a fingerprint: the first ten hex
    /// digits in two groups, an ellipsis, and the last four — enough to match
    /// against the fleet panel's value at a glance, short enough to read aloud.</summary>
    public static string VerificationCode(string fingerprintHex)
    {
        var fp = fingerprintHex.Trim().ToUpperInvariant();
        if (fp.Length < 14) return fp;
        return $"{fp[..5]} {fp[5..10]} … {fp[^4..]}";
    }

    /// <summary>Loads the operator key at <paramref name="keyPath"/> or creates it
    /// (the keygen operation). Returns the base64 SubjectPublicKeyInfo — the only
    /// part anyone else ever sees. An existing key is never overwritten.</summary>
    public static (bool Created, string PublicKeyBase64) EnsureOperatorKey(string keyPath)
    {
        using var key = ECDsa.Create();
        if (File.Exists(keyPath))
        {
            key.ImportFromPem(File.ReadAllText(keyPath));
            return (false, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(keyPath))!);
        using var fresh = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        // CreateNew: if a key appeared since the check above, fail rather than replace someone's identity.
        using (var fs = new FileStream(keyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(fs))
            writer.Write(fresh.ExportPkcs8PrivateKeyPem());
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch { /* permissions are hardening, not a pairing blocker */ }
        return (true, Convert.ToBase64String(fresh.ExportSubjectPublicKeyInfo()));
    }

    /// <summary>Finds THIS computer in the fleet device list, when its agent is
    /// enrolled: first by a local-IP match against the devices' recorded
    /// addresses (strong), then by the machine name (the name an agent
    /// enrolls with by default). Null when this computer is not a fleet device.</summary>
    public static DeviceItem? FindThisDevice(IEnumerable<DeviceItem> devices)
    {
        var list = devices.Where(d => d.IsFleet).ToList();
        var localIps = LocalIpAddresses();
        var byIp = list.FirstOrDefault(d =>
            (d.VirtualIp != null && localIps.Contains(d.VirtualIp)) || d.Ips.Any(localIps.Contains));
        if (byIp != null) return byIp;
        var machine = Environment.MachineName;
        return list.FirstOrDefault(d => string.Equals(d.Name, machine, StringComparison.OrdinalIgnoreCase));
    }

    private static HashSet<string> LocalIpAddresses()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                    if (addr.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                        set.Add(addr.Address.ToString());
            }
        }
        catch { /* identity matching is best-effort; the name fallback still applies */ }
        return set;
    }
}
