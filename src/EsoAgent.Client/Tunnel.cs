using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using EsoAgent.Core;

namespace EsoAgent.Client;

/// <summary>
/// TCP/UDP port forwarding (v1.2), SSH-style:
///   eso tunnel &lt;localPort&gt; &lt;device&gt; &lt;remoteHost:remotePort&gt; [--udp]
///   eso tunnel --socks &lt;localPort&gt; &lt;device&gt;
/// The device is a fleet CODE (relayed stream — the device verified the
/// operator's stream signature) or an IP (direct /api/tunnel WebSocket).
/// Every connection is audit-logged by the agent. Ctrl+C shuts everything
/// down cleanly: listeners close, sockets close, streams close.
/// </summary>
public static class TunnelCommand
{
    public static async Task<int> RunAsync(ClientConfig cfg, List<string> rest)
    {
        var udp = rest.Remove("--udp");
        var socks = rest.Remove("--socks");
        if (rest.Count < 2 || !int.TryParse(rest[0], out var localPort))
        {
            Console.Error.WriteLine("usage: eso tunnel <localPort> <device> <remoteHost:remotePort> [--udp]");
            Console.Error.WriteLine("       eso tunnel --socks <localPort> <device>");
            return 2;
        }
        var device = rest[1];
        TunnelTarget? fixedTarget = null;
        if (!socks)
        {
            if (rest.Count < 3 || !TunnelTarget.TryParse(rest[2], out var parsed))
            {
                Console.Error.WriteLine("error: remote target must be host:port.");
                return 2;
            }
            fixedTarget = parsed;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        if (udp && !socks)
        {
            await RunUdpAsync(cfg, localPort, device, fixedTarget!, cts.Token);
            return 0;
        }

        var listener = new TcpListener(IPAddress.Loopback, localPort);
        listener.Start();
        Console.WriteLine(socks
            ? $"SOCKS5 proxy on 127.0.0.1:{localPort} via {device}. Ctrl+C to stop."
            : $"Forwarding 127.0.0.1:{localPort} -> {fixedTarget!.Host}:{fixedTarget.Port} via {device}. Ctrl+C to stop.");
        var connections = new List<Task>();
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cts.Token);
                connections.Add(HandleTcpAsync(cfg, client, device, fixedTarget, socks, cts.Token));
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            listener.Stop();
            try { await Task.WhenAll(connections); } catch { }
        }
        Console.WriteLine("Tunnel closed.");
        return 0;
    }

    private static async Task HandleTcpAsync(ClientConfig cfg, TcpClient client, string device, TunnelTarget? fixedTarget, bool socks, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                var target = fixedTarget;
                if (socks)
                {
                    target = await Socks5HandshakeAsync(client.GetStream(), ct);
                    if (target == null) return;
                }
                using var channel = await OpenStreamAsync(cfg, device, udp: false, ct);
                await SendTargetAsync(channel, target!, ct);
                await PumpTcpAsync(client.GetStream(), channel.Ws, ct);
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested) Console.Error.WriteLine($"tunnel connection failed: {ex.Message}");
            }
        }
    }

    private static async Task RunUdpAsync(ClientConfig cfg, int localPort, string device, TunnelTarget target, CancellationToken ct)
    {
        using var udpListener = new UdpClient(new IPEndPoint(IPAddress.Loopback, localPort));
        var sessions = new Dictionary<IPEndPoint, (WebSocket Ws, Task Pump)>();
        Console.WriteLine($"Forwarding UDP 127.0.0.1:{localPort} -> {target.Host}:{target.Port} via {device}. Ctrl+C to stop.");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var datagram = await udpListener.ReceiveAsync(ct);
                if (!sessions.TryGetValue(datagram.RemoteEndPoint, out var session))
                {
                    var channel = await OpenStreamAsync(cfg, device, udp: true, ct);
                    await SendTargetAsync(channel, target, ct, fleetKind: "tunnel-udp");
                    var pump = PumpUdpRepliesAsync(channel.Ws, udpListener, datagram.RemoteEndPoint, ct);
                    session = (channel.Ws, pump);
                    sessions[datagram.RemoteEndPoint] = session;
                }
                await session.Ws.SendAsync(datagram.Buffer, WebSocketMessageType.Binary, true, ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            foreach (var (ws, _) in sessions.Values) { try { ws.Dispose(); } catch { } }
        }
        Console.WriteLine("UDP tunnel closed.");
    }

    private static async Task PumpUdpRepliesAsync(WebSocket ws, UdpClient listener, IPEndPoint remote, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult frame;
                do
                {
                    frame = await ws.ReceiveAsync(buffer, ct);
                    if (frame.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, frame.Count);
                } while (!frame.EndOfMessage);
                await listener.SendAsync(ms.ToArray(), remote, ct);
            }
        }
        catch { }
    }

    /// <summary>Minimal SOCKS5 (no-auth, CONNECT only — the dynamic-forwarding mode).</summary>
    private static async Task<TunnelTarget?> Socks5HandshakeAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[2];
        if (!await ReadExactAsync(stream, header, ct) || header[0] != 0x05) return null;
        var methods = new byte[header[1]];
        if (!await ReadExactAsync(stream, methods, ct)) return null;
        await stream.WriteAsync(new byte[] { 0x05, 0x00 }, ct); // no auth
        var request = new byte[4];
        if (!await ReadExactAsync(stream, request, ct) || request[1] != 0x01) // CONNECT only
        {
            await stream.WriteAsync(new byte[] { 0x05, 0x07, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct);
            return null;
        }
        string host;
        switch (request[3])
        {
            case 0x01: // IPv4
                var ip = new byte[4];
                if (!await ReadExactAsync(stream, ip, ct)) return null;
                host = new IPAddress(ip).ToString();
                break;
            case 0x03: // domain
                var lenBuf = new byte[1];
                if (!await ReadExactAsync(stream, lenBuf, ct)) return null;
                var domain = new byte[lenBuf[0]];
                if (!await ReadExactAsync(stream, domain, ct)) return null;
                host = Encoding.ASCII.GetString(domain);
                break;
            case 0x04: // IPv6
                var ip6 = new byte[16];
                if (!await ReadExactAsync(stream, ip6, ct)) return null;
                host = new IPAddress(ip6).ToString();
                break;
            default:
                await stream.WriteAsync(new byte[] { 0x05, 0x08, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct);
                return null;
        }
        var portBuf = new byte[2];
        if (!await ReadExactAsync(stream, portBuf, ct)) return null;
        var port = (portBuf[0] << 8) | portBuf[1];
        await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct);
        return new TunnelTarget { Host = host, Port = port };
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    /// <summary>One tunnel connection's channel: the socket plus, for the
    /// fleet path, the fleet client and device code the open frame needs.</summary>
    private sealed class TunnelChannel : IDisposable
    {
        public required ClientWebSocket Ws { get; init; }
        public FleetApiClient? Fleet { get; init; }
        public string? Code { get; init; }
        public void Dispose() { Ws.Dispose(); Fleet?.Dispose(); }
    }

    /// <summary>Opens the data WebSocket for one tunnel connection: the fleet
    /// stream channel for a device CODE, the agent's /api/tunnel for an IP.</summary>
    private static async Task<TunnelChannel> OpenStreamAsync(ClientConfig cfg, string device, bool udp, CancellationToken ct)
    {
        if (DeviceIdentity.LooksLikeCode(device))
        {
            var fleet = Program.CreateFleetClient(cfg);
            var ws = await fleet.ConnectStreamSocketAsync(udp ? "/fleet/tunnel-udp" : "/fleet/tunnel", ct);
            // The open frame is sent by SendTargetAsync (it carries the target).
            return new TunnelChannel { Ws = ws, Fleet = fleet, Code = device.ToUpperInvariant() };
        }
        var directCfg = cfg;
        var parts = device.Split(':');
        directCfg = cfg.WithEndpoint(parts[0], parts.Length == 2 && int.TryParse(parts[1], out var p) ? p : cfg.Port);
        var wsUri = new Uri($"wss://{directCfg.Host}:{directCfg.Port}/api/tunnel{(udp ? "?udp=1" : "")}");
        var directWs = new ClientWebSocket();
        var fp = directCfg.Fingerprint.Trim();
        directWs.Options.RemoteCertificateValidationCallback = (_, cert, _, _) =>
            cert != null && string.Equals(
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new System.Security.Cryptography.X509Certificates.X509Certificate2(cert).GetRawCertData())).ToLowerInvariant(),
                fp, StringComparison.OrdinalIgnoreCase);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var path = "/api/tunnel" + (udp ? "?udp=1" : "");
        var sig = HmacAuth.Sign(directCfg.TokenBytes, "GET", path, ts, Array.Empty<byte>());
        directWs.Options.SetRequestHeader(HmacAuth.TimestampHeader, ts.ToString());
        directWs.Options.SetRequestHeader(HmacAuth.SignatureHeader, sig);
        await directWs.ConnectAsync(wsUri, ct);
        return new TunnelChannel { Ws = directWs };
    }

    /// <summary>Sends the connection's target: for the fleet channel that's the
    /// signed stream-open (payload = the target); direct takes the bare target JSON.</summary>
    private static async Task SendTargetAsync(TunnelChannel channel, TunnelTarget target, CancellationToken ct, string fleetKind = "tunnel")
    {
        var ws = channel.Ws;
        var payloadJson = JsonSerializer.Serialize(target, EsoJson.Options);
        if (channel.Fleet is { } fleet)
        {
            var streamId = Guid.NewGuid().ToString("N");
            var issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var open = new FleetStreamOpenFrame
            {
                StreamId = streamId,
                Kind = fleetKind,
                IssuedAt = issuedAt,
                Signer = "operator",
                PayloadJson = payloadJson,
                Signature = OperatorSigning.SignStream(fleet.OperatorKey, streamId, issuedAt, fleetKind, payloadJson),
            };
            var request = new FleetStreamOpenRequest { Code = channel.Code ?? "", Open = open };
            await ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, EsoJson.Options)), WebSocketMessageType.Text, true, ct);
            var openedJson = await ReceiveTextMessageAsync(ws, ct)
                ?? throw new InvalidOperationException("Fleet server closed the tunnel stream without an answer.");
            var opened = JsonSerializer.Deserialize<FleetStreamOpenedFrame>(openedJson, EsoJson.Options);
            if (opened is not { Ok: true })
                throw new InvalidOperationException(opened?.Error ?? "The device refused the tunnel stream.");
            return;
        }
        await ws.SendAsync(Encoding.UTF8.GetBytes(payloadJson), WebSocketMessageType.Text, true, ct);
    }

    private static async Task PumpTcpAsync(NetworkStream stream, WebSocket ws, CancellationToken ct)
    {
        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var socketToWs = Task.Run(async () =>
        {
            var buf = new byte[32 * 1024];
            try
            {
                while (!pumpCts.Token.IsCancellationRequested)
                {
                    var read = await stream.ReadAsync(buf, pumpCts.Token);
                    if (read <= 0) break;
                    await ws.SendAsync(buf.AsMemory(0, read), WebSocketMessageType.Binary, true, pumpCts.Token);
                }
            }
            catch { }
            pumpCts.Cancel();
        });
        var buffer = new byte[64 * 1024];
        try
        {
            while (!pumpCts.Token.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult frame;
                do
                {
                    frame = await ws.ReceiveAsync(buffer, pumpCts.Token);
                    if (frame.MessageType == WebSocketMessageType.Close) break;
                    ms.Write(buffer, 0, frame.Count);
                } while (!frame.EndOfMessage);
                if (frame.MessageType == WebSocketMessageType.Close) break;
                await stream.WriteAsync(ms.ToArray(), pumpCts.Token);
            }
        }
        catch { }
        pumpCts.Cancel();
        try { await socketToWs; } catch { }
        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { }
    }

    private static async Task<string?> ReceiveTextMessageAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
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
