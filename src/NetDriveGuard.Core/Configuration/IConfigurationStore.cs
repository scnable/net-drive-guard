namespace NetDriveGuard.Core.Configuration;

public interface IConfigurationStore
{
    Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken = default);

    Task<ConfigurationSaveResult> SaveAsync(
        MonitorConfiguration configuration,
        CancellationToken cancellationToken = default);
}
