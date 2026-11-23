using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using EsoAgent.Client;
using EsoAgent.Core;

namespace EsoAgent.App.Views;

/// <summary>Item 41 in the app: mint access codes for the selected fleet
/// device, see its codes + live grants, revoke instantly. Fleet-only —
/// codes live on the fleet server by design.</summary>
public sealed partial class AccessView : UserControl
{
    private ClientConfig? _cfg;
    private DeviceItem? _device;

    public AccessView() => InitializeComponent();

    public void Attach(ClientConfig cfg, DeviceItem device)
    {
        _cfg = cfg;
        _device = device;
        CreatedCodeBox.Text = "";
        RevokeBox.Text = "";
        BodyText.Text = device.IsFleet
            ? "Press “Refresh” to list this device's codes and live grants."
            : "Access codes live on the fleet server — select a fleet device.";
        StateText.Text = "";
    }

    private async void OnCreateClick(object? sender, RoutedEventArgs e)
    {
        if (_cfg == null || _device is not { IsFleet: true }) { StateText.Text = "Fleet devices only."; return; }
        CreateButton.IsEnabled = false;
        try
        {
            var scope = new GrantScope
            {
                Exec = ExecCheck.IsChecked == true,
                Files = FilesCheck.IsChecked == true,
                Clipboard = ClipboardCheck.IsChecked == true,
                Tunnels = TunnelsCheck.IsChecked == true,
                DesktopControl = DesktopCheck.IsChecked == true,
                DesktopViewOnly = ViewOnlyCheck.IsChecked == true,
            };
            var minutes = int.TryParse(MinutesBox.Text, out var m) ? m : 60;
            int? uses = int.TryParse(UsesBox.Text, out var u) ? u : null;
            using var fleet = EsoAgent.Client.Program.CreateFleetClient(_cfg);
            var created = await fleet.CreateAccessCodeAsync(new AccessCodeCreateRequest
            {
                DeviceCode = _device.Code ?? "", Scope = scope, ExpiresMinutes = minutes, MaxUses = uses,
            });
            CreatedCodeBox.Text = created.Code;
            StateText.Text = $"Code for {_device.Name} — expires {created.ExpiresAt.LocalDateTime:g}. Copy it now: it is shown once.";
            await LoadAsync();
        }
        catch (Exception) { StateText.Text = Strings.T("ActionFailedPlain"); }
        finally { CreateButton.IsEnabled = true; }
    }

    private async void OnRevokeClick(object? sender, RoutedEventArgs e)
    {
        var id = RevokeBox.Text?.Trim() ?? "";
        if (_cfg == null || id.Length == 0) { StateText.Text = "Paste a code or grant id first."; return; }
        try
        {
            using var fleet = EsoAgent.Client.Program.CreateFleetClient(_cfg);
            await fleet.RevokeAccessAsync(id);
            StateText.Text = $"Revoked {id} — effective immediately.";
            RevokeBox.Text = "";
            await LoadAsync();
        }
        catch (Exception) { StateText.Text = Strings.T("ActionFailedPlain"); }
    }

    private async void OnRefreshClick(object? sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        if (_cfg == null || _device is not { IsFleet: true }) return;
        try
        {
            using var fleet = EsoAgent.Client.Program.CreateFleetClient(_cfg);
            var codes = await fleet.GetAccessCodesAsync(_device.Code);
            var grants = await fleet.GetAccessGrantsAsync(_device.Code);
            var sb = new StringBuilder();
            sb.AppendLine("CODES (full id — use it to revoke):");
            if (codes.Count == 0) sb.AppendLine("  (none)");
            foreach (var c in codes)
                sb.AppendLine($"  {c.Id}  exp {c.ExpiresAt.LocalDateTime:g} · used {c.Uses}{(c.MaxUses != null ? "/" + c.MaxUses : "")}{(c.Revoked ? " · REVOKED" : "")}{(c.Expired ? " · EXPIRED" : "")} · {c.Scope.Describe()}");
            sb.AppendLine();
            sb.AppendLine("LIVE GRANTS:");
            if (grants.Count == 0) sb.AppendLine("  (none)");
            foreach (var g in grants)
                sb.AppendLine($"  {g.Id}  from {g.RedeemedFrom} · {g.Actions} action(s) · exp {g.ExpiresAt.LocalDateTime:g} · {(g.Revoked ? "REVOKED" : g.Active ? "ACTIVE" : "expired")} · {g.Scope.Describe()}");
            BodyText.Text = sb.ToString();
            StateText.Text = $"Updated {DateTime.Now:T}";
        }
        catch (Exception) { StateText.Text = Strings.T("LoadFailedPlain"); }
    }
}
