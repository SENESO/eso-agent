using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EsoAgent.Core;

// =====================================================================
// App-command registry + design-as-data perception (items 32 + 36).
// =====================================================================

/// <summary>One app-bridge registry entry (esoagent.json "appCommands"):
/// a named program the operator can run like a command, with full output
/// capture. {placeholders} in the executable/args are filled from the
/// call's parameter bag — any program with a CLI becomes callable by
/// adding an entry; nothing about an entry is trusted beyond what the
/// owner's config says.</summary>
public sealed class AppCommandConfig
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Executable { get; set; } = "";
    public string ArgsTemplate { get; set; } = "";
    public string? WorkingDirectory { get; set; }
    public int TimeoutMs { get; set; } = 300_000;
    /// <summary>Optional glob (after substitution) whose newest matching file
    /// is scanned for error lines after a run — e.g. an engine's Saved/Logs.</summary>
    public string? LogScanGlob { get; set; }
    /// <summary>The verify verb (item 38): an args template for a command
    /// whose exit code 0 proves a state (build green, tests pass, port open…).
    /// Null = the entry has no verify verb; callers use the run verb.</summary>
    public string? VerifyTemplate { get; set; }
    /// <summary>Honesty label: what this bridge can and cannot do — e.g. a
    /// degraded bridge for a program with no scripting API. Shown in
    /// listings; never dressed up as deep integration.</summary>
    public string? Notes { get; set; }
    public PerceiveConfig? Perceive { get; set; }
}

/// <summary>The verify verb's answer: did the checked state hold?</summary>
public sealed class AppVerifyResponse
{
    public string App { get; set; } = "";
    public bool Passed { get; set; }
    public int? ExitCode { get; set; }
    public long DurationMs { get; set; }
    public string OutputTail { get; set; } = "";
    public List<string> LogErrors { get; set; } = new();
    public string? LogScannedFile { get; set; }
}

public sealed class PerceiveConfig
{
    /// <summary>Device path to the perception script, or a builtin:
    /// "builtin:blender" / "builtin:unreal" (scripts shipped with the agent).</summary>
    public string Script { get; set; } = "";
    /// <summary>Args template for the perceive run; defaults to the entry's
    /// ArgsTemplate. Gets {script}, {outdir} and the call's params.</summary>
    public string? ArgsTemplate { get; set; }
}

public sealed class TelemetryWatchConfig
{
    public bool Enabled { get; set; }
    public int IntervalSeconds { get; set; } = 60;
    public double CpuPercent { get; set; } = 90;
    public double MemoryPercent { get; set; } = 90;
    public double GpuTempC { get; set; } = 90;
}

public sealed class WatchConfig
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Recursive { get; set; } = true;
}

public sealed class AppRunRequest
{
    public Dictionary<string, string> Params { get; set; } = new();
}

public sealed class AppRunResponse
{
    public string App { get; set; } = "";
    public int? ExitCode { get; set; }
    public string Stdout { get; set; } = "";
    public string Stderr { get; set; } = "";
    public long DurationMs { get; set; }
    /// <summary>Error lines from the newest file matching the entry's LogScanGlob.</summary>
    public List<string> LogErrors { get; set; } = new();
    public string? LogScannedFile { get; set; }
}

public sealed class PerceiveRequest
{
    public Dictionary<string, string> Params { get; set; } = new();
    public bool IdPass { get; set; }
    /// <summary>Delta perception (item 37): when true and the agent holds a
    /// baseline dump for this app, the response carries ONLY the diff
    /// (Dump is null, IsDelta is true) — a full dump is sent once, never twice.</summary>
    public bool Delta { get; set; }
}

public sealed class IdPassMeasurement
{
    public string Name { get; set; } = "";
    public int Index { get; set; }
    public long Pixels { get; set; }
    public double CoveragePercent { get; set; }
    public int BboxX { get; set; }
    public int BboxY { get; set; }
    public int BboxW { get; set; }
    public int BboxH { get; set; }
}

public sealed class ScreenMeasurement
{
    public string Name { get; set; } = "";
    /// <summary>Screen-space bbox as fractions of the frame (top-left origin).</summary>
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    public double CoveragePercent { get; set; }
    public bool PartiallyBehind { get; set; }
}

public sealed class PerceiveResponse
{
    /// <summary>Cost meter (item 39): serialized size of the perception
    /// payload in bytes — delta responses report the (much smaller) diff.</summary>
    public long CostBytes { get; set; }
    public string App { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public int? ExitCode { get; set; }
    /// <summary>The scene dump exactly as the app reported it (items + camera).
    /// Null when IsDelta — the diff below is the whole answer.</summary>
    public JsonElement? Dump { get; set; }
    /// <summary>Delta perception: true when this response is a diff against
    /// the agent's stored baseline (see BaselineAt) instead of a full dump.</summary>
    public bool IsDelta { get; set; }
    public DateTimeOffset? BaselineAt { get; set; }
    /// <summary>The diff (SceneDiff result for item dumps, structural JSON
    /// diff otherwise) — present only when IsDelta.</summary>
    public JsonElement? Diff { get; set; }
    public List<IdPassMeasurement> IdPass { get; set; } = new();
    public List<ScreenMeasurement> Screen { get; set; } = new();
    public List<string> Notes { get; set; } = new();
    public string StderrTail { get; set; } = "";
}

/// <summary>Command-line construction for app-bridge entries: {placeholder}
/// substitution + a small argv tokenizer honoring double quotes. Pure.</summary>
public static class AppCommandLine
{
    public static (string File, string[] Args, string? Error) Build(string executable, string argsTemplate, IReadOnlyDictionary<string, string> parameters)
    {
        var missing = new List<string>();
        string Substitute(string text) => Regex.Replace(text, @"\{([a-zA-Z0-9_]+)\}", m =>
        {
            if (parameters.TryGetValue(m.Groups[1].Value, out var value)) return value;
            missing.Add(m.Groups[1].Value);
            return m.Value;
        });
        var file = Substitute(executable ?? "");
        var argsText = Substitute(argsTemplate ?? "");
        if (missing.Count > 0)
            return (file, Array.Empty<string>(), $"Missing parameter(s): {string.Join(", ", missing.Distinct())}");
        return (file, Tokenize(argsText), null);
    }

