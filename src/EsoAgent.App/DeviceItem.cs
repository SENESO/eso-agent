using EsoAgent.Core;

namespace EsoAgent.App;

/// <summary>One row in the devices list: a fleet device, or the single direct device from the config.</summary>
public sealed class DeviceItem
{
    public string? Code { get; private init; }          // null = the direct config device
    public required string Name { get; init; }
    public string Os { get; private init; } = "";
    public string State { get; private init; } = "";    // Pending | Approved | Rejected (fleet only)
    public bool Online { get; private init; }
    public string? VirtualIp { get; private init; }
    public List<string> Ips { get; private init; } = new();
    public List<string> Groups { get; private init; } = new();

    /// <summary>Latest doctor snapshot overall (ok|warn|fail) from the fleet health cache; null = unknown.</summary>
    public string? HealthOverall { get; set; }
    public bool ShowHealthWarn => HealthOverall == "warn";
    public bool ShowHealthFail => HealthOverall == "fail";

    public bool IsFleet => Code != null;
    public bool IsPending => State == "Pending";

    public string Title => IsFleet ? $"{Name}  ·  {Code}" : $"{Name}  ·  direct";
    public string StatusLine => Online ? "Online" : "Offline";
    public string DetailLine
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(Os)) parts.Add(Os);
            if (VirtualIp != null) parts.Add($"mesh {VirtualIp}");
            if (Groups.Count > 0) parts.Add(string.Join(", ", Groups));
            return string.Join("  ·  ", parts);
        }
    }

    public static DeviceItem FromFleet(FleetDeviceDto d) => new()
    {
        Code = d.Code,
        Name = d.Name,
        Os = d.Os,
        State = d.State,
        Online = d.Online,
        VirtualIp = d.VirtualIp,
        Ips = d.Ips,
        Groups = d.Groups,
    };

    /// <summary>The direct-mode fallback: the one device the config's host/token/fingerprint describe.</summary>
    public static DeviceItem Direct(string host) => new()
    {
        Code = null,
        Name = host,
        Os = "",
        Online = false, // unknown until a status call succeeds; the Status tab tells the truth
    };
}
