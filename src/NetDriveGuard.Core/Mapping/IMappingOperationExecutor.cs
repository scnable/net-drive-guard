namespace NetDriveGuard.Core.Mapping;

public interface IMappingOperationExecutor
{
    MappingExecutionResult Execute(
        MappingOperationPlan plan,
        Security.NetworkCredentialSecret? credential = null);
}
