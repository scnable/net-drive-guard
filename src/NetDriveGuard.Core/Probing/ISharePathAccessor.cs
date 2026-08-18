namespace NetDriveGuard.Core.Probing;

/// <summary>
/// 执行一次同步、只读的共享目录访问，由超时探测器负责隔离阻塞。
/// </summary>
public interface ISharePathAccessor
{
    ShareProbeStatus Check(string sharePath);
}
