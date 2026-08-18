using NetDriveGuard.Core.Scheduling;

namespace NetDriveGuard.Core.Abstractions;

/// <summary>
/// 只执行调度器明确要求的检查层，避免每个周期都访问高成本资源。
/// </summary>
public interface ILayeredProbe<TObservation> : IProbe<TObservation>
{
    Task<TObservation> ObserveAsync(
        CheckLayer checkLayers,
        CancellationToken cancellationToken);

    Task<TObservation> IProbe<TObservation>.ObserveAsync(CancellationToken cancellationToken)
    {
        return ObserveAsync(CheckLayer.All, cancellationToken);
    }
}
