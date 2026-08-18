using NetDriveGuard.Core.Abstractions;
using NetDriveGuard.Core.Scheduling;

namespace NetDriveGuard.Core.Runtime;

/// <summary>
/// 根据当前状态和各层上次执行时间选择检查层，再交给协调器执行。
/// </summary>
public sealed class ScheduledMonitorRunner
{
    private readonly MonitorCoordinator _coordinator;
    private readonly LayeredSchedulePlanner _planner;
    private readonly IClock _clock;
    private readonly Lock _scheduleLock = new();
    private ScheduleSnapshot _scheduleSnapshot;

    public ScheduledMonitorRunner(
        MonitorCoordinator coordinator,
        LayeredSchedulePlanner planner,
        IClock clock,
        ScheduleSnapshot? initialSchedule = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _scheduleSnapshot = initialSchedule ?? new ScheduleSnapshot();
    }

    public ScheduleSnapshot ScheduleSnapshot
    {
        get
        {
            lock (_scheduleLock)
            {
                return _scheduleSnapshot;
            }
        }
    }

    public async Task<ScheduledMonitorRunResult> RunAsync(
        ScheduleTrigger trigger,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var requestedChecks = _planner.GetDueChecks(
            new ScheduleContext(
                _coordinator.Snapshot.CurrentState,
                now,
                trigger,
                ScheduleSnapshot));

        var run = await _coordinator
            .RunOnceAsync(requestedChecks, cancellationToken)
            .ConfigureAwait(false);

        if (run.Status == MonitorRunStatus.Completed)
        {
            UpdateSchedule(requestedChecks, _clock.UtcNow);
        }

        return new ScheduledMonitorRunResult(trigger, requestedChecks, run);
    }

    private void UpdateSchedule(CheckLayer completedChecks, DateTimeOffset completedAt)
    {
        lock (_scheduleLock)
        {
            _scheduleSnapshot = _scheduleSnapshot with
            {
                LastLocalMappingQueryAt = completedChecks.HasFlag(CheckLayer.LocalMapping)
                    ? completedAt
                    : _scheduleSnapshot.LastLocalMappingQueryAt,
                LastTcpProbeAt = completedChecks.HasFlag(CheckLayer.Tcp)
                    ? completedAt
                    : _scheduleSnapshot.LastTcpProbeAt,
                LastShareProbeAt = completedChecks.HasFlag(CheckLayer.Share)
                    ? completedAt
                    : _scheduleSnapshot.LastShareProbeAt,
            };
        }
    }
}
