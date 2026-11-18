using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Hosting;

namespace EsoAgent.Core;

// =====================================================================
// Item 39 — the beyond-screenshot perception stack. The operator's
// default way of seeing any screen, strongest evidence first:
//   1. UI structure trees (UIA / AT-SPI) — elements, text, bounds; act by
//      element identity, never by pixel hunting.
//   2. App data APIs — the bridges' perceive verbs (the app's own model).
//   3. Event streams — window open/close/focus events pushed as anomaly
//      events (WindowWatchService below → audit → /api/events → activity).
//   4. Semantic renders — ID/depth/normal passes for spatial questions.
//   5. Delta pixels — captures that ship only what changed.
//   6. Full screenshot / OCR — LAST resort; the weakest evidence class.
// `eso see` implements the stack's entry point: tree-first, pixels only
// on explicit request or when no tree exists.
// =====================================================================

public sealed class WindowSeeDto
{
    public WindowInfoDto Window { get; set; } = new();
    public bool HasTree { get; set; }
    public int ElementCount { get; set; }
    /// <summary>Elements carrying readable text (a Name) — the operator can
    /// READ these directly instead of OCR-ing pixels.</summary>
    public int TextElementCount { get; set; }
    public List<UiNodeDto> Nodes { get; set; } = new();
    public string? Note { get; set; }
}

/// <summary>The human-glance checklist (item 39 acceptance): what ONE look
/// must answer — what's open, what's focused, what text is shown, what
/// changed since the last look, and where the error/dialog is. Computed
/// from the trees, never from pixels. If a checklist question needs a
/// screenshot on a session-ful device, that is a stack bug.</summary>
public sealed class GlanceDto
{
    public List<string> OpenWindows { get; set; } = new();
    public string? FocusedWindow { get; set; }
    /// <summary>Readable text sampled across all windows (capped) — text
    /// READ from controls, the strongest evidence class.</summary>
    public List<string> VisibleTexts { get; set; } = new();
    /// <summary>Title/name of a likely dialog or error surface, if any.</summary>
    public string? LikelyDialogOrError { get; set; }
    public List<string> ChangedSinceLastLook { get; set; } = new();
}

/// <summary>Pure glance computation over (windows, trees) — unit-tested.</summary>
public static class GlanceBuilder
{
    private static readonly string[] ErrorWords =
        { "error", "failed", "failure", "alert", "warning", "exception", "خطأ", "تحذير", "فشل", "تنبيه" };

    public static GlanceDto Build(List<WindowSeeDto> windows, IReadOnlyList<string>? previousTitles)
    {
        var glance = new GlanceDto
        {
            OpenWindows = windows.Select(w => w.Window.Title).ToList(),
            FocusedWindow = windows.FirstOrDefault(w => w.Window.Focused)?.Window.Title,
        };
        foreach (var w in windows)
        {
            foreach (var n in w.Nodes)
            {
                if (!string.IsNullOrWhiteSpace(n.Name) && glance.VisibleTexts.Count < 40)
                    glance.VisibleTexts.Add(n.Name);
                if (glance.LikelyDialogOrError == null &&
                    ((n.Role?.Contains("dialog", StringComparison.OrdinalIgnoreCase) ?? false) ||
                     (n.Role?.Contains("alert", StringComparison.OrdinalIgnoreCase) ?? false) ||
                     ErrorWords.Any(e => n.Name?.Contains(e, StringComparison.OrdinalIgnoreCase) ?? false)))
                    glance.LikelyDialogOrError = string.IsNullOrWhiteSpace(n.Name) ? w.Window.Title : n.Name;
            }
            if (glance.LikelyDialogOrError == null &&
                ErrorWords.Any(e => w.Window.Title.Contains(e, StringComparison.OrdinalIgnoreCase)))
                glance.LikelyDialogOrError = w.Window.Title;
        }
        if (previousTitles == null)
        {
            glance.ChangedSinceLastLook.Add("first look — baseline established; the next look reports changes");
        }
        else
        {
            var now = glance.OpenWindows.ToHashSet();
            var before = previousTitles.ToHashSet();
            foreach (var t in now.Except(before)) glance.ChangedSinceLastLook.Add($"opened: {t}");
            foreach (var t in before.Except(now)) glance.ChangedSinceLastLook.Add($"closed: {t}");
            if (glance.ChangedSinceLastLook.Count == 0) glance.ChangedSinceLastLook.Add("no window changes since the last look");
        }
        return glance;
    }
}

