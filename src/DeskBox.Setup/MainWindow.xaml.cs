using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using DeskBox.Setup.Services;
using Microsoft.Win32;

namespace DeskBox.Setup;

public partial class MainWindow : Window
{
    private readonly bool _uninstallMode;
    private readonly string _uninstallDir;
    private bool _finished;
    private bool _busy;

    public MainWindow(bool uninstallMode)
    {
        InitializeComponent();
        _uninstallMode = uninstallMode;
        _uninstallDir = InstallerEngine.GetRegisteredInstallLocation()
            ?? AppContext.BaseDirectory.TrimEnd('\\');

        if (_uninstallMode)
        {
            Title = "卸载 DeskBox";
            WindowHeaderTitle.Text = "DeskBox 卸载向导";
            InstallPanel.Visibility = Visibility.Collapsed;
            UninstallPanel.Visibility = Visibility.Visible;
            TxtUninstallDir.Text = _uninstallDir;
            BtnPrimary.Content = "确认卸载";
        }
        else
        {
            TxtInstallDir.Text = InstallerEngine.GetRegisteredInstallLocation()
                ?? InstallerEngine.DefaultInstallDirectory;
            if (!InstallerEngine.HasEmbeddedPayload())
            {
                TxtStatus.Text = "提示：开发预览模式（请运行 build-installer.ps1 生成完整安装包）";
            }
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy)
            Close();
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "选择 DeskBox 安装文件夹",
            InitialDirectory = Directory.Exists(TxtInstallDir.Text) ? TxtInstallDir.Text : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };
        if (dlg.ShowDialog() == true)
        {
            var chosen = dlg.FolderName;
            if (!chosen.EndsWith("DeskBox", StringComparison.OrdinalIgnoreCase))
                chosen = Path.Combine(chosen, "DeskBox");
            TxtInstallDir.Text = chosen;
        }
    }

    private async void BtnPrimary_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        if (_finished)
        {
            Close();
            return;
        }

        _busy = true;
        BtnPrimary.IsEnabled = false;
        CloseButton.IsEnabled = false;

        var progress = new Progress<(int percent, string message)>(report =>
        {
            Progress.Value = report.percent;
            TxtStatus.Text = report.message;
        });

        try
        {
            if (_uninstallMode)
            {
                await InstallerEngine.UninstallAsync(_uninstallDir, ChkRemoveConfig.IsChecked == true, progress);
                _finished = true;
                BtnPrimary.Content = "完成";
            }
            else
            {
                var dir = TxtInstallDir.Text.Trim();
                if (string.IsNullOrWhiteSpace(dir))
                {
                    MessageBox.Show("请选择有效的安装目录。", "DeskBox 安装向导", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var options = new InstallOptions(
                    InstallDirectory: dir,
                    CreateDesktopShortcut: ChkDesktopShortcut.IsChecked == true,
                    CreateStartMenuShortcut: ChkStartMenuShortcut.IsChecked == true,
                    AutoStart: ChkAutoStart.IsChecked == true,
                    LaunchAfterInstall: ChkLaunchAfter.IsChecked == true);

                await InstallerEngine.InstallAsync(options, progress);
                _finished = true;
                BtnPrimary.Content = "完成";

                if (options.LaunchAfterInstall)
                {
                    var exe = Path.Combine(dir, "DeskBox.exe");
                    if (File.Exists(exe))
                    {
                        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = dir });
                    }
                    Close();
                }
            }
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "操作失败";
            MessageBox.Show($"操作过程中发生错误：\n{ex.Message}", "DeskBox", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
            BtnPrimary.IsEnabled = true;
            CloseButton.IsEnabled = true;
        }
    }
}