    public static string[] Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;
        foreach (var c in text)
        {
            if (c == '"') { inQuotes = !inQuotes; hasToken = true; }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasToken || current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); hasToken = false; }
            }
            else current.Append(c);
        }
        if (hasToken || current.Length > 0) tokens.Add(current.ToString());
        return tokens.ToArray();
    }

    /// <summary>Newest file matching a simple glob (one directory + a filename
    /// pattern with *), or null. Used for engine log scans.</summary>
    public static string? NewestMatchingFile(string glob)
    {
        var dir = Path.GetDirectoryName(glob);
        var pattern = Path.GetFileName(glob);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(pattern) || !Directory.Exists(dir)) return null;
        return Directory.GetFiles(dir, pattern)
            .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
            .FirstOrDefault();
    }

    /// <summary>Error lines from a log file: lines containing "Error:" /
    /// "Error " / "ERROR", bounded, oldest first.</summary>
    public static List<string> ExtractLogErrors(string path, int max = 50)
    {
        var errors = new List<string>();
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (line.Contains("Error:") || line.Contains("ERROR") || line.Contains(" error ", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(line.Length > 400 ? line[..400] : line);
                    if (errors.Count >= max) break;
                }
            }
        }
        catch { }
        return errors;
    }
}

/// <summary>Extracts the sentinel-delimited perception JSON from an app's
/// stdout (apps are noisy; the dump must survive log lines around it). Pure.</summary>
public static class PerceiveJson
{
    public const string Begin = "ESO_PERCEIVE_JSON_BEGIN";
    public const string End = "ESO_PERCEIVE_JSON_END";

    public static JsonElement? Extract(string stdout)
    {
        var start = stdout.IndexOf(Begin, StringComparison.Ordinal);
        var end = stdout.IndexOf(End, StringComparison.Ordinal);
        if (start < 0 || end <= start) return null;
        var json = stdout[(start + Begin.Length)..end].Trim();
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch { return null; }
    }
}

/// <summary>
/// Diffs two perception dumps (same app): items are matched by "name";
/// a change lists the fields that differ with old → new values. Pure —
/// this is how the operator proves a design change did exactly what was
/// intended and nothing else.
/// </summary>
public static class SceneDiff
{
    public sealed class FieldChange { public string Field { get; set; } = ""; public string From { get; set; } = ""; public string To { get; set; } = ""; }
    public sealed class ItemChange { public string Name { get; set; } = ""; public List<FieldChange> Changes { get; set; } = new(); }
    public sealed class Result
    {
        public List<string> Added { get; set; } = new();
        public List<string> Removed { get; set; } = new();
        public List<ItemChange> Changed { get; set; } = new();
        public int Unchanged { get; set; }
    }

    public static Result Compare(JsonElement baseline, JsonElement current)
    {
        var result = new Result();
        var baseItems = ItemsByName(baseline);
        var currentItems = ItemsByName(current);
        foreach (var (name, item) in currentItems)
        {
            if (!baseItems.TryGetValue(name, out var before))
            {
                result.Added.Add(name);
                continue;
            }
            var changes = new List<FieldChange>();
            var fields = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var p in before.EnumerateObject()) fields.Add(p.Name);
            foreach (var p in item.EnumerateObject()) fields.Add(p.Name);
            foreach (var field in fields)
            {
                var from = before.TryGetProperty(field, out var fv) ? fv.GetRawText() : "(absent)";
                var to = item.TryGetProperty(field, out var tv) ? tv.GetRawText() : "(absent)";
                if (from != to) changes.Add(new FieldChange { Field = field, From = from, To = to });
            }
            if (changes.Count > 0) result.Changed.Add(new ItemChange { Name = name, Changes = changes });
            else result.Unchanged++;
        }
        foreach (var name in baseItems.Keys)
            if (!currentItems.ContainsKey(name)) result.Removed.Add(name);
        return result;
    }

    private static Dictionary<string, JsonElement> ItemsByName(JsonElement dump)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (dump.ValueKind == JsonValueKind.Object && dump.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var item in items.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                    map[name.GetString()!] = item;
        return map;
    }
}

/// <summary>
/// Exact measurement from a semantic ID render (item 36): the render is an
/// uncompressed BMP where every object has one flat, unique color (the
/// legend maps color → object). Counting pixels and bounding boxes from
/// it is exact — no estimation, no segmentation. Pure — unit-tested.
/// </summary>
public static class BmpIdPass
{
    public sealed class LegendEntry { public string Name { get; set; } = ""; public int Index { get; set; } public int R { get; set; } public int G { get; set; } public int B { get; set; } }

    public static List<LegendEntry> ParseLegend(string legendJson)
    {
        var entries = new List<LegendEntry>();
        using var doc = JsonDocument.Parse(legendJson);
        if (!doc.RootElement.TryGetProperty("items", out var items)) return entries;
        foreach (var item in items.EnumerateArray())
        {
            var color = item.GetProperty("color");
            entries.Add(new LegendEntry
            {
                Name = item.GetProperty("name").GetString() ?? "",
                Index = item.GetProperty("index").GetInt32(),
                R = color[0].GetInt32(), G = color[1].GetInt32(), B = color[2].GetInt32(),
            });
        }
        return entries;
    }

