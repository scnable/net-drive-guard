using NetDriveGuard.Core.Events;
using NetDriveGuard.Core.Monitoring;

namespace NetDriveGuard.Core.Tests;

public sealed class MonitorEventQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 2, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(MonitorEventFilter.All, 3)]
    [InlineData(MonitorEventFilter.WarningAndError, 2)]
    [InlineData(MonitorEventFilter.ErrorOnly, 1)]
    public void 按严重级别筛选并保持原有顺序(MonitorEventFilter filter, int expectedCount)
    {
        var information = CreateEvent(MonitorEventSeverity.Information, "event.info");
        var warning = CreateEvent(MonitorEventSeverity.Warning, "event.warning");
        var error = CreateEvent(MonitorEventSeverity.Error, "event.error");
        MonitorEvent[] source = [information, warning, error];

        var result = MonitorEventQuery.Apply(source, filter);

        Assert.Equal(expectedCount, result.Count);
        Assert.Equal(
            source.Where(item => result.Contains(item)),
            result);
    }

    [Fact]
    public void 空集合返回空快照()
    {
        var result = MonitorEventQuery.Apply([], MonitorEventFilter.All);

        Assert.Empty(result);
        Assert.IsType<MonitorEvent[]>(result);
    }

    [Fact]
    public void 空输入和未知筛选被拒绝()
    {
        Assert.Throws<ArgumentNullException>(
            () => MonitorEventQuery.Apply(null!, MonitorEventFilter.All));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MonitorEventQuery.Apply([], (MonitorEventFilter)999));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MonitorEventQuery.Apply(
                [],
                MonitorEventFilter.All,
                (MonitorState)999));
    }

    [Fact]
    public void 严重级别和监控状态能够组合筛选()
    {
        MonitorEvent[] source =
        [
            CreateEvent(
                MonitorEventSeverity.Warning,
                "event.checking",
                MonitorState.Checking),
            CreateEvent(
                MonitorEventSeverity.Error,
                "event.offline",
                MonitorState.ServerOffline),
            CreateEvent(
                MonitorEventSeverity.Warning,
                "event.share",
                MonitorState.ShareUnavailable),
        ];

        var result = MonitorEventQuery.Apply(
            source,
            MonitorEventFilter.WarningAndError,
            MonitorState.ShareUnavailable);

        var monitorEvent = Assert.Single(result);
        Assert.Equal("event.share", monitorEvent.Code);
    }

    [Fact]
    public void 只指定状态时保留该状态的所有严重级别()
    {
        MonitorEvent[] source =
        [
            CreateEvent(
                MonitorEventSeverity.Information,
                "event.healthy",
                MonitorState.Healthy),
            CreateEvent(
                MonitorEventSeverity.Error,
                "event.internal",
                MonitorState.InternalError),
        ];

        var result = MonitorEventQuery.Apply(
            source,
            MonitorEventFilter.All,
            MonitorState.Healthy);

        Assert.Equal([source[0]], result);
    }

    private static MonitorEvent CreateEvent(
        MonitorEventSeverity severity,
        string code,
        MonitorState state = MonitorState.Healthy)
    {
        return new MonitorEvent(
            Now,
            state,
            severity,
            code,
            "固定脱敏描述。");
    }
}
