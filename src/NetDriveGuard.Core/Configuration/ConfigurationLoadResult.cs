namespace NetDriveGuard.Core.Configuration;

public sealed record ConfigurationLoadResult(
    ConfigurationLoadStatus Status,
    MonitorConfiguration? Configuration = null,
    IReadOnlyList<ConfigurationIssue>? Issues = null)
{
    public bool IsLoaded => Status == ConfigurationLoadStatus.Loaded && Configuration is not null;

    public IReadOnlyList<ConfigurationIssue> EffectiveIssues =>
        Issues ?? Array.Empty<ConfigurationIssue>();
}
