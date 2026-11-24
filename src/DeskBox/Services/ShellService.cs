using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeskBox.Interop;
using DeskBox.Models;

namespace DeskBox.Services;

public sealed class ShellService
{
    private const int MaxCachedIcons = 96;
    private readonly ConcurrentDictionary<string, ImageSource> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _cacheOrder = new();
    private readonly object _cacheLock = new();

    public ImageSource? GetIcon(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (_iconCache.TryGetValue(path, out var cached))
            return cached;

        ImageSource? created = ExtractIconCore(path);
        if (created == null)
            return null;

        created.Freeze();
        lock (_cacheLock)
        {
            if (!_iconCache.ContainsKey(path))
            {
                if (_cacheOrder.Count >= MaxCachedIcons)
                {
                    var oldest = _cacheOrder.Dequeue();
                    _iconCache.TryRemove(oldest, out _);
                }
                _iconCache[path] = created;
                _cacheOrder.Enqueue(path);
            }
        }
        return created;
    }

    public void ClearIconCache()
    {
        lock (_cacheLock)
        {
            _iconCache.Clear();
            _cacheOrder.Clear();
        }
    }

    private static ImageSource? ExtractIconCore(string path)
    {
        IntPtr pidl = IntPtr.Zero;
        IntPtr hIcon = IntPtr.Zero;
        try
        {
            var iconPath = path.Contains("26EE0668-A00A-44D7-9371-BEB064C986EE", StringComparison.OrdinalIgnoreCase)
                ? System.IO.Path.Combine(Environment.SystemDirectory, "control.exe")
                : path;

            var shfi = new NativeMethods.SHFILEINFO();
            int iconIndex = -1;

            if (iconPath.StartsWith("shell:::", StringComparison.OrdinalIgnoreCase))
            {
                if (NativeMethods.SHParseDisplayName(iconPath, IntPtr.Zero, out pidl, 0, out _) == 0 && pidl != IntPtr.Zero)
                {
                    NativeMethods.SHGetFileInfo(
                        pidl,
                        0,
                        ref shfi,
                        (uint)Marshal.SizeOf(shfi),
                        NativeMethods.SHGFI_PIDL | NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON);
                    hIcon = shfi.hIcon;
                }
            }
            else
            {
                var attrs = Directory.Exists(iconPath) ? 0x10u : 0x80u;
                var flags = NativeMethods.SHGFI_SYSICONINDEX | (File.Exists(iconPath) || Directory.Exists(iconPath) ? 0 : NativeMethods.SHGFI_USEFILEATTRIBUTES);
                var res = NativeMethods.SHGetFileInfo(iconPath, attrs, ref shfi, (uint)Marshal.SizeOf(shfi), flags);
                if (res != IntPtr.Zero && shfi.iIcon > 0)
                    iconIndex = shfi.iIcon;
            }

            if (hIcon == IntPtr.Zero && iconIndex > 0)
            {
                var iid = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");
                if (NativeMethods.SHGetImageList(NativeMethods.SHIL_EXTRALARGE, ref iid, out var imgList) == 0 && imgList != null)
                {
                    imgList.GetIcon(iconIndex, 1 /* ILD_TRANSPARENT */, out hIcon);
                    Marshal.ReleaseComObject(imgList);
                }
            }

            if (hIcon == IntPtr.Zero)
            {
                if (pidl != IntPtr.Zero)
                {
                    NativeMethods.SHGetFileInfo(pidl, 0, ref shfi, (uint)Marshal.SizeOf(shfi), NativeMethods.SHGFI_PIDL | NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON);
                }
                else
                {
                    NativeMethods.SHGetFileInfo(iconPath, 0, ref shfi, (uint)Marshal.SizeOf(shfi), NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON);
                }
                hIcon = shfi.hIcon;
            }

            if (hIcon == IntPtr.Zero)
                return null;

            return Imaging.CreateBitmapSourceFromHIcon(
                hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(36, 36));
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hIcon != IntPtr.Zero)
                NativeMethods.DestroyIcon(hIcon);
            if (pidl != IntPtr.Zero)
                NativeMethods.CoTaskMemFree(pidl);
        }
    }

    public static void OpenItem(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (path.StartsWith("shell:::", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
                return;
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"无法打开 \"{path}\"：\n{ex.Message}", "DeskBox", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public static void OpenContainingFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("shell:::", StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
        }
        catch
        {
        }
    }

    public static IEnumerable<BoxItemEntry> SortItems(IEnumerable<BoxItemEntry> items, BoxSortField field, bool descending, IReadOnlyList<string>? customOrder = null)
    {
        var list = items.ToList();
        if (field == BoxSortField.Custom)
        {
            if (customOrder == null || customOrder.Count == 0)
                return list;

            var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < customOrder.Count; i++)
            {
                rank.TryAdd(customOrder[i], i);
            }

            return list.OrderBy(item => rank.TryGetValue(GetItemOrderKey(item.Path), out var idx) ? idx : int.MaxValue);
        }

        Func<BoxItemEntry, object> keySelector = field switch
        {
            BoxSortField.Name => item => item.DisplayName,
            BoxSortField.ModifiedTime => item => SafeGetLastWriteTime(item.Path),
            BoxSortField.Type => item => Directory.Exists(item.Path) ? "" : Path.GetExtension(item.Path).ToLowerInvariant(),
            BoxSortField.Size => item => SafeGetFileSize(item.Path),
            _ => item => item.DisplayName
        };

        return descending
            ? list.OrderByDescending(keySelector)
            : list.OrderBy(keySelector);
    }

    public static string GetItemOrderKey(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;
        if (path.StartsWith("shell:::", StringComparison.OrdinalIgnoreCase))
            return path;
        return Path.GetFileName(path.TrimEnd('\\', '/'));
    }

    public static List<string> ReorderCustomOrder(
        IReadOnlyList<string> currentVisibleKeys,
        IReadOnlyList<string> movingKeys,
        int insertIndex)
    {
        var movingSet = new HashSet<string>(movingKeys, StringComparer.OrdinalIgnoreCase);
        int clampedInsert = Math.Clamp(insertIndex, 0, currentVisibleKeys.Count);

        int remainingBeforeInsert = 0;
        for (int i = 0; i < clampedInsert; i++)
        {
            if (!movingSet.Contains(currentVisibleKeys[i]))
                remainingBeforeInsert++;
        }

        var remaining = currentVisibleKeys.Where(k => !movingSet.Contains(k)).ToList();
        var orderedMoving = currentVisibleKeys.Where(k => movingSet.Contains(k)).ToList();
        // 若存在外部新插入尚未在 currentVisibleKeys 里的 key，一并接上
        foreach (var mk in movingKeys)
        {
            if (!orderedMoving.Contains(mk, StringComparer.OrdinalIgnoreCase))
                orderedMoving.Add(mk);
        }

        remaining.InsertRange(Math.Clamp(remainingBeforeInsert, 0, remaining.Count), orderedMoving);
        return remaining;
    }

    private static DateTime SafeGetLastWriteTime(string path)
    {
        try
        {
            if (File.Exists(path)) return File.GetLastWriteTimeUtc(path);
            if (Directory.Exists(path)) return Directory.GetLastWriteTimeUtc(path);
        }
        catch { }
        return DateTime.MinValue;
    }

    private static long SafeGetFileSize(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
        }
        catch { }
        return 0;
    }

    /// <summary>
    /// 通过 Windows Shell 删除文件或文件夹：默认直接移入回收站（可撤销），按 Shift 时弹出永久删除确认。
    /// </summary>
    public static bool DeleteItems(IEnumerable<string> paths, bool permanent = false, IntPtr ownerHwnd = default)
    {
        var validPaths = paths
            .Where(p => !string.IsNullOrWhiteSpace(p) && (File.Exists(p) || Directory.Exists(p)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (validPaths.Count == 0)
            return false;

        string multiNull = string.Join('\0', validPaths) + "\0\0";
        IntPtr pFrom = Marshal.StringToHGlobalUni(multiNull);
        try
        {
            var op = new NativeMethods.SHFILEOPSTRUCT64
            {
                hwnd = ownerHwnd,
                wFunc = NativeMethods.FO_DELETE,
                pFrom = pFrom,
                pTo = IntPtr.Zero,
                fFlags = permanent
                    ? NativeMethods.FOF_WANTNUKEWARNING
                    : (ushort)(NativeMethods.FOF_ALLOWUNDO | NativeMethods.FOF_NOCONFIRMATION)
            };
            int res = NativeMethods.SHFileOperation64(ref op);
            return res == 0 && !op.fAnyOperationsAborted;
        }
        finally
        {
            Marshal.FreeHGlobal(pFrom);
        }
    }

    public static string? TryNormalizePhysicalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("shell:::", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            if (!string.IsNullOrEmpty(root) && string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
                return full;
            return full.TrimEnd('\\', '/');
        }
        catch
        {
            return null;
        }
    }

    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".bat", ".cmd", ".com"
    };

    public static bool TryResolveShortcut(string lnkPath, out string targetPath, out string arguments, out string workingDirectory)
    {
        targetPath = string.Empty;
        arguments = string.Empty;
        workingDirectory = string.Empty;

        if (string.IsNullOrWhiteSpace(lnkPath) || !File.Exists(lnkPath))
            return false;

        if (!string.Equals(Path.GetExtension(lnkPath), ".lnk", StringComparison.OrdinalIgnoreCase))
            return false;

        IShellLinkW? link = null;
        try
        {
            link = (IShellLinkW)new ShellLink();
            var persist = (IPersistFile)link;
            persist.Load(lnkPath, 0 /* STGM_READ */);

            var sbPath = new StringBuilder(1024);
            link.GetPath(sbPath, sbPath.Capacity, IntPtr.Zero, 0);
            targetPath = sbPath.ToString().Trim();

            if (string.IsNullOrEmpty(targetPath))
            {
                try
                {
                    link.GetIDList(out var pidl);
                    if (pidl != IntPtr.Zero)
                    {
                        try
                        {
                            targetPath = GetNameFromPidl(pidl, NativeMethods.SIGDN_DESKTOPABSOLUTEPARSING) ?? string.Empty;
                        }
                        finally
                        {
                            NativeMethods.ILFree(pidl);
                        }
                    }
                }
                catch
                {
                }
            }

            var sbArgs = new StringBuilder(1024);
            link.GetArguments(sbArgs, sbArgs.Capacity);
            arguments = sbArgs.ToString().Trim();

            var sbWorkDir = new StringBuilder(1024);
            link.GetWorkingDirectory(sbWorkDir, sbWorkDir.Capacity);
            workingDirectory = sbWorkDir.ToString().Trim();

            return !string.IsNullOrWhiteSpace(targetPath);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (link != null)
            {
                try { Marshal.FinalReleaseComObject(link); } catch { }
            }
        }
    }

    public static string? TryResolveTargetFolder(string? itemPath)
    {
        var normalized = TryNormalizePhysicalPath(itemPath);
        if (normalized == null)
            return null;

        if (Directory.Exists(normalized))
            return normalized;

        if (File.Exists(normalized) &&
            string.Equals(Path.GetExtension(normalized), ".lnk", StringComparison.OrdinalIgnoreCase) &&
            TryResolveShortcut(normalized, out var targetPath, out _, out _))
        {
            var resolved = TryNormalizePhysicalPath(targetPath);
            if (resolved != null && Directory.Exists(resolved))
                return resolved;
        }

        return null;
    }

    public static bool CanMoveOrCopyPathIntoFolder(string? sourcePath, string? targetFolderPath)
    {
        var source = TryNormalizePhysicalPath(sourcePath);
        var rawTarget = TryNormalizePhysicalPath(targetFolderPath);
        var targetFolder = TryResolveTargetFolder(targetFolderPath);
        if (source == null || targetFolder == null)
            return false;

        if (!File.Exists(source) && !Directory.Exists(source))
            return false;

        if (string.IsNullOrEmpty(Path.GetFileName(source)))
            return false;

        // 禁止文件夹（或文件夹快捷方式）拖入自身
        if ((rawTarget != null && string.Equals(source, rawTarget, StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(source, targetFolder, StringComparison.OrdinalIgnoreCase))
            return false;

        // 禁止将已在目标文件夹直接子级下的同名项重复移入自身所在目录
        var sourceParent = TryNormalizePhysicalPath(Path.GetDirectoryName(source));
        if (string.Equals(sourceParent, targetFolder, StringComparison.OrdinalIgnoreCase))
            return false;

        // 禁止将父文件夹拖入其子孙目录
        var sourcePrefix = source.EndsWith(Path.DirectorySeparatorChar)
            ? source
            : source + Path.DirectorySeparatorChar;
        if (targetFolder.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    public static bool CanDropIntoFolder(IReadOnlyList<string> draggedPaths, string? targetFolderPath)
    {
        var rawTarget = TryNormalizePhysicalPath(targetFolderPath);
        var targetFolder = TryResolveTargetFolder(targetFolderPath);
        if (targetFolder == null || draggedPaths.Count == 0)
            return false;

        bool hasValidSource = false;
        foreach (var rawPath in draggedPaths)
        {
            var normalized = TryNormalizePhysicalPath(rawPath);
            if (normalized != null)
            {
                if ((rawTarget != null && string.Equals(normalized, rawTarget, StringComparison.OrdinalIgnoreCase)) ||
                    string.Equals(normalized, targetFolder, StringComparison.OrdinalIgnoreCase))
                    return false;

                var prefix = normalized.EndsWith(Path.DirectorySeparatorChar)
                    ? normalized
                    : normalized + Path.DirectorySeparatorChar;
                if (targetFolder.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            if (CanMoveOrCopyPathIntoFolder(rawPath, targetFolder))
                hasValidSource = true;
        }

        return hasValidSource;
    }

    public static bool MoveOrCopyItemToFolder(string sourcePath, string targetFolderPath, bool copy = false, IntPtr ownerHwnd = default)
    {
        var source = TryNormalizePhysicalPath(sourcePath);
        var targetFolder = TryResolveTargetFolder(targetFolderPath);
        if (source == null || targetFolder == null || !CanMoveOrCopyPathIntoFolder(source, targetFolder))
            return false;

        var fileName = Path.GetFileName(source);
        if (string.IsNullOrEmpty(fileName))
            return false;

        var destPath = Path.Combine(targetFolder, fileName);
        bool destExists = File.Exists(destPath) || Directory.Exists(destPath);

        if (!destExists)
        {
            try
            {
                if (!copy)
                {
                    if (File.Exists(source))
                    {
                        File.Move(source, destPath);
                        return true;
                    }

                    if (Directory.Exists(source))
                    {
                        var srcRoot = Path.GetPathRoot(source);
                        var dstRoot = Path.GetPathRoot(targetFolder);
                        if (string.Equals(srcRoot, dstRoot, StringComparison.OrdinalIgnoreCase))
                        {
                            Directory.Move(source, destPath);
                            return true;
                        }
                    }
                }
                else if (File.Exists(source))
                {
                    File.Copy(source, destPath, overwrite: false);
                    return true;
                }
            }
            catch
            {
                // 跨卷目录、权限或占用冲突时回退到 Shell 原生操作
            }
        }

        IntPtr pFrom = Marshal.StringToHGlobalUni(source + "\0\0");
        IntPtr pTo = Marshal.StringToHGlobalUni(targetFolder + "\0\0");
        try
        {
            ushort flags = NativeMethods.FOF_ALLOWUNDO;
            if (!destExists)
                flags |= NativeMethods.FOF_NOCONFIRMATION;

            var op = new NativeMethods.SHFILEOPSTRUCT64
            {
                hwnd = ownerHwnd,
                wFunc = copy ? NativeMethods.FO_COPY : NativeMethods.FO_MOVE,
                pFrom = pFrom,
                pTo = pTo,
                fFlags = flags
            };
            return NativeMethods.SHFileOperation64(ref op) == 0 && !op.fAnyOperationsAborted;
        }
        finally
        {
            Marshal.FreeHGlobal(pFrom);
            Marshal.FreeHGlobal(pTo);
        }
    }

    public static bool IsApplicationDropTarget(string? itemPath)
    {
        var normalized = TryNormalizePhysicalPath(itemPath);
        if (normalized == null || !File.Exists(normalized) || Directory.Exists(normalized))
            return false;

        var ext = Path.GetExtension(normalized);
        if (ExecutableExtensions.Contains(ext))
            return true;

        if (string.Equals(ext, ".lnk", StringComparison.OrdinalIgnoreCase))
        {
            // 指向文件夹的 .lnk 走文件夹投放逻辑，不视为应用图标
            return TryResolveTargetFolder(normalized) == null;
        }

        return false;
    }

    public static bool CanDropOntoApplication(IReadOnlyList<string> draggedPaths, string? targetAppItemPath)
    {
        if (!IsApplicationDropTarget(targetAppItemPath) || draggedPaths.Count == 0)
            return false;

        var normalizedTarget = TryNormalizePhysicalPath(targetAppItemPath);
        if (normalizedTarget == null)
            return false;

        string? resolvedTargetExe = null;
        if (string.Equals(Path.GetExtension(normalizedTarget), ".lnk", StringComparison.OrdinalIgnoreCase) &&
            TryResolveShortcut(normalizedTarget, out var shortcutTarget, out _, out _))
        {
            resolvedTargetExe = TryNormalizePhysicalPath(shortcutTarget);
        }

        bool hasValidFile = false;
        foreach (var rawPath in draggedPaths)
        {
            var normalized = TryNormalizePhysicalPath(rawPath);
            if (normalized == null)
                continue;

            if (string.Equals(normalized, normalizedTarget, StringComparison.OrdinalIgnoreCase))
                return false;

            if (resolvedTargetExe != null &&
                string.Equals(normalized, resolvedTargetExe, StringComparison.OrdinalIgnoreCase))
                return false;

            if (File.Exists(normalized) || Directory.Exists(normalized))
                hasValidFile = true;
        }

        return hasValidFile;
    }

    public static bool OpenFilesWithApplication(string appItemPath, IEnumerable<string> filePaths)
    {
        var normalizedApp = TryNormalizePhysicalPath(appItemPath);
        if (normalizedApp == null || !IsApplicationDropTarget(normalizedApp))
            return false;

        var validFiles = filePaths
            .Select(TryNormalizePhysicalPath)
            .Where(p => p != null && (File.Exists(p) || Directory.Exists(p)))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (validFiles.Count == 0)
            return false;

        var quotedFiles = string.Join(" ", validFiles.Select(p => p.EndsWith('\\') ? $"\"{p}\\\"" : $"\"{p}\""));

        try
        {
            if (string.Equals(Path.GetExtension(normalizedApp), ".lnk", StringComparison.OrdinalIgnoreCase) &&
                TryResolveShortcut(normalizedApp, out var targetExe, out var lnkArgs, out var workDir) &&
                File.Exists(targetExe))
            {
                var combinedArgs = string.IsNullOrWhiteSpace(lnkArgs)
                    ? quotedFiles
                    : $"{lnkArgs} {quotedFiles}";
                var effectiveWorkDir = !string.IsNullOrWhiteSpace(workDir) && Directory.Exists(workDir)
                    ? workDir
                    : Path.GetDirectoryName(targetExe) ?? string.Empty;

                Process.Start(new ProcessStartInfo(targetExe)
                {
                    Arguments = combinedArgs,
                    WorkingDirectory = effectiveWorkDir,
                    UseShellExecute = true
                });
                return true;
            }

            Process.Start(new ProcessStartInfo(normalizedApp)
            {
                Arguments = quotedFiles,
                WorkingDirectory = Path.GetDirectoryName(normalizedApp) ?? string.Empty,
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"无法使用 \"{Path.GetFileName(normalizedApp)}\" 打开所选项目：\n{ex.Message}", "DeskBox", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    public static bool MoveItemViaShell(string sourcePath, string destPath, IntPtr ownerHwnd = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(destPath))
            return false;

        IntPtr pFrom = Marshal.StringToHGlobalUni(sourcePath + "\0\0");
        IntPtr pTo = Marshal.StringToHGlobalUni(destPath + "\0\0");
        try
        {
            var op = new NativeMethods.SHFILEOPSTRUCT64
            {
                hwnd = ownerHwnd,
                wFunc = NativeMethods.FO_MOVE,
                pFrom = pFrom,
                pTo = pTo,
                fFlags = NativeMethods.FOF_NOCONFIRMATION
            };
            return NativeMethods.SHFileOperation64(ref op) == 0 && !op.fAnyOperationsAborted;
        }
        finally
        {
            Marshal.FreeHGlobal(pFrom);
            Marshal.FreeHGlobal(pTo);
        }
    }

    /// <summary>
    /// 解析 Windows 桌面拖拽虚拟对象（回收站 / 此电脑 / 控制面板 / 网络等）传入的 "Shell IDList Array" (CIDA) 数据。
    /// </summary>
    public static List<(string path, string? displayName)> ParseShellIdListArray(Stream cidaStream)
    {
        var results = new List<(string path, string? displayName)>();
        byte[] buffer;
        using (var ms = new MemoryStream())
        {
            if (cidaStream.CanSeek)
                cidaStream.Position = 0;
            cidaStream.CopyTo(ms);
            if (cidaStream.CanSeek)
                cidaStream.Position = 0;
            buffer = ms.ToArray();
        }

        if (buffer.Length < 8)
            return results;

        int cidl = BitConverter.ToInt32(buffer, 0);
        if (cidl <= 0 || buffer.Length < 4 + (cidl + 1) * 4)
            return results;

        var pinned = Marshal.AllocHGlobal(buffer.Length);
        try
        {
            Marshal.Copy(buffer, 0, pinned, buffer.Length);
            int parentOffset = BitConverter.ToInt32(buffer, 4);
            if (parentOffset < 0 || parentOffset >= buffer.Length)
                return results;

            IntPtr parentPidl = IntPtr.Add(pinned, parentOffset);

            for (int i = 1; i <= cidl; i++)
            {
                int childOffset = BitConverter.ToInt32(buffer, 4 + i * 4);
                if (childOffset < 0 || childOffset >= buffer.Length)
                    continue;

                IntPtr childPidl = IntPtr.Add(pinned, childOffset);
                IntPtr fullPidl = NativeMethods.ILCombine(parentPidl, childPidl);
                if (fullPidl == IntPtr.Zero)
                    continue;

                try
                {
                    string? parsing = GetNameFromPidl(fullPidl, NativeMethods.SIGDN_DESKTOPABSOLUTEPARSING);
                    string? display = GetNameFromPidl(fullPidl, NativeMethods.SIGDN_NORMALDISPLAY);

                    if (!string.IsNullOrWhiteSpace(parsing))
                    {
                        var normalized = parsing.StartsWith("::{", StringComparison.Ordinal)
                            ? "shell:" + parsing
                            : parsing;
                        results.Add((normalized, display));
                    }
                }
                finally
                {
                    NativeMethods.ILFree(fullPidl);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pinned);
        }

        return results;
    }

    private static string? GetNameFromPidl(IntPtr pidl, uint sigdn)
    {
        if (NativeMethods.SHGetNameFromIDList(pidl, sigdn, out var ptr) == 0 && ptr != IntPtr.Zero)
        {
            try
            {
                return Marshal.PtrToStringUni(ptr);
            }
            finally
            {
                NativeMethods.CoTaskMemFree(ptr);
            }
        }
        return null;
    }

    public static string? ResolveHideDesktopIconGuid(string shellPath)
    {
        if (string.IsNullOrWhiteSpace(shellPath))
            return null;

        // 提取 {XXXXXXXX-...}
        int start = shellPath.IndexOf('{');
        int end = shellPath.IndexOf('}');
        if (start < 0 || end <= start)
            return null;

        var guid = shellPath.Substring(start, end - start + 1);
        // 控制面板的两个常用 CLSID 映射到 HideDesktopIcons\NewStartPanel 中的标准键
        if (guid.Equals("{26EE0668-A00A-44D7-9371-BEB064C986EE}", StringComparison.OrdinalIgnoreCase))
            return "{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}";

        return guid;
    }

    public static bool SetSystemDesktopIconHidden(string hideGuid, bool hidden, DesktopHostService hostService, bool refreshImmediately = true)
    {
        if (string.IsNullOrWhiteSpace(hideGuid))
            return false;

        try
        {
            const string regPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel";
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(regPath, writable: true);
            key.SetValue(hideGuid, hidden ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
            if (refreshImmediately)
                hostService.RefreshDesktopShellView();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsSystemDesktopIconCurrentlyHidden(string hideGuid)
    {
        try
        {
            const string regPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel";
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(regPath, writable: false);
            var val = key?.GetValue(hideGuid);
            return val != null && Convert.ToInt32(val) == 1;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 弹出原汁原味的 Windows Shell 文件右键菜单，并在顶部附加 DeskBox 快捷项。
    /// </summary>
    public static void ShowNativeContextMenu(
        string itemPath,
        int screenX,
        int screenY,
        Action onRemoveFromBox,
        Action? onRefreshAfterShellCommand = null,
        string customTopActionLabel = "放回桌面（移出格子）")
    {
        IntPtr pidl = IntPtr.Zero;
        IntPtr parentFolderPtr = IntPtr.Zero;
        IShellFolder? parentFolder = null;
        IntPtr ctxMenuPtr = IntPtr.Zero;
        IContextMenu? ctxMenu = null;
        IntPtr hMenu = IntPtr.Zero;

        try
        {
            if (NativeMethods.SHParseDisplayName(itemPath, IntPtr.Zero, out pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
                return;

            var iidShellFolder = new Guid("000214E6-0000-0000-C000-000000000046");
            if (NativeMethods.SHBindToParent(pidl, ref iidShellFolder, out parentFolderPtr, out var pidlChild) != 0 || parentFolderPtr == IntPtr.Zero)
                return;

            parentFolder = (IShellFolder)Marshal.GetObjectForIUnknown(parentFolderPtr);
            var iidContextMenu = new Guid("000214E4-0000-0000-C000-000000000046");
            IntPtr[] apidl = [pidlChild];

            if (parentFolder.GetUIObjectOf(IntPtr.Zero, 1, apidl, ref iidContextMenu, IntPtr.Zero, out ctxMenuPtr) != 0 || ctxMenuPtr == IntPtr.Zero)
                return;

            ctxMenu = (IContextMenu)Marshal.GetObjectForIUnknown(ctxMenuPtr);
            hMenu = NativeMethods.CreatePopupMenu();
            if (hMenu == IntPtr.Zero)
                return;

            const uint idCmdFirst = 1000;
            const uint idCmdLast = 0x7FFF;
            ctxMenu.QueryContextMenu(hMenu, 0, idCmdFirst, idCmdLast, 0x00000000 /* CMF_NORMAL */);

            const uint MF_BYPOSITION = 0x0400;
            const uint MF_SEPARATOR = 0x0800;
            const uint customRemoveId = 1;
            const uint customOpenFolderId = 2;

            NativeMethods.InsertMenu(hMenu, 0, MF_BYPOSITION, new UIntPtr(customRemoveId), customTopActionLabel);
            if (!itemPath.StartsWith("shell:::", StringComparison.OrdinalIgnoreCase))
            {
                NativeMethods.InsertMenu(hMenu, 1, MF_BYPOSITION, new UIntPtr(customOpenFolderId), "打开文件所在位置");
                NativeMethods.InsertMenu(hMenu, 2, MF_BYPOSITION | MF_SEPARATOR, UIntPtr.Zero, string.Empty);
            }
            else
            {
                NativeMethods.InsertMenu(hMenu, 1, MF_BYPOSITION | MF_SEPARATOR, UIntPtr.Zero, string.Empty);
            }

            using var host = new ContextMenuHostWindow(ctxMenu as IContextMenu2, ctxMenu as IContextMenu3);
            var selectedCmd = NativeMethods.TrackPopupMenuEx(
                hMenu,
                NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON,
                screenX,
                screenY,
                host.Handle,
                IntPtr.Zero);

            if (selectedCmd == customRemoveId)
            {
                onRemoveFromBox();
            }
            else if (selectedCmd == customOpenFolderId)
            {
                OpenContainingFolder(itemPath);
            }
            else if (selectedCmd >= idCmdFirst)
            {
                var invoke = new CMINVOKECOMMANDINFOEX
                {
                    cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                    fMask = 0x00004000 /* CMIC_MASK_UNICODE */,
                    hwnd = host.Handle,
                    lpVerb = new IntPtr(selectedCmd - idCmdFirst),
                    lpVerbW = new IntPtr(selectedCmd - idCmdFirst),
                    nShow = 1 /* SW_SHOWNORMAL */
                };
                ctxMenu.InvokeCommand(ref invoke);
                onRefreshAfterShellCommand?.Invoke();
            }
        }
        catch
        {
        }
        finally
        {
            if (hMenu != IntPtr.Zero) NativeMethods.DestroyMenu(hMenu);
            if (ctxMenu != null) Marshal.ReleaseComObject(ctxMenu);
            if (ctxMenuPtr != IntPtr.Zero) Marshal.Release(ctxMenuPtr);
            if (parentFolder != null) Marshal.ReleaseComObject(parentFolder);
            if (parentFolderPtr != IntPtr.Zero) Marshal.Release(parentFolderPtr);
            if (pidl != IntPtr.Zero) NativeMethods.CoTaskMemFree(pidl);
        }
    }

    private sealed class ContextMenuHostWindow : IDisposable
    {
        private readonly IContextMenu2? _cm2;
        private readonly IContextMenu3? _cm3;
        private readonly HwndSource _hwndSource;

        public IntPtr Handle => _hwndSource.Handle;

        public ContextMenuHostWindow(IContextMenu2? cm2, IContextMenu3? cm3)
        {
            _cm2 = cm2;
            _cm3 = cm3;
            _hwndSource = new HwndSource(new HwndSourceParameters("DeskBoxShellMenuHost") { Width = 0, Height = 0, WindowStyle = 0 });
            _hwndSource.AddHook(WndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (_cm3 != null && msg is NativeMethods.WM_INITMENUPOPUP or NativeMethods.WM_DRAWITEM or NativeMethods.WM_MEASUREITEM or NativeMethods.WM_MENUCHAR)
            {
                if (_cm3.HandleMenuMsg2(msg, wParam, lParam, out var result) == 0)
                {
                    handled = true;
                    return result;
                }
            }
            else if (_cm2 != null && msg is NativeMethods.WM_INITMENUPOPUP or NativeMethods.WM_DRAWITEM or NativeMethods.WM_MEASUREITEM)
            {
                if (_cm2.HandleMenuMsg(msg, wParam, lParam) == 0)
                {
                    handled = true;
                    return IntPtr.Zero;
                }
            }

            return IntPtr.Zero;
        }

        public void Dispose()
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        [MarshalAs(UnmanagedType.LPStr)]
        public string? lpParameters;
        [MarshalAs(UnmanagedType.LPStr)]
        public string? lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.LPStr)]
        public string? lpTitle;
        public IntPtr lpVerbW;
        public string? lpParametersW;
        public string? lpDirectoryW;
        public string? lpTitleW;
        public NativeMethods.POINT ptInvoke;
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214E6-0000-0000-C000-000000000046")]
    private interface IShellFolder
    {
        int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName, ref uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
        int EnumObjects(IntPtr hwnd, uint grfFlags, out IntPtr ppenumIDList);
        int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
        int GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);
        int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);
        int GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);
        int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214E4-0000-0000-C000-000000000046")]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F4-0000-0000-C000-000000000046")]
    private interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
        [PreserveSig] int HandleMenuMsg(int uMsg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719")]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
        [PreserveSig] int HandleMenuMsg(int uMsg, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(int uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