public sealed class SeeResponse
{
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Which stack tier(s) produced this answer.</summary>
    public string Tier { get; set; } = "1-ui-tree";
    /// <summary>Cost meter: the serialized size of this response in bytes
    /// (≈ operator tokens at ~4 bytes/token for text). Consumption is
    /// reported, never guessed.</summary>
    public long CostBytes { get; set; }
    public GlanceDto Glance { get; set; } = new();
    public List<WindowSeeDto> Windows { get; set; } = new();
    /// <summary>Pixel captures — populated ONLY when explicitly requested
    /// (tier 6 of the stack), or empty.</summary>
    public List<VisionImageDto> Images { get; set; } = new();
    public List<MonitorDto> Monitors { get; set; } = new();
    public List<string> Notes { get; set; } = new();
}

public static class SeeService
{
    /// <summary>Tree-first capture of the whole desktop: every top-level
    /// window with its UI tree (capped), summaries computed from the trees.
    /// Pixels only when includePixels is set — the caller's explicit choice.</summary>
    public static async Task<SeeResponse> SeeAsync(AgentState state, bool includePixels, CancellationToken ct = default)
    {
        var response = new SeeResponse();
        var (windows, error) = await WindowService.ListAsync(state, ct);
        if (error != null)
        {
            response.Notes.Add($"Window list unavailable: {error}");
            response.Notes.Add("No user session means no UI trees either — this device currently offers only tier-6 evidence (screenshots) once a session exists.");
        }
        foreach (var window in windows.Take(10))
        {
            var entry = new WindowSeeDto { Window = window };
            var (tree, treeError) = await UiTreeService.DumpAsync(state, window.Id, ct);
            if (treeError != null) entry.Note = treeError;
            else
            {
                entry.Nodes = tree.Nodes;
                entry.ElementCount = tree.Nodes.Count;
                entry.TextElementCount = tree.Nodes.Count(n => !string.IsNullOrWhiteSpace(n.Name));
                entry.HasTree = tree.Nodes.Count > 0;
                if (tree.Notes.Count > 0) entry.Note = string.Join(" ", tree.Notes);
            }
            response.Windows.Add(entry);
        }
        if (windows.Count > 10) response.Notes.Add($"Window list capped at 10 of {windows.Count} — target a specific window with uitree for the rest.");
        response.Notes.Add("Perception stack tier 1 (UI structure). Text above was READ from controls, not OCR-ed. Pixels (tier 6) only on explicit request: eso see --pixels, or when a window has no tree.");

        if (includePixels)
        {
            var pixels = await VisionService.CaptureAllMonitorsAsync(state, ct);
            response.Images = pixels.Images;
            response.Monitors = pixels.Monitors;
            response.Notes.AddRange(pixels.Notes);
            response.Tier = "1-ui-tree+6-pixels";
        }
        // Glance + change baseline (titles only — the cheap diff).
        List<string>? previousTitles = null;
        if (state.Deltas.TryGet("see:windows", out _, out var previousJson) && previousJson != null)
        {
            try { previousTitles = JsonSerializer.Deserialize<List<string>>(previousJson); } catch { }
        }
        response.Glance = GlanceBuilder.Build(response.Windows, previousTitles);
        state.Deltas.Set("see:windows", JsonSerializer.Serialize(response.Glance.OpenWindows));
        // Cost meter: serialize with the meter at zero, then stamp it.
        response.CostBytes = System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(response, EsoJson.Options));
        state.Audit.Write("vision", $"see: {response.Windows.Count} window(s), {response.Windows.Count(w => w.HasTree)} with trees, pixels={includePixels}, cost {response.CostBytes} B", "ok");
        return response;
    }
}