    public static (List<IdPassMeasurement> Measurements, string? Error) Analyze(byte[] bmp, IReadOnlyList<LegendEntry> legend)
    {
        if (bmp.Length < 54 || bmp[0] != (byte)'B' || bmp[1] != (byte)'M')
            return (new(), "Not a BMP file.");
        var dataOffset = BitConverter.ToInt32(bmp, 10);
        var dibSize = BitConverter.ToInt32(bmp, 14);
        if (dibSize < 40) return (new(), "Unsupported BMP (DIB header < 40).");
        var width = BitConverter.ToInt32(bmp, 18);
        var heightRaw = BitConverter.ToInt32(bmp, 22);
        var bpp = BitConverter.ToInt16(bmp, 28);
        var compression = BitConverter.ToInt32(bmp, 30);
        if (compression != 0) return (new(), "Compressed BMP is not supported (the ID pass renders uncompressed).");
        if (bpp is not (24 or 32)) return (new(), $"Unsupported BMP depth {bpp} (need 24/32).");
        var topDown = heightRaw < 0;
        var height = Math.Abs(heightRaw);
        var bytesPerPixel = bpp / 8;
        var stride = (width * bytesPerPixel + 3) & ~3;

        var byColor = new Dictionary<int, LegendEntry>();
        foreach (var e in legend) byColor[(e.R << 16) | (e.G << 8) | e.B] = e;
        var stats = new Dictionary<int, (long Count, int MinX, int MinY, int MaxX, int MaxY)>();
        for (var row = 0; row < height; row++)
        {
            var y = topDown ? row : height - 1 - row;
            var rowStart = dataOffset + row * stride;
            for (var x = 0; x < width; x++)
            {
                var at = rowStart + x * bytesPerPixel;
                if (at + 2 >= bmp.Length) break;
                var b = bmp[at]; var g = bmp[at + 1]; var r = bmp[at + 2]; // BMP stores BGR
                var key = (r << 16) | (g << 8) | b;
                if (!byColor.TryGetValue(key, out var entry)) continue;
                if (!stats.TryGetValue(entry.Index, out var s)) s = (0, int.MaxValue, int.MaxValue, -1, -1);
                s.Count++;
                if (x < s.MinX) s.MinX = x;
                if (y < s.MinY) s.MinY = y;
                if (x > s.MaxX) s.MaxX = x;
                if (y > s.MaxY) s.MaxY = y;
                stats[entry.Index] = s;
            }
        }
        var total = (long)width * height;
        var measurements = new List<IdPassMeasurement>();
        foreach (var entry in legend)
        {
            if (!stats.TryGetValue(entry.Index, out var s)) continue;
            measurements.Add(new IdPassMeasurement
            {
                Name = entry.Name,
                Index = entry.Index,
                Pixels = s.Count,
                CoveragePercent = total > 0 ? Math.Round(100.0 * s.Count / total, 4) : 0,
                BboxX = s.MinX, BboxY = s.MinY,
                BboxW = s.MaxX - s.MinX + 1, BboxH = s.MaxY - s.MinY + 1,
            });
        }
        return (measurements.OrderByDescending(m => m.Pixels).ToList(), null);
    }
}

/// <summary>
/// Screen-space projection for perception dumps whose app did not compute
/// screen boxes itself (Unreal): projects an item's world bounds through
/// the reported camera (Unreal rotator convention: pitch/yaw/roll degrees,
/// Z-up, X-forward; FOV is horizontal). Pure — unit-tested on axis cases.
/// </summary>
public static class ScreenProjection
{
    public static ScreenMeasurement? Project(string name, double[] camLoc, double pitchDeg, double yawDeg, double rollDeg,
        double fovDeg, double aspect, double[] boundsOrigin, double[] boundsExtent)
    {
        var cp = Math.Cos(D2R(pitchDeg)); var sp = Math.Sin(D2R(pitchDeg));
        var cy = Math.Cos(D2R(yawDeg)); var sy = Math.Sin(D2R(yawDeg));
        var forward = new[] { cp * cy, cp * sy, sp };
        // Right at roll 0: cross(worldUp, forward) normalized (verified against
        // the yaw=0 → +Y and yaw=90 → −X cases); up = cross(forward, right).
        var right0 = Normalize(new[] { -forward[1], forward[0], 0.0 });
        if (right0 == null) right0 = new[] { 0.0, 1.0, 0.0 }; // looking straight up/down
        var up0 = Cross(forward, right0);
        var cr = Math.Cos(D2R(rollDeg)); var sr = Math.Sin(D2R(rollDeg));
        var right = Combine(right0, cr, up0, -sr);
        var up = Combine(up0, cr, right0, sr);

        var tanH = Math.Tan(D2R(fovDeg) / 2);
        var tanV = tanH / aspect;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        var anyInFront = false;
        var partiallyBehind = false;
        for (var i = 0; i < 8; i++)
        {
            var corner = new[]
            {
                boundsOrigin[0] + ((i & 1) == 0 ? -boundsExtent[0] : boundsExtent[0]),
                boundsOrigin[1] + ((i & 2) == 0 ? -boundsExtent[1] : boundsExtent[1]),
                boundsOrigin[2] + ((i & 4) == 0 ? -boundsExtent[2] : boundsExtent[2]),
            };
            var d = new[] { corner[0] - camLoc[0], corner[1] - camLoc[1], corner[2] - camLoc[2] };
            var depth = Dot(d, forward);
            if (depth <= 1e-6) { partiallyBehind = true; continue; }
            anyInFront = true;
            var ndcX = Dot(d, right) / depth / tanH;
            var ndcY = Dot(d, up) / depth / tanV;
            var sx = 0.5 + ndcX / 2;
            var sy2 = 0.5 - ndcY / 2;
            minX = Math.Min(minX, sx); maxX = Math.Max(maxX, sx);
            minY = Math.Min(minY, sy2); maxY = Math.Max(maxY, sy2);
        }
        if (!anyInFront) return null;
        var w = maxX - minX;
        var h = maxY - minY;
        return new ScreenMeasurement
        {
            Name = name,
            X = Math.Round(minX, 5), Y = Math.Round(minY, 5),
            W = Math.Round(w, 5), H = Math.Round(h, 5),
            CoveragePercent = Math.Round(Math.Max(0, w) * Math.Max(0, h) * 100, 4),
            PartiallyBehind = partiallyBehind,
        };
    }

