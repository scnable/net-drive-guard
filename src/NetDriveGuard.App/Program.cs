using NetDriveGuard.Windows;

namespace NetDriveGuard.App;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
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
        Application.Run(new TrayApplicationContext(demoMode));
        return 0;
    }
}
