using NetDriveGuard.Core.Abstractions;
using NetDriveGuard.Core.Configuration;
using NetDriveGuard.Core.Mapping;
using NetDriveGuard.Core.Monitoring;
using NetDriveGuard.Core.Probing;
using NetDriveGuard.Core.Scheduling;

namespace NetDriveGuard.Windows;

/// <summary>
/// 组合 WNet 查询与 TCP 短连接，只观察配置目标，不执行映射写操作。
/// </summary>
public sealed class ReadOnlyNetworkDriveProbe : ILayeredProbe<MonitorObservation>
{
    private readonly MonitorConfiguration _configuration;
    private readonly INetworkDriveQuery _mappingQuery;
    private readonly ITcpPortProbe _tcpProbe;
    private readonly IShareAccessibilityProbe? _shareProbe;
    private readonly IClock _clock;
    private readonly Lock _cacheLock = new();
    private NetworkDriveQueryResult? _lastMapping;
    private TcpProbeStatus? _lastTcpStatus;
    private ShareProbeStatus? _lastShareStatus;

    public ReadOnlyNetworkDriveProbe(MonitorConfiguration configuration, IClock clock)
        : this(
            configuration,
            new WNetNetworkDriveQuery(),
            new TcpPortProbe(
                new TcpProbeOptions(
                    GetServerName(configuration),
                    Timeout: configuration.EffectiveTcpTimeout)),
            clock,
            new TimedShareAccessibilityProbe(
                new ShareProbeOptions(
                    configuration.SharePath,
                    GetShareTimeout(configuration)),
                new DirectorySharePathAccessor()))
    {
    }

    public ReadOnlyNetworkDriveProbe(
        MonitorConfiguration configuration,
        INetworkDriveQuery mappingQuery,
        ITcpPortProbe tcpProbe,
        IClock clock,
        IShareAccessibilityProbe? shareProbe = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var validation = MonitorConfigurationValidator.Validate(configuration);
        if (!validation.IsValid)
        {
            throw new ArgumentException("只读探测器需要有效配置。", nameof(configuration));
        }

        _configuration = configuration;
        _mappingQuery = mappingQuery ?? throw new ArgumentNullException(nameof(mappingQuery));
        _tcpProbe = tcpProbe ?? throw new ArgumentNullException(nameof(tcpProbe));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _shareProbe = shareProbe;
    }

    public async Task<MonitorObservation> ObserveAsync(
        CheckLayer checkLayers,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (checkLayers == CheckLayer.None || (checkLayers & ~CheckLayer.All) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(checkLayers), "请求包含未知或空检查层。");
        }

        NetworkDriveQueryResult? mapping = null;
        TcpProbeResult? tcp = null;
        ShareProbeResult? share = null;

        if (checkLayers.HasFlag(CheckLayer.LocalMapping))
        {
            mapping = _mappingQuery.Query(_configuration.DriveLetter);
        }

        if (checkLayers.HasFlag(CheckLayer.Tcp))
        {
            tcp = await _tcpProbe.ProbeAsync(cancellationToken).ConfigureAwait(false);
        }

        if (checkLayers.HasFlag(CheckLayer.Share) && _shareProbe is not null)
        {
            share = await _shareProbe.ProbeAsync(cancellationToken).ConfigureAwait(false);
        }

        var status = Evaluate(mapping, tcp, share);
        return new MonitorObservation(status, _clock.UtcNow, GetObservationCode(status));
    }

    private ObservationStatus Evaluate(
        NetworkDriveQueryResult? currentMapping,
        TcpProbeResult? currentTcp,
        ShareProbeResult? currentShare)
    {
        lock (_cacheLock)
        {
            if (currentMapping is not null)
            {
                _lastMapping = currentMapping;
            }

            if (currentTcp is not null)
            {
                _lastTcpStatus = currentTcp.Status;
            }

            if (currentShare is not null)
            {
                _lastShareStatus = currentShare.Status;
            }

            if (currentTcp is not null)
            {
                if (currentTcp.Status == TcpProbeStatus.Failed)
                {
                    return ObservationStatus.InternalError;
                }

                if (currentTcp.Status != TcpProbeStatus.Reachable)
                {
                    return ObservationStatus.ServerUnreachable;
                }
            }
            else if (_lastTcpStatus is not null and not TcpProbeStatus.Reachable)
            {
                return ObservationStatus.NoChange;
            }

            if (_lastMapping is null)
            {
                return currentTcp?.Status == TcpProbeStatus.Reachable
                    ? ObservationStatus.Healthy
                    : ObservationStatus.NoChange;
            }

            var mappingProblem = _lastMapping.Status switch
            {
                NetworkDriveQueryStatus.Error => ObservationStatus.InternalError,
                NetworkDriveQueryStatus.InvalidDrive => ObservationStatus.InternalError,
                NetworkDriveQueryStatus.NotMapped => ObservationStatus.MappingMissing,
                NetworkDriveQueryStatus.Mapped when !TargetsConfiguredShare(_lastMapping.RemotePath) =>
                    ObservationStatus.DriveConflict,
                _ => (ObservationStatus?)null,
            };

            if (mappingProblem is not null)
            {
                return mappingProblem.Value;
            }

            var shareProblem = _lastShareStatus switch
            {
                ShareProbeStatus.AccessDenied => ObservationStatus.AuthenticationFailed,
                ShareProbeStatus.NotFound or ShareProbeStatus.TimedOut or ShareProbeStatus.Unavailable =>
                    ObservationStatus.ShareUnavailable,
                ShareProbeStatus.Failed => ObservationStatus.InternalError,
                _ => (ObservationStatus?)null,
            };
            if (shareProblem is not null)
            {
                return shareProblem.Value;
            }

            return currentTcp?.Status == TcpProbeStatus.Reachable
                || currentShare?.Status == ShareProbeStatus.Accessible
                ? ObservationStatus.Healthy
                : ObservationStatus.NoChange;
        }
    }

    private bool TargetsConfiguredShare(string? remotePath)
    {
        return string.Equals(
            NormalizeSharePath(remotePath),
            NormalizeSharePath(_configuration.SharePath),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeSharePath(string? path)
    {
        return (path ?? string.Empty).Trim().TrimEnd('\\');
    }

    private static string GetObservationCode(ObservationStatus status)
    {
        return status switch
        {
            ObservationStatus.NoChange => "observation.no-change",
            ObservationStatus.Healthy => "observation.healthy",
            ObservationStatus.ServerUnreachable => "server.unreachable",
            ObservationStatus.MappingMissing => "mapping.missing",
            ObservationStatus.DriveConflict => "mapping.conflict",
            ObservationStatus.AuthenticationFailed => "share.access-denied",
            ObservationStatus.ShareUnavailable => "share.unavailable",
            ObservationStatus.InternalError => "observation.internal-error",
            _ => "observation.completed",
        };
    }

    private static string GetServerName(MonitorConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var validation = MonitorConfigurationValidator.Validate(configuration);
        if (!validation.IsValid)
        {
            throw new ArgumentException("共享地址配置无效。", nameof(configuration));
        }

        return configuration.SharePath[2..]
            .Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
    }

    private static TimeSpan GetShareTimeout(MonitorConfiguration configuration)
    {
        var remaining = configuration.EffectiveOverallCheckTimeout
            - configuration.EffectiveTcpTimeout;
        return TimeSpan.FromMilliseconds(
            Math.Clamp(
                remaining.TotalMilliseconds,
                100,
                TimeSpan.FromSeconds(5).TotalMilliseconds));
    }
}
