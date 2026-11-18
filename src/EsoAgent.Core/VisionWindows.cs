using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EsoAgent.Core;

// =====================================================================
// Vision pack, window management, launch harness, UI-structure reading
// (items 27, 28, 33, 35). All of these need the user's graphical
// session, so they run through IInteractiveExec (the session bridge) —
// never from the daemon's own context. Where a platform tool is missing
// (xdotool, pyatspi, a session at all) the answer is a clean, explicit
// "not available", never a fabricated result.
// =====================================================================

public sealed class MonitorDto
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Primary { get; set; }
}

public sealed class VisionImageDto
{
    public string Label { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public string PngBase64 { get; set; } = "";
}

public sealed class VisionResponse
{
    public List<MonitorDto> Monitors { get; set; } = new();
    public List<VisionImageDto> Images { get; set; } = new();
    public List<string> Notes { get; set; } = new();
}

public sealed class WindowInfoDto
{
    public string Id { get; set; } = "";
    public int? Pid { get; set; }
    public string? Process { get; set; }
    public string Title { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Focused { get; set; }
}

/// <summary>Pure parsers for the session tools' output — unit-tested.</summary>
public static class VisionParsers
{
    /// <summary>ESO_WIN|id|pid|x|y|w|h|focused|title lines (both platforms emit this).</summary>
    public static List<WindowInfoDto> ParseWindowDump(string output)
    {
        var windows = new List<WindowInfoDto>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!line.StartsWith("ESO_WIN|", StringComparison.Ordinal)) continue;
            var parts = line.Split('|', 9);
            if (parts.Length < 9) continue;
            windows.Add(new WindowInfoDto
            {
                Id = parts[1],
                Pid = int.TryParse(parts[2], out var pid) ? pid : null,
                X = ParseInt(parts[3]), Y = ParseInt(parts[4]),
                Width = ParseInt(parts[5]), Height = ParseInt(parts[6]),
                Focused = parts[7] == "1",
                Title = parts[8],
            });
        }
        return windows;
    }

    /// <summary>xrandr --listactivemonitors output.</summary>
    public static List<MonitorDto> ParseXrandrMonitors(string output)
    {
        var monitors = new List<MonitorDto>();
        var rx = new Regex(@"^\s*(\d+):\s+(\S+)\s+(\d+)/\d+x(\d+)/\d+\+(\d+)\+(\d+)\s+(\S+)", RegexOptions.Multiline);
        foreach (Match m in rx.Matches(output))
        {
            monitors.Add(new MonitorDto
            {
                Index = ParseInt(m.Groups[1].Value),
                Primary = m.Groups[2].Value.Contains('*'),
                Width = ParseInt(m.Groups[3].Value),
                Height = ParseInt(m.Groups[4].Value),
                X = ParseInt(m.Groups[5].Value),
                Y = ParseInt(m.Groups[6].Value),
                Name = m.Groups[7].Value,
            });
        }
        return monitors;
    }

    /// <summary>ESO_MON|index|name|x|y|w|h|primary lines (Windows monitor enumeration).</summary>
    public static List<MonitorDto> ParseMonitorDump(string output)
    {
        var monitors = new List<MonitorDto>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!line.StartsWith("ESO_MON|", StringComparison.Ordinal)) continue;
            var parts = line.Split('|', 8);
            if (parts.Length < 8) continue;
            monitors.Add(new MonitorDto
            {
                Index = ParseInt(parts[1]),
                Name = parts[2],
                X = ParseInt(parts[3]), Y = ParseInt(parts[4]),
                Width = ParseInt(parts[5]), Height = ParseInt(parts[6]),
                Primary = parts[7] == "1",
            });
        }
        return monitors;
    }

    private static int ParseInt(string text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
}

