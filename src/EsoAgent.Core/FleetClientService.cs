using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;

namespace EsoAgent.Core;

/// <summary>
/// The agent's fleet link: a persistent OUTBOUND WebSocket to the fleet server.
/// Flow: hello (device code + public key) → server challenge → device signs the
/// nonce (proof of key possession) → server reports enrollment state. Only when
/// the owner has APPROVED the device does the server deliver the trusted signer
/// public keys, and only then are relayed commands executed — each command is
/// verified against those pinned keys on the device itself, so the relay cannot
/// forge commands. Verified commands run against the agent's own loopback API
/// (full HMAC + audit + kill-switch semantics apply unchanged).
/// </summary>
public sealed class FleetClientService : BackgroundService
{
    private readonly AgentState _state;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    // Two trusted signer roles (access tiers): the OPERATOR key (full fleet control)
    // and the OWNER-PANEL key (the only key allowed to sign owner-reserved actions).
    private string? _operatorKey;
    private string? _panelKey;
    private string _enrollmentState = "Unknown";
    private volatile bool _stateFrameSeen;
    private MeshApplier? _meshApplier;
    /// <summary>Live relayed streams (v1.2) by stream id: relayed desktop sessions + tunnels.</summary>
    private readonly ConcurrentDictionary<string, StreamSession> _streams = new();

    public FleetClientService(AgentState state) => _state = state;

    private string SignersFile => _state.Config.ResolvePath("operator.pubs");

