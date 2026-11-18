using System.Windows;
using DeskBox.Setup.Services;

namespace DeskBox.Setup;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        bool uninstallMode = e.Args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase));
        bool quietMode = e.Args.Any(a => a.Equals("--quiet", StringComparison.OrdinalIgnoreCase));

        if (uninstallMode && quietMode)
        {
            var dir = InstallerEngine.GetRegisteredInstallLocation()
                ?? AppContext.BaseDirectory.TrimEnd('\\');
            InstallerEngine.UninstallAsync(dir, removeUserConfig: false, new Progress<(int, string)>())
                .GetAwaiter()
                .GetResult();
            Shutdown();
            return;
        }

        var win = new MainWindow(uninstallMode);
        MainWindow = win;
        win.Show();
    }
}
