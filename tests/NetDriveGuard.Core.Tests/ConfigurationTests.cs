using NetDriveGuard.Core.Configuration;
using NetDriveGuard.Core.Scheduling;

namespace NetDriveGuard.Core.Tests;

public class ConfigurationTests
{
    [Fact]
    public void 合法配置通过验证且不包含密码字段()
    {
        var configuration = ValidConfiguration();

        var result = MonitorConfigurationValidator.Validate(configuration);

        Assert.True(result.IsValid);
        Assert.DoesNotContain(
            typeof(MonitorConfiguration).GetProperties(),
            property => property.Name.Contains("Password", StringComparison.OrdinalIgnoreCase));
        Assert.False(configuration.RemoveMappingWhenOffline);
    }

    [Fact]
    public void 空配置返回明确问题()
    {
        var result = MonitorConfigurationValidator.Validate(null);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "configuration.missing");
    }

    [Theory]
    [InlineData("Z", "drive.invalid")]
    [InlineData("1:", "drive.invalid")]
    [InlineData("", "drive.invalid")]
    public void 非法盘符被拒绝(string drive, string expectedCode)
    {
        var result = MonitorConfigurationValidator.Validate(ValidConfiguration() with { DriveLetter = drive });
        Assert.Contains(result.Issues, issue => issue.Code == expectedCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("server")]
    [InlineData("server\\share")]
    public void 非法共享地址被拒绝(string share)
    {
        var result = MonitorConfigurationValidator.Validate(ValidConfiguration() with { SharePath = share });
        Assert.Contains(result.Issues, issue => issue.Code == "share.invalid");
    }

    [Fact]
    public void 自定义模式必须提供范围内周期()
    {
        var missing = MonitorConfigurationValidator.Validate(
            ValidConfiguration() with { Profile = MonitoringProfile.Custom, CustomTcpInterval = null });
        var tooFast = MonitorConfigurationValidator.Validate(
            ValidConfiguration() with
            {
                Profile = MonitoringProfile.Custom,
                CustomTcpInterval = TimeSpan.FromMilliseconds(500),
            });

        Assert.Contains(missing.Issues, issue => issue.Code == "custom-interval.missing");
        Assert.Contains(tooFast.Issues, issue => issue.Code == "custom-interval.out-of-range");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(11)]
    public void 离线确认次数越界时被拒绝(int threshold)
    {
        var result = MonitorConfigurationValidator.Validate(
            ValidConfiguration() with { OfflineFailureThreshold = threshold });

        Assert.Contains(result.Issues, issue => issue.Code == "offline-threshold.out-of-range");
    }

    [Theory]
    [InlineData(50)]
    [InlineData(11000)]
    public void Tcp超时越界时被拒绝(int milliseconds)
    {
        var result = MonitorConfigurationValidator.Validate(
            ValidConfiguration() with { TcpTimeout = TimeSpan.FromMilliseconds(milliseconds) });

        Assert.Contains(result.Issues, issue => issue.Code == "tcp-timeout.out-of-range");
    }

    [Fact]
    public void 合法自定义周期和显式超时通过验证()
    {
        var configuration = ValidConfiguration() with
        {
            Profile = MonitoringProfile.Custom,
            CustomTcpInterval = TimeSpan.FromSeconds(2),
            TcpTimeout = TimeSpan.FromMilliseconds(500),
            OverallCheckTimeout = TimeSpan.FromSeconds(5),
        };

        Assert.True(MonitorConfigurationValidator.Validate(configuration).IsValid);
        Assert.Equal(TimeSpan.FromMilliseconds(500), configuration.EffectiveTcpTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), configuration.EffectiveOverallCheckTimeout);
    }

    [Fact]
    public void 总超时必须大于Tcp超时()
    {
        var result = MonitorConfigurationValidator.Validate(
            ValidConfiguration() with
            {
                TcpTimeout = TimeSpan.FromSeconds(2),
                OverallCheckTimeout = TimeSpan.FromSeconds(2),
            });

        Assert.Contains(result.Issues, issue => issue.Code == "overall-timeout.invalid");
    }

    [Fact]
    public void 总超时不能超过两分钟()
    {
        var result = MonitorConfigurationValidator.Validate(
            ValidConfiguration() with { OverallCheckTimeout = TimeSpan.FromMinutes(3) });

        Assert.Contains(result.Issues, issue => issue.Code == "overall-timeout.invalid");
    }

    [Fact]
    public void 配置Json往返保持内容且包含版本()
    {
        var configuration = ValidConfiguration() with
        {
            Profile = MonitoringProfile.Custom,
            CustomTcpInterval = TimeSpan.FromSeconds(8),
            OfflineFailureThreshold = 4,
        };

        var json = MonitorConfigurationJson.Serialize(configuration);
        var result = MonitorConfigurationJson.Deserialize(json);

        Assert.Contains("\"schemaVersion\": 1", json, StringComparison.Ordinal);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.IsLoaded);
        Assert.Equal(configuration, result.Configuration);
        Assert.Empty(result.EffectiveIssues);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{not-json")]
    [InlineData("{}")]
    public void 空白损坏或缺失内容安全降级(string json)
    {
        var result = MonitorConfigurationJson.Deserialize(json);

        Assert.Equal(ConfigurationLoadStatus.Invalid, result.Status);
        Assert.False(result.IsLoaded);
        Assert.NotEmpty(result.EffectiveIssues);
    }

    [Fact]
    public void 过高或过低配置版本均被拒绝()
    {
        const string template = """
            {
              "schemaVersion": VERSION,
              "configuration": {
                "driveLetter": "Z:",
                "sharePath": "\\\\server\\share"
              }
            }
            """;

        foreach (var version in new[] { 0, 2 })
        {
            var result = MonitorConfigurationJson.Deserialize(
                template.Replace("VERSION", version.ToString(), StringComparison.Ordinal));

            Assert.Equal(ConfigurationLoadStatus.UnsupportedVersion, result.Status);
            Assert.False(result.IsLoaded);
        }
    }

    [Fact]
    public void Json中的无效业务配置不会被加载()
    {
        var json = """
            {
              "schemaVersion": 1,
              "configuration": {
                "driveLetter": "1:",
                "sharePath": "not-a-share"
              }
            }
            """;

        var result = MonitorConfigurationJson.Deserialize(json);

        Assert.Equal(ConfigurationLoadStatus.Invalid, result.Status);
        Assert.Contains(result.EffectiveIssues, issue => issue.Code == "drive.invalid");
        Assert.Contains(result.EffectiveIssues, issue => issue.Code == "share.invalid");
    }

    [Fact]
    public void 无效配置不能被序列化()
    {
        Assert.Throws<ArgumentException>(
            () => MonitorConfigurationJson.Serialize(ValidConfiguration() with { DriveLetter = "bad" }));
    }

    private static MonitorConfiguration ValidConfiguration() => new(
        "Z:",
        @"\\server\share");
}
