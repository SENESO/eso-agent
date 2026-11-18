using EsoAgent.Linux;
using Xunit;

namespace EsoAgent.Tests;

/// <summary>
/// §14 unit coverage for the two Linux-desktop helpers behind the §13
/// findings: the consent prompt's mode wording (a lowercase comparison
/// made Control requests prompt as VIEW) and the DPMS capability probe
/// (xset exits 0 even when the X server has no DPMS extension, so the
/// capability must be read from `xset q` output, not the exit code).
/// </summary>
public class LinuxDesktopHelperTests
{
    [Theory]
    [InlineData("Control")]
    [InlineData("control")]
    [InlineData("CONTROL")]
    public void ConsentPromptText_ControlModes_SayControl(string mode)
    {
        var text = LinuxDesktopHost.ConsentPromptText(mode);
        Assert.Contains("CONTROL", text);
        Assert.DoesNotContain("VIEW", text);
    }

    [Theory]
    [InlineData("View")]
    [InlineData("view")]
    [InlineData("VIEW")]
    public void ConsentPromptText_ViewModes_SayView(string mode)
    {
        var text = LinuxDesktopHost.ConsentPromptText(mode);
        Assert.Contains("VIEW", text);
        Assert.DoesNotContain("CONTROL", text);
    }

    private const string XsetQWithDpms = """
        Screen Saver:
          prefer blanking:  yes    allow exposures:  yes
        DPMS (Energy Star):
          Standby: 600    Suspend: 600    Off: 600
          DPMS is Enabled
          Monitor is On
        """;

    private const string XsetQWithoutDpms = """
        Screen Saver:
          prefer blanking:  yes    allow exposures:  yes
        Font Path:
          /usr/share/fonts/X11/misc
        """;

    [Fact]
    public void XsetQueryShowsDpms_TrueWhenDpmsLinePresent()
        => Assert.True(LinuxDesktopSession.XsetQueryShowsDpms(XsetQWithDpms));

    [Fact]
    public void XsetQueryShowsDpms_FalseWhenNoDpmsSection()
        => Assert.False(LinuxDesktopSession.XsetQueryShowsDpms(XsetQWithoutDpms));

    [Fact]
    public void XsetQueryShowsDpms_FalseForTheCocoonErrorAndEmptyOutput()
    {
        // What the DPMS-less cocoon server answers to `xset dpms force off`
        // (while exiting 0) — must never be read as capability.
        Assert.False(LinuxDesktopSession.XsetQueryShowsDpms("server does not have extension for dpms option"));
        Assert.False(LinuxDesktopSession.XsetQueryShowsDpms(""));
    }
}
