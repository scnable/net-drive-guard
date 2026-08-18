using System.Security;
using NetDriveGuard.Core.Probing;

namespace NetDriveGuard.Windows;

/// <summary>
/// 通过枚举目录的第一项验证共享可访问性，不创建、修改或删除任何内容。
/// </summary>
public sealed class DirectorySharePathAccessor : ISharePathAccessor
{
    public ShareProbeStatus Check(string sharePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sharePath);

        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(sharePath).GetEnumerator();
            _ = entries.MoveNext();
            return ShareProbeStatus.Accessible;
        }
        catch (DirectoryNotFoundException)
        {
            return ShareProbeStatus.NotFound;
        }
        catch (UnauthorizedAccessException)
        {
            return ShareProbeStatus.AccessDenied;
        }
        catch (SecurityException)
        {
            return ShareProbeStatus.AccessDenied;
        }
        catch (IOException)
        {
            return ShareProbeStatus.Unavailable;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return ShareProbeStatus.Failed;
        }
    }
}
