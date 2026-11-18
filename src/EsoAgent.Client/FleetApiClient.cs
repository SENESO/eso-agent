using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using EsoAgent.Core;

namespace EsoAgent.Client;

/// <summary>
/// Talks to the fleet server with OPERATOR-SIGNED requests (ECDSA, headers
/// X-Eso-Operator-Timestamp / X-Eso-Operator-Signature). Commands submitted here
/// are additionally signed as command envelopes; devices verify those signatures
/// themselves, so the server is a relay, not a trust root.
/// </summary>
public sealed class FleetApiClient : IDisposable
{
    private readonly ClientConfig _cfg;
    private readonly ECDsa _operatorKey;
    private readonly HttpClient _http;

    public FleetApiClient(ClientConfig cfg, ECDsa operatorKey)
    {
        _cfg = cfg;
        _operatorKey = operatorKey;
        var handler = new HttpClientHandler { UseProxy = false }; // direct only — see ApiClient
        if (!string.IsNullOrWhiteSpace(cfg.FleetFingerprint))
        {
            var fp = cfg.FleetFingerprint.Trim();
            handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
                cert != null && string.Equals(
                    Convert.ToHexString(SHA256.HashData(cert.GetRawCertData())).ToLowerInvariant(),
                    fp, StringComparison.OrdinalIgnoreCase);
        }
        _http = new HttpClient(handler) { BaseAddress = new Uri(cfg.FleetUrl!), Timeout = TimeSpan.FromMinutes(35) };
    }

