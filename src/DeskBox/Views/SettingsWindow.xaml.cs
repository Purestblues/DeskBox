using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DeskBox.Services;

namespace DeskBox.Views;

public partial class SettingsWindow : Window
{
    private readonly BoxManager _manager;
    private bool _isInitializing = true;

    public SettingsWindow(BoxManager manager)
    {
        InitializeComponent();
        _manager = manager;
        LoadFromConfig();
        _isInitializing = false;
    }

    private void LoadFromConfig()
    {
        var cfg = _manager.Config;
        ChkAutoStart.IsChecked = cfg.General.AutoStart || DesktopHostService.IsAutoStartEnabled();
        ChkHwAccel.IsChecked = cfg.General.EnableHardwareAcceleration;
        ChkMinToTray.IsChecked = cfg.General.MinimizeToTrayOnClose;
        ChkHideTrayIcon.IsChecked = cfg.General.HideTrayIcon;

        foreach (ComboBoxItem item in ComboHotkey.Items)
        {
            if (string.Equals(item.Content?.ToString(), cfg.General.ToggleHotkey, StringComparison.OrdinalIgnoreCase))
            {
                ComboHotkey.SelectedItem = item;
                break;
            }
        }
        if (ComboHotkey.SelectedItem == null)
            ComboHotkey.SelectedIndex = 0;

        ChkDoubleClickHide.IsChecked = cfg.Tidy.DoubleClickBlankToHide;
        ChkFrostedGlass.IsChecked = cfg.Tidy.EnableFrostedGlass;
        ChkAutoExpand.IsChecked = cfg.Tidy.AutoExpandOnHover;
        ChkHideNativeIcons.IsChecked = cfg.Tidy.HideNativeDesktopIcons;

        RefreshTopologyAndStats();
    }

    public void RefreshTopologyAndStats()
    {
        var monitors = _manager.TopologyService.GetConnectedMonitors();
        TxtCurrentTopology.Text = MonitorTopologyService.BuildTopologyDescription(monitors);

        SnapshotsListBox.Items.Clear();
        foreach (var kv in _manager.Config.LayoutSnapshots.Values.OrderByDescending(s => s.UpdatedAt))
        {
            SnapshotsListBox.Items.Add($"[{kv.Placements.Count} 个格子] {kv.Description}  (更新: {kv.UpdatedAt.ToLocalTime():MM-dd HH:mm})");
        }

        using var proc = Process.GetCurrentProcess();
        var mb = proc.WorkingSet64 / (1024.0 * 1024.0);
        TxtMemoryUsage.Text = $"当前进程工作集内存：{mb:F1} MB（单进程运行，无后台推广服务）";
        TxtConfigPath.Text = $"配置目录：{_manager.ConfigService.ConfigDirectory}";
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;

        PanelGeneral.Visibility = NavGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelTidy.Visibility = NavTidy.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelMonitors.Visibility = NavMonitors.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelAbout.Visibility = NavAbout.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RefreshTopologyAndStats();
    }

    private void SettingCheckbox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
            return;

        var cfg = _manager.Config;
        cfg.General.AutoStart = ChkAutoStart.IsChecked == true;
        DesktopHostService.SetAutoStart(cfg.General.AutoStart);

        cfg.General.EnableHardwareAcceleration = ChkHwAccel.IsChecked == true;
        cfg.General.MinimizeToTrayOnClose = ChkMinToTray.IsChecked == true;

        var hideTray = ChkHideTrayIcon.IsChecked == true;
        if (cfg.General.HideTrayIcon != hideTray)
        {
            cfg.General.HideTrayIcon = hideTray;
            _manager.SetTrayIconHidden(hideTray);
        }

        cfg.Tidy.DoubleClickBlankToHide = ChkDoubleClickHide.IsChecked == true;
        cfg.Tidy.EnableFrostedGlass = ChkFrostedGlass.IsChecked == true;
        cfg.Tidy.AutoExpandOnHover = ChkAutoExpand.IsChecked == true;

        var hideIcons = ChkHideNativeIcons.IsChecked == true;
        _manager.SetHideNativeDesktopIconsSetting(hideIcons);

        _manager.ApplyAppearanceToAllBoxes();
        _manager.SaveConfigDebounced();
    }

    private void ComboHotkey_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing)
            return;

        if (ComboHotkey.SelectedItem is ComboBoxItem item)
        {
            _manager.Config.General.ToggleHotkey = item.Content?.ToString() ?? "Alt+Space";
            _manager.RegisterGlobalToggleHotkey();
            _manager.SaveConfigDebounced();
        }
    }

    private void BtnNewBox_Click(object sender, RoutedEventArgs e) => _manager.CreateNewBox();

    private void BtnUnorganizedBox_Click(object sender, RoutedEventArgs e) => _manager.CreateUnorganizedDesktopBox();

    private void BtnToggleCollapseAll_Click(object sender, RoutedEventArgs e) => _manager.ToggleCollapseAllBoxes();

    private void BtnRestoreToDesktop_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show(
            "确定要清空全部格子的分类索引并恢复原生桌面图标显示吗？\n放心：桌面原文件与文件夹位置不会做任何移动或删除。",
            "DeskBox",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _manager.RestoreAllBoxesToDesktop();
        }
    }

    private void BtnCustomizeAppearance_Click(object sender, RoutedEventArgs e) => _manager.ShowBoxAppearanceWindow();

    private void BtnSaveSnapshot_Click(object sender, RoutedEventArgs e)
    {
        _manager.RecordCurrentPlacementsAndSave(immediate: true);
        RefreshTopologyAndStats();
    }

    private void BtnReapplySnapshot_Click(object sender, RoutedEventArgs e)
    {
        _manager.ReapplyCurrentTopologyLayout();
        RefreshTopologyAndStats();
    }

    private void BtnTrimMemory_Click(object sender, RoutedEventArgs e)
    {
        _manager.TrimMemory();
        RefreshTopologyAndStats();
    }

    private void BtnOpenConfigFolder_Click(object sender, RoutedEventArgs e)
    {
        ShellService.OpenItem(_manager.ConfigService.ConfigDirectory);
    }
}
