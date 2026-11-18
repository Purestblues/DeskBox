using System.Runtime.InteropServices;
using DeskBox.Interop;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// 负责多显示器硬件识别、拓扑签名生成、以及基于归一化相对坐标的无损布局捕获与还原。
/// </summary>
public sealed class MonitorTopologyService
{
    public readonly record struct PhysicalRect(int X, int Y, int Width, int Height);

    public IReadOnlyList<MonitorDescriptor> GetConnectedMonitors()
    {
        var list = new List<MonitorDescriptor>();

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr _, ref NativeMethods.RECT _, IntPtr _) =>
        {
            var mi = new NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
            if (!NativeMethods.GetMonitorInfo(hMon, ref mi))
                return true;

            uint dpiX = 96, dpiY = 96;
            try
            {
                if (NativeMethods.GetDpiForMonitor(hMon, 0, out var dx, out var dy) == 0 && dx > 0 && dy > 0)
                {
                    dpiX = dx;
                    dpiY = dy;
                }
            }
            catch
            {
                // Fallback to 96 DPI on older systems
            }

            var (monitorId, friendlyName) = ResolveMonitorIdentity(mi.szDevice, mi.rcMonitor.Width, mi.rcMonitor.Height);
            list.Add(new MonitorDescriptor
            {
                MonitorId = monitorId,
                DeviceName = mi.szDevice,
                FriendlyName = friendlyName,
                IsPrimary = (mi.dwFlags & 1) != 0,
                BoundsX = mi.rcMonitor.Left,
                BoundsY = mi.rcMonitor.Top,
                BoundsWidth = mi.rcMonitor.Width,
                BoundsHeight = mi.rcMonitor.Height,
                WorkX = mi.rcWork.Left,
                WorkY = mi.rcWork.Top,
                WorkWidth = mi.rcWork.Width,
                WorkHeight = mi.rcWork.Height,
                DpiX = dpiX,
                DpiY = dpiY
            });

            return true;
        }, IntPtr.Zero);

        return list.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.BoundsX).ThenBy(m => m.BoundsY).ToList();
    }

    private static (string monitorId, string friendlyName) ResolveMonitorIdentity(string deviceName, int width, int height)
    {
        var dd = new NativeMethods.DISPLAY_DEVICE { cb = Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>() };
        if (NativeMethods.EnumDisplayDevices(deviceName, 0, ref dd, NativeMethods.EDD_GET_DEVICE_INTERFACE_NAME))
        {
            var rawId = dd.DeviceID ?? string.Empty;
            var friendly = string.IsNullOrWhiteSpace(dd.DeviceString) ? deviceName : dd.DeviceString.Trim();
            if (!string.IsNullOrWhiteSpace(rawId))
            {
                // 提取稳定的硬件路径前缀，例如 DISPLAY#DELA123#5&...
                var parts = rawId.Split('#');
                var stablePart = parts.Length >= 3 ? $"{parts[1]}_{parts[2]}" : rawId;
                return (stablePart, friendly);
            }
        }

        // 回退读取不带 INTERFACE_NAME 的显示设备名
        var ddBasic = new NativeMethods.DISPLAY_DEVICE { cb = Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>() };
        if (NativeMethods.EnumDisplayDevices(deviceName, 0, ref ddBasic, 0))
        {
            var friendly = string.IsNullOrWhiteSpace(ddBasic.DeviceString) ? deviceName : ddBasic.DeviceString.Trim();
            if (!string.IsNullOrWhiteSpace(ddBasic.DeviceID))
                return (ddBasic.DeviceID, friendly);
            return ($"{deviceName}_{width}x{height}", friendly);
        }

        return ($"{deviceName}_{width}x{height}", deviceName);
    }

    public static string ComputeTopologySignature(IReadOnlyList<MonitorDescriptor> monitors)
    {
        if (monitors.Count == 0)
            return "EMPTY";

        var tokens = monitors
            .OrderBy(m => m.MonitorId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.BoundsX)
            .ThenBy(m => m.BoundsY)
            .Select(m => $"{m.MonitorId}@{m.BoundsX},{m.BoundsY}:{m.BoundsWidth}x{m.BoundsHeight}@{m.DpiX}");

        return $"{monitors.Count}M|{string.Join("|", tokens)}";
    }

    public static string BuildTopologyDescription(IReadOnlyList<MonitorDescriptor> monitors)
    {
        if (monitors.Count == 0)
            return "未检测到显示器";
        return string.Join(" + ", monitors.Select(m => m.Summary));
    }

    public static MonitorDescriptor FindOwnerMonitor(PhysicalRect rect, IReadOnlyList<MonitorDescriptor> monitors)
    {
        if (monitors.Count == 0)
            throw new InvalidOperationException("No monitors available.");

        var centerX = rect.X + rect.Width / 2;
        var centerY = rect.Y + rect.Height / 2;

        var containing = monitors.FirstOrDefault(m =>
            centerX >= m.BoundsX && centerX < m.BoundsX + m.BoundsWidth &&
            centerY >= m.BoundsY && centerY < m.BoundsY + m.BoundsHeight);

        if (containing != null)
            return containing;

        // 若中心点不在任何屏幕内，选交集面积最大的显示器
        MonitorDescriptor? best = null;
        long maxArea = -1;
        foreach (var m in monitors)
        {
            var ix1 = Math.Max(rect.X, m.BoundsX);
            var iy1 = Math.Max(rect.Y, m.BoundsY);
            var ix2 = Math.Min(rect.X + rect.Width, m.BoundsX + m.BoundsWidth);
            var iy2 = Math.Min(rect.Y + rect.Height, m.BoundsY + m.BoundsHeight);
            var area = (long)Math.Max(0, ix2 - ix1) * Math.Max(0, iy2 - iy1);
            if (area > maxArea)
            {
                maxArea = area;
                best = m;
            }
        }

        return best ?? monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
    }

    public static BoxPlacement CapturePlacement(
        PhysicalRect rect,
        int expandedPhysicalHeight,
        bool isCollapsed,
        IReadOnlyList<MonitorDescriptor> monitors)
    {
        var owner = FindOwnerMonitor(rect, monitors);
        var workW = Math.Max(1.0, owner.WorkWidth);
        var workH = Math.Max(1.0, owner.WorkHeight);

        var relW = Math.Clamp(rect.Width / workW, 0.08, 0.95);
        var relExpandedH = Math.Clamp(expandedPhysicalHeight / workH, 0.10, 0.95);
        var relX = Math.Clamp((rect.X - owner.WorkX) / workW, 0.0, Math.Max(0.0, 1.0 - relW));
        var relY = Math.Clamp((rect.Y - owner.WorkY) / workH, 0.0, Math.Max(0.0, 1.0 - 0.03));

        return new BoxPlacement
        {
            MonitorId = owner.MonitorId,
            WasPrimaryMonitor = owner.IsPrimary,
            RelX = Math.Round(relX, 5),
            RelY = Math.Round(relY, 5),
            RelWidth = Math.Round(relW, 5),
            RelExpandedHeight = Math.Round(relExpandedH, 5),
            IsCollapsed = isCollapsed
        };
    }

    public static (PhysicalRect expandedRect, int collapsedHeaderHeight, MonitorDescriptor targetMonitor, bool matchedExactMonitor)
        ResolvePhysicalPlacement(BoxPlacement placement, IReadOnlyList<MonitorDescriptor> monitors)
    {
        if (monitors.Count == 0)
            throw new InvalidOperationException("No monitors available.");

        var exact = monitors.FirstOrDefault(m => string.Equals(m.MonitorId, placement.MonitorId, StringComparison.OrdinalIgnoreCase));
        var matchedExact = exact != null;

        var target = exact
            ?? (placement.WasPrimaryMonitor ? monitors.FirstOrDefault(m => m.IsPrimary) : null)
            ?? monitors.FirstOrDefault(m => m.IsPrimary)
            ?? monitors[0];

        var minWidth = Math.Max(200, (int)Math.Round(130 * target.ScaleX));
        var minHeight = (int)Math.Round(120 * target.ScaleY);
        var headerHeight = (int)Math.Round(34 * target.ScaleY);

        var w = Math.Clamp((int)Math.Round(placement.RelWidth * target.WorkWidth), minWidth, Math.Max(minWidth, target.WorkWidth));
        var h = Math.Clamp((int)Math.Round(placement.RelExpandedHeight * target.WorkHeight), minHeight, Math.Max(minHeight, target.WorkHeight));

        var x = target.WorkX + (int)Math.Round(placement.RelX * target.WorkWidth);
        var y = target.WorkY + (int)Math.Round(placement.RelY * target.WorkHeight);

        // 安全边界 Clamping：确保任何时候窗口都不会跑到屏幕外面点不到
        x = Math.Clamp(x, target.WorkX, Math.Max(target.WorkX, target.WorkX + target.WorkWidth - w));
        var effectiveH = placement.IsCollapsed ? headerHeight : h;
        y = Math.Clamp(y, target.WorkY, Math.Max(target.WorkY, target.WorkY + target.WorkHeight - Math.Min(effectiveH, target.WorkHeight)));

        return (new PhysicalRect(x, y, w, h), headerHeight, target, matchedExact);
    }

    /// <summary>
    /// 拖动格子时将位置严格约束在鼠标当前所在显示器的安全工作区内，既支持双屏无缝跨屏，又彻底防止拖出边界或进入异形屏黑洞区。
    /// </summary>
    public static (int x, int y) ClampDragPositionToMonitors(
        int candidateX,
        int candidateY,
        int width,
        int visibleHeight,
        int cursorX,
        int cursorY,
        IReadOnlyList<MonitorDescriptor> monitors)
    {
        if (monitors.Count == 0)
            return (candidateX, candidateY);

        var target = monitors.FirstOrDefault(m =>
            cursorX >= m.BoundsX && cursorX < m.BoundsX + m.BoundsWidth &&
            cursorY >= m.BoundsY && cursorY < m.BoundsY + m.BoundsHeight)
            ?? FindOwnerMonitor(new PhysicalRect(candidateX, candidateY, width, visibleHeight), monitors);

        int maxW = Math.Min(width, target.WorkWidth);
        int headerMinH = Math.Min(visibleHeight, target.WorkHeight);

        int clampedX = Math.Clamp(candidateX, target.WorkX, Math.Max(target.WorkX, target.WorkX + target.WorkWidth - maxW));
        int clampedY = Math.Clamp(candidateY, target.WorkY, Math.Max(target.WorkY, target.WorkY + target.WorkHeight - headerMinH));
        return (clampedX, clampedY);
    }
}
