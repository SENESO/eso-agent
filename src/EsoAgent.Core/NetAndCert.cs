using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace EsoAgent.Core;

public static class NetHelpers
{
    /// <summary>
    /// Request-body ceiling applied by BOTH Kestrel hosts (the agent in
    /// EsoAgentServer and the fleet server in Program). Kestrel's default
    /// (~30 MB) aborted real rollout / self-update package uploads (~41 MB)
    /// mid-stream — proven live on the box (BUILD-REPORT §12, live script
    /// §F: the upload died in the client while the version gate behind it
    /// was fine). Package uploads are the only large request bodies in the
    /// product, and both upload endpoints already stream the body to disk;
    /// 256 MB leaves headroom for future packages while still bounding a
    /// single request. Both hosts call ApplyRequestBodyLimit so the two
    /// can never drift apart; the test suite pins the value.
    /// </summary>
    public const long MaxRequestBodyBytes = 256L * 1024 * 1024;

    public static void ApplyRequestBodyLimit(KestrelServerOptions kestrel) =>
        kestrel.Limits.MaxRequestBodySize = MaxRequestBodyBytes;

    /// <summary>Tailscale assigns addresses from 100.64.0.0/10 (CGNAT range).</summary>
    public static bool IsTailnetAddress(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 100 && b[1] >= 64 && b[1] <= 127;
    }

    public static IReadOnlyList<string> DiscoverTailnetIps()
    {
        var result = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                    if (IsTailnetAddress(addr.Address)) result.Add(addr.Address.ToString());
            }
        }
        catch { /* discovery is best-effort */ }
        return result;
    }

    /// <summary>
    /// Whether an address is worth reporting to the fleet server at enrollment
    /// (v1.1): a real interface address — not loopback, not link-local
    /// (169.254.0.0/16, fe80::/10), not unspecified, not multicast. Tailnet
    /// (100.64/10), LAN and mesh addresses all qualify.
    /// </summary>
    public static bool IsReportableIp(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return false;
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.None)) return false;
        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            if (bytes[0] == 169 && bytes[1] == 254) return false;   // link-local
            if (bytes[0] >= 224 && bytes[0] <= 239) return false;   // multicast
            return true;
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6Multicast) return false;
            if (ip.IsIPv4MappedToIPv6) return IsReportableIp(ip.MapToIPv4());
            return true;
        }
        return false;
    }

    /// <summary>
    /// All reportable IPs of this machine's up interfaces (v1.1) — what the agent
    /// puts in its fleet hello, so the server's device record carries real
    /// addresses (direct connections / desktop targeting use them). IPv4 first.
    /// </summary>
    public static IReadOnlyList<string> DiscoverInterfaceIps()
    {
        var v4 = new List<string>();
        var v6 = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (!IsReportableIp(addr.Address)) continue;
                    var s = addr.Address.ToString();
                    var list = addr.Address.AddressFamily == AddressFamily.InterNetwork ? v4 : v6;
                    if (!list.Contains(s)) list.Add(s);
                }
            }
        }
        catch { /* discovery is best-effort */ }
        v4.AddRange(v6);
        return v4;
    }

    /// <summary>
    /// Bind set for Kestrel: configured addresses if any, otherwise the machine's
    /// Tailscale address(es) plus loopback. Never 0.0.0.0 — the agent must not be
    /// reachable from the LAN/internet, only over the owner's tailnet or locally.
    /// If no tailnet address exists yet, loopback only (safe default).
    /// </summary>
    public static IReadOnlyList<string> ResolveBindAddresses(AgentConfig cfg)
    {
        if (cfg.BindAddresses.Count > 0) return cfg.BindAddresses;
        var addrs = new List<string> { "127.0.0.1" };
        foreach (var ip in DiscoverTailnetIps())
            if (!addrs.Contains(ip)) addrs.Add(ip);
        return addrs;
    }
}

/// <summary>
/// Self-signed TLS certificate for the agent API, generated on first run and
/// stored as a PFX in the data directory. The client pins its SHA-256 fingerprint,
/// so a self-signed cert is as strong as a CA one for this single-owner use.
/// </summary>
public static class CertManager
{
    public static X509Certificate2 LoadOrCreate(AgentConfig cfg)
    {
        var pfxPath = cfg.ResolvePath(cfg.CertFile);
        if (File.Exists(pfxPath))
        {
            try { return new X509Certificate2(pfxPath, (string?)null, X509KeyStorageFlags.Exportable); }
            catch { /* corrupt — regenerate below */ }
        }
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN=EsoAgent ({Environment.MachineName})", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        foreach (var ip in NetHelpers.DiscoverTailnetIps())
            if (IPAddress.TryParse(ip, out var parsed)) san.AddIpAddress(parsed);
        req.CertificateExtensions.Add(san.Build());
        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        var bytes = cert.Export(X509ContentType.Pfx);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pfxPath))!);
        File.WriteAllBytes(pfxPath, bytes);
        File.WriteAllText(pfxPath + ".fingerprint", Fingerprint(cert));
        return new X509Certificate2(bytes, (string?)null, X509KeyStorageFlags.Exportable);
    }

    public static string Fingerprint(X509Certificate2 cert)
        => Convert.ToHexString(SHA256.HashData(cert.RawData)).ToLowerInvariant();
}
