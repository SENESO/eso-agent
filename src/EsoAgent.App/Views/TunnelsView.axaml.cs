using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using EsoAgent.Client;
using EsoAgent.Core;

namespace EsoAgent.App.Views;

/// <summary>One row in the Tunnels list: a forwarder plus its live state.</summary>
public sealed class TunnelItem : INotifyPropertyChanged
{
    public required TunnelForwarder Forwarder { get; init; }

    public string Describe => Forwarder.Spec.Describe();
    public string StateLine
    {
        get
        {
            var state = Forwarder.State switch
            {
                "running" => Strings.T("Running"),
                "failed" => Strings.T("FailedWord"),
                _ => Strings.T("Stopped"),
            };
            var extra = Forwarder.State == "failed" && Forwarder.Error != null
                ? $" · {Forwarder.Error}"
                : Forwarder.State == "running"
                    ? $" · {Forwarder.ActiveConnections} {Strings.T("Connections")}"
                    : "";
            return state + extra;
        }
    }
    public string ToggleLabel => Forwarder.State == "running" ? Strings.T("Stop") : Strings.T("Start");
    public string RemoveLabel => Strings.T("Remove");

    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StateLine)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ToggleLabel)));
    }
}

/// <summary>
/// Tunnels tab (Phase 4) — port forwarding through the selected device,
/// managed as a list instead of a blocking CLI command. Each row is a
/// TunnelForwarder (the same code `eso tunnel` runs): Start binds the
/// local port on this computer, Stop releases it. Tunnels belong to the
/// device they were created under and keep running across tab switches;
/// re-attaching the view to another device shows that device's tunnels.
/// </summary>
public sealed partial class TunnelsView : UserControl
{
    private ClientConfig? _cfg;
    private DeviceItem? _device;
    private readonly Dictionary<string, List<TunnelItem>> _byDevice = new();
    private List<TunnelItem> _current = new();

    public TunnelsView()
    {
        InitializeComponent();
        ApplyLanguage();
    }

    public void Attach(ClientConfig cfg, DeviceItem device)
    {
        _cfg = cfg;
        _device = device;
        var key = DeviceKey(device);
        if (!_byDevice.TryGetValue(key, out var list))
        {
            list = new List<TunnelItem>();
            _byDevice[key] = list;
        }
        _current = list;
        TunnelsList.ItemsSource = _current;
        StateText.Text = "";
        ApplyLanguage();
    }

    public void ApplyLanguage()
    {
        NewButton.Content = Strings.T("NewTunnel");
        foreach (var item in _current) item.Refresh();
    }

    private static string DeviceKey(DeviceItem d) => d.IsFleet ? d.Code! : "direct";

    private string DeviceArg() => _device!.IsFleet ? _device.Code! : $"{_cfg!.Host}:{_cfg.Port}";

    private async void OnNewClick(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner == null || _cfg == null || _device == null) return;
        var spec = await new TunnelEditorWindow().ShowDialog<TunnelSpec?>(owner);
        if (spec == null) return;
        spec.Device = DeviceArg();
        if (_current.Any(t => t.Forwarder.Spec.LocalPort == spec.LocalPort && t.Forwarder.Spec.Udp == spec.Udp))
        {
            StateText.Text = $"Local port {spec.LocalPort} is already used by another tunnel here.";
            return;
        }
        var forwarder = new TunnelForwarder(_cfg, spec);
        var item = new TunnelItem { Forwarder = forwarder };
        forwarder.Changed += () => Dispatcher.UIThread.Post(item.Refresh);
        _current.Add(item);
        TunnelsList.ItemsSource = null;
        TunnelsList.ItemsSource = _current;
        await forwarder.StartAsync();
        StateText.Text = forwarder.State == "running"
            ? $"Tunnel started: {spec.Describe()}"
            : $"Tunnel failed: {forwarder.Error}";
    }

    private async void OnRowToggleClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TunnelItem item }) return;
        if (item.Forwarder.State == "running")
        {
            item.Forwarder.Stop();
            StateText.Text = $"Tunnel stopped: {item.Describe}";
        }
        else
        {
            var ok = await item.Forwarder.StartAsync();
            StateText.Text = ok ? $"Tunnel started: {item.Describe}" : $"Tunnel failed: {item.Forwarder.Error}";
        }
        item.Refresh();
    }

    private void OnRowRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TunnelItem item }) return;
        item.Forwarder.Dispose();
        _current.Remove(item);
        TunnelsList.ItemsSource = null;
        TunnelsList.ItemsSource = _current;
        StateText.Text = $"Tunnel removed: {item.Describe}";
    }
}

