using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EsoAgent.Core;

namespace EsoAgent.Windows;

/// <summary>
/// Forwards session-bound capabilities to the tray companion over the
/// "EsoAgentTrayPipe" named pipe (the tray is the pipe server; this service,
/// running in session 0, is the client). Protocol: one JSON request line,
/// one JSON response line.
///   screenshot: {"action":"screenshot"}            → {"ok":true,"pngBase64":"..."} | {"ok":false,"error":"..."}
///   exec:       {"action":"exec","request":{...}}  → the ExecResponse JSON
/// A missing tray is a clean, honest error — never a faked result.
/// </summary>
public sealed class TrayPipeForwarder : IScreenshotProvider, IInteractiveExec
{
    public const string PipeName = "EsoAgentTrayPipe";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // The pipe carries one request at a time; serialize callers.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<(byte[]? Png, string? Error)> CaptureAsync(CancellationToken ct = default)
    {
        try
        {
            var line = await SendAsync(new JsonObject { ["action"] = "screenshot" }, ct);
            if (line == null)
                return (null, "Tray companion is not running (could not connect to the EsoAgentTrayPipe pipe). Start EsoAgent.Tray in the user session.");

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            {
                var error = root.TryGetProperty("error", out var e) ? e.GetString() : "Unknown tray error.";
                return (null, error ?? "Unknown tray error.");
            }
            var base64 = root.GetProperty("pngBase64").GetString();
            if (string.IsNullOrEmpty(base64)) return (null, "Tray returned an empty screenshot.");
            return (Convert.FromBase64String(base64), null);
        }
        catch (Exception ex)
        {
            // Screenshots must never throw out of the endpoint: report, don't fake.
            return (null, $"Screenshot failed: {ex.Message}");
        }
    }

    public async Task<ExecResponse> RunInUserSessionAsync(ExecRequest request, CancellationToken ct = default)
    {
        try
        {
            var payload = new JsonObject
            {
                ["action"] = "exec",
                ["request"] = JsonSerializer.SerializeToNode(request, EsoJson.Options),
            };
            var line = await SendAsync(payload, ct);
            if (line == null)
            {
                return new ExecResponse
                {
                    ExitCode = null,
                    Stdout = "",
                    Stderr = "Tray companion is not running (could not connect to the EsoAgentTrayPipe pipe). Start EsoAgent.Tray in the user session.",
                };
            }
            return JsonSerializer.Deserialize<ExecResponse>(line, EsoJson.Options)
                   ?? new ExecResponse { Stderr = "Tray returned an unreadable response." };
        }
        catch (Exception ex)
        {
            return new ExecResponse { ExitCode = null, Stdout = "", Stderr = $"Interactive exec failed: {ex.Message}" };
        }
    }

    /// <summary>Sends one JSON line and reads one JSON line back. Null = pipe unreachable.</summary>
    private async Task<string?> SendAsync(JsonObject payload, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(Timeout);

            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(timeoutCts.Token);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException)
            {
                if (ct.IsCancellationRequested) throw;
                return null; // tray not running / pipe absent
            }

            using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), bufferSize: 4096, leaveOpen: true) { AutoFlush = true };

            await writer.WriteLineAsync(payload.ToJsonString(EsoJson.Options).AsMemory(), timeoutCts.Token);
            var line = await reader.ReadLineAsync(timeoutCts.Token);
            return line;
        }
        finally
        {
            _gate.Release();
        }
    }
}
