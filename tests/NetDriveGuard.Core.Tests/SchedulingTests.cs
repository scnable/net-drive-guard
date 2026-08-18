using NetDriveGuard.Core.Monitoring;
using NetDriveGuard.Core.Scheduling;

namespace NetDriveGuard.Core.Tests;

public class SchedulingTests
{
    [Fact]
    public void 默认模式符合五秒轻量探测方案()
    {
        var intervals = MonitoringIntervals.ForProfile(MonitoringProfile.Default);

        Assert.Equal(TimeSpan.FromSeconds(2), intervals.LocalMappingQuery);
        Assert.Equal(TimeSpan.FromSeconds(5), intervals.TcpProbe);
        Assert.Equal(TimeSpan.FromSeconds(1), intervals.FailureRetry);
        Assert.Equal(TimeSpan.FromSeconds(5), intervals.OfflineRecovery);
        Assert.Equal(TimeSpan.FromSeconds(45), intervals.ShareProbe);
    }

    [Theory]
    [InlineData(MonitoringProfile.Fast, 2)]
    [InlineData(MonitoringProfile.EnergySaving, 30)]
    public void 预设模式返回约定的Tcp周期(MonitoringProfile profile, int expectedSeconds)
    {
        var intervals = MonitoringIntervals.ForProfile(profile);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), intervals.TcpProbe);
    }

    [Fact]
    public void 自定义周期限制在一秒到三十分钟()
    {
        Assert.False(MonitoringIntervals.IsCustomIntervalValid(TimeSpan.FromMilliseconds(999)));
        Assert.True(MonitoringIntervals.IsCustomIntervalValid(TimeSpan.FromSeconds(1)));
        Assert.True(MonitoringIntervals.IsCustomIntervalValid(TimeSpan.FromMinutes(30)));
        Assert.False(MonitoringIntervals.IsCustomIntervalValid(TimeSpan.FromMinutes(31)));
    }

    [Theory]
    [InlineData(1, 1, 5, 30)]
    [InlineData(10, 2, 10, 60)]
    public void 自定义模式按边界生成分层周期(
        int tcpSeconds,
        int localSeconds,
        int offlineSeconds,
        int shareSeconds)
    {
        var intervals = MonitoringIntervals.ForProfile(
            MonitoringProfile.Custom,
            TimeSpan.FromSeconds(tcpSeconds));

        Assert.Equal(TimeSpan.FromSeconds(tcpSeconds), intervals.TcpProbe);
        Assert.Equal(TimeSpan.FromSeconds(localSeconds), intervals.LocalMappingQuery);
        Assert.Equal(TimeSpan.FromSeconds(offlineSeconds), intervals.OfflineRecovery);
        Assert.Equal(TimeSpan.FromSeconds(shareSeconds), intervals.ShareProbe);
    }

    [Fact]
    public void 自定义模式缺少或越界周期时被拒绝()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MonitoringIntervals.ForProfile(MonitoringProfile.Custom));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MonitoringIntervals.ForProfile(MonitoringProfile.Custom, TimeSpan.FromMilliseconds(500)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MonitoringIntervals.ForProfile((MonitoringProfile)999));
    }

    [Fact]
    public void 启动和人工触发立即执行全部检查层()
    {
        var planner = DefaultPlanner();
        var now = DateTimeOffset.UtcNow;
        var recent = new ScheduleSnapshot(now, now, now);

        foreach (var trigger in new[]
                 {
                     ScheduleTrigger.Startup,
                     ScheduleTrigger.Resume,
                     ScheduleTrigger.NetworkChanged,
                     ScheduleTrigger.Manual,
                     ScheduleTrigger.ConfigurationSaved,
                 })
        {
            var due = planner.GetDueChecks(new ScheduleContext(MonitorState.Healthy, now, trigger, recent));
            Assert.Equal(CheckLayer.All, due);
        }
    }

    [Fact]
    public void 暂停和未配置状态不执行检查()
    {
        var planner = DefaultPlanner();
        var now = DateTimeOffset.UtcNow;
        var empty = new ScheduleSnapshot();

        Assert.Equal(
            CheckLayer.None,
            planner.GetDueChecks(new ScheduleContext(MonitorState.Paused, now, ScheduleTrigger.Manual, empty)));
        Assert.Equal(
            CheckLayer.None,
            planner.GetDueChecks(new ScheduleContext(MonitorState.NotConfigured, now, ScheduleTrigger.Startup, empty)));
    }

    [Fact]
    public void 保存有效配置后允许未配置状态执行首次检查()
    {
        var planner = DefaultPlanner();
        var now = DateTimeOffset.UtcNow;

        var due = planner.GetDueChecks(
            new ScheduleContext(
                MonitorState.NotConfigured,
                now,
                ScheduleTrigger.ConfigurationSaved,
                new ScheduleSnapshot()));

        Assert.Equal(CheckLayer.All, due);
    }

    [Fact]
    public void 正常状态按各自周期独立调度()
    {
        var planner = DefaultPlanner();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new ScheduleSnapshot(
            now.AddSeconds(-2),
            now.AddSeconds(-4),
            now.AddSeconds(-44));

        var due = planner.GetDueChecks(
            new ScheduleContext(MonitorState.Healthy, now, ScheduleTrigger.Timer, snapshot));

        Assert.Equal(CheckLayer.LocalMapping, due);
    }

    [Fact]
    public void 确认失败阶段一秒后重试且不访问共享()
    {
        var planner = DefaultPlanner();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new ScheduleSnapshot(now, now.AddSeconds(-1), now.AddMinutes(-1));

        var due = planner.GetDueChecks(
            new ScheduleContext(MonitorState.Checking, now, ScheduleTrigger.Timer, snapshot));

        Assert.True(due.HasFlag(CheckLayer.Tcp));
        Assert.False(due.HasFlag(CheckLayer.Share));
    }

    [Fact]
    public void 离线状态五秒后探测恢复且不访问共享()
    {
        var planner = DefaultPlanner();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new ScheduleSnapshot(now, now.AddSeconds(-5), now.AddMinutes(-1));

        var due = planner.GetDueChecks(
            new ScheduleContext(MonitorState.ServerOffline, now, ScheduleTrigger.Timer, snapshot));

        Assert.True(due.HasFlag(CheckLayer.Tcp));
        Assert.False(due.HasFlag(CheckLayer.Share));
    }

    [Fact]
    public void 系统时间回拨时重新检查而不是计算负间隔()
    {
        var planner = DefaultPlanner();
        var now = DateTimeOffset.UtcNow;
        var future = new ScheduleSnapshot(now.AddMinutes(1), now.AddMinutes(1), now.AddMinutes(1));

        var due = planner.GetDueChecks(
            new ScheduleContext(MonitorState.Healthy, now, ScheduleTrigger.Timer, future));

        Assert.Equal(CheckLayer.All, due);
    }

    private static LayeredSchedulePlanner DefaultPlanner() => new(
        MonitoringIntervals.ForProfile(MonitoringProfile.Default));
}
