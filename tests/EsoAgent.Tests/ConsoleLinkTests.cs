using EsoAgent.Core;
using Xunit;

namespace EsoAgent.Tests;

/// <summary>
/// The local console link (v1.2.7): the non-secret file the agent publishes
/// in its data dir so the desktop app on the same machine can discover it.
/// Regression coverage for the owner's first-run report — the app showed
/// "Not paired yet", a dash instead of this device's code, and the raw
/// "Client config not found at ..." exception in its footer, on a machine
/// whose agent was enrolled and connected the whole time.
/// </summary>
public class ConsoleLinkTests
{
    private static ConsoleLink Sample() => new()
    {
        DeviceCode = "GY37-HEX3",
        DeviceName = "ESO",
        FleetUrl = "https://100.98.165.106:8740",
        FleetFingerprint = "9b5c0bcc778ebd660d3997f1d9b6dfcc0eae678b1340b3f6d8dfcbe1d0e849c9",
        AgentVersion = "1.2.7",
    };

    [Fact]
    public void Link_WriteRead_RoundTripsAllFields()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "eso-link-" + Guid.NewGuid().ToString("N")));
        try
        {
            ConsoleLink.Write(dir.FullName, Sample());
            var path = ConsoleLink.PathForDataDir(dir.FullName);
            Assert.True(File.Exists(path));
            Assert.Equal("console-link.json", Path.GetFileName(path));

            var read = ConsoleLink.TryRead(path);
            Assert.NotNull(read);
            Assert.Equal("GY37-HEX3", read!.DeviceCode);
            Assert.Equal("ESO", read.DeviceName);
            Assert.Equal("https://100.98.165.106:8740", read.FleetUrl);
            Assert.Equal("9b5c0bcc778ebd660d3997f1d9b6dfcc0eae678b1340b3f6d8dfcbe1d0e849c9", read.FleetFingerprint);
            Assert.Equal("1.2.7", read.AgentVersion);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void Link_TryRead_MissingOrBroken_ReturnsNull()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "eso-link-" + Guid.NewGuid().ToString("N")));
        try
        {
            var path = ConsoleLink.PathForDataDir(dir.FullName);
            Assert.Null(ConsoleLink.TryRead(path)); // absent

            File.WriteAllText(path, "{ not json");
            Assert.Null(ConsoleLink.TryRead(path)); // malformed

            File.WriteAllText(path, """{"deviceName":"ESO"}""");
            Assert.Null(ConsoleLink.TryRead(path)); // no code / fleet URL — not a usable link
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void Link_JsonShape_IsCamelCaseAndCarriesNoSecrets()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "eso-link-" + Guid.NewGuid().ToString("N")));
        try
        {
            ConsoleLink.Write(dir.FullName, Sample());
            var json = File.ReadAllText(ConsoleLink.PathForDataDir(dir.FullName));
            Assert.Contains("\"deviceCode\":\"GY37-HEX3\"", json);
            Assert.Contains("\"fleetUrl\":\"https://100.98.165.106:8740\"", json);
            Assert.Contains("\"fleetFingerprint\":", json);
            Assert.Contains("\"agentVersion\":\"1.2.7\"", json);
            // The link is a discovery aid, never a credential carrier.
            Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("key", json, StringComparison.OrdinalIgnoreCase);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("wss://100.98.165.106:8740/fleet/connect", "https://100.98.165.106:8740")]
    [InlineData("ws://fleet.lan:8740/fleet/connect", "http://fleet.lan:8740")]
    [InlineData("wss://fleet.example.com/fleet/connect", "https://fleet.example.com")]
    [InlineData("https://fleet.example.com:8740", "https://fleet.example.com:8740")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("not a url", null)]
    public void FleetUrl_FromServerUrl_DerivesTheConsoleBase(string? serverUrl, string? expected)
        => Assert.Equal(expected, ConsoleLink.FleetUrlFromServerUrl(serverUrl));

    [Fact]
    public void Discovery_Decide_CoversTheThreeStates()
    {
        var link = Sample();
        Assert.Equal(ConsolePairingState.Paired, ConsoleDiscovery.Decide(hasClientConfig: true, link));
        Assert.Equal(ConsolePairingState.Paired, ConsoleDiscovery.Decide(hasClientConfig: true, null));
        Assert.Equal(ConsolePairingState.LocalAgentFound, ConsoleDiscovery.Decide(hasClientConfig: false, link));
        Assert.Equal(ConsolePairingState.Unpaired, ConsoleDiscovery.Decide(hasClientConfig: false, null));
    }
}