    private static double D2R(double deg) => deg * Math.PI / 180.0;
    private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
    private static double[] Cross(double[] a, double[] b) => new[]
    {
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0],
    };
    private static double[]? Normalize(double[] v)
    {
        var len = Math.Sqrt(Dot(v, v));
        return len < 1e-9 ? null : new[] { v[0] / len, v[1] / len, v[2] / len };
    }
    private static double[] Combine(double[] a, double ka, double[] b, double kb) => new[]
    {
        a[0] * ka + b[0] * kb, a[1] * ka + b[1] * kb, a[2] * ka + b[2] * kb,
    };
}

/// <summary>Documented starter entries for the registry (USER-GUIDE carries
/// the same text). Presets resolve BY NAME as a fallback in
/// <see cref="AppBridgeService.Find"/> — a device-config entry with the same
/// name overrides the preset. Executable paths are the owner's to set for
/// their install.</summary>
public static class AppCommandPresets
{
    public static IReadOnlyList<AppCommandConfig> All { get; } = new List<AppCommandConfig>
    {
        new()
        {
            Name = "blender",
            Description = "Blender headless: run a Python script, or perceive the scene as data.",
            Executable = "blender",
            ArgsTemplate = "--background \"{blend}\" --python \"{script}\"",
            TimeoutMs = 600_000,
            Perceive = new PerceiveConfig
            {
                Script = "builtin:blender",
                ArgsTemplate = "--background \"{blend}\" --python \"{script}\" -- --outdir \"{outdir}\" {idpassflag}",
            },
        },
        new()
        {
            Name = "unreal-python",
            Description = "Unreal Engine: run an editor Python script via the PythonScript commandlet; latest Saved/Logs errors are surfaced.",
            Executable = "UnrealEditor-Cmd",
            ArgsTemplate = "\"{project}\" -run=pythonscript -script=\"{script}\" -unattended -nopause",
            TimeoutMs = 900_000,
            LogScanGlob = "{projectdir}/Saved/Logs/*.log",
            Perceive = new PerceiveConfig
            {
                Script = "builtin:unreal",
                ArgsTemplate = "\"{project}\" -run=pythonscript -script=\"{script}\" -unattended -nopause",
            },
        },
        new()
        {
            Name = "gimp",
            Description = "GIMP batch: run a Script-Fu expression headless (where GIMP is installed).",
            Executable = "gimp",
            ArgsTemplate = "--no-interface --batch \"{expr}\" --batch \"(gimp-quit 0)\"",
            TimeoutMs = 600_000,
            Notes = "Script-Fu batch only — GIMP has no scene-graph API, so there is no perceive verb; treat results as command output.",
        },
        new()
        {
            Name = "photoshop",
            Description = "Adobe Photoshop: pass a JSX script file to the app (where the platform build supports script-by-file).",
            Executable = "Photoshop",
            ArgsTemplate = "\"{script}\"",
            TimeoutMs = 600_000,
            Notes = "DEGRADED bridge: Photoshop has no stable headless CLI. Where script-by-file does not run, the honest path is launch + window management + input (items 33/35) — not deep integration.",
        },
        new()
        {
            Name = "unity",
            Description = "Unity Editor: run a static editor method in batch mode (build players, run tests, export packages).",
            Executable = "Unity",
            ArgsTemplate = "-batchmode -nographics -projectPath \"{project}\" -executeMethod {method} -quit -logFile \"{log}\"",
            TimeoutMs = 1_800_000,
            LogScanGlob = "{log}",
            Notes = "The method must be a public static method in an Editor script inside the project. Perceive for Unity = your own editor method that prints JSON between the ESO_PERCEIVE sentinels (see USER-GUIDE) — register it as a custom entry.",
        },
        new()
        {
            Name = "godot",
            Description = "Godot 4: run a GDScript headless, or perceive a scene's node tree as data.",
            Executable = "godot",
            ArgsTemplate = "--headless --path \"{project}\" --script \"{script}\"",
            TimeoutMs = 600_000,
            Perceive = new PerceiveConfig
            {
                Script = "builtin:godot",
                ArgsTemplate = "--headless --path \"{project}\" --script \"{script}\" -- --eso-scene=\"{scene}\"",
            },
        },
        new()
        {
            Name = "dotnet-build",
            Description = "Build a .NET project/solution; verify = the build's exit code.",
            Executable = "dotnet",
            ArgsTemplate = "build \"{project}\" --nologo -v q",
            TimeoutMs = 1_200_000,
            VerifyTemplate = "build \"{project}\" --nologo -v q",
        },
        new()
        {
            Name = "dotnet-test",
            Description = "Run a .NET test project; verify = the test run's exit code.",
            Executable = "dotnet",
            ArgsTemplate = "test \"{project}\" --nologo",
            TimeoutMs = 1_800_000,
            VerifyTemplate = "test \"{project}\" --nologo",
        },
        new()
        {
            Name = "git",
            Description = "Git in a repo on the device: any subcommand via {args} (status, log, diff, worktree…).",
            Executable = "git",
            ArgsTemplate = "-C \"{repo}\" {args}",
            TimeoutMs = 300_000,
            VerifyTemplate = "-C \"{repo}\" status --porcelain",
        },
        new()
        {
            Name = "node",
            Description = "Run a Node.js script on the device.",
            Executable = "node",
            ArgsTemplate = "\"{script}\"",
            TimeoutMs = 600_000,
        },
        new()
        {
            Name = "net-interfaces",
            Description = "Network pack (Linux): interface addresses, brief.",
            Executable = "ip",
            ArgsTemplate = "-brief address",
            Notes = "Linux tool names; on Windows use the diagnose command or register the ipconfig equivalent.",
        },
        new()
        {
            Name = "net-routes",
            Description = "Network pack (Linux): routing table.",
            Executable = "ip",
            ArgsTemplate = "route",
            Notes = "Linux tool names; Windows equivalent: route print.",
        },
        new()
        {
            Name = "net-connections",
            Description = "Network pack (Linux): listening + established sockets with processes.",
            Executable = "ss",
            ArgsTemplate = "-tulpn",
            Notes = "Linux tool names; the telemetry command already returns per-process connections on both OSes.",
        },
        new()
        {
            Name = "net-ping",
            Description = "Network pack: ICMP reachability (Linux flags; Windows ping uses -n).",
            Executable = "ping",
            ArgsTemplate = "-c 4 -W 2 {host}",
            VerifyTemplate = "-c 1 -W 2 {host}",
        },
        new()
        {
            Name = "net-port",
            Description = "Network pack (Linux): is a TCP port reachable from THIS device? verify = exit code (0 = open).",
            Executable = "bash",
            ArgsTemplate = "-c \"timeout 3 bash -c '</dev/tcp/{host}/{port}' && echo open\"",
            VerifyTemplate = "-c \"timeout 3 bash -c '</dev/tcp/{host}/{port}'\"",
            Notes = "Linux/bash only. For mesh health use the mesh status + diagnose commands.",
        },
        new()
        {
            Name = "python", Description = "Python — run a script (polyglot workbench).",
            Executable = "python3", ArgsTemplate = "\"{script}\"",
        },
        new()
        {
            Name = "go-build", Description = "Go — build a module (verify = go vet).",
            Executable = "go", ArgsTemplate = "build ./...", WorkingDirectory = "{project}", VerifyTemplate = "vet ./...",
        },
        new()
        {
            Name = "cargo-build", Description = "Rust — cargo build (verify = cargo check).",
            Executable = "cargo", ArgsTemplate = "build", WorkingDirectory = "{project}", VerifyTemplate = "check",
        },
        new()
        {
            Name = "maven-package", Description = "Java/Maven — package a project.",
            Executable = "mvn", ArgsTemplate = "-q -DskipTests package", WorkingDirectory = "{project}",
        },
        new()
        {
            Name = "cmake-build", Description = "C/C++ — build a configured CMake build dir.",
            Executable = "cmake", ArgsTemplate = "--build \"{build}\"",
        },
        new()
        {
            Name = "unreal-commandlet",
            Description = "Unreal Engine: run any commandlet (e.g. -run=CompileAllBlueprints).",
            Executable = "UnrealEditor-Cmd",
            ArgsTemplate = "\"{project}\" -run={commandlet} {args} -unattended -nopause",
            TimeoutMs = 900_000,
            LogScanGlob = "{projectdir}/Saved/Logs/*.log",
        },
    };
}

