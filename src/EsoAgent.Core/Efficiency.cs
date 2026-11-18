using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace EsoAgent.Core;

// =====================================================================
// Item 37 — the efficiency layer: optimize CONSUMPTION (device resources
// and the operator's tokens) by intelligence, never by lowering quality.
//   * Delta perception — a full dump is sent once; afterwards only the
//     structural diff travels. Baselines live agent-side, per target.
//   * Anomaly-only events — standing watches emit ONLY on crossings /
//     events; /api/events reads them with a cursor (zero idle polling).
//   * Intent-level operations — one declarative call, executed and
//     VERIFIED agent-side, returning a compact proof object.
//   * Quality-floored adaptive streaming — fps/quality adapt to measured
//     bandwidth but never below the legibility floor; if the floor cannot
//     be met the session SAYS so (notice frame + audit), never silently.
// =====================================================================

// ---------- structural JSON diff (pure) ----------

public sealed class JsonChange
{
    public string Path { get; set; } = "";
    /// <summary>added | removed | changed</summary>
    public string Kind { get; set; } = "";
    public string? From { get; set; }
    public string? To { get; set; }
}

public sealed class JsonDiffResult
{
    public List<JsonChange> Changes { get; set; } = new();
    public int UnchangedFields { get; set; }
    public bool Truncated { get; set; }
}

/// <summary>Field-level structural diff of two JSON documents. Objects are
/// compared by key, arrays by index, scalars by raw text. Values in changes
/// are clipped to 200 chars — a diff is a summary, not a second copy.</summary>
public static class JsonStructuralDiff
{
    public static JsonDiffResult Compare(JsonElement baseline, JsonElement current, int maxChanges = 200)
    {
        var result = new JsonDiffResult();
        Walk("$", baseline, current, result, maxChanges);
        return result;
    }

    private static void Add(JsonDiffResult result, string path, string kind, JsonElement? from, JsonElement? to, int max)
    {
        if (result.Changes.Count >= max) { result.Truncated = true; return; }
        result.Changes.Add(new JsonChange
        {
            Path = path, Kind = kind,
            From = from == null ? null : Clip(from.Value),
            To = to == null ? null : Clip(to.Value),
        });
    }

    private static string Clip(JsonElement value)
    {
        var text = value.GetRawText();
        return text.Length <= 200 ? text : text[..200] + "…";
    }

    private static void Walk(string path, JsonElement a, JsonElement b, JsonDiffResult result, int max)
    {
        if (result.Truncated) return;
        if (a.ValueKind != b.ValueKind)
        {
            // A missing side arrives as Undefined.
            if (a.ValueKind == JsonValueKind.Undefined) Add(result, path, "added", null, b, max);
            else if (b.ValueKind == JsonValueKind.Undefined) Add(result, path, "removed", a, null, max);
            else Add(result, path, "changed", a, b, max);
            return;
        }
        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var keys = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var p in a.EnumerateObject()) keys.Add(p.Name);
                foreach (var p in b.EnumerateObject()) keys.Add(p.Name);
                foreach (var key in keys)
                {
                    var hasA = a.TryGetProperty(key, out var va);
                    var hasB = b.TryGetProperty(key, out var vb);
                    if (hasA && hasB) Walk(path + "." + key, va, vb, result, max);
                    else if (hasB) Add(result, path + "." + key, "added", null, vb, max);
                    else Add(result, path + "." + key, "removed", va, null, max);
                }
                break;
            }
            case JsonValueKind.Array:
            {
                var lenA = a.GetArrayLength();
                var lenB = b.GetArrayLength();
                for (var i = 0; i < Math.Max(lenA, lenB); i++)
                {
                    if (i < lenA && i < lenB) Walk($"{path}[{i}]", a[i], b[i], result, max);
                    else if (i < lenB) Add(result, $"{path}[{i}]", "added", null, b[i], max);
                    else Add(result, $"{path}[{i}]", "removed", a[i], null, max);
                }
                break;
            }
            default:
                if (a.GetRawText() == b.GetRawText()) result.UnchangedFields++;
                else Add(result, path, "changed", a, b, max);
                break;
        }
    }
}

