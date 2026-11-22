using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;

namespace EsoAgent.Core;

// =====================================================================
// Item 45 — release channel + in-place update invariants.
//
// ONE version source (Directory.Build.props -> ProductInfo.Version).
// ONE update machinery (UpdateService stage/apply + rollback, v1.0) —
// the release channel is just a signed, scheduled way to FEED it.
// Invariants (owner's words: never install twice):
//   * in-place only: binaries swap inside the existing install dir; the
//     service registration, data dir, device identity and config are
//     never touched by an update;
//   * identity preserved: an updated device is the SAME device (same
//     code, keys, owner password) — never a re-enrollment;
//   * version-gated: same-version and downgrade packages are REFUSED
//     cleanly at staging time (UpdateGate), idempotent by construction;
//   * rollback in place: the v1.0 apply script restores the previous
//     binaries in the same location on failure; staging temp is cleaned.
// =====================================================================

public enum GateDecision { Newer, Same, Downgrade, Unknown }

public static class UpdateGate
{
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        return Version.TryParse(text.Trim().TrimStart('v', 'V'), out version!);
    }

    public static GateDecision Decide(string? current, string? candidate)
    {
        if (!TryParse(candidate, out var c)) return GateDecision.Unknown;
        if (!TryParse(current, out var cur)) return GateDecision.Newer;
        var cmp = c.CompareTo(cur);
        return cmp > 0 ? GateDecision.Newer : cmp == 0 ? GateDecision.Same : GateDecision.Downgrade;
    }

    /// <summary>The package's declared version: the esoagent-version.txt
    /// entry publish.sh stamps into every release zip. Null for legacy
    /// packages built before the stamp existed.</summary>
    public static string? ReadPackageVersion(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entry = zip.Entries.FirstOrDefault(e =>
                e.FullName.Equals("esoagent-version.txt", StringComparison.OrdinalIgnoreCase));
            if (entry == null) return null;
            using var reader = new StreamReader(entry.Open());
            return reader.ReadToEnd().Trim();
        }
        catch { return null; }
    }
}

/// <summary>Agent config block "releaseUpdate". Mode: Auto | Notify | Off.
/// Empty ManifestUrl disables the channel (fleet rollout still works).</summary>
public sealed class ReleaseUpdateConfig
{
    public string Mode { get; set; } = "Notify";
    public string ManifestUrl { get; set; } = "";
    /// <summary>Optional SHA-256 fingerprint pin for the manifest host —
    /// for private mirrors with self-signed certs, exactly like the fleet
    /// server pin. Null = normal public CA validation (GitHub).</summary>
    public string? ManifestFingerprint { get; set; }
    /// <summary>Test/rotation override; null = the pinned release key.</summary>
    public string? PublicKeySpki { get; set; }
    public int CheckHours { get; set; } = 6;

    [JsonIgnore]
    public bool Auto => string.Equals(Mode, "Auto", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore]
    public bool Enabled => !string.Equals(Mode, "Off", StringComparison.OrdinalIgnoreCase)
                           && !string.IsNullOrWhiteSpace(ManifestUrl);
}

public sealed class ReleaseArtifact
{
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
}

public sealed class ReleaseManifest
{
    public string Version { get; set; } = "";
    public DateTimeOffset PublishedAt { get; set; }
    public Dictionary<string, ReleaseArtifact> Artifacts { get; set; } = new();
    public string Signature { get; set; } = "";
}

public static class ReleaseManifestSigning
{
    /// <summary>The pinned release public key (SPKI, base64). The private
    /// half lives ONLY in the release pipeline (GitHub secret
    /// ESO_RELEASE_PRIVATE_KEY) — never in the product, never on devices.</summary>
    public const string PinnedPublicKeySpki =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEIhUhXYIMFVIqNsMxNlfyGLJ6ednnkJ8UBtDWWbS2UZQB3rbTIDKZReZtTNzI66b6ND0BOKjhGr3p68/w9HB6FQ==";

