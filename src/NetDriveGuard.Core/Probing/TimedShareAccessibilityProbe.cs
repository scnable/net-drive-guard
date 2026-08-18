using System.Diagnostics;

namespace NetDriveGuard.Core.Probing;

/// <summary>
/// 为不可取消的目录访问提供调用方超时，并保证同一时刻最多只有一个底层访问任务。
/// </summary>
public sealed class TimedShareAccessibilityProbe : IShareAccessibilityProbe
{
    private readonly ShareProbeOptions _options;
    private readonly ISharePathAccessor _accessor;
    private readonly Lock _sync = new();
    private Task<ShareProbeStatus>? _inFlight;

    public TimedShareAccessibilityProbe(
        ShareProbeOptions options,
        ISharePathAccessor accessor)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.EnsureValid();
        _options = options;
        _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
    }

    public async Task<ShareProbeResult> ProbeAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startedAt = Stopwatch.GetTimestamp();
        Task<ShareProbeStatus> operation;
        lock (_sync)
        {
            if (_inFlight is null || _inFlight.IsCompleted)
            {
                _inFlight = Task.Run(AccessShareSafely, CancellationToken.None);
            }

            operation = _inFlight;
        }

        try
        {
            var status = await operation
                .WaitAsync(_options.EffectiveTimeout, cancellationToken)
                .ConfigureAwait(false);
            return Result(status, startedAt);
        }
        catch (TimeoutException)
        {
            return Result(ShareProbeStatus.TimedOut, startedAt);
        }
    }

    private ShareProbeStatus AccessShareSafely()
    {
        try
        {
            var status = _accessor.Check(_options.SharePath);
            return Enum.IsDefined(status) ? status : ShareProbeStatus.Failed;
        }
        catch
        {
            return ShareProbeStatus.Failed;
        }
    }

    private static ShareProbeResult Result(ShareProbeStatus status, long startedAt)
    {
        return new ShareProbeResult(
            status,
            Stopwatch.GetElapsedTime(startedAt),
            status switch
            {
                ShareProbeStatus.Accessible => "share.accessible",
                ShareProbeStatus.NotFound => "share.not-found",
                ShareProbeStatus.AccessDenied => "share.access-denied",
                ShareProbeStatus.TimedOut => "share.timed-out",
                ShareProbeStatus.Unavailable => "share.unavailable",
                _ => "share.failed",
            });
    }
}
