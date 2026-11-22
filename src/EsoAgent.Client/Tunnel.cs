using EsoAgent.Core;

namespace EsoAgent.Client;

/// <summary>
/// TCP/UDP port forwarding (v1.2), SSH-style:
///   eso tunnel &lt;localPort&gt; &lt;device&gt; &lt;remoteHost:remotePort&gt; [--udp]
///   eso tunnel --socks &lt;localPort&gt; &lt;device&gt;
/// The device is a fleet CODE (relayed stream — the device verified the
/// operator's stream signature) or an IP (direct /api/tunnel WebSocket).
/// Every connection is audit-logged by the agent. Ctrl+C shuts everything
/// down cleanly. The plumbing lives in <see cref="TunnelForwarder"/> (the
/// App's Tunnels view runs the same code); this class is the console shell.
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

        var spec = new TunnelSpec
        {
            LocalPort = localPort,
            Device = device,
            Target = fixedTarget,
            Socks = socks,
            Udp = udp && !socks,
        };
        using var forwarder = new TunnelForwarder(cfg, spec, msg => Console.Error.WriteLine(msg));
        if (!await forwarder.StartAsync())
        {
            Console.Error.WriteLine($"error: could not start the tunnel: {forwarder.Error}");
            return 1;
        }
        Console.WriteLine(spec.Socks
            ? $"SOCKS5 proxy on 127.0.0.1:{localPort} via {device}. Ctrl+C to stop."
            : $"Forwarding 127.0.0.1:{localPort} -> {fixedTarget!.Host}:{fixedTarget.Port} via {device}. Ctrl+C to stop.");

        var done = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };
        await done.Task;
        forwarder.Stop();
        Console.WriteLine("Tunnel closed.");
        return 0;
    }
}
