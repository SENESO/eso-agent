using EsoAgent.Core;
using Xunit;

namespace EsoAgent.Tests;

/// <summary>
/// App correctness lane (2026-11-22): the pure decisions behind the
/// owner's four live verdicts — self-device exclusion (defect 1), the
/// desktop session state machine (defect 2), and the THIS DEVICE card
/// (defects 4 + 7, tonight's incident). The App itself has no test seam
/// (the test project cannot reference the Avalonia executable), so the
/// decisions live in Core and are pinned here; the views only render
/// what these types decide.
/// </summary>
public class SelfDeviceTests
{
    [Fact]
    public void IsSelf_MatchesCodesCaseInsensitively()
    {
        Assert.True(SelfDevice.IsSelf("GY37-HEX3", "GY37-HEX3"));
        Assert.True(SelfDevice.IsSelf("gy37-hex3", "GY37-HEX3"));
    }

    [Fact]
    public void IsSelf_NullOrEmpty_IsNeverSelf()
    {
        Assert.False(SelfDevice.IsSelf(null, "GY37-HEX3"));   // the direct device has no code
        Assert.False(SelfDevice.IsSelf("GY37-HEX3", null));   // self unknown → exclude nothing
        Assert.False(SelfDevice.IsSelf("", ""));
        Assert.False(SelfDevice.IsSelf("8AY7-DDZ1", "GY37-HEX3"));
    }

    [Fact]
    public void ExcludingSelf_RemovesOnlyThisMachine()
    {
        var items = new[] { "GY37-HEX3", "8AY7-DDZ1", null, "ABCD-1234" };
        var kept = SelfDevice.ExcludingSelf(items, code => code, "GY37-HEX3");
        Assert.Equal(new[] { "8AY7-DDZ1", null, "ABCD-1234" }, kept);
    }
}

public class IdentityCardTests
{
    private static ConsoleLink Link() => new()
    {
        DeviceCode = "GY37-HEX3",
        DeviceName = "ESO",
        FleetUrl = "https://100.98.165.106:8740",
    };

    [Fact]
    public void Incident_PairedConsole_LocalAgent_FleetDown_CodeStillShows_NoPair()
    {
        // 2026-11-22 22:20: the fleet was briefly unreachable; the card
        // showed "—", claimed "not itself an enrolled device" and offered
        // Pair. The console-link said GY37-HEX3 the whole time.
        var state = IdentityCard.Resolve(
            hasClientConfig: true, localLink: Link(),
            fleetSelfCode: null, fleetSelfName: null, fleetSelfOnline: false,
            fleetReachable: false, awaitingApproval: false);

        Assert.Equal("GY37-HEX3", state.Code);
        Assert.Equal("ESO", state.Name);
        Assert.Equal(IdentityDetail.Reconnecting, state.Detail);
        Assert.False(state.ShowPair);
        Assert.False(state.ShowUseFleet);
    }

    [Fact]
    public void PairedConsole_LocalAgent_FleetUp_ShowsEnrolled()
    {
        var state = IdentityCard.Resolve(true, Link(), null, null, false,
            fleetReachable: true, awaitingApproval: false);
        Assert.Equal("GY37-HEX3", state.Code);
        Assert.Equal(IdentityDetail.LocalAgentEnrolled, state.Detail);
        Assert.False(state.ShowPair);
    }

    [Fact]
    public void UnpairedConsole_LocalAgent_OffersAdoptionNotPairing()
    {
        var state = IdentityCard.Resolve(false, Link(), null, null, false,
            fleetReachable: false, awaitingApproval: false);
        Assert.Equal("GY37-HEX3", state.Code);
        Assert.Equal(IdentityDetail.LocalAgentEnrolled, state.Detail);
        Assert.True(state.ShowUseFleet);
        Assert.False(state.ShowPair);
    }

    [Fact]
    public void GenuineFirstRun_NoConfigNoAgent_OffersPair()
    {
        var state = IdentityCard.Resolve(false, null, null, null, false,
            fleetReachable: false, awaitingApproval: false);
        Assert.Null(state.Code);
        Assert.Equal(IdentityDetail.NotPaired, state.Detail);
        Assert.True(state.ShowPair);
    }

    [Fact]
    public void PairedConsole_NoLocalAgent_FleetDown_IsReconnectingNeverUnenrolled()
    {
        var state = IdentityCard.Resolve(true, null, null, null, false,
            fleetReachable: false, awaitingApproval: false);
        Assert.Null(state.Code);
        Assert.Equal(IdentityDetail.Reconnecting, state.Detail);
        Assert.False(state.ShowPair);
    }

    [Fact]
    public void PairedConsole_NoLocalAgent_FleetAnswered_ConsoleOnlyIsAFact()
    {
        var state = IdentityCard.Resolve(true, null, null, null, false,
            fleetReachable: true, awaitingApproval: false);
        Assert.Equal(IdentityDetail.ConsoleOnly, state.Detail);
        Assert.False(state.ShowPair);
    }

