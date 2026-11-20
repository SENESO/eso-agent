using Avalonia.Controls;
using Avalonia.Interactivity;
using EsoAgent.Client;

namespace EsoAgent.App.Views;

/// <summary>
/// First-run pairing (Phase 2, replacing the raw 7-field editor as the
/// default path). Three steps: (1) how this device will be used, (2) join
/// a fleet or continue standalone, (3) name this computer → Done.
/// The user never types a hash, a fingerprint, a token, or a file path:
/// the fleet fingerprint is learned from the server's own certificate and
/// confirmed by comparing a short verification code; the operator key is
/// generated in place and only its PUBLIC half is shown, for the fleet
/// owner to add. The raw editor stays one explicit "Advanced" click away,
/// unchanged. Closes with true when the config or the console name changed.
/// </summary>
public sealed partial class PairingWizard : Window
{
    private readonly string _path;
    private readonly AppConfig _existing;

    private int _step = 1;
    private string? _fleetUrl;            // normalized, once probed
    private string? _probedFingerprint;   // learned from the server's certificate
    private bool _fleetVerified;          // the user confirmed the verification code matches
    private string? _keyPath;
    private string? _publicKey;
    private bool _testedOk;
    private int _testedDeviceCount;

    /// <summary>Parameterless ctor for the Avalonia XAML loader; the app always uses the (path, cfg) ctor.</summary>
    public PairingWizard() : this(AppConfig.DefaultPath(), new AppConfig()) { }

    public PairingWizard(string path, AppConfig existing)
    {
        _path = path;
        _existing = existing;
        InitializeComponent();
        ApplyLanguage();

        if (!string.IsNullOrWhiteSpace(existing.FleetUrl))
            FleetAddressBox.Text = existing.FleetUrl;
        var settings = AppSettingsStore.Load(AppSettingsStore.PathFor(path));
        ConsoleNameBox.Text = AppSettingsStore.GetString(settings, "consoleName") ?? Environment.MachineName;

        RoleControlRadio.IsCheckedChanged += OnRoleChanged;
        RoleBothRadio.IsCheckedChanged += OnRoleChanged;
        RoleHostRadio.IsCheckedChanged += OnRoleChanged;
        PairFleetRadio.IsCheckedChanged += OnPairModeChanged;
        PairStandaloneRadio.IsCheckedChanged += OnPairModeChanged;
        FleetAddressBox.TextChanged += OnFleetAddressChanged;
        ShowStep(1);
    }

    private void ApplyLanguage()
    {
        TitleText.Text = Strings.T("WizTitle");
        Step1Heading.Text = Strings.T("WizStep1");
        Step2Heading.Text = Strings.T("WizStep2");
        Step3Heading.Text = Strings.T("WizStep3");
        RoleControlRadio.Content = Strings.T("RoleControl");
        RoleControlDescText.Text = Strings.T("RoleControlDesc");
        RoleBothRadio.Content = Strings.T("RoleBoth");
        RoleBothDescText.Text = Strings.T("RoleBothDesc");
        RoleHostRadio.Content = Strings.T("RoleHost");
        RoleHostDescText.Text = Strings.T("RoleHostDesc");
        HostGuideText.Text = Strings.T("HostGuide");
        PairFleetRadio.Content = Strings.T("PairFleet");
        PairStandaloneRadio.Content = Strings.T("PairStandalone");
        FleetAddressLabel.Text = Strings.T("FleetAddress");
        FleetAddressBox.Watermark = Strings.T("FleetAddressWatermark");
        ProbeButton.Content = Strings.T("Probe");
        VerifyLabelText.Text = Strings.T("VerifyLabel");
        VerifyMatchesButton.Content = Strings.T("VerifyMatches");
        KeyLabelText.Text = Strings.T("KeyLabel");
        CreateKeyButton.Content = Strings.T("CreateKey");
        CopyKeyButton.Content = Strings.T("Copy");
        TestButton.Content = Strings.T("Test");
        StandaloneNoteText.Text = Strings.T("StandaloneNote");
        NameLabelText.Text = Strings.T("NameLabel");
        AdvancedButton.Content = Strings.T("Advanced");
        BackButton.Content = Strings.T("Back");
        NextButton.Content = Strings.T("Next");
        FinishButton.Content = Strings.T("Finish");
        FlowDirection = Strings.IsArabic ? Avalonia.Media.FlowDirection.RightToLeft : Avalonia.Media.FlowDirection.LeftToRight;
    }

