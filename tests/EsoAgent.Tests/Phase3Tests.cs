using System.Diagnostics;
using System.Text.Json;
using EsoAgent.Core;
using Xunit;

namespace EsoAgent.Tests;

// UI overhaul Phase 3 — the pure logic behind the session experience:
// desktop quality presets, the session stats tracker, the shell-session
// catch-up buffer, and the client transfer engine + queue. The transfer
// tests drive the real engine through an in-process channel backed by
// the real FileTransferService (the same service the agent endpoints
// wrap), so the begin → chunk → commit / info → ranged-download wire
// sequence is exercised end to end; only the network itself is fake.

public class Phase3PresetTests
{
    [Fact]
    public void Presets_MapToTheDocumentedValues()
    {
        Assert.Equal((60, 30), DesktopPresets.For(DesktopQualityPreset.Auto));
        Assert.Equal((60, 20), DesktopPresets.For(DesktopQualityPreset.Balanced));
        Assert.Equal((40, 30), DesktopPresets.For(DesktopQualityPreset.Speed));
        Assert.Equal((88, 15), DesktopPresets.For(DesktopQualityPreset.Quality));
    }

    [Fact]
    public void Presets_StayInsideTheRangesAgentsEnforce()
    {
        foreach (var preset in Enum.GetValues<DesktopQualityPreset>())
        {
            var (quality, fps) = DesktopPresets.For(preset);
            Assert.InRange(quality, 10, 95);
            Assert.InRange(fps, 1, DesktopPresets.MaxFpsCeiling);
        }
        // The default is the adaptive preset at the full protocol ceiling —
        // the old app-side hard cap of 12 fps is gone from the default path.
        Assert.Equal(DesktopQualityPreset.Auto, DesktopPresets.Default);
        Assert.Equal(DesktopPresets.MaxFpsCeiling, DesktopPresets.For(DesktopPresets.Default).MaxFps);
    }

    [Fact]
    public void Presets_ParseRoundTrips()
    {
        Assert.True(DesktopPresets.TryParse("balanced", out var p));
        Assert.Equal(DesktopQualityPreset.Balanced, p);
        Assert.False(DesktopPresets.TryParse("ludicrous", out _));
    }
}

public class Phase3StatsTests
{
    [Fact]
    public void Tracker_ComputesWindowedRates()
    {
        var tracker = new DesktopStatsTracker();
        // 21 frames at exact 50 ms intervals = a steady 20 fps, 50 KB each.
        for (var i = 0; i <= 20; i++)
            tracker.AddFrame(50_000, 1920, 1080, "jpeg", nowMs: i * 50);
        var (fps, bps, avg) = tracker.Snapshot(nowMs: 1000);
        Assert.Equal(20.0, fps, precision: 1);
        Assert.Equal(21 * 50_000.0, bps, precision: 0);
        Assert.Equal(50_000.0, avg, precision: 0);
        Assert.Equal(21, tracker.Frames);
        Assert.Equal(1920, tracker.Width);
        Assert.Equal("jpeg", tracker.Codec);
    }

    [Fact]
    public void Tracker_SingleFrameReportsNoRate_AndOldFramesFallOutOfTheWindow()
    {
        var tracker = new DesktopStatsTracker();
        tracker.AddFrame(10_000, 800, 600, "jpeg", nowMs: 0);
        Assert.Equal(0.0, tracker.Snapshot(nowMs: 0).Fps);
        // Frames long past the 2 s window no longer count toward the rate.
        for (var i = 0; i <= 10; i++)
            tracker.AddFrame(10_000, 800, 600, "jpeg", nowMs: 5000 + i * 100);
        var (fps, _, _) = tracker.Snapshot(nowMs: 6000);
        Assert.Equal(10.0, fps, precision: 1);
    }
}

public class Phase3ShellBufferTests
{
    private static ShellSessionChunk Chunk(long seq, string text) =>
        new() { Seq = seq, Stream = "stdout", Text = text };

    [Fact]
    public void Buffer_AppendsInOrder_AndDedupesReattachOverlap()
    {
        var buffer = new ShellSessionBuffer();
        Assert.Equal(3, buffer.Append(new[] { Chunk(1, "a"), Chunk(2, "b"), Chunk(3, "c") }));
        Assert.Equal("abc", buffer.Text);
        // Reattach replays from an older seq: only the genuinely new chunk lands.
        Assert.Equal(1, buffer.Append(new[] { Chunk(2, "b"), Chunk(3, "c"), Chunk(4, "d") }));
        Assert.Equal("abcd", buffer.Text);
        Assert.Equal(4, buffer.LastSeq);
    }

