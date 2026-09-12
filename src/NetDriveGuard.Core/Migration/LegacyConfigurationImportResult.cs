using NetDriveGuard.Core.Configuration;

namespace NetDriveGuard.Core.Migration;

public sealed record LegacyConfigurationImportResult(
    bool Success,
    string Code,
    MonitorConfiguration? Configuration = null,
    IReadOnlyList<string>? Warnings = null)
{
    public IReadOnlyList<string> EffectiveWarnings => Warnings ?? [];
}
