using NetDriveGuard.Core.Abstractions;
using NetDriveGuard.Core.Monitoring;
using NetDriveGuard.Core.Scheduling;

namespace NetDriveGuard.Core.Runtime;

/// <summary>
/// 串联单次探测、状态决策和精确修复动作，并阻止并发检查。
/// </summary>
public sealed class MonitorCoordinator : IDisposable
{
    private readonly IProbe<MonitorObservation> _probe;
    private readonly IRemediation<MonitorAction> _remediation;
    private readonly IClock _clock;
    private readonly MonitorStateMachine _stateMachine;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly Lock _snapshotLock = new();
    private MonitorSnapshot _snapshot;
    private bool _disposed;

    public MonitorCoordinator(
        IProbe<MonitorObservation> probe,
        IRemediation<MonitorAction> remediation,
        IClock clock,
        MonitorStateMachine stateMachine,
        MonitorSnapshot initialSnapshot)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _remediation = remediation ?? throw new ArgumentNullException(nameof(remediation));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _stateMachine = stateMachine ?? throw new ArgumentNullException(nameof(stateMachine));
        _snapshot = initialSnapshot ?? throw new ArgumentNullException(nameof(initialSnapshot));
    }

    public MonitorSnapshot Snapshot
    {
        get
        {
            lock (_snapshotLock)
            {
                return _snapshot;
            }
        }
    }

    public async Task<MonitorRunResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        return await RunOnceAsync(CheckLayer.All, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MonitorRunResult> RunOnceAsync(
        CheckLayer checkLayers,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (checkLayers == CheckLayer.None)
        {
            return new MonitorRunResult(MonitorRunStatus.SkippedNoChecks);
        }

        if ((checkLayers & ~CheckLayer.All) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(checkLayers),
                checkLayers,
                "包含未知检查层。");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new MonitorRunResult(MonitorRunStatus.Cancelled);
        }

        if (!await _runGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new MonitorRunResult(MonitorRunStatus.SkippedAlreadyRunning);
        }

        try
        {
            MonitorObservation observation;
            try
            {
                observation = _probe is ILayeredProbe<MonitorObservation> layeredProbe
                    ? await layeredProbe
                        .ObserveAsync(checkLayers, cancellationToken)
                        .ConfigureAwait(false)
                    : await _probe.ObserveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new MonitorRunResult(MonitorRunStatus.Cancelled);
            }
            catch (Exception)
            {
                observation = new MonitorObservation(
                    ObservationStatus.InternalError,
                    _clock.UtcNow,
                    "probe.unhandled-error");
            }

            var decision = _stateMachine.Evaluate(observation, Snapshot);
            RemediationResult? remediationResult = null;

            if (decision.Action != MonitorAction.None)
            {
                try
                {
                    remediationResult = await _remediation
                        .ExecuteAsync(decision.Action, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return new MonitorRunResult(MonitorRunStatus.Cancelled);
                }
                catch (Exception)
                {
                    remediationResult = RemediationResult.Failed("remediation.unhandled-error");
                }

                if (!remediationResult.Success)
                {
                    decision = _stateMachine.Evaluate(
                        new MonitorObservation(
                            remediationResult.Code == "remediation.unhandled-error"
                                ? ObservationStatus.InternalError
                                : ObservationStatus.MappingFailed,
                            _clock.UtcNow,
                            remediationResult.Code),
                        decision.Snapshot with { LastStableState = Snapshot.LastStableState });
                }
            }

            SetSnapshot(decision.Snapshot);
            return new MonitorRunResult(MonitorRunStatus.Completed, decision, remediationResult);
        }
        finally
        {
            _runGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _runGate.Dispose();
        _disposed = true;
    }

    private void SetSnapshot(MonitorSnapshot snapshot)
    {
        lock (_snapshotLock)
        {
            _snapshot = snapshot;
        }
    }
}