internal static class SessionScripts
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    /// <summary>Runs a bash -c (Linux) or powershell -Command (Windows) script
    /// inside the user's session and returns the raw response.</summary>
    public static async Task<ExecResponse> RunAsync(AgentState state, string script, CancellationToken ct, int timeoutMs = 30_000)
    {
        var request = IsWindows
            ? new ExecRequest { File = "powershell", Args = new[] { "-NoProfile", "-NonInteractive", "-Command", script }, TimeoutMs = timeoutMs }
            : new ExecRequest { File = "bash", Args = new[] { "-c", script }, TimeoutMs = timeoutMs };
        return await state.Platform.InteractiveExec.RunInUserSessionAsync(request, ct);
    }

    public const string LinuxListWindows = """
        active=$(xdotool getactivewindow 2>/dev/null)
        for id in $(xdotool search --onlyvisible --name ".+" 2>/dev/null); do
          name=$(xdotool getwindowname "$id" 2>/dev/null)
          pid=$(xdotool getwindowpid "$id" 2>/dev/null)
          eval $(xdotool getwindowgeometry --shell "$id" 2>/dev/null)
          focused=0; [ "$id" = "$active" ] && focused=1
          printf 'ESO_WIN|%s|%s|%s|%s|%s|%s|%s|%s\n' "$id" "$pid" "$X" "$Y" "$WIDTH" "$HEIGHT" "$focused" "$name"
        done
        """;

    public const string WindowsListWindows = """
        Add-Type @"
        using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
        public static class EsoWin {
          public delegate bool EnumProc(IntPtr h, IntPtr l);
          [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
          [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
          [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
          [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
          [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
          [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
          [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
          public struct RECT { public int Left, Top, Right, Bottom; }
          public static List<string> List() {
            var fg = GetForegroundWindow(); var lines = new List<string>();
            EnumWindows((h, l) => {
              if (!IsWindowVisible(h)) return true;
              int len = GetWindowTextLength(h); if (len == 0) return true;
              var sb = new StringBuilder(len + 1); GetWindowText(h, sb, sb.Capacity);
              uint pid; GetWindowThreadProcessId(h, out pid);
              RECT r; GetWindowRect(h, out r);
              var title = sb.ToString().Replace("|", "/").Replace("\n", " ");
              lines.Add("ESO_WIN|" + h.ToInt64() + "|" + pid + "|" + r.Left + "|" + r.Top + "|" + (r.Right - r.Left) + "|" + (r.Bottom - r.Top) + "|" + (h == fg ? "1" : "0") + "|" + title);
              return true;
            }, IntPtr.Zero);
            return lines;
          }
        }
        "@
        [EsoWin]::List() | ForEach-Object { $_ }
        """;

    public const string WindowsListMonitors = """
        Add-Type -AssemblyName System.Windows.Forms
        $i = 0
        foreach ($s in [System.Windows.Forms.Screen]::AllScreens) {
          "ESO_MON|$i|$($s.DeviceName)|$($s.Bounds.X)|$($s.Bounds.Y)|$($s.Bounds.Width)|$($s.Bounds.Height)|$(if ($s.Primary) {1} else {0})"
          $i++
        }
        """;
}

public static class WindowService
{
    public static async Task<(List<WindowInfoDto> Windows, string? Error)> ListAsync(AgentState state, CancellationToken ct = default)
    {
        var script = SessionScripts.IsWindows ? SessionScripts.WindowsListWindows : SessionScripts.LinuxListWindows;
        var result = await SessionScripts.RunAsync(state, script, ct);
        if (result.ExitCode != 0 && string.IsNullOrWhiteSpace(result.Stdout))
            return (new(), result.Stderr.Trim().Length > 0 ? result.Stderr.Trim() : "Window listing failed (no session tool available — on Linux this needs xdotool and an interactive session).");
        var windows = VisionParsers.ParseWindowDump(result.Stdout);
        foreach (var w in windows)
        {
            if (w.Pid is { } pid)
            {
                try { w.Process = System.Diagnostics.Process.GetProcessById(pid).ProcessName; } catch { }
            }
        }
        return (windows, null);
    }

    public static async Task<(bool Ok, string? Error)> FocusAsync(AgentState state, string id, CancellationToken ct = default)
    {
        string script;
        if (SessionScripts.IsWindows)
        {
            if (!long.TryParse(id, out var hwnd)) return (false, "Bad window id.");
            script = $$"""
                Add-Type @" using System; using System.Runtime.InteropServices;
                public static class EsoFocus { [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd); }
                "@
                [EsoFocus]::ShowWindow([IntPtr]{{hwnd}}, 9) | Out-Null
                [EsoFocus]::SetForegroundWindow([IntPtr]{{hwnd}}) | Out-Null
                """;
        }
        else
        {
            if (!Regex.IsMatch(id, "^[0-9]+$")) return (false, "Bad window id.");
            script = $"xdotool windowactivate {id} windowraise {id}";
        }
        var result = await SessionScripts.RunAsync(state, script, ct);
        state.Audit.Write("windows", $"focus window {id}", result.ExitCode == 0 ? "ok" : "error");
        return (result.ExitCode == 0, result.ExitCode == 0 ? null : result.Stderr.Trim());
    }

