using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DeskBox.Interop;
using Microsoft.Win32;

namespace DeskBox.Services;

/// <summary>
/// 负责将格子窗口稳定保持在桌面层（不遮挡普通应用、不进 Alt+Tab、兼容 Win+D），
/// 以及控制原生桌面图标可见性与开机自启。
/// </summary>
public sealed class DesktopHostService
{
    private const string AutoStartRegKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string DesktopBgShellRegKey = @"Software\Classes\DesktopBackground\Shell";
    private const string MenuKeyNewBox = "DeskBox_1_NewBox";
    private const string MenuKeyToggleTidy = "DeskBox_2_ToggleTidy";
    private const string MenuKeySettings = "DeskBox_3_Settings";
    private const string AppName = "DeskBox";

    public static string GetToggleTidyMenuText(bool isTidyPaused) =>
        isTidyPaused ? "开启桌面整理" : "临时退出桌面整理";

    public static void RegisterOrUpdateDesktopContextMenu(bool isTidyPaused, string? exePathOverride = null)
    {
        try
        {
            var exePath = exePathOverride ?? Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
                return;

            using var shellKey = Registry.CurrentUser.CreateSubKey(DesktopBgShellRegKey, writable: true);
            if (shellKey == null)
                return;

            WriteMenuVerb(shellKey, MenuKeyNewBox, "新建格子", exePath, "--new-box");
            WriteMenuVerb(shellKey, MenuKeyToggleTidy, GetToggleTidyMenuText(isTidyPaused), exePath, "--toggle-tidy");
            WriteMenuVerb(shellKey, MenuKeySettings, "打开设置中心", exePath, "--settings");
        }
        catch
        {
        }
    }

    public static void UnregisterDesktopContextMenu()
    {
        try
        {
            using var shellKey = Registry.CurrentUser.OpenSubKey(DesktopBgShellRegKey, writable: true);
            if (shellKey == null)
                return;

            shellKey.DeleteSubKeyTree(MenuKeyNewBox, throwOnMissingSubKey: false);
            shellKey.DeleteSubKeyTree(MenuKeyToggleTidy, throwOnMissingSubKey: false);
            shellKey.DeleteSubKeyTree(MenuKeySettings, throwOnMissingSubKey: false);
        }
        catch
        {
        }
    }

    private static void WriteMenuVerb(RegistryKey shellKey, string subKeyName, string label, string exePath, string argument)
    {
        using var itemKey = shellKey.CreateSubKey(subKeyName, writable: true);
        if (itemKey == null)
            return;

        itemKey.SetValue(null, label);
        itemKey.SetValue("MUIVerb", label);
        itemKey.SetValue("Icon", $"\"{exePath}\",0");

        using var cmdKey = itemKey.CreateSubKey("command", writable: true);
        cmdKey?.SetValue(null, $"\"{exePath}\" {argument}");
    }

    public IntPtr FindDesktopHostWindow()
    {
        var progman = NativeMethods.FindWindow("Progman", null);
        if (progman != IntPtr.Zero)
        {
            var defView = NativeMethods.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero)
                return progman;
        }

