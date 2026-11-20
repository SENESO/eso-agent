using EsoAgent.Client;
using EsoAgent.Core;

namespace EsoAgent.App.Services;

/// <summary>
/// Adapts the shared <see cref="Transport"/> (direct HMAC or fleet relay)
/// to the Core transfer engine's channel seam. The Transport's calls carry
/// no cancellation token, so a cancel lands at the next chunk boundary —
/// exactly the engine's documented contract. Device errors surface as a
/// failed reply, never a throw, matching how the Files view always read
/// them.
/// </summary>
public sealed class TransportChannelAdapter : ITransferChannel
{
    private readonly Transport _transport;

    public TransportChannelAdapter(Transport transport) => _transport = transport;

    public bool IsFleet => _transport.IsFleet;

    public async Task<TransferReply> SendAsync(string method, string path, object? body, CancellationToken ct) =>
        Map(await _transport.SendAsync(method, path, body));

    public async Task<TransferReply> SendRawAsync(string method, string path, byte[] body, CancellationToken ct) =>
        Map(await _transport.SendRawAsync(method, path, body));

    private static TransferReply Map(List<DeviceReply> replies)
    {
        if (replies.Count == 0) return TransferReply.Fail("No reply from the device.");
        var reply = replies[0];
        if (reply.Ok) return new TransferReply(true, reply.Body, null);
        var error = reply.Error ?? (string.IsNullOrWhiteSpace(reply.Text) ? $"HTTP {reply.Status}" : reply.Text);
        return TransferReply.Fail(error);
    }

    public void Dispose() => _transport.Dispose();
}
