using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using EsoAgent.Client;
using EsoAgent.Core;

namespace EsoAgent.App.Services;

/// <summary>
/// Streaming exec over the agent's /api/exec/stream WebSocket — the GUI twin
/// of <see cref="ApiClient.ExecStreamAsync"/> (which writes to a console).
/// Same HMAC-signed upgrade and {type:data|exit} frame protocol; output is
/// delivered through callbacks so the terminal view can render it live.
/// Direct connections only (the fleet relay has no streaming channel).
/// </summary>
public static class ExecStreamClient
{
    /// <summary>Runs the request, streaming stdout/stderr chunks to <paramref name="onData"/>; returns the exit code.</summary>
    public static async Task<int> RunAsync(
        ClientConfig cfg, ExecRequest request, Action<string, string> onData, CancellationToken ct = default)
    {
        var fingerprint = cfg.Fingerprint.Trim();
        using var ws = new ClientWebSocket();
        ws.Options.RemoteCertificateValidationCallback = (_, cert, _, _) =>
            cert != null && string.Equals(
                Convert.ToHexString(SHA256.HashData(new X509Certificate2(cert).GetRawCertData())).ToLowerInvariant(),
                fingerprint, StringComparison.OrdinalIgnoreCase);

        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sig = HmacAuth.Sign(cfg.TokenBytes, "GET", "/api/exec/stream", ts, Array.Empty<byte>());
        ws.Options.SetRequestHeader(HmacAuth.TimestampHeader, ts.ToString());
        ws.Options.SetRequestHeader(HmacAuth.SignatureHeader, sig);

        await ws.ConnectAsync(new Uri($"wss://{cfg.Host}:{cfg.Port}/api/exec/stream"), ct);
        await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(request, EsoJson.Options),
            WebSocketMessageType.Text, true, ct);

        var buffer = new byte[64 * 1024];
        var exitCode = 1;
        while (ws.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult frame;
            do
            {
                frame = await ws.ReceiveAsync(buffer, ct);
                if (frame.MessageType == WebSocketMessageType.Close) break;
                ms.Write(buffer, 0, frame.Count);
            } while (!frame.EndOfMessage);
            if (frame.MessageType == WebSocketMessageType.Close) break;

            using var doc = JsonDocument.Parse(ms.ToArray());
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();
            if (type == "data")
            {
                var stream = root.GetProperty("stream").GetString() ?? "stdout";
                onData(stream, root.GetProperty("data").GetString() ?? "");
            }
            else if (type == "exit")
            {
                exitCode = root.TryGetProperty("exitCode", out var ec) && ec.ValueKind == JsonValueKind.Number
                    ? ec.GetInt32() : 1;
                break;
            }
        }
        return exitCode;
    }
}
