using System.IO.Compression;

namespace EsoAgent.Core;

/// <summary>One raw captured frame: BGRA32, tightly packed (stride = width*4).</summary>
public sealed class RawFrame
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] Bgra { get; init; }
}

/// <summary>A source of raw frames (a real capture device in hosts; synthetic in tests).</summary>
public interface IDesktopCaptureSource
{
    IReadOnlyList<MonitorInfo> Monitors { get; }
    RawFrame Capture(int monitorIndex);
}

/// <summary>
/// Deterministic synthetic capture source (test pattern: gradient + a moving box).
/// Used by unit tests and pipeline verification ONLY — never presented as a real desktop.
/// </summary>
public sealed class SyntheticCaptureSource : IDesktopCaptureSource
{
    private int _tick;
    public IReadOnlyList<MonitorInfo> Monitors { get; } = new[]
    {
        new MonitorInfo { Index = 0, Name = "Synthetic", Width = 128, Height = 96, Primary = true },
    };

    public RawFrame Capture(int monitorIndex)
    {
        var (w, h) = (128, 96);
        var data = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            data[i] = (byte)(x * 2); data[i + 1] = (byte)(y * 2); data[i + 2] = 128; data[i + 3] = 255;
        }
        // moving white box
        var bx = (_tick * 8) % (w - 20); var by = 20 + (_tick * 4) % 40;
        for (int y = by; y < by + 16 && y < h; y++)
        for (int x = bx; x < bx + 16 && x < w; x++)
        {
            var i = (y * w + x) * 4;
            data[i] = 255; data[i + 1] = 255; data[i + 2] = 255; data[i + 3] = 255;
        }
        _tick++;
        return new RawFrame { Width = w, Height = h, Bgra = data };
    }
}

/// <summary>
/// The "rawdiff" codec: frames are split into 64×64 tiles; only changed tiles are
/// sent (FNV-1a tile hashes), deflate-compressed. A keyframe (Full=true) carries
/// every tile. Pure managed code — the always-available baseline codec, and the
/// frame pipeline exercised end-to-end by the unit tests with a synthetic source.
/// Packet layout: 'R','D','1' | u16 width | u16 height | u16 tileSize | u32 tileCount
/// then deflate( per tile: u32 index | tileBytes ).
/// </summary>
public static class RawDiffCodec
{
    public const int TileSize = 64;

    public static ulong TileHash(RawFrame frame, int tileIndex)
    {
        var (tx, ty, tw, th) = TileRect(frame.Width, frame.Height, tileIndex);
        ulong hash = 14695981039346656037UL;
        for (int y = 0; y < th; y++)
        {
            var rowStart = ((ty + y) * frame.Width + tx) * 4;
            for (int i = 0; i < tw * 4; i++)
            {
                hash ^= frame.Bgra[rowStart + i];
                hash *= 1099511628211UL;
            }
        }
        return hash;
    }

    public static int TileCount(int width, int height)
        => ((width + TileSize - 1) / TileSize) * ((height + TileSize - 1) / TileSize);

    public static (int X, int Y, int W, int H) TileRect(int width, int height, int tileIndex)
    {
        var cols = (width + TileSize - 1) / TileSize;
        var tx = (tileIndex % cols) * TileSize;
        var ty = (tileIndex / cols) * TileSize;
        return (tx, ty, Math.Min(TileSize, width - tx), Math.Min(TileSize, height - ty));
    }

    /// <summary>Hashes of every tile in a frame (for diffing the next one).</summary>
    public static ulong[] TileHashes(RawFrame frame)
    {
        var count = TileCount(frame.Width, frame.Height);
        var hashes = new ulong[count];
        for (int i = 0; i < count; i++) hashes[i] = TileHash(frame, i);
        return hashes;
    }

