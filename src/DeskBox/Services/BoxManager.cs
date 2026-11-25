using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DeskBox.Interop;
using DeskBox.Models;
using DeskBox.Views;
using Microsoft.Win32;
using Application = System.Windows.Application;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace DeskBox.Services;

public sealed class BoxManager : IDisposable
{
    private const int ToggleHotkeyId = 9001;
    public const int WmAppCommand = 0x8000 + 0x50;
    public const int IpcCmdNewBox = 1;
    public const int IpcCmdToggleTidy = 2;
    public const int IpcCmdShowSettings = 3;
    public const int IpcCmdShowAppearance = 4;
    public const string IpcHostWindowName = "DeskBoxHotkeyHost";
    public const string IpcPipeName = "DeskBox_IPC_Pipe_v1";

    private readonly Dispatcher _dispatcher;
    private readonly List<DeskBoxWindow> _windows = new();
    private readonly List<DesktopCanvasWindow> _canvasWindows = new();
    private readonly DispatcherTimer _displayChangeDebounce;
    private readonly DispatcherTimer _idleTrimTimer;
    private readonly DispatcherTimer _desktopSyncDebounce;
    private readonly List<FileSystemWatcher> _desktopWatchers = new();

    private TrayIconService? _trayService;
    private MenuItem? _trayToggleTidyItem;
    private HotkeyHostWindow? _hotkeyHost;
    private DesktopInteractionHook? _mouseHook;
    private SettingsWindow? _settingsWindow;
    private BoxAppearanceWindow? _appearanceWindow;
    private GuideOverlayWindow? _guideOverlay;
    private string _currentSignature = string.Empty;
    private bool _nativeIconsHiddenByUs;
    private bool _pureDesktopNativeIconsHiddenByUs;
    private CancellationTokenSource? _pureDesktopRestoreCts;
    private CancellationTokenSource? _ipcCts;
    private bool _isDisposed;
    private bool _isReorderingZOrder;
    private readonly bool _isSandbox;

    public bool IsSandbox => _isSandbox;
    public bool IsReorderingZOrder => _isReorderingZOrder;
    public ConfigService ConfigService { get; }
    public MonitorTopologyService TopologyService { get; }
    public ShellService ShellService { get; }
    public DesktopHostService HostService { get; }
    public DesktopIconFilterService IconFilterService { get; }
    public WallpaperBlurService WallpaperBlurService { get; }
    public BoxStorageService StorageService { get; }
    public AppConfig Config { get; private set; }

    public BoxManager(Dispatcher dispatcher, string? sandboxDir = null)
    {
        _dispatcher = dispatcher;
        _isSandbox = !string.IsNullOrWhiteSpace(sandboxDir);
        TopologyService = new MonitorTopologyService();
        ShellService = new ShellService();
        HostService = new DesktopHostService();

        if (_isSandbox)
        {
            var configDir = Path.Combine(sandboxDir!, "Config");
            var storageDir = Path.Combine(sandboxDir!, "Storage");
            var userDesktopDir = Path.Combine(sandboxDir!, "UserDesktop");
            var publicDesktopDir = Path.Combine(sandboxDir!, "PublicDesktop");
            Directory.CreateDirectory(configDir);
            Directory.CreateDirectory(storageDir);
            Directory.CreateDirectory(userDesktopDir);
            Directory.CreateDirectory(publicDesktopDir);

            ConfigService = new ConfigService(configDir);
            StorageService = new BoxStorageService(storageDir, userDesktopDir, publicDesktopDir);
            IconFilterService = new DesktopIconFilterService(HostService, StorageService, enableShellRefresh: false);
        }
        else
        {
            ConfigService = new ConfigService();
            StorageService = new BoxStorageService();
            IconFilterService = new DesktopIconFilterService(HostService, StorageService);
        }

        WallpaperBlurService = new WallpaperBlurService();
        WallpaperBlurService.WallpaperChanged += () => _dispatcher.BeginInvoke(ApplyAppearanceToAllBoxes);
        Config = ConfigService.LoadOrCreateDefault();

        _displayChangeDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _displayChangeDebounce.Tick += (_, _) =>
        {
            _displayChangeDebounce.Stop();
            OnDisplayTopologyChanged();
        };

        _idleTrimTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _idleTrimTimer.Tick += (_, _) =>
        {
            _idleTrimTimer.Stop();
            TrimWorkingSetLight();
        };

        _desktopSyncDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _desktopSyncDebounce.Tick += (_, _) =>
        {
            _desktopSyncDebounce.Stop();
            OnDesktopFilesChanged();
        };
    }

    public void Start()
    {
        // 默认使用 SoftwareOnly，避免加载 D3D9 / 核显着色器驱动额外占用 40MB+ 后台内存
        if (!Config.General.EnableHardwareAcceleration)
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        }

        NordThemeService.ApplyTheme(Config.General.UiTheme);

        if (!_isSandbox)
        {
            _hotkeyHost = new HotkeyHostWindow(ToggleHotkeyId, ToggleAllBoxesVisibility, HandleIpcCommand);
            RegisterGlobalToggleHotkey();
            DesktopHostService.RegisterOrUpdateDesktopContextMenu(Config.Tidy.IsTidyPaused);
            _ipcCts = new CancellationTokenSource();
            _ = ListenIpcPipeAsync(_ipcCts.Token);
        }

        InitTrayIcon();

        var monitors = TopologyService.GetConnectedMonitors();
        _currentSignature = MonitorTopologyService.ComputeTopologySignature(monitors);

        MigrateLegacyStorageBackToDesktop();

        // 启动时自动清理旧版可能遗留的 Hidden 文件属性与系统图标注册表隐藏项
        if (Config.HiddenDesktopPaths.Count > 0)
        {
            IconFilterService.RestoreAllHiddenDesktopIcons(Config);
            ConfigService.SaveImmediate(Config);
        }
        if (Config.HiddenSystemDesktopGuids.Count > 0)
        {
            RestoreAllSystemDesktopIcons(refreshImmediately: false);
            ConfigService.SaveImmediate(Config);
        }

        var hadCurrentSnapshot = Config.LayoutSnapshots.TryGetValue(_currentSignature, out var initialSnapshot);
        var missingAnyBoxInSnapshot = !hadCurrentSnapshot ||
            Config.Boxes.Any(b => !initialSnapshot!.Placements.ContainsKey(b.Id));
        var upgradedAnyPlacement = false;

        foreach (var boxCfg in Config.Boxes)
        {
            if (CreateAndShowBoxWindow(boxCfg, monitors))
            {
                upgradedAnyPlacement = true;
            }
        }

        if (Config.Tidy.IsTidyPaused)
        {
            foreach (var w in _windows)
            {
                w.Hide();
            }
            HideDesktopCanvases();
            if (!_isSandbox)
            {
                HostService.SetNativeDesktopIconsVisible(true);
            }
        }
        else
        {
            if (!_isSandbox)
            {
                HostService.SetNativeDesktopIconsVisible(false);
                _nativeIconsHiddenByUs = true;
            }

            if (!Config.Tidy.HideNativeDesktopIcons)
            {
                SyncDesktopCanvasesForMonitors(monitors, show: true);
                RefreshAllDesktopCanvases();
            }
            EnsureCanvasesAtBottom();
        }

        SetupDesktopWatchers();

        var wasBoxesHidden = Config.Tidy.BoxesHidden;
        Config.Tidy.BoxesHidden = false;

