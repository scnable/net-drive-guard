using NetDriveGuard.Core.Abstractions;
using NetDriveGuard.Core.Monitoring;
using NetDriveGuard.Core.Runtime;

namespace NetDriveGuard.Core.Tests;

public class MonitorCoordinatorTests
{
    [Fact]
    public async Task 单次健康探测更新快照且不执行修复()
    {
        var now = DateTimeOffset.UtcNow;
        var remediation = new RecordingRemediation();
        using var coordinator = CreateCoordinator(
            new QueueProbe(new MonitorObservation(ObservationStatus.Healthy, now)),
            remediation,
            now);

        var result = await coordinator.RunOnceAsync();

        Assert.Equal(MonitorRunStatus.Completed, result.Status);
        Assert.Equal(MonitorState.Healthy, coordinator.Snapshot.CurrentState);
        Assert.Empty(remediation.Actions);
    }

    [Fact]
    public async Task 三次失败通过协调器只执行一次移除动作()
    {
        var now = DateTimeOffset.UtcNow;
        var probe = new QueueProbe(
            new MonitorObservation(ObservationStatus.ServerUnreachable, now.AddSeconds(1)),
            new MonitorObservation(ObservationStatus.ServerUnreachable, now.AddSeconds(2)),
            new MonitorObservation(ObservationStatus.ServerUnreachable, now.AddSeconds(3)),
            new MonitorObservation(ObservationStatus.ServerUnreachable, now.AddSeconds(8)));
        var remediation = new RecordingRemediation();
        using var coordinator = CreateCoordinator(probe, remediation, now, HealthySnapshot(now));

        await coordinator.RunOnceAsync();
        await coordinator.RunOnceAsync();
        await coordinator.RunOnceAsync();
        await coordinator.RunOnceAsync();

        Assert.Equal(MonitorState.ServerOffline, coordinator.Snapshot.CurrentState);
        Assert.Equal(new[] { MonitorAction.RemoveMapping }, remediation.Actions);
    }

    [Fact]
    public async Task 已有检查运行时第二次检查被跳过()
    {
        var now = DateTimeOffset.UtcNow;
        var probe = new BlockingProbe(now);
        using var coordinator = CreateCoordinator(probe, new RecordingRemediation(), now);

        var firstRun = coordinator.RunOnceAsync();
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var secondRun = await coordinator.RunOnceAsync();
        probe.Release.SetResult();
        var firstResult = await firstRun;

        Assert.Equal(MonitorRunStatus.SkippedAlreadyRunning, secondRun.Status);
        Assert.Equal(MonitorRunStatus.Completed, firstResult.Status);
    }

    [Fact]
    public async Task 外部取消不会改变最后快照()
    {
        var now = DateTimeOffset.UtcNow;
        using var source = new CancellationTokenSource();
        var probe = new CancellingProbe();
        using var coordinator = CreateCoordinator(probe, new RecordingRemediation(), now);

        var run = coordinator.RunOnceAsync(source.Token);
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await source.CancelAsync();
        var result = await run;

        Assert.Equal(MonitorRunStatus.Cancelled, result.Status);
        Assert.Equal(MonitorState.NotConfigured, coordinator.Snapshot.CurrentState);
    }

    [Fact]
    public async Task 调用前已经取消时立即返回且不探测()
    {
        var now = DateTimeOffset.UtcNow;
        using var source = new CancellationTokenSource();
        await source.CancelAsync();
        var probe = new QueueProbe(new MonitorObservation(ObservationStatus.Healthy, now));
        using var coordinator = CreateCoordinator(probe, new RecordingRemediation(), now);

        var result = await coordinator.RunOnceAsync(source.Token);

        Assert.Equal(MonitorRunStatus.Cancelled, result.Status);
        Assert.Equal(MonitorState.NotConfigured, coordinator.Snapshot.CurrentState);
    }

    [Fact]
    public async Task 探测异常被归一化为内部错误()
    {
        var now = DateTimeOffset.UtcNow;
        using var coordinator = CreateCoordinator(new ThrowingProbe(), new RecordingRemediation(), now);

        var result = await coordinator.RunOnceAsync();

        Assert.Equal(MonitorRunStatus.Completed, result.Status);
        Assert.Equal(MonitorState.InternalError, result.Decision?.Snapshot.CurrentState);
        Assert.Equal("state.internal-error", result.Decision?.EventCode);
    }