/// <summary>The new-tunnel form: local port, fixed target or SOCKS, TCP/UDP.</summary>
public sealed class TunnelEditorWindow : Window
{
    private readonly TextBox _localPortBox = new();
    private readonly TextBox _hostBox = new();
    private readonly TextBox _portBox = new();
    private readonly CheckBox _udpBox = new();
    private readonly CheckBox _socksBox = new();
    private readonly TextBlock _errorText = new();

    public TunnelEditorWindow()
    {
        Title = Strings.T("TunnelEditorTitle");
        Width = 420;
        Height = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _localPortBox.Text = "13389";
        _hostBox.Text = "127.0.0.1";
        _portBox.Text = "3389";
        _udpBox.Content = Strings.T("Udp");
        _socksBox.Content = Strings.T("SocksMode");
        _socksBox.IsCheckedChanged += (_, _) =>
        {
            var socks = _socksBox.IsChecked == true;
            _hostBox.IsEnabled = !socks;
            _portBox.IsEnabled = !socks;
            _udpBox.IsEnabled = !socks;
            if (socks) _udpBox.IsChecked = false;
        };
        _errorText.Foreground = Avalonia.Media.Brushes.OrangeRed;

        // Keyboard pass (Phase 6): Esc cancels; first field focused on open.
        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) Close(null); };
        Opened += (_, _) => _localPortBox.Focus();

        var create = new Button { Content = Strings.T("Start"), HorizontalAlignment = HorizontalAlignment.Right };
        create.Click += OnCreateClick;
        var cancel = new Button { Content = Strings.T("Cancel"), HorizontalAlignment = HorizontalAlignment.Right };
        cancel.Click += (_, _) => Close(null);

        Content = new StackPanel
        {
            Margin = new Thickness(14),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = Strings.T("LocalPort") }, _localPortBox,
                new TextBlock { Text = Strings.T("RemoteHost") }, _hostBox,
                new TextBlock { Text = Strings.T("RemotePort") }, _portBox,
                _udpBox, _socksBox, _errorText,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, create },
                },
            },
        };
    }

    private void OnCreateClick(object? sender, RoutedEventArgs e)
    {
        var socks = _socksBox.IsChecked == true;
        TunnelTarget? target = null;
        if (!socks)
        {
            if (!int.TryParse(_portBox.Text, out var rp) || rp is < 1 or > 65535 || string.IsNullOrWhiteSpace(_hostBox.Text))
            {
                _errorText.Text = "Remote target must be a host and a port 1–65535.";
                return;
            }
            target = new TunnelTarget { Host = _hostBox.Text.Trim(), Port = rp };
        }
        var spec = new TunnelSpec
        {
            LocalPort = int.TryParse(_localPortBox.Text, out var lp) ? lp : 0,
            Device = "", // filled in by the view (the attached device)
            Target = target,
            Socks = socks,
            Udp = _udpBox.IsChecked == true,
        };
        // Validate with a placeholder device; the view substitutes the real one.
        var probe = new TunnelSpec { LocalPort = spec.LocalPort, Device = "probe", Target = spec.Target, Socks = spec.Socks, Udp = spec.Udp };
        if (TunnelSpec.Validate(probe) is { } error)
        {
            _errorText.Text = error;
            return;
        }
        Close(spec);
    }
}
