namespace NetDriveGuard.Core.Abstractions;

/// <summary>
/// 执行由状态决策产生的精确修复动作。
/// </summary>
public interface IRemediation<in TAction>
{
    Task<RemediationResult> ExecuteAsync(TAction action, CancellationToken cancellationToken);
}

public sealed record RemediationResult(bool Success, string Code)
{
    public static RemediationResult Succeeded(string code = "remediation.succeeded") => new(true, code);

    public static RemediationResult Failed(string code) => new(false, code);
}
