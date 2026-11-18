using System.IO;
using System.Runtime.InteropServices;
using DeskBox.Interop;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// 非破坏性桌面视觉同步器：对已收纳进格子的桌面文件/快捷方式按清单受控标记 FileAttributes.Hidden 并刷新桌面视图，
/// 物理文件/文件夹 100% 保留在原桌面目录（不移动、不删除、不影响资源管理器与第三方软件工作目录）。
/// 当项目移出格子、解散格子、恢复桌面、退出或卸载时，严格按 HiddenDesktopPaths 清单清除隐藏属性并刷新桌面。
/// </summary>
public sealed class DesktopIconFilterService
{
    private static readonly Guid ClsidShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid SidSTopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");

    private const int CSIDL_DESKTOP = 0;
    private const int SWC_DESKTOP = 8;
    private const int SWFO_NEEDDISPATCH = 1;
    private const uint SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;
    private const uint SVSI_POSITIONITEM = 0x00000080;

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

    public bool NeedsResync(AppConfig config)
    {
        if (config.Tidy.HideNativeDesktopIcons)
            return false;

        var organizedDesktopPaths = CollectOrganizedDesktopPaths(config);
        foreach (var tracked in config.HiddenDesktopPaths)
        {
            if (!organizedDesktopPaths.Contains(tracked))
                return true;
        }

        foreach (var path in organizedDesktopPaths)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
                continue;

            try
            {
                var attrs = File.GetAttributes(path);
                if ((attrs & FileAttributes.Hidden) == 0)
                    return true;
            }
            catch
            {
            }
        }

