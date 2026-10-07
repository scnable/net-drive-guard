using System.Runtime.InteropServices;
using System.Text;
using NetDriveGuard.Core.Mapping;

namespace NetDriveGuard.Windows;

/// <summary>
/// 通过 WNetGetConnection 查询现有映射，不建立、取消或修改任何映射。
/// </summary>
public sealed class WNetNetworkDriveQuery : INetworkDriveQuery
{
    private const int NoError = 0;
    private const int ErrorMoreData = 234;
    private const int ErrorBadDevice = 1200;
    private const int ErrorNotConnected = 2250;
    private const int InitialBufferLength = 512;
    private const int MaximumBufferLength = 32768;

    public NetworkDriveQueryResult Query(string driveLetter)
    {
        if (!TryNormalizeDriveLetter(driveLetter, out var normalizedDrive))
        {
            return new NetworkDriveQueryResult(NetworkDriveQueryStatus.InvalidDrive);
        }

        var bufferLength = InitialBufferLength;
        var remotePath = new StringBuilder(bufferLength);
        var result = WNetGetConnection(normalizedDrive, remotePath, ref bufferLength);

        if (result == ErrorMoreData)
        {
            if (bufferLength is <= 0 or > MaximumBufferLength)
            {
                return new NetworkDriveQueryResult(NetworkDriveQueryStatus.Error, NativeErrorCode: result);
            }

            remotePath = new StringBuilder(bufferLength);
            result = WNetGetConnection(normalizedDrive, remotePath, ref bufferLength);
        }

        return FromNativeResult(result, remotePath.ToString());
    }

    // 1201 表示持久映射仍占用盘符，但连接已断开；不能当成程序内部错误。
    internal static NetworkDriveQueryResult FromNativeResult(int result, string remotePath)
    {
        return result switch
        {
            NoError => new NetworkDriveQueryResult(NetworkDriveQueryStatus.Mapped, remotePath),
            1201 => new NetworkDriveQueryResult(NetworkDriveQueryStatus.Disconnected, remotePath, result),
            ErrorBadDevice or ErrorNotConnected => new NetworkDriveQueryResult(
                NetworkDriveQueryStatus.NotMapped,
                NativeErrorCode: result),
            _ => new NetworkDriveQueryResult(NetworkDriveQueryStatus.Error, NativeErrorCode: result),
        };
    }

    private static bool TryNormalizeDriveLetter(string value, out string normalized)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && value.Length == 2
            && char.IsAsciiLetter(value[0])
            && value[1] == ':')
        {
            normalized = string.Create(
                2,
                value,
                static (span, source) =>
                {
                    span[0] = char.ToUpperInvariant(source[0]);
                    span[1] = ':';
                });
            return true;
        }

        normalized = string.Empty;
        return false;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnection(
        string localName,
        StringBuilder remoteName,
        ref int length);
}