    [Fact]
    public void AwaitingApproval_NoLocalAgent_ShowsAwaiting()
    {
        var state = IdentityCard.Resolve(true, null, null, null, false,
            fleetReachable: true, awaitingApproval: true);
        Assert.Equal(IdentityDetail.AwaitingApproval, state.Detail);
    }

    [Fact]
    public void FleetRowForThisDevice_WinsOverTheLink_AndCarriesLiveState()
    {
        var online = IdentityCard.Resolve(true, Link(), "GY37-HEX3", "ESO", fleetSelfOnline: true,
            fleetReachable: true, awaitingApproval: false);
        Assert.Equal(IdentityDetail.SelfOnline, online.Detail);
        Assert.Equal("GY37-HEX3", online.Code);

        var offline = IdentityCard.Resolve(true, Link(), "GY37-HEX3", "ESO", fleetSelfOnline: false,
            fleetReachable: true, awaitingApproval: false);
        Assert.Equal(IdentityDetail.SelfOffline, offline.Detail);
    }
}

public class DesktopSessionTrackerTests
{
    [Fact]
    public void Lifecycle_IdleConnectingConnectedDisconnectingIdle()
    {
        var tracker = new DesktopSessionTracker();
        Assert.Equal(DesktopSessionPhase.Idle, tracker.Phase);

        Assert.True(tracker.BeginConnect("8AY7-DDZ1"));
        Assert.Equal(DesktopSessionPhase.Connecting, tracker.Phase);
        Assert.True(tracker.IsActiveFor("8ay7-ddz1", DesktopSessionPhase.Connecting));

        Assert.True(tracker.MarkConnected());
        Assert.Equal(DesktopSessionPhase.Connected, tracker.Phase);

        Assert.True(tracker.BeginDisconnect());
        Assert.Equal(DesktopSessionPhase.Disconnecting, tracker.Phase);

        tracker.MarkDisconnected();
        Assert.Equal(DesktopSessionPhase.Idle, tracker.Phase);
        Assert.Null(tracker.DeviceCode);
    }

    [Fact]
    public void BeginConnect_RefusedWhileASessionIsInFlight()
    {
        var tracker = new DesktopSessionTracker();
        Assert.True(tracker.BeginConnect("AAAA-1111"));
        Assert.False(tracker.BeginConnect("BBBB-2222"));
        Assert.Equal("AAAA-1111", tracker.DeviceCode);
    }

    [Fact]
    public void ConnectFailure_ReturnsToIdleAndClearsTheDevice()
    {
        var tracker = new DesktopSessionTracker();
        tracker.BeginConnect("8AY7-DDZ1");
        Assert.True(tracker.MarkConnectFailed());
        Assert.Equal(DesktopSessionPhase.Idle, tracker.Phase);
        Assert.Null(tracker.DeviceCode);
        Assert.False(tracker.IsActive);
    }

    [Fact]
    public void InvalidTransitions_AreNoOps()
    {
        var tracker = new DesktopSessionTracker();
        Assert.False(tracker.MarkConnected());     // not connecting
        Assert.False(tracker.MarkConnectFailed()); // not connecting
        Assert.False(tracker.BeginDisconnect());   // nothing to disconnect
        Assert.False(tracker.MarkSessionEnded());  // nothing live
        Assert.Equal(DesktopSessionPhase.Idle, tracker.Phase);
    }

    [Fact]
    public void SessionEnded_FromConnected_Idles_AndStaleEventsCannotKillANewSession()
    {
        var tracker = new DesktopSessionTracker();
        tracker.BeginConnect("8AY7-DDZ1");
        tracker.MarkConnected();
        Assert.True(tracker.MarkSessionEnded());
        Assert.Equal(DesktopSessionPhase.Idle, tracker.Phase);

        // A new session starts; the OLD session's late end event arrives.
        tracker.BeginConnect("CCCC-3333");
        Assert.False(tracker.MarkSessionEnded()); // Connecting, not Connected → ignored
        Assert.Equal(DesktopSessionPhase.Connecting, tracker.Phase);
        Assert.Equal("CCCC-3333", tracker.DeviceCode);
    }

    [Fact]
    public void Changed_FiresOnEveryAcceptedTransition()
    {
        var tracker = new DesktopSessionTracker();
        var fired = 0;
        tracker.Changed += () => fired++;
        tracker.BeginConnect("8AY7-DDZ1");
        tracker.MarkConnected();
        tracker.BeginDisconnect();
        tracker.MarkDisconnected();
        Assert.Equal(4, fired);
    }
}

public class DesktopSessionTextTests
{
    [Theory]
    [InlineData("Session closed by the agent.", "Session closed by the agent.")]
    [InlineData("Disconnected.", "Disconnected.")]
    [InlineData("  agent closed: user logged off  ", "agent closed: user logged off")]
    [InlineData("Session error: A connection attempt failed because the connected party did not properly respond", null)]
    [InlineData("session error: socket reset", null)]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void SanitizeEndReason_KeepsPlainReasons_DropsRawExceptions(string? input, string? expected)
        => Assert.Equal(expected, DesktopSessionText.SanitizeEndReason(input));
}
