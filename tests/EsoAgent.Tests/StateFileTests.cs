using System.Text.Json;
using EsoAgent.Core;
using Xunit;

namespace EsoAgent.Tests;

/// <summary>
/// Defect-8 hardening (2026-11-22): every JSON state file goes through
/// StateFile — atomic + content-gated writes, a last-good .bak beside
/// each file, backup recovery on read, and NO silent reset: when both
/// copies are unreadable the read reports Corrupt and leaves the bytes
/// exactly as found. The console-link cases are tonight's incident as a
/// regression test: the device code must survive a torn link file.
/// </summary>
public class StateFileTests
{
    private sealed record Sample(string Code, string Name, int Count);

    private static string TempDir()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "eso-state-" + Guid.NewGuid().ToString("N")));
        return dir.FullName;
    }

    [Fact]
    public void Write_RoundTrips_AndKeepsAnIdenticalBackup()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "state.json");
            Assert.True(StateFile.WriteJson(path, new Sample("GY37-HEX3", "ESO", 3)));
            Assert.True(File.Exists(path));
            Assert.True(File.Exists(path + ".bak"));
            Assert.Equal(File.ReadAllText(path), File.ReadAllText(path + ".bak"));

            var status = StateFile.TryReadJson<Sample>(path, out var value);
            Assert.Equal(StateFileRead.Ok, status);
            Assert.Equal(new Sample("GY37-HEX3", "ESO", 3), value);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Write_IsContentGated()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "state.json");
            Assert.True(StateFile.WriteText(path, "{\"a\":1}"));
            var stamp = File.GetLastWriteTimeUtc(path);
            Assert.False(StateFile.WriteText(path, "{\"a\":1}")); // unchanged → not rewritten
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
            Assert.True(StateFile.WriteText(path, "{\"a\":2}"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("truncated")]
    [InlineData("empty")]
    public void CorruptMain_RecoversFromBackup_AndSelfHeals(string damage)
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "state.json");
            var good = new Sample("GY37-HEX3", "ESO", 3);
            StateFile.WriteJson(path, good);
            var goodBytes = File.ReadAllText(path);

            File.WriteAllText(path, damage switch
            {
                "garbage" => "{ this is not json",
                "truncated" => goodBytes[..(goodBytes.Length / 2)],
                _ => "",
            });

            var status = StateFile.TryReadJson<Sample>(path, out var value);
            Assert.Equal(StateFileRead.RecoveredFromBackup, status);
            Assert.Equal(good, value);
            Assert.Equal(goodBytes, File.ReadAllText(path)); // main restored from the backup bytes
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void MissingMain_GoodBackup_Recovers()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "state.json");
            var good = new Sample("8AY7-DDZ1", "main", 1);
            StateFile.WriteJson(path, good);
            File.Delete(path);

            var status = StateFile.TryReadJson<Sample>(path, out var value);
            Assert.Equal(StateFileRead.RecoveredFromBackup, status);
            Assert.Equal(good, value);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void BothCorrupt_ReportsCorrupt_AndPreservesTheBytes()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "state.json");
            StateFile.WriteJson(path, new Sample("GY37-HEX3", "ESO", 3));
            File.WriteAllText(path, "torn main {");
            File.WriteAllText(path + ".bak", "torn bak {");

            var status = StateFile.TryReadJson<Sample>(path, out var value);
            Assert.Equal(StateFileRead.Corrupt, status);
            Assert.Null(value);
            // No silent reset: the unreadable bytes are still on disk.
            Assert.Equal("torn main {", File.ReadAllText(path));
            Assert.Equal("torn bak {", File.ReadAllText(path + ".bak"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void MissingEverything_ReportsMissing()
    {
        var dir = TempDir();
        try
        {
            var status = StateFile.TryReadJson<Sample>(Path.Combine(dir, "nope.json"), out var value);
            Assert.Equal(StateFileRead.Missing, status);
            Assert.Null(value);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Quarantine_MovesTheCorruptFileAside()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "state.json");
            File.WriteAllText(path, "torn {");
            var quarantined = StateFile.QuarantineCorrupt(path);
            Assert.NotNull(quarantined);
            Assert.False(File.Exists(path));
            Assert.Equal("torn {", File.ReadAllText(quarantined!));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ConsoleLink_TornMainFile_TheDeviceCodeSurvives()
    {
        // Tonight's incident, pinned: the code lives in console-link.json;
        // a torn main file must not blank it while the backup is intact.
        var dir = TempDir();
        try
        {
            var link = new ConsoleLink
            {
                DeviceCode = "GY37-HEX3",
                DeviceName = "ESO",
                FleetUrl = "https://100.98.165.106:8740",
                FleetFingerprint = "9b5c",
                AgentVersion = "1.3.0",
            };
            ConsoleLink.Write(dir, link);
            var path = ConsoleLink.PathForDataDir(dir);
            File.WriteAllText(path, "{\"deviceCode\":\"GY37"); // torn mid-write

            var read = ConsoleLink.TryRead(path);
            Assert.NotNull(read);
            Assert.Equal("GY37-HEX3", read!.DeviceCode);
            Assert.Equal("ESO", read.DeviceName);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ReadText_FallsBackToBackupWhenMainIsEmpty()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "counter.txt");
            StateFile.WriteText(path, "42", keepBackup: true);
            File.WriteAllText(path, "");
            Assert.Equal("42", StateFile.ReadText(path));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Write_NoBackupWhenDeclined()
    {
        // Credential writers opt out: rotation must not leave a copy.
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "token.txt");
            StateFile.WriteText(path, "secret-1", keepBackup: false);
            Assert.False(File.Exists(path + ".bak"));
            StateFile.WriteText(path, "secret-2", keepBackup: false);
            Assert.False(File.Exists(path + ".bak"));
            Assert.Equal("secret-2", File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
