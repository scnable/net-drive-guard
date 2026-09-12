using NetDriveGuard.Core.Migration;

namespace NetDriveGuard.Windows;

public sealed class LegacyScheduledTaskConflictDetector : ILegacyConflictDetector
{
    internal const string LegacyTaskName = "NetDriveCheck";
    private readonly IScheduledTaskBackend _backend;

    public LegacyScheduledTaskConflictDetector()
        : this(new ComScheduledTaskBackend())
    {
    }

    internal LegacyScheduledTaskConflictDetector(IScheduledTaskBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    public async Task<LegacyConflictResult> DetectAsync(
        CancellationToken cancellationToken = default)
    {
        var query = await _backend.QueryAsync(LegacyTaskName, cancellationToken);
        return query.Status switch
        {
            ScheduledTaskQueryStatus.NotFound =>
                new LegacyConflictResult(LegacyConflictStatus.None, "legacy.no-conflict"),
            ScheduledTaskQueryStatus.Found =>
                new LegacyConflictResult(LegacyConflictStatus.Conflict, "legacy.task-conflict"),
            _ => new LegacyConflictResult(LegacyConflictStatus.Error, "legacy.query-failed"),
        };
    }
}
