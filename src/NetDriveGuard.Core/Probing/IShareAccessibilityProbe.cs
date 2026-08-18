namespace NetDriveGuard.Core.Probing;

public interface IShareAccessibilityProbe
{
    Task<ShareProbeResult> ProbeAsync(CancellationToken cancellationToken = default);
}
