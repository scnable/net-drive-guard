using System.Net;
using System.Net.Sockets;
using NetDriveGuard.Core.Probing;

namespace NetDriveGuard.Windows.Tests;

public class TcpPortProbeTests
{
    [Fact]
    public async Task 回环监听端口被识别为可达()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var acceptTask = listener.AcceptTcpClientAsync();
            var probe = new TcpPortProbe(new TcpProbeOptions("127.0.0.1", port, TimeSpan.FromSeconds(1)));

            var result = await probe.ProbeAsync().WaitAsync(TimeSpan.FromSeconds(2));
            using var accepted = await acceptTask.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(result.IsReachable);
            Assert.Equal(TcpProbeStatus.Reachable, result.Status);
            Assert.Equal("tcp.reachable", result.Code);
            Assert.True(result.Duration >= TimeSpan.Zero);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task 已关闭的回环端口不会被识别为可达()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var probe = new TcpPortProbe(new TcpProbeOptions("127.0.0.1", port, TimeSpan.FromSeconds(1)));

        var result = await probe.ProbeAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(result.IsReachable);
        Assert.NotEqual(TcpProbeStatus.Reachable, result.Status);
    }

    [Fact]
    public async Task 外部取消被传递给调用方()
    {
        using var source = new CancellationTokenSource();
        await source.CancelAsync();
        var probe = new TcpPortProbe(new TcpProbeOptions("127.0.0.1", 445));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => probe.ProbeAsync(source.Token));
    }

    [Theory]
    [InlineData("", 445, 800)]
    [InlineData("localhost", 0, 800)]
    [InlineData("localhost", 65536, 800)]
    [InlineData("localhost", 445, 10)]
    [InlineData("localhost", 445, 31000)]
    public void 非法探测参数在构造时被拒绝(string host, int port, int timeoutMilliseconds)
    {
        var options = new TcpProbeOptions(host, port, TimeSpan.FromMilliseconds(timeoutMilliseconds));
        Assert.ThrowsAny<ArgumentException>(() => new TcpPortProbe(options));
    }
}
