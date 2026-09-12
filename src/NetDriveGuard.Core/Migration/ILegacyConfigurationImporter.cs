namespace NetDriveGuard.Core.Migration;

public interface ILegacyConfigurationImporter
{
    Task<LegacyConfigurationImportResult> ImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default);
}
