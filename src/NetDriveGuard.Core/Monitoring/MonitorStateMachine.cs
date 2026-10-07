using NetDriveGuard.Core.Abstractions;

namespace NetDriveGuard.Core.Monitoring;

/// <summary>
/// 纯逻辑状态机：只产生决策，不直接执行网络或系统操作。
/// </summary>
public sealed class MonitorStateMachine(MonitorPolicy policy)
    : IStateEvaluator<MonitorObservation, MonitorSnapshot, MonitorDecision>
{
    private readonly MonitorPolicy _policy = ValidatePolicy(policy);

    public MonitorDecision Evaluate(MonitorObservation observation, MonitorSnapshot previousState)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(previousState);

        return observation.Status switch
        {
            ObservationStatus.NoChange => ObserveWithoutTransition(previousState, observation),

            ObservationStatus.NotConfigured => Confirm(
                previousState,
                observation,
                MonitorState.NotConfigured,
                MonitorAction.None,
                false,
                "state.not-configured"),

            ObservationStatus.Paused => Confirm(
                previousState,
                observation,
                MonitorState.Paused,
                MonitorAction.None,
                false,
                "state.paused"),

            ObservationStatus.Healthy => ConfirmHealthy(previousState, observation),
            ObservationStatus.ServerUnreachable => ConfirmServerFailure(previousState, observation),
            ObservationStatus.MappingMissing => ConfirmMappingMissing(previousState, observation),

            ObservationStatus.Repairing => Pending(
                previousState,
                observation,
                MonitorState.Repairing,
                "state.repairing"),

            ObservationStatus.AuthenticationFailed => ConfirmProblem(
                previousState,
                observation,
                MonitorState.AuthenticationFailed,
                "state.authentication-failed"),

            ObservationStatus.ShareUnavailable => ConfirmProblem(
                previousState,
                observation,
                MonitorState.ShareUnavailable,
                "state.share-unavailable"),

            ObservationStatus.DriveConflict => ConfirmProblem(
                previousState,
                observation,
                MonitorState.DriveConflict,
                "state.drive-conflict"),

            ObservationStatus.InternalError => ConfirmProblem(
                previousState,
                observation,
                MonitorState.InternalError,
                "state.internal-error"),

            ObservationStatus.MappingFailed => ConfirmProblem(
                previousState,
                observation,
                MonitorState.MappingFailed,
                observation.Code),

            _ => throw new ArgumentOutOfRangeException(nameof(observation), observation.Status, "未知观察状态。"),
        };
    }

    private static MonitorDecision ObserveWithoutTransition(
        MonitorSnapshot previous,
        MonitorObservation observation)
    {
        return new MonitorDecision(
            previous with { LastObservationAt = observation.ObservedAt },
            MonitorAction.None,
            false,
            observation.Code);
    }

    private MonitorDecision ConfirmHealthy(MonitorSnapshot previous, MonitorObservation observation)
    {
        var shouldNotify = previous.LastStableState is MonitorState.ServerOffline
            or MonitorState.MappingMissing
            or MonitorState.Repairing
            or MonitorState.AuthenticationFailed
            or MonitorState.ShareUnavailable
            or MonitorState.DriveConflict
            or MonitorState.InternalError
            or MonitorState.MappingFailed;

        return Confirm(
            previous,
            observation,
            MonitorState.Healthy,
            MonitorAction.None,
            shouldNotify,
            shouldNotify ? "state.recovered" : "state.healthy");
    }

    private MonitorDecision ConfirmServerFailure(MonitorSnapshot previous, MonitorObservation observation)
    {
        var failures = previous.ConsecutiveServerFailures + 1;
        if (failures < _policy.OfflineFailureThreshold)
        {
            var transitionAt = previous.CurrentState == MonitorState.Checking
                ? previous.LastTransitionAt
                : observation.ObservedAt;

            return new MonitorDecision(
                previous with
                {
                    CurrentState = MonitorState.Checking,
                    ConsecutiveServerFailures = failures,
                    LastTransitionAt = transitionAt,
                    LastObservationAt = observation.ObservedAt,
                },
                MonitorAction.None,
                false,
                "server.failure-unconfirmed");
        }

        var transitionedToOffline = previous.LastStableState != MonitorState.ServerOffline;
        var action = transitionedToOffline && _policy.RemoveMappingWhenOffline
            ? MonitorAction.RemoveMapping
            : MonitorAction.None;

        return Confirm(
            previous with { ConsecutiveServerFailures = failures },
            observation,
            MonitorState.ServerOffline,
            action,
            transitionedToOffline,
            transitionedToOffline ? "server.offline" : "server.still-offline",
            failures);
    }

    private MonitorDecision ConfirmMappingMissing(
        MonitorSnapshot previous,
        MonitorObservation observation)
    {
        var shouldRepair = _policy.RepairMissingMapping
            && previous.CurrentState is not MonitorState.MappingMissing and not MonitorState.Repairing;
        return Confirm(
            previous,
            observation,
            MonitorState.MappingMissing,
            shouldRepair ? MonitorAction.RepairMapping : MonitorAction.None,
            false,
            shouldRepair ? "mapping.repair-requested" : "mapping.missing");
    }

    private static MonitorDecision ConfirmProblem(
        MonitorSnapshot previous,
        MonitorObservation observation,
        MonitorState problemState,
        string eventCode)
    {
        // 离线确认途中偶发的内部错误先留在面板，不抢在离线结论前弹窗。
        // 保留上一个稳定状态；下一次仍是内部错误时再确认并提醒，避免持续故障被隐藏。
        if (problemState == MonitorState.InternalError
            && previous.CurrentState == MonitorState.Checking)
        {
            return Pending(previous, observation, problemState, eventCode);
        }

        return Confirm(
            previous,
            observation,
            problemState,
            MonitorAction.None,
            previous.LastStableState != problemState,
            eventCode);
    }

    private static MonitorDecision Pending(
        MonitorSnapshot previous,
        MonitorObservation observation,
        MonitorState pendingState,
        string eventCode)
    {
        var transitionAt = previous.CurrentState == pendingState
            ? previous.LastTransitionAt
            : observation.ObservedAt;

        return new MonitorDecision(
            previous with
            {
                CurrentState = pendingState,
                ConsecutiveServerFailures = 0,
                LastTransitionAt = transitionAt,
                LastObservationAt = observation.ObservedAt,
            },
            MonitorAction.None,
            false,
            eventCode);
    }

    private static MonitorDecision Confirm(
        MonitorSnapshot previous,
        MonitorObservation observation,
        MonitorState state,
        MonitorAction action,
        bool shouldNotify,
        string eventCode,
        int consecutiveFailures = 0)
    {
        var transitionAt = previous.CurrentState == state
            ? previous.LastTransitionAt
            : observation.ObservedAt;

        return new MonitorDecision(
            new MonitorSnapshot(
                state,
                state,
                consecutiveFailures,
                transitionAt,
                observation.ObservedAt),
            action,
            shouldNotify,
            eventCode);
    }

    private static MonitorPolicy ValidatePolicy(MonitorPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.EnsureValid();
        return policy;
    }
}
