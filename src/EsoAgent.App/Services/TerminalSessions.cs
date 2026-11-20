using EsoAgent.Client;
using EsoAgent.Core;

namespace EsoAgent.App.Services;

/// <summary>
/// App-side state for one device's persistent shell session (Phase 3).
/// The session itself lives on the AGENT (ShellSessionService — it
/// survives any client disconnect by design); what the app keeps here is
/// the attach state: which named session, the output caught up so far
/// (a ShellSessionBuffer, so a reattach replays only what is new), the
/// shell it was created with, and the command history. Switching devices
/// or tabs never tears this down — the view binds and unbinds, the state
/// (and the agent-side shell, with its cwd and environment) stays.
/// </summary>
public sealed class TerminalSessionState
{
    public required string DeviceKey { get; init; }

    /// <summary>The agent-side session name. One app session per device.</summary>
    public string SessionName { get; set; } = "app";

    /// <summary>Shell the session was (or will be) created with.</summary>
    public string Shell { get; set; } = "bash";

    public ShellSessionBuffer Buffer { get; } = new();
    public List<string> History { get; } = new();

    /// <summary>The agent confirmed the session exists (created or found).</summary>
    public bool Created { get; set; }

    /// <summary>The shell process is still alive on the device.</summary>
    public bool Running { get; set; }

    /// <summary>The device has no session service — the view falls back to
    /// one-shot commands for this device (the pre-Phase-3 behaviour).</summary>
    public bool UseFallback { get; set; }

    public string? Note { get; set; }
}

/// <summary>The session states, keyed by device, for the app's lifetime.</summary>
public static class TerminalSessionStore
{
    private static readonly Dictionary<string, TerminalSessionState> _states = new();

    public static TerminalSessionState For(ClientConfig cfg, DeviceItem device)
    {
        var key = device.IsFleet && !string.IsNullOrEmpty(device.Code)
            ? "fleet:" + device.Code
            : $"direct:{cfg.Host}:{cfg.Port}";
        if (!_states.TryGetValue(key, out var state))
        {
            state = new TerminalSessionState
            {
                DeviceKey = key,
                Shell = device.Os.Equals("Windows", StringComparison.OrdinalIgnoreCase)
                    ? "powershell"
                    : "bash",
            };
            _states[key] = state;
        }
        return state;
    }
}
