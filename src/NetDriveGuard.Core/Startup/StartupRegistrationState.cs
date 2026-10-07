namespace NetDriveGuard.Core.Startup;

public enum StartupRegistrationStatus
{
    NotRegistered,
    Registered,
    Conflict,
    Error,
}

public sealed record StartupRegistrationState(
    StartupRegistrationStatus Status,
    string Code,
    bool PathNeedsUpdate = false,
    bool Enabled = true)
{
    public bool IsRegistered => Status == StartupRegistrationStatus.Registered && Enabled;

    /// <summary>保存时以系统实际任务为准；旧路径即使已经勾选，也需要更新。</summary>
    public bool RequiresUpdate(bool enabled) => enabled
        ? !IsRegistered || PathNeedsUpdate
        : Status != StartupRegistrationStatus.NotRegistered;
}
