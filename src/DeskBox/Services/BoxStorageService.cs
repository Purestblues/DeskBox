using System.IO;
using DeskBox.Interop;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// 桌面路径识别、旧版 Storage 迁移恢复与未整理桌面项检索服务。
/// 绝不移动、隐藏或删除用户桌面上的真实物理文件/文件夹，确保资源管理器与第三方应用工作目录始终完整可用。
/// </summary>
public sealed class BoxStorageService
{
    public string StorageRoot { get; }
    public string UserDesktopDirectory { get; }
    public string PublicDesktopDirectory { get; }

    public BoxStorageService(
        string? customStorageRoot = null,
        string? customDesktopDirectory = null,
        string? customPublicDesktopDirectory = null)
    {
        StorageRoot = customStorageRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeskBox",
            "Storage");

        UserDesktopDirectory = Path.GetFullPath(
            customDesktopDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory))
            .TrimEnd('\\', '/');
        PublicDesktopDirectory = Path.GetFullPath(
            customPublicDesktopDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory))
            .TrimEnd('\\', '/');
    }

    public bool IsInUserDesktop(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("shell:::", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(path))?.TrimEnd('\\', '/');
            return string.Equals(parent, UserDesktopDirectory, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public bool IsInPublicDesktop(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("shell:::", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(path))?.TrimEnd('\\', '/');
            return string.Equals(parent, PublicDesktopDirectory, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public bool IsInAnyDesktop(string path) => IsInUserDesktop(path) || IsInPublicDesktop(path);

    public static bool IsIgnoredSystemEntry(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        return name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("thumbs.db", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 兼容恢复：若早期版本曾在 Storage\<boxId> 中存放过用户文件，全部无损移回用户桌面并在对应格子保留原路径引用。
    /// </summary>
    public bool RestoreLegacyStorageToDesktopAndConfig(AppConfig config)
    {
        if (!Directory.Exists(StorageRoot))
            return false;

        bool changed = false;
        Directory.CreateDirectory(UserDesktopDirectory);

        foreach (var box in config.Boxes)
        {
            var boxDir = Path.Combine(StorageRoot, box.Id);
            if (!Directory.Exists(boxDir))
                continue;

            var entries = Directory.EnumerateFileSystemEntries(boxDir)
                .Where(p => !IsIgnoredSystemEntry(p))
                .ToList();

            foreach (var entry in entries)
            {
                var movedToDesktop = MoveEntrySafeToDesktop(entry);
                if (movedToDesktop == null)
                    continue;

                if (!box.Items.Any(i => string.Equals(i.Path, movedToDesktop, StringComparison.OrdinalIgnoreCase)))
                {
                    box.Items.Add(new BoxItemEntry { Path = movedToDesktop });
                }
                changed = true;
            }
        }

        if (changed)
        {
            NotifyDesktopRefresh();
        }
        return changed;
    }

    /// <summary>
    /// 枚举当前“用户桌面 + 公共桌面”中尚未被任何正常格子收纳的真实文件与文件夹。
    /// </summary>
    public IReadOnlyList<BoxItemEntry> EnumerateUnorganizedDesktopEntries(AppConfig config)
    {
        var organizedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var box in config.Boxes)
        {
            if (box.IsUnorganizedBox || !string.IsNullOrWhiteSpace(box.MappedFolderPath))
                continue;

            foreach (var item in box.Items)
            {
                if (string.IsNullOrWhiteSpace(item.Path) || item.Path.StartsWith("shell:::", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    organizedPaths.Add(Path.GetFullPath(item.Path).TrimEnd('\\', '/'));
                }
                catch
                {
                }
            }
        }

        var result = new List<BoxItemEntry>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in new[] { UserDesktopDirectory, PublicDesktopDirectory })
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                continue;

            IEnumerable<string> diskEntries;
            try
            {
                diskEntries = Directory.EnumerateFileSystemEntries(root);
            }
            catch
            {
                continue;
            }

            foreach (var path in diskEntries)
            {
                if (IsIgnoredSystemEntry(path))
                    continue;

                try
                {
                    var attrs = File.GetAttributes(path);
                    if ((attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                        continue;
                }
                catch
                {
                }

                string normalized;
                try
                {
                    normalized = Path.GetFullPath(path).TrimEnd('\\', '/');
                }
                catch
                {
                    continue;
                }

                if (organizedPaths.Contains(normalized) || !seenPaths.Add(normalized))
                    continue;

                result.Add(new BoxItemEntry { Path = normalized });
            }
        }

        return result;
    }

    private string? MoveEntrySafeToDesktop(string sourcePath)
    {
        bool isDir = Directory.Exists(sourcePath);
        if (!isDir && !File.Exists(sourcePath))
            return null;

        var dest = GetUniqueDestinationPath(UserDesktopDirectory, Path.GetFileName(sourcePath), isDir);
        var srcRoot = Path.GetPathRoot(Path.GetFullPath(sourcePath));
        var dstRoot = Path.GetPathRoot(Path.GetFullPath(dest));

        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                if (string.Equals(srcRoot, dstRoot, StringComparison.OrdinalIgnoreCase))
                {
                    if (isDir) Directory.Move(sourcePath, dest);
                    else File.Move(sourcePath, dest);
                    return dest;
                }

                return ShellService.MoveItemViaShell(sourcePath, dest) ? dest : null;
            }
            catch (UnauthorizedAccessException)
            {
                return ShellService.MoveItemViaShell(sourcePath, dest) ? dest : null;
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(45);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    public static string GetUniqueDestinationPath(string folder, string fileName, bool isDirectory)
    {
        var candidate = Path.Combine(folder, fileName);
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
            return candidate;

        var stem = isDirectory ? fileName : Path.GetFileNameWithoutExtension(fileName);
        var ext = isDirectory ? string.Empty : Path.GetExtension(fileName);
        for (int i = 1; i < 1000; i++)
        {
            var next = Path.Combine(folder, $"{stem} ({i}){ext}");
            if (!File.Exists(next) && !Directory.Exists(next))
                return next;
        }

        return Path.Combine(folder, $"{stem}_{Guid.NewGuid().ToString("N")[..4]}{ext}");
    }

    public static void NotifyDesktopRefresh()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        NativeMethods.SHChangeNotify(NativeMethods.SHCNE_UPDATEDIR, NativeMethods.SHCNF_PATHW, desktop, null);
    }
}
