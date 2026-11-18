using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using DeskBox.Interop;
using DeskBox.Models;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace DeskBox.Views;

public readonly record struct AlignmentGuideLine(
    int X1,
    int Y1,
    int X2,
    int Y2,
    string? Label = null);

/// <summary>
/// 拖动对齐或缩放等宽/等高时临时显示的鼠标穿透透明辅助线浮层。
/// </summary>
public partial class GuideOverlayWindow : Window
{
    private const int WS_EX_TRANSPARENT = 0x00000020;

    private static readonly Brush LineBrush = CreateFrozenBrush(Color.FromRgb(56, 210, 242));
    private static readonly Brush BadgeBgBrush = CreateFrozenBrush(Color.FromArgb(225, 22, 30, 42));
    private static readonly Brush BadgeBorderBrush = CreateFrozenBrush(Color.FromRgb(56, 210, 242));

    private static Brush CreateFrozenBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public GuideOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            exStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | WS_EX_TRANSPARENT;
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, exStyle);
        };
    }

    public void ShowGuides(MonitorDescriptor monitor, IReadOnlyList<AlignmentGuideLine> guides)
    {
        if (guides.Count == 0)
        {
            HideGuides();
            return;
        }

        if (!IsVisible)
            Show();

        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_TOPMOST,
            monitor.BoundsX,
            monitor.BoundsY,
            monitor.BoundsWidth,
            monitor.BoundsHeight,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

        GuideCanvas.Children.Clear();

        double scaleX = Math.Max(0.5, monitor.ScaleX);
        double scaleY = Math.Max(0.5, monitor.ScaleY);

        foreach (var g in guides)
        {
            double x1 = (g.X1 - monitor.BoundsX) / scaleX;
            double y1 = (g.Y1 - monitor.BoundsY) / scaleY;
            double x2 = (g.X2 - monitor.BoundsX) / scaleX;
            double y2 = (g.Y2 - monitor.BoundsY) / scaleY;

            var line = new Line
            {
                X1 = x1,
                Y1 = y1,
                X2 = x2,
                Y2 = y2,
                Stroke = LineBrush,
                StrokeThickness = 1.2,
                SnapsToDevicePixels = true
            };
            GuideCanvas.Children.Add(line);

            if (!string.IsNullOrEmpty(g.Label))
            {
                var badge = new Border
                {
                    Background = BadgeBgBrush,
                    BorderBrush = BadgeBorderBrush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(5, 1, 5, 1),
                    Child = new TextBlock
                    {
                        Text = g.Label,
                        Foreground = Brushes.White,
                        FontSize = 10.5
                    }
                };
                Canvas.SetLeft(badge, (x1 + x2) / 2 + 6);
                Canvas.SetTop(badge, (y1 + y2) / 2 - 10);
                GuideCanvas.Children.Add(badge);
            }
        }
    }

    public void HideGuides()
    {
        if (IsVisible)
        {
            GuideCanvas.Children.Clear();
            Hide();
        }
    }
}
