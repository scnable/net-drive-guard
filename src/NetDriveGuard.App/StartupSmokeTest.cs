using NetDriveGuard.Core.Configuration;
using NetDriveGuard.Core.Events;
using NetDriveGuard.Core.Monitoring;
using NetDriveGuard.Core.Scheduling;
using NetDriveGuard.Core.Localization;

namespace NetDriveGuard.App;

internal static class StartupSmokeTest
{
    public static int Run()
    {
        try
        {
            var configuration = new MonitorConfiguration("Z:", @"\\server\share", Language: AppText.Language);
            var json = MonitorConfigurationJson.Serialize(configuration);
            var load = MonitorConfigurationJson.Deserialize(json);
            if (!load.IsLoaded)
            {
                return 2;
            }

            using var statusWindow = new Form1();
            using var settingsWindow = new SettingsForm(configuration);
            var systemChangeSignals = new SystemChangeSignalQueue();
            using var systemChangeMonitor = new WindowsSystemChangeMonitor(systemChangeSignals.Publish);
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
            if (!CheckMonitoringSettings())
            {
                return 5;
            }
            if (AppText.Language == "en")
            {
                // 三个设置页均已创建；检查静态界面没有漏翻译，历史错误文字允许保留中文。
                var controls = Descendants(settingsWindow).Concat(Descendants(statusWindow));
                foreach (var control in controls)
                {
                    if (control is TextBox || control.Text.Contains("烟雾测试错误展示。", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (System.Text.RegularExpressions.Regex.IsMatch(control.Text, @"[\p{IsCJKUnifiedIdeographs}]"))
                    {
                        return 3;
                    }
                }

                var list = Descendants(statusWindow).OfType<ListView>().Single();
                if (!list.Items.Cast<ListViewItem>().Any(item => item.SubItems[3].Text == "烟雾测试错误展示。"))
                {
                    return 4;
                }
            }
            return 0;
        }
        catch
        {
            return 1;
        }
    }

    /// <summary>只操作未显示的设置窗口，不保存文件、不修改映射或启动任务。</summary>
    private static bool CheckMonitoringSettings()
    {
        foreach (var initialProfile in Enum.GetValues<MonitoringProfile>())
        {
            var configuration = new MonitorConfiguration("Z:", @"\\server\share",
                initialProfile, TimeSpan.FromSeconds(17), OfflineFailureThreshold: 4,
                TcpTimeout: TimeSpan.FromMilliseconds(1200));
            using var form = new SettingsForm(configuration);
            _ = form.Handle;
            var controls = Descendants(form).ToArray();
            var tabs = controls.OfType<TabControl>().Single();
            var profile = controls.OfType<ComboBox>().Single(control => control.DataSource is MonitoringProfile[]);
            var numbers = controls.OfType<NumericUpDown>().ToArray();
            var interval = numbers.Single(control => control.Maximum == 1800);
            var threshold = numbers.Single(control => control.Maximum == 10);
            var timeout = numbers.Single(control => control.Maximum == 10000);
            var expectedInterval = (decimal)MonitoringIntervals.ForProfile(initialProfile, TimeSpan.FromSeconds(17)).TcpProbe.TotalSeconds;
            var editable = initialProfile == MonitoringProfile.Custom;
            if (tabs.TabPages.Count != 3 || numbers.Any(number => number.Enabled != editable)
                || interval.Value != expectedInterval || threshold.Value != 4 || timeout.Value != 1200
                || !Equals(profile.SelectedItem, initialProfile)
                || interval.Parent != profile.Parent || threshold.Parent != profile.Parent || timeout.Parent != profile.Parent)
            {
                Console.Error.WriteLine($"Settings load check failed: {initialProfile}; actual {profile.SelectedItem}, {interval.Value}, {threshold.Value}, {timeout.Value}.");
                return false;
            }

            foreach (var preset in new[] { MonitoringProfile.Default, MonitoringProfile.Fast, MonitoringProfile.EnergySaving })
            {
                profile.SelectedItem = MonitoringProfile.Custom;
                profile.SelectedItem = preset;
                var presetInterval = (decimal)MonitoringIntervals.ForProfile(preset).TcpProbe.TotalSeconds;
                if (interval.Value != presetInterval || threshold.Value != 3 || timeout.Value != 800
                    || !Equals(profile.SelectedItem, preset) || numbers.Any(number => number.Enabled))
                {
                    Console.Error.WriteLine($"Settings preset check failed: {preset}; actual {profile.SelectedItem}, {interval.Value}, {threshold.Value}, {timeout.Value}.");
                    return false;
                }

                profile.SelectedItem = MonitoringProfile.Custom;
                if (interval.Value != presetInterval || threshold.Value != 3 || timeout.Value != 800
                    || numbers.Any(number => !number.Enabled))
                {
                    return false;
                }

                foreach (var number in numbers)
                {
                    var previousValue = number.Value;
                    number.Value += number.Increment;
                    if (!Equals(profile.SelectedItem, MonitoringProfile.Custom)
                        || number.Value != previousValue + number.Increment)
                    {
                        Console.Error.WriteLine("Settings Custom edit failed.");
                        return false;
                    }
                }

                profile.SelectedItem = preset;
                if (interval.Value != presetInterval || threshold.Value != 3 || timeout.Value != 800
                    || numbers.Any(number => number.Enabled))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }
}
