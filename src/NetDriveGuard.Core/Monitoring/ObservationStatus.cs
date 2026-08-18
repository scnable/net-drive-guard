namespace NetDriveGuard.Core.Monitoring;

public enum ObservationStatus
{
    NoChange,
    NotConfigured,
    Paused,
    Healthy,
    ServerUnreachable,
    MappingMissing,
    Repairing,
    AuthenticationFailed,
    ShareUnavailable,
    DriveConflict,
    InternalError,
}
