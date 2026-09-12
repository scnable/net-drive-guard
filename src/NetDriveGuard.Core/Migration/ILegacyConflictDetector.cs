namespace NetDriveGuard.Core.Migration;

public interface ILegacyConflictDetector
{
    Task<LegacyConflictResult> DetectAsync(CancellationToken cancellationToken = default);
}
