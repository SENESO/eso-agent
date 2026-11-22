using EsoAgent.Core;
using EsoAgent.Linux;
using Xunit;

namespace EsoAgent.Tests;

/// <summary>
/// Phase 5 consent v2: the prompt must name the right requester for every
/// origin/signer combination, and clipboard permission must follow the
/// mode (control sessions sync the clipboard; view sessions never get
/// clipboard controls, so the prompt must not claim it).
/// </summary>
public class ConsentRequestTests
{
    [Theory]
    [InlineData("panel", null, "the owner dashboard on this device")]
    [InlineData("api", null, "a direct connection (the EsoAgent app or CLI)")]
    [InlineData("fleet", "operator", "the operator console")]
    [InlineData("fleet", "owner-panel", "the fleet owner panel")]
    [InlineData("fleet", null, "the fleet server")]
    [InlineData("fleet", "someone-else", "the fleet server")]
    [InlineData("carrier-pigeon", null, "a remote operator")]
    public void DescribeRequester_MapsOriginAndSigner(string origin, string? signer, string expected)
        => Assert.Equal(expected, ConsentRequest.DescribeRequester(origin, signer));

    [Fact]
    public void For_ControlMode_ClaimsClipboard()
    {
        var request = ConsentRequest.For("Control", "fleet", "operator");
        Assert.True(request.Clipboard);
        Assert.Equal("the operator console", request.Requester);
    }

    [Fact]
    public void For_ViewMode_NeverClaimsClipboard()
    {
        var request = ConsentRequest.For("View", "panel", null);
        Assert.False(request.Clipboard);
    }

    [Fact]
    public void LinuxPrompt_NamesTheRequester()
    {
        var text = LinuxDesktopHost.ConsentPromptText("Control", "the operator console");
        Assert.Contains("The operator console", text);
        Assert.Contains("CONTROL", text);
        Assert.Contains("clipboard", text);
    }

    [Fact]
    public void LinuxPrompt_AnonymousFallbackStillWorks()
    {
        var text = LinuxDesktopHost.ConsentPromptText("View", null);
        Assert.Contains("A remote operator", text);
        Assert.Contains("VIEW", text);
    }
}
