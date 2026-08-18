using NetDriveGuard.Core.Probing;

namespace NetDriveGuard.Core.Tests;

public sealed class TcpProbeOptionsTests
{
    [Fact]
    public void 默认值有效()
    {
        var options = new TcpProbeOptions("localhost");

        options.EnsureValid();

        Assert.Equal(445, options.Port);
        Assert.Equal(TimeSpan.FromMilliseconds(800), options.EffectiveTimeout);
    }

    [Fact]
    public void 自定义超时有效()
    {
        var timeout = TimeSpan.FromSeconds(2);
        var options = new TcpProbeOptions("localhost", 1445, timeout);

        options.EnsureValid();

        Assert.Equal(timeout, options.EffectiveTimeout);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void 空主机被拒绝(string host)
    {
        var options = new TcpProbeOptions(host);

        Assert.Throws<ArgumentException>(options.EnsureValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void 越界端口被拒绝(int port)
    {
        var options = new TcpProbeOptions("localhost", port);

        Assert.Throws<ArgumentOutOfRangeException>(options.EnsureValid);
    }

    [Theory]
    [InlineData(49)]
    [InlineData(30001)]
    public void 越界超时被拒绝(int timeoutMilliseconds)
    {
        var options = new TcpProbeOptions(
            "localhost",
            Timeout: TimeSpan.FromMilliseconds(timeoutMilliseconds));

        Assert.Throws<ArgumentOutOfRangeException>(options.EnsureValid);
    }
}
