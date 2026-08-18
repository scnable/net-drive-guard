using NetDriveGuard.Core.Events;
using NetDriveGuard.Core.Monitoring;
using NetDriveGuard.Windows;

namespace NetDriveGuard.Windows.Tests;

public sealed class JsonLinesMonitorEventStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task 不存在的事件文件返回空集合且不创建目录()
    {
        var path = NewEventPath("missing");
        using var store = new JsonLinesMonitorEventStore(path);

        var events = await store.LoadRecentAsync(100);

        Assert.Empty(events);
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }

    [Fact]
    public async Task 追加事件后按新到旧恢复指定数量()
    {
        var path = NewEventPath("append-load");
        using var store = new JsonLinesMonitorEventStore(path);
        var first = CreateEvent(Now, MonitorState.Checking, "state.checking");
        var second = CreateEvent(Now.AddSeconds(1), MonitorState.ServerOffline, "state.server-offline");
        var third = CreateEvent(Now.AddSeconds(2), MonitorState.Healthy, "state.recovered");

        await store.AppendAsync(first);
        await store.AppendAsync(second);
        await store.AppendAsync(third);

        var events = await store.LoadRecentAsync(2);
        var lines = await File.ReadAllLinesAsync(path);
        Assert.Equal([third, second], events);
        Assert.Equal(3, lines.Length);
        Assert.All(lines, line => Assert.DoesNotContain("password", line, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task 损坏行和不合法事件被忽略而完整事件仍可恢复()
    {
        var path = NewEventPath("invalid-lines");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.AppendAllLinesAsync(
            path,
            [
                "not-json",
                "{\"occurredAt\":\"2026-08-19T01:00:00+00:00\",\"state\":999,\"severity\":0,\"code\":\"bad\",\"description\":\"无效\"}",
            ]);
        using var store = new JsonLinesMonitorEventStore(path);
        var expected = CreateEvent(Now, MonitorState.Healthy, "state.healthy");
        await store.AppendAsync(expected);

        var events = await store.LoadRecentAsync(100);

        Assert.Equal([expected], events);
    }

    [Fact]
    public async Task 并发追加由存储内部串行化且每行都能恢复()
    {
        var path = NewEventPath("concurrent");
        using var store = new JsonLinesMonitorEventStore(path);
        var events = Enumerable.Range(0, 20)
            .Select(index => CreateEvent(
                Now.AddSeconds(index),
                MonitorState.Checking,
                $"state.checking-{index}"))
            .ToArray();

        await Task.WhenAll(events.Select(monitorEvent => store.AppendAsync(monitorEvent)));

        var loaded = await store.LoadRecentAsync(100);
        Assert.Equal(events.Length, loaded.Count);
        Assert.Equal(events.Select(item => item.Code).Order(), loaded.Select(item => item.Code).Order());
    }

    [Fact]
    public async Task 尾部读取跳过被截断的首行并恢复后续完整事件()
    {
        var path = NewEventPath("tail");
        using var store = new JsonLinesMonitorEventStore(path, maxReadBytes: 256);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.AppendAllTextAsync(path, new string('x', 512) + Environment.NewLine);
        var expected = CreateEvent(Now, MonitorState.Healthy, "state.healthy");
        await store.AppendAsync(expected);

        var events = await store.LoadRecentAsync(100);

        Assert.Equal([expected], events);
    }

    [Fact]
    public async Task 参数和取消状态受到校验()
    {
        Assert.Throws<ArgumentException>(() => new JsonLinesMonitorEventStore(" "));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new JsonLinesMonitorEventStore(NewEventPath("small"), 127));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new JsonLinesMonitorEventStore(NewEventPath("large"), 16 * 1024 * 1024 + 1));

        using var store = new JsonLinesMonitorEventStore(NewEventPath("validation"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.AppendAsync(null!));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.LoadRecentAsync(0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.LoadRecentAsync(1001));

        using var source = new CancellationTokenSource();
        await source.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.AppendAsync(
                CreateEvent(Now, MonitorState.Healthy, "state.healthy"),
                source.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.LoadRecentAsync(1, source.Token));
    }

    private static MonitorEvent CreateEvent(
        DateTimeOffset occurredAt,
        MonitorState state,
        string code)
    {
        return new MonitorEvent(
            occurredAt,
            state,
            MonitorEventSeverity.Information,
            code,
            "固定脱敏描述。");
    }

    private static string NewEventPath(string scenario)
    {
        return Path.Combine(
            AppContext.BaseDirectory,
            "TestResults",
            "event-store",
            scenario,
            Guid.NewGuid().ToString("N"),
            "events.jsonl");
    }
}
