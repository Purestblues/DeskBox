using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace DeskBox.Views;

/// <summary>
/// 轻量 Nord Frost 重命名对话框（供格子与桌面画布 F2 重命名共用）。
/// </summary>
public static class RenamePromptDialog
{
    public static string? Show(string currentPath, Window? owner = null)
    {
        var fileName = Path.GetFileName(currentPath.TrimEnd('\\', '/'));
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        bool isDir = Directory.Exists(currentPath);
        var ext = isDir ? string.Empty : Path.GetExtension(fileName);
        var displayEditable = (!isDir && (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || ext.Equals(".url", StringComparison.OrdinalIgnoreCase)))
            ? Path.GetFileNameWithoutExtension(fileName)
            : fileName;

        string? result = null;

        var dialog = new Window
        {
            Title = "重命名",
            Width = 360,
            Height = 148,
            WindowStyle = WindowStyle.ToolWindow,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false,
            Topmost = true,
            Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x34, 0x40)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xEC, 0xEF, 0xF4))
        };

        var root = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };
        root.Children.Add(new TextBlock
        {
            Text = "输入新名称：",
            FontSize = 12.5,
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = new SolidColorBrush(Color.FromRgb(0xD8, 0xDE, 0xE9))
        });

        var textBox = new TextBox
        {
            Text = displayEditable,
            FontSize = 13,
            Padding = new Thickness(8, 5, 8, 5),
            Background = new SolidColorBrush(Color.FromRgb(0x3B, 0x42, 0x52)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xEC, 0xEF, 0xF4)),
            CaretBrush = new SolidColorBrush(Color.FromRgb(0x88, 0xC0, 0xD0)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x5E, 0x81, 0xAC)),
            BorderThickness = new Thickness(1)
        };
        root.Children.Add(textBox);

        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };

        var cancelBtn = new Button
        {
            Content = "取消",
            Width = 72,
            Height = 28,
            Margin = new Thickness(0, 0, 8, 0),
            IsCancel = true
        };
        var okBtn = new Button
        {
            Content = "确定",
            Width = 72,
            Height = 28,
            IsDefault = true
        };

        okBtn.Click += (_, _) =>
        {
            result = textBox.Text.Trim();
            dialog.DialogResult = true;
            dialog.Close();
        };
        cancelBtn.Click += (_, _) => dialog.Close();

        textBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                result = textBox.Text.Trim();
                dialog.DialogResult = true;
                dialog.Close();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                dialog.Close();
                e.Handled = true;
            }
        };

        btnPanel.Children.Add(cancelBtn);
        btnPanel.Children.Add(okBtn);
        root.Children.Add(btnPanel);
        dialog.Content = root;

        dialog.Loaded += (_, _) =>
        {
            textBox.Focus();
            int stemLen = (!isDir && !string.IsNullOrEmpty(ext) && displayEditable.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                ? displayEditable.Length - ext.Length
                : displayEditable.Length;
            textBox.Select(0, Math.Max(0, stemLen));
        };

        dialog.ShowDialog();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }
}