    public static async Task<(bool Ok, string? Error)> CloseAsync(AgentState state, string id, CancellationToken ct = default)
    {
        string script;
        if (SessionScripts.IsWindows)
        {
            if (!long.TryParse(id, out var hwnd)) return (false, "Bad window id.");
            script = $$"""
                Add-Type @" using System; using System.Runtime.InteropServices;
                public static class EsoClose { [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l); }
                "@
                [EsoClose]::PostMessage([IntPtr]{{hwnd}}, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
                """;
        }
        else
        {
            if (!Regex.IsMatch(id, "^[0-9]+$")) return (false, "Bad window id.");
            script = $"xdotool windowclose {id}";
        }
        var result = await SessionScripts.RunAsync(state, script, ct);
        state.Audit.Write("windows", $"close window {id} (graceful)", result.ExitCode == 0 ? "ok" : "error");
        return (result.ExitCode == 0, result.ExitCode == 0 ? null : result.Stderr.Trim());
    }

    /// <summary>Polls the window list until a title contains the substring or the timeout lapses.</summary>
    public static async Task<(WindowInfoDto? Window, string? Error)> WaitForAsync(AgentState state, string titleSubstring, int timeoutSeconds, CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(timeoutSeconds, 1, 300));
        string? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var (windows, error) = await ListAsync(state, ct);
            lastError = error;
            var found = windows.FirstOrDefault(w => w.Title.Contains(titleSubstring, StringComparison.OrdinalIgnoreCase));
            if (found != null) return (found, null);
            try { await Task.Delay(1000, ct); } catch { return (null, "Cancelled."); }
        }
        return (null, lastError ?? $"No window matching '{titleSubstring}' appeared within {timeoutSeconds}s.");
    }

    /// <summary>Synthesizes a left click at a screen point (UI-tree click-through).</summary>
    public static async Task<(bool Ok, string? Error)> ClickAtAsync(AgentState state, int x, int y, CancellationToken ct = default)
    {
        string script;
        if (SessionScripts.IsWindows)
        {
            script = $$"""
                Add-Type @" using System; using System.Runtime.InteropServices;
                public static class EsoClick { [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y); [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra); }
                "@
                [EsoClick]::SetCursorPos({{x}}, {{y}}) | Out-Null
                Start-Sleep -Milliseconds 60
                [EsoClick]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
                [EsoClick]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
                """;
        }
        else script = $"xdotool mousemove {x} {y} click 1";
        var result = await SessionScripts.RunAsync(state, script, ct);
        return (result.ExitCode == 0, result.ExitCode == 0 ? null : result.Stderr.Trim());
    }
}

public static class VisionService
{
    public static async Task<(List<MonitorDto> Monitors, string? Error)> ListMonitorsAsync(AgentState state, CancellationToken ct = default)
    {
        if (SessionScripts.IsWindows)
        {
            var result = await SessionScripts.RunAsync(state, SessionScripts.WindowsListMonitors, ct);
            var monitors = VisionParsers.ParseMonitorDump(result.Stdout);
            return (monitors, monitors.Count > 0 ? null : "No monitors reported by the session.");
        }
        var linux = await SessionScripts.RunAsync(state, "xrandr --listactivemonitors 2>/dev/null", ct);
        var parsed = VisionParsers.ParseXrandrMonitors(linux.Stdout);
        return (parsed, parsed.Count > 0 ? null : "xrandr reported no active monitors (no X session?).");
    }

