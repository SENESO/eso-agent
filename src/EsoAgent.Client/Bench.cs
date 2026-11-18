using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using EsoAgent.Core;

namespace EsoAgent.Client;

/// <summary>
/// eso bench (v1.2) — measures THIS deployment, honestly:
///   status round-trip ×20 and shell-exec round-trip ×10 (min/median/p95),
///   8 MiB file upload + download throughput over the same channel,
///   desktop frames/second at quality 60 over ~5 s,
///   offline-queue enqueue latency (fleet mode, when an offline device exists).
/// Run it once with a direct target and once with --device &lt;CODE&gt; to fill
/// both columns of BENCHMARKS.md. Numbers are this machine + this network,
/// nothing more — the method is printed with the results.
/// </summary>
public static class BenchCommand
{
    private sealed record Stats(double Min, double Median, double P95)
    {
        public static Stats From(List<double> samples)
        {
            samples.Sort();
            double Pick(double q) => samples[Math.Min(samples.Count - 1, (int)(q * samples.Count))];
            return new Stats(samples[0], Pick(0.5), Pick(0.95));
        }
        public override string ToString() => $"min {Min:F0} ms · median {Median:F0} ms · p95 {P95:F0} ms";
    }

    public static async Task<int> RunAsync(ClientConfig cfg, string? deviceArg, List<string> rest)
    {
        string? outPath = null;
        for (int i = 0; i + 1 < rest.Count; i++)
            if (rest[i] == "--out") outPath = rest[i + 1];

        using var transport = Transport.Create(cfg, deviceArg, null, false);
        var mode = transport.IsFleet ? "relay" : "direct";
        Console.WriteLine($"Benchmarking via {transport.ModeLabel} …");

        var statusStats = await SampleAsync(20, async () =>
        {
            var r = await transport.SendAsync("GET", "/api/status", null, 30);
            if (r.Count == 0 || !r[0].Ok) throw new InvalidOperationException(r.Count == 0 ? "no reply" : r[0].Error ?? r[0].Text);
        });
        Console.WriteLine($"status RTT ×20:        {statusStats}");

        var execStats = await SampleAsync(10, async () =>
        {
            var r = await transport.SendAsync("POST", "/api/shell", new ShellRequest { Shell = "", Script = "echo eso-bench" }, 60);
            if (r.Count == 0 || !r[0].Ok) throw new InvalidOperationException(r.Count == 0 ? "no reply" : r[0].Error ?? r[0].Text);
        });
        Console.WriteLine($"exec RTT ×10:          {execStats}");

        // ---- file throughput: 8 MiB up, then back down ----
        var payloadPath = Path.Combine(Path.GetTempPath(), $"eso-bench-{Guid.NewGuid():N}.bin");
        var remotePath = "eso-bench.bin";
        double upMbps = 0, downMbps = 0;
        try
        {
            var bytes = new byte[8 * 1024 * 1024];
            RandomNumberGenerator.Fill(bytes);
            await File.WriteAllBytesAsync(payloadPath, bytes);
            var sha = FileTransferService.HashFile(payloadPath);

            var sw = Stopwatch.StartNew();
            var begin = await transport.SendAsync("POST", "/api/files/begin", new BeginUploadRequest { Path = remotePath });
            if (begin.Count == 0 || !begin[0].Ok) throw new InvalidOperationException("begin upload failed");
            const int chunkSize = 256 * 1024;
            long current = 0;
            await using (var fs = File.OpenRead(payloadPath))
            {
                var buffer = new byte[chunkSize];
                int read;
                while ((read = await fs.ReadAsync(buffer)) > 0)
                {
                    var chunk = read == buffer.Length ? buffer : buffer[..read];
                    var rs = await transport.SendRawAsync("PUT", $"/api/files/chunk?path={Uri.EscapeDataString(remotePath)}&offset={current}", chunk, 120);
                    if (rs.Count == 0 || !rs[0].Ok) throw new InvalidOperationException("chunk failed");
                    current = rs[0].Json<ChunkResponse>().Received;
                }
            }
            var commit = await transport.SendAsync("POST", "/api/files/commit", new CommitUploadRequest { Path = remotePath, Sha256 = sha });
            if (commit.Count == 0 || !commit[0].Ok) throw new InvalidOperationException("commit failed");
            sw.Stop();
            upMbps = bytes.Length / 1048576.0 / sw.Elapsed.TotalSeconds;
            Console.WriteLine($"upload 8 MiB:          {upMbps:F1} MiB/s ({sw.Elapsed.TotalSeconds:F1} s, sha256 verified by commit)");

            sw.Restart();
            long offset = 0;
            while (offset < bytes.Length)
            {
                var length = Math.Min(1024 * 1024, bytes.Length - offset);
                var rs = await transport.SendRawAsync("GET", $"/api/files/download?path={Uri.EscapeDataString(remotePath)}&offset={offset}&length={length}", Array.Empty<byte>(), 120);
                if (rs.Count == 0 || !rs[0].Ok) throw new InvalidOperationException("download failed");
                offset += rs[0].Body.Length;
            }
            sw.Stop();
            downMbps = bytes.Length / 1048576.0 / sw.Elapsed.TotalSeconds;
            Console.WriteLine($"download 8 MiB:        {downMbps:F1} MiB/s ({sw.Elapsed.TotalSeconds:F1} s)");
            await transport.SendAsync("POST", "/api/files/delete", new FilePathRequest { Path = remotePath });
        }
        finally { try { File.Delete(payloadPath); } catch { } }

        // ---- delta efficiency (item 37): full payload vs delta payload ----
        string deltaLine = "n/a";
        try
        {
            var first = await transport.SendRawAsync("GET", "/api/inventory?delta=1", Array.Empty<byte>(), 120);
            var second = await transport.SendRawAsync("GET", "/api/inventory?delta=1", Array.Empty<byte>(), 120);
            if (first.Count > 0 && first[0].Ok && second.Count > 0 && second[0].Ok)
            {
                var env1 = second[0].Json<DeltaEnvelope>();
                var full = env1.FullBytes;
                var sent = env1.SentBytes;
                var saved = full > 0 ? 100.0 * (full - sent) / full : 0;
                deltaLine = env1.IsDelta
                    ? $"inventory payload {full} B full → {sent} B as delta ({saved:F0}% less on the wire)"
                    : $"inventory payload {full} B (second read was not a delta: {env1.Note})";
                Console.WriteLine($"delta efficiency:      {deltaLine}");
            }
        }
        catch (Exception ex) { deltaLine = $"not measured ({ex.Message})"; }

        // ---- desktop fps at quality 60 ----
        double? fps = null;
        string fpsNote = "";
        try
        {
            fps = transport.IsFleet
                ? await MeasureFleetDesktopFpsAsync(cfg, deviceArg!)
                : await MeasureDirectDesktopFpsAsync(cfg, deviceArg);
            Console.WriteLine($"desktop fps (q60):     {fps:F1}");
        }
        catch (Exception ex) { fpsNote = ex.Message; Console.WriteLine($"desktop fps (q60):     not measured ({ex.Message})"); }

        // ---- queue enqueue latency (fleet, offline device) ----
        string queueLine = "n/a (direct mode)";
        if (transport.IsFleet)
        {
            using var fleet = Program.CreateFleetClient(cfg);
            var devices = await fleet.GetDevicesAsync();
            var offline = devices.FirstOrDefault(d => d.State == "Approved" && !d.Online);
            if (offline == null) queueLine = "n/a (no offline approved device to queue for)";
            else
            {
                var sw = Stopwatch.StartNew();
                var req = fleet.BuildCommand(new FleetTargets { Codes = new List<string> { offline.Code } }, "GET", "/api/status", Array.Empty<byte>(), 30, queueIfOffline: true);
                var resp = await fleet.PostCommandAsync(req);
                sw.Stop();
                var queued = resp.Results.FirstOrDefault()?.Queued == true;
                queueLine = queued
                    ? $"enqueue {sw.ElapsedMilliseconds} ms (delivery happens when {offline.Code} reconnects — measured in the live test, not here)"
                    : "device answered directly (it came online during the run)";
                Console.WriteLine($"queue:                 {queueLine}");
            }
        }

        if (outPath != null)
        {
            var md = $"""
                ## Bench run — {DateTimeOffset.Now:yyyy-MM-dd HH:mm} ({mode})

                | Metric | Result |
                |---|---|
                | Status round-trip ×20 | {statusStats} |
                | Shell exec round-trip ×10 | {execStats} |
                | File upload 8 MiB | {upMbps:F1} MiB/s |
                | File download 8 MiB | {downMbps:F1} MiB/s |
                | Desktop fps @ quality 60 | {(fps is { } f ? $"{f:F1}" : $"not measured ({fpsNote})")} |
                | Offline queue | {queueLine} |
                | Delta efficiency | {deltaLine} |

                """;
            await File.WriteAllTextAsync(outPath, md);
            Console.WriteLine($"Wrote {outPath}");
        }
        return 0;
    }

