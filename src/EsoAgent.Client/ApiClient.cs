using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using EsoAgent.Core;

namespace EsoAgent.Client;

/// <summary>
/// Talks to an agent: HTTPS with the server cert pinned by SHA-256 fingerprint,
/// every request HMAC-signed exactly as the server expects
/// (method + pathAndQuery + timestamp + body hash).
/// </summary>
public sealed class ApiClient : IDisposable
{
    private readonly ClientConfig _cfg;
    private readonly HttpClient _http;

    public ApiClient(ClientConfig cfg)
    {
        _cfg = cfg;
        var handler = new HttpClientHandler
        {
            // Agents are reached directly (tailnet / loopback / LAN) — an ambient
            // HTTP proxy from the environment must never intercept agent traffic.
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (_, cert, _, _) => CheckFingerprint(cert),
        };
        _http = new HttpClient(handler) { BaseAddress = new Uri(cfg.BaseUrl), Timeout = TimeSpan.FromMinutes(35) };
    }

    private bool CheckFingerprint(X509Certificate2? cert)
    {
        if (cert == null) return false;
        var fp = Convert.ToHexString(SHA256.HashData(cert.GetRawCertData())).ToLowerInvariant();
        return string.Equals(fp, _cfg.Fingerprint.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private HttpRequestMessage Signed(HttpMethod method, string pathAndQuery, byte[] body, string contentType = "application/json")
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sig = HmacAuth.Sign(_cfg.TokenBytes, method.Method, pathAndQuery, ts, body);
        var req = new HttpRequestMessage(method, pathAndQuery);
        req.Headers.Add(HmacAuth.TimestampHeader, ts.ToString());
        req.Headers.Add(HmacAuth.SignatureHeader, sig);
        if (body.Length > 0 || method == HttpMethod.Post || method == HttpMethod.Put)
            req.Content = new ByteArrayContent(body) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType) } };
        return req;
    }

    private static byte[] JsonBody<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, EsoJson.Options);

    public async Task<T> GetJsonAsync<T>(string pathAndQuery)
    {
        using var req = Signed(HttpMethod.Get, pathAndQuery, Array.Empty<byte>());
        return await SendJsonAsync<T>(req);
    }

    public async Task<T> PostJsonAsync<T>(string path, object? body = null)
    {
        using var req = Signed(HttpMethod.Post, path, body == null ? Array.Empty<byte>() : JsonBody(body));
        return await SendJsonAsync<T>(req);
    }

    public async Task<T> PutJsonAsync<T>(string path, object body)
    {
        using var req = Signed(HttpMethod.Put, path, JsonBody(body));
        return await SendJsonAsync<T>(req);
    }

    public async Task<byte[]> GetBytesAsync(string pathAndQuery, Action<long>? onTotalSize = null)
    {
        using var req = Signed(HttpMethod.Get, pathAndQuery, Array.Empty<byte>());
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        await EnsureOk(res);
        if (res.Headers.TryGetValues("X-Total-Size", out var totals) && long.TryParse(totals.First(), out var total))
            onTotalSize?.Invoke(total);
        return await res.Content.ReadAsByteArrayAsync();
    }

    /// <summary>PUT with a raw body (file chunks).</summary>
    public async Task<T> PutRawAsync<T>(string pathAndQuery, byte[] body)
    {
        using var req = Signed(HttpMethod.Put, pathAndQuery, body, "application/octet-stream");
        return await SendJsonAsync<T>(req);
    }

    /// <summary>
    /// Uploads an update package: streamed body, authenticated by the package-signature
    /// scheme (the request signature covers the declared package SHA-256).
    /// </summary>
    public async Task<string> PutUpdatePackageAsync(string zipPath)
    {
        var sha = FileTransferService.HashFile(zipPath);
        var packageSig = UpdateService.SignPackage(_cfg.TokenBytes, sha);
        var pathAndQuery = "/api/update/package";
        var pseudoBody = Encoding.UTF8.GetBytes(sha);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sig = HmacAuth.Sign(_cfg.TokenBytes, "PUT", pathAndQuery, ts, pseudoBody);
        using var req = new HttpRequestMessage(HttpMethod.Put, pathAndQuery);
        req.Headers.Add(HmacAuth.TimestampHeader, ts.ToString());
        req.Headers.Add(HmacAuth.SignatureHeader, sig);
        req.Headers.Add("X-Package-Sha256", sha);
        req.Headers.Add("X-Package-Signature", packageSig);
        req.Content = new StreamContent(File.OpenRead(zipPath)) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip") } };
        using var res = await _http.SendAsync(req);
        await EnsureOk(res);
        return sha;
    }

    /// <summary>Streams an exec over WebSocket: prints stdout/stderr live, returns the remote exit code.</summary>
    public async Task<int> ExecStreamAsync(ExecRequest execReq)
    {
        var wsUri = new Uri($"wss://{_cfg.Host}:{_cfg.Port}/api/exec/stream");
        using var ws = new ClientWebSocket();
        ws.Options.RemoteCertificateValidationCallback = (_, cert, _, _) => cert != null && CheckFingerprint(new X509Certificate2(cert));
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sig = HmacAuth.Sign(_cfg.TokenBytes, "GET", "/api/exec/stream", ts, Array.Empty<byte>());
        ws.Options.SetRequestHeader(HmacAuth.TimestampHeader, ts.ToString());
        ws.Options.SetRequestHeader(HmacAuth.SignatureHeader, sig);
        await ws.ConnectAsync(wsUri, CancellationToken.None);

        var payload = JsonSerializer.SerializeToUtf8Bytes(execReq, EsoJson.Options);
        await ws.SendAsync(payload, WebSocketMessageType.Text, true, CancellationToken.None);

        var buffer = new byte[64 * 1024];
        var exitCode = 1;
        while (ws.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult frame;
            do
            {
                frame = await ws.ReceiveAsync(buffer, CancellationToken.None);
                if (frame.MessageType == WebSocketMessageType.Close) break;
                ms.Write(buffer, 0, frame.Count);
            } while (!frame.EndOfMessage);
            if (frame.MessageType == WebSocketMessageType.Close) break;
            using var doc = JsonDocument.Parse(ms.ToArray());
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();
            if (type == "data")
            {
                var stream = root.GetProperty("stream").GetString();
                var data = root.GetProperty("data").GetString() ?? "";
                if (stream == "stderr") Console.Error.Write(data); else Console.Write(data);
            }
            else if (type == "exit")
            {
                exitCode = root.TryGetProperty("exitCode", out var ec) && ec.ValueKind == JsonValueKind.Number ? ec.GetInt32() : 1;
                break;
            }
        }
        return exitCode;
    }

    /// <summary>
    /// Follows a log file over WebSocket (direct mode): prints the tail, then
    /// streams appended lines until cancelled (Ctrl+C) or the server ends the
    /// session. Returns 0 on a clean follow, 1 on a server-reported error.
    /// </summary>
    public async Task<int> FollowLogAsync(LogFollowRequest followReq, CancellationToken ct)
    {
        var wsUri = new Uri($"wss://{_cfg.Host}:{_cfg.Port}/api/logs/follow");
        using var ws = new ClientWebSocket();
        ws.Options.RemoteCertificateValidationCallback = (_, cert, _, _) => cert != null && CheckFingerprint(new X509Certificate2(cert));
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sig = HmacAuth.Sign(_cfg.TokenBytes, "GET", "/api/logs/follow", ts, Array.Empty<byte>());
        ws.Options.SetRequestHeader(HmacAuth.TimestampHeader, ts.ToString());
        ws.Options.SetRequestHeader(HmacAuth.SignatureHeader, sig);
        await ws.ConnectAsync(wsUri, ct);

        var payload = JsonSerializer.SerializeToUtf8Bytes(followReq, EsoJson.Options);
        await ws.SendAsync(payload, WebSocketMessageType.Text, true, ct);

        var buffer = new byte[64 * 1024];
        var exit = 0;
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult frame;
                do
                {
                    frame = await ws.ReceiveAsync(buffer, ct);
                    if (frame.MessageType == WebSocketMessageType.Close) break;
                    ms.Write(buffer, 0, frame.Count);
                } while (!frame.EndOfMessage);
                if (frame.MessageType == WebSocketMessageType.Close) break;
                using var doc = JsonDocument.Parse(ms.ToArray());
                var root = doc.RootElement;
                var type = root.GetProperty("type").GetString();
                if (type == "line") Console.WriteLine(root.GetProperty("line").GetString());
                else if (type == "reset") Console.Error.WriteLine($"[follow] {root.GetProperty("error").GetString()}");
                else if (type == "error") { Console.Error.WriteLine($"error: {root.GetProperty("error").GetString()}"); exit = 1; break; }
            }
        }
        catch (OperationCanceledException) { /* Ctrl+C — a follow ends when the operator says so */ }
        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { }
        return exit;
    }

    /// <summary>Raw signed send used by the transport layer (direct mode). Returns (status, body bytes).</summary>
    public async Task<(int Status, byte[] Body)> RawSendAsync(string method, string pathAndQuery, byte[] body)
    {
        using var req = Signed(new HttpMethod(method), pathAndQuery, body);
        using var res = await _http.SendAsync(req);
        return ((int)res.StatusCode, await res.Content.ReadAsByteArrayAsync());
    }

    private async Task<T> SendJsonAsync<T>(HttpRequestMessage req)
    {
        using var res = await _http.SendAsync(req);
        await EnsureOk(res);
        var json = await res.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(json, EsoJson.Options)
               ?? throw new InvalidOperationException("Empty response from agent.");
    }

    private static async Task EnsureOk(HttpResponseMessage res)
    {
        if (res.IsSuccessStatusCode) return;
        var body = await res.Content.ReadAsStringAsync();
        throw new HttpRequestException($"Agent returned {(int)res.StatusCode} {res.ReasonPhrase}: {body}");
    }

    public void Dispose() => _http.Dispose();
}
