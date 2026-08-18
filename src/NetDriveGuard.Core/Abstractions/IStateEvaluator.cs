namespace NetDriveGuard.Core.Abstractions;

/// <summary>
/// 根据观察结果和上一状态计算新的业务决策。
/// </summary>
public interface IStateEvaluator<in TObservation, in TState, out TDecision>
{
    TDecision Evaluate(TObservation observation, TState previousState);
}