    public static string Canonical(ReleaseManifest m)
    {
        var sb = new StringBuilder("eso-release\n").Append(m.Version);
        foreach (var kv in m.Artifacts.OrderBy(k => k.Key, StringComparer.Ordinal))
            sb.Append('\n').Append(kv.Key).Append('=').Append(kv.Value.Sha256.ToLowerInvariant());
        return sb.ToString();
    }

    public static string Sign(ECDsa key, ReleaseManifest m) =>
        Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(Canonical(m)),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    public static bool Verify(string publicKeySpkiBase64, ReleaseManifest m)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpkiBase64), out _);
            return key.VerifyData(Encoding.UTF8.GetBytes(Canonical(m)),
                Convert.FromBase64String(m.Signature),
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch { return false; }
    }
}

/// <summary>Live state of the release channel on this device, surfaced in
/// /api/status (UpdateAvailable) so the app, panel and tray can badge it.</summary>
public sealed class ReleaseUpdateState
{
    public string? AvailableVersion { get; set; }
    public DateTimeOffset? CheckedAt { get; set; }
    public string? LastError { get; set; }
    public string? NotifiedVersion { get; set; }
    public string? AppliedVersion { get; set; }
}

/// <summary>
/// Consumption-light by design (item 37): one small manifest fetch at
/// start (+30 s settle) and then on a slow timer (default 6 h). Nothing
/// chatty. Notify mode only records + audits; Auto mode downloads,
/// verifies (SHA-256 + the staging version gate) and applies through the
/// v1.0 UpdateService — in place, with its rollback.
/// </summary>
public sealed class ReleaseUpdateService : BackgroundService
{
    private readonly AgentState _state;
    // Direct connections only — like every other agent channel (the fleet
    // fetch does the same). A fleet agent must not silently route its
    // update checks through a desktop system proxy.
    private readonly HttpClient _http;

