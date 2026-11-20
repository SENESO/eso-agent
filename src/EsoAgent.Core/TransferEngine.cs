using System.Text.Json;

namespace EsoAgent.Core;

// Client-side file transfer (UI overhaul Phase 3): the chunked resumable
// upload + ranged download the app used to inline in its Files view, lifted
// into one engine with per-chunk progress and cooperative cancellation, and
// written against a narrow channel seam. The app adapts its Transport to
// the seam; the unit tests drive the same engine through an in-process
// channel backed by the real FileTransferService, so the wire sequence
// (begin → chunks → SHA-256 commit / info → ranged download → verify) is
// exercised end to end without a network.
//
// Cancellation is cooperative and lands at a chunk boundary: an in-flight
// chunk request completes, the next one never starts. A cancelled upload
// leaves the partial on the device, where the protocol's begin/resume
// picks it up again; a cancelled download leaves the partial local file.
// Neither is ever committed or verified as complete.

/// <summary>One device reply as the transfer engine needs it (mirrors the
/// client's DeviceReply shape without depending on the client project).</summary>
public sealed record TransferReply(bool Ok, byte[] Body, string? Error)
{
    public T Json<T>() => JsonSerializer.Deserialize<T>(Body, EsoJson.Options)
                          ?? throw new InvalidOperationException("Empty response.");

    public static TransferReply Fail(string error) => new(false, Array.Empty<byte>(), error);
}

/// <summary>The wire as the transfer engine sees it. Implementations must
/// surface device errors as a reply with Ok = false, not throw, matching
/// the existing Transport behaviour.</summary>
public interface ITransferChannel : IDisposable
{
    bool IsFleet { get; }

    Task<TransferReply> SendAsync(string method, string path, object? body, CancellationToken ct);

    Task<TransferReply> SendRawAsync(string method, string path, byte[] body, CancellationToken ct);
}

public enum TransferDirection { Upload, Download }

public enum TransferState { Queued, Running, Completed, Failed, Cancelled }

/// <summary>One queued transfer. State and counters are mutated by the
/// queue's runner thread; readers (UI) subscribe to TransferQueue.JobChanged
/// and read a consistent snapshot after each raise.</summary>
public sealed class TransferJob
{
    private readonly CancellationTokenSource _cts = new();

    internal TransferJob(TransferDirection direction, string name, string localPath, string remotePath,
        Func<ITransferChannel> channelFactory)
    {
        Direction = direction;
        Name = name;
        LocalPath = localPath;
        RemotePath = remotePath;
        ChannelFactory = channelFactory;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public TransferDirection Direction { get; }
    public string Name { get; }
    public string LocalPath { get; }
    public string RemotePath { get; }
    public long TotalBytes { get; internal set; }
    public long BytesDone { get; internal set; }
    public TransferState State { get; internal set; } = TransferState.Queued;
    public string? Error { get; internal set; }
    public DateTimeOffset? StartedAt { get; internal set; }
    public DateTimeOffset? FinishedAt { get; internal set; }

    public double Fraction => TotalBytes > 0
        ? Math.Min(1.0, (double)BytesDone / TotalBytes)
        : State == TransferState.Completed ? 1.0 : 0.0;

    internal Func<ITransferChannel> ChannelFactory { get; }
    internal CancellationToken Token => _cts.Token;

    /// <summary>Ask the transfer to stop at the next chunk boundary. A
    /// queued job that is cancelled never starts.</summary>
    public void Cancel()
    {
        if (State is TransferState.Queued or TransferState.Running) _cts.Cancel();
    }
}

/// <summary>The chunk loops themselves — stateless, one job per call.</summary>
public static class TransferEngine
{
    /// <summary>Fleet-relayed chunks stay small (the relay carries them as
    /// command payloads); direct chunks are as large as the agent allows.</summary>
    public static int UploadChunkBytes(bool fleet) => fleet ? 256 * 1024 : 4 * 1024 * 1024;

    public const int DownloadChunkBytes = 1024 * 1024;

    private static void EnsureOk(TransferReply reply)
    {
        if (!reply.Ok) throw new InvalidOperationException(reply.Error ?? "The device returned an error.");
    }