    /// <summary>Captures one screen region (virtual-screen pixel coordinates) as PNG.</summary>
    public static async Task<(VisionImageDto? Image, string? Error)> CaptureRegionAsync(AgentState state, string label, int x, int y, int width, int height, CancellationToken ct = default)
    {
        if (width <= 0 || height <= 0) return (null, "Region must have positive width/height.");
        string script;
        if (SessionScripts.IsWindows)
        {
            script = $$"""
                Add-Type -AssemblyName System.Drawing
                $bmp = New-Object System.Drawing.Bitmap {{width}}, {{height}}
                $g = [System.Drawing.Graphics]::FromImage($bmp)
                $g.CopyFromScreen({{x}}, {{y}}, 0, 0, (New-Object System.Drawing.Size {{width}}, {{height}}))
                $ms = New-Object System.IO.MemoryStream
                $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
                [Convert]::ToBase64String($ms.ToArray())
                """;
        }
        else
        {
            // grim (Wayland) → scrot (X11) → import (X11); first available wins.
            script = $$"""
                tmp=$(mktemp --suffix=.png)
                if command -v grim >/dev/null 2>&1; then grim -g "{{x}},{{y}} {{width}}x{{height}}" "$tmp" 2>/dev/null;
                elif command -v scrot >/dev/null 2>&1; then scrot -a {{x}},{{y}},{{width}},{{height}} "$tmp" 2>/dev/null;
                elif command -v import >/dev/null 2>&1; then import -window root -crop {{width}}x{{height}}+{{x}}+{{y}} "$tmp" 2>/dev/null;
                fi
                if [ -s "$tmp" ]; then base64 -w0 "$tmp"; fi
                rm -f "$tmp"
                """;
        }
        var result = await SessionScripts.RunAsync(state, script, ct, 30_000);
        var base64 = result.Stdout.Trim();
        if (base64.Length == 0)
            return (null, "Region capture produced no image (no capture tool, or no graphical session). " + result.Stderr.Trim());
        return (new VisionImageDto { Label = label, Width = width, Height = height, PngBase64 = base64 }, null);
    }

    public static async Task<VisionResponse> CaptureAllMonitorsAsync(AgentState state, CancellationToken ct = default)
    {
        var response = new VisionResponse();
        var (monitors, error) = await ListMonitorsAsync(state, ct);
        response.Monitors = monitors;
        if (error != null) response.Notes.Add(error);
        foreach (var monitor in monitors)
        {
            var (image, captureError) = await CaptureRegionAsync(state, monitor.Name, monitor.X, monitor.Y, monitor.Width, monitor.Height, ct);
            if (image != null) response.Images.Add(image);
            else response.Notes.Add($"Monitor {monitor.Name}: {captureError}");
        }
        state.Audit.Write("vision", $"capture all monitors ({response.Images.Count}/{monitors.Count} captured)", response.Images.Count > 0 ? "ok" : "error");
        return response;
    }

    public static async Task<(VisionImageDto? Image, string? Error)> CaptureWindowAsync(AgentState state, string idOrTitle, CancellationToken ct = default)
    {
        var (windows, error) = await WindowService.ListAsync(state, ct);
        if (error != null) return (null, error);
        var window = windows.FirstOrDefault(w => w.Id == idOrTitle)
                     ?? windows.FirstOrDefault(w => w.Title.Contains(idOrTitle, StringComparison.OrdinalIgnoreCase));
        if (window == null) return (null, $"No window matching '{idOrTitle}'.");
        var (image, captureError) = await CaptureRegionAsync(state, window.Title, window.X, window.Y, window.Width, window.Height, ct);
        state.Audit.Write("vision", $"capture window '{window.Title}'", image != null ? "ok" : "error");
        return (image, captureError);
    }
}

// ---------- launch harness (item 35) ----------

public sealed class LaunchGuiRequest
{
    public string File { get; set; } = "";
    public List<string> Args { get; set; } = new();
    /// <summary>Wait until a window whose title contains this appears.</summary>
    public string? WaitTitle { get; set; }
    public int WaitTimeoutSeconds { get; set; } = 30;
    /// <summary>After this many seconds, take a full-screen screenshot and return it.</summary>
    public int? ScreenshotAfterSeconds { get; set; }
}

public sealed class LaunchGuiResponse
{
    public int? Pid { get; set; }
    public bool? WindowFound { get; set; }
    public string? WindowTitle { get; set; }
    public string? ScreenshotPngBase64 { get; set; }
    public List<string> Notes { get; set; } = new();
}