// ---------- delta baselines ----------

/// <summary>Agent-side memory of the last full payload per delta key
/// ("telemetry", "inventory", "uitree:&lt;target&gt;", "perceive:&lt;app&gt;").
/// In-memory only: an agent restart starts a fresh baseline, which the
/// envelope states plainly (IsDelta=false) rather than faking a diff.</summary>
public sealed class DeltaStore
{
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, string Json)> _baselines = new();

    public bool TryGet(string key, out DateTimeOffset at, out string json)
    {
        if (_baselines.TryGetValue(key, out var entry)) { at = entry.At; json = entry.Json; return true; }
        at = default; json = ""; return false;
    }

    public void Set(string key, string json) => _baselines[key] = (DateTimeOffset.UtcNow, json);

    public void Clear(string key) => _baselines.TryRemove(key, out _);
}

/// <summary>What a delta-capable endpoint returns: either the full payload
/// (first sight / no baseline) or ONLY the diff, with both sizes stated so
/// the saving is measurable, not asserted.</summary>
public sealed class DeltaEnvelope
{
    public bool IsDelta { get; set; }
    public DateTimeOffset? BaselineAt { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>The full payload — present only when IsDelta is false.</summary>
    public JsonElement? Full { get; set; }
    /// <summary>The structural diff — present only when IsDelta is true.</summary>
    public JsonDiffResult? Diff { get; set; }
    public int FullBytes { get; set; }
    public int SentBytes { get; set; }
    public string Note { get; set; } = "";

    /// <summary>Builds the envelope for one payload and refreshes the baseline.</summary>
    public static DeltaEnvelope Build(DeltaStore store, string key, string currentJson, bool deltaRequested)
    {
        var fullBytes = Encoding.UTF8.GetByteCount(currentJson);
        using var doc = JsonDocument.Parse(currentJson);
        if (deltaRequested && store.TryGet(key, out var at, out var baselineJson))
        {
            using var baseDoc = JsonDocument.Parse(baselineJson);
            var diff = JsonStructuralDiff.Compare(baseDoc.RootElement, doc.RootElement);
            var diffJson = JsonSerializer.Serialize(diff, EsoJson.Options);
            store.Set(key, currentJson);
            return new DeltaEnvelope
            {
                IsDelta = true,
                BaselineAt = at,
                Diff = diff,
                FullBytes = fullBytes,
                SentBytes = Encoding.UTF8.GetByteCount(diffJson),
                Note = diff.Changes.Count == 0 ? "No changes since the baseline." : $"{diff.Changes.Count} change(s) since the baseline.",
            };
        }
        store.Set(key, currentJson);
        return new DeltaEnvelope
        {
            IsDelta = false,
            Full = doc.RootElement.Clone(),
            FullBytes = fullBytes,
            SentBytes = fullBytes,
            Note = deltaRequested ? "First sight — full payload sent and kept as the delta baseline." : "Full payload (delta not requested).",
        };
    }
}

// ---------- anomaly-only event stream ----------

/// <summary>One anomaly/event line from the agent's standing watches —
/// the compact event format behind /api/events. Events exist ONLY because
/// something crossed or happened: telemetry threshold strikes, application
/// crashes, watched-file changes, supervision give-ups, doctor state
/// changes. Silence means nothing happened; there is no heartbeat spam.</summary>
public sealed class AgentEventDto
{
    public DateTimeOffset At { get; set; }
    public string Kind { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Result { get; set; } = "";
}

public sealed class AgentEventsResponse
{
    /// <summary>Audit-line offset to pass as ?since= on the next read.</summary>
    public int Next { get; set; }
    public List<AgentEventDto> Events { get; set; } = new();
}

public static class AgentEvents
{
    /// <summary>Audit endpoints whose lines are EVENTS (crossings/happenings),
    /// not request traffic.</summary>
    public static readonly HashSet<string> EventEndpoints = new(StringComparer.Ordinal)
    {
        "telemetry", "crash", "watch", "supervision", "doctor",
    };

