using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EsoAgent.Core;

/// <summary>
/// The operator-pack API (items 27–36): telemetry, one-call diagnostics,
/// the vision pack, window management, the launch harness, UI-structure
/// reading, file-change watch, and the app-command registry with
/// design-as-data perception. Same HMAC auth + audit pipeline as every
/// other endpoint; fleet-relayed calls land here too (the relay dispatches
/// through the same loopback pipeline).
/// </summary>
public static class OperatorEndpoints
{
    private static IResult Bad(string error, int status = 400)
        => Results.Json(new ErrorResponse { Error = error }, EsoJson.Options, statusCode: status);

    public static void MapOperatorPack(this IEndpointRouteBuilder app, AgentState state)
    {
        // ----- deep telemetry + diagnostics -----
        app.MapGet("/api/telemetry", async (HttpContext ctx) =>
        {
            var telemetry = await TelemetryService.CaptureAsync(state, ctx.RequestAborted);
            if (ctx.Request.Query["delta"] != "1") return Results.Json(telemetry, EsoJson.Options);
            var json = JsonSerializer.Serialize(telemetry, EsoJson.Options);
            return Results.Json(DeltaEnvelope.Build(state.Deltas, "telemetry", json, deltaRequested: true), EsoJson.Options);
        });

        app.MapGet("/api/diagnose", async (HttpContext ctx) =>
            Results.Json(await DiagnoseService.BuildAsync(state, ctx.RequestAborted), EsoJson.Options));

        // ----- vision pack -----
        app.MapGet("/api/vision/monitors", async (HttpContext ctx) =>
        {
            var (monitors, error) = await VisionService.ListMonitorsAsync(state, ctx.RequestAborted);
            return Results.Json(new VisionResponse { Monitors = monitors, Notes = error != null ? new() { error } : new() }, EsoJson.Options);
        });

        app.MapGet("/api/vision/capture", async (HttpContext ctx) =>
        {
            var full = await VisionService.CaptureAllMonitorsAsync(state, ctx.RequestAborted);
            // Delta pixels (stack tier 5): ?delta=1 withholds unchanged monitors' bytes.
            return Results.Json(ctx.Request.Query["delta"] == "1" ? VisionDelta.Apply(state, full) : full, EsoJson.Options);
        });

        // The perception stack's entry point (item 39): tree-first "see".
        app.MapGet("/api/vision/see", async (HttpContext ctx) =>
        {
            var see = await SeeService.SeeAsync(state, ctx.Request.Query["pixels"] == "1", ctx.RequestAborted);
            return Results.Json(see, EsoJson.Options);
        });

        app.MapGet("/api/vision/window", async (HttpContext ctx) =>
        {
            var target = ctx.Request.Query["target"].ToString();
            if (string.IsNullOrWhiteSpace(target)) return Bad("Missing ?target=<window id or title substring>.");
            var (image, error) = await VisionService.CaptureWindowAsync(state, target, ctx.RequestAborted);
            return image == null ? Bad(error ?? "Capture failed.", 502) : Results.Json(image, EsoJson.Options);
        });

        app.MapGet("/api/vision/region", async (HttpContext ctx) =>
        {
            if (!int.TryParse(ctx.Request.Query["x"], out var x) || !int.TryParse(ctx.Request.Query["y"], out var y) ||
                !int.TryParse(ctx.Request.Query["w"], out var w) || !int.TryParse(ctx.Request.Query["h"], out var h))
                return Bad("Region needs integer x, y, w, h query parameters.");
            var (image, error) = await VisionService.CaptureRegionAsync(state, "region", x, y, w, h, ctx.RequestAborted);
            return image == null ? Bad(error ?? "Capture failed.", 502) : Results.Json(image, EsoJson.Options);
        });

        // ----- window management -----
        app.MapGet("/api/windows", async (HttpContext ctx) =>
        {
            var (windows, error) = await WindowService.ListAsync(state, ctx.RequestAborted);
            return error != null && windows.Count == 0 ? Bad(error, 502) : Results.Json(windows, EsoJson.Options);
        });

        app.MapPost("/api/windows/{id}/focus", async (HttpContext ctx, string id) =>
        {
            var (ok, error) = await WindowService.FocusAsync(state, id, ctx.RequestAborted);
            return ok ? Results.Json(new { focused = id }, EsoJson.Options) : Bad(error ?? "Focus failed.", 502);
        });

        app.MapPost("/api/windows/{id}/close", async (HttpContext ctx, string id) =>
        {
            var (ok, error) = await WindowService.CloseAsync(state, id, ctx.RequestAborted);
            return ok ? Results.Json(new { closed = id }, EsoJson.Options) : Bad(error ?? "Close failed.", 502);
        });

        app.MapGet("/api/windows/wait", async (HttpContext ctx) =>
        {
            var title = ctx.Request.Query["title"].ToString();
            if (string.IsNullOrWhiteSpace(title)) return Bad("Missing ?title=.");
            var timeout = int.TryParse(ctx.Request.Query["timeoutSeconds"], out var t) ? t : 30;
            var (window, error) = await WindowService.WaitForAsync(state, title, timeout, ctx.RequestAborted);
            return window == null ? Bad(error ?? "Not found.", 404) : Results.Json(window, EsoJson.Options);
        });

        // ----- launch harness -----
        app.MapPost("/api/launch/gui", async (HttpRequest req) =>
        {
            var body = await req.ReadFromJsonAsync<LaunchGuiRequest>(EsoJson.Options);
            if (body == null || string.IsNullOrWhiteSpace(body.File)) return Bad("Missing 'file'.");
            var (response, error) = await LaunchService.LaunchGuiAsync(state, body, req.HttpContext.RequestAborted);
            return error != null ? Bad(error, 502) : Results.Json(response, EsoJson.Options);
        });

        // ----- UI-structure reading -----
        app.MapGet("/api/uitree", async (HttpContext ctx) =>
        {
            var target = ctx.Request.Query["target"].ToString();
            if (string.IsNullOrWhiteSpace(target)) return Bad("Missing ?target=<pid or window title substring>.");
            var (response, error) = await UiTreeService.DumpAsync(state, target, ctx.RequestAborted);
            if (error != null) return Bad(error, 502);
            if (ctx.Request.Query["delta"] != "1") return Results.Json(response, EsoJson.Options);
            var json = JsonSerializer.Serialize(response, EsoJson.Options);
            return Results.Json(DeltaEnvelope.Build(state.Deltas, "uitree:" + target, json, deltaRequested: true), EsoJson.Options);
        });

        app.MapPost("/api/uitree/click", async (HttpRequest req) =>
        {
            var body = await req.ReadFromJsonAsync<UiClickRequest>(EsoJson.Options);
            if (body == null || string.IsNullOrWhiteSpace(body.Target) || string.IsNullOrWhiteSpace(body.Element))
                return Bad("Missing 'target' or 'element'.");
            var (ok, error) = await UiTreeService.ClickAsync(state, body.Target, body.Element, req.HttpContext.RequestAborted);
            return ok ? Results.Json(new { clicked = body.Element }, EsoJson.Options) : Bad(error ?? "Click failed.", 502);
        });

        // ----- file-change watch -----
        app.MapGet("/api/watch", (HttpContext ctx) =>
        {
            var since = long.TryParse(ctx.Request.Query["since"], out var s) ? s : 0;
            return Results.Json(new
            {
                watches = state.Watches?.List() ?? new List<WatchInfoDto>(),
                events = state.Watches?.EventsSince(since) ?? new List<WatchEventDto>(),
            }, EsoJson.Options);
        });

        app.MapPost("/api/watch", async (HttpRequest req) =>
        {
            var body = await req.ReadFromJsonAsync<WatchAddRequest>(EsoJson.Options);
            if (body == null || string.IsNullOrWhiteSpace(body.Name) || string.IsNullOrWhiteSpace(body.Path))
                return Bad("A watch needs a name and a path.");
            if (state.Watches == null) return Bad("Watch service unavailable.", 500);
            var (ok, error) = state.Watches.Add(body.Name, body.Path, body.Recursive, persist: false);
            return ok
                ? Results.Json(new { watching = body.Name, path = body.Path, note = "Runtime watch (this agent lifetime). For a durable watch, add it to esoagent.json 'watches'." }, EsoJson.Options)
                : Bad(error ?? "Could not start the watch.");
        });

        // ----- app-command registry + perception -----
        app.MapGet("/api/app", () => Results.Json(new
        {
            registered = state.Config.AppCommands.Select(a => new { a.Name, a.Description, hasPerceive = a.Perceive != null, hasVerify = a.VerifyTemplate != null, a.Notes }).ToList(),
            presets = AppCommandPresets.All.Select(a => a.Name).ToList(),
        }, EsoJson.Options));

        app.MapGet("/api/app/presets", () => Results.Json(AppCommandPresets.All, EsoJson.Options));

        // Polyglot detection (item 40): what kind of project lives at a
        // path, and the registry entries that would make it buildable.
        app.MapGet("/api/app/detect", (HttpContext ctx) =>
        {
            var path = ctx.Request.Query["path"].ToString();
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                return Bad($"Directory not found: {path}");
            var names = Directory.GetFiles(path).Select(f => Path.GetFileName(f)!).ToList();
            return Results.Json(new { path, detections = ProjectDetector.Detect(names) }, EsoJson.Options);
        });

        app.MapPost("/api/app/{name}", async (HttpRequest req, string name) =>
        {
            var body = await req.ReadFromJsonAsync<AppRunRequest>(EsoJson.Options) ?? new AppRunRequest();
            var (response, error) = await AppBridgeService.RunAsync(state, name, body, req.HttpContext.RequestAborted);
            return error != null ? Bad(error) : Results.Json(response, EsoJson.Options);
        });

        app.MapPost("/api/app/{name}/verify", async (HttpRequest req, string name) =>
        {
            var body = await req.ReadFromJsonAsync<AppRunRequest>(EsoJson.Options) ?? new AppRunRequest();
            var (response, error) = await AppBridgeService.VerifyAsync(state, name, body, req.HttpContext.RequestAborted);
            return error != null ? Bad(error) : Results.Json(response, EsoJson.Options);
        });

        app.MapPost("/api/app/{name}/perceive", async (HttpRequest req, string name) =>
        {
            var body = await req.ReadFromJsonAsync<PerceiveRequest>(EsoJson.Options) ?? new PerceiveRequest();
            var (response, error) = await AppBridgeService.PerceiveAsync(state, name, body, req.HttpContext.RequestAborted);
            return error != null ? Bad(error) : Results.Json(response, EsoJson.Options);
        });

        app.MapPost("/api/perceive/diff", async (HttpRequest req) =>
        {
            var body = await req.ReadFromJsonAsync<PerceiveDiffRequest>(EsoJson.Options);
            if (body == null) return Bad("Missing diff request.");
            return Results.Json(SceneDiff.Compare(body.Baseline, body.Current), EsoJson.Options);
        });
    }
}

public sealed class UiClickRequest
{
    public string Target { get; set; } = "";
    public string Element { get; set; } = "";
}

public sealed class PerceiveDiffRequest
{
    public JsonElement Baseline { get; set; }
    public JsonElement Current { get; set; }
}
