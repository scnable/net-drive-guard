using System.Net.NetworkInformation;
using Microsoft.Win32;
using NetDriveGuard.Core.Scheduling;

namespace NetDriveGuard.App;

/// <summary>
/// 只订阅 Windows 系统变化并发布轻量信号，不在系统事件线程执行监控检查。
/// </summary>
internal sealed class WindowsSystemChangeMonitor : IDisposable
{
    private readonly Action<SystemChangeSignal> _publish;
    private bool _disposed;

    public WindowsSystemChangeMonitor(Action<SystemChangeSignal> publish)
    {
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
        NetworkChange.NetworkAddressChanged += HandleNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += HandleNetworkAvailabilityChanged;
        SystemEvents.PowerModeChanged += HandlePowerModeChanged;
    }

    private void HandleNetworkAddressChanged(object? sender, EventArgs eventArgs)
    {
        _publish(SystemChangeSignal.NetworkChanged);
    }

    private void HandleNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs eventArgs)
    {
        _publish(SystemChangeSignal.NetworkChanged);
    }

    private void HandlePowerModeChanged(object sender, PowerModeChangedEventArgs eventArgs)
    {
        if (eventArgs.Mode == PowerModes.Resume)
        {
            _publish(SystemChangeSignal.PowerResumed);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        NetworkChange.NetworkAddressChanged -= HandleNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= HandleNetworkAvailabilityChanged;
        SystemEvents.PowerModeChanged -= HandlePowerModeChanged;
    }
}
