namespace NetDriveGuard.Core.Startup;

public interface IStartupManager
{
    Task<StartupRegistrationState> QueryAsync(
        string executablePath,
        CancellationToken cancellationToken = default);

    Task<StartupOperationResult> EnableAsync(
        string executablePath,
        string userSid,
        CancellationToken cancellationToken = default);

    Task<StartupOperationResult> DisableAsync(
        string executablePath,
        CancellationToken cancellationToken = default);
}
