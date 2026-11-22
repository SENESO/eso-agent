using System.Text.Json;

namespace EsoAgent.Core;

/// <summary>
/// Doctor (v1.1): the agent's self-check panel — the first thing the operator
/// calls when a device misbehaves. Each check reports pass | warn | fail with
/// evidence; the overall result is the worst check. Checks never mutate state
/// (the writability probe creates and deletes one temp file).
/// </summary>
public static class Doctor
{
    public static Task<DoctorResponse> BuildAsync(AgentState state)
    {
        var checks = new List<DoctorCheckDto>
        {
            CertCheck(state),
            DataDirWritableCheck(state),
            FreeDiskCheck(state),
            FleetLinkCheck(state),
            MeshCheck(state),
            P2pCheck(state),
            ClockSkewCheck(state),
            AuditErrorsCheck(state),
        };
        var overall = checks.Any(c => c.Status == "fail") ? "fail"
            : checks.Any(c => c.Status == "warn") ? "warn" : "pass";
        return Task.FromResult(new DoctorResponse { Overall = overall, Checks = checks });
    }

    private static DoctorCheckDto CertCheck(AgentState state)
    {
        try
        {
            using var cert = CertManager.LoadOrCreate(state.Config);
            var days = (cert.NotAfter - DateTime.UtcNow).TotalDays;
            var detail = $"fingerprint {CertManager.Fingerprint(cert)[..16]}… · valid {cert.NotBefore:yyyy-MM-dd} → {cert.NotAfter:yyyy-MM-dd} ({days:F0} days left)";
            if (days < 0) return new() { Name = "certificate", Status = "fail", Detail = "certificate is EXPIRED · " + detail };
            if (days < 30) return new() { Name = "certificate", Status = "warn", Detail = detail };
            return new() { Name = "certificate", Status = "pass", Detail = detail };
        }
        catch (Exception ex)
        {
            return new() { Name = "certificate", Status = "fail", Detail = $"cannot load certificate: {ex.Message}" };
        }
    }

