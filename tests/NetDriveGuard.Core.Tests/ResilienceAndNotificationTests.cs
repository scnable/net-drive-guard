using NetDriveGuard.Core.Abstractions;
using NetDriveGuard.Core.Notifications;
using NetDriveGuard.Core.Resilience;

namespace NetDriveGuard.Core.Tests;

public class ResilienceAndNotificationTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(5, 10)]
    [InlineData(20, 10)]
    public void 网络退避按指数增长并限制为十秒(int failures, int expectedSeconds)
    {
        var delay = BackoffPolicy.GetDelay(FailureCategory.Network, failures);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    [Fact]
    public void 认证退避最终限制为十分钟()
    {
        Assert.Equal(
            TimeSpan.FromMinutes(10),
            BackoffPolicy.GetDelay(FailureCategory.Authentication, 20));
    }

    [Fact]
    public void 内部错误退避最终限制为一分钟()
    {
        Assert.Equal(
            TimeSpan.FromMinutes(1),
            BackoffPolicy.GetDelay(FailureCategory.Internal, 20));
    }

    [Fact]
    public void 零失败次数被拒绝()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BackoffPolicy.GetDelay(FailureCategory.Internal, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BackoffPolicy.GetDelay((FailureCategory)999, 1));
    }

    [Fact]
    public void 相同通知在冷却时间内只允许一次()
    {
        var gate = new NotificationGate(TimeSpan.FromMinutes(5));
        var now = DateTimeOffset.UtcNow;

        Assert.True(gate.TryRegister("server.offline", now));
        Assert.False(gate.TryRegister("server.offline", now.AddMinutes(1)));
        Assert.True(gate.TryRegister("server.offline", now.AddMinutes(5)));
    }

    [Fact]
    public void 不同通知键和重置后的通知互不阻塞()
    {
        var gate = new NotificationGate(TimeSpan.FromMinutes(5));
        var now = DateTimeOffset.UtcNow;

        Assert.True(gate.TryRegister("server.offline", now));
        Assert.True(gate.TryRegister("server.recovered", now));
        gate.Reset("server.offline");
        Assert.True(gate.TryRegister("server.offline", now.AddSeconds(1)));
    }

    [Fact]
    public void 系统时间回拨时允许重新登记通知()
    {
        var gate = new NotificationGate(TimeSpan.FromMinutes(5));
        var now = DateTimeOffset.UtcNow;

        Assert.True(gate.TryRegister("state.changed", now));
        Assert.True(gate.TryRegister("state.changed", now.AddMinutes(-1)));
    }

    [Fact]
    public void 通知参数校验拒绝负冷却和空键()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NotificationGate(TimeSpan.FromSeconds(-1)));

        var gate = new NotificationGate(TimeSpan.Zero);
        Assert.Throws<ArgumentException>(() => gate.TryRegister("", DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => gate.Reset(" "));
    }

    [Fact]
    public void 修复结果工厂返回稳定代码和结果()
    {
        var success = RemediationResult.Succeeded();
        var failure = RemediationResult.Failed("remediation.failed");

        Assert.True(success.Success);
        Assert.Equal("remediation.succeeded", success.Code);
        Assert.False(failure.Success);
        Assert.Equal("remediation.failed", failure.Code);
    }
}
