using NetDriveGuard.Core.Abstractions;
using NetDriveGuard.Core.Configuration;
using NetDriveGuard.Core.Mapping;
using NetDriveGuard.Core.Monitoring;
using NetDriveGuard.Core.Probing;
using NetDriveGuard.Core.Scheduling;
using NetDriveGuard.Windows;

namespace NetDriveGuard.Windows.Tests;

public sealed class ReadOnlyNetworkDriveProbeTests
{
    [Fact]
    public async Task 映射目标正确且Tcp可达时返回健康()
    {
        var mapping = new FakeMappingQuery(
            new NetworkDriveQueryResult(NetworkDriveQueryStatus.Mapped, @"\\server\share\"));
        var tcp = new FakeTcpProbe(TcpProbeStatus.Reachable);
        var probe = CreateProbe(mapping, tcp);

        var result = await probe.ObserveAsync(CheckLayer.All, CancellationToken.None);

        Assert.Equal(ObservationStatus.Healthy, result.Status);
        Assert.Equal(["Z:"], mapping.Requests);
        Assert.Equal(1, tcp.CallCount);
    }

    [Fact]
    public async Task Tcp失败优先于映射缺失并在仅本地复查时保持状态()
    {
        var mapping = new FakeMappingQuery(
            new NetworkDriveQueryResult(NetworkDriveQueryStatus.NotMapped));
        var tcp = new FakeTcpProbe(TcpProbeStatus.TimedOut);
        var probe = CreateProbe(mapping, tcp);

        var failed = await probe.ObserveAsync(CheckLayer.All, CancellationToken.None);
        var localOnly = await probe.ObserveAsync(CheckLayer.LocalMapping, CancellationToken.None);

        Assert.Equal(ObservationStatus.ServerUnreachable, failed.Status);
        Assert.Equal(ObservationStatus.NoChange, localOnly.Status);
    }

    [Fact]
    public async Task 服务器可达时区分映射缺失和盘符冲突()
    {
        var missing = CreateProbe(
            new FakeMappingQuery(new NetworkDriveQueryResult(NetworkDriveQueryStatus.NotMapped)),
            new FakeTcpProbe(TcpProbeStatus.Reachable));
        var conflict = CreateProbe(
            new FakeMappingQuery(
                new NetworkDriveQueryResult(NetworkDriveQueryStatus.Mapped, @"\\other\share")),
            new FakeTcpProbe(TcpProbeStatus.Reachable));

        var missingResult = await missing.ObserveAsync(CheckLayer.All, CancellationToken.None);
        var conflictResult = await conflict.ObserveAsync(CheckLayer.All, CancellationToken.None);

        Assert.Equal(ObservationStatus.MappingMissing, missingResult.Status);
        Assert.Equal(ObservationStatus.DriveConflict, conflictResult.Status);
    }

    [Theory]
    [InlineData(NetworkDriveQueryStatus.Error, TcpProbeStatus.Reachable)]
    [InlineData(NetworkDriveQueryStatus.Mapped, TcpProbeStatus.Failed)]
    public async Task 系统查询或Tcp未知失败归一化为内部错误(
        NetworkDriveQueryStatus mappingStatus,
        TcpProbeStatus tcpStatus)
    {
        var probe = CreateProbe(
            new FakeMappingQuery(
                new NetworkDriveQueryResult(mappingStatus, @"\\server\share")),
            new FakeTcpProbe(tcpStatus));

        var result = await probe.ObserveAsync(CheckLayer.All, CancellationToken.None);

        Assert.Equal(ObservationStatus.InternalError, result.Status);
    }

    [Fact]
    public async Task 共享层尚未实现时返回无变化且不访问映射或Tcp()
    {
        var mapping = new FakeMappingQuery(
            new NetworkDriveQueryResult(NetworkDriveQueryStatus.Mapped, @"\\server\share"));
        var tcp = new FakeTcpProbe(TcpProbeStatus.Reachable);
        var probe = CreateProbe(mapping, tcp);

        var result = await probe.ObserveAsync(CheckLayer.Share, CancellationToken.None);

        Assert.Equal(ObservationStatus.NoChange, result.Status);
        Assert.Empty(mapping.Requests);
        Assert.Equal(0, tcp.CallCount);
    }

    [Theory]
    [InlineData(ShareProbeStatus.NotFound, ObservationStatus.ShareUnavailable)]
    [InlineData(ShareProbeStatus.TimedOut, ObservationStatus.ShareUnavailable)]
    [InlineData(ShareProbeStatus.Unavailable, ObservationStatus.ShareUnavailable)]
    [InlineData(ShareProbeStatus.AccessDenied, ObservationStatus.AuthenticationFailed)]
    [InlineData(ShareProbeStatus.Failed, ObservationStatus.InternalError)]
    public async Task 共享访问结果映射为明确观察状态(
        ShareProbeStatus shareStatus,
        ObservationStatus expected)
    {
        var probe = CreateProbe(
            new FakeMappingQuery(
                new NetworkDriveQueryResult(NetworkDriveQueryStatus.Mapped, @"\\server\share")),
            new FakeTcpProbe(TcpProbeStatus.Reachable),
            new FakeShareProbe(shareStatus));

        var result = await probe.ObserveAsync(CheckLayer.All, CancellationToken.None);

        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public async Task 共享恢复后在共享检查层返回健康()
    {
        var share = new QueueShareProbe(
            ShareProbeStatus.Unavailable,
            ShareProbeStatus.Accessible);
        var probe = CreateProbe(
            new FakeMappingQuery(
                new NetworkDriveQueryResult(NetworkDriveQueryStatus.Mapped, @"\\server\share")),
            new FakeTcpProbe(TcpProbeStatus.Reachable),
            share);

        var unavailable = await probe.ObserveAsync(CheckLayer.All, CancellationToken.None);
        var recovered = await probe.ObserveAsync(CheckLayer.Share, CancellationToken.None);

        Assert.Equal(ObservationStatus.ShareUnavailable, unavailable.Status);
        Assert.Equal(ObservationStatus.Healthy, recovered.Status);
    }

    [Fact]
    public async Task 非法配置检查层和取消均被拒绝()
    {
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        Assert.Throws<ArgumentException>(
            () => new ReadOnlyNetworkDriveProbe(
                new MonitorConfiguration("bad", "bad"),
                new FakeMappingQuery(new NetworkDriveQueryResult(NetworkDriveQueryStatus.NotMapped)),
                new FakeTcpProbe(TcpProbeStatus.Reachable),
                clock));

        var probe = CreateProbe(
            new FakeMappingQuery(new NetworkDriveQueryResult(NetworkDriveQueryStatus.NotMapped)),
            new FakeTcpProbe(TcpProbeStatus.Reachable));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => probe.ObserveAsync(CheckLayer.None, CancellationToken.None));

        using var source = new CancellationTokenSource();
        await source.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => probe.ObserveAsync(CheckLayer.All, source.Token));
    }

    private static ReadOnlyNetworkDriveProbe CreateProbe(
        INetworkDriveQuery mapping,
        ITcpPortProbe tcp,
        IShareAccessibilityProbe? share = null)
    {
        return new ReadOnlyNetworkDriveProbe(
            new MonitorConfiguration("Z:", @"\\server\share"),
            mapping,
            tcp,
            new FixedClock(DateTimeOffset.UtcNow),
            share);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class FakeMappingQuery(NetworkDriveQueryResult result) : INetworkDriveQuery
    {
        public List<string> Requests { get; } = [];

        public NetworkDriveQueryResult Query(string driveLetter)
        {
            Requests.Add(driveLetter);
            return result;
        }
    }

    private sealed class FakeTcpProbe(TcpProbeStatus status) : ITcpPortProbe
    {
        public int CallCount { get; private set; }

        public Task<TcpProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(
                new TcpProbeResult(status, TimeSpan.FromMilliseconds(1), "tcp.test"));
        }
    }

    private sealed class FakeShareProbe(ShareProbeStatus status) : IShareAccessibilityProbe
    {
        public Task<ShareProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                new ShareProbeResult(status, TimeSpan.FromMilliseconds(1), "share.test"));
        }
    }

    private sealed class QueueShareProbe(params ShareProbeStatus[] statuses) : IShareAccessibilityProbe
    {
        private readonly Queue<ShareProbeStatus> _statuses = new(statuses);

        public Task<ShareProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = _statuses.Dequeue();
            return Task.FromResult(
                new ShareProbeResult(status, TimeSpan.FromMilliseconds(1), "share.test"));
        }
    }
}
