using System.Collections.Specialized;
using System.IO;
using System.Windows;
using DeskBox.Models;
using Clipboard = System.Windows.Clipboard;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;

namespace DeskBox.Services;

/// <summary>
/// 统一处理格子与自绘桌面画布的剪贴板操作（Ctrl+C / Ctrl+X / Ctrl+V）、副本命名、重命名与新建项。
/// 完全兼容 Windows 资源管理器标准剪贴板格式（FileDrop + Preferred DropEffect）。
/// </summary>
public static class DesktopClipboardService
{
    private const string PreferredDropEffectFormat = "Preferred DropEffect";
    private const int DropEffectCopy = 1;
    private const int DropEffectMove = 2;

    private static readonly HashSet<string> ActiveCutPaths = new(StringComparer.OrdinalIgnoreCase);

    public static event Action? CutStateChanged;

    public static bool IsPathCut(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        var norm = ShellService.TryNormalizePhysicalPath(path) ?? path;
        return ActiveCutPaths.Contains(norm);
    }

    public static void ClearCutState()
    {
        if (ActiveCutPaths.Count == 0)
            return;
        ActiveCutPaths.Clear();
        CutStateChanged?.Invoke();
    }

    public static int CopyPathsToClipboard(IEnumerable<string> paths, bool isCut)
    {
        var valid = paths
            .Select(ShellService.TryNormalizePhysicalPath)
            .Where(p => p != null && (File.Exists(p) || Directory.Exists(p)))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (valid.Count == 0)
            return 0;

        var sc = new StringCollection();
        sc.AddRange(valid.ToArray());

        var data = new DataObject();
        data.SetFileDropList(sc);

        var effectBytes = BitConverter.GetBytes(isCut ? DropEffectMove : DropEffectCopy);
        data.SetData(PreferredDropEffectFormat, new MemoryStream(effectBytes));

        try
        {
            Clipboard.SetDataObject(data, copy: true);
        }
        catch
        {
            return 0;
        }

        ActiveCutPaths.Clear();
        if (isCut)
        {
            foreach (var p in valid)
                ActiveCutPaths.Add(p);
        }
        CutStateChanged?.Invoke();

        return valid.Count;
    }

