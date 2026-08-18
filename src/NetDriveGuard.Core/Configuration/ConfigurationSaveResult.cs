namespace NetDriveGuard.Core.Configuration;

public sealed record ConfigurationSaveResult(bool Success, string Code)
{
    public static ConfigurationSaveResult Saved { get; } = new(true, "configuration.saved");

    public static ConfigurationSaveResult Failed(string code) => new(false, code);
}