public static class LaunchService
{
    /// <summary>Starts a GUI program in the user's session (visible by default),
    /// optionally waits for its window and screenshots the result. The PID is
    /// returned so the caller can kill exactly that process — PID-scoped,
    /// never a name sweep.</summary>
    public static async Task<(LaunchGuiResponse Response, string? Error)> LaunchGuiAsync(AgentState state, LaunchGuiRequest request, CancellationToken ct = default)
    {
        var response = new LaunchGuiResponse();
        string script;
        if (SessionScripts.IsWindows)
        {
            var argsPs = request.Args.Count == 0
                ? ""
                : "-ArgumentList " + string.Join(", ", request.Args.Select(a => "'" + a.Replace("'", "''") + "'"));
            script = $"$p = Start-Process -FilePath '{request.File.Replace("'", "''")}' {argsPs} -PassThru; $p.Id";
        }
        else
        {
            var quoted = string.Join(" ", request.Args.Select(a => "'" + a.Replace("'", "'\\''") + "'"));
            script = $"nohup '{request.File.Replace("'", "'\\''")}' {quoted} >/dev/null 2>&1 & echo $!";
        }
        var result = await SessionScripts.RunAsync(state, script, ct, 30_000);
        var pidLine = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
        if (!int.TryParse(pidLine, out var pid))
        {
            state.Audit.Write("launch", $"gui launch of {request.File} failed", "error");
            return (response, "Launch failed: " + (result.Stderr.Trim().Length > 0 ? result.Stderr.Trim() : "no PID returned"));
        }
        response.Pid = pid;
        state.Audit.Write("launch", $"gui launch {request.File} -> pid {pid}", "ok");

        if (!string.IsNullOrWhiteSpace(request.WaitTitle))
        {
            var (window, _) = await WindowService.WaitForAsync(state, request.WaitTitle, request.WaitTimeoutSeconds, ct);
            response.WindowFound = window != null;
            response.WindowTitle = window?.Title;
            if (window == null) response.Notes.Add($"Window '{request.WaitTitle}' did not appear within {request.WaitTimeoutSeconds}s (the process may still be loading, or it opened with a different title).");
        }
        if (request.ScreenshotAfterSeconds is { } seconds)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 120)), ct); } catch { }
            var (png, shotError) = await state.Platform.Screenshot.CaptureAsync(ct);
            if (png != null) response.ScreenshotPngBase64 = Convert.ToBase64String(png);
            else response.Notes.Add("Screenshot failed: " + shotError);
        }
        return (response, null);
    }
}

// ---------- UI-structure reading (item 28) ----------

