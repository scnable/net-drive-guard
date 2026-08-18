using NetDriveGuard.Core.Abstractions;
using NetDriveGuard.Core.Monitoring;

namespace NetDriveGuard.Core.Runtime;

public sealed record MonitorRunResult(
    MonitorRunStatus Status,
    MonitorDecision? Decision = null,
    RemediationResult? Remediation = null);
