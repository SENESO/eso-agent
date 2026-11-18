using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using EsoAgent.Core;
using Microsoft.AspNetCore.Http;

namespace EsoAgent.Server;

/// <summary>
/// Server side of relayed streams (v1.2): bridges a client WebSocket (the
/// operator's app/CLI, or the owner's panel browser) to a device stream over
/// the device's existing fleet connection. The bridge is a dumb pipe — the
/// stream-open it carries was signed by the operator or panel key and is
/// verified by the DEVICE; the server cannot open streams itself.
/// Flavors: Text (desktop protocol JSON), Binary (TCP tunnel bytes),
/// Datagram (UDP tunnel — one WS message = one datagram).
/// </summary>
public static class StreamRelay
{
    public enum Flavor { Text, Binary, Datagram }

    public static async Task BridgeAsync(FleetServerState state, WebSocket clientWs, DeviceConnection conn,
        FleetStreamOpenFrame open, Flavor flavor, bool forwardOpened, CancellationToken ct,
        Func<string, bool>? clientFrameFilter = null, Func<string, bool>? deviceFrameFilter = null)
    {
        var streamId = open.StreamId;
        var deviceClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendGate = new SemaphoreSlim(1, 1);

        async Task SendToClientAsync(byte[] bytes, WebSocketMessageType type)
        {
            await sendGate.WaitAsync(ct);
            try
            {
                if (clientWs.State == WebSocketState.Open)
                    await clientWs.SendAsync(bytes, type, true, ct);
            }
            catch { }
            finally { sendGate.Release(); }
        }

        conn.StreamSinks[streamId] = async frame =>
        {
            if (flavor == Flavor.Text)
            {
                // Grant sessions (item 41): the relay enforces view-only /
                // no-clipboard scopes by dropping frames here.
                if (deviceFrameFilter != null && !deviceFrameFilter(frame.Data)) return;
                await SendToClientAsync(Encoding.UTF8.GetBytes(frame.Data), WebSocketMessageType.Text);
            }
            else
            {
                byte[] bytes;
                try { bytes = Convert.FromBase64String(frame.Data); } catch { return; }
                await SendToClientAsync(bytes, WebSocketMessageType.Binary);
            }
        };
        conn.StreamClosers[streamId] = () => deviceClosed.TrySetResult();

        try
        {
            var opened = await conn.OpenStreamAsync(open, TimeSpan.FromSeconds(45), ct);
            if (forwardOpened)
                await SendToClientAsync(JsonSerializer.SerializeToUtf8Bytes(opened, EsoJson.Options), WebSocketMessageType.Text);
            if (!opened.Ok)
            {
                if (!forwardOpened)
                {
                    // Panel browser speaks pure desktop protocol: surface the refusal as a welcome error.
                    await SendToClientAsync(JsonSerializer.SerializeToUtf8Bytes(
                        new DesktopWelcome { Error = opened.Error ?? "The device refused the stream." }, EsoJson.Options),
                        WebSocketMessageType.Text);
                }
                state.Audit.Write("fleet/stream", $"stream {streamId} ({open.Kind}) to {conn.Code} refused: {opened.Error}", "refused");
                return;
            }
            state.Audit.Write("fleet/stream", $"stream {streamId} ({open.Kind}, signer {open.Signer}) bridged to {conn.Code}", "ok");

            var buffer = new byte[64 * 1024];
            using var ms = new MemoryStream();
            while (!ct.IsCancellationRequested && clientWs.State == WebSocketState.Open && !deviceClosed.Task.IsCompleted)
            {
                var receiveTask = clientWs.ReceiveAsync(buffer, ct);
                var done = await Task.WhenAny(receiveTask, deviceClosed.Task);
                if (done != receiveTask) break;
                WebSocketReceiveResult frame;
                try { frame = await receiveTask; }
                catch { break; }
                if (frame.MessageType == WebSocketMessageType.Close) break;
                ms.Write(buffer, 0, frame.Count);
                if (!frame.EndOfMessage) continue;
                var payload = ms.ToArray();
                ms.SetLength(0);
                var data = flavor == Flavor.Text ? Encoding.UTF8.GetString(payload) : Convert.ToBase64String(payload);
                if (clientFrameFilter != null && flavor == Flavor.Text && !clientFrameFilter(data)) continue;
                await conn.SendAsync(new FleetStreamDataFrame { StreamId = streamId, Data = data }, ct);
            }
        }
        catch { /* bridge ends with either side */ }
        finally
        {
            conn.StreamSinks.TryRemove(streamId, out _);
            conn.StreamClosers.TryRemove(streamId, out _);
            try { await conn.SendAsync(new FleetStreamCloseFrame { StreamId = streamId, Reason = "client disconnected" }); } catch { }
            try { await clientWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { }
        }
    }

    /// <summary>Reads one complete text message from a WebSocket (hello/open frames).</summary>
    public static async Task<string?> ReceiveTextAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var frame = await ws.ReceiveAsync(buffer, ct);
            if (frame.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, frame.Count);
            if (frame.EndOfMessage) break;
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