/// <summary>Executes registry entries and perception runs. Every run is
/// audit-logged (endpoint "app"); perception artifacts stay on the device
/// under the data dir and are listed in the response for download.</summary>
public static class AppBridgeService
{
    /// <summary>Resolve an app-command by name: the device's own registry
    /// (config <c>appCommands</c>) wins; the shipped presets are the fallback,
    /// so a preset name works on a stock device with no registration step
    /// (§13/§14 — the §M script expected exactly this).</summary>
    public static AppCommandConfig? Find(AgentState state, string name)
        => state.Config.AppCommands.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
           ?? AppCommandPresets.All.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static async Task<(AppRunResponse Response, string? Error)> RunAsync(AgentState state, string name, AppRunRequest request, CancellationToken ct = default)
    {
        var entry = Find(state, name);
        if (entry == null) return (new AppRunResponse { App = name }, $"No app-command named '{name}' is available on this device (not in its registry, not a built-in preset).");
        var (file, args, error) = AppCommandLine.Build(entry.Executable, entry.ArgsTemplate, request.Params);
        if (error != null) return (new AppRunResponse { App = name }, error);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ExecResponse result;
        try
        {
            result = await state.Platform.ProcessRunner.RunAsync(new ExecRequest
            {
                File = file,
                Args = args,
                Cwd = entry.WorkingDirectory,
                TimeoutMs = entry.TimeoutMs,
            }, ct);
        }
        catch (Exception ex)
        {
            state.Audit.Write("app", $"{name}: launch failed: {ex.Message}", "error");
            return (new AppRunResponse { App = name }, ex.Message);
        }
        sw.Stop();
        var response = new AppRunResponse
        {
            App = name,
            ExitCode = result.ExitCode,
            Stdout = result.Stdout,
            Stderr = result.Stderr,
            DurationMs = sw.ElapsedMilliseconds,
        };
        if (!string.IsNullOrWhiteSpace(entry.LogScanGlob))
        {
            var (_, _, globError) = AppCommandLine.Build("", entry.LogScanGlob, request.Params);
            if (globError == null)
            {
                var substituted = entry.LogScanGlob;
                foreach (var (k, v) in request.Params)
                    substituted = substituted.Replace("{" + k + "}", v);
                var newest = AppCommandLine.NewestMatchingFile(substituted);
                if (newest != null)
                {
                    response.LogScannedFile = newest;
                    response.LogErrors = AppCommandLine.ExtractLogErrors(newest);
                }
            }
        }
        state.Audit.Write("app", $"{name} ran (exit {result.ExitCode?.ToString() ?? "?"}, {sw.ElapsedMilliseconds} ms)", result.ExitCode == 0 ? "ok" : "error");
        return (response, null);
    }