    private static DoctorCheckDto DataDirWritableCheck(AgentState state)
    {
        var dir = state.Config.ResolvedDataDir;
        try
        {
            var probe = Path.Combine(dir, $".doctor-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return new() { Name = "data-dir writable", Status = "pass", Detail = dir };
        }
        catch (Exception ex)
        {
            return new() { Name = "data-dir writable", Status = "fail", Detail = $"{dir}: {ex.Message}" };
        }
    }

    private static DoctorCheckDto FreeDiskCheck(AgentState state)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(state.Config.ResolvedDataDir))!;
            var drive = new DriveInfo(root);
            var freeGb = drive.AvailableFreeSpace / 1073741824.0;
            var detail = $"{freeGb:F1} GB free on {root} ({drive.TotalSize / 1073741824.0:F0} GB total)";
            if (drive.AvailableFreeSpace < 100L * 1024 * 1024) return new() { Name = "free disk (data volume)", Status = "fail", Detail = detail };
            if (drive.AvailableFreeSpace < 1024L * 1024 * 1024) return new() { Name = "free disk (data volume)", Status = "warn", Detail = detail };
            return new() { Name = "free disk (data volume)", Status = "pass", Detail = detail };
        }
        catch (Exception ex)
        {
            return new() { Name = "free disk (data volume)", Status = "warn", Detail = $"cannot measure: {ex.Message}" };
        }
    }

    private static DoctorCheckDto FleetLinkCheck(AgentState state)
    {
        if (state.Config.Fleet is not { Enabled: true })
            return new() { Name = "fleet link", Status = "pass", Detail = "standalone mode (fleet not configured)" };
        return state.FleetConnected
            ? new() { Name = "fleet link", Status = "pass", Detail = $"connected to {state.Config.Fleet.ServerUrl}" }
            : new() { Name = "fleet link", Status = "warn", Detail = "not currently connected — the agent retries automatically with backoff" };
    }

    private static DoctorCheckDto MeshCheck(AgentState state)
    {
        if (!state.Config.Mesh.Enabled)
            return new() { Name = "mesh", Status = "pass", Detail = "mesh disabled" };
        return state.MeshVirtualIp != null
            ? new() { Name = "mesh", Status = "pass", Detail = $"virtual ip {state.MeshVirtualIp} · {state.MeshStatus}" }
            : new() { Name = "mesh", Status = "warn", Detail = state.MeshStatus };
    }

    private static DoctorCheckDto P2pCheck(AgentState state)
    {
        if (!state.Config.P2p.Enabled)
            return new() { Name = "p2p", Status = "pass", Detail = "p2p disabled" };
        var node = state.P2p;
        if (node == null)
            return new() { Name = "p2p", Status = "warn", Detail = state.P2pStatus };
        return new()
        {
            Name = "p2p",
            Status = "pass",
            Detail = $"{node.Table.Count} peers known · {node.SessionsAccepted} sessions accepted · {state.P2pStatus}",
        };
    }

    private static DoctorCheckDto ClockSkewCheck(AgentState state)
    {
        if (state.Config.Fleet is not { Enabled: true })
            return new() { Name = "clock skew vs fleet server", Status = "pass", Detail = "n/a (standalone)" };
        if (state.ServerSkewSeconds is not { } skew || state.ServerSkewSampledAt is not { } sampledAt)
            return new() { Name = "clock skew vs fleet server", Status = "warn", Detail = "no server time sample yet (the server stamps its state frames; a sample lands on the next fleet (re)connect)" };
        var age = DateTimeOffset.UtcNow - sampledAt;
        var abs = Math.Abs(skew);
        var detail = $"server is {(skew >= 0 ? "+" : "")}{skew:F1}s vs this device (sampled {age.TotalMinutes:F0} min ago)";
        // The fleet signature window is ±120 s (HmacAuth.MaxSkewSeconds): beyond
        // it, relayed commands fail verification on this device.
        if (abs > HmacAuth.MaxSkewSeconds) return new() { Name = "clock skew vs fleet server", Status = "fail", Detail = detail + " — beyond the ±120s signature window; fleet commands will fail verification" };
        if (abs > 30) return new() { Name = "clock skew vs fleet server", Status = "warn", Detail = detail };
        return new() { Name = "clock skew vs fleet server", Status = "pass", Detail = detail };
    }

    private static DoctorCheckDto AuditErrorsCheck(AgentState state)
    {
        try
        {
            var lines = state.Audit.Tail(500);
            var errors = CountAuditErrors(lines);
            var detail = $"{errors} error entr{(errors == 1 ? "y" : "ies")} in the last {lines.Length} audit lines";
            if (errors >= 10) return new() { Name = "recent audit errors", Status = "fail", Detail = detail };
            if (errors >= 1) return new() { Name = "recent audit errors", Status = "warn", Detail = detail };
            return new() { Name = "recent audit errors", Status = "pass", Detail = detail };
        }
        catch (Exception ex)
        {
            return new() { Name = "recent audit errors", Status = "warn", Detail = $"cannot read audit log: {ex.Message}" };
        }
    }

    /// <summary>Counts audit lines whose result marks an error: result "error" /
    /// "refused", or an HTTP status ≥ 400 (results look like "404 in 12ms").
    /// Pure — unit-tested.</summary>
    public static int CountAuditErrors(IEnumerable<string> lines)
    {
        var count = 0;
        foreach (var line in lines)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("result", out var result)) continue;
                var text = result.GetString() ?? "";
                if (text is "error" or "refused") { count++; continue; }
                var firstToken = text.Split(' ')[0];
                if (int.TryParse(firstToken, out var code) && code >= 400) count++;
            }
            catch { /* a torn line is not an error entry */ }
        }
        return count;
    }
}
