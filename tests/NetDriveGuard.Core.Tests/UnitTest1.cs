namespace NetDriveGuard.Core.Tests;

using Monitoring = NetDriveGuard.Core.Monitoring;

public class MonitorStateMachineTests
{
    [Fact]
    public void 连续三次服务器失败后确认离线并只请求一次移除()
    {
        var now = new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);
        var machine = new Monitoring.MonitorStateMachine(new Monitoring.MonitorPolicy());
        var snapshot = Monitoring.MonitorSnapshot.Initial(now) with
        {
            CurrentState = Monitoring.MonitorState.Healthy,
            LastStableState = Monitoring.MonitorState.Healthy,
        };

        var first = machine.Evaluate(
            new Monitoring.MonitorObservation(Monitoring.ObservationStatus.ServerUnreachable, now.AddSeconds(1)),
            snapshot);
        var second = machine.Evaluate(
            new Monitoring.MonitorObservation(Monitoring.ObservationStatus.ServerUnreachable, now.AddSeconds(2)),
            first.Snapshot);
        var third = machine.Evaluate(
            new Monitoring.MonitorObservation(Monitoring.ObservationStatus.ServerUnreachable, now.AddSeconds(3)),
            second.Snapshot);
        var fourth = machine.Evaluate(
            new Monitoring.MonitorObservation(Monitoring.ObservationStatus.ServerUnreachable, now.AddSeconds(8)),
            third.Snapshot);