    private sealed class TrustedKeys
    {
        public string? Operator { get; set; }
        public string? Panel { get; set; }
        /// <summary>Approved-peer set for the P2P gate (code → base64 SPKI), as
        /// last delivered in a state frame. Persisted with the signer keys so a
        /// device that restarts offline keeps its last set; the next state
        /// frame replaces it wholesale.</summary>
        public Dictionary<string, string>? Peers { get; set; }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LoadPersistedSigners();
        RefreshConsoleLink();
        var backoff = TimeSpan.FromSeconds(5);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunConnectionAsync(stoppingToken);
                backoff = TimeSpan.FromSeconds(5);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _state.Audit.Write("fleet", $"connection failed: {ex.Message}", "retrying");
            }
            _state.FleetConnected = false;
            try { await Task.Delay(backoff, stoppingToken); } catch { break; }
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, TimeSpan.FromSeconds(60).Ticks));
        }
    }

    private void LoadPersistedSigners()
    {
        try
        {
            if (File.Exists(SignersFile))
            {
                var keys = JsonSerializer.Deserialize<TrustedKeys>(File.ReadAllText(SignersFile));
                _operatorKey = keys?.Operator;
                _panelKey = keys?.Panel;
                if (keys?.Peers is { Count: > 0 } peers)
                    _state.ApprovedPeers = new Dictionary<string, string>(peers, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch { _operatorKey = null; _panelKey = null; }
        // Keep AgentState in sync from boot, not only from the first state
        // frame: the P2P trust gate reads these before any frame arrives.
        _state.OperatorPublicKeySpki = _operatorKey;
        _state.PanelPublicKeySpki = _panelKey;
    }

    private void PersistSigners()
    {
        try
        {
            File.WriteAllText(SignersFile, JsonSerializer.Serialize(new TrustedKeys
            {
                Operator = _operatorKey,
                Panel = _panelKey,
                Peers = new Dictionary<string, string>(_state.ApprovedPeers),
            }));
        }
        catch { }
    }

    /// <summary>
    /// Publishes (or refreshes) the local console link in the data dir so
    /// the desktop app on this machine can discover this agent: its fleet
    /// code, name, fleet address + certificate pin, and version — nothing
    /// secret (see ConsoleLink). Best-effort by design: a link-file failure
    /// must never disturb the fleet connection it describes.
    /// </summary>
    private void RefreshConsoleLink()
    {
        try
        {
            var cfg = _state.Config;
            var device = _state.Device;
            if (!cfg.Fleet.Enabled || device == null || string.IsNullOrWhiteSpace(cfg.Fleet.ServerUrl)) return;
            var fleetUrl = ConsoleLink.FleetUrlFromServerUrl(cfg.Fleet.ServerUrl);
            if (fleetUrl == null) return;
            ConsoleLink.Write(cfg.ResolvedDataDir, new ConsoleLink
            {
                DeviceCode = device.Code,
                DeviceName = cfg.ResolvedDeviceName,
                FleetUrl = fleetUrl,
                FleetFingerprint = (cfg.Fleet.ServerFingerprint ?? "").Trim().ToLowerInvariant(),
                AgentVersion = ProductInfo.Version,
            });
        }
        catch { }
    }

    private void RemoveConsoleLink()
    {
        try
        {
            var path = ConsoleLink.PathForDataDir(_state.Config.ResolvedDataDir);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    private async Task RunConnectionAsync(CancellationToken ct)
    {
        var cfg = _state.Config;
        var fleet = cfg.Fleet;
        var device = _state.Device ?? throw new InvalidOperationException("Device identity not initialized.");
        using var ws = new ClientWebSocket();
        // The fleet server is reached directly (tailnet/LAN) — never via an ambient proxy.
        ws.Options.Proxy = new System.Net.WebProxy();
        if (!string.IsNullOrWhiteSpace(fleet.ServerFingerprint))
        {
            var fp = fleet.ServerFingerprint.Trim();
            ws.Options.RemoteCertificateValidationCallback = (_, cert, _, _) =>
                cert != null && string.Equals(
                    Convert.ToHexString(SHA256.HashData(new X509Certificate2(cert).GetRawCertData())).ToLowerInvariant(),
                    fp, StringComparison.OrdinalIgnoreCase);
        }
        await ws.ConnectAsync(new Uri(fleet.ServerUrl!), ct);
        _state.FleetConnected = true;
        _stateFrameSeen = false;
        _state.Audit.Write("fleet", $"connected to {fleet.ServerUrl} as {device.Code}", "ok");

        await SendAsync(ws, new FleetHelloFrame
        {
            Code = device.Code,
            Name = cfg.ResolvedDeviceName,
            Os = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux",
            OsDescription = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            Version = ProductInfo.Version,
            Ips = NetHelpers.DiscoverInterfaceIps().ToList(),
            PublicKey = device.PublicKeyBase64,
            WgPublicKey = WgIdentity.LoadOrCreate(cfg.DataDir).PublicKeyB64,
            WgEndpoint = cfg.Mesh.AdvertiseEndpoint,
            AdvertisedSubnets = cfg.Mesh.AdvertisedSubnets,
            EnrollmentKey = cfg.Fleet.EnrollmentKey,
            Macs = WakeSender.LocalMacs(),
        }, ct);
        RefreshConsoleLink();

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = Task.Run(async () =>
        {
            while (!heartbeatCts.Token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), heartbeatCts.Token);
                if (ws.State == WebSocketState.Open)
                    await SendAsync(ws, new FleetHeartbeatFrame { At = DateTimeOffset.UtcNow }, heartbeatCts.Token);
            }
        }, heartbeatCts.Token);

        // Activity shipper (v1.2): carries this device's audit lines to the
        // fleet server's unified timeline while Approved. The offset persists
        // across restarts; delivery is at-least-once and the server dedupes.
        using var shipperCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var shipper = Task.Run(() => ShipActivityLoopAsync(ws, shipperCts.Token), shipperCts.Token);

        // Self-heal watchdog (v1.2.7): on the first start after the v1.2.5
        // upgrade this client connected and wrote its "connected" audit line,
        // then never processed the state frame the server sends immediately
        // after hello — enrollment, signers and mesh all stalled until a
        // manual service restart, after which the identical code processed
        // everything in seconds. No provable cause exists in this path (the
        // state handler is deserialize + a small file write; every mesh step
        // behind it is timeout-bounded), so the cure is bounded and generic:
        // a connection that has produced no state frame within 60 s of
        // connecting is dropped ONCE (this watchdog is armed per connection)
        // and the outer loop reconnects under its existing exponential
        // backoff — a persistently broken server therefore cannot cause a
        // tight reconnect loop.
        var healWatchdog = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(60), heartbeatCts.Token);
                if (!_stateFrameSeen && ws.State == WebSocketState.Open)
                {
                    _state.Audit.Write("fleet", "connected for 60s without a state frame — dropping the connection to reconnect (self-heal)", "warn");
                    ws.Abort();
                }
            }
            catch { /* cancelled together with the connection — nothing to heal */ }
        }, heartbeatCts.Token);

        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var frame = await ReceiveAsync(ws, ct);
                if (frame == null) break;
                await HandleFrameAsync(ws, frame, ct);
            }
        }
        finally
        {
            heartbeatCts.Cancel();
            shipperCts.Cancel();
            try { await heartbeat; } catch { }
            try { await shipper; } catch { }
            try { await healWatchdog; } catch { }
            foreach (var (id, session) in _streams)
                if (_streams.TryRemove(id, out var s)) s.Close();
            _state.FleetConnected = false;
        }
    }

    private string ActivityOffsetPath => _state.Config.ResolvePath("activity.offset");

    private async Task ShipActivityLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        long offset = 0;
        try { if (File.Exists(ActivityOffsetPath) && long.TryParse(File.ReadAllText(ActivityOffsetPath).Trim(), out var saved)) offset = saved; }
        catch { }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                if (_enrollmentState != "Approved" || ws.State != WebSocketState.Open) continue;
                var (lines, total) = _state.Audit.ReadRange((int)Math.Min(offset, int.MaxValue), 200);
                if (total < offset) { offset = 0; continue; } // audit file rotated — restart; server dedupes by content hash
                if (lines.Length == 0) continue;
                var frame = new FleetActivityFrame
                {
                    Lines = lines.Select((line, i) => new FleetActivityLine { Offset = offset + i, Line = line }).ToList(),
                };
                await SendAsync(ws, frame, ct);
                offset += lines.Length;
                try { File.WriteAllText(ActivityOffsetPath, offset.ToString()); } catch { }
            }
            catch (OperationCanceledException) { break; }
            catch { /* shipping is best-effort; the next tick retries from the persisted offset */ }
        }
    }

    private async Task HandleFrameAsync(ClientWebSocket ws, string json, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(json);
        var type = doc.RootElement.GetProperty("type").GetString();
        switch (type)
        {
            case "challenge":
            {
                var challenge = JsonSerializer.Deserialize<FleetChallengeFrame>(json, EsoJson.Options)!;
                var nonce = Convert.FromBase64String(challenge.Nonce);
                await SendAsync(ws, new FleetChallengeResponseFrame { Signature = _state.Device!.SignBase64(nonce) }, ct);
                break;
            }
            case "state":
            {
                var stateFrame = JsonSerializer.Deserialize<FleetStateFrame>(json, EsoJson.Options)!;
                _stateFrameSeen = true;
                _enrollmentState = stateFrame.State;
                _state.FleetEnrollmentState = stateFrame.State;
                // Clock-skew sample for the doctor check (v1.1): the server stamps
                // its state frames; skew = server time − local time at receipt.
                if (stateFrame.ServerTime is { } serverTime)
                {
                    _state.ServerSkewSeconds = serverTime - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    _state.ServerSkewSampledAt = DateTimeOffset.UtcNow;
                }
                if (stateFrame.State == "Approved")
                {
                    // State frames are authoritative for the operator key: a null
                    // operator key here means the owner revoked the operator —
                    // drop it. (The explicit revoke-operator admin frame does the
                    // same for devices that are connected when revocation happens.)
                    _operatorKey = string.IsNullOrWhiteSpace(stateFrame.OperatorPublicKey) ? null : stateFrame.OperatorPublicKey;
                    if (!string.IsNullOrWhiteSpace(stateFrame.PanelPublicKey)) _panelKey = stateFrame.PanelPublicKey;
                    // The approved-peer set rides the same frame (P2P trust
                    // feed): replaced wholesale, self excluded defensively.
                    var peers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var p in stateFrame.ApprovedPeers)
                        if (!string.IsNullOrWhiteSpace(p.Code) && !string.IsNullOrWhiteSpace(p.PublicKey)
                            && !p.Code.Equals(_state.Device?.Code, StringComparison.OrdinalIgnoreCase))
                            peers[p.Code] = p.PublicKey;
                    _state.ApprovedPeers = peers;
                    PersistSigners();
                }
                if (stateFrame.State == "Rejected")
                {
                    _operatorKey = null; _panelKey = null;
                    _state.ApprovedPeers = new Dictionary<string, string>();
                    PersistSigners();
                }
                // v1.2: the update-fetch endpoint verifies rollout signatures
                // against the same trusted keys — keep AgentState in sync.
                _state.OperatorPublicKeySpki = _operatorKey;
                _state.PanelPublicKeySpki = _panelKey;
                _state.Audit.Write("fleet", $"enrollment state: {_enrollmentState}", "ok");
                // The console link tracks enrollment: refreshed on every
                // state change, removed when the fleet rejects this device
                // (it is then no longer a fleet device worth discovering).
                if (stateFrame.State == "Rejected") RemoveConsoleLink();
                else RefreshConsoleLink();
                break;
            }
            case "stream-open":
            {
                // Relayed stream (v1.2): verified HERE against the pinned keys —
                // the relay only carried the signed envelope. Long-lived, so it
                // runs off the read loop.
                var open = JsonSerializer.Deserialize<FleetStreamOpenFrame>(json, EsoJson.Options)!;
                _ = Task.Run(() => HandleStreamOpenAsync(ws, open, ct), ct);
                break;
            }
            case "stream-data":
            {
                var data = JsonSerializer.Deserialize<FleetStreamDataFrame>(json, EsoJson.Options)!;
                if (_streams.TryGetValue(data.StreamId, out var session)) session.Deliver(data.Data);
                break;
            }
            case "stream-close":
            {
                var close = JsonSerializer.Deserialize<FleetStreamCloseFrame>(json, EsoJson.Options)!;
                if (_streams.TryRemove(close.StreamId, out var session)) session.Close();
                break;
            }
            case "command":
            {
                var cmd = JsonSerializer.Deserialize<FleetCommandFrame>(json, EsoJson.Options)!;
                var result = await ExecuteCommandAsync(cmd, ct);
                await SendAsync(ws, result, ct);
                break;
            }
            case "admin":
            {
                var admin = JsonSerializer.Deserialize<FleetAdminFrame>(json, EsoJson.Options)!;
                var result = ExecuteAdmin(admin);
                await SendAsync(ws, result, ct);
                break;
            }
            case "mesh":
            {
                // Mesh state is server coordination data (peer keys/IPs/ACL plan),
                // not a command: it carries no operator signature because it
                // grants no execution — the device only reconfigures its own
                // WireGuard/hosts/firewall from it, and every change is audited.
                var mesh = JsonSerializer.Deserialize<FleetMeshFrame>(json, EsoJson.Options)!;
                _meshApplier ??= new MeshApplier(_state);
                await _meshApplier.ApplyAsync(mesh, ct);
                break;
            }
        }
    }

    private async Task<FleetResultFrame> ExecuteCommandAsync(FleetCommandFrame cmd, CancellationToken ct)
    {
        var fail = new Func<string, FleetResultFrame>(error => new FleetResultFrame
        { CommandId = cmd.CommandId, HttpStatus = 0, Error = error });

        if (_operatorKey == null && _panelKey == null)
            return fail("Device is not enrolled/approved (no trusted keys pinned); command refused.");

        byte[] body;
        try { body = Convert.FromBase64String(cmd.BodyBase64); }
        catch { return fail("Invalid body encoding."); }

        // Tier check: the frame's claimed signer role must match the key that verifies it.
        var signerKey = cmd.Signer == "owner-panel" ? _panelKey : _operatorKey;
        if (signerKey == null) return fail($"Signer role '{cmd.Signer}' is not trusted by this device (revoked or never approved).");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // Queued commands (v1.1) carry an operator-signed expiry and verify
        // against it; immediate commands use the ±120 s window as before.
        var verified = cmd.ExpiresAt is { } expiresAt
            ? OperatorSigning.VerifyQueuedCommand(signerKey, cmd.Signature, cmd.CommandId, cmd.IssuedAt, expiresAt, cmd.Method, cmd.Path, body, now)
            : OperatorSigning.VerifyCommand(signerKey, cmd.Signature, cmd.CommandId, cmd.IssuedAt, cmd.Method, cmd.Path, body, now);
        if (!verified)
        {
            _state.Audit.Write("fleet", $"REJECTED command {cmd.CommandId}: signature invalid for signer {cmd.Signer}", "refused");
            return fail("Operator signature verification failed on device.");
        }

        // Dispatch to our own loopback API: HMAC auth, audit log and the owner's
        // kill switch all apply exactly as for a direct connection.
        try
        {
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true, // loopback only; HMAC still authenticates
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
            var pathAndQuery = cmd.Path;
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var sig = HmacAuth.Sign(_state.Token, cmd.Method, pathAndQuery, ts, body);
            using var req = new HttpRequestMessage(new HttpMethod(cmd.Method), $"https://127.0.0.1:{_state.Config.Port}{pathAndQuery}");
            req.Headers.Add(HmacAuth.TimestampHeader, ts.ToString());
            req.Headers.Add(HmacAuth.SignatureHeader, sig);
            if (body.Length > 0)
                req.Content = new ByteArrayContent(body) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") } };
            using var res = await http.SendAsync(req, ct);
            var resBody = await res.Content.ReadAsByteArrayAsync(ct);
            _state.Audit.Write("fleet", $"relayed command {cmd.Method} {cmd.Path} (signer {cmd.Signer})", ((int)res.StatusCode).ToString());
            return new FleetResultFrame
            {
                CommandId = cmd.CommandId,
                HttpStatus = (int)res.StatusCode,
                BodyBase64 = Convert.ToBase64String(resBody),
            };
        }
        catch (Exception ex)
        {
            return fail($"Device dispatch failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Fleet admin actions. Owner-reserved actions (revoke-operator, set-operator-key)
    /// verify against the PANEL key only — the operator key can never sign them,
    /// so the operator cannot revoke itself or rotate its own key.
    /// </summary>
    private FleetResultFrame ExecuteAdmin(FleetAdminFrame admin)
    {
        FleetResultFrame Fail(string error) => new() { CommandId = admin.CommandId, HttpStatus = 403, Error = error };
        FleetResultFrame Ok(object payload) => new()
        {
            CommandId = admin.CommandId,
            HttpStatus = 200,
            BodyBase64 = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(payload, EsoJson.Options)),
        };

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        bool panelSigned = _panelKey != null && admin.Signer == "owner-panel" &&
            OperatorSigning.VerifyAdmin(_panelKey, admin.Signature, admin.Action, admin.CommandId, admin.IssuedAt, admin.PayloadJson, now);
        bool operatorSigned = _operatorKey != null && admin.Signer == "operator" &&
            OperatorSigning.VerifyAdmin(_operatorKey, admin.Signature, admin.Action, admin.CommandId, admin.IssuedAt, admin.PayloadJson, now);

        switch (admin.Action)
        {
            case "rotate-token":
            {
                if (!panelSigned && !operatorSigned) return Fail("rotate-token requires a trusted operator or panel signature.");
                var token = _state.RotateToken();
                _state.Audit.Write("fleet", $"device token rotated via fleet admin (signer {admin.Signer})", "ok");
                return Ok(new { token });
            }
            case "revoke-operator":
            {
                if (!panelSigned) return Fail("revoke-operator is owner-reserved: panel signature required.");
                _operatorKey = null;
                PersistSigners();
                _state.Audit.Write("fleet", "operator key REVOKED by owner (panel-signed)", "ok");
                return Ok(new { revoked = true });
            }
            case "set-operator-key":
            {
                if (!panelSigned) return Fail("set-operator-key is owner-reserved: panel signature required.");
                try
                {
                    using var doc = JsonDocument.Parse(admin.PayloadJson);
                    var newKey = doc.RootElement.GetProperty("newPublicKey").GetString();
                    if (string.IsNullOrWhiteSpace(newKey)) return Fail("Missing newPublicKey.");
                    _operatorKey = newKey;
                    PersistSigners();
                    _state.Audit.Write("fleet", "operator key replaced by owner (panel-signed)", "ok");
                    return Ok(new { replaced = true });
                }
                catch (Exception ex) { return Fail($"Bad set-operator-key payload: {ex.Message}"); }
            }
            default:
                return Fail($"Unknown admin action '{admin.Action}'.");
        }
    }

    // ---------- relayed streams (v1.2): desktop + tunnels ----------

    /// <summary>One live relayed stream on this device.</summary>
    private sealed class StreamSession
    {
        public required CancellationTokenSource Cts { get; init; }
        public FleetDesktopChannel? Channel { get; set; }
        public Func<string, Task>? DataHandler { get; set; }

        public void Deliver(string data)
        {
            if (Channel != null) Channel.Writer.TryWrite(data);
            else if (DataHandler != null) _ = DataHandler(data);
        }

        public void Close()
        {
            try { Cts.Cancel(); } catch { }
            Channel?.Complete();
        }
    }

    /// <summary>The desktop runner's channel over fleet stream frames.
    /// (Internal, not private, so the channel factory in this service and
    /// the unit tests can name the type.)</summary>
    internal sealed class FleetDesktopChannel : IDesktopChannel
    {
        private readonly FleetClientService _svc;
        private readonly ClientWebSocket _ws;
        private readonly string _streamId;
        private readonly Channel<string> _inbound = Channel.CreateUnbounded<string>();

        public FleetDesktopChannel(FleetClientService svc, ClientWebSocket ws, string streamId)
        {
            _svc = svc; _ws = ws; _streamId = streamId;
        }

        public ChannelWriter<string> Writer => _inbound.Writer;
        public void Complete() => _inbound.Writer.TryComplete();

        public Task SendTextAsync(string json, CancellationToken ct)
            => _svc.SendStreamDataAsync(_ws, _streamId, json, ct);

        public async Task<string?> ReceiveTextAsync(CancellationToken ct)
        {
            try { return await _inbound.Reader.ReadAsync(ct); }
            catch { return null; } // channel completed (stream closed) or cancelled
        }
    }

    /// <summary>
    /// Creates the desktop channel for a VERIFIED relay stream-open and seeds
    /// it with the hello from the signed open payload. The desktop runner
    /// reads the hello as the channel's first message — exactly as it does on
    /// the direct socket — but relay clients (App/CLI, panel browser, grant
    /// viewers) send the hello ONLY inside the signed stream-open
    /// (<see cref="FleetStreamOpenFrame.PayloadJson"/>), never as a stream
    /// message. Without this seed the client waits for a welcome while the
    /// runner waits for a hello: the session deadlocks (live-found on the
    /// box, BUILD-REPORT §11 §B; every producer — RelayDesktopClient,
    /// /panel/desktop-relay, /fleet/access/desktop — puts the hello in the
    /// payload, so this one delivery step fixes all relay faces at once).
    /// The payload was signature-verified by the caller before this runs.
    /// </summary>
    internal FleetDesktopChannel CreateDesktopChannel(ClientWebSocket ws, FleetStreamOpenFrame open)
    {
        var channel = new FleetDesktopChannel(this, ws, open.StreamId);
        channel.Writer.TryWrite(open.PayloadJson);
        return channel;
    }

    private Task SendStreamDataAsync(ClientWebSocket ws, string streamId, string data, CancellationToken ct)
        => SendAsync(ws, new FleetStreamDataFrame { StreamId = streamId, Data = data }, ct);

    /// <summary>
    /// Handles a relayed stream-open. THE security core of v1.2: the signature
    /// is verified against the pinned operator/panel keys exactly like a
    /// command — a relay that cannot forge signatures cannot open streams.
    /// </summary>
    private async Task HandleStreamOpenAsync(ClientWebSocket ws, FleetStreamOpenFrame open, CancellationToken connectionCt)
    {
        async Task RefuseAsync(string error)
        {
            _state.Audit.Write("fleet/stream", $"REFUSED stream {open.StreamId} ({open.Kind}, signer {open.Signer}): {error}", "refused");
            try { await SendAsync(ws, new FleetStreamOpenedFrame { StreamId = open.StreamId, Ok = false, Error = error }, connectionCt); } catch { }
        }

        var signerKey = open.Signer == "owner-panel" ? _panelKey : _operatorKey;
        if (signerKey == null) { await RefuseAsync($"Signer role '{open.Signer}' is not trusted by this device (revoked or never approved)."); return; }
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!OperatorSigning.VerifyStream(signerKey, open.Signature, open.StreamId, open.IssuedAt, open.Kind, open.PayloadJson, now))
        { await RefuseAsync("Stream signature verification failed on device."); return; }
        if (_state.KillSwitch.IsLocked) { await RefuseAsync("Agent is locked by the owner's kill switch."); return; }
        if (open.Kind is "tunnel" or "tunnel-udp" && !_state.Config.Capabilities.Tunnels)
        { await RefuseAsync("Tunnels are disabled on this device by the owner's capability policy."); return; }
        if (_streams.ContainsKey(open.StreamId)) { await RefuseAsync("Stream id already in use."); return; }

        var session = new StreamSession { Cts = CancellationTokenSource.CreateLinkedTokenSource(connectionCt) };
        if (!_streams.TryAdd(open.StreamId, session)) { await RefuseAsync("Stream id already in use."); return; }

        try
        {
            switch (open.Kind)
            {
                case "desktop":
                {
                    var channel = CreateDesktopChannel(ws, open);
                    session.Channel = channel;
                    await SendAsync(ws, new FleetStreamOpenedFrame { StreamId = open.StreamId, Ok = true }, connectionCt);
                    _state.Audit.Write("fleet/stream", $"relayed desktop stream {open.StreamId} opened (signer {open.Signer})", "ok");
                    await DesktopStreamRunner.RunAsync(channel, _state, "fleet", session.Cts.Token, open.Signer);
                    break;
                }
                case "tunnel":
                {
                    TunnelTarget? target;
                    try { target = JsonSerializer.Deserialize<TunnelTarget>(open.PayloadJson, EsoJson.Options); }
                    catch { target = null; }
                    if (target == null || string.IsNullOrWhiteSpace(target.Host) || target.Port is < 1 or > 65535)
                    { await RefuseAsync("Bad tunnel target."); return; }
                    var tcp = new TcpClient();
                    try { await tcp.ConnectAsync(target.Host, target.Port, session.Cts.Token); }
                    catch (Exception ex) { await RefuseAsync($"Tunnel connect to {target} failed: {ex.Message}"); return; }
                    var stream = tcp.GetStream();
                    var writeGate = new SemaphoreSlim(1, 1);
                    session.DataHandler = async data =>
                    {
                        try
                        {
                            var bytes = Convert.FromBase64String(data);
                            await writeGate.WaitAsync(session.Cts.Token);
                            try { await stream.WriteAsync(bytes, session.Cts.Token); }
                            finally { writeGate.Release(); }
                        }
                        catch { session.Close(); }
                    };
                    await SendAsync(ws, new FleetStreamOpenedFrame { StreamId = open.StreamId, Ok = true }, connectionCt);
                    _state.Audit.Write("fleet/stream", $"relayed tunnel stream {open.StreamId} opened to {target} (signer {open.Signer})", "ok");
                    await TunnelPump.PumpSocketToFramesAsync(stream,
                        data => SendStreamDataAsync(ws, open.StreamId, data, session.Cts.Token), session.Cts.Token);
                    tcp.Dispose();
                    break;
                }
                case "tunnel-udp":
                {
                    // UDP tunnel (v1.2): each stream-data frame is ONE datagram
                    // (base64) to the target; replies come back the same way.
                    // UDP is connectionless — "connect" here just fixes the peer.
                    TunnelTarget? target;
                    try { target = JsonSerializer.Deserialize<TunnelTarget>(open.PayloadJson, EsoJson.Options); }
                    catch { target = null; }
                    if (target == null || string.IsNullOrWhiteSpace(target.Host) || target.Port is < 1 or > 65535)
                    { await RefuseAsync("Bad tunnel target."); return; }
                    var udp = new UdpClient();
                    try { udp.Connect(target.Host, target.Port); }
                    catch (Exception ex) { await RefuseAsync($"UDP tunnel to {target} failed: {ex.Message}"); return; }
                    var udpGate = new SemaphoreSlim(1, 1);
                    session.DataHandler = async data =>
                    {
                        try
                        {
                            var bytes = Convert.FromBase64String(data);
                            await udpGate.WaitAsync(session.Cts.Token);
                            try { await udp.SendAsync(bytes, session.Cts.Token); }
                            finally { udpGate.Release(); }
                        }
                        catch { session.Close(); }
                    };
                    await SendAsync(ws, new FleetStreamOpenedFrame { StreamId = open.StreamId, Ok = true }, connectionCt);
                    _state.Audit.Write("fleet/stream", $"relayed UDP tunnel stream {open.StreamId} opened to {target} (signer {open.Signer})", "ok");
                    try
                    {
                        while (!session.Cts.Token.IsCancellationRequested)
                        {
                            var received = await udp.ReceiveAsync(session.Cts.Token);
                            await SendStreamDataAsync(ws, open.StreamId, Convert.ToBase64String(received.Buffer), session.Cts.Token);
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch { }
                    udp.Dispose();
                    break;
                }
                default:
                    await RefuseAsync($"Unknown stream kind '{open.Kind}'.");
                    return;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _state.Audit.Write("fleet/stream", $"stream {open.StreamId} error: {ex.Message}", "error");
        }
        finally
        {
            _streams.TryRemove(open.StreamId, out _);
            session.Close();
            try { await SendAsync(ws, new FleetStreamCloseFrame { StreamId = open.StreamId, Reason = "device closed the stream" }, connectionCt); } catch { }
        }
    }

    private async Task SendAsync<T>(ClientWebSocket ws, T frame, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, EsoJson.Options);
        await _sendGate.WaitAsync(ct);
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
        finally { _sendGate.Release(); }
    }

    private static async Task<string?> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
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
