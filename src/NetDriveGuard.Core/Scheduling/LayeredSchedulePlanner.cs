using NetDriveGuard.Core.Monitoring;

namespace NetDriveGuard.Core.Scheduling;

public sealed class LayeredSchedulePlanner(MonitoringIntervals intervals)
{
    private readonly MonitoringIntervals _intervals = intervals
        ?? throw new ArgumentNullException(nameof(intervals));

    public CheckLayer GetDueChecks(ScheduleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.State == MonitorState.Paused)
        {
            return CheckLayer.None;
        }

        if (context.State == MonitorState.NotConfigured
            && context.Trigger != ScheduleTrigger.ConfigurationSaved)
        {
            return CheckLayer.None;
        }

        if (context.Trigger is not ScheduleTrigger.Timer)
        {
            return CheckLayer.All;
        }

        var due = CheckLayer.None;
        if (IsDue(context.Snapshot.LastLocalMappingQueryAt, _intervals.LocalMappingQuery, context.Now))
        {
            due |= CheckLayer.LocalMapping;
        }

        var tcpInterval = context.State switch
        {
            MonitorState.Checking => _intervals.FailureRetry,
            MonitorState.ServerOffline => _intervals.OfflineRecovery,
            _ => _intervals.TcpProbe,
        };

        if (IsDue(context.Snapshot.LastTcpProbeAt, tcpInterval, context.Now))
        {
            due |= CheckLayer.Tcp;
        }

        var canProbeShare = context.State is not MonitorState.ServerOffline
            and not MonitorState.Checking;
        if (canProbeShare && IsDue(context.Snapshot.LastShareProbeAt, _intervals.ShareProbe, context.Now))
        {
            due |= CheckLayer.Share;
        }

        return due;
    }

    private static bool IsDue(DateTimeOffset? lastRun, TimeSpan interval, DateTimeOffset now)
    {
        if (lastRun is null || now < lastRun.Value)
        {
            return true;
        }

        return now - lastRun.Value >= interval;
    }
}
