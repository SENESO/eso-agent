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

    /// <summary>Pinned to the top of the list by the operator (app-local state, persisted by the shell).</summary>
    public bool IsFavourite { get; set; }
    public string StarText => IsFavourite ? "★" : "☆";

    /// <summary>A desktop session can be started on approved fleet devices and on the direct device.
    /// Pending devices get the Approve action instead; rejected devices get neither.</summary>
    public bool CanConnect => !IsFleet || State == "Approved";

    /// <summary>Live desktop-session phase for THIS row's device, mirrored
    /// from the session tracker by the shell on every transition (Idle for
    /// every device but the session's own). Drives the row's action: the
    /// Connect button is a state display, not a decoration (defect-2).</summary>
    public DesktopSessionPhase SessionPhase { get; set; } = DesktopSessionPhase.Idle;

    /// <summary>Row action labels, read from the string table when the row is
    /// rendered (the shell re-renders rows on a language switch).</summary>
    public string ConnectLabel => SessionPhase switch
    {
        DesktopSessionPhase.Connecting => Strings.T("Connecting"),
        DesktopSessionPhase.Connected => Strings.T("Disconnect"),
        DesktopSessionPhase.Disconnecting => Strings.T("Disconnecting"),
        _ => Strings.T("Connect"),
    };
    public string ApproveLabel => Strings.T("Approve");
    public string PendingBadgeLabel => Strings.T("PendingBadge");
    public string HealthWarnBadgeLabel => Strings.T("HealthWarnBadge");
    public string HealthFailBadgeLabel => Strings.T("HealthFailBadge");

    /// <summary>The row action is live in every phase except mid-disconnect:
    /// while Connecting it acts as cancel, while Connected as Disconnect.</summary>
    public bool ConnectActionEnabled => SessionPhase != DesktopSessionPhase.Disconnecting;

    public string Title => IsFleet ? $"{Name}  ·  {Code}" : Name;
    public string StatusLine => Strings.T(Online ? "Online" : "Offline");
    public string DetailLine
    {
        get
        {
            // Names and plain facts only — network addresses (the mesh IP
            // included) are diagnostics, not display (defect-9).
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(Os)) parts.Add(Os);
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

    /// <summary>The direct-mode fallback: the one device the config's host/token/fingerprint describe.
    /// Displayed by its plain name, never by its raw address (defect-9) —
    /// the address stays in the config and the diagnostics.</summary>
    public static DeviceItem Direct(string host) => new()
    {
        Code = null,
        Name = Strings.T("DirectDevice"),
        Os = "",
        Online = false, // unknown until a status call succeeds; the Status tab tells the truth
    };
}