    /// <summary>The verify verb: run the entry's VerifyTemplate (or its run
    /// template when no verify template is set) and report pass = exit 0,
    /// with the output tail + scanned-log errors as evidence.</summary>
    public static async Task<(AppVerifyResponse Response, string? Error)> VerifyAsync(AgentState state, string name, AppRunRequest request, CancellationToken ct = default)
    {
        var entry = Find(state, name);
        if (entry == null) return (new AppVerifyResponse { App = name }, $"No app-command named '{name}' is available on this device (not in its registry, not a built-in preset).");
        var template = entry.VerifyTemplate ?? entry.ArgsTemplate;
        var (file, args, error) = AppCommandLine.Build(entry.Executable, template, request.Params);
        if (error != null) return (new AppVerifyResponse { App = name }, error);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ExecResponse result;
        try
        {
            result = await state.Platform.ProcessRunner.RunAsync(new ExecRequest
            {
                File = file, Args = args, Cwd = entry.WorkingDirectory, TimeoutMs = entry.TimeoutMs,
            }, ct);
        }
        catch (Exception ex)
        {
            state.Audit.Write("app", $"{name} verify: launch failed: {ex.Message}", "error");
            return (new AppVerifyResponse { App = name }, ex.Message);
        }
        sw.Stop();
        var combined = (result.Stdout ?? "") + (result.Stderr ?? "");
        var response = new AppVerifyResponse
        {
            App = name,
            Passed = result.ExitCode == 0,
            ExitCode = result.ExitCode,
            DurationMs = sw.ElapsedMilliseconds,
            OutputTail = combined.Length > 2000 ? combined[^2000..] : combined,
        };
        if (!string.IsNullOrWhiteSpace(entry.LogScanGlob))
        {
            var substituted = entry.LogScanGlob;
            foreach (var (k, v) in request.Params) substituted = substituted.Replace("{" + k + "}", v);
            var newest = AppCommandLine.NewestMatchingFile(substituted);
            if (newest != null)
            {
                response.LogScannedFile = newest;
                response.LogErrors = AppCommandLine.ExtractLogErrors(newest);
            }
        }
        state.Audit.Write("app", $"{name} verify: {(response.Passed ? "PASS" : "FAIL")} (exit {result.ExitCode})", response.Passed ? "ok" : "error");
        return (response, null);
    }