    [Fact]
    public void Buffer_SortsAnOutOfOrderBatch_AndAcceptsGaps()
    {
        var buffer = new ShellSessionBuffer();
        Assert.Equal(3, buffer.Append(new[] { Chunk(3, "c"), Chunk(1, "a"), Chunk(2, "b") }));
        Assert.Equal("abc", buffer.Text);
        // A gap means the agent evicted old chunks; the text simply continues.
        Assert.Equal(1, buffer.Append(new[] { Chunk(9, "z") }));
        Assert.Equal("abcz", buffer.Text);
        Assert.Equal(9, buffer.LastSeq);
    }

    [Fact]
    public void Buffer_TrimsFromTheFrontOnALineBoundary()
    {
        var buffer = new ShellSessionBuffer(maxChars: 20);
        buffer.Append(new[] { Chunk(1, "line-one\nline-two\nline-three\n") });
        Assert.Equal("line-three\n", buffer.Text);   // trimmed on a line boundary, never mid-line
        Assert.Equal(1, buffer.LastSeq);
    }
}

/// <summary>An ITransferChannel that serves the engine's calls from a real
/// FileTransferService rooted at a temp directory — the same service the
/// agent's /api/files endpoints wrap, minus HTTP.</summary>
internal sealed class FakeTransferChannel : ITransferChannel
{
    private readonly string _root;
    private readonly FileTransferService _svc = new();

    public FakeTransferChannel(string root, bool isFleet = false)
    {
        _root = root;
        IsFleet = isFleet;
    }

    public bool IsFleet { get; }

    /// <summary>Test hook fired after each stored chunk (1-based count).</summary>
    public Action<int>? OnChunk { get; set; }
    private int _chunks;

    public void Dispose() { }

    private string Full(string remotePath) => Path.Combine(_root, remotePath);

    private static TransferReply Json<T>(T dto) =>
        new(true, JsonSerializer.SerializeToUtf8Bytes(dto, EsoJson.Options), null);

    private static (string Path, long Offset, long Length) ParseQuery(string url)
    {
        var q = url[(url.IndexOf('?') + 1)..].Split('&')
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        return (q["path"],
            q.TryGetValue("offset", out var o) ? long.Parse(o) : 0,
            q.TryGetValue("length", out var l) ? long.Parse(l) : -1);
    }

    public Task<TransferReply> SendAsync(string method, string path, object? body, CancellationToken ct)
    {
        try
        {
            if (path == "/api/files/begin")
            {
                var req = (BeginUploadRequest)body!;
                return Task.FromResult(Json(new BeginUploadResponse { Received = _svc.BeginUpload(Full(req.Path)) }));
            }
            if (path == "/api/files/commit")
            {
                var req = (CommitUploadRequest)body!;
                var hash = _svc.CommitUpload(Full(req.Path), req.Sha256);
                return Task.FromResult(Json(new FileInfoResponse
                { Exists = true, Size = new FileInfo(Full(req.Path)).Length, Sha256 = hash }));
            }
            if (path.StartsWith("/api/files/info", StringComparison.Ordinal))
            {
                var (remote, _, _) = ParseQuery(path);
                return Task.FromResult(Json(_svc.Info(Full(remote))));
            }
            return Task.FromResult(TransferReply.Fail($"Unexpected call: {method} {path}"));
        }
        catch (Exception ex) { return Task.FromResult(TransferReply.Fail(ex.Message)); }
    }

    public Task<TransferReply> SendRawAsync(string method, string path, byte[] body, CancellationToken ct)
    {
        try
        {
            if (path.StartsWith("/api/files/chunk", StringComparison.Ordinal))
            {
                var (remote, offset, _) = ParseQuery(path);
                var received = _svc.AppendChunk(Full(remote), offset, body);
                OnChunk?.Invoke(++_chunks);
                return Task.FromResult(Json(new ChunkResponse { Received = received }));
            }
            if (path.StartsWith("/api/files/download", StringComparison.Ordinal))
            {
                var (remote, offset, length) = ParseQuery(path);
                var full = Full(remote);
                var total = new FileInfo(full).Length;
                var want = length < 0 ? total - offset : Math.Min(length, total - offset);
                var bytes = new byte[want];
                using var fs = File.OpenRead(full);
                fs.Seek(offset, SeekOrigin.Begin);
                fs.ReadExactly(bytes);
                return Task.FromResult(new TransferReply(true, bytes, null));
            }
            return Task.FromResult(TransferReply.Fail($"Unexpected call: {method} {path}"));
        }
        catch (Exception ex) { return Task.FromResult(TransferReply.Fail(ex.Message)); }
    }
}

public class Phase3TransferTests
{
    private static byte[] Pattern(int size)
    {
        var data = new byte[size];
        for (int i = 0; i < size; i++) data[i] = (byte)(i % 251);
        return data;
    }