    private HttpRequestMessage Signed(HttpMethod method, string pathAndQuery, byte[] body)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sig = OperatorSigning.SignRequest(_operatorKey, method.Method, pathAndQuery, ts, body);
        var req = new HttpRequestMessage(method, pathAndQuery);
        req.Headers.Add("X-Eso-Operator-Timestamp", ts.ToString());
        req.Headers.Add("X-Eso-Operator-Signature", sig);
        if (body.Length > 0)
            req.Content = new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } };
        return req;
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? bodyObj = null)
    {
        var body = bodyObj == null ? Array.Empty<byte>() : JsonSerializer.SerializeToUtf8Bytes(bodyObj, EsoJson.Options);
        using var req = Signed(method, path, body);
        using var res = await _http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"Fleet server returned {(int)res.StatusCode}: {text}");
        return JsonSerializer.Deserialize<T>(text, EsoJson.Options)
               ?? throw new InvalidOperationException("Empty response from fleet server.");
    }

    public Task<List<FleetDeviceDto>> GetDevicesAsync() => SendAsync<List<FleetDeviceDto>>(HttpMethod.Get, "/fleet/devices");

    public Task<MeshStateDto> GetMeshAsync() => SendAsync<MeshStateDto>(HttpMethod.Get, "/fleet/mesh");

    public Task<JsonElement> ResolveAsync(string name) => SendAsync<JsonElement>(HttpMethod.Get, $"/fleet/resolve?name={Uri.EscapeDataString(name)}");

    public Task<FleetCommandResponse> PostCommandAsync(FleetCommandRequest req) => SendAsync<FleetCommandResponse>(HttpMethod.Post, "/fleet/command", req);

    public Task<FleetResultFrame> PostAdminRelayAsync(FleetAdminRelayRequest req) => SendAsync<FleetResultFrame>(HttpMethod.Post, "/fleet/admin", req);

    public Task<JsonElement> PostRegistryActionAsync(string action, object body) => SendAsync<JsonElement>(HttpMethod.Post, $"/fleet/admin/{action}", body);

    /// <summary>Builds a signed command envelope for one API call (method/path/body).
    /// With queueIfOffline (v1.1), the envelope is signed in the QUEUED canonical
    /// form with an explicit expiry (default 24 h): the server may then persist
    /// it for offline devices and deliver it on reconnect; devices verify the
    /// queued signature + window. Online delivery works identically.</summary>
    public FleetCommandRequest BuildCommand(FleetTargets targets, string method, string path, byte[] body, int timeoutSeconds = 300, bool queueIfOffline = false)
    {
        var commandId = Guid.NewGuid().ToString("N");
        var issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (queueIfOffline)
        {
            var expiresAt = issuedAt + QueuePolicy.DefaultTtlSeconds;
            return new FleetCommandRequest
            {
                Targets = targets,
                CommandId = commandId,
                IssuedAt = issuedAt,
                Signature = OperatorSigning.SignQueuedCommand(_operatorKey, commandId, issuedAt, expiresAt, method, path, body),
                Method = method,
                Path = path,
                BodyBase64 = Convert.ToBase64String(body),
                TimeoutSeconds = timeoutSeconds,
                QueueIfOffline = true,
                ExpiresAt = expiresAt,
            };
        }
        return new FleetCommandRequest
        {
            Targets = targets,
            CommandId = commandId,
            IssuedAt = issuedAt,
            Signature = OperatorSigning.SignCommand(_operatorKey, commandId, issuedAt, method, path, body),
            Method = method,
            Path = path,
            BodyBase64 = Convert.ToBase64String(body),
            TimeoutSeconds = timeoutSeconds,
        };
    }

    /// <summary>Offline queue (v1.1): list queued/delivered/expired commands, optionally per device.</summary>
    public Task<List<QueuedCommandRecord>> GetQueueAsync(string? deviceCode = null)
        => SendAsync<List<QueuedCommandRecord>>(HttpMethod.Get,
            deviceCode == null ? "/fleet/queue" : $"/fleet/queue?device={Uri.EscapeDataString(deviceCode)}");

    /// <summary>Offline queue (v1.1): one queued command with its delivery state/result.</summary>
    public Task<QueuedCommandRecord> GetQueuedAsync(string id)
        => SendAsync<QueuedCommandRecord>(HttpMethod.Get, $"/fleet/queue/{Uri.EscapeDataString(id)}");

    // ---------- v1.2: activity, health, scripts, rollout, wake, streams ----------

    public Task<List<ActivityEvent>> GetActivityAsync(ActivityQuery query)
    {
        var path = "/fleet/activity?limit=" + query.Limit;
        if (query.Device != null) path += "&device=" + Uri.EscapeDataString(query.Device);
        if (query.Kind != null) path += "&kind=" + Uri.EscapeDataString(query.Kind);
        if (query.Source != null) path += "&source=" + Uri.EscapeDataString(query.Source);
        if (query.Since != null) path += "&since=" + Uri.EscapeDataString(query.Since.Value.ToString("O"));
        if (query.Until != null) path += "&until=" + Uri.EscapeDataString(query.Until.Value.ToString("O"));
        return SendAsync<List<ActivityEvent>>(HttpMethod.Get, path);
    }

    /// <summary>Exports the unified timeline (JSON array or CSV text) for a date range.</summary>
    public async Task<string> ExportActivityAsync(ActivityQuery query, string format)
    {
        var path = "/fleet/activity/export?format=" + format + "&limit=5000";
        if (query.Device != null) path += "&device=" + Uri.EscapeDataString(query.Device);
        if (query.Since != null) path += "&since=" + Uri.EscapeDataString(query.Since.Value.ToString("O"));
        if (query.Until != null) path += "&until=" + Uri.EscapeDataString(query.Until.Value.ToString("O"));
        using var req = Signed(HttpMethod.Get, path, Array.Empty<byte>());
        using var res = await _http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"Fleet server returned {(int)res.StatusCode}: {text}");
        return text;
    }

    public Task<List<DeviceHealthDto>> GetHealthAsync() => SendAsync<List<DeviceHealthDto>>(HttpMethod.Get, "/fleet/health");

    public Task<List<ScriptDto>> GetScriptsAsync() => SendAsync<List<ScriptDto>>(HttpMethod.Get, "/fleet/scripts");

    public Task<ScriptDto> UpsertScriptAsync(ScriptDto dto) => SendAsync<ScriptDto>(HttpMethod.Post, "/fleet/scripts", dto);

    public Task<JsonElement> DeleteScriptAsync(string id) => SendAsync<JsonElement>(HttpMethod.Delete, $"/fleet/scripts/{Uri.EscapeDataString(id)}");

    public Task<JsonElement> RecordScriptRunAsync(string id, ScriptRunRecordRequest req)
        => SendAsync<JsonElement>(HttpMethod.Post, $"/fleet/scripts/{Uri.EscapeDataString(id)}/runs", req);

    public async Task<JsonElement> UploadUpdatePackageAsync(byte[] zipBytes)
    {
        using var req = Signed(HttpMethod.Put, "/fleet/update/package", zipBytes);
        req.Content = new ByteArrayContent(zipBytes) { Headers = { ContentType = new MediaTypeHeaderValue("application/zip") } };
        using var res = await _http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"Fleet server returned {(int)res.StatusCode}: {text}");
        return JsonSerializer.Deserialize<JsonElement>(text, EsoJson.Options);
    }

    public Task<RolloutCreatedResponse> CreateRolloutAsync(FleetTargets targets)
        => SendAsync<RolloutCreatedResponse>(HttpMethod.Post, "/fleet/rollout", new RolloutCreateRequest { Targets = targets });

    public Task<List<RolloutDto>> GetRolloutsAsync() => SendAsync<List<RolloutDto>>(HttpMethod.Get, "/fleet/rollout");

    public Task<RolloutDto> GetRolloutAsync(string id) => SendAsync<RolloutDto>(HttpMethod.Get, $"/fleet/rollout/{Uri.EscapeDataString(id)}");

    public Task<RolloutDto> SetRolloutDeviceStateAsync(string id, string code, string state, string? error = null)
        => SendAsync<RolloutDto>(HttpMethod.Post, $"/fleet/rollout/{Uri.EscapeDataString(id)}/device",
            new RolloutDeviceStateRequest { Code = code, State = state, Error = error });

    public Task<List<WakeResultDto>> WakeAsync(string codeOrName)
        => SendAsync<List<WakeResultDto>>(HttpMethod.Post, "/fleet/wake", new WakeCodeRequest { Code = codeOrName });

    public Task<JsonElement> SetExitNodeAsync(string? code)
        => SendAsync<JsonElement>(HttpMethod.Post, "/fleet/admin/exit-node", new ExitNodeRequest { Code = code });

    // ----- access codes / grants (item 41) -----

    public Task<AccessCodeCreatedResponse> CreateAccessCodeAsync(AccessCodeCreateRequest req)
        => SendAsync<AccessCodeCreatedResponse>(HttpMethod.Post, "/fleet/access/codes", req);

    public Task<List<AccessCodeDto>> GetAccessCodesAsync(string? device = null)
        => SendAsync<List<AccessCodeDto>>(HttpMethod.Get, "/fleet/access/codes" + (device == null ? "" : $"?device={Uri.EscapeDataString(device)}"));

    public Task<List<GrantDto>> GetAccessGrantsAsync(string? device = null)
        => SendAsync<List<GrantDto>>(HttpMethod.Get, "/fleet/access/grants" + (device == null ? "" : $"?device={Uri.EscapeDataString(device)}"));

    public Task<JsonElement> RevokeAccessAsync(string id)
        => SendAsync<JsonElement>(HttpMethod.Post, "/fleet/access/revoke", new AccessRevokeRequest { Id = id });

    /// <summary>Redemption is intentionally UNSIGNED — the code itself is the
    /// credential. Uses the same pinned-fingerprint HTTP client.</summary>
    public async Task<AccessRedeemResponse> RedeemAccessCodeAsync(string code)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new AccessRedeemRequest { Code = code }, EsoJson.Options);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/fleet/access/redeem")
        {
            Content = new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } },
        };
        using var res = await _http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"Fleet server returned {(int)res.StatusCode}: {text}");
        return JsonSerializer.Deserialize<AccessRedeemResponse>(text, EsoJson.Options)
               ?? throw new InvalidOperationException("Empty response from fleet server.");
    }

    /// <summary>Runs one device command under a grant token (item 41): the
    /// server checks the grant's scope and dispatches it panel-signed.</summary>
    public async Task<FleetResultFrame> GrantCommandAsync(GrantTokenDto token, string method, string path, object? bodyObj, int timeoutSeconds = 300)
    {
        var body = bodyObj == null ? Array.Empty<byte>() : JsonSerializer.SerializeToUtf8Bytes(bodyObj, EsoJson.Options);
        var envelope = new FleetCommandRequest
        {
            Method = method, Path = path, BodyBase64 = Convert.ToBase64String(body), TimeoutSeconds = timeoutSeconds,
        };
        var envelopeBytes = JsonSerializer.SerializeToUtf8Bytes(envelope, EsoJson.Options);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/fleet/access/command")
        {
            Content = new ByteArrayContent(envelopeBytes) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } },
        };
        req.Headers.Add("X-Eso-Grant", Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(token, EsoJson.Options)));
        using var res = await _http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"Fleet server returned {(int)res.StatusCode}: {text}");
        return JsonSerializer.Deserialize<FleetResultFrame>(text, EsoJson.Options)
               ?? throw new InvalidOperationException("Empty response from fleet server.");
    }

    /// <summary>The operator key itself (stream-open signing lives with the caller).</summary>
    public ECDsa OperatorKey => _operatorKey;

    /// <summary>
    /// Opens a WebSocket to a fleet stream endpoint (/fleet/desktop, /fleet/tunnel,
    /// /fleet/tunnel-udp) with the operator request signature in the headers.
    /// The caller then sends the FleetStreamOpenRequest as the first message and
    /// reads the FleetStreamOpenedFrame the server forwards back.
    /// </summary>
    public async Task<System.Net.WebSockets.ClientWebSocket> ConnectStreamSocketAsync(string path, CancellationToken ct = default)
    {
        var ws = new System.Net.WebSockets.ClientWebSocket();
        if (!string.IsNullOrWhiteSpace(_cfg.FleetFingerprint))
        {
            var fp = _cfg.FleetFingerprint.Trim();
            ws.Options.RemoteCertificateValidationCallback = (_, cert, _, _) =>
                cert != null && string.Equals(
                    Convert.ToHexString(SHA256.HashData(new X509Certificate2(cert).GetRawCertData())).ToLowerInvariant(),
                    fp, StringComparison.OrdinalIgnoreCase);
        }
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sig = OperatorSigning.SignRequest(_operatorKey, "GET", path, ts, Array.Empty<byte>());
        ws.Options.SetRequestHeader("X-Eso-Operator-Timestamp", ts.ToString());
        ws.Options.SetRequestHeader("X-Eso-Operator-Signature", sig);
        var baseUri = new Uri(_cfg.FleetUrl!);
        var wsUri = new UriBuilder(baseUri) { Scheme = baseUri.Scheme == "https" ? "wss" : "ws", Path = path, Query = "" }.Uri;
        await ws.ConnectAsync(wsUri, ct);
        return ws;
    }

    /// <summary>Builds a signed admin relay request (e.g. rotate-token) for one device.</summary>
    public FleetAdminRelayRequest BuildAdminRelay(string code, string action, string payloadJson = "{}")
    {
        var commandId = Guid.NewGuid().ToString("N");
        var issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return new FleetAdminRelayRequest
        {
            Code = code,
            Action = action,
            CommandId = commandId,
            IssuedAt = issuedAt,
            Signature = OperatorSigning.SignAdmin(_operatorKey, action, commandId, issuedAt, payloadJson),
            PayloadJson = payloadJson,
        };
    }

    public void Dispose() => _http.Dispose();
}