    public static async Task<(PerceiveResponse Response, string? Error)> PerceiveAsync(AgentState state, string name, PerceiveRequest request, CancellationToken ct = default)
    {
        var entry = Find(state, name);
        if (entry == null) return (new PerceiveResponse { App = name }, $"No app-command named '{name}' is available on this device (not in its registry, not a built-in preset).");
        if (entry.Perceive == null) return (new PerceiveResponse { App = name }, $"App '{name}' has no perceive configuration.");
        var scriptPath = MaterializeScript(state, entry.Perceive.Script, out var scriptError);
        if (scriptPath == null) return (new PerceiveResponse { App = name }, scriptError);

        var outDir = Path.Combine(state.Config.ResolvedDataDir, "perception",
            $"{entry.Name}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}");
        Directory.CreateDirectory(outDir);
        var parameters = new Dictionary<string, string>(request.Params, StringComparer.OrdinalIgnoreCase)
        {
            ["script"] = scriptPath,
            ["outdir"] = outDir,
            ["idpassflag"] = request.IdPass ? "--id-pass" : "",
        };
        var template = entry.Perceive.ArgsTemplate ?? entry.ArgsTemplate;
        var (file, args, error) = AppCommandLine.Build(entry.Executable, template, parameters);
        if (error != null) return (new PerceiveResponse { App = name }, error);

        var env = new Dictionary<string, string>
        {
            ["ESO_PERCEIVE_OUTDIR"] = outDir,
            ["ESO_PERCEIVE_MODE"] = request.Params.TryGetValue("mode", out var mode) ? mode : "level",
        };
        if (request.Params.TryGetValue("path", out var path)) env["ESO_PERCEIVE_PATH"] = path;

        ExecResponse result;
        try
        {
            result = await state.Platform.ProcessRunner.RunAsync(new ExecRequest
            {
                File = file, Args = args, Cwd = entry.WorkingDirectory,
                TimeoutMs = entry.TimeoutMs, Env = env,
            }, ct);
        }
        catch (Exception ex)
        {
            state.Audit.Write("app", $"{name} perceive failed: {ex.Message}", "error");
            return (new PerceiveResponse { App = name }, ex.Message);
        }

        var response = new PerceiveResponse
        {
            App = name,
            At = DateTimeOffset.UtcNow,
            ExitCode = result.ExitCode,
            StderrTail = result.Stderr.Length > 2000 ? result.Stderr[^2000..] : result.Stderr,
        };
        var dump = PerceiveJson.Extract(result.Stdout);
        if (dump == null)
        {
            response.Notes.Add("The app produced no perception JSON (see stderr tail). The raw output is not a scene dump — nothing was inferred from pixels.");
            state.Audit.Write("app", $"{name} perceive: no JSON in output", "error");
            return (response, null);
        }
        // Delta perception (item 37): keep the newest dump as the baseline;
        // when the caller asked for a delta and a baseline exists, ship ONLY
        // the diff. Scene dumps diff by item name (SceneDiff); anything else
        // diffs structurally.
        var dumpJson = dump.Value.GetRawText();
        var deltaKey = "perceive:" + entry.Name;
        if (request.Delta && state.Deltas.TryGet(deltaKey, out var baselineAt, out var baselineJson))
        {
            using var baseDoc = JsonDocument.Parse(baselineJson);
            JsonElement diffElement;
            if (dump.Value.TryGetProperty("items", out var curItems) && curItems.ValueKind == JsonValueKind.Array
                && baseDoc.RootElement.TryGetProperty("items", out var baseItems) && baseItems.ValueKind == JsonValueKind.Array)
            {
                var sceneDiff = SceneDiff.Compare(baseDoc.RootElement, dump.Value);
                diffElement = JsonSerializer.SerializeToElement(sceneDiff, EsoJson.Options);
            }
            else
            {
                var structural = JsonStructuralDiff.Compare(baseDoc.RootElement, dump.Value);
                diffElement = JsonSerializer.SerializeToElement(structural, EsoJson.Options);
            }
            response.IsDelta = true;
            response.BaselineAt = baselineAt;
            response.Diff = diffElement;
            response.Notes.Add("Delta response: only the changes since the baseline dump are included (Dump is null). Ask without delta for a full dump.");
        }
        else
        {
            response.Dump = dump;
        }
        state.Deltas.Set(deltaKey, dumpJson);

        // Exact ID-pass measurement when the dump carries the artifacts.
        if (dump.Value.TryGetProperty("artifacts", out var artifacts) && artifacts.ValueKind == JsonValueKind.Array)
        {
            string? bmpPath = null, legendPath = null;
            foreach (var artifact in artifacts.EnumerateArray())
            {
                var kind = artifact.TryGetProperty("kind", out var k) ? k.GetString() : null;
                var p = artifact.TryGetProperty("path", out var pv) ? pv.GetString() : null;
                if (kind == "idpass-bmp") bmpPath = p;
                if (kind == "legend") legendPath = p;
            }
            if (bmpPath != null && legendPath != null && File.Exists(bmpPath) && File.Exists(legendPath))
            {
                try
                {
                    var legend = BmpIdPass.ParseLegend(await File.ReadAllTextAsync(legendPath, ct));
                    var (measurements, analyzeError) = BmpIdPass.Analyze(await File.ReadAllBytesAsync(bmpPath, ct), legend);
                    if (analyzeError != null) response.Notes.Add("ID-pass analysis failed: " + analyzeError);
                    else response.IdPass = measurements;
                }
                catch (Exception ex) { response.Notes.Add("ID-pass analysis failed: " + ex.Message); }
            }
        }

        // Screen-space measurement for dumps that report a camera + bounds
        // but no native screen boxes (Unreal path).
        if (dump.Value.TryGetProperty("camera", out var camera) && camera.ValueKind == JsonValueKind.Object &&
            dump.Value.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array &&
            camera.TryGetProperty("location", out _) && camera.TryGetProperty("rotation", out var camRot))
        {
            var camLoc = ReadVec3(camera.GetProperty("location"));
            var fov = camera.TryGetProperty("fov", out var fovEl) ? fovEl.GetDouble() : 90.0;
            var aspect = camera.TryGetProperty("aspect", out var aspectEl) ? aspectEl.GetDouble() : 16.0 / 9.0;
            var pitch = camRot.TryGetProperty("pitch", out var pEl) ? pEl.GetDouble() : 0;
            var yaw = camRot.TryGetProperty("yaw", out var yEl) ? yEl.GetDouble() : 0;
            var roll = camRot.TryGetProperty("roll", out var rEl) ? rEl.GetDouble() : 0;
            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("screenBbox", out _)) continue; // app already measured it natively
                if (!item.TryGetProperty("boundsOrigin", out var bo) || !item.TryGetProperty("boundsExtent", out var be)) continue;
                var name2 = item.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                var m = ScreenProjection.Project(name2, camLoc, pitch, yaw, roll, fov, aspect, ReadVec3(bo), ReadVec3(be));
                if (m != null) response.Screen.Add(m);
            }
        }

        state.Audit.Write("app", $"{name} perceived ({response.IdPass.Count} id-pass measurements, {response.Screen.Count} screen boxes)", "ok");
        return (response, null);
    }

    private static double[] ReadVec3(JsonElement v) => new[] { v[0].GetDouble(), v[1].GetDouble(), v[2].GetDouble() };

    /// <summary>Resolves a perceive script reference: "builtin:blender" /
    /// "builtin:unreal" materialize the shipped script into the data dir;
    /// anything else is a device path used as-is.</summary>
    private static string? MaterializeScript(AgentState state, string script, out string? error)
    {
        error = null;
        if (!script.StartsWith("builtin:", StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(script)) { error = $"Perceive script not found on this device: {script}"; return null; }
            return script;
        }
        var which = script["builtin:".Length..].ToLowerInvariant();
        var resource = which switch
        {
            "blender" => "blender_perceive.py",
            "unreal" => "unreal_perceive.py",
            "godot" => "godot_perceive.gd",
            _ => null,
        };
        if (resource == null) { error = $"Unknown builtin perceive script '{script}'."; return null; }
        var asm = typeof(AppBridgeService).Assembly;
        var full = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(resource, StringComparison.Ordinal));
        if (full == null) { error = $"Builtin script {resource} is not embedded in this build."; return null; }
        var dir = Path.Combine(state.Config.ResolvedDataDir, "perceive-scripts");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, resource);
        using var stream = asm.GetManifestResourceStream(full)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var content = reader.ReadToEnd();
        // Refresh when the shipped script changes; never clobber every call.
        if (!File.Exists(target) || File.ReadAllText(target) != content)
            File.WriteAllText(target, content);
        return target;
    }
}

