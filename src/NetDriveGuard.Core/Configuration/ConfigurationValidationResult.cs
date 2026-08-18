namespace NetDriveGuard.Core.Configuration;

public sealed record ConfigurationIssue(string Code, string Message);

public sealed record ConfigurationValidationResult(IReadOnlyList<ConfigurationIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;

    public static ConfigurationValidationResult Success { get; } = new(Array.Empty<ConfigurationIssue>());
}