/// <summary>Delta pixels (stack tier 5): a capture whose monitors are
/// byte-identical to the previous capture ship WITHOUT their image data —
/// the response says "unchanged" per monitor instead of re-sending PNGs.</summary>
public static class VisionDelta
{
    public static VisionResponse Apply(AgentState state, VisionResponse full)
    {
        var result = new VisionResponse
        {
            Monitors = full.Monitors,
            Notes = new List<string>(full.Notes),
        };
        var unchanged = 0;
        foreach (var image in full.Images)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(image.PngBase64))).ToLowerInvariant();
            var key = "vision:hash:" + image.Label;
            if (state.Deltas.TryGet(key, out _, out var previous) && previous == hash)
            {
                result.Images.Add(new VisionImageDto
                {
                    Label = image.Label, Width = image.Width, Height = image.Height, PngBase64 = "",
                });
                result.Notes.Add($"Monitor {image.Label}: unchanged since the previous capture (delta — image bytes not re-sent).");
                unchanged++;
            }
            else
            {
                state.Deltas.Set(key, hash);
                result.Images.Add(image);
            }
        }
        if (unchanged > 0) result.Notes.Add($"Delta pixels: {unchanged}/{full.Images.Count} monitor image(s) unchanged and withheld.");
        return result;
    }
}

/// <summary>Window watch config (esoagent.json "windowWatch"). OFF by
/// default: it needs a user session to enumerate windows, and not every
/// device has one. When on, window open/close/focus changes become
/// anomaly events (stack tier 3).</summary>
public sealed class WindowWatchConfig
{
    public bool Enabled { get; set; }
    public int IntervalSeconds { get; set; } = 5;
}

/// <summary>Tier-3 event source for windows: a cheap agent-side diff of the
/// window list (local enumeration, seconds apart — no operator traffic at
/// all) that writes an audit event ONLY when the set or the focus changes.
/// The operator reads them through /api/events / the activity timeline;
/// silence means nothing changed. Enumeration failures (no session) are
/// silent by design.</summary>
public sealed class WindowWatchService : BackgroundService
{
    private readonly AgentState _state;
    private Dictionary<string, (string Title, bool Focused)> _previous = new();
    private bool _baselined;

    public WindowWatchService(AgentState state) => _state = state;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            var cfg = _state.Config.WindowWatch;
            var interval = TimeSpan.FromSeconds(Math.Clamp(cfg.IntervalSeconds, 2, 120));
            if (cfg.Enabled)
            {
                try { await PollOnceAsync(stoppingToken); }
                catch (OperationCanceledException) { return; }
                catch { /* enumeration is best-effort; silence on failure */ }
            }
            try { await Task.Delay(cfg.Enabled ? interval : TimeSpan.FromSeconds(30), stoppingToken); }
            catch { return; }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        var (windows, error) = await WindowService.ListAsync(_state, ct);
        if (error != null) return;
        var current = windows.ToDictionary(w => w.Id, w => (w.Title, w.Focused));
        if (!_baselined)
        {
            _previous = current;
            _baselined = true;
            return; // first sight is a baseline, not a burst of "opened" events
        }
        foreach (var (id, info) in current)
        {
            if (!_previous.ContainsKey(id))
                _state.Audit.Write("watch", $"window opened: '{info.Title}' (id {id})", "event");
            else if (info.Focused && !_previous[id].Focused)
                _state.Audit.Write("watch", $"window focused: '{info.Title}' (id {id})", "event");
        }
        foreach (var (id, info) in _previous)
            if (!current.ContainsKey(id))
                _state.Audit.Write("watch", $"window closed: '{info.Title}' (id {id})", "event");
        _previous = current;
    }
}
