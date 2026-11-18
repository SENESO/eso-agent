using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EsoAgent.Core;
using Microsoft.Extensions.Hosting;

namespace EsoAgent.Windows;

/// <summary>
/// Hosts the "EsoAgentControlPipe" named pipe — the local-only channel the
/// tray uses for the owner's kill switch and status display. Locking is a
/// local act only: this pipe is the ONLY way to lock/unlock the agent, it is
/// never exposed over the network API.
/// Protocol: one JSON request line {"action":"lock"|"unlock"|"status"},
/// one JSON reply line, always {"ok":true, ...}.
/// </summary>
public sealed class ControlPipeService : BackgroundService
{
    public const string PipeName = "EsoAgentControlPipe";

    private readonly AgentState _state;
    private readonly string _version;

    public ControlPipeService(AgentState state)
    {
        _state = state;
        _version = typeof(ControlPipeService).Assembly.GetName().Version?.ToString() ?? "1.0.0";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(stoppingToken);
                await HandleConnectionAsync(server, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // A broken client must not take the pipe down; loop and listen again.
                try { await Task.Delay(250, stoppingToken); } catch { break; }
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        using var writer = new StreamWriter(server, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true) { AutoFlush = true };

        var line = await reader.ReadLineAsync(ct);
        var reply = new JsonObject { ["ok"] = true };

        string? action = null;
        string? password = null;
        if (!string.IsNullOrWhiteSpace(line))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("action", out var a)) action = a.GetString();
                if (doc.RootElement.TryGetProperty("password", out var pw)) password = pw.GetString();
            }
            catch (JsonException) { /* fall through: reply ok:false below */ }
        }

        switch (action)
        {
            case "lock":
                _state.KillSwitch.Lock();
                _state.Audit.Write("control", "agent locked from the tray (kill switch)", "ok");
                reply["locked"] = true;
                break;
            case "unlock":
                // Resuming re-enables remote access = an owner act: the tray
                // must prove the owner password (item 42). Locking stays
                // one-click — pausing is the safe direction.
                if (_state.OwnerAuth.IsConfigured && !_state.OwnerAuth.Verify(password ?? ""))
                {
                    _state.Audit.Write("control", "tray unlock refused (owner password missing/wrong)", "denied");
                    reply["ok"] = false;
                    reply["error"] = "Owner password required to resume.";
                    reply["locked"] = true;
                    break;
                }
                _state.KillSwitch.Unlock();
                _state.Audit.Write("control", "agent resumed from the tray (owner password verified)", "ok");
                reply["locked"] = false;
                break;
            case "status":
                reply["locked"] = _state.KillSwitch.IsLocked;
                reply["lastCommand"] = _state.LastCommandSummary;
                reply["version"] = _version;
                reply["deviceName"] = _state.Config.ResolvedDeviceName;
                reply["deviceCode"] = _state.Device?.Code;
                reply["meshIp"] = _state.MeshVirtualIp;
                reply["fleetConnected"] = _state.FleetConnected;
                reply["fleetPanelUrl"] = string.IsNullOrWhiteSpace(_state.Config.Fleet.ServerUrl)
                    ? null : _state.Config.Fleet.ServerUrl.TrimEnd('/') + "/panel/";
                reply["ownerSessionActive"] = (_state.PanelSessions?.ActiveCount ?? 0) > 0;
                reply["updateAvailable"] = _state.ReleaseUpdates.AvailableVersion;
                break;
            default:
                reply["ok"] = false;
                reply["error"] = "Unknown action. Use lock, unlock or status.";
                break;
        }

        await writer.WriteLineAsync(reply.ToJsonString(EsoJson.Options).AsMemory(), ct);
        try { server.Disconnect(); } catch { /* client may already be gone */ }
    }
}