        Assert.Equal(Monitoring.MonitorState.Checking, first.Snapshot.CurrentState);
        Assert.Equal(1, first.Snapshot.ConsecutiveServerFailures);
        Assert.Equal(Monitoring.MonitorState.Checking, second.Snapshot.CurrentState);
        Assert.Equal(2, second.Snapshot.ConsecutiveServerFailures);
        Assert.Equal(Monitoring.MonitorState.ServerOffline, third.Snapshot.CurrentState);
        Assert.Equal(Monitoring.MonitorAction.RemoveMapping, third.Action);
        Assert.True(third.ShouldNotify);
        Assert.Equal(Monitoring.MonitorAction.None, fourth.Action);
        Assert.False(fourth.ShouldNotify);
    }

    [Fact]
    public void 仅提醒策略确认离线时不请求移除()
    {
        var now = DateTimeOffset.UtcNow;
        var machine = new Monitoring.MonitorStateMachine(new Monitoring.MonitorPolicy(2, false));
        var snapshot = HealthySnapshot(now);

        var first = machine.Evaluate(Unreachable(now.AddSeconds(1)), snapshot);
        var second = machine.Evaluate(Unreachable(now.AddSeconds(2)), first.Snapshot);

        Assert.Equal(Monitoring.MonitorState.ServerOffline, second.Snapshot.CurrentState);
        Assert.Equal(Monitoring.MonitorAction.None, second.Action);
        Assert.True(second.ShouldNotify);
    }

    [Fact]
    public void 离线恢复后通知并清零失败计数()
    {
        var now = DateTimeOffset.UtcNow;
        var machine = new Monitoring.MonitorStateMachine(new Monitoring.MonitorPolicy());
        var offline = new Monitoring.MonitorSnapshot(
            Monitoring.MonitorState.ServerOffline,
            Monitoring.MonitorState.ServerOffline,
            3,
            now,
            now);

        var decision = machine.Evaluate(
            new Monitoring.MonitorObservation(Monitoring.ObservationStatus.Healthy, now.AddSeconds(5)),
            offline);

        Assert.Equal(Monitoring.MonitorState.Healthy, decision.Snapshot.CurrentState);
        Assert.Equal(0, decision.Snapshot.ConsecutiveServerFailures);
        Assert.True(decision.ShouldNotify);
        Assert.Equal("state.recovered", decision.EventCode);
    }

    [Theory]
    [InlineData(Monitoring.MonitorState.MappingMissing)]
    [InlineData(Monitoring.MonitorState.Repairing)]
    [InlineData(Monitoring.MonitorState.AuthenticationFailed)]
    [InlineData(Monitoring.MonitorState.ShareUnavailable)]
    [InlineData(Monitoring.MonitorState.DriveConflict)]
    [InlineData(Monitoring.MonitorState.InternalError)]
    public void 各类异常恢复为健康时产生恢复通知(Monitoring.MonitorState previousState)
    {
        var now = DateTimeOffset.UtcNow;
        var machine = new Monitoring.MonitorStateMachine(new Monitoring.MonitorPolicy());
        var snapshot = new Monitoring.MonitorSnapshot(previousState, previousState, 0, now, now);

        var decision = machine.Evaluate(
            new Monitoring.MonitorObservation(Monitoring.ObservationStatus.Healthy, now.AddSeconds(1)),
            snapshot);

        Assert.True(decision.ShouldNotify);
        Assert.Equal("state.recovered", decision.EventCode);
    }

    [Fact]
    public void 映射缺失只在首次观察时请求修复()
    {
        var now = DateTimeOffset.UtcNow;
        var machine = new Monitoring.MonitorStateMachine(new Monitoring.MonitorPolicy());

        var first = machine.Evaluate(
            new Monitoring.MonitorObservation(Monitoring.ObservationStatus.MappingMissing, now.AddSeconds(1)),
            HealthySnapshot(now));
        var second = machine.Evaluate(
            new Monitoring.MonitorObservation(Monitoring.ObservationStatus.MappingMissing, now.AddSeconds(2)),
            first.Snapshot);

        Assert.Equal(Monitoring.MonitorAction.RepairMapping, first.Action);
        Assert.Equal(Monitoring.MonitorAction.None, second.Action);
        Assert.False(first.ShouldNotify);
        Assert.False(second.ShouldNotify);
    }

    [Fact]
    public void 只读策略发现映射缺失时不请求修复()
    {
        var now = DateTimeOffset.UtcNow;
        var machine = new Monitoring.MonitorStateMachine(
            new Monitoring.MonitorPolicy(RepairMissingMapping: false));

        var decision = machine.Evaluate(
            new Monitoring.MonitorObservation(Monitoring.ObservationStatus.MappingMissing, now),
            HealthySnapshot(now.AddSeconds(-1)));

        Assert.Equal(Monitoring.MonitorState.MappingMissing, decision.Snapshot.CurrentState);
        Assert.Equal(Monitoring.MonitorAction.None, decision.Action);
        Assert.Equal("mapping.missing", decision.EventCode);
    }

    [Theory]
    [InlineData(Monitoring.ObservationStatus.AuthenticationFailed, Monitoring.MonitorState.AuthenticationFailed)]
    [InlineData(Monitoring.ObservationStatus.ShareUnavailable, Monitoring.MonitorState.ShareUnavailable)]
    [InlineData(Monitoring.ObservationStatus.DriveConflict, Monitoring.MonitorState.DriveConflict)]
    [InlineData(Monitoring.ObservationStatus.InternalError, Monitoring.MonitorState.InternalError)]
    public void 关键问题在首次状态变化时通知(
        Monitoring.ObservationStatus status,
        Monitoring.MonitorState expectedState)
    {
        var now = DateTimeOffset.UtcNow;
        var machine = new Monitoring.MonitorStateMachine(new Monitoring.MonitorPolicy());
        var observation = new Monitoring.MonitorObservation(status, now.AddSeconds(1));

        var first = machine.Evaluate(observation, HealthySnapshot(now));
        var second = machine.Evaluate(observation with { ObservedAt = now.AddSeconds(2) }, first.Snapshot);

        Assert.Equal(expectedState, first.Snapshot.CurrentState);
        Assert.True(first.ShouldNotify);
        Assert.False(second.ShouldNotify);
    }

    [Fact]
    public void 暂停状态不产生动作或通知()
    {
        var now = DateTimeOffset.UtcNow;
        var machine = new Monitoring.MonitorStateMachine(new Monitoring.MonitorPolicy());

        var decision = machine.Evaluate(
            new Monitoring.MonitorObservation(Monitoring.ObservationStatus.Paused, now.AddSeconds(1)),
            HealthySnapshot(now));

        Assert.Equal(Monitoring.MonitorState.Paused, decision.Snapshot.CurrentState);
        Assert.Equal(Monitoring.MonitorAction.None, decision.Action);
        Assert.False(decision.ShouldNotify);
    }

    [Fact]
    public void 未配置状态保持安全且不产生动作()
    {
        var now = DateTimeOffset.UtcNow;
        var machine = new Monitoring.MonitorStateMachine(new Monitoring.MonitorPolicy());

        var decision = machine.Evaluate(
            new Monitoring.MonitorObservation(Monitoring.ObservationStatus.NotConfigured, now.AddSeconds(1)),
            HealthySnapshot(now));

        Assert.Equal(Monitoring.MonitorState.NotConfigured, decision.Snapshot.CurrentState);
        Assert.Equal(Monitoring.MonitorAction.None, decision.Action);
        Assert.False(decision.ShouldNotify);
    }

    [Fact]
    public void 无变化观察只更新时间且不重复累计失败()
    {
        var now = DateTimeOffset.UtcNow;
        var machine = new Monitoring.MonitorStateMachine(new Monitoring.MonitorPolicy());
        var checking = new Monitoring.MonitorSnapshot(
            Monitoring.MonitorState.Checking,
            Monitoring.MonitorState.Healthy,
            1,
            now,
            now);

        var decision = machine.Evaluate(
            new Monitoring.MonitorObservation(
                Monitoring.ObservationStatus.NoChange,
                now.AddSeconds(1),
                "local-mapping.unchanged"),
            checking);

        Assert.Equal(Monitoring.MonitorState.Checking, decision.Snapshot.CurrentState);
        Assert.Equal(Monitoring.MonitorState.Healthy, decision.Snapshot.LastStableState);
        Assert.Equal(1, decision.Snapshot.ConsecutiveServerFailures);
        Assert.Equal(now, decision.Snapshot.LastTransitionAt);
        Assert.Equal(now.AddSeconds(1), decision.Snapshot.LastObservationAt);
        Assert.Equal(Monitoring.MonitorAction.None, decision.Action);
        Assert.False(decision.ShouldNotify);
        Assert.Equal("local-mapping.unchanged", decision.EventCode);
    }

    [Fact]
    public void 修复中状态保留上一个稳定状态()
    {
        var now = DateTimeOffset.UtcNow;
        var machine = new Monitoring.MonitorStateMachine(new Monitoring.MonitorPolicy());
        var previous = HealthySnapshot(now);

        var first = machine.Evaluate(
            new Monitoring.MonitorObservation(Monitoring.ObservationStatus.Repairing, now.AddSeconds(1)),
            previous);
        var second = machine.Evaluate(
            new Monitoring.MonitorObservation(Monitoring.ObservationStatus.Repairing, now.AddSeconds(2)),
            first.Snapshot);

        Assert.Equal(Monitoring.MonitorState.Repairing, first.Snapshot.CurrentState);
        Assert.Equal(Monitoring.MonitorState.Healthy, first.Snapshot.LastStableState);
        Assert.Equal(first.Snapshot.LastTransitionAt, second.Snapshot.LastTransitionAt);
    }

    [Fact]
    public void 空输入和未知状态被明确拒绝()
    {
        var now = DateTimeOffset.UtcNow;
        var machine = new Monitoring.MonitorStateMachine(new Monitoring.MonitorPolicy());
        var snapshot = HealthySnapshot(now);

        Assert.Throws<ArgumentNullException>(() => machine.Evaluate(null!, snapshot));
        Assert.Throws<ArgumentNullException>(
            () => machine.Evaluate(
                new Monitoring.MonitorObservation(Monitoring.ObservationStatus.Healthy, now),
                null!));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => machine.Evaluate(
                new Monitoring.MonitorObservation((Monitoring.ObservationStatus)999, now),
                snapshot));
    }

    [Fact]
    public void 非法离线阈值在构造状态机时被拒绝()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new Monitoring.MonitorStateMachine(new Monitoring.MonitorPolicy(1)));
    }

    private static Monitoring.MonitorSnapshot HealthySnapshot(DateTimeOffset now) => new(
        Monitoring.MonitorState.Healthy,
        Monitoring.MonitorState.Healthy,
        0,
        now,
        now);

    private static Monitoring.MonitorObservation Unreachable(DateTimeOffset at) => new(
        Monitoring.ObservationStatus.ServerUnreachable,
        at);
}
