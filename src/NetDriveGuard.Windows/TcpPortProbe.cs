using System.Diagnostics;
using System.Net.Sockets;
using NetDriveGuard.Core.Probing;

namespace NetDriveGuard.Windows;

/// <summary>
/// 使用短连接执行只读 TCP 探测，不发送应用层数据。
/// </summary>
public sealed class TcpPortProbe : ITcpPortProbe
{
    private readonly TcpProbeOptions _options;

    public TcpPortProbe(TcpProbeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.EnsureValid();
        _options = options;
    }

    public async Task<TcpProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var startedAt = Stopwatch.GetTimestamp();
        using var client = new TcpClient();

        try
        {
            await client
                .ConnectAsync(_options.Host, _options.Port, cancellationToken)
                .AsTask()
                .WaitAsync(_options.EffectiveTimeout, cancellationToken)
                .ConfigureAwait(false);

            return Result(TcpProbeStatus.Reachable, "tcp.reachable", startedAt);
        }
        catch (TimeoutException)
        {
            return Result(TcpProbeStatus.TimedOut, "tcp.timed-out", startedAt);
        }
        catch (SocketException exception)
        {
            return MapSocketError(exception.SocketErrorCode, startedAt);
        }
    }

    private static TcpProbeResult MapSocketError(SocketError error, long startedAt)
    {
        return error switch
        {
            SocketError.ConnectionRefused => Result(TcpProbeStatus.Refused, "tcp.refused", startedAt),
            SocketError.TimedOut => Result(TcpProbeStatus.TimedOut, "tcp.timed-out", startedAt),
            SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain =>
                Result(TcpProbeStatus.NameResolutionFailed, "tcp.name-resolution-failed", startedAt),
            SocketError.HostUnreachable or SocketError.NetworkUnreachable =>
                Result(TcpProbeStatus.Unreachable, "tcp.unreachable", startedAt),
            _ => Result(TcpProbeStatus.Failed, "tcp.failed", startedAt),
        };
    }

    private static TcpProbeResult Result(TcpProbeStatus status, string code, long startedAt)
    {
        return new TcpProbeResult(status, Stopwatch.GetElapsedTime(startedAt), code);
    }
}
