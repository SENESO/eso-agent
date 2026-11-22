using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace EsoAgent.Core;

/// <summary>
/// The P2P layer's transport seam. Production is TCP (the lab's measured
/// design); the seam exists so the full stack — DHT, sessions, relay —
/// can also run over the WireGuard mesh tunnel later, and so tests can
/// run the real stack over paired in-memory streams on hosts where
/// loopback TCP is unavailable or intercepted.
/// </summary>
public interface IP2pTransport
{
    /// <summary>Opens one connection. Throws (SocketException/IOException) when the peer cannot be reached.</summary>
    Task<Stream> DialAsync(string host, int port, CancellationToken ct);
    /// <summary>Starts listening. host is advisory for TCP (binds all interfaces);
    /// for the in-memory transport it is part of the address key.</summary>
    IP2pListener Listen(string host, int port);
}

public interface IP2pListener : IAsyncDisposable
{
    /// <summary>Accepts one connection; the string is a remote-endpoint label for logs.</summary>
    Task<(Stream Stream, string Remote)> AcceptAsync(CancellationToken ct);
}

/// <summary>TCP transport: dials the advertised host:port, listens on all interfaces.</summary>
public sealed class TcpP2pTransport : IP2pTransport
{
    public static readonly TcpP2pTransport Instance = new();

    public async Task<Stream> DialAsync(string host, int port, CancellationToken ct)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(host, port, ct);
            return client.GetStream();
        }
        catch { client.Dispose(); throw; }
    }

    public IP2pListener Listen(string host, int port)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        return new TcpListenerAdapter(listener);
    }

    private sealed class TcpListenerAdapter : IP2pListener
    {
        private readonly TcpListener _listener;
        public TcpListenerAdapter(TcpListener listener) => _listener = listener;

        public async Task<(Stream Stream, string Remote)> AcceptAsync(CancellationToken ct)
        {
            var client = await _listener.AcceptTcpClientAsync(ct);
            client.NoDelay = true;
            return (client.GetStream(), client.Client.RemoteEndPoint?.ToString() ?? "?");
        }

        public ValueTask DisposeAsync()
        {
            try { _listener.Stop(); } catch { }
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// In-memory transport: listeners and dialers that share one instance
/// form a closed world addressed by host:port. Used by the test suite
/// (and the product-run harness) to run the real P2P stack without
/// sockets. Not shipped in any agent path — the agent always uses TCP.
/// </summary>
public sealed class InMemoryP2pTransport : IP2pTransport
{
    private readonly Dictionary<string, InMemListener> _listeners = new();
    private readonly object _lock = new();

    private static string Key(string host, int port) => host + ":" + port;

    public Task<Stream> DialAsync(string host, int port, CancellationToken ct)
    {
        InMemListener? listener;
        lock (_lock) _listeners.TryGetValue(Key(host, port), out listener);
        if (listener == null)
            throw new SocketException((int)SocketError.ConnectionRefused);
        var (mine, theirs) = QueuedStream.Pair();
        listener.Enqueue(theirs, "mem:" + Key(host, port));
        return Task.FromResult<Stream>(mine);
    }

    public IP2pListener Listen(string host, int port)
    {
        var listener = new InMemListener(this, Key(host, port));
        lock (_lock)
        {
            if (_listeners.ContainsKey(listener.Key))
                throw new SocketException((int)SocketError.AddressAlreadyInUse);
            _listeners[listener.Key] = listener;
        }
        return listener;
    }

    private void Unregister(string key)
    {
        lock (_lock) _listeners.Remove(key);
    }

    private sealed class InMemListener : IP2pListener
    {
        private readonly InMemoryP2pTransport _owner;
        private readonly Channel<(Stream, string)> _pending = Channel.CreateUnbounded<(Stream, string)>();
        public string Key { get; }
        public InMemListener(InMemoryP2pTransport owner, string key) { _owner = owner; Key = key; }

        public void Enqueue(Stream s, string remote) => _pending.Writer.TryWrite((s, remote));

        public async Task<(Stream Stream, string Remote)> AcceptAsync(CancellationToken ct)
            => await _pending.Reader.ReadAsync(ct);

        public ValueTask DisposeAsync()
        {
            _owner.Unregister(Key);
            _pending.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>One end of an in-memory full-duplex byte stream.</summary>
    private sealed class QueuedStream : Stream
    {
        private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>();
        private QueuedStream _peer = null!;
        private byte[]? _current;
        private int _offset;
        private volatile bool _disposed;

        public static (QueuedStream, QueuedStream) Pair()
        {
            var a = new QueuedStream();
            var b = new QueuedStream();
            a._peer = b;
            b._peer = a;
            return (a, b);
        }

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (buffer.Length == 0) return 0;
            while (_current == null)
            {
                if (!await _inbound.Reader.WaitToReadAsync(ct)) return 0; // peer closed + drained
                if (!_inbound.Reader.TryRead(out _current)) return 0;
                _offset = 0;
            }
            var n = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, n).CopyTo(buffer);
            _offset += n;
            if (_offset >= _current.Length) _current = null;
            return n;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (_disposed) throw new IOException("connection closed");
            var copy = buffer.ToArray();
            try { await _peer._inbound.Writer.WriteAsync(copy, ct); }
            catch (ChannelClosedException) { throw new IOException("connection closed by peer"); }
        }

        protected override void Dispose(bool disposing)
        {
            _disposed = true;
            // Signal EOF to the peer once it has drained what we already sent.
            _peer._inbound.Writer.TryComplete();
            base.Dispose(disposing);
        }
    }
}
