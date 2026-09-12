namespace NetDriveGuard.Core.Migration;

public enum LegacyConflictStatus
{
    None,
    Conflict,
    Error,
}

public sealed record LegacyConflictResult(LegacyConflictStatus Status, string Code)
{
    public bool HasConflict => Status == LegacyConflictStatus.Conflict;
}