    /// <summary>Extracts events from raw audit JSON lines at/after an offset. Pure.</summary>
    public static AgentEventsResponse FromAuditLines(string[] lines, int total, int since)
    {
        var response = new AgentEventsResponse { Next = total };
        foreach (var line in lines)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var endpoint = root.TryGetProperty("endpoint", out var e) ? e.GetString() ?? "" : "";
                if (!EventEndpoints.Contains(endpoint)) continue;
                response.Events.Add(new AgentEventDto
                {
                    At = root.TryGetProperty("ts", out var ts) && ts.TryGetDateTimeOffset(out var at) ? at : default,
                    Kind = endpoint,
                    Summary = root.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "",
                    Result = root.TryGetProperty("result", out var r) ? r.GetString() ?? "" : "",
                });
            }
            catch { /* a torn audit line is skipped, never fatal */ }
        }
        return response;
    }
}

// ---------- quality-floored adaptive streaming ----------

public sealed class StreamAdaptation
{
    public int Quality { get; set; }
    public int Fps { get; set; }
    public bool Changed { get; set; }
    /// <summary>True when even the floor settings exceed the measured
    /// bandwidth — the session reports this honestly instead of silently
    /// degrading below the floor.</summary>
    public bool BelowFloor { get; set; }
    public string Reason { get; set; } = "";
}

/// <summary>Adaptive streaming policy (pure). Floors are LEGIBILITY floors:
/// quality 40 jpeg / 4 fps — below that, text on a remote screen stops being
/// reliably readable, so the policy refuses to go lower and flags
/// BelowFloor instead. Telemetry-style fidelity is untouched: adaptation
/// changes how OFTEN frames ship and their encode quality, never what a
/// frame claims to be.</summary>
public static class AdaptiveQuality
{
    public const int FloorQuality = 40;
    public const int FloorFps = 4;

    public static StreamAdaptation Evaluate(int currentQuality, int currentFps,
        int requestedQuality, int requestedFps, double bytesPerSecond, double avgFrameBytes)
    {
        var result = new StreamAdaptation { Quality = currentQuality, Fps = currentFps };
        if (avgFrameBytes <= 0 || bytesPerSecond <= 0) { result.Reason = "no measurement yet"; return result; }
        var sustainableFps = bytesPerSecond / avgFrameBytes;

        if (sustainableFps >= currentFps * 1.5)
        {
            // Headroom: climb back toward what the operator asked for.
            if (currentQuality < requestedQuality)
            {
                result.Quality = Math.Min(requestedQuality, currentQuality + 5);
                result.Changed = true; result.Reason = "headroom — quality up";
            }
            else if (currentFps < requestedFps)
            {
                result.Fps = Math.Min(requestedFps, currentFps + 2);
                result.Changed = true; result.Reason = "headroom — fps up";
            }
            else result.Reason = "at requested settings";
            return result;
        }

        if (sustainableFps >= currentFps) { result.Reason = "sustainable"; return result; }

        // Over budget: shed encode quality first (cheapest legibility cost),
        // then frame rate — never below the floors.
        if (currentQuality > FloorQuality)
        {
            result.Quality = Math.Max(FloorQuality, currentQuality - 10);
            result.Changed = true; result.Reason = "over budget — quality down";
            return result;
        }
        if (currentFps > FloorFps)
        {
            result.Fps = Math.Max(FloorFps, Math.Min(currentFps - 2, Math.Max(FloorFps, (int)sustainableFps)));
            result.Changed = true; result.Reason = "over budget — fps down";
            return result;
        }
        result.BelowFloor = sustainableFps < FloorFps;
        result.Reason = result.BelowFloor
            ? $"bandwidth sustains ~{sustainableFps:F1} fps at the floor (quality {FloorQuality}, {FloorFps} fps) — below the legibility floor"
            : "at floor, sustainable";
        return result;
    }
}

// ---------- intent-level operations ----------

