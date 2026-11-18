using EsoAgent.Core;
using Xunit;

namespace EsoAgent.Tests;

public class ParserTests
{
    [Fact]
    public void Winget_FixedWidthTable_WithAvailableColumn()
    {
        string Row(string name, string id, string version, string available, string source)
            => name.PadRight(22) + id.PadRight(21) + version.PadRight(13) + available.PadRight(10) + source;
        var output = string.Join("\n",
            Row("Name", "Id", "Version", "Available", "Source"),
            new string('-', 90),
            Row("Git", "Git.Git", "2.45.1", "", "winget"),
            Row("Notepad++", "Notepad++.Notepad++", "8.6.7", "8.6.8", "winget"));
        var parsed = Parsers.ParseWingetList(output);
        Assert.Equal(2, parsed.Count);
        Assert.Equal("Git", parsed[0].Name);
        Assert.Equal("Git.Git", parsed[0].Id);
        Assert.Equal("2.45.1", parsed[0].Version);
        Assert.Equal("Notepad++", parsed[1].Name);
        Assert.Equal("8.6.7", parsed[1].Version); // installed version, not the Available one
    }

    [Fact]
    public void Winget_NoHeader_ReturnsEmpty()
        => Assert.Empty(Parsers.ParseWingetList("random output\nnothing tabular"));

    [Fact]
    public void Dpkg_TabFormat_SkipsNotInstalled()
    {
        var output = "bash\t5.2.21-2ubuntu4\t1229\tinstall ok installed\n" +
                     "oldpkg\t1.0\t10\tdeinstall ok config-files\n" +
                     "curl\t8.5.0-2ubuntu10\t523\tinstall ok installed\n";
        var parsed = Parsers.ParseDpkgList(output);
        Assert.Equal(2, parsed.Count);
        Assert.Equal("bash", parsed[0].Name);
        Assert.Equal("5.2.21-2ubuntu4", parsed[0].Version);
        Assert.Equal(1229L * 1024, parsed[0].SizeBytes);
        Assert.Equal("curl", parsed[1].Name);
    }

    [Fact]
    public void Rpm_TabFormat()
    {
        var parsed = Parsers.ParseRpmList("bash\t5.2.15-3.fc40\t7891234\tThe GNU Bourne Again shell\n");
        var pkg = Assert.Single(parsed);
        Assert.Equal("bash", pkg.Name);
        Assert.Equal("5.2.15-3.fc40", pkg.Version);
        Assert.Equal(7891234L, pkg.SizeBytes);
    }

    [Fact]
    public void Pacman_NameVersionPairs()
    {
        var parsed = Parsers.ParsePacmanList("bash 5.2.026-2\nlinux-api-headers 6.8-1\n");
        Assert.Equal(2, parsed.Count);
        Assert.Equal("linux-api-headers", parsed[1].Name);
        Assert.Equal("6.8-1", parsed[1].Version);
    }
}

public class UninstallStringResolverTests
{
    [Fact]
    public void QuietString_IsPreferredVerbatim()
    {
        var (file, args) = UninstallStringResolver.ResolveSilent(
            "\"C:\\Program Files\\App\\unins000.exe\" /SILENT", "\"C:\\Program Files\\App\\unins000.exe\"");
        Assert.Equal(@"C:\Program Files\App\unins000.exe", file);
        Assert.Equal("/SILENT", args);
    }

    [Fact]
    public void MsiExec_InstallSwitchBecomesUninstall_WithQuietAndNoRestart()
    {
        var (file, args) = UninstallStringResolver.ResolveSilent(null,
            "MsiExec.exe /I{12345678-1234-1234-1234-1234567890AB}");
        Assert.Equal("MsiExec.exe", file);
        Assert.Contains("/X{12345678-1234-1234-1234-1234567890AB}", args);
        Assert.Contains("/qn", args);
        Assert.Contains("/norestart", args);
    }

    [Fact]
    public void GenericExe_GetsSilentFlagAppended_Once()
    {
        var (file, args) = UninstallStringResolver.ResolveSilent(null, "\"C:\\App\\uninstall.exe\"");
        Assert.Equal(@"C:\App\uninstall.exe", file);
        Assert.Equal("/S", args);
        var (_, args2) = UninstallStringResolver.ResolveSilent(null, "\"C:\\App\\uninstall.exe\" /S");
        Assert.Equal("/S", args2);
    }

    [Fact]
    public void MissingStrings_Throw()
        => Assert.Throws<ArgumentException>(() => UninstallStringResolver.ResolveSilent(null, null));

    [Fact]
    public void ProductCode_AndInstallDate()
    {
        Assert.True(UninstallStringResolver.LooksLikeProductCode("{12345678-1234-1234-1234-1234567890AB}"));
        Assert.False(UninstallStringResolver.LooksLikeProductCode("Git.Git"));
        Assert.Equal("2024-03-15", UninstallStringResolver.FormatInstallDate("20240315"));
        Assert.Null(UninstallStringResolver.FormatInstallDate(""));
        Assert.Equal("garbage", UninstallStringResolver.FormatInstallDate("garbage"));
    }
}
