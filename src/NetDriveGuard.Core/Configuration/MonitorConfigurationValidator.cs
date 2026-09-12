using NetDriveGuard.Core.Scheduling;

namespace NetDriveGuard.Core.Configuration;

public static class MonitorConfigurationValidator
{
    public static ConfigurationValidationResult Validate(MonitorConfiguration? configuration)
    {
        if (configuration is null)
        {
            return new ConfigurationValidationResult(
                [new ConfigurationIssue("configuration.missing", "尚未提供监控配置。")]);
        }

        var issues = new List<ConfigurationIssue>();

        if (configuration.Language is not "zh-CN" and not "en")
        {
            issues.Add(new ConfigurationIssue("language.invalid", "请选择简体中文或 English。"));
        }

        if (!IsValidDriveLetter(configuration.DriveLetter))
        {
            issues.Add(new ConfigurationIssue("drive.invalid", "盘符必须使用类似 Z: 的格式。"));
        }

        if (!IsValidSharePath(configuration.SharePath))
        {
            issues.Add(new ConfigurationIssue("share.invalid", "共享地址必须包含服务器和共享名称。"));
        }

        if (configuration.OfflineFailureThreshold is < 2 or > 10)
        {
            issues.Add(new ConfigurationIssue("offline-threshold.out-of-range", "离线确认次数必须位于 2 到 10 之间。"));
        }

        if (configuration.EffectiveTcpTimeout < TimeSpan.FromMilliseconds(100)
            || configuration.EffectiveTcpTimeout > TimeSpan.FromSeconds(10))
        {
            issues.Add(new ConfigurationIssue("tcp-timeout.out-of-range", "TCP 超时必须位于 100 毫秒到 10 秒之间。"));
        }

        if (configuration.EffectiveOverallCheckTimeout <= configuration.EffectiveTcpTimeout
            || configuration.EffectiveOverallCheckTimeout > TimeSpan.FromMinutes(2))
        {
            issues.Add(new ConfigurationIssue("overall-timeout.invalid", "总检查超时必须大于 TCP 超时且不超过 2 分钟。"));
        }

        if (configuration.Profile == MonitoringProfile.Custom)
        {
            if (configuration.CustomTcpInterval is null)
            {
                issues.Add(new ConfigurationIssue("custom-interval.missing", "自定义模式必须提供检测周期。"));
            }
            else if (!MonitoringIntervals.IsCustomIntervalValid(configuration.CustomTcpInterval.Value))
            {
                issues.Add(new ConfigurationIssue("custom-interval.out-of-range", "自定义周期必须位于 1 秒到 30 分钟之间。"));
            }
        }

        return issues.Count == 0
            ? ConfigurationValidationResult.Success
            : new ConfigurationValidationResult(issues);
    }

    private static bool IsValidDriveLetter(string value)
    {
        return value.Length == 2
            && char.IsAsciiLetter(value[0])
            && value[1] == ':';
    }

    private static bool IsValidSharePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        var segments = value[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Length >= 2 && segments.All(segment => segment.Length > 0);
    }
}