    // ---------- step navigation ----------

    private void ShowStep(int step)
    {
        _step = step;
        Step1Panel.IsVisible = step == 1;
        Step2Panel.IsVisible = step == 2;
        Step3Panel.IsVisible = step == 3;
        StepLineText.Text = $"{Strings.T("StepWord")} {step} {Strings.T("OfWord")} 3";
        BackButton.IsEnabled = step > 1;
        NextButton.IsVisible = step < 3;
        FinishButton.IsVisible = step == 3;
        ErrorText.Text = "";
        if (step == 3) SummaryText.Text = BuildSummary();
    }

    private string BuildSummary()
    {
        var role = RoleHostRadio.IsChecked == true ? Strings.T("RoleHost")
            : RoleBothRadio.IsChecked == true ? Strings.T("RoleBoth")
            : Strings.T("RoleControl");
        var mode = PairStandaloneRadio.IsChecked == true
            ? Strings.T("PairStandalone")
            : $"{Strings.T("PairFleet")}: {_fleetUrl ?? FleetAddressBox.Text?.Trim()}";
        var test = PairStandaloneRadio.IsChecked == true ? ""
            : _testedOk ? $" · {Strings.T("TestOkPrefix")}{_testedDeviceCount}{Strings.T("TestOkSuffix")}"
            : "";
        return $"{role} · {mode}{test}";
    }

    private void OnBackClick(object? sender, RoutedEventArgs e) => ShowStep(Math.Max(1, _step - 1));

    private void OnNextClick(object? sender, RoutedEventArgs e)
    {
        if (_step == 2 && PairFleetRadio.IsChecked == true && !_fleetVerified)
        {
            ErrorText.Text = _probedFingerprint == null
                ? Strings.T("ProbeFirst")
                : Strings.T("VerifyFirst");
            return;
        }
        ShowStep(Math.Min(3, _step + 1));
    }

    private void OnRoleChanged(object? sender, RoutedEventArgs e)
        => HostGuideText.IsVisible = RoleHostRadio.IsChecked == true || RoleBothRadio.IsChecked == true;

    private void OnPairModeChanged(object? sender, RoutedEventArgs e)
    {
        var fleet = PairFleetRadio.IsChecked == true;
        FleetPanel.IsVisible = fleet;
        StandaloneNoteText.IsVisible = !fleet;
    }

    // ---------- fleet probing + verification ----------

    private void OnFleetAddressChanged(object? sender, TextChangedEventArgs e)
    {
        // Editing the address after a probe invalidates the learned identity:
        // the fingerprint belongs to the address it was probed from.
        if (_probedFingerprint == null) return;
        if (PairingSupport.NormalizeFleetUrl(FleetAddressBox.Text) == _fleetUrl) return;
        _probedFingerprint = null;
        _fleetUrl = null;
        _fleetVerified = false;
        _testedOk = false;
        VerifyPanel.IsVisible = false;
        KeyPanel.IsVisible = false;
        TestButton.IsEnabled = false;
        ProbeStateText.Text = "";
    }

    private async void OnProbeClick(object? sender, RoutedEventArgs e)
    {
        var url = PairingSupport.NormalizeFleetUrl(FleetAddressBox.Text);
        if (url == null)
        {
            ProbeStateText.Text = Strings.T("ProbeFailedPrefix") + (FleetAddressBox.Text?.Trim() ?? "");
            return;
        }
        ProbeButton.IsEnabled = false;
        ProbeStateText.Text = Strings.T("Probing");
        try
        {
            var fingerprint = await PairingSupport.ProbeFingerprintAsync(url);
            _fleetUrl = url;
            _probedFingerprint = fingerprint;
            _fleetVerified = false;
            _testedOk = false;
            VerificationCodeText.Text = PairingSupport.VerificationCode(fingerprint);
            VerifyPanel.IsVisible = true;
            KeyPanel.IsVisible = false;
            TestButton.IsEnabled = false;
            ProbeStateText.Text = url;
        }
        catch (Exception ex)
        {
            ProbeStateText.Text = Strings.T("ProbeFailedPrefix") + ex.Message;
        }
        finally { ProbeButton.IsEnabled = true; }
    }

    private void OnVerifyMatchesClick(object? sender, RoutedEventArgs e)
    {
        _fleetVerified = true;
        KeyPanel.IsVisible = true;
        ResolveOperatorKey();
    }

