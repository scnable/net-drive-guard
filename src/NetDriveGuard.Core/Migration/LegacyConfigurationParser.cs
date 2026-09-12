using System.Globalization;
using System.Text.RegularExpressions;
using NetDriveGuard.Core.Configuration;

namespace NetDriveGuard.Core.Migration;

/// <summary>
/// 只解析明确允许的旧脚本字面量，不执行脚本，也不保留用户或密码字段。
/// </summary>
public static class LegacyConfigurationParser
{
    private static readonly Regex SensitiveAssignment = new(
        @"(?i)\b(?:User|UserName|Pass|Password|Credential|Token|Secret)\s*=",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SafeStringAssignment = new(
        "^\\s*(?<key>Drive|UNC)\\s*=\\s*(?<quote>['\\\"])(?<value>[^\\r\\n]*?)\\k<quote>\\s*;?\\s*(?:#.*)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SafeIntegerAssignment = new(
        @"^\s*\$(?<key>RetryCount|TimeoutMs)\s*=\s*(?<value>\d+)\s*(?:#.*)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static LegacyConfigurationImportResult Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        string? driveLetter = null;
        string? sharePath = null;
        var retryCount = 3;
        var tcpTimeout = TimeSpan.FromMilliseconds(800);

        foreach (var line in lines.Take(2000))
        {
            if (string.IsNullOrWhiteSpace(line) || SensitiveAssignment.IsMatch(line))
            {
                continue;
            }

            var stringMatch = SafeStringAssignment.Match(line);
            if (stringMatch.Success)
            {
                var stringValue = stringMatch.Groups["value"].Value.Trim();
                if (stringValue.Contains('$', StringComparison.Ordinal)
                    || stringValue.Contains('`', StringComparison.Ordinal))
                {
                    continue;
                }

                var key = stringMatch.Groups["key"].Value;
                if (key.Equals("Drive", StringComparison.OrdinalIgnoreCase))
                {
                    driveLetter = stringValue;
                }
                else if (key.Equals("UNC", StringComparison.OrdinalIgnoreCase))
                {
                    sharePath = stringValue;
                }

                continue;
            }

            var integerMatch = SafeIntegerAssignment.Match(line);
            if (!integerMatch.Success
                || !int.TryParse(
                    integerMatch.Groups["value"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var integerValue))
            {
                continue;
            }

            var integerKey = integerMatch.Groups["key"].Value;
            if (integerKey.Equals("RetryCount", StringComparison.OrdinalIgnoreCase)
                && integerValue is >= 2 and <= 10)
            {
                retryCount = integerValue;
            }
            else if (integerKey.Equals("TimeoutMs", StringComparison.OrdinalIgnoreCase)
                     && integerValue is >= 100 and <= 10000)
            {
                tcpTimeout = TimeSpan.FromMilliseconds(integerValue);
            }
        }

        if (driveLetter is null || sharePath is null)
        {
            return new LegacyConfigurationImportResult(
                false,
                "legacy.configuration-not-found");
        }

        var configuration = new MonitorConfiguration(
            driveLetter.Trim().ToUpperInvariant(),
            sharePath.Trim(),
            OfflineFailureThreshold: retryCount,
            RemoveMappingWhenOffline: false,
            RepairMissingMapping: false,
            StartAtLogon: false,
            TcpTimeout: tcpTimeout,
            OverallCheckTimeout: TimeSpan.FromSeconds(30));
        if (!MonitorConfigurationValidator.Validate(configuration).IsValid)
        {
            return new LegacyConfigurationImportResult(
                false,
                "legacy.configuration-invalid");
        }

        return new LegacyConfigurationImportResult(
            true,
            "legacy.configuration-imported",
            configuration,
            [
                "用户名和密码未导入，需要时请在设置中重新填写。",
                "自动恢复、离线取消和登录启动均保持关闭。",
            ]);
    }
}
