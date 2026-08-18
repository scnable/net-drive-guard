using NetDriveGuard.Core.Abstractions;
using NetDriveGuard.Core.Monitoring;
using NetDriveGuard.Core.Runtime;
using NetDriveGuard.Core.Scheduling;
using NetDriveGuard.Core.Simulation;

namespace NetDriveGuard.Core.Tests;

public sealed class ScriptedMonitorProbeTests
{
    [Fact]
    public async Task 只有指定检查层才推进序列且结束后保持最后状态()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var probe = new ScriptedMonitorProbe(
            clock,
            [ObservationStatus.Healthy, ObservationStatus.ServerUnreachable]);

        var local = await probe.ObserveAsync(CheckLayer.LocalMapping, CancellationToken.None);
        var firstTcp = await probe.ObserveAsync(CheckLayer.Tcp, CancellationToken.None);
        var secondTcp = await probe.ObserveAsync(CheckLayer.Tcp, CancellationToken.None);
        var held = await probe.ObserveAsync(CheckLayer.All, CancellationToken.None);

        Assert.Equal(ObservationStatus.NoChange, local.Status);
        Assert.Equal(ObservationStatus.Healthy, firstTcp.Status);
        Assert.Equal(ObservationStatus.ServerUnreachable, secondTcp.Status);
        Assert.Equal(ObservationStatus.ServerUnreachable, held.Status);
    }

    [Fact]
    public async Task 动态调度演示只在确认离线和恢复时通知()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 8, 19, 0, 0, 0, TimeSpan.Zero));
        var probe = new ScriptedMonitorProbe(
            clock,
            [
                ObservationStatus.Healthy,
                ObservationStatus.ServerUnreachable,
                ObservationStatus.ServerUnreachable,
                ObservationStatus.ServerUnreachable,
                ObservationStatus.ServerUnreachable,
                ObservationStatus.Healthy,
            ]);
        using var coordinator = new MonitorCoordinator(
            probe,
            new NoOpRemediation(),
            clock,
            new MonitorStateMachine(new MonitorPolicy(RemoveMappingWhenOffline: false)),
            MonitorSnapshot.Initial(clock.UtcNow));
        var runner = new ScheduledMonitorRunner(
            coordinator,
            new LayeredSchedulePlanner(MonitoringIntervals.ForProfile(MonitoringProfile.Default)),
            clock);

        var healthy = await runner.RunAsync(ScheduleTrigger.ConfigurationSaved);
        clock.Advance(TimeSpan.FromSeconds(5));
        var firstFailure = await runner.RunAsync(ScheduleTrigger.Timer);
        clock.Advance(TimeSpan.FromSeconds(1));
        var secondFailure = await runner.RunAsync(ScheduleTrigger.Timer);
        clock.Advance(TimeSpan.FromSeconds(1));
        var offline = await runner.RunAsync(ScheduleTrigger.Timer);
        clock.Advance(TimeSpan.FromSeconds(5));
        var stillOffline = await runner.RunAsync(ScheduleTrigger.Timer);
        clock.Advance(TimeSpan.FromSeconds(5));
        var recovered = await runner.RunAsync(ScheduleTrigger.Timer);

        Assert.Equal(MonitorState.Healthy, healthy.Run.Decision?.Snapshot.CurrentState);
        Assert.Equal(MonitorState.Checking, firstFailure.Run.Decision?.Snapshot.CurrentState);
        Assert.Equal(MonitorState.Checking, secondFailure.Run.Decision?.Snapshot.CurrentState);
        Assert.Equal(MonitorState.ServerOffline, offline.Run.Decision?.Snapshot.CurrentState);
        Assert.True(offline.Run.Decision?.ShouldNotify);
        Assert.Equal(MonitorState.ServerOffline, stillOffline.Run.Decision?.Snapshot.CurrentState);
        Assert.False(stillOffline.Run.Decision?.ShouldNotify);
        Assert.Equal(MonitorState.Healthy, recovered.Run.Decision?.Snapshot.CurrentState);
        Assert.True(recovered.Run.Decision?.ShouldNotify);
    }

    [Fact]
    public async Task 构造参数检查层和取消均受保护()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);

        Assert.Throws<ArgumentException>(() => new ScriptedMonitorProbe(clock, []));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ScriptedMonitorProbe(clock, [ObservationStatus.Healthy], CheckLayer.None));
        var probe = new ScriptedMonitorProbe(clock, [ObservationStatus.Healthy]);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => probe.ObserveAsync(CheckLayer.None, CancellationToken.None));

        using var source = new CancellationTokenSource();
        await source.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => probe.ObserveAsync(CheckLayer.Tcp, source.Token));
    }

    private sealed class MutableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = now;

        public void Advance(TimeSpan duration)
        {
            UtcNow += duration;
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