    private static async Task WaitForAsync(Func<bool> done, string what, int timeoutMs = 60_000)
    {
        var sw = Stopwatch.StartNew();
        while (!done())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) Assert.Fail($"Timed out waiting for {what}.");
            await Task.Delay(25);
        }
    }

    private static bool Terminal(TransferJob j) =>
        j.State is TransferState.Completed or TransferState.Failed or TransferState.Cancelled;

    [Fact]
    public async Task Queue_Upload100MB_ProgressMonotonic_AndHashVerifiedCommit()
    {
        var dir = Directory.CreateTempSubdirectory("eso-p3-up-");
        var remoteDir = Directory.CreateTempSubdirectory("eso-p3-remote-");
        try
        {
            var local = Path.Combine(dir.FullName, "local.bin");
            var data = Pattern(100_000_000);
            await File.WriteAllBytesAsync(local, data);

            using var queue = new TransferQueue();
            var progress = new List<long>();
            var gate = new object();
            TransferJob? mine = null;
            queue.JobChanged += j =>
            {
                if (mine != null && j.Id == mine.Id)
                    lock (gate) progress.Add(j.BytesDone);
            };
            mine = queue.Enqueue(TransferDirection.Upload, "big.bin", local, "big.bin",
                () => new FakeTransferChannel(remoteDir.FullName));
            await WaitForAsync(() => Terminal(mine), "100 MB upload");
            Assert.Equal(TransferState.Completed, mine.State);
            Assert.Equal(data.Length, mine.TotalBytes);
            Assert.Equal(data.Length, mine.BytesDone);
            lock (gate)
            {
                Assert.NotEmpty(progress);
                for (var i = 1; i < progress.Count; i++)
                    Assert.True(progress[i] >= progress[i - 1], "progress must never go backwards");
                Assert.Equal(data.Length, progress[^1]);
            }
            var committed = Path.Combine(remoteDir.FullName, "big.bin");
            Assert.True(File.Exists(committed));
            Assert.Equal(FileTransferService.HashBytes(data), FileTransferService.HashFile(committed));
        }
        finally { dir.Delete(recursive: true); remoteDir.Delete(recursive: true); }
    }

    [Fact]
    public async Task Queue_UploadThenDownload_RoundTripsBytes()
    {
        var dir = Directory.CreateTempSubdirectory("eso-p3-rt-");
        var remoteDir = Directory.CreateTempSubdirectory("eso-p3-remote-");
        try
        {
            var data = Pattern(3_000_000);
            var local = Path.Combine(dir.FullName, "a.bin");
            await File.WriteAllBytesAsync(local, data);
            using var queue = new TransferQueue();
            var up = queue.Enqueue(TransferDirection.Upload, "a.bin", local, "a.bin",
                () => new FakeTransferChannel(remoteDir.FullName));
            await WaitForAsync(() => Terminal(up), "upload");
            Assert.Equal(TransferState.Completed, up.State);
            Assert.Equal(data.Length, up.BytesDone);

            var back = Path.Combine(dir.FullName, "back.bin");
            var down = queue.Enqueue(TransferDirection.Download, "a.bin", back, "a.bin",
                () => new FakeTransferChannel(remoteDir.FullName));
            await WaitForAsync(() => Terminal(down), "download");
            Assert.True(down.State == TransferState.Completed, $"download failed: {down.Error}");
            Assert.Equal(data, await File.ReadAllBytesAsync(back));
        }
        finally { dir.Delete(recursive: true); remoteDir.Delete(recursive: true); }
    }

    [Fact]
    public async Task Queue_CancelMidUpload_StopsAtChunkBoundary_AndNeverCommits()
    {
        var dir = Directory.CreateTempSubdirectory("eso-p3-cancel-");
        var remoteDir = Directory.CreateTempSubdirectory("eso-p3-remote-");
        try
        {
            var local = Path.Combine(dir.FullName, "big.bin");
            await File.WriteAllBytesAsync(local, Pattern(20_000_000));   // 5 direct chunks of 4 MB
            var secondChunkStored = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();
            using var queue = new TransferQueue();
            var job = queue.Enqueue(TransferDirection.Upload, "big.bin", local, "big.bin", () =>
                new FakeTransferChannel(remoteDir.FullName)
                {
                    OnChunk = n =>
                    {
                        if (n != 2) return;
                        secondChunkStored.Set();
                        // Hold the transfer open with chunk 2 already stored:
                        // the cancel below must land at the NEXT boundary.
                        release.Wait(TimeSpan.FromSeconds(30));
                    },
                });
            Assert.True(secondChunkStored.Wait(TimeSpan.FromSeconds(30)), "second chunk never landed");
            job.Cancel();
            release.Set();
            await WaitForAsync(() => Terminal(job), "cancelled terminal state");
            Assert.Equal(TransferState.Cancelled, job.State);
            var target = Path.Combine(remoteDir.FullName, "big.bin");
            Assert.False(File.Exists(target));                                // never committed
            var part = FileTransferService.PartPath(target);
            Assert.True(File.Exists(part));                                   // partial kept, resumable
            Assert.Equal(2L * 4 * 1024 * 1024, new FileInfo(part).Length);    // exactly the stored chunks
        }
        finally { dir.Delete(recursive: true); remoteDir.Delete(recursive: true); }
    }

    [Fact]
    public async Task Queue_RunsJobsSequentiallyInOrder()
    {
        var dir = Directory.CreateTempSubdirectory("eso-p3-seq-");
        var remoteDir = Directory.CreateTempSubdirectory("eso-p3-remote-");
        try
        {
            var local = Path.Combine(dir.FullName, "s.bin");
            await File.WriteAllBytesAsync(local, Pattern(100_000));
            var beginOrder = new List<string>();
            var gate = new object();
            Func<ITransferChannel> Factory(string tag) => () =>
                new OrderRecordingChannel(new FakeTransferChannel(remoteDir.FullName),
                    () => { lock (gate) beginOrder.Add(tag); });
            using var queue = new TransferQueue();
            var first = queue.Enqueue(TransferDirection.Upload, "1.bin", local, "1.bin", Factory("first"));
            var second = queue.Enqueue(TransferDirection.Upload, "2.bin", local, "2.bin", Factory("second"));
            await WaitForAsync(() => Terminal(first) && Terminal(second), "both uploads");
            Assert.Equal(TransferState.Completed, first.State);
            Assert.Equal(TransferState.Completed, second.State);
            Assert.Equal(new[] { "first", "second" }, beginOrder.ToArray());
            Assert.True(first.FinishedAt <= second.StartedAt);
        }
        finally { dir.Delete(recursive: true); remoteDir.Delete(recursive: true); }
    }

    [Fact]
    public async Task Queue_Upload_ResumesFromTheServerSidePartial()
    {
        var dir = Directory.CreateTempSubdirectory("eso-p3-resume-");
        var remoteDir = Directory.CreateTempSubdirectory("eso-p3-remote-");
        try
        {
            var data = Pattern(10_000_000);
            var local = Path.Combine(dir.FullName, "r.bin");
            await File.WriteAllBytesAsync(local, data);
            // Pre-seed the device with the first 4 MB, as an interrupted upload would.
            var svc = new FileTransferService();
            var target = Path.Combine(remoteDir.FullName, "r.bin");
            svc.BeginUpload(target);
            Assert.Equal(4_000_000, svc.AppendChunk(target, 0, data.AsSpan(0, 4_000_000)));

            using var queue = new TransferQueue();
            var progress = new List<long>();
            var gate = new object();
            TransferJob? mine = null;
            queue.JobChanged += j =>
            {
                if (mine != null && j.Id == mine.Id)
                    lock (gate) progress.Add(j.BytesDone);
            };
            mine = queue.Enqueue(TransferDirection.Upload, "r.bin", local, "r.bin",
                () => new FakeTransferChannel(remoteDir.FullName));
            await WaitForAsync(() => Terminal(mine), "resumed upload");
            Assert.Equal(TransferState.Completed, mine.State);
            lock (gate)
            {
                // First report is the begin/resume point: the engine's counters
                // start at the 4 MB the device already held, not at zero.
                Assert.Contains(4_000_000L, progress);
                Assert.Equal(data.Length, progress[^1]);
            }
            Assert.Equal(FileTransferService.HashBytes(data), FileTransferService.HashFile(target));
        }
        finally { dir.Delete(recursive: true); remoteDir.Delete(recursive: true); }
    }

    /// <summary>Wraps a channel and reports when an upload begins (its first call).</summary>
    private sealed class OrderRecordingChannel : ITransferChannel
    {
        private readonly ITransferChannel _inner;
        private readonly Action _onBegin;
        private bool _fired;

        public OrderRecordingChannel(ITransferChannel inner, Action onBegin)
        {
            _inner = inner;
            _onBegin = onBegin;
        }

        public bool IsFleet => _inner.IsFleet;

        public Task<TransferReply> SendAsync(string method, string path, object? body, CancellationToken ct)
        {
            if (!_fired && path == "/api/files/begin") { _fired = true; _onBegin(); }
            return _inner.SendAsync(method, path, body, ct);
        }

        public Task<TransferReply> SendRawAsync(string method, string path, byte[] body, CancellationToken ct) =>
            _inner.SendRawAsync(method, path, body, ct);

        public void Dispose() => _inner.Dispose();
    }
}
