using NetDriveGuard.Core.Configuration;

namespace NetDriveGuard.Core.Mapping;

/// <summary>
/// 在任何 WNet 写操作之前生成安全计划，本类型不修改系统映射。
/// </summary>
public static class MappingOperationPlanner
{
    /// <summary>根据刚查询的盘符状态决定是否允许操作；查询失败或被其他共享占用时拒绝写入。</summary>
    public static MappingOperationPlan Plan(
        MappingOperationIntent intent,
        MonitorConfiguration configuration,
        NetworkDriveQueryResult currentMapping)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(currentMapping);

        if (!Enum.IsDefined(intent))
        {
            throw new ArgumentOutOfRangeException(nameof(intent), "映射操作意图无效。");
        }

        if (!MonitorConfigurationValidator.Validate(configuration).IsValid)
        {
            return Blocked("mapping.blocked-invalid-configuration", null, null);
        }

        var driveLetter = configuration.DriveLetter.ToUpperInvariant();
        var sharePath = configuration.SharePath.Trim();

        return currentMapping.Status switch
        {
            NetworkDriveQueryStatus.NotMapped => PlanForMissingMapping(intent, driveLetter, sharePath),
            NetworkDriveQueryStatus.Mapped => PlanForExistingMapping(
                intent,
                driveLetter,
                sharePath,
                currentMapping.RemotePath),
            NetworkDriveQueryStatus.InvalidDrive => Blocked(
                "mapping.blocked-invalid-drive",
                driveLetter,
                sharePath),
            NetworkDriveQueryStatus.Error => Blocked(
                "mapping.blocked-query-error",
                driveLetter,
                sharePath),
            _ => Blocked("mapping.blocked-query-error", driveLetter, sharePath),
        };
    }

    private static MappingOperationPlan PlanForMissingMapping(
        MappingOperationIntent intent,
        string driveLetter,
        string sharePath)
    {
        return intent == MappingOperationIntent.EnsureMapped
            ? Ready(MappingOperation.Connect, "mapping.connect-ready", driveLetter, sharePath)
            : NoAction("mapping.already-absent", driveLetter, sharePath);
    }

    private static MappingOperationPlan PlanForExistingMapping(
        MappingOperationIntent intent,
        string driveLetter,
        string configuredSharePath,
        string? currentRemotePath)
    {
        if (!NetworkSharePathComparer.AreEquivalent(currentRemotePath, configuredSharePath))
        {
            return Blocked(
                "mapping.blocked-drive-conflict",
                driveLetter,
                configuredSharePath);
        }

        return intent == MappingOperationIntent.EnsureMapped
            ? NoAction("mapping.already-correct", driveLetter, configuredSharePath)
            : Ready(
                MappingOperation.Disconnect,
                "mapping.disconnect-ready",
                driveLetter,
                configuredSharePath);
    }

    private static MappingOperationPlan Ready(
        MappingOperation operation,
        string code,
        string driveLetter,
        string sharePath)
    {
        return new MappingOperationPlan(
            MappingPlanStatus.Ready,
            operation,
            code,
            driveLetter,
            sharePath);
    }

    private static MappingOperationPlan NoAction(
        string code,
        string driveLetter,
        string sharePath)
    {
        return new MappingOperationPlan(
            MappingPlanStatus.NoAction,
            MappingOperation.None,
            code,
            driveLetter,
            sharePath);
    }

    private static MappingOperationPlan Blocked(
        string code,
        string? driveLetter,
        string? sharePath)
    {
        return new MappingOperationPlan(
            MappingPlanStatus.Blocked,
            MappingOperation.None,
            code,
            driveLetter,
            sharePath);
    }
}
