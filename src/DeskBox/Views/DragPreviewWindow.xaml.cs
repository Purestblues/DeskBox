using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using DeskBox.Interop;

namespace DeskBox.Views;

/// <summary>
/// 拖拽格子内项目时跟随鼠标移动的半透明图标预览浮窗（鼠标完全穿透）。
/// </summary>
public partial class DragPreviewWindow : Window
{
    private const int WS_EX_TRANSPARENT = 0x00000020;

    public DragPreviewWindow(ImageSource? icon, string title, int totalCount)
    {
        InitializeComponent();
        PreviewIcon.Source = icon;
        PreviewTitle.Text = title;
        if (totalCount > 1)
        {
            CountBadge.Visibility = Visibility.Visible;
            CountText.Text = totalCount.ToString();
        }

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            exStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | WS_EX_TRANSPARENT;
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, exStyle);
        };
    }

    public void UpdatePositionNearCursor()
    {
        NativeMethods.GetCursorPos(out var pt);
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_TOPMOST,
            pt.X + 16,
            pt.Y + 16,
            0,
            0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }
}
