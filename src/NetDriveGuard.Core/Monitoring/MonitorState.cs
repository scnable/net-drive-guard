namespace NetDriveGuard.Core.Monitoring;

public enum MonitorState
{
    NotConfigured,
    Checking,
    Healthy,
    ServerOffline,
    MappingMissing,
    Repairing,
    AuthenticationFailed,
    ShareUnavailable,
    DriveConflict,
    Paused,
    InternalError,
}
