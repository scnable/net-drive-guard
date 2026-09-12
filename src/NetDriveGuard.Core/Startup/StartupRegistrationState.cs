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
    string Code)
{
    public bool IsRegistered => Status == StartupRegistrationStatus.Registered;
}
