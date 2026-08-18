using NetDriveGuard.Core.Configuration;
using NetDriveGuard.Core.Events;
using NetDriveGuard.Core.Monitoring;

namespace NetDriveGuard.App;

internal static class StartupSmokeTest
{
    public static int Run()
    {
        try
        {
            var configuration = new MonitorConfiguration("Z:", @"\\server\share");
            var json = MonitorConfigurationJson.Serialize(configuration);
            var load = MonitorConfigurationJson.Deserialize(json);
            if (!load.IsLoaded)
            {
                return 2;
            }

            using var statusWindow = new Form1();
            using var settingsWindow = new SettingsForm(configuration);
            statusWindow.UpdateRecentEvents(
            [
                new MonitorEvent(
                    DateTimeOffset.UtcNow,
                    MonitorState.Healthy,
                    MonitorEventSeverity.Information,
                    "smoke.healthy",
                    "烟雾测试状态正常。"),
                new MonitorEvent(
                    DateTimeOffset.UtcNow,
                    MonitorState.InternalError,
                    MonitorEventSeverity.Error,
                    "smoke.error",
                    "烟雾测试错误展示。"),
            ]);
            _ = statusWindow.Handle;
            _ = settingsWindow.Handle;
            return 0;
        }
        catch
        {
            return 1;
        }
    }
}