public sealed class UiNodeDto
{
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
    public string? AutomationId { get; set; }
    public int Depth { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

public sealed class UiTreeResponse
{
    public string Target { get; set; } = "";
    public List<UiNodeDto> Nodes { get; set; } = new();
    public List<string> Notes { get; set; } = new();
}

public static class UiTreeService
{
    private const string WindowsDumpScript = """
        param([string]$Target)
        Add-Type -AssemblyName UIAutomationClient
        Add-Type -AssemblyName UIAutomationTypes
        $root = [System.Windows.Automation.AutomationElement]::RootElement
        $window = $null
        if ($Target -match '^\d+$') {
          $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$Target)
          $window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
        } else {
          $all = $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
          foreach ($w in $all) { if ($w.Current.Name -like "*$Target*") { $window = $w; break } }
        }
        if ($null -eq $window) { Write-Error "window not found"; exit 2 }
        $nodes = New-Object System.Collections.Generic.List[object]
        function Walk($el, $depth) {
          if ($nodes.Count -ge 400 -or $depth -gt 12) { return }
          try {
            $c = $el.Current
            $r = $c.BoundingRectangle
            $nodes.Add(@{ name = $c.Name; role = $c.ControlType.ProgrammaticName; automationId = $c.AutomationId; depth = $depth; x = [int]$r.X; y = [int]$r.Y; w = [int]$r.Width; h = [int]$r.Height })
            $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
            $child = $walker.GetFirstChild($el)
            while ($null -ne $child) { Walk $child ($depth + 1); $child = $walker.GetNextSibling($child) }
          } catch { }
        }
        Walk $window 0
        $nodes | ConvertTo-Json -Compress
        """;

    private const string LinuxDumpScript = """
        import json, sys
        try:
            import pyatspi
        except Exception:
            print("ESO_UITREE_UNAVAILABLE", file=sys.stderr)
            sys.exit(3)
        target = sys.argv[1] if len(sys.argv) > 1 else ""
        desktop = pyatspi.Registry.getDesktop(0)
        nodes = []
        def walk(node, depth):
            if len(nodes) >= 400 or depth > 12:
                return
            try:
                comp = node.queryComponent()
                ext = comp.getExtents(pyatspi.DESKTOP_COORDS)
                nodes.append({
                    "name": node.name or "", "role": node.getRoleName(),
                    "automationId": None, "depth": depth,
                    "x": ext.x, "y": ext.y, "w": ext.width, "h": ext.height,
                })
            except Exception:
                pass
            try:
                for i in range(node.childCount):
                    walk(node.getChildAtIndex(i), depth + 1)
            except Exception:
                pass
        apps = [desktop.getChildAtIndex(i) for i in range(desktop.childCount)]
        chosen = None
        for app in apps:
            if not target or target.lower() in (app.name or "").lower() or (target.isdigit() and app.get_process_id() == int(target)):
                chosen = app
                break
        if chosen is None:
            print("ESO_UITREE_NOTFOUND", file=sys.stderr)
            sys.exit(2)
        walk(chosen, 0)
        print(json.dumps(nodes))
        """;

    public static async Task<(UiTreeResponse Response, string? Error)> DumpAsync(AgentState state, string target, CancellationToken ct = default)
    {
        var response = new UiTreeResponse { Target = target };
        ExecResponse result;
        if (SessionScripts.IsWindows)
        {
            // PowerShell param() binding: pass the target via an env var instead.
            var script = "$Target = $env:ESO_UITREE_TARGET\n" + WindowsDumpScript.Replace("param([string]$Target)", "");
            result = await RunWithEnvAsync(state, script, new Dictionary<string, string> { ["ESO_UITREE_TARGET"] = target }, ct);
        }
        else
        {
            var scriptPath = Path.Combine(Path.GetTempPath(), "eso-uitree.py");
            await File.WriteAllTextAsync(scriptPath, LinuxDumpScript, ct);
            var escaped = target.Replace("'", "'\\''");
            result = await SessionScripts.RunAsync(state, $"python3 '{scriptPath}' '{escaped}'", ct, 45_000);
        }
        if (result.Stderr.Contains("ESO_UITREE_UNAVAILABLE"))
            return (response, "UI-structure reading is not available on this device: AT-SPI (pyatspi) is not installed. Pixels (vision pack) still work.");
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Stdout))
            return (response, "UI tree dump failed: " + result.Stderr.Trim());
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(result.Stdout.Trim());
            var root = doc.RootElement;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                response.Nodes.Add(ReadNode(root));
            }
            else if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var el in root.EnumerateArray()) response.Nodes.Add(ReadNode(el));
            }
        }
        catch (Exception ex) { return (response, "UI tree output was not valid JSON: " + ex.Message); }
        state.Audit.Write("uitree", $"ui tree of '{target}' ({response.Nodes.Count} nodes)", "ok");
        return (response, null);
    }

    /// <summary>Click-by-element: dump the tree, resolve the named element's
    /// bounds, click its center through the session input path.</summary>
    public static async Task<(bool Ok, string? Error)> ClickAsync(AgentState state, string target, string elementName, CancellationToken ct = default)
    {
        var (tree, error) = await DumpAsync(state, target, ct);
        if (error != null) return (false, error);
        var node = tree.Nodes.FirstOrDefault(n => n.Name.Equals(elementName, StringComparison.OrdinalIgnoreCase))
                   ?? tree.Nodes.FirstOrDefault(n => n.Name.Contains(elementName, StringComparison.OrdinalIgnoreCase));
        if (node == null) return (false, $"No element named '{elementName}' in the UI tree of '{target}'.");
        if (node.Width <= 0 || node.Height <= 0) return (false, $"Element '{elementName}' has no on-screen bounds.");
        var (ok, clickError) = await WindowService.ClickAtAsync(state, node.X + node.Width / 2, node.Y + node.Height / 2, ct);
        state.Audit.Write("uitree", $"click element '{elementName}' in '{target}' at {node.X + node.Width / 2},{node.Y + node.Height / 2}", ok ? "ok" : "error");
        return (ok, clickError);
    }

    private static UiNodeDto ReadNode(System.Text.Json.JsonElement el) => new()
    {
        Name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
        Role = el.TryGetProperty("role", out var r) ? r.GetString() ?? "" : "",
        AutomationId = el.TryGetProperty("automationId", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.String ? a.GetString() : null,
        Depth = el.TryGetProperty("depth", out var d) ? d.GetInt32() : 0,
        X = el.TryGetProperty("x", out var x) ? x.GetInt32() : 0,
        Y = el.TryGetProperty("y", out var y) ? y.GetInt32() : 0,
        Width = el.TryGetProperty("w", out var w) ? w.GetInt32() : 0,
        Height = el.TryGetProperty("h", out var h) ? h.GetInt32() : 0,
    };

    private static async Task<ExecResponse> RunWithEnvAsync(AgentState state, string script, Dictionary<string, string> env, CancellationToken ct)
    {
        var request = new ExecRequest
        {
            File = "powershell",
            Args = new[] { "-NoProfile", "-NonInteractive", "-Command", script },
            Env = env,
            TimeoutMs = 45_000,
        };
        return await state.Platform.InteractiveExec.RunInUserSessionAsync(request, ct);
    }
}