        if (!_isSandbox)
        {
            _mouseHook = new DesktopInteractionHook(
                _dispatcher,
                HostService,
                () => Config.Tidy.DoubleClickBlankToHide,
                ToggleAllBoxesVisibility);
            _mouseHook.Start();
        }

        if (missingAnyBoxInSnapshot || upgradedAnyPlacement)
        {
            RecordCurrentPlacementsAndSave(immediate: true, propagateSameMonitor: false);
        }
        else if (wasBoxesHidden)
        {
            ConfigService.SaveImmediate(Config);
        }
        SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
        if (Application.Current != null)
        {
            Application.Current.SessionEnding += OnSessionEnding;
        }

        ScheduleIdleMemoryTrim(delayMs: 1800);
    }

    private void ScheduleIdleMemoryTrim(int delayMs = 5000)
    {
        _idleTrimTimer.Stop();
        _idleTrimTimer.Interval = TimeSpan.FromMilliseconds(delayMs);
        _idleTrimTimer.Start();
    }

    private static void TrimWorkingSetLight()
    {
        try
        {
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
            using var proc = Process.GetCurrentProcess();
            NativeMethods.EmptyWorkingSet(proc.Handle);
        }
        catch
        {
        }
    }

    /// <summary>
    /// 启用 Windows 11 效率模式 (EcoQoS)，降低后台空闲时的系统调度与内存页驻留开销。
    /// </summary>
    private static void TryEnableProcessEfficiencyMode()
    {
        try
        {
            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = 1,
                ControlMask = 0x1 | 0x4, // EXECUTION_SPEED | IGNORE_TIMER_RESOLUTION
                StateMask = 0x1 | 0x4
            };
            int size = Marshal.SizeOf(state);
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(state, ptr, false);
                using var proc = Process.GetCurrentProcess();
                SetProcessInformation(proc.Handle, 4 /* ProcessPowerThrottling */, ptr, size);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch
        {
        }
    }

    private void SystemEvents_DisplaySettingsChanged(object? sender, EventArgs e)
    {
        _dispatcher.BeginInvoke(() =>
        {
            _displayChangeDebounce.Stop();
            _displayChangeDebounce.Start();
        });
    }

    private void OnDisplayTopologyChanged()
    {
        var monitors = TopologyService.GetConnectedMonitors();
        if (monitors.Count == 0)
            return;

        _currentSignature = MonitorTopologyService.ComputeTopologySignature(monitors);
        ApplyTopologySnapshotToAllWindows(monitors);

        if (!Config.Tidy.IsTidyPaused && !Config.Tidy.BoxesHidden && !Config.Tidy.HideNativeDesktopIcons)
        {
            SyncDesktopCanvasesForMonitors(monitors, show: true);
            RefreshAllDesktopCanvases();
            EnsureCanvasesAtBottom();
        }

        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.RefreshTopologyAndStats();
        }
        ScheduleIdleMemoryTrim();
    }

    public void ReapplyCurrentTopologyLayout()
    {
        var monitors = TopologyService.GetConnectedMonitors();
        if (monitors.Count == 0)
            return;

        _currentSignature = MonitorTopologyService.ComputeTopologySignature(monitors);
        ApplyTopologySnapshotToAllWindows(monitors);
    }

    private bool CreateAndShowBoxWindow(BoxConfig boxCfg, IReadOnlyList<MonitorDescriptor> monitors)
    {
        var window = new DeskBoxWindow(boxCfg, ShellService, HostService, this);
        if (_isSandbox)
        {
            window.Closed += (_, _) =>
            {
                if (!_isDisposed && _windows.All(w => !w.IsVisible))
                    Application.Current?.Shutdown();
            };
        }
        _windows.Add(window);
        window.Show();

        var placement = ResolveInitialPlacementForBox(boxCfg, monitors, out var upgradedFromNewerSnapshot);
        var (expandedRect, headerH, _, _) = MonitorTopologyService.ResolvePhysicalPlacement(placement, monitors);
        boxCfg.IsCollapsed = placement.IsCollapsed;
        window.ApplyPhysicalPlacement(expandedRect, headerH);

        if (_isSandbox)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd != IntPtr.Zero)
            {
                NativeMethods.SetWindowPos(
                    hwnd,
                    NativeMethods.HWND_TOP,
                    0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE);
            }
        }

        return upgradedFromNewerSnapshot;
    }

    private void ApplyTopologySnapshotToAllWindows(IReadOnlyList<MonitorDescriptor> monitors)
    {
        foreach (var window in _windows)
        {
            var boxCfg = window.Config;
            var placement = ResolveInitialPlacementForBox(boxCfg, monitors, out _);
            var (expandedRect, headerH, _, _) = MonitorTopologyService.ResolvePhysicalPlacement(placement, monitors);
            boxCfg.IsCollapsed = placement.IsCollapsed;
            window.UpdateMonitorsCache(monitors);
            window.ApplyPhysicalPlacement(expandedRect, headerH);
        }
    }

    private BoxPlacement ResolveInitialPlacementForBox(BoxConfig boxCfg, IReadOnlyList<MonitorDescriptor> monitors)
        => ResolveInitialPlacementForBox(boxCfg, monitors, out _);

    private BoxPlacement ResolveInitialPlacementForBox(
        BoxConfig boxCfg,
        IReadOnlyList<MonitorDescriptor> monitors,
        out bool upgradedFromNewerSnapshot)
    {
        var resolved = MonitorTopologyService.ResolveBoxPlacement(boxCfg, _currentSignature, Config.LayoutSnapshots, monitors);
        upgradedFromNewerSnapshot = Config.LayoutSnapshots.TryGetValue(_currentSignature, out var currentSnap) &&
            currentSnap.Placements.TryGetValue(boxCfg.Id, out var currentStored) &&
            (Math.Abs(resolved.RelX - currentStored.RelX) > 1e-5 ||
             Math.Abs(resolved.RelY - currentStored.RelY) > 1e-5 ||
             Math.Abs(resolved.RelWidth - currentStored.RelWidth) > 1e-5 ||
             Math.Abs(resolved.RelExpandedHeight - currentStored.RelExpandedHeight) > 1e-5 ||
             resolved.IsCollapsed != currentStored.IsCollapsed);
        return resolved;
    }

    public void RecordCurrentPlacementsAndSave(bool immediate = false, bool propagateSameMonitor = true)
    {
        var monitors = TopologyService.GetConnectedMonitors();
        if (monitors.Count == 0)
            return;

        _currentSignature = MonitorTopologyService.ComputeTopologySignature(monitors);
        if (!Config.LayoutSnapshots.TryGetValue(_currentSignature, out var snapshot))
        {
            snapshot = new TopologyLayoutSnapshot
            {
                Signature = _currentSignature
            };
            Config.LayoutSnapshots[_currentSignature] = snapshot;
        }

        snapshot.Description = MonitorTopologyService.BuildTopologyDescription(monitors);
        snapshot.UpdatedAt = DateTime.UtcNow;

        foreach (var w in _windows)
        {
            var (rect, expandedH) = w.GetPhysicalGeometry();
            if (rect.Width <= 0 || rect.Height <= 0)
                continue;
            var placement = MonitorTopologyService.CapturePlacement(rect, expandedH, w.Config.IsCollapsed, monitors);
            snapshot.Placements[w.BoxId] = placement.Clone();
            w.Config.FallbackPlacement = placement.Clone();

            if (propagateSameMonitor)
            {
                MonitorTopologyService.PropagateSameMonitorPlacement(
                    w.BoxId,
                    placement,
                    _currentSignature,
                    Config.LayoutSnapshots);
            }
        }

        if (immediate) ConfigService.SaveImmediate(Config);
        else ConfigService.SaveDebounced(Config);

        ScheduleIdleMemoryTrim();
    }

    public void SaveConfigDebounced()
    {
        ConfigService.SaveDebounced(Config);
        ScheduleIdleMemoryTrim();
    }

    public (int x, int y) SnapMoveWithGuides(
        DeskBoxWindow movingWindow,
        int candidateX,
        int candidateY,
        int width,
        int visibleHeight,
        int cursorX,
        int cursorY)
    {
        const int snapThreshold = 14;
        var monitors = TopologyService.GetConnectedMonitors();
        if (monitors.Count == 0)
            return (candidateX, candidateY);

        var owner = monitors.FirstOrDefault(m =>
            cursorX >= m.BoundsX && cursorX < m.BoundsX + m.BoundsWidth &&
            cursorY >= m.BoundsY && cursorY < m.BoundsY + m.BoundsHeight)
            ?? MonitorTopologyService.FindOwnerMonitor(
                new MonitorTopologyService.PhysicalRect(candidateX, candidateY, width, visibleHeight),
                monitors);

        var others = _windows
            .Where(w => !ReferenceEquals(w, movingWindow) && w.IsVisible)
            .Select(w => w.GetPhysicalGeometry().currentRect)
            .ToList();

        int bestDx = int.MaxValue;
        int bestDy = int.MaxValue;

        void ConsiderDx(int targetX)
        {
            int d = targetX - candidateX;
            if (Math.Abs(d) <= snapThreshold && Math.Abs(d) < Math.Abs(bestDx))
                bestDx = d;
        }
        void ConsiderDy(int targetY)
        {
            int d = targetY - candidateY;
            if (Math.Abs(d) <= snapThreshold && Math.Abs(d) < Math.Abs(bestDy))
                bestDy = d;
        }

        // 显示器工作区边缘
        ConsiderDx(owner.WorkX);
        ConsiderDx(owner.WorkX + owner.WorkWidth - width);
        ConsiderDy(owner.WorkY);
        ConsiderDy(owner.WorkY + owner.WorkHeight - visibleHeight);

        // 其它格子对齐 & 紧贴
        foreach (var r in others)
        {
            ConsiderDx(r.X);
            ConsiderDx(r.X + r.Width - width);
            ConsiderDx(r.X - width);
            ConsiderDx(r.X + r.Width);

            ConsiderDy(r.Y);
            ConsiderDy(r.Y + r.Height - visibleHeight);
            ConsiderDy(r.Y - visibleHeight);
            ConsiderDy(r.Y + r.Height);
        }

        int snappedX = bestDx != int.MaxValue ? candidateX + bestDx : candidateX;
        int snappedY = bestDy != int.MaxValue ? candidateY + bestDy : candidateY;

        // 严格防越界，彻底禁止拖出屏幕或黑洞区
        (snappedX, snappedY) = MonitorTopologyService.ClampDragPositionToMonitors(
            snappedX, snappedY, width, visibleHeight, cursorX, cursorY, monitors);

        // 生成贯穿对齐辅助线
        var guides = new List<AlignmentGuideLine>();
        foreach (var r in others)
        {
            int minY = Math.Min(snappedY, r.Y);
            int maxY = Math.Max(snappedY + visibleHeight, r.Y + r.Height);
            int minX = Math.Min(snappedX, r.X);
            int maxX = Math.Max(snappedX + width, r.X + r.Width);

            if (Math.Abs(snappedX - r.X) <= 1)
                guides.Add(new AlignmentGuideLine(snappedX, minY, snappedX, maxY, "左对齐"));
            if (Math.Abs((snappedX + width) - (r.X + r.Width)) <= 1)
                guides.Add(new AlignmentGuideLine(snappedX + width, minY, snappedX + width, maxY, "右对齐"));
            if (Math.Abs(snappedX - (r.X + r.Width)) <= 1)
                guides.Add(new AlignmentGuideLine(snappedX, minY, snappedX, maxY));
            if (Math.Abs((snappedX + width) - r.X) <= 1)
                guides.Add(new AlignmentGuideLine(snappedX + width, minY, snappedX + width, maxY));

            if (Math.Abs(snappedY - r.Y) <= 1)
                guides.Add(new AlignmentGuideLine(minX, snappedY, maxX, snappedY, "顶对齐"));
            if (Math.Abs((snappedY + visibleHeight) - (r.Y + r.Height)) <= 1)
                guides.Add(new AlignmentGuideLine(minX, snappedY + visibleHeight, maxX, snappedY + visibleHeight, "底对齐"));
            if (Math.Abs(snappedY - (r.Y + r.Height)) <= 1)
                guides.Add(new AlignmentGuideLine(minX, snappedY, maxX, snappedY));
            if (Math.Abs((snappedY + visibleHeight) - r.Y) <= 1)
                guides.Add(new AlignmentGuideLine(minX, snappedY + visibleHeight, maxX, snappedY + visibleHeight));
        }

        if (Math.Abs(snappedX - owner.WorkX) <= 1)
            guides.Add(new AlignmentGuideLine(owner.WorkX, owner.WorkY, owner.WorkX, owner.WorkY + owner.WorkHeight));
        if (Math.Abs((snappedX + width) - (owner.WorkX + owner.WorkWidth)) <= 1)
            guides.Add(new AlignmentGuideLine(owner.WorkX + owner.WorkWidth - 1, owner.WorkY, owner.WorkX + owner.WorkWidth - 1, owner.WorkY + owner.WorkHeight));

        ShowGuidesOverlay(owner, guides);
        return (snappedX, snappedY);
    }

    public (int x, int y, int width, int height) SnapResizeWithGuides(
        DeskBoxWindow resizingWindow,
        int candidateX,
        int candidateY,
        int candidateWidth,
        int candidateHeight,
        bool resizeLeft,
        bool resizeRight,
        bool resizeHeight)
    {
        const int snapThreshold = 14;
        const int minW = 200;
        const int minH = 120;

        var monitors = TopologyService.GetConnectedMonitors();
        if (monitors.Count == 0)
            return (candidateX, candidateY, Math.Max(minW, candidateWidth), Math.Max(minH, candidateHeight));

        var owner = MonitorTopologyService.FindOwnerMonitor(
            new MonitorTopologyService.PhysicalRect(candidateX, candidateY, candidateWidth, candidateHeight),
            monitors);

        var others = _windows
            .Where(w => !ReferenceEquals(w, resizingWindow) && w.IsVisible)
            .Select(w => w.GetPhysicalGeometry().currentRect)
            .ToList();

        int x = candidateX;
        int y = candidateY;
        int w = Math.Max(minW, candidateWidth);
        int h = Math.Max(minH, candidateHeight);
        int rightEdge = x + w;

        var guides = new List<AlignmentGuideLine>();
        MonitorTopologyService.PhysicalRect? equalWidthMatch = null;
        MonitorTopologyService.PhysicalRect? equalHeightMatch = null;

        if (resizeLeft || resizeRight)
        {
            // 1. 优先检查边缘对齐其它格子左/右边
            bool edgeAligned = false;
            foreach (var r in others)
            {
                if (resizeRight)
                {
                    if (Math.Abs(rightEdge - (r.X + r.Width)) <= snapThreshold)
                    {
                        w = Math.Max(minW, r.X + r.Width - x);
                        edgeAligned = true;
                        break;
                    }
                    if (Math.Abs(rightEdge - r.X) <= snapThreshold)
                    {
                        w = Math.Max(minW, r.X - x);
                        edgeAligned = true;
                        break;
                    }
                }
                else if (resizeLeft)
                {
                    if (Math.Abs(x - r.X) <= snapThreshold)
                    {
                        x = Math.Min(rightEdge - minW, r.X);
                        w = rightEdge - x;
                        edgeAligned = true;
                        break;
                    }
                    if (Math.Abs(x - (r.X + r.Width)) <= snapThreshold)
                    {
                        x = Math.Min(rightEdge - minW, r.X + r.Width);
                        w = rightEdge - x;
                        edgeAligned = true;
                        break;
                    }
                }
            }

            // 2. 若未边缘对齐，检查等宽吸附
            if (!edgeAligned)
            {
                foreach (var r in others)
                {
                    if (Math.Abs(w - r.Width) <= snapThreshold)
                    {
                        w = Math.Max(minW, r.Width);
                        if (resizeLeft)
                            x = rightEdge - w;
                        equalWidthMatch = r;
                        break;
                    }
                }
            }
            else
            {
                equalWidthMatch = others.FirstOrDefault(r => Math.Abs(w - r.Width) <= 1);
            }
        }

        if (resizeHeight)
        {
            bool edgeAlignedH = false;
            int bottomEdge = y + h;
            foreach (var r in others)
            {
                if (Math.Abs(bottomEdge - (r.Y + r.Height)) <= snapThreshold)
                {
                    h = Math.Max(minH, r.Y + r.Height - y);
                    edgeAlignedH = true;
                    break;
                }
                if (Math.Abs(bottomEdge - r.Y) <= snapThreshold)
                {
                    h = Math.Max(minH, r.Y - y);
                    edgeAlignedH = true;
                    break;
                }
            }

            if (!edgeAlignedH)
            {
                foreach (var r in others)
                {
                    if (Math.Abs(h - r.Height) <= snapThreshold)
                    {
                        h = Math.Max(minH, r.Height);
                        equalHeightMatch = r;
                        break;
                    }
                }
            }
            else
            {
                equalHeightMatch = others.FirstOrDefault(r => Math.Abs(h - r.Height) <= 1);
            }
        }

        // 严格限制在显示器工作区内，不溢出屏幕
        x = Math.Max(owner.WorkX, x);
        if (x + w > owner.WorkX + owner.WorkWidth)
            w = Math.Max(minW, owner.WorkX + owner.WorkWidth - x);
        if (y + h > owner.WorkY + owner.WorkHeight)
            h = Math.Max(minH, owner.WorkY + owner.WorkHeight - y);

        // 生成缩放提示线
        foreach (var r in others)
        {
            int minY = Math.Min(y, r.Y);
            int maxY = Math.Max(y + h, r.Y + r.Height);
            int minX = Math.Min(x, r.X);
            int maxX = Math.Max(x + w, r.X + r.Width);

            if ((resizeLeft || resizeRight) && Math.Abs((x + w) - (r.X + r.Width)) <= 1)
                guides.Add(new AlignmentGuideLine(x + w, minY, x + w, maxY, "右对齐"));
            if ((resizeLeft || resizeRight) && Math.Abs(x - r.X) <= 1)
                guides.Add(new AlignmentGuideLine(x, minY, x, maxY, "左对齐"));
            if (resizeHeight && Math.Abs((y + h) - (r.Y + r.Height)) <= 1)
                guides.Add(new AlignmentGuideLine(minX, y + h, maxX, y + h, "底对齐"));
        }

        if (equalWidthMatch is { } ew)
        {
            // 等宽双侧标尺提示
            guides.Add(new AlignmentGuideLine(x, y + h + 8, x + w, y + h + 8, "等宽"));
            guides.Add(new AlignmentGuideLine(ew.X, ew.Y + ew.Height + 8, ew.X + ew.Width, ew.Y + ew.Height + 8, "等宽"));
        }

        if (equalHeightMatch is { } eh)
        {
            // 等高标尺提示
            guides.Add(new AlignmentGuideLine(x + w + 8, y, x + w + 8, y + h, "等高"));
            guides.Add(new AlignmentGuideLine(eh.X + eh.Width + 8, eh.Y, eh.X + eh.Width + 8, eh.Y + eh.Height, "等高"));
        }

        ShowGuidesOverlay(owner, guides);
        return (x, y, w, h);
    }

    private void ShowGuidesOverlay(MonitorDescriptor monitor, IReadOnlyList<AlignmentGuideLine> guides)
    {
        if (guides.Count == 0)
        {
            HideAlignmentGuides();
            return;
        }

        _guideOverlay ??= new GuideOverlayWindow();
        _guideOverlay.ShowGuides(monitor, guides);
    }

    public void HideAlignmentGuides()
    {
        _guideOverlay?.HideGuides();
    }

    public void CreateNewBox()
    {
        var monitors = TopologyService.GetConnectedMonitors();
        var index = Config.Boxes.Count + 1;
        var offset = (Config.Boxes.Count % 5) * 0.04;

        var box = new BoxConfig
        {
            Title = $"新格子 {index}",
            FallbackPlacement = new BoxPlacement
            {
                WasPrimaryMonitor = true,
                RelX = Math.Min(0.75, 0.35 + offset),
                RelY = Math.Min(0.60, 0.08 + offset),
                RelWidth = 0.185,
                RelExpandedHeight = 0.40
            }
        };
        box.ApplyDefaultAppearanceFrom(Config.Tidy);

        Config.Boxes.Add(box);
        if (Config.Tidy.IsTidyPaused)
        {
            SetTidyPaused(false);
        }
        else if (Config.Tidy.BoxesHidden)
        {
            SetPureDesktopHidden(false);
        }
        CreateAndShowBoxWindow(box, monitors);
        EnsureCanvasesAtBottom();
        RecordCurrentPlacementsAndSave(immediate: true);
    }

    public void CreateUnorganizedDesktopBox()
    {
        var existing = _windows.FirstOrDefault(w => w.Config.IsUnorganizedBox);
        if (existing != null)
        {
            if (Config.Tidy.BoxesHidden)
                SetPureDesktopHidden(false);
            existing.RefreshItems();
            existing.Show();
            return;
        }

        var monitors = TopologyService.GetConnectedMonitors();
        var box = new BoxConfig
        {
            Title = "桌面未整理",
            IsUnorganizedBox = true,
            FallbackPlacement = new BoxPlacement
            {
                WasPrimaryMonitor = true,
                RelX = 0.72,
                RelY = 0.08,
                RelWidth = 0.21,
                RelExpandedHeight = 0.56
            }
        };
        box.ApplyDefaultAppearanceFrom(Config.Tidy);

        Config.Boxes.Add(box);
        if (Config.Tidy.BoxesHidden)
            SetPureDesktopHidden(false);

        CreateAndShowBoxWindow(box, monitors);
        EnsureCanvasesAtBottom();
        RecordCurrentPlacementsAndSave(immediate: true);
    }

    private void SetupDesktopWatchers()
    {
        foreach (var dir in new[] { StorageService.UserDesktopDirectory, StorageService.PublicDesktopDirectory })
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                continue;

            try
            {
                var watcher = new FileSystemWatcher(dir)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    EnableRaisingEvents = true
                };
                FileSystemEventHandler onFsChanged = (_, _) => _dispatcher.BeginInvoke(() =>
                {
                    _desktopSyncDebounce.Stop();
                    _desktopSyncDebounce.Start();
                });
                RenamedEventHandler onFsRenamed = (_, e) => _dispatcher.BeginInvoke(() =>
                {
                    HandleDesktopFileRenamed(e.OldFullPath, e.FullPath);
                    _desktopSyncDebounce.Stop();
                    _desktopSyncDebounce.Start();
                });
                watcher.Created += onFsChanged;
                watcher.Deleted += onFsChanged;
                watcher.Renamed += onFsRenamed;
                _desktopWatchers.Add(watcher);
            }
            catch
            {
            }
        }
    }

    private void HandleDesktopFileRenamed(string oldPath, string newPath)
    {
        bool changed = false;
        var oldKey = ShellService.GetItemOrderKey(oldPath);
        var newKey = ShellService.GetItemOrderKey(newPath);

        foreach (var box in Config.Boxes)
        {
            foreach (var item in box.Items)
            {
                if (string.Equals(item.Path, oldPath, StringComparison.OrdinalIgnoreCase))
                {
                    item.Path = newPath;
                    changed = true;
                }
            }

            for (int i = 0; i < box.CustomOrder.Count; i++)
            {
                if (string.Equals(box.CustomOrder[i], oldKey, StringComparison.OrdinalIgnoreCase))
                {
                    box.CustomOrder[i] = newKey;
                    changed = true;
                }
            }
        }

        if (Config.HiddenDesktopPaths.Remove(oldPath))
        {
            Config.HiddenDesktopPaths.Add(newPath);
            changed = true;
        }

        if (Config.DesktopIconPositions.TryGetValue(oldPath, out var gridPos))
        {
            Config.DesktopIconPositions.Remove(oldPath);
            Config.DesktopIconPositions[newPath] = gridPos;
            changed = true;
        }

        if (changed)
            ConfigService.SaveDebounced(Config);
    }

    private void OnDesktopFilesChanged()
    {
        foreach (var w in _windows)
        {
            if (string.IsNullOrWhiteSpace(w.Config.MappedFolderPath))
            {
                w.RefreshItems();
            }
        }
        RefreshAllDesktopCanvases();
    }

    public void RefreshAllBoxes()
    {
        foreach (var w in _windows)
        {
            w.RefreshItems();
        }
    }

    public void RefreshUnorganizedBoxes()
    {
        foreach (var w in _windows)
        {
            if (w.Config.IsUnorganizedBox)
                w.RefreshItems();
        }
        RefreshAllDesktopCanvases();
    }

    public void PushCanvasesBelowBoxes()
    {
        if (_isDisposed || _isReorderingZOrder)
            return;

        _isReorderingZOrder = true;
        try
        {
            foreach (var canvas in _canvasWindows)
            {
                if (canvas.IsVisible)
                {
                    DesktopHostService.SendWindowToBottom(canvas);
                }
            }
        }
        finally
        {
            _isReorderingZOrder = false;
        }
    }

    public void EnsureCanvasesAtBottom()
    {
        if (_isReorderingZOrder || _isDisposed)
            return;

        _isReorderingZOrder = true;
        try
        {
            // 先将所有格子沉底于普通应用下方，再将所有桌面画布沉底于格子下方
            foreach (var box in _windows)
            {
                if (box.IsVisible)
                {
                    DesktopHostService.SendWindowToBottom(box);
                }
            }

            foreach (var canvas in _canvasWindows)
            {
                if (canvas.IsVisible)
                {
                    DesktopHostService.SendWindowToBottom(canvas);
                }
            }
        }
        finally
        {
            _isReorderingZOrder = false;
        }
    }

    private void SyncDesktopCanvasesForMonitors(IReadOnlyList<MonitorDescriptor> monitors, bool show)
    {
        if (_isSandbox)
            return;

        var monitorIds = new HashSet<string>(monitors.Select(m => m.MonitorId), StringComparer.OrdinalIgnoreCase);

        foreach (var stale in _canvasWindows.Where(c => !monitorIds.Contains(c.MonitorId)).ToList())
        {
            _canvasWindows.Remove(stale);
            stale.Close();
        }

        foreach (var m in monitors)
        {
            var existing = _canvasWindows.FirstOrDefault(c =>
                string.Equals(c.MonitorId, m.MonitorId, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                existing = new DesktopCanvasWindow(m, this, ShellService, HostService);
                _canvasWindows.Add(existing);
            }
            else
            {
                existing.UpdateMonitorBounds(m);
            }

            if (show && !existing.IsVisible)
            {
                existing.Show();
            }
            else if (!show && existing.IsVisible)
            {
                existing.Hide();
            }
        }

        if (show)
        {
            EnsureCanvasesAtBottom();
        }
    }

    private void HideDesktopCanvases()
    {
        foreach (var canvas in _canvasWindows)
        {
            if (canvas.IsVisible)
                canvas.Hide();
        }
    }

    public void RefreshAllDesktopCanvases()
    {
        if (_isDisposed || _canvasWindows.Count == 0 || Config.Tidy.IsTidyPaused || Config.Tidy.BoxesHidden || Config.Tidy.HideNativeDesktopIcons)
            return;

        var entries = StorageService.EnumerateUnorganizedDesktopEntries(Config);
        var monitorGrids = _canvasWindows
            .Select(c => (c.MonitorId, c.Monitor.IsPrimary, c.GetGridMetrics()))
            .ToList();

        var resolved = DesktopGridLayoutService.ResolveAllPositions(
            entries.Select(e => e.Path).ToList(),
            Config.DesktopIconPositions,
            monitorGrids,
            out var positionsChanged);

        if (positionsChanged)
        {
            foreach (var kv in resolved)
                Config.DesktopIconPositions[kv.Key] = kv.Value;
            ConfigService.SaveDebounced(Config);
        }

        var entryLookup = entries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var canvas in _canvasWindows)
        {
            var itemsForMonitor = new List<(BoxItemEntry Entry, DesktopIconGridPosition Position)>();
            foreach (var kv in resolved)
            {
                if (string.Equals(kv.Value.MonitorId, canvas.MonitorId, StringComparison.OrdinalIgnoreCase) &&
                    entryLookup.TryGetValue(kv.Key, out var entry))
                {
                    itemsForMonitor.Add((entry, kv.Value));
                }
            }
            canvas.RenderItems(itemsForMonitor);
        }
    }

    public void ArrangeDesktopCanvasIcons(BoxSortField sortField, bool descending)
    {
        if (_canvasWindows.Count == 0)
            return;

        var primaryCanvas = _canvasWindows.FirstOrDefault(c => c.Monitor.IsPrimary) ?? _canvasWindows[0];
        var entries = StorageService.EnumerateUnorganizedDesktopEntries(Config);
        var sorted = sortField == BoxSortField.Custom
            ? entries.OrderBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList()
            : ShellService.SortItems(entries, sortField, descending).ToList();

        var arranged = DesktopGridLayoutService.ArrangeColumnMajor(
            sorted.Select(e => e.Path).ToList(),
            primaryCanvas.MonitorId,
            primaryCanvas.GetGridMetrics());

        foreach (var kv in arranged)
            Config.DesktopIconPositions[kv.Key] = kv.Value;

        Config.Tidy.DesktopIconSortField = sortField;
        Config.Tidy.DesktopIconSortDescending = descending;
        ConfigService.SaveDebounced(Config);
        RefreshAllDesktopCanvases();
        primaryCanvas.ShowToast("已重新排列桌面图标");
    }

    public void MoveItemsOutOfBoxToDesktopCanvas(string sourceBoxId, IReadOnlyList<string> itemPaths)
    {
        var sourceWin = _windows.FirstOrDefault(w => w.BoxId == sourceBoxId);
        if (sourceWin != null)
        {
            foreach (var p in itemPaths)
            {
                sourceWin.RemoveItemPath(p);
            }
            sourceWin.RefreshItems();
        }
        RefreshUnorganizedBoxes();
        ConfigService.SaveDebounced(Config);
    }

    public void CreateBoxWithItems(IReadOnlyList<string> itemPaths)
    {
        if (itemPaths.Count == 0)
            return;

        CreateNewBox();
        var newest = _windows.LastOrDefault();
        if (newest != null)
        {
            newest.AddDroppedFiles(itemPaths);
            RefreshAllDesktopCanvases();
        }
    }

    public void AddItemsToBox(string targetBoxId, IReadOnlyList<string> itemPaths)
    {
        var targetWin = _windows.FirstOrDefault(w => w.BoxId == targetBoxId);
        if (targetWin == null || itemPaths.Count == 0)
            return;

        targetWin.AddDroppedFiles(itemPaths);
        RefreshAllDesktopCanvases();
    }

    public void SyncDesktopHiddenIcons(bool forceShellRefreshFirst = false)
    {
        // 新架构不再设置任何 Hidden 文件属性，直接同步刷新桌面画布即可
        RefreshAllDesktopCanvases();
    }

    public void RemovePathFromOtherBoxes(string path, string excludeBoxId, bool syncDesktopImmediately = true)
    {
        bool changed = false;
        foreach (var w in _windows)
        {
            if (w.BoxId == excludeBoxId || w.Config.IsUnorganizedBox)
                continue;

            var removed = w.Config.Items.RemoveAll(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                w.RefreshItems();
                changed = true;
            }
        }

        if (changed)
        {
            ConfigService.SaveDebounced(Config);
            if (syncDesktopImmediately)
            {
                RefreshUnorganizedBoxes();
                SyncDesktopHiddenIcons(forceShellRefreshFirst: true);
            }
        }
    }

    /// <summary>
    /// 启动迁移恢复：若旧版本 Storage 目录留有文件，全部无损移回用户桌面并保留在对应格子引用列表中。
    /// </summary>
    private void MigrateLegacyStorageBackToDesktop()
    {
        if (StorageService.RestoreLegacyStorageToDesktopAndConfig(Config))
        {
            ConfigService.SaveImmediate(Config);
        }
    }

    public void RestoreAllBoxesToDesktop()
    {
        StorageService.RestoreLegacyStorageToDesktopAndConfig(Config);
        foreach (var box in Config.Boxes)
        {
            if (!string.IsNullOrWhiteSpace(box.MappedFolderPath) || box.IsUnorganizedBox)
                continue;

            box.Items.Clear();
            box.CustomOrder.Clear();
        }
        RestoreAllSystemDesktopIcons();
        IconFilterService.RestoreAllHiddenDesktopIcons(Config);
        foreach (var w in _windows)
        {
            w.RefreshItems();
        }
        ConfigService.SaveImmediate(Config);
    }

    public void HideSystemDesktopIconIfOnDesktop(string shellPath, bool refreshImmediately = true)
    {
        var hideGuid = ShellService.ResolveHideDesktopIconGuid(shellPath);
        if (hideGuid == null)
            return;

        if (!ShellService.IsSystemDesktopIconCurrentlyHidden(hideGuid))
        {
            if (ShellService.SetSystemDesktopIconHidden(hideGuid, hidden: true, HostService, refreshImmediately))
            {
                Config.HiddenSystemDesktopGuids.Add(hideGuid);
                ConfigService.SaveDebounced(Config);
            }
        }
    }

    public void RestoreSystemDesktopIconIfOwnedByUs(string shellPath)
    {
        var hideGuid = ShellService.ResolveHideDesktopIconGuid(shellPath);
        if (hideGuid == null || !Config.HiddenSystemDesktopGuids.Contains(hideGuid))
            return;

        // 如果还有其他格子保留着同一个系统图标，先不恢复桌面显示
        bool stillInAnyBox = Config.Boxes.Any(b => b.Items.Any(i =>
            string.Equals(ShellService.ResolveHideDesktopIconGuid(i.Path), hideGuid, StringComparison.OrdinalIgnoreCase)));
        if (stillInAnyBox)
            return;

        ShellService.SetSystemDesktopIconHidden(hideGuid, hidden: false, HostService);
        Config.HiddenSystemDesktopGuids.Remove(hideGuid);
        ConfigService.SaveDebounced(Config);
    }

    private void RestoreAllSystemDesktopIcons(bool refreshImmediately = true)
    {
        foreach (var guid in Config.HiddenSystemDesktopGuids.ToList())
        {
            ShellService.SetSystemDesktopIconHidden(guid, hidden: false, HostService, refreshImmediately);
        }
        Config.HiddenSystemDesktopGuids.Clear();
    }

    public void RemoveBox(string boxId)
    {
        var win = _windows.FirstOrDefault(w => w.BoxId == boxId);
        if (win != null)
        {
            _windows.Remove(win);
            win.Close();
        }

        var removedShellPaths = Config.Boxes
            .FirstOrDefault(b => b.Id == boxId)?.Items
            .Select(i => i.Path)
            .ToList() ?? [];

        Config.Boxes.RemoveAll(b => b.Id == boxId);
        foreach (var snap in Config.LayoutSnapshots.Values)
        {
            snap.Placements.Remove(boxId);
        }

        foreach (var sp in removedShellPaths)
            RestoreSystemDesktopIconIfOwnedByUs(sp);

        RefreshUnorganizedBoxes();
        SyncDesktopHiddenIcons(forceShellRefreshFirst: true);
        ConfigService.SaveImmediate(Config);
        ScheduleIdleMemoryTrim();
    }

    public void TransferItemsBetweenBoxes(string sourceBoxId, string targetBoxId, IReadOnlyList<string> itemPaths)
    {
        var sourceWin = _windows.FirstOrDefault(w => w.BoxId == sourceBoxId);
        var targetWin = _windows.FirstOrDefault(w => w.BoxId == targetBoxId);
        if (sourceWin == null || targetWin == null)
            return;

        if (targetWin.Config.IsUnorganizedBox)
        {
            foreach (var path in itemPaths)
            {
                sourceWin.RemoveItemPath(path);
            }
        }
        else
        {
            foreach (var path in itemPaths)
            {
                if (!sourceWin.Config.IsUnorganizedBox)
                {
                    sourceWin.RemoveItemPath(path);
                }
                targetWin.AddDroppedFiles([path]);
            }
        }

        sourceWin.RefreshItems();
        targetWin.RefreshItems();
        RefreshUnorganizedBoxes();
        SyncDesktopHiddenIcons(forceShellRefreshFirst: targetWin.Config.IsUnorganizedBox);
    }

    public readonly record struct PureDesktopTransition(
        bool TargetBoxesHidden,
        bool? SetNativeIconsVisible,
        bool NextPureDesktopNativeIconsHiddenByUs);

    public static PureDesktopTransition ComputePureDesktopTransition(
        bool targetHidden,
        bool alwaysHideNativeIcons,
        bool currentlyNativeIconsVisible,
        bool pureDesktopNativeIconsHiddenByUs)
    {
        if (targetHidden)
        {
            var shouldHideNativeNow = !alwaysHideNativeIcons && currentlyNativeIconsVisible;
            bool? nativeAction = (alwaysHideNativeIcons || shouldHideNativeNow) && currentlyNativeIconsVisible
                ? false
                : null;
            return new PureDesktopTransition(
                TargetBoxesHidden: true,
                SetNativeIconsVisible: nativeAction,
                NextPureDesktopNativeIconsHiddenByUs: pureDesktopNativeIconsHiddenByUs || shouldHideNativeNow);
        }

        bool? restoreNative = pureDesktopNativeIconsHiddenByUs && !alwaysHideNativeIcons
            ? true
            : null;
        return new PureDesktopTransition(
            TargetBoxesHidden: false,
            SetNativeIconsVisible: restoreNative,
            NextPureDesktopNativeIconsHiddenByUs: false);
    }

    public void SetPureDesktopHidden(bool hidden)
    {
        _pureDesktopRestoreCts?.Cancel();
        _pureDesktopRestoreCts?.Dispose();
        _pureDesktopRestoreCts = null;

        Config.Tidy.BoxesHidden = hidden;

        if (hidden)
        {
            HideAlignmentGuides();
            HideDesktopCanvases();
            foreach (var w in _windows)
            {
                w.Hide();
            }
            if (!_isSandbox)
            {
                HostService.SetNativeDesktopIconsVisible(false);
            }
        }
        else
        {
            var monitors = TopologyService.GetConnectedMonitors();
            if (!_isSandbox)
            {
                HostService.SetNativeDesktopIconsVisible(false);
                _nativeIconsHiddenByUs = true;
            }

            if (!Config.Tidy.HideNativeDesktopIcons)
            {
                SyncDesktopCanvasesForMonitors(monitors, show: true);
                RefreshAllDesktopCanvases();
            }

            ShowAllBoxWindows();
            EnsureCanvasesAtBottom();
        }

        ConfigService.SaveDebounced(Config);
        ScheduleIdleMemoryTrim(delayMs: 1200);
    }

    private void ShowAllBoxWindows()
    {
        foreach (var w in _windows)
        {
            w.Show();
        }
        EnsureCanvasesAtBottom();
    }

    private async Task WaitAndShowBoxesAsync(int delayMs, CancellationToken token)
    {
        try
        {
            await Task.Delay(delayMs, token);
            if (!token.IsCancellationRequested && !Config.Tidy.BoxesHidden)
            {
                ShowAllBoxWindows();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void SetTrayIconHidden(bool hidden)
    {
        Config.General.HideTrayIcon = hidden;
        _trayService?.SetVisible(!hidden);
        ConfigService.SaveDebounced(Config);
    }

    public void SetTidyPaused(bool paused)
    {
        if (Config.Tidy.IsTidyPaused == paused)
            return;

        Config.Tidy.IsTidyPaused = paused;
        if (paused)
        {
            HideAlignmentGuides();
            _appearanceWindow?.Close();
            HideDesktopCanvases();
            foreach (var w in _windows)
            {
                w.Hide();
            }
            if (!_isSandbox)
            {
                HostService.SetNativeDesktopIconsVisible(true);
            }
        }
        else
        {
            Config.Tidy.BoxesHidden = false;
            _pureDesktopNativeIconsHiddenByUs = false;
            var monitors = TopologyService.GetConnectedMonitors();

            if (!_isSandbox)
            {
                HostService.SetNativeDesktopIconsVisible(false);
                _nativeIconsHiddenByUs = true;
            }

            if (!Config.Tidy.HideNativeDesktopIcons)
            {
                SyncDesktopCanvasesForMonitors(monitors, show: true);
                RefreshAllDesktopCanvases();
            }

            foreach (var w in _windows)
            {
                w.Show();
                w.RefreshItems();
            }
            EnsureCanvasesAtBottom();
        }

        if (_trayToggleTidyItem != null)
        {
            _trayToggleTidyItem.Header = DesktopHostService.GetToggleTidyMenuText(paused);
        }
        if (!_isSandbox)
        {
            DesktopHostService.RegisterOrUpdateDesktopContextMenu(paused);
        }
        ConfigService.SaveImmediate(Config);
        ScheduleIdleMemoryTrim(delayMs: 1200);
    }

    public void ToggleTidyPaused() => SetTidyPaused(!Config.Tidy.IsTidyPaused);

    private async Task ListenIpcPipeAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && !_isDisposed)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    IpcPipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(token);
                int cmd = server.ReadByte();
                if (cmd > 0)
                {
                    await _dispatcher.InvokeAsync(() => HandleIpcCommand(cmd));
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(100, token).ConfigureAwait(false);
            }
        }
    }

    private void ScheduleDesktopIconPositionsRestore()
    {
        if (Config.SavedDesktopIconPositions.Count == 0)
            return;

        IconFilterService.RestoreDesktopIconPositions(Config.SavedDesktopIconPositions);
        _ = RunDesktopIconRestoreBeatsAsync();
    }

    private async Task RunDesktopIconRestoreBeatsAsync()
    {
        foreach (var delay in new[] { 80, 140, 230, 350 })
        {
            await Task.Delay(delay).ConfigureAwait(false);
            if (_isDisposed)
                return;

            await _dispatcher.InvokeAsync(() =>
            {
                if (!_isDisposed && Config.SavedDesktopIconPositions.Count > 0)
                {
                    IconFilterService.RestoreDesktopIconPositions(Config.SavedDesktopIconPositions);
                }
            });
        }
    }

    public void HandleIpcCommand(int command)
    {
        switch (command)
        {
            case IpcCmdNewBox:
                CreateNewBox();
                break;
            case IpcCmdToggleTidy:
                ToggleTidyPaused();
                break;
            case IpcCmdShowSettings:
                ShowSettingsWindow();
                break;
            case IpcCmdShowAppearance:
                ShowBoxAppearanceWindow();
                break;
        }
    }

    public void ToggleAllBoxesVisibility()
    {
        if (Config.Tidy.IsTidyPaused)
            return;
        SetPureDesktopHidden(!Config.Tidy.BoxesHidden);
    }

    public void SetHideNativeDesktopIconsSetting(bool hideIcons)
    {
        if (Config.Tidy.HideNativeDesktopIcons == hideIcons)
            return;

        Config.Tidy.HideNativeDesktopIcons = hideIcons;
        if (hideIcons)
        {
            HideDesktopCanvases();
        }
        else if (!Config.Tidy.IsTidyPaused && !Config.Tidy.BoxesHidden)
        {
            var monitors = TopologyService.GetConnectedMonitors();
            SyncDesktopCanvasesForMonitors(monitors, show: true);
            RefreshAllDesktopCanvases();
            EnsureCanvasesAtBottom();
        }
        ConfigService.SaveDebounced(Config);
    }

    public void ToggleCollapseAllBoxes()
    {
        var anyExpanded = _windows.Any(w => !w.Config.IsCollapsed);
        var targetCollapsed = anyExpanded;
        var monitors = TopologyService.GetConnectedMonitors();

        foreach (var w in _windows)
        {
            w.Config.IsCollapsed = targetCollapsed;
            var placement = ResolveInitialPlacementForBox(w.Config, monitors);
            placement.IsCollapsed = targetCollapsed;
            var (expandedRect, headerH, _, _) = MonitorTopologyService.ResolvePhysicalPlacement(placement, monitors);
            w.ApplyPhysicalPlacement(expandedRect, headerH);
        }

        RecordCurrentPlacementsAndSave();
    }

    public void ApplyAppearanceToAllBoxes()
    {
        var monitors = TopologyService.GetConnectedMonitors();
        foreach (var w in _windows)
        {
            w.UpdateMonitorsCache(monitors);
            w.UpdateAppearance();
            w.UpdateViewModeVisual();
        }
    }

    public void ApplyBoxAppearanceToAllAndDefault(BoxConfig source)
    {
        source.CopyAppearanceToDefaults(Config.Tidy);
        foreach (var box in Config.Boxes)
        {
            if (!ReferenceEquals(box, source))
            {
                box.CopyAppearanceFrom(source);
            }
        }
        ApplyAppearanceToAllBoxes();
        ConfigService.SaveImmediate(Config);
    }

    public void ShowBoxAppearanceWindow(DeskBoxWindow? target = null)
    {
        var resolvedTarget = target ?? _windows.FirstOrDefault();
        if (resolvedTarget == null)
            return;

        if (_appearanceWindow == null || !_appearanceWindow.IsLoaded)
        {
            _appearanceWindow = new BoxAppearanceWindow(this, resolvedTarget);
            _appearanceWindow.Closed += (_, _) =>
            {
                _appearanceWindow = null;
                ScheduleIdleMemoryTrim(delayMs: 1000);
            };
            _appearanceWindow.Show();
        }
        else
        {
            _appearanceWindow.BindTarget(resolvedTarget);
            if (_appearanceWindow.WindowState == WindowState.Minimized)
                _appearanceWindow.WindowState = WindowState.Normal;
            _appearanceWindow.Activate();
        }
    }

    public void ShowSettingsWindow()
    {
        if (_settingsWindow == null || !_settingsWindow.IsLoaded)
        {
            _settingsWindow = new SettingsWindow(this);
            _settingsWindow.Closed += (_, _) =>
            {
                _settingsWindow = null;
                ScheduleIdleMemoryTrim(delayMs: 1000);
            };
            _settingsWindow.Show();
        }
        else
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
        }
    }

    public void RegisterGlobalToggleHotkey()
    {
        if (_hotkeyHost == null)
            return;

        _hotkeyHost.Unregister();
        var text = Config.General.ToggleHotkey;
        if (string.IsNullOrWhiteSpace(text) || text == "无")
            return;

        uint mods = 0;
        uint vk = 0;
        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) mods |= 0x0001;
            else if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) mods |= 0x0002;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) mods |= 0x0004;
            else if (part.Equals("Space", StringComparison.OrdinalIgnoreCase)) vk = 0x20;
            else if (part.Length == 1) vk = char.ToUpperInvariant(part[0]);
        }

        if (vk != 0)
        {
            _hotkeyHost.Register(mods, vk);
        }
    }

    public void TrimMemory()
    {
        ShellService.ClearIconCache();
        foreach (var w in _windows)
        {
            w.RefreshItems();
        }
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        using var proc = Process.GetCurrentProcess();
        NativeMethods.EmptyWorkingSet(proc.Handle);
    }

    private void InitTrayIcon()
    {
        var menu = new ContextMenu();

        var toggleItem = new MenuItem { Header = "隐藏 / 显示全部图标（纯净桌面）" };
        toggleItem.Click += (_, _) => ToggleAllBoxesVisibility();
        menu.Items.Add(toggleItem);

        _trayToggleTidyItem = new MenuItem { Header = DesktopHostService.GetToggleTidyMenuText(Config.Tidy.IsTidyPaused) };
        _trayToggleTidyItem.Click += (_, _) => ToggleTidyPaused();
        menu.Items.Add(_trayToggleTidyItem);

        var newItem = new MenuItem { Header = "新建桌面格子" };
        newItem.Click += (_, _) => CreateNewBox();
        menu.Items.Add(newItem);

        var unorganizedItem = new MenuItem { Header = "显示「桌面未整理」动态格子" };
        unorganizedItem.Click += (_, _) => CreateUnorganizedDesktopBox();
        menu.Items.Add(unorganizedItem);

        var collapseAllItem = new MenuItem { Header = "全部展开 / 折叠" };
        collapseAllItem.Click += (_, _) => ToggleCollapseAllBoxes();
        menu.Items.Add(collapseAllItem);

        menu.Items.Add(new Separator());

        var snapshotItem = new MenuItem { Header = "保存当前多屏布局快照" };
        snapshotItem.Click += (_, _) => RecordCurrentPlacementsAndSave(immediate: true);
        menu.Items.Add(snapshotItem);

        var settingsItem = new MenuItem { Header = "打开设置中心..." };
        settingsItem.Click += (_, _) => ShowSettingsWindow();
        menu.Items.Add(settingsItem);

        menu.Items.Add(new Separator());

        var exitItem = new MenuItem { Header = "退出 DeskBox" };
        exitItem.Click += (_, _) =>
        {
            Dispose();
            Application.Current.Shutdown();
        };
        menu.Items.Add(exitItem);

        _trayService = new TrayIconService(
            "DeskBox 轻量桌面格子",
            menu,
            onLeftClick: ToggleAllBoxesVisibility,
            onDoubleClick: ShowSettingsWindow,
            initiallyVisible: !Config.General.HideTrayIcon);
    }

    private void OnSessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        Dispose();
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;
        _isDisposed = true;

        if (Application.Current != null)
        {
            Application.Current.SessionEnding -= OnSessionEnding;
        }
        SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
        _displayChangeDebounce.Stop();
        _idleTrimTimer.Stop();
        _desktopSyncDebounce.Stop();
        foreach (var watcher in _desktopWatchers)
            watcher.Dispose();
        _desktopWatchers.Clear();
        _pureDesktopRestoreCts?.Cancel();
        _pureDesktopRestoreCts?.Dispose();
        _pureDesktopRestoreCts = null;
        _ipcCts?.Cancel();
        _ipcCts?.Dispose();
        _ipcCts = null;
        _mouseHook?.Dispose();
        _hotkeyHost?.Dispose();
        WallpaperBlurService.Dispose();

        IconFilterService.RestoreAllHiddenDesktopIcons(Config);

        foreach (var canvas in _canvasWindows.ToList())
        {
            canvas.Close();
        }
        _canvasWindows.Clear();

        if (!_isSandbox && (_nativeIconsHiddenByUs || _pureDesktopNativeIconsHiddenByUs || !Config.Tidy.IsTidyPaused))
        {
            HostService.SetNativeDesktopIconsVisible(true);
        }

        RecordCurrentPlacementsAndSave(immediate: true, propagateSameMonitor: false);
        _appearanceWindow?.Close();
        _guideOverlay?.Close();
        _trayService?.Dispose();

        foreach (var w in _windows.ToList())
        {
            w.Close();
        }
        _windows.Clear();
    }

    private sealed class HotkeyHostWindow : IDisposable
    {
        private readonly int _id;
        private readonly Action _callback;
        private readonly Action<int>? _ipcCallback;
        private readonly HwndSource _hwndSource;

        public HotkeyHostWindow(int id, Action callback, Action<int>? ipcCallback = null)
        {
            _id = id;
            _callback = callback;
            _ipcCallback = ipcCallback;
            _hwndSource = new HwndSource(new HwndSourceParameters(IpcHostWindowName)
            {
                Width = 0,
                Height = 0,
                WindowStyle = unchecked((int)0x80000000) // WS_POPUP (hidden top-level message host, FindWindow-able)
            });
            _hwndSource.AddHook(WndProc);
        }

        public void Register(uint modifiers, uint vk)
        {
            NativeMethods.RegisterHotKey(_hwndSource.Handle, _id, modifiers, vk);
        }

        public void Unregister()
        {
            NativeMethods.UnregisterHotKey(_hwndSource.Handle, _id);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == _id)
            {
                _callback();
                handled = true;
            }
            else if (msg == WmAppCommand)
            {
                _ipcCallback?.Invoke(wParam.ToInt32());
                handled = true;
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            Unregister();
            _hwndSource.RemoveHook(WndProc);
            _hwndSource.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr hProcess, int processInformationClass, IntPtr processInformation, int processInformationSize);
}
