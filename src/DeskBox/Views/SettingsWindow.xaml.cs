using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Views;

public partial class SettingsWindow : Window
{
    private sealed record SnapshotListItem(string BoxCountLabel, string Description, string UpdatedLabel)
    {
        public override string ToString() => $"{BoxCountLabel} {Description} ({UpdatedLabel})";
    }

    private readonly BoxManager _manager;
    private bool _isInitializing = true;

    public SettingsWindow(BoxManager manager)
    {
        InitializeComponent();
        _manager = manager;
        NordThemeService.ThemeChanged += OnThemeChanged;
        Closed += (_, _) => NordThemeService.ThemeChanged -= OnThemeChanged;
        LoadFromConfig();
        _isInitializing = false;
    }

    private void OnThemeChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdateQuickThemeChip);
            return;
        }
        UpdateQuickThemeChip();
    }

    private void LoadFromConfig()
    {
        var cfg = _manager.Config;
        ChkAutoStart.IsChecked = cfg.General.AutoStart || DesktopHostService.IsAutoStartEnabled();
        ChkHwAccel.IsChecked = cfg.General.EnableHardwareAcceleration;
        ChkMinToTray.IsChecked = cfg.General.MinimizeToTrayOnClose;
        ChkHideTrayIcon.IsChecked = cfg.General.HideTrayIcon;
        ChkReduceMotion.IsChecked = cfg.General.ReduceMotion;

        SelectComboByTag(ComboUiTheme, cfg.General.UiTheme.ToString());
        UpdateQuickThemeChip();

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

    private void UpdateQuickThemeChip()
    {
        var mode = _manager.Config.General.UiTheme;
        TxtQuickThemeLabel.Text = mode switch
        {
            UiThemeMode.Light => "Snow Storm 浅色",
            UiThemeMode.Dark => "Polar Night 深色",
            _ => NordThemeService.IsDarkActive ? "跟随系统 · 深色" : "跟随系统 · 浅色"
        };

        ThemeChipIcon.Data = Geometry.Parse(NordThemeService.IsDarkActive
            ? "M 7,1.5 A 5.5,5.5 0 1 0 12.5,7 A 4.2,4.2 0 0 1 7,1.5 Z"
            : "M 7,4 A 3,3 0 1 1 6.99,4 Z M 7,1 V 2.3 M 7,11.7 V 13 M 1,7 H 2.3 M 11.7,7 H 13 M 2.8,2.8 L 3.7,3.7 M 10.3,10.3 L 11.2,11.2 M 11.2,2.8 L 10.3,3.7 M 3.7,10.3 L 2.8,11.2");
    }

    public void RefreshTopologyAndStats()
    {
        var monitors = _manager.TopologyService.GetConnectedMonitors();
        TxtCurrentTopology.Text = MonitorTopologyService.BuildTopologyDescription(monitors);

        SnapshotsListBox.Items.Clear();
        foreach (var kv in _manager.Config.LayoutSnapshots.Values.OrderByDescending(s => s.UpdatedAt))
        {
            SnapshotsListBox.Items.Add(new SnapshotListItem(
                $"{kv.Placements.Count} 个格子",
                kv.Description,
                kv.UpdatedAt.ToLocalTime().ToString("MM-dd HH:mm")));
        }
        TxtNoSnapshots.Visibility = SnapshotsListBox.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        using var proc = Process.GetCurrentProcess();
        var mb = proc.WorkingSet64 / (1024.0 * 1024.0);
        TxtMemoryUsage.Text = $"{mb:F1} MB";
        TxtSidebarMemory.Text = $"内存 {mb:F1} MB";
        TxtBoxStats.Text = $"{_manager.Config.Boxes.Count} 个格子";
        TxtSnapshotStats.Text = $"已记忆 {_manager.Config.LayoutSnapshots.Count} 组多屏拓扑快照";
        TxtConfigPath.Text = _manager.ConfigService.ConfigDirectory;
    }

    private static void SelectComboByTag(System.Windows.Controls.ComboBox combo, string tag)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        if (combo.Items.Count > 0)
            combo.SelectedIndex = 0;
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

    private void BtnQuickTheme_Click(object sender, RoutedEventArgs e)
    {
        var next = _manager.Config.General.UiTheme switch
        {
            UiThemeMode.System => UiThemeMode.Light,
            UiThemeMode.Light => UiThemeMode.Dark,
            _ => UiThemeMode.System
        };
        SelectComboByTag(ComboUiTheme, next.ToString());
    }

    private void ComboUiTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || !IsLoaded)
            return;

        if (ComboUiTheme.SelectedItem is ComboBoxItem item &&
            Enum.TryParse<UiThemeMode>(item.Tag?.ToString(), out var mode))
        {
            _manager.Config.General.UiTheme = mode;
            NordThemeService.ApplyTheme(mode);
            UpdateQuickThemeChip();
            _manager.SaveConfigDebounced();
        }
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;

        PanelGeneral.Visibility = NavGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelTidy.Visibility = NavTidy.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelMonitors.Visibility = NavMonitors.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelAbout.Visibility = NavAbout.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        if (NavGeneral.IsChecked == true)
        {
            TxtPageTitle.Text = "基础设置";
            TxtPageSubtitle.Text = "管理界面主题、系统启动项、托盘常驻与全局快捷键";
        }
        else if (NavTidy.IsChecked == true)
        {
            TxtPageTitle.Text = "桌面整理";
            TxtPageSubtitle.Text = "配置桌面格子交互行为、毛玻璃渲染与快捷操作";
        }
        else if (NavMonitors.IsChecked == true)
        {
            TxtPageTitle.Text = "多屏布局";
            TxtPageSubtitle.Text = "查看显示器拓扑并管理热插拔布局快照";
        }
        else if (NavAbout.IsChecked == true)
        {
            TxtPageTitle.Text = "关于与性能";
            TxtPageSubtitle.Text = "监控单进程内存占用、本地配置目录与缓存整理";
        }

        MainScrollViewer.ScrollToTop();
        PlayPageTransition();
        RefreshTopologyAndStats();
    }

    private void PlayPageTransition()
    {
        if (_manager.Config.General.ReduceMotion)
        {
            ContentSlideTransform.Y = 0;
            ContentAnimatedHost.Opacity = 1;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(200);

        var opacityAnim = new DoubleAnimation(0.35, 1.0, duration) { EasingFunction = ease };
        var slideAnim = new DoubleAnimation(8.0, 0.0, duration) { EasingFunction = ease };

        ContentAnimatedHost.BeginAnimation(OpacityProperty, opacityAnim);
        ContentSlideTransform.BeginAnimation(TranslateTransform.YProperty, slideAnim);
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
        cfg.General.ReduceMotion = ChkReduceMotion.IsChecked == true;

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
        if (NordConfirmDialog.Confirm(
            this,
            "清空格子并恢复桌面",
            "确定要清空全部格子的分类索引并恢复原生桌面图标显示吗？\n放心：桌面原文件与文件夹位置不会做任何移动或删除。",
            confirmText: "清空并恢复",
            cancelText: "保留当前整理",
            isDanger: true))
        {
            _manager.RestoreAllBoxesToDesktop();
            RefreshTopologyAndStats();
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
