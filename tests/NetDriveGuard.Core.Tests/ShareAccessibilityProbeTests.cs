using NetDriveGuard.Core.Probing;

namespace NetDriveGuard.Core.Tests;

public sealed class ShareAccessibilityProbeTests
{
    [Theory]
    [InlineData(ShareProbeStatus.Accessible, "share.accessible")]
    [InlineData(ShareProbeStatus.NotFound, "share.not-found")]
    [InlineData(ShareProbeStatus.AccessDenied, "share.access-denied")]
    [InlineData(ShareProbeStatus.Unavailable, "share.unavailable")]
    public async Task 访问结果被转换为稳定代码(ShareProbeStatus status, string expectedCode)
    {
        var probe = new TimedShareAccessibilityProbe(
            new ShareProbeOptions(@"\\server\share"),
            new FixedAccessor(status));

        var result = await probe.ProbeAsync();

        Assert.Equal(status, result.Status);
        Assert.Equal(expectedCode, result.Code);
        Assert.True(result.Duration >= TimeSpan.Zero);
    }

    [Fact]
    public async Task 超时期间重复调用复用同一个底层任务()
    {
        using var release = new ManualResetEventSlim();
        var accessor = new BlockingAccessor(release);
        var probe = new TimedShareAccessibilityProbe(
            new ShareProbeOptions(@"\\server\share", TimeSpan.FromMilliseconds(100)),
            accessor);

        var first = await probe.ProbeAsync();
        var second = await probe.ProbeAsync();
        release.Set();

        Assert.Equal(ShareProbeStatus.TimedOut, first.Status);
        Assert.Equal(ShareProbeStatus.TimedOut, second.Status);
        Assert.Equal(1, accessor.CallCount);
    }

    [Fact]
    public async Task 调用方取消被传递且底层异常被归一化()
    {
        using var source = new CancellationTokenSource();
        await source.CancelAsync();
        var normal = new TimedShareAccessibilityProbe(
            new ShareProbeOptions(@"\\server\share"),
            new FixedAccessor(ShareProbeStatus.Accessible));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => normal.ProbeAsync(source.Token));

        var throwing = new TimedShareAccessibilityProbe(
            new ShareProbeOptions(@"\\server\share"),
            new ThrowingAccessor());
        var result = await throwing.ProbeAsync();
        Assert.Equal(ShareProbeStatus.Failed, result.Status);
    }

    [Fact]
    public void 非法路径超时和空访问器被拒绝()
    {
        Assert.Throws<ArgumentException>(
            () => new TimedShareAccessibilityProbe(
                new ShareProbeOptions("not-unc"),
                new FixedAccessor(ShareProbeStatus.Accessible)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TimedShareAccessibilityProbe(
                new ShareProbeOptions(@"\\server\share", TimeSpan.FromMilliseconds(50)),
                new FixedAccessor(ShareProbeStatus.Accessible)));
        Assert.Throws<ArgumentNullException>(
            () => new TimedShareAccessibilityProbe(
                new ShareProbeOptions(@"\\server\share"),
                null!));
    }

    private sealed class FixedAccessor(ShareProbeStatus status) : ISharePathAccessor
    {
        public ShareProbeStatus Check(string sharePath) => status;
    }

    private sealed class BlockingAccessor(ManualResetEventSlim release) : ISharePathAccessor
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public ShareProbeStatus Check(string sharePath)
        {
            Interlocked.Increment(ref _callCount);
            release.Wait();
            return ShareProbeStatus.Accessible;
        }
    }

    private sealed class ThrowingAccessor : ISharePathAccessor
    {
        public ShareProbeStatus Check(string sharePath)
        {
            throw new IOException("模拟只读访问异常。");
        }
    }
}
