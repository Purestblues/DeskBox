using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using DeskBox.Interop;

namespace DeskBox.Services;

/// <summary>
/// 低开销全局鼠标钩子：仅检测在 Windows 桌面空白区域上的双击，触发切换纯净桌面（隐藏/显示全部格子与桌面图标）。
/// </summary>
public sealed class DesktopInteractionHook : IDisposable
{
    private const int LVM_GETSELECTEDCOUNT = 0x1032;

    private readonly Dispatcher _dispatcher;
    private readonly DesktopHostService _hostService;
    private readonly Func<bool> _isEnabled;
    private readonly Action _onDoubleClickDesktopBlank;

    private NativeMethods.LowLevelMouseProc? _proc;
    private IntPtr _hookHandle;
    private long _lastLeftDownTicks;
    private NativeMethods.POINT _lastLeftDownPoint;

    public DesktopInteractionHook(
        Dispatcher dispatcher,
        DesktopHostService hostService,
        Func<bool> isEnabled,
        Action onDoubleClickDesktopBlank)
    {
        _dispatcher = dispatcher;
        _hostService = hostService;
        _isEnabled = isEnabled;
        _onDoubleClickDesktopBlank = onDoubleClickDesktopBlank;
    }

    public void Start()
    {
        if (_hookHandle != IntPtr.Zero)
            return;

        _proc = HookCallback;
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        var moduleHandle = NativeMethods.GetModuleHandle(curModule?.ModuleName);
        _hookHandle = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _proc, moduleHandle, 0);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _isEnabled())
        {
            if (wParam == (IntPtr)NativeMethods.WM_RBUTTONDOWN)
            {
                _lastLeftDownTicks = 0;
            }
            else if (wParam == (IntPtr)NativeMethods.WM_LBUTTONDOWN)
            {
                var hookStruct = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                var now = Environment.TickCount64;
                var dblTime = Math.Max(200, NativeMethods.GetDoubleClickTime());
                var maxDx = Math.Max(8, NativeMethods.GetSystemMetrics(NativeMethods.SM_CXDOUBLECLK));
                var maxDy = Math.Max(8, NativeMethods.GetSystemMetrics(NativeMethods.SM_CYDOUBLECLK));

                var dx = Math.Abs(hookStruct.pt.X - _lastLeftDownPoint.X);
                var dy = Math.Abs(hookStruct.pt.Y - _lastLeftDownPoint.Y);

                if (now - _lastLeftDownTicks <= dblTime && dx <= maxDx && dy <= maxDy)
                {
                    _lastLeftDownTicks = 0;
                    var pt = hookStruct.pt;
                    _dispatcher.BeginInvoke(() => CheckAndTriggerDesktopBlankDoubleClick(pt));
                }
                else
                {
                    _lastLeftDownTicks = now;
                    _lastLeftDownPoint = hookStruct.pt;
                }
            }
        }

        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private void CheckAndTriggerDesktopBlankDoubleClick(NativeMethods.POINT pt)
    {
        // 按住 Shift / Ctrl 进行多选时不触发纯净桌面切换
        if ((NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0 ||
            (NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0)
        {
            return;
        }

        var hwnd = NativeMethods.WindowFromPoint(pt);
        var hit = _hostService.ClassifyDesktopHitWindow(hwnd);
        if (hit == DesktopHostService.DesktopHitTarget.None)
            return;

        if (hit == DesktopHostService.DesktopHitTarget.DesktopListView)
        {
            // 若桌面图标处于选中状态（用户刚双击打开某图标），则不触发隐藏
            var selectedCount = NativeMethods.SendMessage(hwnd, LVM_GETSELECTEDCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32();
            if (selectedCount > 0)
                return;
        }

        _onDoubleClickDesktopBlank();
    }

    public void Dispose()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
        _proc = null;
    }
}