        return false;
    }

    /// <summary>
    /// 同步隐藏已在任意正常格子中的物理桌面路径，并恢复不再属于任何格子的受控隐藏项。
    /// </summary>
    /// <returns>HiddenDesktopPaths 清单是否发生变化。</returns>
    public bool SyncHiddenDesktopIcons(AppConfig config, bool forceRefresh = false)
    {
        if (config.Tidy.HideNativeDesktopIcons)
            return false;

        var organizedDesktopPaths = CollectOrganizedDesktopPaths(config);
        bool attrChanged = false;
        bool configChanged = false;

        foreach (var path in organizedDesktopPaths)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
                continue;

            try
            {
                var attrs = File.GetAttributes(path);
                if ((attrs & FileAttributes.Hidden) == 0)
                {
                    if (TrySetHiddenAttribute(path, hidden: true))
                    {
                        attrChanged = true;
                        if (config.HiddenDesktopPaths.Add(path))
                            configChanged = true;
                    }
                }
            }
            catch
            {
            }
        }

        foreach (var tracked in config.HiddenDesktopPaths.ToList())
        {
            if (organizedDesktopPaths.Contains(tracked))
                continue;

            if (File.Exists(tracked) || Directory.Exists(tracked))
            {
                if (TrySetHiddenAttribute(tracked, hidden: false))
                    attrChanged = true;
            }

            if (config.HiddenDesktopPaths.Remove(tracked))
                configChanged = true;
        }

        if (attrChanged || forceRefresh)
        {
            RefreshDesktopView();
        }

        return configChanged;
    }

    /// <summary>
    /// 仅对指定路径中由 DeskBox 登记隐藏的项目清除 Hidden 属性（用于删除到回收站或移出格子前）。
    /// </summary>
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
                if (TrySetHiddenAttribute(normalized, hidden: false))
                    attrChanged = true;
            }

            if (config.HiddenDesktopPaths.Remove(normalized))
                configChanged = true;
        }

        if (attrChanged)
            RefreshDesktopView();

        return configChanged;
    }

    /// <summary>
    /// 恢复所有由 DeskBox 登记隐藏的桌面项目属性并刷新桌面视图。
    /// </summary>
    public bool RestoreAllHiddenDesktopIcons(AppConfig config, bool forceRefresh = false)
    {
        bool attrChanged = false;
        bool configChanged = config.HiddenDesktopPaths.Count > 0;

        foreach (var path in config.HiddenDesktopPaths.ToList())
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                if (TrySetHiddenAttribute(path, hidden: false))
                    attrChanged = true;
            }
        }

        config.HiddenDesktopPaths.Clear();
        if (attrChanged || forceRefresh)
            RefreshDesktopView();

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

    private static bool TrySetHiddenAttribute(string path, bool hidden)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            var targetAttrs = hidden
                ? (attrs | FileAttributes.Hidden)
                : (attrs & ~FileAttributes.Hidden);
            if (attrs == targetAttrs)
                return true;

            File.SetAttributes(path, targetAttrs);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void RefreshDesktopView()
    {
        if (!_enableShellRefresh)
            return;

        if (!TryRefreshDesktopShellViewViaCom())
        {
            _hostService.RefreshDesktopShellView();
        }
    }

    private static bool TryRefreshDesktopShellViewViaCom()
    {
        try
        {
            if (!TryGetDesktopShellView(out var sv, out _))
                return false;

            return sv.Refresh() == 0;
        }
        catch
        {
            return false;
        }
    }

    public bool CaptureVisibleDesktopIconPositions(AppConfig config)
    {
        if (!_enableShellRefresh)
            return false;

        try
        {
            if (!TryGetDesktopShellView(out _, out var fv) || fv == null)
                return false;

            if (fv.ItemCount(0, out int count) != 0 || count <= 0)
                return false;

            var captured = new Dictionary<string, DesktopIconPoint>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < count; i++)
            {
                if (fv.Item(i, out var pidl) != 0 || pidl == IntPtr.Zero)
                    continue;

                try
                {
                    if (fv.GetItemPosition(pidl, out var pt) != 0)
                        continue;

                    if (TryGetParsingName(pidl, out var name) && !string.IsNullOrWhiteSpace(name))
                    {
                        captured[name] = new DesktopIconPoint(pt.X, pt.Y);
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pidl);
                }
            }

            if (captured.Count == 0)
                return false;

            config.SavedDesktopIconPositions = captured;
            return true;
        }
        catch
        {
            return false;
        }
    }

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

    public bool RestoreDesktopIconPositions(IReadOnlyDictionary<string, DesktopIconPoint> savedPositions)
    {
        if (!_enableShellRefresh || savedPositions == null || savedPositions.Count == 0)
            return false;

        try
        {
            if (!TryGetDesktopShellView(out _, out var fv) || fv == null)
                return false;

            if (fv.ItemCount(0, out int count) != 0 || count <= 0)
                return false;

            var items = new List<(string Name, IntPtr Pidl, NativeMethods.POINT CurrentPt)>();
            try
            {
                for (int i = 0; i < count; i++)
                {
                    if (fv.Item(i, out var pidl) != 0 || pidl == IntPtr.Zero)
                        continue;

                    if (fv.GetItemPosition(pidl, out var pt) == 0 &&
                        TryGetParsingName(pidl, out var name) &&
                        !string.IsNullOrWhiteSpace(name))
                    {
                        items.Add((name, pidl, pt));
                    }
                    else
                    {
                        Marshal.FreeCoTaskMem(pidl);
                    }
                }

                var snapshotList = items
                    .Select(it => (it.Name, it.CurrentPt.X, it.CurrentPt.Y))
                    .ToList();
                var spacing = new NativeMethods.POINT { X = 180, Y = 200 };
                fv.GetSpacing(ref spacing);

                var plan = ComputeConflictFreeRestorePositions(savedPositions, snapshotList, spacing.X, spacing.Y);
                if (plan.Count == 0)
                    return false;

                // 仅对当前坐标偏离目标位置或发生冲突的项执行移动，已就位的项无需重绘
                var currentLookup = items.ToDictionary(it => it.Name, it => it.CurrentPt, StringComparer.OrdinalIgnoreCase);
                bool anyMoved = false;

                var conflictPidls = new List<IntPtr>();
                var conflictPts = new List<NativeMethods.POINT>();
                var toRestorePidls = new List<IntPtr>();
                var toRestorePts = new List<NativeMethods.POINT>();

                foreach (var item in items)
                {
                    if (!plan.TryGetValue(item.Name, out var targetPt))
                        continue;

                    if (savedPositions.ContainsKey(item.Name))
                    {
                        if (Math.Abs(item.CurrentPt.X - targetPt.X) > 1 || Math.Abs(item.CurrentPt.Y - targetPt.Y) > 1)
                        {
                            toRestorePidls.Add(item.Pidl);
                            toRestorePts.Add(new NativeMethods.POINT { X = targetPt.X, Y = targetPt.Y });
                        }
                    }
                    else
                    {
                        if (Math.Abs(item.CurrentPt.X - targetPt.X) > 1 || Math.Abs(item.CurrentPt.Y - targetPt.Y) > 1)
                        {
                            conflictPidls.Add(item.Pidl);
                            conflictPts.Add(new NativeMethods.POINT { X = targetPt.X, Y = targetPt.Y });
                        }
                    }
                }

                if (conflictPidls.Count > 0)
                {
                    fv.SelectAndPositionItems((uint)conflictPidls.Count, conflictPidls.ToArray(), conflictPts.ToArray(), SVSI_POSITIONITEM);
                    anyMoved = true;
                }

                if (toRestorePidls.Count > 0)
                {
                    fv.SelectAndPositionItems((uint)toRestorePidls.Count, toRestorePidls.ToArray(), toRestorePts.ToArray(), SVSI_POSITIONITEM);
                    anyMoved = true;
                }
                return anyMoved;
            }
            finally
            {
                foreach (var item in items)
                {
                    Marshal.FreeCoTaskMem(item.Pidl);
                }
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetDesktopShellView(out IShellView sv, out IFolderView? fv)
    {
        sv = null!;
        fv = null;

        var swType = Type.GetTypeFromCLSID(ClsidShellWindows);
        if (swType == null)
            return false;

        dynamic? sw = Activator.CreateInstance(swType);
        if (sw == null)
            return false;

        object loc = CSIDL_DESKTOP;
        object empty = Type.Missing;
        int hwnd = 0;
        object? disp = sw.FindWindowSW(ref loc, ref empty, SWC_DESKTOP, out hwnd, SWFO_NEEDDISPATCH);
        if (disp is not IServiceProvider sp)
            return false;

        var sid = SidSTopLevelBrowser;
        var iidSB = typeof(IShellBrowser).GUID;
        if (sp.QueryService(ref sid, ref iidSB, out var sbObj) != 0 || sbObj is not IShellBrowser sb)
            return false;

        if (sb.QueryActiveShellView(out var svObj) != 0 || svObj is not IShellView shellView)
            return false;

        sv = shellView;
        fv = svObj as IFolderView;
        return true;
    }

    private static bool TryGetParsingName(IntPtr pidl, out string name)
    {
        name = string.Empty;
        if (SHGetNameFromIDList(pidl, SIGDN_DESKTOPABSOLUTEPARSING, out var pName) == 0 && pName != IntPtr.Zero)
        {
            try
            {
                name = Marshal.PtrToStringUni(pName) ?? string.Empty;
                return !string.IsNullOrEmpty(name);
            }
            finally
            {
                Marshal.FreeCoTaskMem(pName);
            }
        }
        return false;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetNameFromIDList(IntPtr pidl, uint sigdnName, out IntPtr ppszName);

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid guidService, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppvObject);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        int GetWindow(out IntPtr phwnd);
        int ContextSensitiveHelp(bool fEnterMode);
        int InsertMenusSB(IntPtr hmenuShared, IntPtr lpMenuWidths);
        int SetMenuSB(IntPtr hmenuShared, IntPtr holemenuRes, IntPtr hwndActiveObject);
        int RemoveMenusSB(IntPtr hmenuShared);
        int SetStatusTextSB(IntPtr pszStatusText);
        int EnableModelessSB(bool fEnable);
        int TranslateAcceleratorSB(IntPtr pmsg, ushort wID);
        int BrowseObject(IntPtr pidl, uint wFlags);
        int GetViewStateStream(uint grfMode, out IntPtr ppStrm);
        int GetControlWindow(uint id, out IntPtr lphwnd);
        int SendControlMsg(uint id, uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr pret);
        [PreserveSig]
        int QueryActiveShellView([MarshalAs(UnmanagedType.Interface)] out object ppshv);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        [PreserveSig] int GetWindow(out IntPtr phwnd);
        [PreserveSig] int ContextSensitiveHelp(bool fEnterMode);
        [PreserveSig] int TranslateAccelerator(IntPtr pmsg);
        [PreserveSig] int EnableModeless(bool fEnable);
        [PreserveSig] int UIActivate(uint uState);
        [PreserveSig] int Refresh();
    }

    [ComImport, Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView
    {
        [PreserveSig] int GetCurrentViewMode(out uint pViewMode);
        [PreserveSig] int SetCurrentViewMode(uint ViewMode);
        [PreserveSig] int GetFolder(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        [PreserveSig] int Item(int iItemIndex, out IntPtr ppidl);
        [PreserveSig] int ItemCount(uint uFlags, out int pcItems);
        [PreserveSig] int Items(uint uFlags, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        [PreserveSig] int GetSelectionMarkedItem(out int piItem);
        [PreserveSig] int GetFocusedItem(out int piItem);
        [PreserveSig] int GetItemPosition(IntPtr pidl, out NativeMethods.POINT ppt);
        [PreserveSig] int GetSpacing(ref NativeMethods.POINT ppt);
        [PreserveSig] int GetDefaultSpacing(ref NativeMethods.POINT ppt);
        [PreserveSig] int GetAutoArrange();
        [PreserveSig] int SelectItem(int iItem, uint dwFlags);
        [PreserveSig] int SelectAndPositionItems(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, [MarshalAs(UnmanagedType.LPArray)] NativeMethods.POINT[] apt, uint dwFlags);
    }
}