public sealed class IntentRequest
{
    /// <summary>ensure-service | ensure-software | ensure-file | ensure-supervised</summary>
    public string Verb { get; set; } = "";
    /// <summary>Service or supervised-entry name (ensure-service / ensure-supervised).</summary>
    public string? Name { get; set; }
    /// <summary>running | stopped (ensure-service).</summary>
    public string? State { get; set; }
    /// <summary>Package id (ensure-software).</summary>
    public string? Id { get; set; }
    /// <summary>Desired presence (ensure-software). Default true.</summary>
    public bool? Present { get; set; }
    /// <summary>File path (ensure-file).</summary>
    public string? Path { get; set; }
    /// <summary>Expected SHA-256 (ensure-file, optional).</summary>
    public string? Sha256 { get; set; }
}

/// <summary>The compact proof an intent returns: what was wanted, whether it
/// holds NOW (verified after any change), what the agent changed to make it
/// hold, and the evidence it checked.</summary>
public sealed class IntentProof
{
    public string Verb { get; set; } = "";
    public string Target { get; set; } = "";
    public bool Satisfied { get; set; }
    public bool Changed { get; set; }
    public Dictionary<string, string> Evidence { get; set; } = new();
    public string Detail { get; set; } = "";
    public DateTimeOffset VerifiedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class IntentService
{
    public static async Task<IntentProof> ExecuteAsync(AgentState state, IntentRequest request, CancellationToken ct = default)
    {
        var proof = new IntentProof { Verb = request.Verb };
        try
        {
            switch (request.Verb)
            {
                case "ensure-service": await EnsureServiceAsync(state, request, proof, ct); break;
                case "ensure-software": await EnsureSoftwareAsync(state, request, proof, ct); break;
                case "ensure-file": EnsureFile(request, proof); break;
                case "ensure-supervised": EnsureSupervised(state, request, proof); break;
                default:
                    proof.Detail = $"Unknown intent verb '{request.Verb}'. Known: ensure-service, ensure-software, ensure-file, ensure-supervised.";
                    break;
            }
        }
        catch (Exception ex) { proof.Detail = $"Intent failed: {ex.Message}"; }
        proof.VerifiedAt = DateTimeOffset.UtcNow;
        state.Audit.Write("intent",
            $"{proof.Verb} {proof.Target}: satisfied={proof.Satisfied} changed={proof.Changed} — {proof.Detail}",
            proof.Satisfied ? "ok" : "error");
        return proof;
    }

    private static async Task EnsureServiceAsync(AgentState state, IntentRequest request, IntentProof proof, CancellationToken ct)
    {
        var name = (request.Name ?? "").Trim();
        var wantRunning = !string.Equals(request.State, "stopped", StringComparison.OrdinalIgnoreCase);
        proof.Target = name;
        if (name.Length == 0) { proof.Detail = "ensure-service needs a name."; return; }

        async Task<ServiceDto?> FindAsync()
        {
            var all = await state.Platform.Services.ListAsync(ct);
            return all.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)
                                        || string.Equals(s.DisplayName, name, StringComparison.OrdinalIgnoreCase));
        }