    public ReleaseUpdateService(AgentState state)
    {
        _state = state;
        var handler = new HttpClientHandler { UseProxy = false };
        var pin = state.Config.ReleaseUpdate.ManifestFingerprint;
        if (!string.IsNullOrWhiteSpace(pin))
        {
            var fp = pin.Trim();
            handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
                cert != null && string.Equals(
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                        new System.Security.Cryptography.X509Certificates.X509Certificate2(cert).GetRawCertData())).ToLowerInvariant(),
                    fp, StringComparison.OrdinalIgnoreCase);
        }
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }

    private ReleaseUpdateConfig Cfg => _state.Config.ReleaseUpdate;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!Cfg.Enabled) return;
        try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch { return; }
        while (!ct.IsCancellationRequested)
        {
            await CheckOnceAsync(ct);
            var hours = Math.Clamp(Cfg.CheckHours, 1, 168);
            try { await Task.Delay(TimeSpan.FromHours(hours), ct); }
            catch { return; }
        }
    }

    /// <summary>Fetches + verifies the manifest and refreshes the state.
    /// Returns a human-readable outcome (also the endpoint's answer).</summary>
    public async Task<string> CheckOnceAsync(CancellationToken ct = default)
    {
        var st = _state.ReleaseUpdates;
        if (!Cfg.Enabled) return "release channel disabled (mode Off or no manifest URL)";
        try
        {
            var json = await _http.GetStringAsync(Cfg.ManifestUrl, ct);
            var manifest = JsonSerializer.Deserialize<ReleaseManifest>(json, EsoJson.Options)
                           ?? throw new InvalidDataException("empty manifest");
            var pub = Cfg.PublicKeySpki ?? ReleaseManifestSigning.PinnedPublicKeySpki;
            if (!ReleaseManifestSigning.Verify(pub, manifest))
            {
                st.LastError = "manifest signature invalid";
                _state.Audit.Write("release", "manifest REJECTED: signature invalid", "refused");
                return st.LastError;
            }
            st.CheckedAt = DateTimeOffset.UtcNow;
            st.LastError = null;
            var decision = UpdateGate.Decide(ProductInfo.Version, manifest.Version);
            if (decision != GateDecision.Newer)
            {
                st.AvailableVersion = null;
                return $"up to date (current {ProductInfo.Version}, manifest {manifest.Version})";
            }
            st.AvailableVersion = manifest.Version;
            if (st.NotifiedVersion != manifest.Version)
            {
                st.NotifiedVersion = manifest.Version;
                _state.Audit.Write("release", $"update available: v{manifest.Version} (mode {Cfg.Mode})", "ok");
            }
            if (Cfg.Auto) return await DownloadAndApplyAsync(manifest, ct);
            return $"update available: v{manifest.Version}";
        }
        catch (Exception ex)
        {
            st.LastError = ex.InnerException == null ? ex.Message : $"{ex.Message} ({ex.InnerException.Message})";
            return $"check failed: {st.LastError}";
        }
    }

    /// <summary>Downloads this platform's agent artifact, verifies its
    /// hash, stages it through the version gate and applies it IN PLACE
    /// via the v1.0 machinery. Used by Auto mode and by the operator's
    /// explicit apply-release call.</summary>
    public async Task<string> DownloadAndApplyAsync(ReleaseManifest manifest, CancellationToken ct = default)
    {
        var key = OperatingSystem.IsWindows() ? "windows-agent" : "linux-agent";
        if (!manifest.Artifacts.TryGetValue(key, out var artifact))
            return $"manifest has no {key} artifact";
        var tmp = Path.Combine(Path.GetTempPath(), $"eso-release-{Guid.NewGuid():N}.zip");
        try
        {
            await using (var src = await _http.GetStreamAsync(artifact.Url, ct))
            await using (var dst = File.Create(tmp))
                await src.CopyToAsync(dst, ct);
            var staged = await _state.Update.StageVerifiedFileAsync(tmp, artifact.Sha256, ct);
            _state.Audit.Write("release", $"v{manifest.Version} staged (sha256 {staged[..12]}…); applying in place", "ok");
            var applied = _state.Update.ApplyStaged(AppContext.BaseDirectory);
            if (applied.Success) _state.ReleaseUpdates.AppliedVersion = manifest.Version;
            return applied.Message;
        }
        catch (Exception ex)
        {
            _state.Audit.Write("release", $"apply of v{manifest.Version} failed before apply: {ex.Message}", "error");
            return $"apply failed: {ex.Message}";
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    /// <summary>Fetch + verify the current manifest (for apply-release).</summary>
    public async Task<ReleaseManifest?> FetchManifestAsync(CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync(Cfg.ManifestUrl, ct);
        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(json, EsoJson.Options);
        if (manifest == null) return null;
        var pub = Cfg.PublicKeySpki ?? ReleaseManifestSigning.PinnedPublicKeySpki;
        return ReleaseManifestSigning.Verify(pub, manifest) ? manifest : null;
    }
}

/// <summary>
/// The one apply-an-available-update path (UI Phase 5). Until now the logic
/// lived inline in POST /api/update/apply-release, which the tray's host
/// window cannot reach (it speaks only the local control pipe). Both now
/// call this: fetch the signed manifest, gate on the version, download +
/// verify + apply, audit the outcome.
/// </summary>
public static class ReleaseApply
{
    public static async Task<(bool Ok, string Message, string? Version)> ApplyAvailableAsync(
        AgentState state, CancellationToken ct = default)
    {
        if (state.Release == null)
            return (false, "release service not running", null);
        var manifest = await state.Release.FetchManifestAsync(ct);
        if (manifest == null)
            return (false, "no verifiable manifest (channel disabled or signature invalid)", null);
        if (UpdateGate.Decide(ProductInfo.Version, manifest.Version) != GateDecision.Newer)
            return (false, $"nothing newer (current {ProductInfo.Version}, manifest {manifest.Version})", manifest.Version);
        var outcome = await state.Release.DownloadAndApplyAsync(manifest, ct);
        state.Audit.Write("update/apply", $"release v{manifest.Version}: {outcome}", "ok");
        return (true, outcome, manifest.Version);
    }
}