    /// <summary>Existing configured key wins; otherwise the keygen default path.
    /// An existing key file is loaded silently; creating one is an explicit click.</summary>
    private void ResolveOperatorKey()
    {
        _keyPath = !string.IsNullOrWhiteSpace(_existing.OperatorKeyFile)
            ? _existing.OperatorKeyFile
            : PairingSupport.DefaultOperatorKeyPath();
        if (File.Exists(_keyPath))
        {
            try
            {
                var (_, pub) = PairingSupport.EnsureOperatorKey(_keyPath);
                ShowPublicKey(pub);
            }
            catch (Exception ex) { KeyStateText.Text = ex.Message; }
        }
        else
        {
            PublicKeyBox.Text = "";
            CreateKeyButton.IsVisible = true;
            KeyStateText.Text = "";
            TestButton.IsEnabled = false;
        }
    }

    private void OnCreateKeyClick(object? sender, RoutedEventArgs e)
    {
        if (_keyPath == null) return;
        try
        {
            var (_, pub) = PairingSupport.EnsureOperatorKey(_keyPath);
            ShowPublicKey(pub);
        }
        catch (Exception ex) { KeyStateText.Text = ex.Message; }
    }

    private void ShowPublicKey(string publicKeyBase64)
    {
        _publicKey = publicKeyBase64;
        PublicKeyBox.Text = publicKeyBase64;
        CreateKeyButton.IsVisible = false;
        KeyStateText.Text = Strings.T("KeyReady");
        TestButton.IsEnabled = true;
    }

    private async void OnCopyKeyClick(object? sender, RoutedEventArgs e)
    {
        if (_publicKey == null) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null) await clipboard.SetTextAsync(_publicKey);
        KeyStateText.Text = Strings.T("Copied");
    }

    private async void OnTestClick(object? sender, RoutedEventArgs e)
    {
        if (_fleetUrl == null || _probedFingerprint == null || _keyPath == null) return;
        TestButton.IsEnabled = false;
        TestStateText.Text = Strings.T("Testing");
        try
        {
            var cfg = new ClientConfig
            {
                Host = _existing.Host,
                Port = _existing.Port,
                FleetUrl = _fleetUrl,
                FleetFingerprint = _probedFingerprint,
                OperatorKeyFile = _keyPath,
            };
            using var fleet = EsoAgent.Client.Program.CreateFleetClient(cfg);
            var devices = await fleet.GetDevicesAsync();
            _testedOk = true;
            _testedDeviceCount = devices.Count;
            TestStateText.Text = $"{Strings.T("TestOkPrefix")}{devices.Count}{Strings.T("TestOkSuffix")}";
        }
        catch (Exception ex)
        {
            _testedOk = false;
            TestStateText.Text = Strings.T("TestFailedPrefix") + ex.Message;
        }
        finally { TestButton.IsEnabled = true; }
    }

    // ---------- finish / advanced ----------

    private void OnFinishClick(object? sender, RoutedEventArgs e)
    {
        var cfg = AppConfig.LoadForEdit(_path);
        if (PairFleetRadio.IsChecked == true && _fleetVerified && _fleetUrl != null && _probedFingerprint != null)
        {
            cfg.FleetUrl = _fleetUrl;
            cfg.FleetFingerprint = _probedFingerprint;
            cfg.OperatorKeyFile = _keyPath;
            if (string.IsNullOrWhiteSpace(cfg.Host) && Uri.TryCreate(_fleetUrl, UriKind.Absolute, out var uri))
                cfg.Host = uri.Host; // ClientConfig.Load requires a host; fleet-only installs point it at the fleet host
            cfg.Save(_path);
        }
        var name = ConsoleNameBox.Text?.Trim();
        if (!string.IsNullOrEmpty(name))
            AppSettingsStore.Update(AppSettingsStore.PathFor(_path), obj => obj["consoleName"] = name);
        Close(true);
    }

    private async void OnAdvancedClick(object? sender, RoutedEventArgs e)
    {
        // The raw editor, unchanged: the wizard steps aside and the shell
        // reloads whatever it saved.
        var dialog = new SettingsWindow(_path, AppConfig.LoadForEdit(_path));
        var saved = await dialog.ShowDialog<bool>(this);
        if (saved) Close(true);
    }
}
