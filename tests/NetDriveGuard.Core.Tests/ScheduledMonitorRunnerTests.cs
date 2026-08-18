using NetDriveGuard.Core.Abstractions;
using NetDriveGuard.Core.Monitoring;
using NetDriveGuard.Core.Runtime;
using NetDriveGuard.Core.Scheduling;

namespace NetDriveGuard.Core.Tests;

public sealed class ScheduledMonitorRunnerTests
{
    [Fact]
    public async Task 首次计时检查执行全部层并记录完成时间()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var probe = new RecordingLayeredProbe(clock);
        using var coordinator = CreateCoordinator(probe, clock, HealthySnapshot(clock.UtcNow));
        var runner = CreateRunner(coordinator, clock);

        var result = await runner.RunAsync(ScheduleTrigger.Timer);

        Assert.Equal(MonitorRunStatus.Completed, result.Run.Status);
        Assert.Equal(CheckLayer.All, result.RequestedChecks);
        Assert.Equal([CheckLayer.All], probe.Requests);
        Assert.Equal(clock.UtcNow, runner.ScheduleSnapshot.LastLocalMappingQueryAt);
        Assert.Equal(clock.UtcNow, runner.ScheduleSnapshot.LastTcpProbeAt);
        Assert.Equal(clock.UtcNow, runner.ScheduleSnapshot.LastShareProbeAt);
    }

    [Fact]
    public async Task 周期未到时不调用探测器()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new MutableClock(now);
        var probe = new RecordingLayeredProbe(clock);
        using var coordinator = CreateCoordinator(probe, clock, HealthySnapshot(now));
        var runner = CreateRunner(
            coordinator,
            clock,
            new ScheduleSnapshot(now, now, now));

        clock.Advance(TimeSpan.FromSeconds(1));
        var result = await runner.RunAsync(ScheduleTrigger.Timer);

        Assert.Equal(MonitorRunStatus.SkippedNoChecks, result.Run.Status);
        Assert.Equal(CheckLayer.None, result.RequestedChecks);
        Assert.Empty(probe.Requests);
    }

    [Fact]
    public async Task 只把到期的本地映射层交给分层探测器()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new MutableClock(now);
        var probe = new RecordingLayeredProbe(clock);
        using var coordinator = CreateCoordinator(probe, clock, HealthySnapshot(now));
        var runner = CreateRunner(
            coordinator,
            clock,
            new ScheduleSnapshot(now.AddSeconds(-2), now, now));

        var result = await runner.RunAsync(ScheduleTrigger.Timer);

        Assert.Equal(CheckLayer.LocalMapping, result.RequestedChecks);
        Assert.Equal([CheckLayer.LocalMapping], probe.Requests);
        Assert.Equal(now, runner.ScheduleSnapshot.LastLocalMappingQueryAt);
        Assert.Equal(now, runner.ScheduleSnapshot.LastTcpProbeAt);
        Assert.Equal(now, runner.ScheduleSnapshot.LastShareProbeAt);
    }

    [Fact]
    public async Task 手动检查忽略最近周期并执行全部层()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new MutableClock(now);
        var probe = new RecordingLayeredProbe(clock);
        using var coordinator = CreateCoordinator(probe, clock, HealthySnapshot(now));
        var runner = CreateRunner(
            coordinator,
            clock,
            new ScheduleSnapshot(now, now, now));

        var result = await runner.RunAsync(ScheduleTrigger.Manual);

        Assert.Equal(CheckLayer.All, result.RequestedChecks);
        Assert.Equal([CheckLayer.All], probe.Requests);
    }

    [Fact]
    public async Task 未配置时启动不探测但保存配置会触发全部层()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new MutableClock(now);
        var probe = new RecordingLayeredProbe(clock);
        using var coordinator = CreateCoordinator(probe, clock, MonitorSnapshot.Initial(now));
        var runner = CreateRunner(coordinator, clock);

        var startup = await runner.RunAsync(ScheduleTrigger.Startup);
        var configured = await runner.RunAsync(ScheduleTrigger.ConfigurationSaved);

        Assert.Equal(MonitorRunStatus.SkippedNoChecks, startup.Run.Status);
        Assert.Equal(MonitorRunStatus.Completed, configured.Run.Status);
        Assert.Equal([CheckLayer.All], probe.Requests);
    }

    [Fact]
    public async Task 已取消的检查不会更新调度时间()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new MutableClock(now);
        var probe = new RecordingLayeredProbe(clock);
        using var coordinator = CreateCoordinator(probe, clock, HealthySnapshot(now));
        var runner = CreateRunner(coordinator, clock);
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        var result = await runner.RunAsync(ScheduleTrigger.Manual, source.Token);

        Assert.Equal(MonitorRunStatus.Cancelled, result.Run.Status);
        Assert.Equal(new ScheduleSnapshot(), runner.ScheduleSnapshot);
        Assert.Empty(probe.Requests);
    }

    [Fact]
    public async Task 协调器拒绝未知检查层()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        using var coordinator = CreateCoordinator(
            new RecordingLayeredProbe(clock),
            clock,
            HealthySnapshot(clock.UtcNow));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => coordinator.RunOnceAsync((CheckLayer)8));
    }

    private static ScheduledMonitorRunner CreateRunner(
        MonitorCoordinator coordinator,
        IClock clock,
        ScheduleSnapshot? schedule = null)
    {
        return new ScheduledMonitorRunner(
            coordinator,
            new LayeredSchedulePlanner(MonitoringIntervals.ForProfile(MonitoringProfile.Default)),
            clock,
            schedule);
    }

    private static MonitorCoordinator CreateCoordinator(
        IProbe<MonitorObservation> probe,
        IClock clock,
        MonitorSnapshot initial)
    {
        return new MonitorCoordinator(
            probe,
            new NoOpRemediation(),
            clock,
            new MonitorStateMachine(new MonitorPolicy(RemoveMappingWhenOffline: false)),
            initial);
    }

    private static MonitorSnapshot HealthySnapshot(DateTimeOffset now) => new(
        MonitorState.Healthy,
        MonitorState.Healthy,
        0,
        now,
        now);

    private sealed class MutableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = now;

        public void Advance(TimeSpan duration)
        {
            UtcNow += duration;
        }
    }

    private sealed class RecordingLayeredProbe(IClock clock) : ILayeredProbe<MonitorObservation>
    {
        public List<CheckLayer> Requests { get; } = [];

        public Task<MonitorObservation> ObserveAsync(
            CheckLayer checkLayers,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(checkLayers);
            return Task.FromResult(new MonitorObservation(ObservationStatus.Healthy, clock.UtcNow));
        }
    }

    private sealed class NoOpRemediation : IRemediation<MonitorAction>
    {
        public Task<RemediationResult> ExecuteAsync(
            MonitorAction action,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(RemediationResult.Succeeded());
        }
    }
}
