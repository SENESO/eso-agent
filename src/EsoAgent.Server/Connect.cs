using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EsoAgent.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EsoAgent.Server;

/// <summary>One live agent connection: serialized sends + pending command results.</summary>
public sealed class DeviceConnection
{
    public required string Code { get; init; }
    public required WebSocket Socket { get; init; }
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    public ConcurrentDictionary<string, TaskCompletionSource<FleetResultFrame>> Pending { get; } = new();
    // Relayed streams (v1.2): pending stream-opens + live stream data sinks/closers.
    public ConcurrentDictionary<string, TaskCompletionSource<FleetStreamOpenedFrame>> PendingStreams { get; } = new();
    public ConcurrentDictionary<string, Func<FleetStreamDataFrame, Task>> StreamSinks { get; } = new();
    public ConcurrentDictionary<string, Action> StreamClosers { get; } = new();

    /// <summary>Sends a signed stream-open to the device and awaits its verdict.</summary>
    public async Task<FleetStreamOpenedFrame> OpenStreamAsync(FleetStreamOpenFrame open, TimeSpan timeout, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<FleetStreamOpenedFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        PendingStreams[open.StreamId] = tcs;
        try
        {
            await SendAsync(open, ct);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            return await tcs.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            return new FleetStreamOpenedFrame { StreamId = open.StreamId, Ok = false, Error = "Timed out waiting for the device to open the stream." };
        }
        finally { PendingStreams.TryRemove(open.StreamId, out _); }
    }

    public async Task SendAsync<T>(T frame, CancellationToken ct = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, EsoJson.Options);
        await _sendGate.WaitAsync(ct);
        try { await Socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
        finally { _sendGate.Release(); }
    }

    public async Task<FleetResultFrame> SendAndWaitAsync<T>(T frame, string commandId, TimeSpan timeout, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<FleetResultFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        Pending[commandId] = tcs;
        try
        {
            await SendAsync(frame, ct);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            return await tcs.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            return new FleetResultFrame { CommandId = commandId, HttpStatus = 0, Error = "Timed out waiting for the device." };
        }
        finally { Pending.TryRemove(commandId, out _); }
    }

    public void FailAllPending(string error)
    {
        foreach (var (id, tcs) in Pending)
            tcs.TrySetResult(new FleetResultFrame { CommandId = id, HttpStatus = 0, Error = error });
        Pending.Clear();
        foreach (var (id, tcs) in PendingStreams)
            tcs.TrySetResult(new FleetStreamOpenedFrame { StreamId = id, Ok = false, Error = error });
        PendingStreams.Clear();
        foreach (var (_, closer) in StreamClosers) { try { closer(); } catch { } }
        StreamClosers.Clear();
        StreamSinks.Clear();
    }
}

public static class FleetConnectEndpoint
{
    public static void MapFleetConnect(this IEndpointRouteBuilder app, FleetServerState state)
    {
        app.Map("/fleet/connect", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            DeviceConnection? conn = null;
            try
            {
                // 1) hello
                var helloJson = await ReceiveTextAsync(ws, ctx.RequestAborted);
                if (helloJson == null) return;
                var hello = JsonSerializer.Deserialize<FleetHelloFrame>(helloJson, EsoJson.Options);
                if (hello == null || string.IsNullOrWhiteSpace(hello.Code)) return;

                var record = state.Registry.UpsertHello(hello);
                if (record == null)
                {
                    state.Audit.Write("fleet/connect", $"REFUSED {hello.Code}: public key does not match the registered key for this code", "refused");
                    await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "key mismatch", CancellationToken.None);
                    return;
                }

                conn = new DeviceConnection { Code = record.Code, Socket = ws };

                // 2) challenge-response: the device proves it holds the private key for the code.
                var nonce = RandomNumberGenerator.GetBytes(32);
                await conn.SendAsync(new FleetChallengeFrame { Nonce = Convert.ToBase64String(nonce) }, ctx.RequestAborted);
                var respJson = await ReceiveTextAsync(ws, ctx.RequestAborted);
                if (respJson == null) return;
                var resp = JsonSerializer.Deserialize<FleetChallengeResponseFrame>(respJson, EsoJson.Options);
                if (resp == null || !DeviceIdentity.VerifyWithPublicKey(record.PublicKey, nonce, resp.Signature))
                {
                    state.Audit.Write("fleet/connect", $"REFUSED {hello.Code}: challenge signature invalid", "refused");
                    await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "bad signature", CancellationToken.None);
                    return;
                }

                // Pre-approval enrollment key (unattended installs): a valid key
                // approves a Pending device immediately; manual approval stays
                // the default when no key is presented.
                if (record.State == "Pending" && state.Registry.TryConsumeEnrollmentKey(hello.EnrollmentKey))
                {
                    state.Registry.SetState(record.Code, "Approved");
                    record = state.Registry.Find(record.Code) ?? record;
                    state.Audit.Write("fleet/connect", $"{record.Code} auto-approved via enrollment key", "ok");
                }
                if (record.State == "Approved") state.Registry.EnsureVirtualIp(record.Code);

                // Replace any stale connection for this device.
                if (state.Connections.TryRemove(record.Code, out var stale))
                {
                    stale.FailAllPending("Device reconnected.");
                    try { await stale.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "replaced", CancellationToken.None); } catch { }
                }
                state.Connections[record.Code] = conn;
                state.Audit.Write("fleet/connect", $"{record.Code} ({record.Name}) connected, state {record.State}", "ok");
                await PushStateAsync(state, conn, record);
                if (record.State == "Approved") await FleetMesh.PushToDeviceAsync(state, record.Code);

