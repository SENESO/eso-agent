using EsoAgent.Core;
using Xunit;

namespace EsoAgent.Tests;

/// <summary>
/// §14 regression coverage for the §M finding: AppBridgeService.Find used to
/// consult ONLY the device config's appCommands, so the 21 shipped presets
/// could not be run by name on a stock device ("No app-command named
/// 'net-port'…"). Find now falls back to AppCommandPresets.All, with the
/// device config winning on a name collision.
/// </summary>
public class AppBridgePresetTests
{
    private static AgentState StateWithConfig(Action<AgentConfig>? mutate = null)
    {
        var cfg = new AgentConfig();
        mutate?.Invoke(cfg);
        // Find reads only state.Config; the remaining required members are
        // inert stand-ins (never touched by Find).
        return new AgentState
        {
            Config = cfg,
            Token = new byte[32],
            Audit = null!,
            KillSwitch = null!,
            Platform = null!,
            Files = null!,
            Update = null!,
        };
    }

    [Fact]
    public void Find_ResolvesPresetByName_OnStockConfig()
    {
        var state = StateWithConfig();
        var entry = AppBridgeService.Find(state, "net-port");
        Assert.NotNull(entry);
        Assert.Equal("net-port", entry!.Name);
        Assert.Equal("bash", entry.Executable);
    }

    [Fact]
    public void Find_PresetLookup_IsCaseInsensitive()
    {
        var state = StateWithConfig();
        Assert.NotNull(AppBridgeService.Find(state, "NET-PORT"));
        Assert.NotNull(AppBridgeService.Find(state, "DotNet-Build"));
    }

    [Fact]
    public void Find_AllShippedPresetsResolve_OnStockConfig()
    {
        var state = StateWithConfig();
        Assert.Equal(21, AppCommandPresets.All.Count);
        foreach (var preset in AppCommandPresets.All)
            Assert.NotNull(AppBridgeService.Find(state, preset.Name));
    }

    [Fact]
    public void Find_ConfigEntry_OverridesPresetWithSameName()
    {
        var custom = new AppCommandConfig
        {
            Name = "net-port",
            Description = "owner override",
            Executable = "ncat",
            ArgsTemplate = "-z {host} {port}",
        };
        var state = StateWithConfig(cfg => cfg.AppCommands.Add(custom));
        var entry = AppBridgeService.Find(state, "net-port");
        Assert.Same(custom, entry);
        Assert.Equal("ncat", entry!.Executable);
    }

    [Fact]
    public void Find_ConfigEntry_ResolvesAsBefore()
    {
        var custom = new AppCommandConfig { Name = "my-tool", Executable = "mytool", ArgsTemplate = "go" };
        var state = StateWithConfig(cfg => cfg.AppCommands.Add(custom));
        Assert.Same(custom, AppBridgeService.Find(state, "MY-TOOL"));
    }

    [Fact]
    public void Find_UnknownName_ReturnsNull()
    {
        var state = StateWithConfig();
        Assert.Null(AppBridgeService.Find(state, "no-such-command"));
    }
}
