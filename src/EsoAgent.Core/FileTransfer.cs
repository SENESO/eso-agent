using System.Security.Cryptography;

namespace EsoAgent.Core;

/// <summary>
/// Chunked file transfer with SHA-256 verification and resume.
/// Uploads land in "&lt;path&gt;.esopart" next to the target; a chunk is accepted only
/// at the exact current length (offset), so an interrupted upload resumes cleanly.
/// Commit verifies the whole-file hash before atomically moving the part into place.
/// The offset/hash math here is pure and unit-tested.
/// </summary>
public sealed class FileTransferService
{
    public static string PartPath(string targetPath) => targetPath + ".esopart";

    public long BeginUpload(string targetPath)
    {
        var part = PartPath(targetPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetPath))!);
        if (!File.Exists(part)) File.WriteAllBytes(part, Array.Empty<byte>());
        return new FileInfo(part).Length;
    }

    /// <summary>Appends a chunk. Returns the new received length. Throws on offset mismatch.</summary>
    public long AppendChunk(string targetPath, long offset, ReadOnlySpan<byte> data)
    {
        var part = PartPath(targetPath);
        var current = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (offset != current)
            throw new ChunkOffsetException(current, $"Chunk offset {offset} does not match received {current}; resume from {current}.");
        using var fs = new FileStream(part, FileMode.Append, FileAccess.Write, FileShare.None);
        fs.Write(data);
        return current + data.Length;
    }

    /// <summary>Verifies the part file's SHA-256 and moves it over the target. Returns the verified hash.</summary>
    public string CommitUpload(string targetPath, string expectedSha256Hex)
    {
        var part = PartPath(targetPath);
        if (!File.Exists(part)) throw new FileNotFoundException("No partial upload exists for this path.", part);
        var actual = HashFile(part);
        if (!string.Equals(actual, expectedSha256Hex.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(part);
            throw new InvalidDataException($"SHA-256 mismatch: expected {expectedSha256Hex}, got {actual}. Partial file discarded.");
        }
        File.Move(part, targetPath, overwrite: true);
        return actual;
    }

    public FileInfoResponse Info(string path)
    {
        if (!File.Exists(path)) return new FileInfoResponse { Exists = false };
        var info = new FileInfo(path);
        return new FileInfoResponse { Exists = true, Size = info.Length, Sha256 = HashFile(path) };
    }

    public static string HashFile(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    public static string HashBytes(ReadOnlySpan<byte> data) => HmacAuth.Sha256Hex(data);

    /// <summary>Plans download chunks for a file of `size` read in `chunkSize` steps from `startOffset`.</summary>
    public static IReadOnlyList<(long Offset, int Length)> PlanChunks(long size, long startOffset, int chunkSize)
    {
        if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));
        var plan = new List<(long, int)>();
        for (long off = startOffset; off < size; off += chunkSize)
            plan.Add((off, (int)Math.Min(chunkSize, size - off)));
        return plan;
    }
}

public sealed class ChunkOffsetException : Exception
{
    public long ExpectedOffset { get; }
    public ChunkOffsetException(long expectedOffset, string message) : base(message) => ExpectedOffset = expectedOffset;
}
