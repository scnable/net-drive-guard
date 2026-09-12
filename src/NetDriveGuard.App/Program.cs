using NetDriveGuard.Core.Localization;
using NetDriveGuard.Windows;

namespace NetDriveGuard.App;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // 仅用于界面预览和自检，不读取或改写用户的语言配置。
        if (args.Contains("--preview-ui") || args.Contains("--smoke-test"))
        {
            AppText.Language = args.Contains("--english") ? "en" : "zh-CN";
        }
        // 布局检查入口：使用空配置，不启动监控，也不保存任何设置。
        if (args.Contains("--preview-ui", StringComparer.OrdinalIgnoreCase))
        {
            using var settings = new SettingsForm(null);
            settings.ShowDialog();
            using var status = new Form1(isDemoMode: true);
            status.AllowExit();
            status.ShowDialog();
            return 0;
        }

        if (args.Any(
                argument => string.Equals(
                    argument,
                    "--smoke-test",
                    StringComparison.OrdinalIgnoreCase)))
        {
            return StartupSmokeTest.Run();
        }

        using var instanceGuard = new NamedMutexSingleInstanceGuard("NetDriveGuard.App");
        if (!instanceGuard.IsPrimaryInstance)
        {
            return 0;
        }

        var demoMode = args.Any(
            argument => string.Equals(argument, "--demo", StringComparison.OrdinalIgnoreCase));
        try
        {
            // 在创建任何窗口前读取语言；配置仍由原有加载流程校验和应用。
            using (var data = new ApplicationDataStore(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetDriveGuard")))
            {
                var configuration = data.LoadAsync().GetAwaiter().GetResult();
                AppText.Language = configuration.Configuration?.Language ?? "zh-CN";
            }
            Application.Run(new TrayApplicationContext(demoMode));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or System.Text.Json.JsonException)
        {
            MessageBox.Show(
                AppText.Get("无法读取程序数据，请检查保存目录及默认目录中的 storage-path.json 路径记录。为避免使用错误配置，程序已停止启动。"),
                "NetDriveGuard", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        return 0;
    }
}