                // Offline queue drain (v1.1): deliver anything queued for this
                // device. Fire-and-forget — results resolve through the read
                // loop below via the connection's pending table.
                if (record.State == "Approved")
                    _ = Task.Run(() => CommandQueueDelivery.DrainAsync(state, conn, record.Code));

                state.Activity.Add("device", "connect", $"{record.Name} connected (state {record.State})", "ok", record.Code, record.Name);
                SettleRolloutsOnConnect(state, record);

                // 3) read loop: heartbeats + command results + activity + streams
                while (ws.State == WebSocketState.Open)
                {
                    var json = await ReceiveTextAsync(ws, ctx.RequestAborted);
                    if (json == null) break;
                    using var doc = JsonDocument.Parse(json);
                    var type = doc.RootElement.GetProperty("type").GetString();
                    if (type == "heartbeat") state.Registry.Touch(record.Code);
                    else if (type == "result")
                    {
                        var result = JsonSerializer.Deserialize<FleetResultFrame>(json, EsoJson.Options);
                        if (result != null && conn.Pending.TryRemove(result.CommandId, out var tcs))
                            tcs.TrySetResult(result);
                    }
                    else if (type == "activity")
                    {
                        // Unified timeline (v1.2): the device's own audit lines,
                        // deduped by content-hash id inside the store.
                        var frame = JsonSerializer.Deserialize<FleetActivityFrame>(json, EsoJson.Options);
                        if (frame != null)
                        {
                            foreach (var line in frame.Lines)
                            {
                                var evt = ActivityEvent.FromAuditLine(record.Code, record.Name, line.Offset, line.Line);
                                if (evt != null && state.Activity.Record(evt))
                                {
                                    // A device-reported crash also becomes an alert (item 31):
                                    // the crash watch on the agent raises the audit line, the
                                    // server raises the alarm — same event, two severities.
                                    if (evt.Kind == "crash")
                                        state.Activity.AddAlert(evt.Summary, record.Code, record.Name);
                                }
                            }
                        }
                    }
                    else if (type == "stream-opened")
                    {
                        var opened = JsonSerializer.Deserialize<FleetStreamOpenedFrame>(json, EsoJson.Options);
                        if (opened != null && conn.PendingStreams.TryRemove(opened.StreamId, out var tcs))
                            tcs.TrySetResult(opened);
                    }
                    else if (type == "stream-data")
                    {
                        var data = JsonSerializer.Deserialize<FleetStreamDataFrame>(json, EsoJson.Options);
                        if (data != null && conn.StreamSinks.TryGetValue(data.StreamId, out var sink))
                            _ = sink(data);
                    }
                    else if (type == "stream-close")
                    {
                        var close = JsonSerializer.Deserialize<FleetStreamCloseFrame>(json, EsoJson.Options);
                        if (close != null)
                        {
                            conn.StreamSinks.TryRemove(close.StreamId, out _);
                            if (conn.StreamClosers.TryRemove(close.StreamId, out var closer))
                                closer();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                state.Audit.Write("fleet/connect", $"connection error: {ex.Message}", "error");
            }
            finally
            {
                if (conn != null)
                {
                    state.Connections.TryRemove(new KeyValuePair<string, DeviceConnection>(conn.Code, conn));
                    conn.FailAllPending("Device disconnected.");
                    state.Audit.Write("fleet/connect", $"{conn.Code} disconnected", "ok");
                    var rec = state.Registry.Find(conn.Code);
                    state.Activity.Add("device", "disconnect", $"{rec?.Name ?? conn.Code} disconnected", "ok", conn.Code, rec?.Name);
                }
            }
        });
    }

    /// <summary>
    /// Rollout settlement (v1.2): when a device reconnects, any rollout device
    /// entry still mid-flight is settled by the version the device NOW reports:
    /// changed → applied; unchanged → the update did not take (the agent's own
    /// apply health-check rolls back, so an unchanged version is an honest
    /// failure, never a silent success).
    /// </summary>
    public static void SettleRolloutsOnConnect(FleetServerState state, DeviceRecord record)
    {
        foreach (var rollout in state.Rollouts.InFlightFor(record.Code))
        {
            var device = rollout.Dto.Devices.First(d => d.Code.Equals(record.Code, StringComparison.OrdinalIgnoreCase));
            if (!string.Equals(device.FromVersion, record.Version, StringComparison.Ordinal))
            {
                if (state.Rollouts.SetDeviceState(rollout.Dto.Id, record.Code, RolloutStates.Applied, toVersion: record.Version))
                    state.Activity.Add("server", "rollout",
                        $"rollout {rollout.Dto.Id}: {record.Code} updated {device.FromVersion} → {record.Version}", "applied", record.Code, record.Name);
            }
            else
            {
                if (state.Rollouts.SetDeviceState(rollout.Dto.Id, record.Code, RolloutStates.Failed,
                        error: "Device reconnected still running the old version — the update did not take (rolled back or apply failed)."))
                    state.Activity.Add("server", "rollout",
                        $"rollout {rollout.Dto.Id}: {record.Code} still on {record.Version} after reconnect — update did not take", "failed", record.Code, record.Name);
            }
        }
    }

    /// <summary>Approval side-effects shared by every approval path (operator API, panel, CLI):
    /// assign the mesh virtual IP, push the new state to the device, and refresh mesh everywhere.</summary>
    public static async Task OnApprovedAsync(FleetServerState state, string code)
    {
        state.Registry.EnsureVirtualIp(code);
        var record = state.Registry.Find(code);
        if (record != null && state.Connections.TryGetValue(record.Code, out var conn))
            await PushStateAsync(state, conn, record);
        await FleetMesh.PushToAllAsync(state);
    }

    /// <summary>Sends the enrollment state frame; trusted keys are included only for Approved devices.</summary>
    public static async Task PushStateAsync(FleetServerState state, DeviceConnection conn, DeviceRecord record)
    {
        var frame = new FleetStateFrame { State = record.State, ServerTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
        if (record.State == "Approved")
        {
            frame.OperatorPublicKey = state.Registry.OperatorEnabled ? state.OperatorPublicKey : null;
            frame.PanelPublicKey = state.PanelPublicKey;
        }
        await conn.SendAsync(frame);
    }

    private static async Task<string?> ReceiveTextAsync(WebSocket ws, CancellationToken ct)
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

/// <summary>
/// Periodic state-frame resync (field defect 2026-11-21): the connect-time
/// state frame was the only one a device ever received, so losing that
/// single send stranded the device — the field case was this server
/// waking from hibernation: it accepted the laptop's WebSocket but the
/// frame never arrived, the device looped on its 60 s "no state frame"
/// self-heal, and the mesh only came up after the server was restarted
/// by hand. This service re-pushes the enrollment state frame (and, for
/// Approved devices, the mesh frame) to every connected device every
/// <see cref="Interval"/> — well inside the client's 60 s window — so a
/// lost frame heals on the next tick instead of needing a restart. Both
/// frames are handled idempotently by the device (the same code path as
/// the connect-time push), so the resync changes nothing when the first
/// frame already landed; it costs a few hundred bytes per device per
/// tick. It also covers a server that was itself asleep when a device
/// connected: the audience may have missed the first frame, never the
/// second.
/// </summary>
public sealed class StateFrameResyncService : Microsoft.Extensions.Hosting.BackgroundService
{
    /// <summary>Resync cadence — half the client's 60 s no-state-frame window, so one lost tick still heals in time.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly FleetServerState _state;

    public StateFrameResyncService(FleetServerState state) => _state = state;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(Interval, ct); } catch { return; }
        while (!ct.IsCancellationRequested)
        {
            try { await ResyncOnceAsync(_state); }
            catch (Exception ex) { _state.Audit.Write("fleet/resync", "resync failed: " + ex.Message, "error"); }
            try { await Task.Delay(Interval, ct); } catch { return; }
        }
    }

    /// <summary>
    /// One resync tick: re-push the state frame (plus the mesh frame for
    /// Approved devices) to every connected device. Returns how many
    /// devices were pushed to. A device whose send fails is skipped —
    /// its socket is dead and the connect path settles it; one bad
    /// connection never blocks the others.
    /// </summary>
    public static async Task<int> ResyncOnceAsync(FleetServerState state)
    {
        var pushed = 0;
        foreach (var (code, conn) in state.Connections.ToArray())
        {
            var record = state.Registry.Find(code);
            if (record == null) continue;
            try
            {
                await FleetConnectEndpoint.PushStateAsync(state, conn, record);
                if (record.State == "Approved") await FleetMesh.PushToDeviceAsync(state, record.Code);
                pushed++;
            }
            catch { /* dead socket — the read loop's finally settles it */ }
        }
        return pushed;
    }
}
