using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// 桌面画布图标的对齐网格自由摆放算法（纯逻辑，支持多屏、不重叠、拖拽就近落位与一键排序）。
/// </summary>
public static class DesktopGridLayoutService
{
    public const double DefaultCellWidth = 86.0;
    public const double DefaultCellHeight = 98.0;
    public const double DefaultMargin = 10.0;

    public readonly record struct GridMetrics(int Columns, int Rows, double CellWidth, double CellHeight, double Margin);

    public static GridMetrics ComputeMetrics(double canvasWidthDip, double canvasHeightDip)
    {
        double usableW = Math.Max(DefaultCellWidth, canvasWidthDip - DefaultMargin * 2);
        double usableH = Math.Max(DefaultCellHeight, canvasHeightDip - DefaultMargin * 2);
        int cols = Math.Max(1, (int)Math.Floor(usableW / DefaultCellWidth));
        int rows = Math.Max(1, (int)Math.Floor(usableH / DefaultCellHeight));
        return new GridMetrics(cols, rows, DefaultCellWidth, DefaultCellHeight, DefaultMargin);
    }

    public static (double Left, double Top) CellToCanvasPoint(int col, int row, GridMetrics metrics)
    {
        int c = Math.Clamp(col, 0, Math.Max(0, metrics.Columns - 1));
        int r = Math.Clamp(row, 0, Math.Max(0, metrics.Rows - 1));
        return (metrics.Margin + c * metrics.CellWidth, metrics.Margin + r * metrics.CellHeight);
    }

    public static (int Col, int Row) CanvasPointToCell(double x, double y, GridMetrics metrics)
    {
        int col = (int)Math.Floor((x - metrics.Margin) / metrics.CellWidth);
        int row = (int)Math.Floor((y - metrics.Margin) / metrics.CellHeight);
        return (
            Math.Clamp(col, 0, Math.Max(0, metrics.Columns - 1)),
            Math.Clamp(row, 0, Math.Max(0, metrics.Rows - 1)));
    }

    public static (int Col, int Row) FindNearestFreeCell(
        int desiredCol,
        int desiredRow,
        GridMetrics metrics,
        IReadOnlySet<(int Col, int Row)> occupied)
    {
        int clampedCol = Math.Clamp(desiredCol, 0, Math.Max(0, metrics.Columns - 1));
        int clampedRow = Math.Clamp(desiredRow, 0, Math.Max(0, metrics.Rows - 1));
        if (!occupied.Contains((clampedCol, clampedRow)))
            return (clampedCol, clampedRow);

        int maxRadius = Math.Max(metrics.Columns, metrics.Rows);
        for (int radius = 1; radius <= maxRadius; radius++)
        {
            (int Col, int Row)? best = null;
            int bestDistSq = int.MaxValue;

            int minC = Math.Max(0, clampedCol - radius);
            int maxC = Math.Min(metrics.Columns - 1, clampedCol + radius);
            int minR = Math.Max(0, clampedRow - radius);
            int maxR = Math.Min(metrics.Rows - 1, clampedRow + radius);

            for (int c = minC; c <= maxC; c++)
            {
                for (int r = minR; r <= maxR; r++)
                {
                    if (Math.Max(Math.Abs(c - clampedCol), Math.Abs(r - clampedRow)) != radius)
                        continue;
                    if (occupied.Contains((c, r)))
                        continue;

                    int distSq = (c - clampedCol) * (c - clampedCol) + (r - clampedRow) * (r - clampedRow);
                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        best = (c, r);
                    }
                }
            }

            if (best.HasValue)
                return best.Value;
        }

        return (clampedCol, clampedRow);
    }

    /// <summary>
    /// 按列优先从左上角寻找第一个空闲网格（与 Windows 原生桌面默认排列一致）。
    /// </summary>
    public static (int Col, int Row) FindFirstFreeCellColumnMajor(
        GridMetrics metrics,
        IReadOnlySet<(int Col, int Row)> occupied)
    {
        for (int c = 0; c < metrics.Columns; c++)
        {
            for (int r = 0; r < metrics.Rows; r++)
            {
                if (!occupied.Contains((c, r)))
                    return (c, r);
            }
        }
        return (0, 0);
    }

    /// <summary>
    /// 为当前所有未收纳桌面项解析有效的多屏网格坐标，并补齐缺失/冲突/越界项。
    /// </summary>
    public static Dictionary<string, DesktopIconGridPosition> ResolveAllPositions(
        IReadOnlyList<string> itemPaths,
        IReadOnlyDictionary<string, DesktopIconGridPosition> savedPositions,
        IReadOnlyList<(string MonitorId, bool IsPrimary, GridMetrics Metrics)> monitorGrids,
        out bool changed)
    {
        changed = false;
        var resolved = new Dictionary<string, DesktopIconGridPosition>(StringComparer.OrdinalIgnoreCase);
        if (monitorGrids.Count == 0 || itemPaths.Count == 0)
            return resolved;

        var primary = monitorGrids.FirstOrDefault(m => m.IsPrimary);
        if (string.IsNullOrEmpty(primary.MonitorId))
            primary = monitorGrids[0];

        var gridMap = monitorGrids.ToDictionary(m => m.MonitorId, m => m.Metrics, StringComparer.OrdinalIgnoreCase);
        var occupiedByMonitor = monitorGrids.ToDictionary(
            m => m.MonitorId,
            _ => new HashSet<(int Col, int Row)>(),
            StringComparer.OrdinalIgnoreCase);

        var unassigned = new List<string>();

        // 第一遍：先放置已有且显示器有效的坐标
        foreach (var path in itemPaths)
        {
            if (savedPositions.TryGetValue(path, out var saved) &&
                !string.IsNullOrWhiteSpace(saved.MonitorId) &&
                gridMap.TryGetValue(saved.MonitorId, out var metrics))
            {
                var occupied = occupiedByMonitor[saved.MonitorId];
                var (c, r) = FindNearestFreeCell(saved.Col, saved.Row, metrics, occupied);
                occupied.Add((c, r));
                resolved[path] = new DesktopIconGridPosition { MonitorId = saved.MonitorId, Col = c, Row = r };
                if (c != saved.Col || r != saved.Row)
                    changed = true;
            }
            else
            {
                unassigned.Add(path);
            }
        }

        // 第二遍：把新增项或断开屏幕上的项依次落位到主屏首个空闲网格
        if (unassigned.Count > 0)
        {
            changed = true;
            var primaryOccupied = occupiedByMonitor[primary.MonitorId];
            foreach (var path in unassigned)
            {
                var (c, r) = FindFirstFreeCellColumnMajor(primary.Metrics, primaryOccupied);
                primaryOccupied.Add((c, r));
                resolved[path] = new DesktopIconGridPosition { MonitorId = primary.MonitorId, Col = c, Row = r };
            }
        }

        return resolved;
    }

    /// <summary>
    /// 按给定顺序在主屏按列重新整齐排列所有未收纳图标。
    /// </summary>
    public static Dictionary<string, DesktopIconGridPosition> ArrangeColumnMajor(
        IReadOnlyList<string> orderedPaths,
        string targetMonitorId,
        GridMetrics metrics)
    {
        var result = new Dictionary<string, DesktopIconGridPosition>(StringComparer.OrdinalIgnoreCase);
        var occupied = new HashSet<(int Col, int Row)>();
        foreach (var path in orderedPaths)
        {
            var (c, r) = FindFirstFreeCellColumnMajor(metrics, occupied);
            occupied.Add((c, r));
            result[path] = new DesktopIconGridPosition { MonitorId = targetMonitorId, Col = c, Row = r };
        }
        return result;
    }
}
