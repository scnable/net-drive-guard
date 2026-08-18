using NetDriveGuard.Core.Abstractions;
using NetDriveGuard.Core.Monitoring;
using NetDriveGuard.Core.Scheduling;

namespace NetDriveGuard.Core.Simulation;

/// <summary>
/// 仅用于测试和显式演示模式的确定性探测器，不访问网络或系统资源。
/// </summary>
public sealed class ScriptedMonitorProbe : ILayeredProbe<MonitorObservation>
{
    private readonly IClock _clock;
    private readonly ObservationStatus[] _sequence;
    private readonly CheckLayer _advanceOn;
    private readonly Lock _sync = new();
    private int _index = -1;

    public ScriptedMonitorProbe(
        IClock clock,
        IEnumerable<ObservationStatus> sequence,
        CheckLayer advanceOn = CheckLayer.Tcp)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        ArgumentNullException.ThrowIfNull(sequence);
        _sequence = sequence.ToArray();
        if (_sequence.Length == 0)
        {
            throw new ArgumentException("演示状态序列不能为空。", nameof(sequence));
        }

        if (advanceOn == CheckLayer.None || (advanceOn & ~CheckLayer.All) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(advanceOn), "推进层必须是已知检查层。");
        }

        _advanceOn = advanceOn;
    }

    public Task<MonitorObservation> ObserveAsync(
        CheckLayer checkLayers,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (checkLayers == CheckLayer.None || (checkLayers & ~CheckLayer.All) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(checkLayers), "请求包含未知或空检查层。");
        }

        ObservationStatus status;
        lock (_sync)
        {
            if (checkLayers.HasFlag(_advanceOn))
            {
                _index = Math.Min(_index + 1, _sequence.Length - 1);
                status = _sequence[_index];
            }
            else
            {
                status = ObservationStatus.NoChange;
            }
        }

        return Task.FromResult(
            new MonitorObservation(
                status,
                _clock.UtcNow,
                status == ObservationStatus.NoChange
                    ? "simulation.no-change"
                    : $"simulation.{status.ToString().ToLowerInvariant()}"));
    }
}