    public static bool TryReadClipboardFiles(out List<string> paths, out bool isMove)
    {
        paths = new List<string>();
        isMove = false;

        try
        {
            if (!Clipboard.ContainsFileDropList())
                return false;

            var list = Clipboard.GetFileDropList();
            if (list == null || list.Count == 0)
                return false;

            foreach (var raw in list)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;
                var norm = ShellService.TryNormalizePhysicalPath(raw);
                if (norm != null && (File.Exists(norm) || Directory.Exists(norm)))
                    paths.Add(norm);
            }

            if (paths.Count == 0)
                return false;

            var obj = Clipboard.GetDataObject();
            if (obj != null && obj.GetDataPresent(PreferredDropEffectFormat))
            {
                if (obj.GetData(PreferredDropEffectFormat) is MemoryStream ms)
                {
                    var bytes = ms.ToArray();
                    if (bytes.Length >= 4)
                    {
                        int effect = BitConverter.ToInt32(bytes, 0);
                        isMove = (effect & DropEffectMove) != 0 && (effect & DropEffectCopy) == 0;
                    }
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 生成中文资源管理器风格的唯一副本路径（如 "模板文档 - 副本.docx"、"模板文档 - 副本 (2).docx"）。
    /// </summary>
    public static string GetUniqueCopyPath(string targetDirectory, string sourceName, bool isDirectory)
    {
        var direct = Path.Combine(targetDirectory, sourceName);
        if (!File.Exists(direct) && !Directory.Exists(direct))
            return direct;

        var stem = isDirectory ? sourceName : Path.GetFileNameWithoutExtension(sourceName);
        var ext = isDirectory ? string.Empty : Path.GetExtension(sourceName);

        var firstCopy = Path.Combine(targetDirectory, $"{stem} - 副本{ext}");
        if (!File.Exists(firstCopy) && !Directory.Exists(firstCopy))
            return firstCopy;

        for (int i = 2; i < 1000; i++)
        {
            var next = Path.Combine(targetDirectory, $"{stem} - 副本 ({i}){ext}");
            if (!File.Exists(next) && !Directory.Exists(next))
                return next;
        }

        return Path.Combine(targetDirectory, $"{stem} - 副本 ({Guid.NewGuid().ToString("N")[..4]}){ext}");
    }

    public sealed record PasteResult(List<string> PastedPaths, List<string> MovedSourcePaths, bool WasCut);

    /// <summary>
    /// 将一组源路径粘贴（复制或移动）到目标目录。
    /// 若 isMove 且源路径已在目标目录，不重复搬移物理文件，直接返回原路径供调用方调整格子归属。
    /// </summary>
    public static PasteResult PasteFilesToDirectory(
        IReadOnlyList<string> sourcePaths,
        string targetDirectory,
        bool isMove,
        IntPtr ownerHwnd = default)
    {
        var pasted = new List<string>();
        var movedSources = new List<string>();
        if (sourcePaths.Count == 0 || string.IsNullOrWhiteSpace(targetDirectory))
            return new PasteResult(pasted, movedSources, isMove);

        var normTarget = ShellService.TryNormalizePhysicalPath(targetDirectory);
        if (normTarget == null)
            return new PasteResult(pasted, movedSources, isMove);

        Directory.CreateDirectory(normTarget);

        foreach (var raw in sourcePaths)
        {
            var src = ShellService.TryNormalizePhysicalPath(raw);
            if (src == null)
                continue;

            bool isDir = Directory.Exists(src);
            bool isFile = !isDir && File.Exists(src);
            if (!isDir && !isFile)
                continue;

            var srcParent = ShellService.TryNormalizePhysicalPath(Path.GetDirectoryName(src));
            bool sameDir = string.Equals(srcParent, normTarget, StringComparison.OrdinalIgnoreCase);

            // 防止将目录粘贴进自身或子目录
            if (isDir)
            {
                var srcPrefix = src.EndsWith(Path.DirectorySeparatorChar) ? src : src + Path.DirectorySeparatorChar;
                if (string.Equals(src, normTarget, StringComparison.OrdinalIgnoreCase) ||
                    normTarget.StartsWith(srcPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            if (isMove && sameDir)
            {
                pasted.Add(src);
                movedSources.Add(src);
                continue;
            }

            var name = Path.GetFileName(src);
            var dest = GetUniqueCopyPath(normTarget, name, isDir);

            try
            {
                if (isMove)
                {
                    var srcRoot = Path.GetPathRoot(src);
                    var dstRoot = Path.GetPathRoot(dest);
                    if (string.Equals(srcRoot, dstRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        if (isDir) Directory.Move(src, dest);
                        else File.Move(src, dest);
                        pasted.Add(dest);
                        movedSources.Add(src);
                    }
                    else if (ShellService.MoveItemViaShell(src, dest, ownerHwnd))
                    {
                        pasted.Add(dest);
                        movedSources.Add(src);
                    }
                }
                else
                {
                    if (isDir)
                    {
                        CopyDirectoryRecursive(src, dest);
                    }
                    else
                    {
                        File.Copy(src, dest, overwrite: false);
                        // 确保复制出来的新文件不带意外的 Hidden 属性
                        var attrs = File.GetAttributes(dest);
                        if ((attrs & FileAttributes.Hidden) != 0)
                            File.SetAttributes(dest, attrs & ~FileAttributes.Hidden);
                    }
                    pasted.Add(dest);
                }
            }
            catch
            {
            }
        }

        if (isMove && movedSources.Count > 0)
        {
            ClearCutState();
        }

        return new PasteResult(pasted, movedSources, isMove);
    }

    public static void CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: false);
        }
        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            var destSub = Path.Combine(destDir, Path.GetFileName(subDir));
            CopyDirectoryRecursive(subDir, destSub);
        }
    }

    public static string? CreateNewFolder(string targetDirectory, string baseName = "新建文件夹")
    {
        try
        {
            Directory.CreateDirectory(targetDirectory);
            var path = BoxStorageService.GetUniqueDestinationPath(targetDirectory, baseName, isDirectory: true);
            Directory.CreateDirectory(path);
            return path;
        }
        catch
        {
            return null;
        }
    }

    public static string? CreateNewTextFile(string targetDirectory, string baseName = "新建文本文档.txt")
    {
        try
        {
            Directory.CreateDirectory(targetDirectory);
            var path = BoxStorageService.GetUniqueDestinationPath(targetDirectory, baseName, isDirectory: false);
            File.WriteAllText(path, string.Empty);
            return path;
        }
        catch
        {
            return null;
        }
    }

    public static (string EditableName, int SelectionLength) GetEditableRenameState(string path)
    {
        var fileName = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (string.IsNullOrWhiteSpace(fileName))
            return (string.Empty, 0);

        bool isDir = Directory.Exists(path);
        if (isDir)
            return (fileName, fileName.Length);

        var ext = Path.GetExtension(fileName);
        if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
        {
            var stem = Path.GetFileNameWithoutExtension(fileName);
            return (stem, stem.Length);
        }

        if (!string.IsNullOrEmpty(ext))
        {
            return (fileName, Math.Max(0, fileName.Length - ext.Length));
        }

        return (fileName, fileName.Length);
    }

    public static string? TryRenameFileSystemEntry(string sourcePath, string newName)
    {
        var src = ShellService.TryNormalizePhysicalPath(sourcePath);
        if (src == null || string.IsNullOrWhiteSpace(newName))
            return null;

        var trimmed = newName.Trim();
        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;

        var parent = Path.GetDirectoryName(src);
        if (string.IsNullOrWhiteSpace(parent))
            return null;

        bool isDir = Directory.Exists(src);
        bool isFile = !isDir && File.Exists(src);
        if (!isDir && !isFile)
            return null;

        // 若是快捷方式且用户未输入 .lnk 后缀，自动保留 .lnk
        var origExt = Path.GetExtension(src);
        if (isFile &&
            (origExt.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || origExt.Equals(".url", StringComparison.OrdinalIgnoreCase)) &&
            !trimmed.EndsWith(origExt, StringComparison.OrdinalIgnoreCase))
        {
            trimmed += origExt;
        }

        var dest = Path.Combine(parent, trimmed);
        if (string.Equals(src, dest, StringComparison.OrdinalIgnoreCase))
            return src;

        if (File.Exists(dest) || Directory.Exists(dest))
            return null;

        try
        {
            if (isDir) Directory.Move(src, dest);
            else File.Move(src, dest);
            return dest;
        }
        catch
        {
            return null;
        }
    }
}
