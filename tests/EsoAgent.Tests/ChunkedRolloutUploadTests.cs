using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EsoAgent.Client;
using EsoAgent.Core;
using EsoAgent.Server;
using Xunit;

namespace EsoAgent.Tests;

/// <summary>
/// Chunked rollout upload (v1.3.2, BUILD-REPORT §25 follow-up). The §25 defect:
/// `eso rollout upload` signed ONCE and then spent ~15 min in flight on a
/// ~76 KB/s link, so the operator signature was stale (120 s skew) by the time
/// the fleet server verified it — 401 "Operator signature invalid". The fix
/// signs EACH 256 KiB chunk at send time; the commit re-verifies the whole-file
/// hash before promoting. A live HTTP upload through Kestrel cannot run in
/// this sandbox (.NET loopback TCP is intercepted — same constraint as
/// PackageUploadLimitTests), so the wire is proven with a handler-backed
/// FleetApiClient and the store is proven directly; the live proof is the
/// 1.3.2 rollout on the box.
/// </summary>
public class ChunkedRolloutUploadTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "eso-chunked-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A real zip package of ~`sizeBytes` payload, stamped with `version`.</summary>
    private static string BuildPackage(string dir, string version, long sizeBytes)
    {
        var zipPath = Path.Combine(dir, "pkg.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("esoagent-version.txt");
            using (var w = new StreamWriter(entry.Open())) w.Write(version);
            var payload = zip.CreateEntry("payload.bin", CompressionLevel.NoCompression);
            using var ps = payload.Open();
            var chunk = new byte[1024 * 1024];
            uint state = 0x12345678;
            for (var i = 0; i < chunk.Length; i += 4)
            {
                state = state * 1664525 + 1013904223;
                BitConverter.GetBytes(state).CopyTo(chunk, i);
            }
            var remaining = sizeBytes;
            while (remaining > 0)
            {
                var n = (int)Math.Min(chunk.Length, remaining);
                ps.Write(chunk, 0, n);
                remaining -= n;
            }
        }
        return zipPath;
    }

    // StageVerifiedAsync never calls the runner (only ApplyStaged does).
    private sealed class StubProcessRunner : IProcessRunner
    {
        public Task<ExecResponse> RunAsync(ExecRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ExecResponse> RunStreamingAsync(ExecRequest request, Func<string, string, Task> onData, CancellationToken ct = default) => throw new NotImplementedException();
        public int LaunchDetached(ExecRequest request) => throw new NotImplementedException();
    }

    // ----- the §25 defect, pinned at the signature layer -----

    [Fact]
    public void SingleShotSignature_GoesStaleOnASlowLink_ChunkSignatureStaysFresh()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pub = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var body = new byte[64 * 1024]; // one chunk's worth of bytes

        // The old path: one signature at T0 for the whole package, verified
        // after a 15-minute flight (the §25 link: 69.9 MB at ~76 KB/s).
        var t0 = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var singleShot = OperatorSigning.SignRequest(key, "PUT", "/fleet/update/package", t0, body);
        Assert.False(OperatorSigning.VerifyRequest(pub, singleShot, "PUT", "/fleet/update/package", t0, body, t0 + 900),
            "a signature taken 900 s before verification must fail the 120 s skew window — this is the §25 401");

        // The new path: each chunk is signed at ITS send time. The last chunk
        // leaves at T0+900 and is verified a second later — fresh, accepted.
        var chunkSig = OperatorSigning.SignRequest(key, "PUT",
            "/fleet/update/package/chunk?sha256=abc&offset=12345&total=99999", t0 + 900, body);
        Assert.True(OperatorSigning.VerifyRequest(pub, chunkSig, "PUT",
            "/fleet/update/package/chunk?sha256=abc&offset=12345&total=99999", t0 + 900, body, t0 + 901),
            "a chunk signed at send time must verify: the skew window covers the chunk's flight, not the whole upload");
    }

    // ----- the store state machine -----

    [Fact]
    public void ChunkedUpload_CompletesAndPromotesIdenticalBytes()
    {
        var dir = NewTempDir();
        try
        {
            var store = new UpdatePackageStore(dir);
            var zip = BuildPackage(dir, "9.9.9", 5 * 1024 * 1024 + 12345);
            var sha = FileTransferService.HashFile(zip);
            var size = new FileInfo(zip).Length;
            var bytes = File.ReadAllBytes(zip);

            long received = 0;
            foreach (var (offset, length) in FileTransferService.PlanChunks(size, 0, FileTransferService.PackageChunkSize))
            {
                var chunk = bytes.AsSpan((int)offset, length).ToArray();
                received = store.AppendChunk(chunk, offset, sha, size);
                Assert.Equal(offset + length, received);
            }
            var state = store.GetChunkedState();
            Assert.NotNull(state);
            Assert.Equal(sha, state!.Sha256);
            Assert.Equal(size, state.TotalSize);
            Assert.Equal(size, state.Received);

            var info = store.CommitChunked(sha, size, "operator");
            Assert.Equal(sha, info.Sha256);
            Assert.Equal(size, info.Size);
            Assert.Equal("operator", info.UploadedBy);
            Assert.Null(store.GetChunkedState());
            Assert.True(File.Exists(store.PackagePath));
            Assert.Equal(sha, FileTransferService.HashFile(store.PackagePath));
            Assert.Equal(sha, store.Current?.Sha256);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ChunkedUpload_ResumesFromMidpointAfterClientRestart()
    {
        var dir = NewTempDir();
        try
        {
            var zip = BuildPackage(dir, "9.9.9", 3 * 1024 * 1024);
            var sha = FileTransferService.HashFile(zip);
            var size = new FileInfo(zip).Length;
            var bytes = File.ReadAllBytes(zip);
            var chunks = FileTransferService.PlanChunks(size, 0, FileTransferService.PackageChunkSize);

            var first = new UpdatePackageStore(dir);
            foreach (var (offset, length) in chunks.Take(2))
                first.AppendChunk(bytes.AsSpan((int)offset, length).ToArray(), offset, sha, size);

            // Client restarts: a fresh store over the same dir sees the part.
            var second = new UpdatePackageStore(dir);
            var state = second.GetChunkedState();
            Assert.NotNull(state);
            var resumedFrom = state!.Received;
            Assert.Equal((long)chunks[0].Length + chunks[1].Length, resumedFrom);

            foreach (var (offset, length) in FileTransferService.PlanChunks(size, resumedFrom, FileTransferService.PackageChunkSize))
                second.AppendChunk(bytes.AsSpan((int)offset, length).ToArray(), offset, sha, size);
            var info = second.CommitChunked(sha, size, "operator");
            Assert.Equal(sha, info.Sha256);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ChunkedUpload_OffsetMismatchRejectedWithExpectedOffset()
    {
        var dir = NewTempDir();
        try
        {
            var store = new UpdatePackageStore(dir);
            var zip = BuildPackage(dir, "9.9.9", 600 * 1024);
            var sha = FileTransferService.HashFile(zip);
            var size = new FileInfo(zip).Length;
            var bytes = File.ReadAllBytes(zip);
            store.AppendChunk(bytes.AsSpan(0, FileTransferService.PackageChunkSize).ToArray(), 0, sha, size);

            var ex = Assert.Throws<ChunkOffsetException>(() =>
                store.AppendChunk(new byte[1024], 0, sha, size));
            Assert.Equal(FileTransferService.PackageChunkSize, ex.ExpectedOffset);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ChunkedUpload_CommitWithTamperedBytesDiscardsPart()
    {
        var dir = NewTempDir();
        try
        {
            var store = new UpdatePackageStore(dir);
            var zip = BuildPackage(dir, "9.9.9", 600 * 1024);
            var sha = FileTransferService.HashFile(zip);
            var size = new FileInfo(zip).Length;
            var bytes = File.ReadAllBytes(zip);
            store.AppendChunk(bytes, 0, sha, size);

            // Corrupt one byte of the assembled part (as a torn write or a
            // bit-flip would): the commit must catch the hash mismatch and
            // discard the part — never promote bytes it cannot vouch for.
            var partPath = Path.Combine(dir, "updates", "package.zip.esopart");
            using (var fs = new FileStream(partPath, FileMode.Open, FileAccess.Write))
            {
                fs.Position = 100;
                fs.WriteByte((byte)(bytes[100] ^ 0xFF));
            }

            var ex = Assert.Throws<InvalidDataException>(() => store.CommitChunked(sha, size, "operator"));
            Assert.Contains("SHA-256 mismatch", ex.Message);
            Assert.Null(store.GetChunkedState());
            Assert.False(File.Exists(partPath));
            Assert.False(File.Exists(store.PackagePath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ChunkedUpload_CommitForAnotherPackageKeepsThePart()
    {
        var dir = NewTempDir();
        try
        {
            var store = new UpdatePackageStore(dir);
            var zip = BuildPackage(dir, "9.9.9", 600 * 1024);
            var sha = FileTransferService.HashFile(zip);
            var size = new FileInfo(zip).Length;
            store.AppendChunk(File.ReadAllBytes(zip), 0, sha, size);

            // A commit naming a different hash is simply the wrong commit:
            // rejected, and the rightful package's part is preserved.
            var wrongSha = new string('0', 64);
            Assert.Throws<InvalidDataException>(() => store.CommitChunked(wrongSha, size, "operator"));
            var state = store.GetChunkedState();
            Assert.NotNull(state);
            Assert.Equal(sha, state!.Sha256);
            Assert.Equal(size, state.Received);
            var info = store.CommitChunked(sha, size, "operator");
            Assert.Equal(sha, info.Sha256);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ChunkedUpload_DifferentPackageRejectedUntilAbort()
    {
        var dir = NewTempDir();
        try
        {
            var store = new UpdatePackageStore(dir);
            var zipA = BuildPackage(dir, "9.9.9", 600 * 1024);
            var shaA = FileTransferService.HashFile(zipA);
            var sizeA = new FileInfo(zipA).Length;
            store.AppendChunk(File.ReadAllBytes(zipA), 0, shaA, sizeA);

            var otherSha = new string('1', 64);
            Assert.Throws<InvalidDataException>(() =>
                store.AppendChunk(new byte[1024], 0, otherSha, 1024));

            store.AbortChunked();
            Assert.Null(store.GetChunkedState());
            // After the abort the new package can start cleanly.
            var received = store.AppendChunk(new byte[1024], 0, otherSha, 1024);
            Assert.Equal(1024, received);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ----- the §25 version gate stays intact -----

    [Fact]
    public async Task VersionGate_DowngradePackageStillRefusedByAgentStage()
    {
        var dir = NewTempDir();
        try
        {
            // The §25 case: rollout 90c0e1328fb8 bound a 1.2.0 package while the
            // device ran 1.3.0 — the agent's fetch ended 400 and no downgrade
            // was applied. The chunked upload changes the TRANSPORT, not the
            // gate: staging the same package through the agent must still refuse.
            var cfg = AgentConfig.Load(dir);
            var update = new UpdateService(cfg, new StubProcessRunner());
            var zip = BuildPackage(dir, "1.2.0", 1024);
            var sha = FileTransferService.HashFile(zip);
            await using var stream = File.OpenRead(zip);
            var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
                update.StageVerifiedAsync(stream, sha));
            Assert.Contains("Downgrade refused", ex.Message);
            Assert.Contains("1.2.0", ex.Message);
            Assert.False(File.Exists(Path.Combine(update.StagingDir, "package.zip")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task VersionGate_SameVersionPackageStillRefusedByAgentStage()
    {
        var dir = NewTempDir();
        try
        {
            var cfg = AgentConfig.Load(dir);
            var update = new UpdateService(cfg, new StubProcessRunner());
            var zip = BuildPackage(dir, ProductInfo.Version, 1024);
            var sha = FileTransferService.HashFile(zip);
            await using var stream = File.OpenRead(zip);
            var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
                update.StageVerifiedAsync(stream, sha));
            Assert.Contains("Already at version", ex.Message);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ----- the client wire shape, against a fake fleet server -----

    /// <summary>Minimal in-memory fleet server: verifies every operator
    /// signature fresh (as the real CheckOperator does), keeps the part in
    /// memory, and can inject a per-chunk delay to simulate a slow link.</summary>
    private sealed class FakeFleetServerHandler : HttpMessageHandler
    {
        private readonly string _operatorPub;
        private readonly List<byte> _part = new();
        private string _sha = "";
        private long _total;
        public TimeSpan PerChunkDelay { get; set; } = TimeSpan.Zero;
        public List<(string Method, string Path, long Timestamp)> Requests { get; } = new();
        public List<long> ChunkTimestamps { get; } = new();
        public int ChunkPuts => Requests.Count(r => r.Method == "PUT");

        public FakeFleetServerHandler(string operatorPub) => _operatorPub = operatorPub;

        /// <summary>Pre-seed the part as if an earlier interrupted upload left it.</summary>
        public void Seed(byte[] packageBytes, string sha, long total, long received)
        {
            _sha = sha;
            _total = total;
            _part.AddRange(packageBytes.Take((int)received));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = request.RequestUri.Query;
            var body = request.Content == null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(ct);
            var ts = request.Headers.TryGetValues("X-Eso-Operator-Timestamp", out var tsVals)
                && long.TryParse(tsVals.First(), out var t) ? t : -1;
            Requests.Add((request.Method.Method, path + query, ts));

            // The real server verifies the signature BEFORE anything else.
            var sig = request.Headers.TryGetValues("X-Eso-Operator-Signature", out var sigVals) ? sigVals.First() : "";
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (!OperatorSigning.VerifyRequest(_operatorPub, sig, request.Method.Method, path + query, ts, body, now))
                return Json(new { error = "Operator signature invalid." }, HttpStatusCode.Unauthorized);

            if (path == "/fleet/update/package/upload-state" && request.Method == HttpMethod.Get)
                return Json(new ChunkedUploadStateDto { Sha256 = _sha, TotalSize = _total, Received = _part.Count });

            if (path == "/fleet/update/package/chunk" && request.Method == HttpMethod.Put)
            {
                ChunkTimestamps.Add(ts);
                if (PerChunkDelay > TimeSpan.Zero) await Task.Delay(PerChunkDelay, ct);
                var q = ParseQuery(query);
                var sha = q["sha256"];
                var offset = long.Parse(q["offset"]);
                var total = long.Parse(q["total"]);
                if (_part.Count > 0 && !string.Equals(_sha, sha, StringComparison.OrdinalIgnoreCase))
                    return Json(new { error = "another upload in progress" }, HttpStatusCode.Conflict);
                if (_part.Count == 0) { _sha = sha; _total = total; }
                if (offset != _part.Count)
                    return Json(new { error = $"Chunk offset {offset} does not match received {_part.Count}; resume from {_part.Count}." }, HttpStatusCode.Conflict);
                _part.AddRange(body);
                return Json(new ChunkedUploadStateDto { Sha256 = sha, TotalSize = total, Received = _part.Count });
            }

            if (path == "/fleet/update/package/commit" && request.Method == HttpMethod.Post)
            {
                var req = JsonSerializer.Deserialize<ChunkedUploadCommitRequest>(body, EsoJson.Options)!;
                var actual = Convert.ToHexString(SHA256.HashData(_part.ToArray())).ToLowerInvariant();
                if (_part.Count != req.Size || !string.Equals(actual, req.Sha256, StringComparison.OrdinalIgnoreCase))
                    return Json(new { error = "SHA-256 mismatch." }, HttpStatusCode.BadRequest);
                return Json(new { sha256 = actual, size = (long)_part.Count, uploadedAt = DateTimeOffset.UtcNow, uploadedBy = "operator" });
            }

            if (path == "/fleet/update/package/upload" && request.Method == HttpMethod.Delete)
            {
                _part.Clear();
                _sha = "";
                _total = 0;
                return Json(new { aborted = true });
            }

            return Json(new { error = "not found" }, HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(object obj, HttpStatusCode status = HttpStatusCode.OK)
            => new(status)
            {
                Content = new StringContent(JsonSerializer.Serialize(obj, EsoJson.Options), Encoding.UTF8, "application/json"),
            };

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var d = new Dictionary<string, string>();
            foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=', 2);
                if (kv.Length == 2) d[Uri.UnescapeDataString(kv[0])] = Uri.UnescapeDataString(kv[1]);
            }
            return d;
        }
    }

    private static (FleetApiClient Client, FakeFleetServerHandler Handler) NewClientWithFakeServer()
    {
        // No `using`: the client keeps the key and signs with it after return.
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pub = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var handler = new FakeFleetServerHandler(pub);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://127.0.0.1:8740"), Timeout = TimeSpan.FromMinutes(35) };
        var cfg = new ClientConfig { FleetUrl = "https://127.0.0.1:8740" };
        // The client keeps the key: the handler verifies with the public half.
        var client = new FleetApiClient(cfg, key, http);
        return (client, handler);
    }

    [Fact]
    public async Task ClientUpload_EachChunkCarriesAFreshSignature()
    {
        var dir = NewTempDir();
        try
        {
            // 5 chunks; the fake server delays each by 1 s, so a single
            // upfront signature would be visibly stale by the last chunk —
            // per-chunk signing is what keeps every request inside the skew.
            var zip = BuildPackage(dir, "9.9.9", 5 * FileTransferService.PackageChunkSize);
            var (client, handler) = NewClientWithFakeServer();
            handler.PerChunkDelay = TimeSpan.FromSeconds(1);

            var size = new FileInfo(zip).Length;
            // The zip carries its payload PLUS zip headers, so the file is
            // bigger than 5 chunks — count from the real file size, not the
            // payload size.
            var expectedChunks = (int)((size + FileTransferService.PackageChunkSize - 1) / FileTransferService.PackageChunkSize);
            var info = await client.UploadUpdatePackageAsync(zip);
            Assert.Equal(FileTransferService.HashFile(zip), info.GetProperty("sha256").GetString());
            Assert.Equal(size, info.GetProperty("size").GetInt64());
            Assert.Equal(expectedChunks, handler.ChunkPuts);

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var ts in handler.ChunkTimestamps)
                Assert.True(Math.Abs(now - ts) <= HmacAuth.MaxSkewSeconds,
                    $"chunk timestamp {ts} must be fresh at arrival (now {now})");
            Assert.True(handler.ChunkTimestamps[^1] > handler.ChunkTimestamps[0],
                "chunk timestamps must advance across the upload — one upfront signature would repeat the first");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task ClientUpload_ResumesFromServerOffsetWithoutResending()
    {
        var dir = NewTempDir();
        try
        {
            var zip = BuildPackage(dir, "9.9.9", 5 * FileTransferService.PackageChunkSize);
            var bytes = File.ReadAllBytes(zip);
            var sha = FileTransferService.HashFile(zip);
            var size = new FileInfo(zip).Length;
            var (client, handler) = NewClientWithFakeServer();
            handler.Seed(bytes, sha, size, 2L * FileTransferService.PackageChunkSize);

            var info = await client.UploadUpdatePackageAsync(zip);
            Assert.Equal(sha, info.GetProperty("sha256").GetString());
            var totalChunks = (int)((size + FileTransferService.PackageChunkSize - 1) / FileTransferService.PackageChunkSize);
            Assert.Equal(totalChunks - 2, handler.ChunkPuts); // only the missing chunks
            Assert.Contains(handler.Requests, r => r.Method == "GET" && r.Path.StartsWith("/fleet/update/package/upload-state"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task ClientUpload_AbortsDifferentPackageThenStartsOurs()
    {
        var dir = NewTempDir();
        try
        {
            var zip = BuildPackage(dir, "9.9.9", 2 * FileTransferService.PackageChunkSize);
            var bytes = File.ReadAllBytes(zip);
            var sha = FileTransferService.HashFile(zip);
            var (client, handler) = NewClientWithFakeServer();
            // Another package's upload is in progress on the server.
            handler.Seed(new byte[1024], new string('2', 64), 1024, 1024);

            var info = await client.UploadUpdatePackageAsync(zip);
            Assert.Equal(sha, info.GetProperty("sha256").GetString());
            Assert.Contains(handler.Requests, r => r.Method == "DELETE" && r.Path == "/fleet/update/package/upload");
            var size = new FileInfo(zip).Length;
            var totalChunks = (int)((size + FileTransferService.PackageChunkSize - 1) / FileTransferService.PackageChunkSize);
            Assert.Equal(totalChunks, handler.ChunkPuts);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