// ---------- polyglot project detection (item 40) ----------

public sealed class ProjectDetection
{
    public string Kind { get; set; } = "";
    public string Marker { get; set; } = "";
    /// <summary>Registry-ready build/test entries for this project — the
    /// owner pastes them into esoagent.json "appCommands" (adjusting paths)
    /// and the project becomes buildable/testable through the workbench.
    /// A new language is a detection rule + entries here, never new plumbing.</summary>
    public List<AppCommandConfig> Suggested { get; set; } = new();
}

/// <summary>Pure project-type detection from a directory's file names.
/// Language-agnostic by construction: dotnet, Node, Python, Go, Rust,
/// Java (Maven/Gradle), C/C++ (CMake), Make — anything with a marker file
/// and a CLI toolchain can be added as data.</summary>
public static class ProjectDetector
{
    public static List<ProjectDetection> Detect(IEnumerable<string> fileNames)
    {
        var names = fileNames.Select(n => Path.GetFileName(n)).ToList();
        var lower = names.ToDictionary(n => n, n => n.ToLowerInvariant());
        bool Has(string name) => lower.ContainsValue(name);
        string? Suffix(string suffix) => names.FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        var found = new List<ProjectDetection>();

        void Add(string kind, string marker, AppCommandConfig build, AppCommandConfig? test)
        {
            var d = new ProjectDetection { Kind = kind, Marker = marker, Suggested = { build } };
            if (test != null) d.Suggested.Add(test);
            found.Add(d);
        }

        var sln = Suffix(".sln") ?? Suffix(".csproj");
        if (sln != null)
            Add("dotnet", sln,
                new() { Name = "dotnet-build", Executable = "dotnet", ArgsTemplate = "build \"{project}\"", VerifyTemplate = "build \"{project}\"", Description = "Detected .NET project — build." },
                new() { Name = "dotnet-test", Executable = "dotnet", ArgsTemplate = "test \"{project}\"", Description = "Detected .NET project — test." });
        if (Has("package.json"))
            Add("node", "package.json",
                new() { Name = "node-build", Executable = "npm", ArgsTemplate = "run build --prefix \"{project}\"", Description = "Detected Node project — npm run build (the project must define a build script)." },
                new() { Name = "node-test", Executable = "npm", ArgsTemplate = "test --prefix \"{project}\"", Description = "Detected Node project — npm test." });
        var pyMarker = Has("pyproject.toml") ? "pyproject.toml" : Has("requirements.txt") ? "requirements.txt" : Has("setup.py") ? "setup.py" : null;
        if (pyMarker != null)
            Add("python", pyMarker,
                new() { Name = "python-check", Executable = "python3", ArgsTemplate = "-m compileall -q \"{project}\"", VerifyTemplate = "-m compileall -q \"{project}\"", Description = "Detected Python project — byte-compile check (Python has no separate build step)." },
                new() { Name = "python-test", Executable = "python3", ArgsTemplate = "-m pytest \"{project}\" -q", Description = "Detected Python project — pytest." });
        if (Has("go.mod"))
            Add("go", "go.mod",
                new() { Name = "go-build", Executable = "go", ArgsTemplate = "build ./...", WorkingDirectory = "{project}", VerifyTemplate = "vet ./...", Description = "Detected Go module — build (verify = go vet)." },
                new() { Name = "go-test", Executable = "go", ArgsTemplate = "test ./...", WorkingDirectory = "{project}", Description = "Detected Go module — test." });
        if (Has("cargo.toml"))
            Add("rust", "Cargo.toml",
                new() { Name = "cargo-build", Executable = "cargo", ArgsTemplate = "build", WorkingDirectory = "{project}", VerifyTemplate = "check", Description = "Detected Rust crate — cargo build (verify = cargo check)." },
                new() { Name = "cargo-test", Executable = "cargo", ArgsTemplate = "test", WorkingDirectory = "{project}", Description = "Detected Rust crate — cargo test." });
        if (Has("pom.xml"))
            Add("java-maven", "pom.xml",
                new() { Name = "maven-build", Executable = "mvn", ArgsTemplate = "-q -DskipTests package", WorkingDirectory = "{project}", Description = "Detected Maven project — package." },
                new() { Name = "maven-test", Executable = "mvn", ArgsTemplate = "test", WorkingDirectory = "{project}", Description = "Detected Maven project — test." });
        if (Has("build.gradle") || Has("build.gradle.kts"))
            Add("java-gradle", Has("build.gradle") ? "build.gradle" : "build.gradle.kts",
                new() { Name = "gradle-build", Executable = "gradle", ArgsTemplate = "build -x test", WorkingDirectory = "{project}", Description = "Detected Gradle project — build." },
                new() { Name = "gradle-test", Executable = "gradle", ArgsTemplate = "test", WorkingDirectory = "{project}", Description = "Detected Gradle project — test." });
        if (Has("cmakelists.txt"))
            Add("cmake", "CMakeLists.txt",
                new() { Name = "cmake-build", Executable = "cmake", ArgsTemplate = "--build \"{project}/build\"", Description = "Detected CMake project — build an existing build dir (configure first: cmake -S \"{project}\" -B \"{project}/build\")." },
                null);
        if (Has("makefile"))
            Add("make", "Makefile",
                new() { Name = "make-build", Executable = "make", ArgsTemplate = "-C \"{project}\"", Description = "Detected Makefile project — default target." },
                new() { Name = "make-test", Executable = "make", ArgsTemplate = "-C \"{project}\" test", Description = "Detected Makefile project — test target (must exist)." });
        return found;
    }
}
