using Avalonia.Controls;
using Avalonia.Interactivity;

namespace EsoAgent.App.Views;

/// <summary>Edits the shared client.json (see AppConfig). Save validates, writes the file, closes with true.</summary>
public sealed partial class SettingsWindow : Window
{
    private readonly string _path;

    /// <summary>Parameterless ctor for the Avalonia XAML loader; the app always uses the (path, cfg) ctor.</summary>
    public SettingsWindow() : this(AppConfig.DefaultPath(), new AppConfig()) { }

    public SettingsWindow(string path, AppConfig cfg)
    {
        _path = path;
        InitializeComponent();
        PathText.Text = path;
        HostBox.Text = cfg.Host;
        PortBox.Text = cfg.Port.ToString();
        FingerprintBox.Text = cfg.Fingerprint;
        TokenBox.Text = cfg.Token;
        FleetUrlBox.Text = cfg.FleetUrl ?? "";
        FleetFingerprintBox.Text = cfg.FleetFingerprint ?? "";
        OperatorKeyBox.Text = cfg.OperatorKeyFile ?? "";
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var host = HostBox.Text?.Trim() ?? "";
        if (host.Length == 0) { ErrorText.Text = "Device host is required."; return; }
        if (!int.TryParse(PortBox.Text?.Trim(), out var port) || port is < 1 or > 65535)
        { ErrorText.Text = "Device port must be a number between 1 and 65535."; return; }
        var token = TokenBox.Text?.Trim() ?? "";
        if (token.Length > 0)
        {
            try { Convert.FromBase64String(token); }
            catch { ErrorText.Text = "Device token is not valid base64."; return; }
        }
        var fleetUrl = FleetUrlBox.Text?.Trim();
        if (!string.IsNullOrEmpty(fleetUrl) && !Uri.TryCreate(fleetUrl, UriKind.Absolute, out _))
        { ErrorText.Text = "Fleet URL is not a valid absolute URL."; return; }

        new AppConfig
        {
            Host = host,
            Port = port,
            Fingerprint = FingerprintBox.Text?.Trim() ?? "",
            Token = token,
            FleetUrl = string.IsNullOrEmpty(fleetUrl) ? null : fleetUrl,
            FleetFingerprint = NullIfEmpty(FleetFingerprintBox.Text),
            OperatorKeyFile = NullIfEmpty(OperatorKeyBox.Text),
        }.Save(_path);
        Close(true);
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
