namespace EsoAgent.Core;

/// <summary>
/// One offline-queued fleet command (v1.1), persisted by the fleet server in
/// queue.json. The operator signed the command with the QUEUED canonical form
/// (OperatorSigning.SignQueuedCommand), whose validity window is the explicit
/// ExpiresAt instead of the ±120 s immediate window — the relay still cannot
/// forge anything: it stores an envelope it cannot alter, and the device
/// verifies the signature itself when the command is finally delivered.
///
/// State: queued → delivered (device answered; result stored) | failed
/// (delivery attempted, device/transport error) | expired (never delivered
/// before ExpiresAt; dropped, never sent late).
/// </summary>
public sealed class QueuedCommandRecord
{
    /// <summary>Server-side queue id — the receipt the caller fetches later.</summary>
    public string Id { get; set; } = "";
    /// <summary>The operator's command id (the signed envelope's id).</summary>
    public string CommandId { get; set; } = "";
    public string DeviceCode { get; set; } = "";
    public string Method { get; set; } = "POST";
    public string Path { get; set; } = "";
    public string BodyBase64 { get; set; } = "";
    public string Signature { get; set; } = "";
    public string Signer { get; set; } = "operator";
    public long IssuedAt { get; set; }
    public long ExpiresAt { get; set; }
    public int TimeoutSeconds { get; set; } = 120;
    public string State { get; set; } = "queued";
    public DateTimeOffset QueuedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public int? HttpStatus { get; set; }
    public string? ResultBodyBase64 { get; set; }
    public string? Error { get; set; }
}

/// <summary>Queue expiry/delivery decisions — pure, unit-tested.</summary>
public static class QueuePolicy
{
    /// <summary>Default queue lifetime: 24 h from the operator's issue time.</summary>
    public const int DefaultTtlSeconds = 24 * 60 * 60;

    public static bool IsExpired(QueuedCommandRecord record, DateTimeOffset now)
        => now.ToUnixTimeSeconds() > record.ExpiresAt;

    /// <summary>A record may be delivered only while still queued and unexpired.</summary>
    public static bool IsDeliverable(QueuedCommandRecord record, DateTimeOffset now)
        => record.State == "queued" && !IsExpired(record, now);
}