        var before = await FindAsync();
        if (before == null) { proof.Detail = $"Service '{name}' not found on this device."; return; }
        var isRunning = string.Equals(before.Status, "Running", StringComparison.OrdinalIgnoreCase);
        proof.Evidence["before"] = before.Status;
        if (isRunning != wantRunning)
        {
            var op = wantRunning
                ? await state.Platform.Services.StartAsync(before.Name, ct)
                : await state.Platform.Services.StopAsync(before.Name, ct);
            proof.Changed = op.Success;
            proof.Evidence["action"] = wantRunning ? "start" : "stop";
            if (!op.Success) proof.Evidence["actionError"] = op.Message ?? "failed";
        }
        var after = await FindAsync();
        proof.Evidence["after"] = after?.Status ?? "missing";
        proof.Satisfied = after != null
            && string.Equals(after.Status, "Running", StringComparison.OrdinalIgnoreCase) == wantRunning;
        proof.Detail = proof.Satisfied
            ? $"Service '{name}' is {(wantRunning ? "running" : "stopped")} (verified after {(proof.Changed ? "the change" : "no change was needed")})."
            : $"Service '{name}' is {after?.Status ?? "missing"}, wanted {(wantRunning ? "Running" : "Stopped")}.";
    }

    private static async Task EnsureSoftwareAsync(AgentState state, IntentRequest request, IntentProof proof, CancellationToken ct)
    {
        var id = (request.Id ?? "").Trim();
        var wantPresent = request.Present ?? true;
        proof.Target = id;
        if (id.Length == 0) { proof.Detail = "ensure-software needs an id."; return; }

        async Task<SoftwarePackageDto?> FindAsync()
        {
            var all = await state.Platform.Packages.ListAsync(ct);
            return all.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)
                                        || string.Equals(p.Name, id, StringComparison.OrdinalIgnoreCase));
        }

        var before = await FindAsync();
        proof.Evidence["before"] = before == null ? "absent" : $"present ({before.Version ?? "unknown version"})";
        if ((before != null) != wantPresent)
        {
            var op = wantPresent
                ? await state.Platform.Packages.InstallAsync(new InstallRequest { Id = id }, ct)
                : await state.Platform.Packages.UninstallAsync(new UninstallRequest { Name = before!.Name }, ct);
            proof.Changed = op.Success;
            proof.Evidence["action"] = wantPresent ? "install" : "uninstall";
            if (!op.Success) proof.Evidence["actionError"] = op.Message ?? "failed";
        }
        var after = await FindAsync();
        proof.Evidence["after"] = after == null ? "absent" : $"present ({after.Version ?? "unknown version"})";
        proof.Satisfied = (after != null) == wantPresent;
        proof.Detail = proof.Satisfied
            ? $"Software '{id}' is {(wantPresent ? "present" : "absent")} (verified)."
            : $"Software '{id}' presence is {!wantPresent}, wanted {wantPresent}.";
    }

    private static void EnsureFile(IntentRequest request, IntentProof proof)
    {
        var path = (request.Path ?? "").Trim();
        proof.Target = path;
        if (path.Length == 0) { proof.Detail = "ensure-file needs a path."; return; }
        var exists = File.Exists(path);
        proof.Evidence["exists"] = exists.ToString();
        if (!exists)
        {
            proof.Detail = $"File '{path}' does not exist. (ensure-file verifies presence/hash; it cannot conjure content — transfer the file first, then verify.)";
            return;
        }
        proof.Evidence["size"] = new FileInfo(path).Length.ToString();
        if (!string.IsNullOrWhiteSpace(request.Sha256))
        {
            var hash = FileTransferService.HashFile(path);
            proof.Evidence["sha256"] = hash;
            proof.Satisfied = string.Equals(hash, request.Sha256.Trim(), StringComparison.OrdinalIgnoreCase);
            proof.Detail = proof.Satisfied
                ? $"File '{path}' exists and its SHA-256 matches."
                : $"File '{path}' exists but its SHA-256 does NOT match the expected value.";
            return;
        }
        proof.Satisfied = true;
        proof.Detail = $"File '{path}' exists (no hash requested).";
    }

    private static void EnsureSupervised(AgentState state, IntentRequest request, IntentProof proof)
    {
        var name = (request.Name ?? "").Trim();
        proof.Target = name;
        if (name.Length == 0) { proof.Detail = "ensure-supervised needs a name."; return; }
        var snapshot = state.Supervisor?.Snapshot() ?? new List<SupervisedStatusDto>();
        var entry = snapshot.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (entry == null)
        {
            proof.Detail = $"No supervised entry named '{name}' is configured on this device. (Entries are owner-configured in esoagent.json; the supervisor owns their lifecycle — an intent reports state, it does not invent entries.)";
            return;
        }
        proof.Evidence["state"] = entry.State;
        proof.Evidence["restarts"] = entry.Restarts.ToString();
        if (entry.Pid != null) proof.Evidence["pid"] = entry.Pid.Value.ToString();
        proof.Satisfied = entry.State == "running";
        proof.Detail = proof.Satisfied
            ? $"Supervised '{name}' is running (pid {entry.Pid}, restarts {entry.Restarts})."
            : $"Supervised '{name}' is {entry.State} (restarts {entry.Restarts}) — the supervisor's restart policy owns recovery.";
    }
}