    [Fact]
    public async Task 修复失败转为内部错误并保留失败代码()
    {
        var now = DateTimeOffset.UtcNow;
        var remediation = new RecordingRemediation(RemediationResult.Failed("mapping.repair-failed"));
        using var coordinator = CreateCoordinator(
            new QueueProbe(new MonitorObservation(ObservationStatus.MappingMissing, now.AddSeconds(1))),
            remediation,
            now,
            HealthySnapshot(now));

        var result = await coordinator.RunOnceAsync();

        Assert.Equal(MonitorState.InternalError, result.Decision?.Snapshot.CurrentState);
        Assert.NotNull(result.Remediation);
        Assert.False(result.Remediation.Success);
        Assert.Equal("mapping.repair-failed", result.Remediation.Code);
    }

    [Fact]
    public async Task 修复抛出异常时被归一化为失败结果()
    {
        var now = DateTimeOffset.UtcNow;
        using var coordinator = CreateCoordinator(
            new QueueProbe(new MonitorObservation(ObservationStatus.MappingMissing, now.AddSeconds(1))),
            new ThrowingRemediation(),
            now,
            HealthySnapshot(now));

        var result = await coordinator.RunOnceAsync();

        Assert.Equal(MonitorState.InternalError, result.Decision?.Snapshot.CurrentState);
        Assert.Equal("remediation.unhandled-error", result.Remediation?.Code);
    }

    [Fact]
    public async Task 修复开始前取消时不提交中间快照()
    {
        var now = DateTimeOffset.UtcNow;
        using var source = new CancellationTokenSource();
        using var coordinator = CreateCoordinator(
            new CancelThenReturnProbe(source, now),
            new RecordingRemediation(),
            now,
            HealthySnapshot(now));

        var result = await coordinator.RunOnceAsync(source.Token);

        Assert.Equal(MonitorRunStatus.Cancelled, result.Status);
        Assert.Equal(MonitorState.Healthy, coordinator.Snapshot.CurrentState);
    }

    [Fact]
    public async Task 释放协调器可重复调用且拒绝后续运行()
    {
        var now = DateTimeOffset.UtcNow;
        var coordinator = CreateCoordinator(
            new QueueProbe(new MonitorObservation(ObservationStatus.Healthy, now)),
            new RecordingRemediation(),
            now);

        coordinator.Dispose();
        coordinator.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => coordinator.RunOnceAsync());
    }

    private static MonitorCoordinator CreateCoordinator(
        IProbe<MonitorObservation> probe,
        IRemediation<MonitorAction> remediation,
        DateTimeOffset now,
        MonitorSnapshot? initial = null)
    {
        return new MonitorCoordinator(
            probe,
            remediation,
            new FixedClock(now.AddMinutes(1)),
            new MonitorStateMachine(new MonitorPolicy()),
            initial ?? MonitorSnapshot.Initial(now));
    }

    private static MonitorSnapshot HealthySnapshot(DateTimeOffset now) => new(
        MonitorState.Healthy,
        MonitorState.Healthy,
        0,
        now,
        now);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class QueueProbe(params MonitorObservation[] observations) : IProbe<MonitorObservation>
    {
        private readonly Queue<MonitorObservation> _observations = new(observations);

        public Task<MonitorObservation> ObserveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_observations.Dequeue());
        }
    }

    private sealed class RecordingRemediation(RemediationResult? result = null) : IRemediation<MonitorAction>
    {
        private readonly RemediationResult _result = result ?? RemediationResult.Succeeded();

        public List<MonitorAction> Actions { get; } = [];

        public Task<RemediationResult> ExecuteAsync(
            MonitorAction action,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Actions.Add(action);
            return Task.FromResult(_result);
        }
    }

    private sealed class BlockingProbe(DateTimeOffset now) : IProbe<MonitorObservation>
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<MonitorObservation> ObserveAsync(CancellationToken cancellationToken)
        {
            Started.SetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new MonitorObservation(ObservationStatus.Healthy, now);
        }
    }

    private sealed class CancellingProbe : IProbe<MonitorObservation>
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<MonitorObservation> ObserveAsync(CancellationToken cancellationToken)
        {
            Started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("取消后不应继续执行。");
        }
    }

    private sealed class ThrowingProbe : IProbe<MonitorObservation>
    {
        public Task<MonitorObservation> ObserveAsync(CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("模拟探测异常。");
        }
    }

    private sealed class ThrowingRemediation : IRemediation<MonitorAction>
    {
        public Task<RemediationResult> ExecuteAsync(
            MonitorAction action,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("模拟修复异常。");
        }
    }

    private sealed class CancelThenReturnProbe(
        CancellationTokenSource source,
        DateTimeOffset now) : IProbe<MonitorObservation>
    {
        public async Task<MonitorObservation> ObserveAsync(CancellationToken cancellationToken)
        {
            await source.CancelAsync();
            return new MonitorObservation(ObservationStatus.MappingMissing, now);
        }
    }
}
