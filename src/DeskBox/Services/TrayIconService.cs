using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using DeskBox.Interop;
using Application = System.Windows.Application;

namespace DeskBox.Services;

/// <summary>
/// 纯 Win32 Shell_NotifyIconW 托盘图标实现，彻底摆脱 System.Windows.Forms 依赖，
/// 并复用深色现代 WPF ContextMenu。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const int WM_TRAYICON = 0x8001;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;

    private readonly HwndSource _hwndSource;
    private readonly IntPtr _hIcon;
    private readonly string _tooltip;
    private readonly ContextMenu _menu;
    private readonly Action _onLeftClick;
    private readonly Action _onDoubleClick;
    private bool _added;

    public TrayIconService(
        string tooltip,
        ContextMenu menu,
        Action onLeftClick,
        Action onDoubleClick,
        bool initiallyVisible = true)
    {
        _tooltip = tooltip;
        _menu = menu;
        _onLeftClick = onLeftClick;
        _onDoubleClick = onDoubleClick;

        var parameters = new HwndSourceParameters("DeskBoxTrayHost")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0
        };
        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(WndProc);

        _hIcon = LoadHighDpiIconHandle();
        if (initiallyVisible)
        {
            AddTrayIcon();
        }
    }

    public void SetVisible(bool visible)
    {
        if (visible && !_added)
        {
            AddTrayIcon();
        }
        else if (!visible && _added)
        {
            RemoveTrayIcon();
        }
    }

    private void AddTrayIcon()
    {
        if (_added)
            return;

        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwndSource.Handle,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _hIcon,
            szTip = _tooltip
        };
        _added = Shell_NotifyIcon(NIM_ADD, ref data);
    }

    private void RemoveTrayIcon()
    {
        if (!_added)
            return;

        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwndSource.Handle,
            uID = 1
        };
        Shell_NotifyIcon(NIM_DELETE, ref data);
        _added = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAYICON)
        {
            int mouseMsg = lParam.ToInt32() & 0xFFFF;
            switch (mouseMsg)
            {
                case WM_LBUTTONUP:
                    _onLeftClick();
                    handled = true;
                    break;
                case WM_LBUTTONDBLCLK:
                    _onDoubleClick();
                    handled = true;
                    break;
                case WM_RBUTTONUP:
                    ShowTrayMenu();
                    handled = true;
                    break;
            }
        }
        return IntPtr.Zero;
    }

    private void ShowTrayMenu()
    {
        // Win32 标准做法：先 SetForegroundWindow 再弹出菜单，点击外部可自动关闭
        SetForegroundWindow(_hwndSource.Handle);
        _menu.Placement = PlacementMode.MousePoint;
        _menu.IsOpen = true;
    }

    private static IntPtr LoadHighDpiIconHandle()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
            {
                // 从当前进程 EXE 资源提取高分辨率小图标（适配高分屏 32/48/64px，避免 16px 强行拉伸发糊）
                int desiredSize = Math.Clamp(GetSystemMetrics(49 /* SM_CXSMICON */) * 2, 32, 64);
                var uri = new Uri("pack://application:,,,/Assets/deskbox.ico", UriKind.Absolute);
                var info = Application.GetResourceStream(uri);
                if (info?.Stream != null)
                {
                    using (info.Stream)
                    {
                        var decoder = new IconBitmapDecoder(
                            info.Stream,
                            BitmapCreateOptions.PreservePixelFormat,
                            BitmapCacheOption.OnLoad);

                        var frame = decoder.Frames
                            .OrderBy(f => Math.Abs(f.PixelWidth - desiredSize))
                            .ThenByDescending(f => f.PixelWidth)
                            .FirstOrDefault();

                        if (frame != null)
                        {
                            return CreateIconFromBitmapSource(frame);
                        }
                    }
                }
            }
        }
        catch
        {
        }

        return CreateFallbackHighResIconHandle();
    }

    private static IntPtr CreateIconFromBitmapSource(BitmapSource source)
    {
        var formatted = new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        int width = formatted.PixelWidth;
        int height = formatted.PixelHeight;
        int stride = width * 4;
        byte[] bgra = new byte[stride * height];
        formatted.CopyPixels(bgra, stride, 0);

        byte[] andMask = new byte[(width + 7) / 8 * height];
        var hColor = CreateBitmap(width, height, 1, 32, bgra);
        var hMask = CreateBitmap(width, height, 1, 1, andMask);
        try
        {
            var iconInfo = new ICONINFO
            {
                fIcon = true,
                xHotspot = 0,
                yHotspot = 0,
                hbmMask = hMask,
                hbmColor = hColor
            };
            return CreateIconIndirect(ref iconInfo);
        }
        finally
        {
            if (hColor != IntPtr.Zero) DeleteObject(hColor);
            if (hMask != IntPtr.Zero) DeleteObject(hMask);
        }
    }

    private static IntPtr CreateFallbackHighResIconHandle()
    {
        // 纯内存 32x32 ARGB 兜底图标，配合高分屏保持边缘清晰
        const int size = 32;
        byte[] xorMask = new byte[size * size * 4];
        byte[] andMask = new byte[size * size / 8];

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int idx = (y * size + x) * 4;
                bool inCornerCut = (x < 3 || x >= size - 3) && (y < 3 || y >= size - 3);
                if (inCornerCut)
                    continue;

                bool inWhiteCell =
                    (x is >= 6 and <= 13 && y is >= 6 and <= 13) ||
                    (x is >= 18 and <= 25 && y is >= 6 and <= 13) ||
                    (x is >= 6 and <= 13 && y is >= 18 and <= 25) ||
                    (x is >= 18 and <= 25 && y is >= 18 and <= 25);

                if (inWhiteCell)
                {
                    xorMask[idx] = 255;
                    xorMask[idx + 1] = 255;
                    xorMask[idx + 2] = 255;
                    xorMask[idx + 3] = 255;
                }
                else
                {
                    xorMask[idx] = 216;
                    xorMask[idx + 1] = 78;
                    xorMask[idx + 2] = 29;
                    xorMask[idx + 3] = 255;
                }
            }
        }

        var hColor = CreateBitmap(size, size, 1, 32, xorMask);
        var hMask = CreateBitmap(size, size, 1, 1, andMask);
        try
        {
            var info = new ICONINFO
            {
                fIcon = true,
                xHotspot = 0,
                yHotspot = 0,
                hbmMask = hMask,
                hbmColor = hColor
            };
            return CreateIconIndirect(ref info);
        }
        finally
        {
            if (hColor != IntPtr.Zero) DeleteObject(hColor);
            if (hMask != IntPtr.Zero) DeleteObject(hMask);
        }
    }

    public void Dispose()
    {
        RemoveTrayIcon();
        if (_hIcon != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_hIcon);
        }
        _hwndSource.RemoveHook(WndProc);
        _hwndSource.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint cPlanes, uint cBitsPerPel, byte[] lpvBits);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
