using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace EsoAgent.Core;

/// <summary>
/// Self-update: the client uploads a zip package whose SHA-256 is signed with the
/// same HMAC token (signature = HMAC(token, "update\n" + sha256hex)). Apply stages
/// the package, then hands off to a detached platform script (update-apply.ps1 /
/// update-apply.sh shipped next to the binaries) which stops the service, swaps
/// files keeping a backup, restarts, health-checks /api/status locally, and rolls
/// back from the backup if the new build does not come up.
/// </summary>
public sealed class UpdateService
{
    private readonly AgentConfig _cfg;
    private readonly IProcessRunner _runner;

    public UpdateService(AgentConfig cfg, IProcessRunner runner)
    {
        _cfg = cfg;
        _runner = runner;
    }

    public string StagingDir => Path.Combine(_cfg.ResolvedDataDir, "update-staging");

    public static string SignPackage(byte[] token, string sha256Hex)
    {
        using var hmac = new HMACSHA256(token);
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes("update\n" + sha256Hex.ToLowerInvariant()))).ToLowerInvariant();
    }

    public static bool VerifyPackageSignature(byte[] token, string sha256Hex, string signatureHex)
    {
        try
        {
            var expected = Convert.FromHexString(SignPackage(token, sha256Hex));
            var provided = Convert.FromHexString(signatureHex.Trim());
            return expected.Length == provided.Length && CryptographicOperations.FixedTimeEquals(expected, provided);
        }
        catch (FormatException) { return false; }
    }

    /// <summary>Stores an uploaded package after verifying hash + signature. Returns the verified hash.</summary>
    public async Task<string> StageAsync(Stream packageStream, string expectedSha256Hex, string signatureHex, byte[] token, CancellationToken ct = default)
    {
        Directory.CreateDirectory(StagingDir);
        var tmp = Path.Combine(StagingDir, "package.zip.tmp");
        var final = Path.Combine(StagingDir, "package.zip");
        await using (var fs = File.Create(tmp))
            await packageStream.CopyToAsync(fs, ct);
        var actual = FileTransferService.HashFile(tmp);
        if (!string.Equals(actual, expectedSha256Hex.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(tmp);
            throw new InvalidDataException($"Package SHA-256 mismatch: expected {expectedSha256Hex}, got {actual}.");
        }
        if (!VerifyPackageSignature(token, actual, signatureHex))
        {
            File.Delete(tmp);
            throw new UnauthorizedAccessException("Package signature invalid.");
        }
        // Sanity: must be a readable zip.
        using (var zip = ZipFile.OpenRead(tmp)) { _ = zip.Entries.Count; }
        try { EnforceVersionGate(tmp); }
        catch { File.Delete(tmp); throw; }
        File.Move(tmp, final, overwrite: true);
        return actual;
    }

    /// <summary>
    /// Stages a package whose authenticity was established by an ECDSA
    /// signature instead of the HMAC package signature (fleet rollout, v1.2:
    /// the caller verified OperatorSigning.VerifyUpdateFetch and passes the
    /// bytes here; the SHA-256 + zip readability are re-verified regardless).
    /// Returns the verified hash.
    /// </summary>
    public async Task<string> StageVerifiedAsync(Stream packageStream, string expectedSha256Hex, CancellationToken ct = default)
    {
        Directory.CreateDirectory(StagingDir);
        var tmp = Path.Combine(StagingDir, "package.zip.tmp");
        var final = Path.Combine(StagingDir, "package.zip");
        await using (var fs = File.Create(tmp))
            await packageStream.CopyToAsync(fs, ct);
        var actual = FileTransferService.HashFile(tmp);
        if (!string.Equals(actual, expectedSha256Hex.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(tmp);
            throw new InvalidDataException($"Package SHA-256 mismatch: expected {expectedSha256Hex}, got {actual}.");
        }
        using (var zip = ZipFile.OpenRead(tmp)) { _ = zip.Entries.Count; }
        try { EnforceVersionGate(tmp); }
        catch { File.Delete(tmp); throw; }
        File.Move(tmp, final, overwrite: true);
        return actual;
    }


    /// <summary>
    /// The in-place invariant gate (item 45): a stamped package may only
    /// ever move the install FORWARD. Same version = clean refusal
    /// (idempotent: running the updater twice converges, never
    /// reinstalls); downgrade = clean refusal. Legacy packages without a
    /// version stamp predate the gate and are allowed through.
    /// </summary>
    private static void EnforceVersionGate(string zipPath)
    {
        var pkg = UpdateGate.ReadPackageVersion(zipPath);
        if (pkg == null) return;
        switch (UpdateGate.Decide(ProductInfo.Version, pkg))
        {
            case GateDecision.Same:
                throw new InvalidDataException($"Already at version {pkg} — nothing to do.");
            case GateDecision.Downgrade:
                throw new InvalidDataException($"Downgrade refused: package is {pkg}, this device runs {ProductInfo.Version}.");
            case GateDecision.Unknown:
                throw new InvalidDataException($"Package version stamp '{pkg}' is not a valid version.");
        }
    }

    /// <summary>Stages a package from a local file (release channel).</summary>
    public async Task<string> StageVerifiedFileAsync(string path, string expectedSha256Hex, CancellationToken ct = default)
    {
        await using var fs = File.OpenRead(path);
        return await StageVerifiedAsync(fs, expectedSha256Hex, ct);
    }

    /// <summary>Extracts the staged package and launches the platform apply script, detached.</summary>
    public OperationResult ApplyStaged(string appDir)
    {
        var package = Path.Combine(StagingDir, "package.zip");
        if (!File.Exists(package)) return new OperationResult { Success = false, Message = "No staged update package." };
        var extractDir = Path.Combine(StagingDir, "extracted");
        if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true);
        ZipFile.ExtractToDirectory(package, extractDir);

        string scriptName = OperatingSystem.IsWindows() ? "update-apply.ps1" : "update-apply.sh";
        var script = Path.Combine(appDir, scriptName);
        if (!File.Exists(script))
            return new OperationResult { Success = false, Message = $"Apply script {scriptName} not found next to the agent binaries." };

        var req = OperatingSystem.IsWindows()
            ? new ExecRequest { File = "powershell.exe", Args = new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, extractDir, appDir } }
            : new ExecRequest { File = "bash", Args = new[] { script, extractDir, appDir } };
        var pid = _runner.LaunchDetached(req);
        return new OperationResult { Success = true, Message = $"Update apply script launched (pid {pid}). The service will restart; verify /api/status afterwards." };
    }
}
