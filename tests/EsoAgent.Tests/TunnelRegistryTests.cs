using EsoAgent.Server;
using Xunit;

namespace EsoAgent.Tests;

/// <summary>
/// Live tunnel mirror (UI overhaul Phase 4): the panel's Tunnels view reads
/// this registry, so its bookkeeping — open shows up, close removes, list is
/// newest-first — is pinned here. The streams themselves need live sockets
/// and are proven on the fleet, not in this sandbox.
/// </summary>
public class TunnelRegistryTests
{
    private static ActiveTunnelDto Dto(string id, string code = "ABCD-1234") => new()
    {
        StreamId = id,
        Code = code,
        DeviceName = "test device",
        Protocol = "tcp",
        Target = "127.0.0.1:3389",
        Signer = "operator",
        OpenedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void Open_ThenClose_TracksLiveTunnelsOnly()
    {
        var store = new ActiveTunnelStore();
        Assert.Empty(store.List());
        store.Open(Dto("s1"));
        store.Open(Dto("s2", "EFGH-5678"));
        Assert.Equal(2, store.List().Count);
        store.Close("s1");
        var left = Assert.Single(store.List());
        Assert.Equal("s2", left.StreamId);
        store.Close("missing"); // closing an unknown id is a no-op, never a throw
        Assert.Single(store.List());
    }

    [Fact]
    public void Open_SameStreamId_ReplacesNotDuplicates()
    {
        var store = new ActiveTunnelStore();
        store.Open(Dto("s1"));
        var again = Dto("s1");
        again.Target = "127.0.0.1:22";
        store.Open(again);
        var only = Assert.Single(store.List());
        Assert.Equal("127.0.0.1:22", only.Target);
    }
}
