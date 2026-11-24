using System.IO;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// 已收纳路径集合统计与旧版 Hidden 属性遗留清理器。
/// 新架构采用自绘透明桌面画布接管桌面渲染，绝不再对任何用户文件/文件夹写入 FileAttributes.Hidden。
/// </summary>
public sealed class DesktopIconFilterService
{
    private readonly DesktopHostService _hostService;
    private readonly BoxStorageService _storageService;
    private readonly bool _enableShellRefresh;

    public DesktopIconFilterService(
        DesktopHostService hostService,
        BoxStorageService? storageService = null,
        bool enableShellRefresh = true)
    {
        _hostService = hostService;
        _storageService = storageService ?? new BoxStorageService();
        _enableShellRefresh = enableShellRefresh;
    }

    public bool NeedsResync(AppConfig config) => config.HiddenDesktopPaths.Count > 0;

    /// <summary>
    /// 兼容旧调用点：永不再写入 Hidden 属性；若检测到旧版遗留的 HiddenDesktopPaths，自动清理恢复可见。
    /// </summary>
    public bool SyncHiddenDesktopIcons(AppConfig config, bool forceRefresh = false)
    {
        if (config.HiddenDesktopPaths.Count > 0)
            return RestoreAllHiddenDesktopIcons(config, forceRefresh);

        if (forceRefresh && _enableShellRefresh)
            _hostService.RefreshDesktopShellView();

        return false;
    }

    public bool RestoreHiddenAttributeForPaths(AppConfig config, IEnumerable<string> paths)
    {
        bool attrChanged = false;
        bool configChanged = false;

        foreach (var raw in paths)
        {
            var normalized = TryNormalizePhysicalPath(raw);
            if (normalized == null || !config.HiddenDesktopPaths.Contains(normalized))
                continue;

            if (File.Exists(normalized) || Directory.Exists(normalized))
            {
                if (TryClearHiddenAttribute(normalized))
                    attrChanged = true;
            }

            if (config.HiddenDesktopPaths.Remove(normalized))
                configChanged = true;
        }

        if (attrChanged && _enableShellRefresh)
            _hostService.RefreshDesktopShellView();

        return configChanged;
    }

    public bool RestoreAllHiddenDesktopIcons(AppConfig config, bool forceRefresh = false)
    {
        bool attrChanged = false;
        bool configChanged = config.HiddenDesktopPaths.Count > 0;

        foreach (var path in config.HiddenDesktopPaths.ToList())
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                if (TryClearHiddenAttribute(path))
                    attrChanged = true;
            }
        }

        config.HiddenDesktopPaths.Clear();
        if ((attrChanged || forceRefresh) && _enableShellRefresh)
            _hostService.RefreshDesktopShellView();

        return configChanged;
    }

    public HashSet<string> CollectOrganizedDesktopPaths(AppConfig config)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in CollectOrganizedPaths(config))
        {
            if (path.StartsWith("shell:::", StringComparison.OrdinalIgnoreCase))
                continue;

            if (_storageService.IsInAnyDesktop(path))
                result.Add(path);
        }

        return result;
    }

    public static HashSet<string> CollectOrganizedPaths(AppConfig config)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var box in config.Boxes)
        {
            if (box.IsUnorganizedBox || !string.IsNullOrWhiteSpace(box.MappedFolderPath))
                continue;

            foreach (var item in box.Items)
            {
                if (string.IsNullOrWhiteSpace(item.Path))
                    continue;

                if (item.Path.StartsWith("shell:::", StringComparison.OrdinalIgnoreCase))
                {
                    set.Add(item.Path);
                    continue;
                }

                var normalized = TryNormalizePhysicalPath(item.Path);
                if (normalized != null)
                    set.Add(normalized);
            }
        }
        return set;
    }

    public bool CaptureVisibleDesktopIconPositions(AppConfig config) => false;

    public bool RestoreDesktopIconPositions(IReadOnlyDictionary<string, DesktopIconPoint> savedPositions) => false;

    public static Dictionary<string, DesktopIconPoint> ComputeConflictFreeRestorePositions(
        IReadOnlyDictionary<string, DesktopIconPoint> savedPositions,
        IReadOnlyList<(string Name, int CurrentX, int CurrentY)> currentDesktopItems,
        int stepX,
        int stepY)
    {
        var updates = new Dictionary<string, DesktopIconPoint>(StringComparer.OrdinalIgnoreCase);
        var targetPts = new List<DesktopIconPoint>();

        foreach (var item in currentDesktopItems)
        {
            if (savedPositions.TryGetValue(item.Name, out var target))
            {
                targetPts.Add(target);
                updates[item.Name] = target;
            }
        }

        if (targetPts.Count == 0)
            return updates;

        int safeStepX = Math.Max(140, stepX);
        int safeStepY = Math.Max(140, stepY);
        int parkIndex = 0;

        foreach (var item in currentDesktopItems)
        {
            if (savedPositions.ContainsKey(item.Name))
                continue;

            bool collides = targetPts.Any(t =>
                Math.Abs(item.CurrentX - t.X) < 60 &&
                Math.Abs(item.CurrentY - t.Y) < 60);

            if (!collides)
                continue;

            int candidateX = 36 + (parkIndex / 5) * safeStepX;
            int candidateY = 36 + (parkIndex % 5) * safeStepY;
            while (targetPts.Any(t => Math.Abs(candidateX - t.X) < 60 && Math.Abs(candidateY - t.Y) < 60))
            {
                parkIndex++;
                candidateX = 36 + (parkIndex / 5) * safeStepX;
                candidateY = 36 + (parkIndex % 5) * safeStepY;
            }
            parkIndex++;
            updates[item.Name] = new DesktopIconPoint(candidateX, candidateY);
        }

        return updates;
    }

    private static string? TryNormalizePhysicalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("shell:::", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            return Path.GetFullPath(path).TrimEnd('\\', '/');
        }
        catch
        {
            return null;
        }
    }

    private static bool TryClearHiddenAttribute(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.Hidden) == 0)
                return false;

            File.SetAttributes(path, attrs & ~FileAttributes.Hidden);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
