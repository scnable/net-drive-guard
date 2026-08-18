namespace NetDriveGuard.Core.Scheduling;

public enum ScheduleTrigger
{
    Timer,
    Startup,
    Resume,
    NetworkChanged,
    Manual,
    ConfigurationSaved,
}
