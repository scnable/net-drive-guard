namespace NetDriveGuard.Core.Abstractions;

/// <summary>
/// 表示一个只负责收集观察结果的监控探测器。
/// </summary>
public interface IProbe<TObservation>
{
    Task<TObservation> ObserveAsync(CancellationToken cancellationToken);
}