    private static async Task<Stats> SampleAsync(int count, Func<Task> call)
    {
        var samples = new List<double>();
        for (int i = 0; i < count; i++)
        {
            var sw = Stopwatch.StartNew();
            await call();
            sw.Stop();
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }
        return Stats.From(samples);
    }

    private static async Task<double> MeasureDirectDesktopFpsAsync(ClientConfig cfg, string? deviceArg)
    {
        var directCfg = cfg;
        if (deviceArg != null)
        {
            var parts = deviceArg.Split(':');
            directCfg = cfg.WithEndpoint(parts[0], parts.Length == 2 && int.TryParse(parts[1], out var p) ? p : cfg.Port);
        }
        using var ws = new ClientWebSocket();
        var fp = directCfg.Fingerprint.Trim();
        ws.Options.RemoteCertificateValidationCallback = (_, cert, _, _) =>
            cert != null && string.Equals(
                Convert.ToHexString(SHA256.HashData(new X509Certificate2(cert).GetRawCertData())).ToLowerInvariant(),
                fp, StringComparison.OrdinalIgnoreCase);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        ws.Options.SetRequestHeader(HmacAuth.TimestampHeader, ts.ToString());
        ws.Options.SetRequestHeader(HmacAuth.SignatureHeader,
            HmacAuth.Sign(directCfg.TokenBytes, "GET", "/api/desktop", ts, Array.Empty<byte>()));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await ws.ConnectAsync(new Uri($"wss://{directCfg.Host}:{directCfg.Port}/api/desktop"), cts.Token);
        var hello = new DesktopHello { Quality = 60, Mode = "View", MaxFps = 30, Codecs = new List<string> { "jpeg", "rawdiff" } };
        await ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(hello, EsoJson.Options)), WebSocketMessageType.Text, true, cts.Token);
        return await CountFramesAsync(ws, cts.Token);
    }

    private static async Task<double> MeasureFleetDesktopFpsAsync(ClientConfig cfg, string code)
    {
        using var fleet = Program.CreateFleetClient(cfg);
        await using var client = new RelayDesktopClient();
        var frames = 0;
        client.FrameReceived += _ => Interlocked.Increment(ref frames);
        await client.ConnectAsync(fleet, code.ToUpperInvariant(), new DesktopHello { Quality = 60, Mode = "View", MaxFps = 30, Codecs = new List<string> { "jpeg", "rawdiff" } });
        var sw = Stopwatch.StartNew();
        await Task.Delay(5000);
        sw.Stop();
        return frames / sw.Elapsed.TotalSeconds;
    }

    /// <summary>Counts desktop "frame" messages for ~5 s after the welcome.</summary>
    private static async Task<double> CountFramesAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[256 * 1024];
        var frames = 0;
        var started = false;
        var sw = new Stopwatch();
        using var windowCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Delay(20000, ct).ContinueWith(_ => windowCts.Cancel(), TaskScheduler.Default);
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult frame;
                do
                {
                    frame = await ws.ReceiveAsync(buffer, windowCts.Token);
                    if (frame.MessageType == WebSocketMessageType.Close) return 0;
                    ms.Write(buffer, 0, frame.Count);
                } while (!frame.EndOfMessage);
                var text = Encoding.UTF8.GetString(ms.ToArray());
                if (!started)
                {
                    if (text.Contains("\"welcome\"")) { started = true; sw.Start(); _ = Task.Delay(5000).ContinueWith(_ => windowCts.Cancel(), TaskScheduler.Default); }
                    continue;
                }
                if (text.Contains("\"frame\"")) frames++;
                if (sw.Elapsed > TimeSpan.FromSeconds(5)) break;
            }
        }
        catch (OperationCanceledException) { }
        if (!started || sw.Elapsed.TotalSeconds < 1) throw new InvalidOperationException("no desktop frames arrived (no interactive session?)");
        return frames / Math.Min(5.0, sw.Elapsed.TotalSeconds);
    }
}
