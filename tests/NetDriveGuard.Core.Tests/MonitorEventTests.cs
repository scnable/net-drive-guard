using NetDriveGuard.Core.Events;
using NetDriveGuard.Core.Monitoring;

namespace NetDriveGuard.Core.Tests;

public class MonitorEventTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(MonitorState.NotConfigured, MonitorEventSeverity.Information, "尚未配置监控目标")]
    [InlineData(MonitorState.Checking, MonitorEventSeverity.Warning, "检测到异常")]
    [InlineData(MonitorState.Healthy, MonitorEventSeverity.Information, "监控目标状态正常")]
    [InlineData(MonitorState.ServerOffline, MonitorEventSeverity.Error, "服务器当前不可达")]
    [InlineData(MonitorState.MappingMissing, MonitorEventSeverity.Warning, "网络驱动器映射缺失")]
    [InlineData(MonitorState.Repairing, MonitorEventSeverity.Information, "正在处理网络驱动器映射")]
    [InlineData(MonitorState.AuthenticationFailed, MonitorEventSeverity.Error, "共享访问认证失败")]
    [InlineData(MonitorState.ShareUnavailable, MonitorEventSeverity.Warning, "共享目录当前不可用")]
    [InlineData(MonitorState.DriveConflict, MonitorEventSeverity.Error, "目标盘符存在映射冲突")]
    [InlineData(MonitorState.Paused, MonitorEventSeverity.Information, "监控已暂停")]
    [InlineData(MonitorState.InternalError, MonitorEventSeverity.Error, "监控过程中发生内部错误")]
    public void 状态转换生成固定脱敏描述(
        MonitorState state,
        MonitorEventSeverity severity,
        string description)
    {
        var decision = CreateDecision(state, "raw.target-or-error-must-not-appear");

        var previousState = state == MonitorState.Healthy
            ? MonitorState.NotConfigured
            : MonitorState.Healthy;
        var monitorEvent = MonitorEventFactory.Create(decision, previousState);

        Assert.NotNull(monitorEvent);
        Assert.Equal(severity, monitorEvent.Severity);
        Assert.Contains(description, monitorEvent.Description);
        Assert.DoesNotContain("raw.target", monitorEvent.Description);
        Assert.DoesNotContain("raw.target", monitorEvent.Code);
    }

    [Fact]
    public void 恢复事件使用信息级别和固定描述()
    {
        var decision = CreateDecision(
            MonitorState.Healthy,
            "state.recovered",
            shouldNotify: true);

        var monitorEvent = MonitorEventFactory.Create(decision, MonitorState.ServerOffline);

        Assert.NotNull(monitorEvent);
        Assert.Equal(MonitorEventSeverity.Information, monitorEvent.Severity);
        Assert.Equal("监控目标已恢复正常。", monitorEvent.Description);
    }

    [Fact]
    public void 状态未变化且无需通知时不生成事件()
    {
        var decision = CreateDecision(MonitorState.Healthy, "observation.no-change");

        Assert.Null(MonitorEventFactory.Create(decision, MonitorState.Healthy));
    }

    [Fact]
    public void 工厂拒绝空决策和未知状态()
    {
        Assert.Throws<ArgumentNullException>(
            () => MonitorEventFactory.Create(null!, MonitorState.Healthy));

        var decision = CreateDecision((MonitorState)999, "state.unknown");
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MonitorEventFactory.Create(decision, MonitorState.Healthy));
    }

    [Fact]
    public void 缓冲按新到旧排序并限制容量()
    {
        var buffer = new RecentEventBuffer(2);
        var first = CreateEvent(MonitorState.Checking, "checking", Now);
        var second = CreateEvent(MonitorState.ServerOffline, "offline", Now.AddSeconds(1));
        var third = CreateEvent(MonitorState.Healthy, "healthy", Now.AddSeconds(2));

        Assert.True(buffer.TryAdd(first));
        Assert.True(buffer.TryAdd(second));
        Assert.True(buffer.TryAdd(third));

        Assert.Equal([third, second], buffer.Snapshot());
    }

    [Fact]
    public void 缓冲抑制连续相同状态和代码但保留不同事件()
    {
        var buffer = new RecentEventBuffer();

        Assert.True(buffer.TryAdd(CreateEvent(MonitorState.ServerOffline, "offline", Now)));
        Assert.False(buffer.TryAdd(
            CreateEvent(MonitorState.ServerOffline, "offline", Now.AddSeconds(1))));
        Assert.True(buffer.TryAdd(
            CreateEvent(MonitorState.ServerOffline, "offline-again", Now.AddSeconds(2))));
        Assert.True(buffer.TryAdd(
            CreateEvent(MonitorState.Healthy, "offline-again", Now.AddSeconds(3))));
        Assert.Equal(3, buffer.Snapshot().Count);
    }

    [Fact]
    public void 缓冲快照不允许修改内部集合()
    {
        var buffer = new RecentEventBuffer();
        buffer.TryAdd(CreateEvent(MonitorState.Healthy, "healthy", Now));

        var snapshot = buffer.Snapshot();
        var array = Assert.IsType<MonitorEvent[]>(snapshot);
        array[0] = CreateEvent(MonitorState.InternalError, "changed", Now);

        Assert.Equal(MonitorState.Healthy, buffer.Snapshot()[0].State);
    }

    [Fact]
    public void 事件和缓冲参数受到校验()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecentEventBuffer(0));

        var buffer = new RecentEventBuffer();
        Assert.Throws<ArgumentNullException>(() => buffer.TryAdd(null!));
        Assert.Throws<ArgumentException>(
            () => new MonitorEvent(
                Now,
                MonitorState.Healthy,
                MonitorEventSeverity.Information,
                " ",
                "描述"));
        Assert.Throws<ArgumentException>(
            () => new MonitorEvent(
                Now,
                MonitorState.Healthy,
                MonitorEventSeverity.Information,
                "code",
                ""));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CreateEvent((MonitorState)999, "code", Now));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MonitorEvent(
                Now,
                MonitorState.Healthy,
                (MonitorEventSeverity)999,
                "code",
                "描述"));
        Assert.Throws<ArgumentException>(
            () => CreateEvent(MonitorState.Healthy, "invalid_code", Now));
        Assert.Throws<ArgumentException>(
            () => new MonitorEvent(
                Now,
                MonitorState.Healthy,
                MonitorEventSeverity.Information,
                new string('a', 65),
                "描述"));
        Assert.Throws<ArgumentException>(
            () => new MonitorEvent(
                Now,
                MonitorState.Healthy,
                MonitorEventSeverity.Information,
                "code",
                "第一行\n第二行"));
        Assert.Throws<ArgumentException>(
            () => new MonitorEvent(
                Now,
                MonitorState.Healthy,
                MonitorEventSeverity.Information,
                "code",
                new string('字', 201)));
    }

    private static MonitorDecision CreateDecision(
        MonitorState state,
        string code,
        bool shouldNotify = false)
    {
        return new MonitorDecision(
            new MonitorSnapshot(state, state, 0, Now, Now),
            MonitorAction.None,
            shouldNotify,
            code);
    }

    private static MonitorEvent CreateEvent(
        MonitorState state,
        string code,
        DateTimeOffset occurredAt)
    {
        return new MonitorEvent(
            occurredAt,
            state,
            MonitorEventSeverity.Information,
            code,
            "固定描述");
    }
}