    public static async Task UploadAsync(ITransferChannel channel, TransferJob job,
        Action<TransferJob>? progress, CancellationToken ct)
    {
        var size = new FileInfo(job.LocalPath).Length;
        job.TotalBytes = size;
        progress?.Invoke(job);

        var sha = FileTransferService.HashFile(job.LocalPath);
        var begin = await channel.SendAsync("POST", "/api/files/begin",
            new BeginUploadRequest { Path = job.RemotePath }, ct);
        EnsureOk(begin);
        var offset = begin.Json<BeginUploadResponse>().Received;
        if (offset < 0 || offset > size) offset = 0;   // a stale partial larger than the file restarts clean
        job.BytesDone = offset;
        progress?.Invoke(job);

        var chunkSize = UploadChunkBytes(channel.IsFleet);
        await using var fs = new FileStream(job.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[chunkSize];
        var current = offset;
        while (current < size)
        {
            ct.ThrowIfCancellationRequested();
            var want = (int)Math.Min(chunkSize, size - current);
            var read = await fs.ReadAsync(buffer.AsMemory(0, want), ct);
            if (read <= 0) throw new IOException("The local file shrank during upload.");
            var chunk = read == buffer.Length ? buffer : buffer[..read];
            var rs = await channel.SendRawAsync("PUT",
                $"/api/files/chunk?path={Uri.EscapeDataString(job.RemotePath)}&offset={current}", chunk, ct);
            EnsureOk(rs);
            current = rs.Json<ChunkResponse>().Received;
            job.BytesDone = current;
            progress?.Invoke(job);
        }

        ct.ThrowIfCancellationRequested();
        var commit = await channel.SendAsync("POST", "/api/files/commit",
            new CommitUploadRequest { Path = job.RemotePath, Sha256 = sha }, ct);
        EnsureOk(commit);
        job.BytesDone = size;
        progress?.Invoke(job);
    }

    public static async Task DownloadAsync(ITransferChannel channel, TransferJob job,
        Action<TransferJob>? progress, CancellationToken ct)
    {
        var infoReply = await channel.SendAsync("GET",
            $"/api/files/info?path={Uri.EscapeDataString(job.RemotePath)}", null, ct);
        EnsureOk(infoReply);
        var info = infoReply.Json<FileInfoResponse>();
        if (!info.Exists) throw new FileNotFoundException($"The device has no file at {job.RemotePath}.");
        job.TotalBytes = info.Size;
        progress?.Invoke(job);

        var dir = Path.GetDirectoryName(Path.GetFullPath(job.LocalPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        long offset = 0;
        await using (var outFs = new FileStream(job.LocalPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            while (offset < info.Size)
            {
                ct.ThrowIfCancellationRequested();
                var length = Math.Min(DownloadChunkBytes, info.Size - offset);
                var rs = await channel.SendRawAsync("GET",
                    $"/api/files/download?path={Uri.EscapeDataString(job.RemotePath)}&offset={offset}&length={length}",
                    Array.Empty<byte>(), ct);
                EnsureOk(rs);
                if (rs.Body.Length == 0) throw new IOException("Download stalled: the device returned no data.");
                await outFs.WriteAsync(rs.Body, ct);
                offset += rs.Body.Length;
                job.BytesDone = offset;
                progress?.Invoke(job);
            }
            await outFs.FlushAsync(ct);
        }

        // Verified only after the stream is closed — the file is exclusively
        // ours while writing, and the hash must read the finished bytes.
        if (info.Sha256 != null && !string.Equals(info.Sha256,
                FileTransferService.HashFile(job.LocalPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded file failed SHA-256 verification.");
    }
}

/// <summary>
/// A sequential transfer queue: one transfer at a time (a device link is
/// not helped by parallel chunk storms), in enqueue order. Thread-safe;
/// JobChanged fires on the runner thread — subscribers that touch UI must
/// marshal. Completed/failed/cancelled jobs stay listed until the owner
/// clears them, so the user can see what happened.
/// </summary>
public sealed class TransferQueue : IDisposable
{
    private readonly object _gate = new();
    private readonly List<TransferJob> _jobs = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _runner;

    public event Action<TransferJob>? JobChanged;

    public TransferQueue() => _runner = Task.Run(RunLoopAsync);

    public IReadOnlyList<TransferJob> Jobs
    {
        get { lock (_gate) return _jobs.ToArray(); }
    }

    public TransferJob Enqueue(TransferDirection direction, string name, string localPath, string remotePath,
        Func<ITransferChannel> channelFactory)
    {
        var job = new TransferJob(direction, name, localPath, remotePath, channelFactory);
        lock (_gate) _jobs.Add(job);
        JobChanged?.Invoke(job);
        _signal.Release();
        return job;
    }

    /// <summary>Drop finished jobs from the list (running/queued stay).</summary>
    public void ClearFinished()
    {
        lock (_gate)
            _jobs.RemoveAll(j => j.State is TransferState.Completed or TransferState.Failed or TransferState.Cancelled);
    }

    private async Task RunLoopAsync()
    {
        while (true)
        {
            TransferJob? next;
            lock (_gate) next = _jobs.FirstOrDefault(j => j.State == TransferState.Queued);
            if (next == null)
            {
                try { await _signal.WaitAsync(_stop.Token); }
                catch (OperationCanceledException) { return; }
                continue;
            }
            await RunJobAsync(next);
        }
    }

    private async Task RunJobAsync(TransferJob job)
    {
        if (job.Token.IsCancellationRequested)
        {
            job.State = TransferState.Cancelled;
            job.FinishedAt = DateTimeOffset.UtcNow;
            JobChanged?.Invoke(job);
            return;
        }
        job.State = TransferState.Running;
        job.StartedAt = DateTimeOffset.UtcNow;
        JobChanged?.Invoke(job);
        try
        {
            using var channel = job.ChannelFactory();
            if (job.Direction == TransferDirection.Upload)
                await TransferEngine.UploadAsync(channel, job, j => JobChanged?.Invoke(j), job.Token);
            else
                await TransferEngine.DownloadAsync(channel, job, j => JobChanged?.Invoke(j), job.Token);
            job.State = TransferState.Completed;
        }
        catch (OperationCanceledException) { job.State = TransferState.Cancelled; }
        catch (Exception ex) { job.State = TransferState.Failed; job.Error = ex.Message; }
        job.FinishedAt = DateTimeOffset.UtcNow;
        JobChanged?.Invoke(job);
    }

    public void Dispose() => _stop.Cancel();
}
