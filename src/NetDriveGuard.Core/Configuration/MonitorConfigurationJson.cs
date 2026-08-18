using System.Text.Json;

namespace NetDriveGuard.Core.Configuration;

public static class MonitorConfigurationJson
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string Serialize(MonitorConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var validation = MonitorConfigurationValidator.Validate(configuration);
        if (!validation.IsValid)
        {
            throw new ArgumentException("不能序列化无效监控配置。", nameof(configuration));
        }

        return JsonSerializer.Serialize(
            new ConfigurationDocument(CurrentSchemaVersion, configuration),
            SerializerOptions);
    }

    public static ConfigurationLoadResult Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Invalid("configuration.empty", "配置内容为空。");
        }

        ConfigurationDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<ConfigurationDocument>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            return Invalid("configuration.malformed", "配置 JSON 格式无效。");
        }
        catch (NotSupportedException)
        {
            return Invalid("configuration.unsupported-content", "配置包含不支持的内容。");
        }

        if (document is null || document.Configuration is null)
        {
            return Invalid("configuration.missing-content", "配置缺少监控内容。");
        }

        if (document.SchemaVersion != CurrentSchemaVersion)
        {
            return new ConfigurationLoadResult(
                ConfigurationLoadStatus.UnsupportedVersion,
                Issues:
                [
                    new ConfigurationIssue(
                        "configuration.version-unsupported",
                        "配置版本不受当前程序支持。"),
                ]);
        }

        var validation = MonitorConfigurationValidator.Validate(document.Configuration);
        return validation.IsValid
            ? new ConfigurationLoadResult(ConfigurationLoadStatus.Loaded, document.Configuration)
            : new ConfigurationLoadResult(
                ConfigurationLoadStatus.Invalid,
                Issues: validation.Issues);
    }

    private static ConfigurationLoadResult Invalid(string code, string message)
    {
        return new ConfigurationLoadResult(
            ConfigurationLoadStatus.Invalid,
            Issues: [new ConfigurationIssue(code, message)]);
    }

    private sealed record ConfigurationDocument(
        int SchemaVersion,
        MonitorConfiguration? Configuration);
}
