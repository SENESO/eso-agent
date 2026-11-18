using EsoAgent.Core;
using Xunit;

namespace EsoAgent.Tests;

public class FileTransferTests
{
    private static byte[] Pattern(int size)
    {
        var data = new byte[size];
        for (int i = 0; i < size; i++) data[i] = (byte)(i % 251);
        return data;
    }

    [Fact]
    public void ChunkedUpload_ResumeAndCommit_VerifiesHash()
    {
        var dir = Directory.CreateTempSubdirectory("eso-test-");
        try
        {
            var target = Path.Combine(dir.FullName, "file.bin");
            var data = Pattern(100_000);
            var sha = FileTransferService.HashBytes(data);
            var svc = new FileTransferService();

            Assert.Equal(0, svc.BeginUpload(target));
            Assert.Equal(40_000, svc.AppendChunk(target, 0, data.AsSpan(0, 40_000)));

            // Simulated interruption + resume: begin again reports what the server holds.
            Assert.Equal(40_000, svc.BeginUpload(target));
            Assert.Equal(100_000, svc.AppendChunk(target, 40_000, data.AsSpan(40_000)));

            var committed = svc.CommitUpload(target, sha);
            Assert.Equal(sha, committed);
            Assert.Equal(data, File.ReadAllBytes(target));
            Assert.False(File.Exists(FileTransferService.PartPath(target)));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void AppendChunk_WrongOffset_ThrowsWithExpectedOffset()
    {
        var dir = Directory.CreateTempSubdirectory("eso-test-");
        try
        {
            var target = Path.Combine(dir.FullName, "file.bin");
            var svc = new FileTransferService();
            svc.BeginUpload(target);
            var ex = Assert.Throws<ChunkOffsetException>(() => svc.AppendChunk(target, 500, new byte[10]));
            Assert.Equal(0, ex.ExpectedOffset);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void CommitUpload_WrongHash_ThrowsAndDiscardsPartial()
    {
        var dir = Directory.CreateTempSubdirectory("eso-test-");
        try
        {
            var target = Path.Combine(dir.FullName, "file.bin");
            var svc = new FileTransferService();
            svc.BeginUpload(target);
            svc.AppendChunk(target, 0, Pattern(100));
            Assert.Throws<InvalidDataException>(() => svc.CommitUpload(target, new string('0', 64)));
            Assert.False(File.Exists(target));
            // A hash mismatch means the partial is corrupt, not resumable — it is discarded.
            Assert.False(File.Exists(FileTransferService.PartPath(target)));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void PlanChunks_CoversExactlyFromOffset()
    {
        var plan = FileTransferService.PlanChunks(size: 1000, startOffset: 250, chunkSize: 300);
        Assert.Equal(new[] { (250L, 300), (550L, 300), (850L, 150) }, plan.ToArray());
        Assert.Empty(FileTransferService.PlanChunks(100, 100, 300));
        Assert.Equal(new[] { (0L, 10) }, FileTransferService.PlanChunks(10, 0, 300).ToArray());
    }

    [Fact]
    public void Info_ReportsExistenceSizeAndHash()
    {
        var dir = Directory.CreateTempSubdirectory("eso-test-");
        try
        {
            var target = Path.Combine(dir.FullName, "f.txt");
            File.WriteAllBytes(target, Pattern(1234));
            var svc = new FileTransferService();
            var info = svc.Info(target);
            Assert.True(info.Exists);
            Assert.Equal(1234, info.Size);
            Assert.Equal(FileTransferService.HashFile(target), info.Sha256);
            Assert.False(svc.Info(Path.Combine(dir.FullName, "nope")).Exists);
        }
        finally { dir.Delete(recursive: true); }
    }
}