        IntPtr workerWHost = IntPtr.Zero;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            var defView = NativeMethods.FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero)
            {
                workerWHost = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);

        return workerWHost != IntPtr.Zero ? workerWHost : progman;
    }

    public (IntPtr shellDefView, IntPtr listView) FindDesktopListView()
    {
        var progman = NativeMethods.FindWindow("Progman", null);
        if (progman != IntPtr.Zero)
        {
            var defView = NativeMethods.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero)
            {
                var lv = NativeMethods.FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
                return (defView, lv);
            }
        }

        IntPtr foundDef = IntPtr.Zero;
        IntPtr foundLv = IntPtr.Zero;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            var defView = NativeMethods.FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero)
            {
                foundDef = defView;
                foundLv = NativeMethods.FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
                return false;
            }
            return true;
        }, IntPtr.Zero);

        return (foundDef, foundLv);
    }

    public bool IsNativeDesktopIconsVisible()
    {
        var (_, lv) = FindDesktopListView();
        return lv != IntPtr.Zero && NativeMethods.IsWindowVisible(lv);
    }

    public void SetNativeDesktopIconsVisible(bool visible)
    {
        var (defView, lv) = FindDesktopListView();
        if (defView == IntPtr.Zero || lv == IntPtr.Zero)
            return;

        var current = NativeMethods.IsWindowVisible(lv);
        if (current != visible)
        {
            // 0x7402 = "显示桌面图标" 的 Shell 视图命令 ID
            NativeMethods.SendMessage(defView, NativeMethods.WM_COMMAND, new IntPtr(0x7402), IntPtr.Zero);
            var targetRedraw = visible ? lv : defView;
            NativeMethods.RedrawWindow(
                targetRedraw,
                IntPtr.Zero,
                IntPtr.Zero,
                NativeMethods.RDW_INVALIDATE | NativeMethods.RDW_ERASE | NativeMethods.RDW_UPDATENOW);
            NativeMethods.UpdateWindow(targetRedraw);
        }
    }

    public enum DesktopHitTarget
    {
        None,
        DesktopListView,
        DesktopSurface
    }

    public DesktopHitTarget ClassifyDesktopHitWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return DesktopHitTarget.None;

        var (defView, lv) = FindDesktopListView();
        if (hwnd == lv)
            return DesktopHitTarget.DesktopListView;

        if (hwnd == defView)
            return DesktopHitTarget.DesktopSurface;

        var progman = NativeMethods.FindWindow("Progman", null);
        var host = FindDesktopHostWindow();
        var root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);

        if (hwnd == progman || hwnd == host || root == progman || (host != IntPtr.Zero && root == host))
            return DesktopHitTarget.DesktopSurface;

        var cls = new System.Text.StringBuilder(64);
        NativeMethods.GetClassName(hwnd, cls, cls.Capacity);
        if (cls.ToString() == "WorkerW" && progman != IntPtr.Zero)
        {
            // Win10/Win11 壁纸层的顶级 WorkerW 与 Progman 同属桌面 Explorer 进程
            NativeMethods.GetWindowThreadProcessId(progman, out var progmanPid);
            NativeMethods.GetWindowThreadProcessId(hwnd, out var hitPid);
            if (hitPid != 0 && hitPid == progmanPid)
                return DesktopHitTarget.DesktopSurface;
        }

        return DesktopHitTarget.None;
    }

    public void RefreshDesktopShellView()
    {
        var (defView, _) = FindDesktopListView();
        if (defView != IntPtr.Zero)
        {
            // 0x7103 = Shell View 刷新（等价于在桌面按 F5），使系统图标显隐立即生效
            NativeMethods.SendMessage(defView, NativeMethods.WM_COMMAND, new IntPtr(0x7103), IntPtr.Zero);
        }
    }

    /// <summary>
    /// 配置窗口为桌面挂件风格：隐藏于任务栏/Alt+Tab，沉底于普通应用下方，并强制锁定 Z-Order 为 HWND_BOTTOM（零重入、不卡死）。
    /// </summary>
    public void AttachDesktopWidgetBehavior(
        Window window,
        bool frostedGlass,
        System.Windows.Media.Color tintColor,
        double opacity,
        Func<bool>? allowBringForward = null,
        Action? onSentToBottom = null)
    {
        var helper = new WindowInteropHelper(window);
        var hwnd = helper.Handle;
        if (hwnd == IntPtr.Zero)
            return;

        // 1. 设为 TOOLWINDOW，不进 Alt+Tab 和任务栏
        var exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, exStyle);

        // 2. 挂接桌面窗口作为 Owner（防止 Win+D 把格子一起最小化）
        var desktopHost = FindDesktopHostWindow();
        if (desktopHost != IntPtr.Zero)
        {
            NativeMethods.SetWindowOwner(hwnd, desktopHost);
        }

        // 3. 初始沉底于普通应用下方
        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_BOTTOM,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook((IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (lParam != IntPtr.Zero && (allowBringForward == null || !allowBringForward()))
            {
                if (msg == NativeMethods.WM_WINDOWPOSCHANGING)
                {
                    var pos = Marshal.PtrToStructure<NativeMethods.WINDOWPOS>(lParam);
                    if ((pos.flags & NativeMethods.SWP_NOZORDER) == 0 || (pos.flags & NativeMethods.SWP_SHOWWINDOW) != 0)
                    {
                        // 禁止点击/激活/显示时浮到普通应用之上：显式固定为 HWND_BOTTOM
                        pos.flags &= ~NativeMethods.SWP_NOZORDER;
                        pos.hwndInsertAfter = NativeMethods.HWND_BOTTOM;
                        Marshal.StructureToPtr(pos, lParam, false);
                    }
                }
                else if (msg == NativeMethods.WM_WINDOWPOSCHANGED)
                {
                    var pos = Marshal.PtrToStructure<NativeMethods.WINDOWPOS>(lParam);
                    if ((pos.flags & NativeMethods.SWP_NOZORDER) == 0 || (pos.flags & NativeMethods.SWP_SHOWWINDOW) != 0)
                    {
                        onSentToBottom?.Invoke();
                    }
                }
            }
            return IntPtr.Zero;
        });
    }

    /// <summary>
    /// 配置全屏透明桌面画布窗口：永远沉底在所有普通窗口和格子窗口之下，不进 Alt+Tab，兼容 Win+D。
    /// </summary>
    public void AttachDesktopCanvasBehavior(Window window, Func<bool>? allowZOrderChange = null)
    {
        var helper = new WindowInteropHelper(window);
        var hwnd = helper.Handle;
        if (hwnd == IntPtr.Zero)
            return;

        var exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, exStyle);

        var desktopHost = FindDesktopHostWindow();
        if (desktopHost != IntPtr.Zero)
        {
            NativeMethods.SetWindowOwner(hwnd, desktopHost);
        }

        SendWindowToBottom(window);

        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook((IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == NativeMethods.WM_WINDOWPOSCHANGING && lParam != IntPtr.Zero)
            {
                if (allowZOrderChange == null || !allowZOrderChange())
                {
                    var pos = Marshal.PtrToStructure<NativeMethods.WINDOWPOS>(lParam);
                    if ((pos.flags & NativeMethods.SWP_NOZORDER) == 0 || (pos.flags & NativeMethods.SWP_SHOWWINDOW) != 0)
                    {
                        // 禁止点击/激活/显示时将桌面画布抬高到普通窗口之上，强制保持在 HWND_BOTTOM
                        pos.flags &= ~NativeMethods.SWP_NOZORDER;
                        pos.hwndInsertAfter = NativeMethods.HWND_BOTTOM;
                        Marshal.StructureToPtr(pos, lParam, false);
                    }
                }
            }
            return IntPtr.Zero;
        });
    }

    public static void SendWindowToBottom(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_BOTTOM,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    public static void ApplyBackdropAndCorners(IntPtr hwnd, bool frostedGlass, System.Windows.Media.Color tintColor, double opacity)
    {
        if (hwnd == IntPtr.Zero)
            return;

        try
        {
            // 小圆角（DWMWCP_ROUNDSMALL = 3）更贴近桌面收纳卡片的轻盈观感
            int corner = 3;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

            // 去除 Win11 默认的生硬 1px 灰白窗口边框（DWMWA_BORDER_COLOR = 34, DWMWA_COLOR_NONE = 0xFFFFFFFE）
            unchecked
            {
                int noBorder = (int)0xFFFFFFFE;
                NativeMethods.DwmSetWindowAttribute(hwnd, 34, ref noBorder, sizeof(int));
            }
        }
        catch { }

        // 先确保清空系统预设 Backdrop，改用可控 Alpha 的 SetWindowCompositionAttribute
        try
        {
            const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
            int noneBackdrop = 1; // DWMSBT_NONE
            NativeMethods.DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref noneBackdrop, sizeof(int));
        }
        catch { }

        try
        {
            // 使用 ACCENT_ENABLE_ACRYLICBLURBEHIND (4) 配合低 Alpha ABGR，可自由控制通透度而不发灰发闷
            // opacity(0.20..0.90) -> alpha(0x18..0xB8)，在 0.35..0.55 时可清晰透出壁纸颜色与模糊轮廓
            var alpha = (uint)Math.Clamp((int)Math.Round(opacity * 120), 22, 195);
            uint abgr = (alpha << 24) | ((uint)tintColor.B << 16) | ((uint)tintColor.G << 8) | tintColor.R;

            var accent = new NativeMethods.AccentPolicy
            {
                AccentState = frostedGlass ? 4 : 0,
                AccentFlags = 2,
                GradientColor = frostedGlass ? abgr : 0
            };
            int size = Marshal.SizeOf(accent);
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, ptr, false);
                var data = new NativeMethods.WindowCompositionAttributeData
                {
                    Attribute = 19,
                    Data = ptr,
                    SizeOfData = size
                };
                NativeMethods.SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch { }
    }

    public static void MoveWindowPhysical(Window window, int x, int y, int width, int height)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_BOTTOM,
            x, y, width, height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER);
    }

    public static void SetAutoStart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutoStartRegKey, writable: true);
            if (key == null)
                return;

            if (enabled)
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exePath))
                {
                    key.SetValue(AppName, $"\"{exePath}\"");
                }
            }
            else
            {
                key.DeleteValue(AppName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Ignore permission issues
        }
    }

    public static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutoStartRegKey, writable: false);
            return key?.GetValue(AppName) != null;
        }
        catch
        {
            return false;
        }
    }
}