    /// <summary>Indices of tiles that differ between two hash sets (null previous = all).</summary>
    public static IReadOnlyList<int> ChangedTiles(ulong[]? previous, ulong[] current)
    {
        if (previous == null || previous.Length != current.Length)
            return Enumerable.Range(0, current.Length).ToList();
        var changed = new List<int>();
        for (int i = 0; i < current.Length; i++)
            if (previous[i] != current[i]) changed.Add(i);
        return changed;
    }

    public static byte[] Encode(RawFrame frame, IReadOnlyList<int> tiles)
    {
        using var payload = new MemoryStream();
        foreach (var index in tiles)
        {
            var (tx, ty, tw, th) = TileRect(frame.Width, frame.Height, index);
            payload.Write(BitConverter.GetBytes(index));
            for (int y = 0; y < th; y++)
            {
                var rowStart = ((ty + y) * frame.Width + tx) * 4;
                payload.Write(frame.Bgra, rowStart, tw * 4);
            }
        }
        using var output = new MemoryStream();
        output.Write(new byte[] { (byte)'R', (byte)'D', (byte)'1' });
        output.Write(BitConverter.GetBytes((ushort)frame.Width));
        output.Write(BitConverter.GetBytes((ushort)frame.Height));
        output.Write(BitConverter.GetBytes((ushort)TileSize));
        output.Write(BitConverter.GetBytes(tiles.Count));
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
            deflate.Write(payload.ToArray());
        return output.ToArray();
    }

    /// <summary>Applies a packet onto the previous frame (or a blank frame for keyframes) and returns the new frame.</summary>
    public static RawFrame Decode(byte[] packet, RawFrame? previous)
    {
        using var input = new MemoryStream(packet);
        var header = new byte[3 + 2 + 2 + 2 + 4];
        input.ReadExactly(header);
        if (header[0] != 'R' || header[1] != 'D' || header[2] != '1')
            throw new InvalidDataException("Bad rawdiff packet magic.");
        var width = BitConverter.ToUInt16(header, 3);
        var height = BitConverter.ToUInt16(header, 5);
        var tileCount = BitConverter.ToUInt32(header, 9);

        byte[] basePixels = previous != null && previous.Width == width && previous.Height == height
            ? (byte[])previous.Bgra.Clone()
            : new byte[width * height * 4];

        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var payload = new MemoryStream();
        deflate.CopyTo(payload);
        var data = payload.ToArray();
        var pos = 0;
        for (uint t = 0; t < tileCount; t++)
        {
            var index = BitConverter.ToInt32(data, pos); pos += 4;
            var (tx, ty, tw, th) = TileRect(width, height, index);
            for (int y = 0; y < th; y++)
            {
                var dest = ((ty + y) * width + tx) * 4;
                Array.Copy(data, pos, basePixels, dest, tw * 4);
                pos += tw * 4;
            }
        }
        return new RawFrame { Width = width, Height = height, Bgra = basePixels };
    }
}

/// <summary>
/// Stateful frame pump over a capture source for the rawdiff codec: emits a
/// keyframe first, then diffs; returns null when nothing changed (frame skip).
/// Hosts with a JPEG encoder do the same skip by hashing their raw pixels.
/// </summary>
public sealed class RawDiffPump
{
    private ulong[]? _previousHashes;
    private long _seq;

    public DesktopFrame? Next(IDesktopCaptureSource source, int monitorIndex)
    {
        var frame = source.Capture(monitorIndex);
        var hashes = RawDiffCodec.TileHashes(frame);
        var changed = RawDiffCodec.ChangedTiles(_previousHashes, hashes);
        if (changed.Count == 0) return null;
        var full = _previousHashes == null;
        var packet = RawDiffCodec.Encode(frame, changed);
        _previousHashes = hashes;
        return new DesktopFrame
        {
            Seq = ++_seq,
            Codec = "rawdiff",
            Full = full,
            Width = frame.Width,
            Height = frame.Height,
            Data = Convert.ToBase64String(packet),
        };
    }
}
