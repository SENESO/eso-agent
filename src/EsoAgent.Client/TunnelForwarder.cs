using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using EsoAgent.Core;

namespace EsoAgent.Client;

/// <summary>
/// One running tunnel (UI overhaul Phase 4): the listener lifecycle behind
/// both the CLI's `eso tunnel` and the App's Tunnels view. The CLI used to
/// own this plumbing inline; it now lives here so both surfaces run the
/// same code. State changes raise <see cref="Changed"/>; per-connection
/// failures go to the optional log callback and never stop the listener.
/// </summary>
public sealed class TunnelForwarder : IDisposable
{
    private readonly ClientConfig _cfg;
    private readonly Action<string>? _log;
    private CancellationTokenSource? _cts;
    private TcpListener? _listener;
    private UdpClient? _udpListener;
    private Task? _loop;
    private int _activeConnections;

    public TunnelForwarder(ClientConfig cfg, TunnelSpec spec, Action<string>? log = null)
    {
        _cfg = cfg;
        Spec = spec;
        _log = log;
    }

    public TunnelSpec Spec { get; }
    public string State { get; private set; } = "stopped";   // starting | running | stopped | failed
    public string? Error { get; private set; }
    public int ActiveConnections => Volatile.Read(ref _activeConnections);
    public DateTimeOffset StartedAt { get; private set; }

    public event Action? Changed;

    private void SetState(string state, string? error = null)
    {
        State = state;
        Error = error;
        Changed?.Invoke();
    }

    /// <summary>Binds the local listener and starts accepting. Returns false
    /// (State = failed, Error set) instead of throwing for expected failures
    /// like a busy local port.</summary>
    public Task<bool> StartAsync()
    {
        if (State is "starting" or "running") return Task.FromResult(true);
        if (TunnelSpec.Validate(Spec) is { } invalid)
        {
            SetState("failed", invalid);
            return Task.FromResult(false);
        }
        _cts = new CancellationTokenSource();
        try
        {
            if (Spec.Udp)
            {
                _udpListener = new UdpClient(new IPEndPoint(IPAddress.Loopback, Spec.LocalPort));
                _loop = RunUdpLoopAsync(_cts.Token);
            }
            else
            {
                _listener = new TcpListener(IPAddress.Loopback, Spec.LocalPort);
                _listener.Start();
                _loop = RunTcpLoopAsync(_cts.Token);
            }
        }
        catch (Exception ex)
        {
            SetState("failed", ex.Message);
            return Task.FromResult(false);
        }
        StartedAt = DateTimeOffset.Now;
        SetState("running");
        return Task.FromResult(true);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _udpListener?.Dispose(); } catch { }
        if (State == "running") SetState("stopped");
    }

    public void Dispose() => Stop();

    // ---------- TCP ----------

    private async Task RunTcpLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                _ = HandleTcpAsync(client, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!ct.IsCancellationRequested) SetState("failed", ex.Message); }
    }

    private async Task HandleTcpAsync(TcpClient client, CancellationToken ct)
    {
        Interlocked.Increment(ref _activeConnections);
        Changed?.Invoke();
        using (client)
        {
            try
            {
                var target = Spec.Target;
                if (Spec.Socks)
                {
                    target = await Socks5HandshakeAsync(client.GetStream(), ct);
                    if (target == null) return;
                }
                using var channel = await OpenStreamAsync(_cfg, Spec.Device, udp: false, ct);
                await SendTargetAsync(channel, target!, ct);
                await PumpTcpAsync(client.GetStream(), channel.Ws, ct);
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested) _log?.Invoke($"tunnel connection failed: {ex.Message}");
            }
        }
        Interlocked.Decrement(ref _activeConnections);
        Changed?.Invoke();
    }

    // ---------- UDP ----------

    private async Task RunUdpLoopAsync(CancellationToken ct)
    {
        var sessions = new Dictionary<IPEndPoint, (WebSocket Ws, Task Pump)>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var datagram = await _udpListener!.ReceiveAsync(ct);
                if (!sessions.TryGetValue(datagram.RemoteEndPoint, out var session))
                {
                    var channel = await OpenStreamAsync(_cfg, Spec.Device, udp: true, ct);
                    await SendTargetAsync(channel, Spec.Target!, ct, fleetKind: "tunnel-udp");
                    var pump = PumpUdpRepliesAsync(channel.Ws, _udpListener, datagram.RemoteEndPoint, ct);
                    session = (channel.Ws, pump);
                    sessions[datagram.RemoteEndPoint] = session;
                }
                await session.Ws.SendAsync(datagram.Buffer, WebSocketMessageType.Binary, true, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!ct.IsCancellationRequested) _log?.Invoke($"udp tunnel failed: {ex.Message}"); }
        finally
        {
            foreach (var (ws, _) in sessions.Values) { try { ws.Dispose(); } catch { } }
        }
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

    // ---------- SOCKS5 (no-auth, CONNECT only) ----------

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

    // ---------- channel plumbing (fleet stream or direct /api/tunnel) ----------

    private sealed class TunnelChannel : IDisposable
    {
        public required ClientWebSocket Ws { get; init; }
        public FleetApiClient? Fleet { get; init; }
        public string? Code { get; init; }
        public void Dispose() { Ws.Dispose(); Fleet?.Dispose(); }
    }

    private static async Task<TunnelChannel> OpenStreamAsync(ClientConfig cfg, string device, bool udp, CancellationToken ct)
    {
        if (DeviceIdentity.LooksLikeCode(device))
        {
            var fleet = Program.CreateFleetClient(cfg);
            var ws = await fleet.ConnectStreamSocketAsync(udp ? "/fleet/tunnel-udp" : "/fleet/tunnel", ct);
            return new TunnelChannel { Ws = ws, Fleet = fleet, Code = device.ToUpperInvariant() };
        }
        var parts = device.Split(':');
        var directCfg = cfg.WithEndpoint(parts[0], parts.Length == 2 && int.TryParse(parts[1], out var p) ? p : cfg.Port);
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
