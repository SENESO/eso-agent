using EsoAgent.Core;
using Xunit;

namespace EsoAgent.Tests;

/// <summary>
/// Schedule write path (UI overhaul Phase 4): the Tasks UIs edit a device's
/// schedules through PUT /api/schedules, so the validation rules and the
/// service's live replacement are covered here — an invalid task must never
/// reach the runner, and an edit must apply without an agent restart.
/// </summary>
public class ScheduleWriteTests
{
    private static ScheduledTaskConfig Valid(string name = "nightly") => new()
    {
        Name = name,
        Command = "echo hi",
        Shell = "bash",
        IntervalMinutes = 60,
    };

    [Fact]
    public void Validate_AcceptsIntervalAndDailyTasks()
    {
        Assert.Null(ScheduleValidation.ValidateOne(Valid()));
        var daily = Valid("morning");
        daily.IntervalMinutes = null;
        daily.DailyAt = "07:30";
        Assert.Null(ScheduleValidation.ValidateOne(daily));
    }

    [Fact]
    public void Validate_RejectsBothIntervalAndDaily()
    {
        var cfg = Valid();
        cfg.DailyAt = "07:30";
        Assert.Contains("interval OR a daily time", ScheduleValidation.ValidateOne(cfg));
    }

    [Theory]
    [InlineData("25:00")]
    [InlineData("7:60")]
    [InlineData("morning")]
    public void Validate_RejectsBadDailyTime(string at)
    {
        var cfg = Valid();
        cfg.IntervalMinutes = null;
        cfg.DailyAt = at;
        Assert.NotNull(ScheduleValidation.ValidateOne(cfg));
    }

    [Fact]
    public void Validate_RejectsUnknownShellAndBadInterval()
    {
        var cfg = Valid();
        cfg.Shell = "zsh";
        Assert.NotNull(ScheduleValidation.ValidateOne(cfg));
        cfg = Valid();
        cfg.IntervalMinutes = 0;
        Assert.NotNull(ScheduleValidation.ValidateOne(cfg));
    }

    [Fact]
    public void ValidateList_RejectsDuplicateNamesCaseInsensitively()
    {
        var list = new List<ScheduledTaskConfig> { Valid("Backup"), Valid("backup") };
        Assert.Contains("Duplicate", ScheduleValidation.ValidateList(list));
    }

    private static (ScheduleService Service, string Dir) NewService(List<ScheduledTaskConfig> initial)
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "eso-sched-" + Guid.NewGuid().ToString("N"))).FullName;
        var cfg = AgentConfig.Load(dir);
        cfg.Schedules = initial;
        var state = new AgentState
        {
            Config = cfg,
            Device = null!,
            Audit = new AuditLog(cfg.ResolvePath(cfg.AuditLogFile)),
            KillSwitch = null!,
            Platform = null!,
            Files = null!,
            Update = null!,
        };
        return (new ScheduleService(state), dir);
    }

    [Fact]
    public void ReplaceSchedules_AppliesLiveAndKeepsSurvivorState()
    {
        var (svc, dir) = NewService(new List<ScheduledTaskConfig> { Valid("a"), Valid("b") });
        try
        {
            Assert.Equal(new[] { "a", "b" }, svc.Configs().Select(c => c.Name).ToArray());
            svc.ReplaceSchedules(new List<ScheduledTaskConfig> { Valid("b"), Valid("c") });
            Assert.Equal(new[] { "b", "c" }, svc.Configs().Select(c => c.Name).ToArray());
            var snapshot = svc.Snapshot();
            Assert.Equal(new[] { "b", "c" }, snapshot.Select(s => s.Name).ToArray());
            Assert.All(snapshot, s => Assert.Equal("every 60 min", s.Description));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task RunNow_UnknownName_ReturnsFalse()
    {
        var (svc, dir) = NewService(new List<ScheduledTaskConfig> { Valid("a") });
        try
        {
            Assert.False(await svc.RunNowAsync("nope"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
