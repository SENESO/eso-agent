using EsoAgent.Core;
using Xunit;

namespace EsoAgent.Tests;

/// <summary>
/// Zero-config first run (v1.2.8). The hub ships as the default in
/// FleetOptions and in the installers' seeded config, so a fresh install
/// enrolls on first run and shows its device code with no manual step —
/// it lands as Pending, and approval still happens in the panel as
/// before. These tests pin the defaults and the rule that an explicit
/// esoagent.json always wins over them (including opting out entirely).
/// </summary>
public sealed class FleetDefaultsTests
{
    [Fact]
    public void FreshConfig_EnrollsAgainstTheShippedHub()
    {
        var fleet = new AgentConfig().Fleet;

        Assert.True(fleet.Enabled);
        Assert.Equal(FleetDefaults.ServerUrl, fleet.ServerUrl);
        Assert.Equal(FleetDefaults.ServerFingerprint, fleet.ServerFingerprint);
        // The hub endpoint is the fleet server the current installs use.
        Assert.Equal("wss://100.98.165.106:8740/fleet/connect", fleet.ServerUrl);
    }

    [Fact]
    public void Load_NoConfigFile_KeepsTheHubDefaults()
    {
        var dir = NewTempDir();
        try
        {
            var cfg = AgentConfig.Load(dir);

            Assert.True(cfg.Fleet.Enabled);
            Assert.Equal(FleetDefaults.ServerUrl, cfg.Fleet.ServerUrl);
            Assert.Equal(FleetDefaults.ServerFingerprint, cfg.Fleet.ServerFingerprint);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Load_ConfigWithoutAFleetSection_KeepsTheHubDefaults()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "esoagent.json"), "{ \"port\": 9999 }");

            var cfg = AgentConfig.Load(dir);

            Assert.Equal(9999, cfg.Port);
            Assert.True(cfg.Fleet.Enabled);
            Assert.Equal(FleetDefaults.ServerUrl, cfg.Fleet.ServerUrl);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Load_PartialFleetSection_FillsTheRestFromTheDefaults()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "esoagent.json"),
                "{ \"fleet\": { \"serverUrl\": \"wss://example:8740/fleet/connect\" } }");

            var cfg = AgentConfig.Load(dir);

            Assert.Equal("wss://example:8740/fleet/connect", cfg.Fleet.ServerUrl);
            Assert.True(cfg.Fleet.Enabled);
            Assert.Equal(FleetDefaults.ServerFingerprint, cfg.Fleet.ServerFingerprint);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Load_ExplicitOptOut_WinsOverTheDefaults()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "esoagent.json"),
                "{ \"fleet\": { \"enabled\": false, \"serverUrl\": \"wss://example:8740/fleet/connect\", \"serverFingerprint\": \"\" } }");

            var cfg = AgentConfig.Load(dir);

            Assert.False(cfg.Fleet.Enabled);
            Assert.Equal("wss://example:8740/fleet/connect", cfg.Fleet.ServerUrl);
            Assert.Equal("", cfg.Fleet.ServerFingerprint);
        }
        finally { TryDelete(dir); }
    }

    private static string NewTempDir()
        => Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "eso-fleetdefaults-" + Guid.NewGuid().ToString("N"))).FullName;

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { }
    }
}
