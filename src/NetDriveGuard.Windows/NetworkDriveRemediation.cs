using NetDriveGuard.Core.Abstractions;
using NetDriveGuard.Core.Configuration;
using NetDriveGuard.Core.Mapping;
using NetDriveGuard.Core.Monitoring;
using NetDriveGuard.Core.Security;

namespace NetDriveGuard.Windows;

/// <summary>
/// 每次修复前重新查询当前映射，并只执行核心规划器批准的操作。
/// </summary>
public sealed class NetworkDriveRemediation : IRemediation<MonitorAction>
{
    private readonly MonitorConfiguration _configuration;
    private readonly INetworkDriveQuery _query;
    private readonly IMappingOperationExecutor _executor;
    private readonly ICredentialStore _credentialStore;

    public NetworkDriveRemediation(MonitorConfiguration configuration)
        : this(
            configuration,
            new WNetNetworkDriveQuery(),
            new WNetMappingOperationExecutor(),
            new WindowsCredentialStore())
    {
    }

    public NetworkDriveRemediation(
        MonitorConfiguration configuration,
        INetworkDriveQuery query,
        IMappingOperationExecutor executor,
        ICredentialStore credentialStore)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!MonitorConfigurationValidator.Validate(configuration).IsValid)
        {
            throw new ArgumentException("映射修复需要有效配置。", nameof(configuration));
        }

        _configuration = configuration;
        _query = query ?? throw new ArgumentNullException(nameof(query));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
    }

    public Task<RemediationResult> ExecuteAsync(
        MonitorAction action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var intent = action switch
        {
            MonitorAction.RepairMapping when _configuration.RepairMissingMapping =>
                MappingOperationIntent.EnsureMapped,
            MonitorAction.RemoveMapping when _configuration.RemoveMappingWhenOffline =>
                MappingOperationIntent.EnsureUnmapped,
            MonitorAction.RepairMapping or MonitorAction.RemoveMapping =>
                (MappingOperationIntent?)null,
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "未知修复动作。"),
        };

        if (intent is null)
        {
            return Task.FromResult(
                RemediationResult.Failed("remediation.disabled-by-configuration"));
        }

        var currentMapping = _query.Query(_configuration.DriveLetter);
        var plan = MappingOperationPlanner.Plan(intent.Value, _configuration, currentMapping);
        if (plan.Status == MappingPlanStatus.NoAction)
        {
            return Task.FromResult(RemediationResult.Succeeded(plan.Code));
        }

        if (!plan.CanExecute)
        {
            return Task.FromResult(RemediationResult.Failed(plan.Code));
        }

        if (plan.Operation != MappingOperation.Connect)
        {
            return Task.FromResult(ToRemediationResult(_executor.Execute(plan)));
        }

        using var credentialResult = _credentialStore.Read(
            CredentialTargetName.ForMapping(
                _configuration.DriveLetter,
                _configuration.SharePath));
        if (credentialResult.Status == CredentialReadStatus.Error)
        {
            return Task.FromResult(RemediationResult.Failed(credentialResult.Code));
        }

        return Task.FromResult(
            ToRemediationResult(_executor.Execute(plan, credentialResult.Credential)));
    }

    private static RemediationResult ToRemediationResult(MappingExecutionResult result)
    {
        return result.Success
            ? RemediationResult.Succeeded(result.Code)
            : RemediationResult.Failed(result.Code);
    }
}
