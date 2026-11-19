using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskBox.Models;
using DeskBox.Services;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace DeskBox.Views;

/// <summary>
/// 轻量级格子外观个性化设置面板，遵循 Nord Frost UI 设计规范。
/// </summary>
public partial class BoxAppearanceWindow : Window
{
    private readonly BoxManager _manager;
    private DeskBoxWindow _targetWindow;
    private readonly List<(string Hex, Border Border)> _swatchBorders = new();
    private bool _isSyncingUi;

    public BoxAppearanceWindow(BoxManager manager, DeskBoxWindow targetWindow)
    {
        InitializeComponent();
        _manager = manager;
        _targetWindow = targetWindow;
        NordThemeService.ThemeChanged += OnThemeChanged;
        Closed += (_, _) => NordThemeService.ThemeChanged -= OnThemeChanged;
        BuildColorSwatches();
        BindTarget(_targetWindow);
    }

    private void OnThemeChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => RefreshSwatchBorders(_targetWindow.Config.ThemeColor));
            return;
        }
        RefreshSwatchBorders(_targetWindow.Config.ThemeColor);
    }

    public void BindTarget(DeskBoxWindow targetWindow)
    {
        _targetWindow = targetWindow;
        var cfg = _targetWindow.Config;
        Title = $"DeskBox - 格子外观设置 ({cfg.Title})";
        TxtWindowTitle.Text = "格子外观设置";
        TxtTargetBoxBadge.Text = cfg.Title;
        SyncControlsFromConfig(cfg);
    }

    private void BuildColorSwatches()
    {
        PresetColorsPanel.Children.Clear();
        _swatchBorders.Clear();

        foreach (var preset in BoxAppearancePresets.Colors)
        {
            var swatchColor = ParseHexOrDefault(preset.Hex);
            var border = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(swatchColor),
                BorderThickness = new Thickness(1)
            };
            border.SetResourceReference(Border.BorderBrushProperty, "NordBorderStrongBrush");

            var btn = new Button
            {
                Width = 28,
                Height = 28,
                Margin = new Thickness(0, 0, 4, 4),
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = $"{preset.Name} ({preset.Hex})",
                Content = border
            };
            AutomationProperties.SetName(btn, $"预设颜色 {preset.Name}");

            var capturedHex = preset.Hex;
            btn.Click += (_, _) =>
            {
                if (_isSyncingUi)
                    return;
                ApplyHexColor(capturedHex);
            };

            _swatchBorders.Add((preset.Hex, border));
            PresetColorsPanel.Children.Add(btn);
        }
    }

    private void SyncControlsFromConfig(BoxConfig cfg)
    {
        _isSyncingUi = true;
        try
        {
            var color = ParseHexOrDefault(cfg.ThemeColor);
            UpdateColorControls(color, cfg.ThemeColor);

            var opacityPct = Math.Clamp((int)Math.Round(cfg.Opacity * 100), 0, 95);
            SliderOpacity.Value = opacityPct;
            TxtOpacityVal.Text = $"{opacityPct}%";

            var frostedTag = cfg.UseFrostedGlass switch
            {
                true => "on",
                false => "off",
                _ => "global"
            };
            SelectComboByTag(ComboFrostedGlass, frostedTag);

            var corner = Math.Clamp((int)Math.Round(cfg.CornerRadius), 0, 16);
            SliderCornerRadius.Value = corner;
            TxtCornerVal.Text = $"{corner}px";

            SelectComboByTag(ComboBorderStyle, cfg.BorderStyle.ToString());
            SelectComboByTag(ComboIconSize, cfg.IconSizeTier.ToString());
            SelectComboByTag(ComboFontSize, cfg.FontSizeTier.ToString());
            ChkTextShadow.IsChecked = cfg.EnableTextShadow;

            SelectComboByTag(ComboTitleAlign, cfg.TitleAlignment.ToString());
            ChkHeaderBand.IsChecked = cfg.HeaderBandVisible;
            ChkAutoFadeButtons.IsChecked = cfg.AutoFadeHeaderButtons;
        }
        finally
        {
            _isSyncingUi = false;
        }
    }

    private void UpdateColorControls(Color color, string hexText)
    {
        CurrentColorPreview.Background = new SolidColorBrush(color);
        TxtCustomHex.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        SliderR.Value = color.R;
        SliderG.Value = color.G;
        SliderB.Value = color.B;
        TxtRVal.Text = color.R.ToString(CultureInfo.InvariantCulture);
        TxtGVal.Text = color.G.ToString(CultureInfo.InvariantCulture);
        TxtBVal.Text = color.B.ToString(CultureInfo.InvariantCulture);
        RefreshSwatchBorders(hexText);
    }

    private void RefreshSwatchBorders(string hexText)
    {
        foreach (var (hex, border) in _swatchBorders)
        {
            bool selected = string.Equals(hex, hexText, StringComparison.OrdinalIgnoreCase);
            border.SetResourceReference(Border.BorderBrushProperty, selected ? "NordAccentBrush" : "NordBorderStrongBrush");
            border.BorderThickness = new Thickness(selected ? 2 : 1);
        }
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

    private static Color ParseHexOrDefault(string? hex)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex))
                return (Color)ColorConverter.ConvertFromString(hex.Trim());
        }
        catch
        {
        }
        return Color.FromRgb(32, 34, 38);
    }

    private void ApplyHexColor(string rawHex)
    {
        var normalized = rawHex.Trim();
        if (!normalized.StartsWith('#'))
            normalized = "#" + normalized;

        try
        {
            var color = (Color)ColorConverter.ConvertFromString(normalized);
            var hex6 = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            _targetWindow.Config.ThemeColor = hex6;

            _isSyncingUi = true;
            try
            {
                UpdateColorControls(color, hex6);
            }
            finally
            {
                _isSyncingUi = false;
            }

            ApplyPreviewAndSave();
        }
        catch
        {
            TxtCustomHex.Text = _targetWindow.Config.ThemeColor;
        }
    }

    private void ApplyPreviewAndSave(bool rebuildItemTemplate = false)
    {
        _targetWindow.UpdateAppearance();
        if (rebuildItemTemplate)
            _targetWindow.UpdateViewModeVisual();
        _manager.SaveConfigDebounced();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void BtnApplyHex_Click(object sender, RoutedEventArgs e) => ApplyHexColor(TxtCustomHex.Text);

    private void TxtCustomHex_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            ApplyHexColor(TxtCustomHex.Text);
        }
    }

    private void RgbSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isSyncingUi || !IsLoaded)
            return;

        byte r = (byte)Math.Clamp((int)Math.Round(SliderR.Value), 0, 255);
        byte g = (byte)Math.Clamp((int)Math.Round(SliderG.Value), 0, 255);
        byte b = (byte)Math.Clamp((int)Math.Round(SliderB.Value), 0, 255);
        var color = Color.FromRgb(r, g, b);
        var hex6 = $"#{r:X2}{g:X2}{b:X2}";
        _targetWindow.Config.ThemeColor = hex6;

        _isSyncingUi = true;
        try
        {
            UpdateColorControls(color, hex6);
        }
        finally
        {
            _isSyncingUi = false;
        }

        ApplyPreviewAndSave();
    }

    private void SliderOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isSyncingUi || !IsLoaded)
            return;

        int pct = Math.Clamp((int)Math.Round(SliderOpacity.Value), 0, 95);
        TxtOpacityVal.Text = $"{pct}%";
        _targetWindow.Config.Opacity = pct / 100.0;
        ApplyPreviewAndSave();
    }

    private void BtnQuickOpacity_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && int.TryParse(btn.Tag?.ToString(), out int pct))
        {
            SliderOpacity.Value = pct;
        }
    }

    private void ComboFrostedGlass_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingUi || !IsLoaded)
            return;

        if (ComboFrostedGlass.SelectedItem is ComboBoxItem item)
        {
            _targetWindow.Config.UseFrostedGlass = item.Tag?.ToString() switch
            {
                "on" => true,
                "off" => false,
                _ => null
            };
            ApplyPreviewAndSave();
        }
    }

    private void SliderCornerRadius_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isSyncingUi || !IsLoaded)
            return;

        int corner = Math.Clamp((int)Math.Round(SliderCornerRadius.Value), 0, 16);
        TxtCornerVal.Text = $"{corner}px";
        _targetWindow.Config.CornerRadius = corner;
        ApplyPreviewAndSave();
    }

    private void BtnQuickCorner_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && int.TryParse(btn.Tag?.ToString(), out int corner))
        {
            SliderCornerRadius.Value = corner;
        }
    }

    private void ComboBorderStyle_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingUi || !IsLoaded)
            return;

        if (ComboBorderStyle.SelectedItem is ComboBoxItem item &&
            Enum.TryParse<BoxBorderStyle>(item.Tag?.ToString(), out var style))
        {
            _targetWindow.Config.BorderStyle = style;
            ApplyPreviewAndSave();
        }
    }

    private void ComboIconSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingUi || !IsLoaded)
            return;

        if (ComboIconSize.SelectedItem is ComboBoxItem item &&
            Enum.TryParse<BoxSizeTier>(item.Tag?.ToString(), out var tier))
        {
            _targetWindow.Config.IconSizeTier = tier;
            ApplyPreviewAndSave(rebuildItemTemplate: true);
        }
    }

    private void ComboFontSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingUi || !IsLoaded)
            return;

        if (ComboFontSize.SelectedItem is ComboBoxItem item &&
            Enum.TryParse<BoxSizeTier>(item.Tag?.ToString(), out var tier))
        {
            _targetWindow.Config.FontSizeTier = tier;
            ApplyPreviewAndSave(rebuildItemTemplate: true);
        }
    }

    private void ComboTitleAlign_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingUi || !IsLoaded)
            return;

        if (ComboTitleAlign.SelectedItem is ComboBoxItem item &&
            Enum.TryParse<BoxTitleAlignment>(item.Tag?.ToString(), out var align))
        {
            _targetWindow.Config.TitleAlignment = align;
            ApplyPreviewAndSave();
        }
    }

    private void OptionCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isSyncingUi || !IsLoaded)
            return;

        _targetWindow.Config.EnableTextShadow = ChkTextShadow.IsChecked == true;
        _targetWindow.Config.HeaderBandVisible = ChkHeaderBand.IsChecked == true;
        _targetWindow.Config.AutoFadeHeaderButtons = ChkAutoFadeButtons.IsChecked == true;
        ApplyPreviewAndSave();
    }

    private void BtnResetDefault_Click(object sender, RoutedEventArgs e)
    {
        _targetWindow.Config.ResetAppearanceToBuiltInDefaults();
        SyncControlsFromConfig(_targetWindow.Config);
        ApplyPreviewAndSave(rebuildItemTemplate: true);
    }

    private void BtnApplyToAll_Click(object sender, RoutedEventArgs e)
    {
        _manager.ApplyBoxAppearanceToAllAndDefault(_targetWindow.Config);
    }
}
