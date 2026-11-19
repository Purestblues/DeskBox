using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeskBox.Interop;
using DeskBox.Models;
using DeskBox.Services;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using ContextMenu = System.Windows.Controls.ContextMenu;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using Geometry = System.Windows.Media.Geometry;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;

namespace DeskBox.Views;

public partial class DeskBoxWindow : Window
{
    private const string InternalDragFormat = "DeskBoxItemTransfer";
    private const string ShellIdListFormat = "Shell IDList Array";

    private static readonly Geometry ChevronUpGeometry = Geometry.Parse("M 1.5,8 L 6,3.5 L 10.5,8");
    private static readonly Geometry ChevronDownGeometry = Geometry.Parse("M 1.5,4 L 6,8.5 L 10.5,4");
    private static readonly Geometry UnlockedShackleGeometry = Geometry.Parse("M 4.5,6 V 4.3 A 2.5,2.5 0 0 1 9.5,4.3 V 5");
    private static readonly Geometry LockedShackleGeometry = Geometry.Parse("M 4.5,6 V 4.3 A 2.5,2.5 0 0 1 9.5,4.3 V 6");
    private static readonly Geometry GridViewGeometry = Geometry.Parse("M 1,1 H 5 V 5 H 1 Z M 7,1 H 11 V 5 H 7 Z M 1,7 H 5 V 11 H 1 Z M 7,7 H 11 V 11 H 7 Z");
    private static readonly Geometry ListViewGeometry = Geometry.Parse("M 1.5,2.5 H 10.5 M 1.5,6 H 10.5 M 1.5,9.5 H 10.5");
    private static readonly DropShadowEffect DarkTextShadowEffect = CreateFrozenShadow(Colors.Black, 0.85);
    private static readonly DropShadowEffect LightTextShadowEffect = CreateFrozenShadow(Colors.White, 0.72);

    private static DropShadowEffect CreateFrozenShadow(Color color, double opacity)
    {
        var effect = new DropShadowEffect
        {
            Color = color,
            BlurRadius = 3.5,
            ShadowDepth = 1.0,
            Direction = 315,
            Opacity = opacity
        };
        effect.Freeze();
        return effect;
    }

    public static readonly DependencyProperty GridCellWidthProperty = DependencyProperty.Register(
        nameof(GridCellWidth),
        typeof(double),
        typeof(DeskBoxWindow),
        new PropertyMetadata(74.0));

    public static readonly DependencyProperty ItemTextShadowEffectProperty = DependencyProperty.Register(
        nameof(ItemTextShadowEffect),
        typeof(Effect),
        typeof(DeskBoxWindow),
        new PropertyMetadata(null));

    public double GridCellWidth
    {
        get => (double)GetValue(GridCellWidthProperty);
        set => SetValue(GridCellWidthProperty, value);
    }

    public Effect? ItemTextShadowEffect
    {
        get => (Effect?)GetValue(ItemTextShadowEffectProperty);
        set => SetValue(ItemTextShadowEffectProperty, value);
    }

    private readonly BoxConfig _config;
    private readonly ShellService _shellService;
    private readonly DesktopHostService _hostService;
    private readonly BoxManager _manager;

    private readonly DispatcherTimer _hoverExpandTimer;
    private readonly DispatcherTimer _hoverCollapseTimer;
    private readonly DispatcherTimer _dragExpandTimer;
    private readonly DispatcherTimer _watcherDebounceTimer;
    private FileSystemWatcher? _storageWatcher;

    private bool _isHoverExpanded;
    private bool _isPopupOpen;
    private bool _isRenaming;
    private bool _isDraggingWindow;
    private bool _isResizing;
    private NativeMethods.POINT _dragStartCursor;
    private NativeMethods.RECT _dragStartRect;
    private NativeMethods.POINT _resizeStartCursor;
    private NativeMethods.RECT _resizeStartRect;
    private int _expandedPhysicalHeight = 420;
    private int _collapsedPhysicalHeight = 34;
    private MonitorTopologyService.PhysicalRect _lastPhysicalRect;

    private Point _itemDragStartPoint;
    private BoxViewItem? _itemDragCandidate;
    private enum ItemDropTargetKind
    {
        None,
        Folder,
        Application,
        RecycleBin
    }

    private DragPreviewWindow? _dragPreviewWindow;
    private int _pendingInsertIndex = -1;
    private string? _pendingTargetItemPath;
    private ItemDropTargetKind _pendingTargetKind;
    private bool _isMarqueeSelecting;
    private Point _marqueeStartPoint;
    private HashSet<BoxViewItem> _marqueeInitialSelection = new();
    private IReadOnlyList<MonitorDescriptor>? _cachedMonitors;
    private ImageBrush? _backdropBrush;
    private BitmapSource? _lastBackdropBitmap;

    public string BoxId => _config.Id;
    public BoxConfig Config => _config;
    public bool IsHoverExpanded => _isHoverExpanded;

    public DeskBoxWindow(
        BoxConfig config,
        ShellService shellService,
        DesktopHostService hostService,
        BoxManager manager)
    {
        InitializeComponent();
        _config = config;
        _shellService = shellService;
        _hostService = hostService;
        _manager = manager;

        _hoverExpandTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _hoverExpandTimer.Tick += (_, _) =>
        {
            _hoverExpandTimer.Stop();
            if (_config.IsCollapsed && _manager.Config.Tidy.AutoExpandOnHover && !_isHoverExpanded)
            {
                _isHoverExpanded = true;
                ApplyCollapseVisualState();
            }
        };

        _hoverCollapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(35) };
        _hoverCollapseTimer.Tick += (_, _) =>
        {
            _hoverCollapseTimer.Stop();
            if (_config.IsCollapsed && _isHoverExpanded && !_isPopupOpen && !_isRenaming && !_isDraggingWindow && !_isResizing)
            {
                NativeMethods.GetCursorPos(out var pt);
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(hwnd, out var rect))
                {
                    if (pt.X >= rect.Left && pt.X <= rect.Right && pt.Y >= rect.Top && pt.Y <= rect.Bottom)
                        return;
                }

                _isHoverExpanded = false;
                ApplyCollapseVisualState();
            }
        };

        _dragExpandTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _dragExpandTimer.Tick += (_, _) =>
        {
            _dragExpandTimer.Stop();
            if (_config.IsCollapsed && !_isHoverExpanded)
            {
                _isHoverExpanded = true;
                ApplyCollapseVisualState();
            }
        };

        _watcherDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _watcherDebounceTimer.Tick += (_, _) =>
        {
            _watcherDebounceTimer.Stop();
            RefreshItems();
        };

        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) =>
        {
            _isDraggingWindow = false;
            _isResizing = false;
            _storageWatcher?.Dispose();
            _dragExpandTimer.Stop();
            _watcherDebounceTimer.Stop();
        };
        MouseEnter += (_, _) =>
        {
            _hoverCollapseTimer.Stop();
            UpdateHeaderButtonsFadeState();
            if (_config.IsCollapsed && _manager.Config.Tidy.AutoExpandOnHover && !_isHoverExpanded)
            {
                _hoverExpandTimer.Stop();
                _isHoverExpanded = true;
                ApplyCollapseVisualState();
            }
        };
        MouseLeave += (_, _) =>
        {
            _hoverExpandTimer.Stop();
            UpdateHeaderButtonsFadeState();
            if (_config.IsCollapsed && _isHoverExpanded && !_isPopupOpen && !_isRenaming && !_isDraggingWindow && !_isResizing)
            {
                _hoverCollapseTimer.Start();
            }
        };

        DragEnter += OnWindowDragEnter;
        DragOver += OnWindowDragOver;
        DragLeave += OnWindowDragLeave;
        Drop += OnWindowDrop;
        ItemsListBox.SizeChanged += (_, _) => UpdateGridCellWidth();

        SetupStorageWatcher();
        ApplyConfigToUi();
    }

    private void SetupStorageWatcher()
    {
        _storageWatcher?.Dispose();
        _storageWatcher = null;
        if (string.IsNullOrWhiteSpace(_config.MappedFolderPath) || !Directory.Exists(_config.MappedFolderPath))
            return;

        try
        {
            _storageWatcher = new FileSystemWatcher(_config.MappedFolderPath)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };
            FileSystemEventHandler onChanged = (_, _) => Dispatcher.BeginInvoke(() =>
            {
                _watcherDebounceTimer.Stop();
                _watcherDebounceTimer.Start();
            });
            RenamedEventHandler onRenamed = (_, _) => Dispatcher.BeginInvoke(() =>
            {
                _watcherDebounceTimer.Stop();
                _watcherDebounceTimer.Start();
            });
            _storageWatcher.Created += onChanged;
            _storageWatcher.Deleted += onChanged;
            _storageWatcher.Renamed += onRenamed;
        }
        catch
        {
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(WndProcHook);

        var tint = ParseThemeColor();
        var useFrosted = _config.UseFrostedGlass ?? _manager.Config.Tidy.EnableFrostedGlass;
        _hostService.AttachDesktopWidgetBehavior(
            this,
            useFrosted,
            tint,
            _config.Opacity,
            allowBringForward: () => _manager.IsSandbox || _isHoverExpanded || _isPopupOpen);

        UpdateMonitorsCache(_manager.TopologyService.GetConnectedMonitors());
        UpdateAppearance();
    }

    public void UpdateMonitorsCache(IReadOnlyList<MonitorDescriptor> monitors)
    {
        _cachedMonitors = monitors;
    }

    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_DPICHANGED)
        {
            if (_isDraggingWindow || _isResizing)
            {
                handled = true;
                return IntPtr.Zero;
            }
            Dispatcher.BeginInvoke(() => _manager.ReapplyCurrentTopologyLayout());
        }
        return IntPtr.Zero;
    }

    private Color ParseThemeColor()
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(_config.ThemeColor);
        }
        catch
        {
            return Color.FromRgb(32, 34, 38);
        }
    }

    public void ApplyConfigToUi()
    {
        Title = _config.Title;
        TitleTextBlock.Text = _config.Title;
        UpdateAppearance();
        UpdateLockVisual();
        UpdateViewModeVisual();
        RefreshItems();
    }

    public void UpdateAppearance()
    {
        var baseColor = ParseThemeColor();
        // 最低 alpha = 1，确保 Opacity 为 0% 时视觉全透明，同时保留 WPF 命中测试能力以便右键与框选
        var alpha = (byte)Math.Clamp((int)Math.Round(_config.Opacity * 255), 1, 245);
        RootBorder.Background = new SolidColorBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));

        var corner = Math.Clamp(_config.CornerRadius, 0.0, 20.0);
        var radius = new CornerRadius(corner);
        BlurBackdropBorder.CornerRadius = radius;
        RootBorder.CornerRadius = radius;

        // 浅色主题自动切换深色字体（包括标题栏、项目列表文字、空状态提示）
        var luminance = (0.299 * baseColor.R + 0.587 * baseColor.G + 0.114 * baseColor.B) / 255.0;
        var isLight = luminance > 0.65;

        switch (_config.BorderStyle)
        {
            case BoxBorderStyle.None:
                RootBorder.BorderThickness = new Thickness(0);
                RootBorder.BorderBrush = Brushes.Transparent;
                break;
            case BoxBorderStyle.Accent:
                RootBorder.BorderThickness = new Thickness(1.5);
                var accentR = (byte)Math.Min(255, baseColor.R + (isLight ? -40 : 75));
                var accentG = (byte)Math.Min(255, baseColor.G + (isLight ? -40 : 75));
                var accentB = (byte)Math.Min(255, baseColor.B + (isLight ? -40 : 75));
                RootBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(200, accentR, accentG, accentB));
                break;
            default:
                RootBorder.BorderThickness = new Thickness(1);
                RootBorder.BorderBrush = new SolidColorBrush(isLight ? Color.FromArgb(40, 0, 0, 0) : Color.FromArgb(30, 255, 255, 255));
                break;
        }

        var fg = isLight ? Color.FromRgb(26, 28, 32) : Color.FromArgb(245, 255, 255, 255);
        var btnFg = isLight ? Color.FromArgb(220, 32, 35, 40) : Color.FromArgb(225, 255, 255, 255);
        var itemFg = isLight ? Color.FromRgb(28, 30, 35) : Color.FromArgb(248, 255, 255, 255);
        var hintFg = isLight ? Color.FromArgb(150, 45, 48, 54) : Color.FromArgb(155, 255, 255, 255);

        var fgBrush = new SolidColorBrush(fg);
        var btnBrush = new SolidColorBrush(btnFg);
        ItemsListBox.Foreground = new SolidColorBrush(itemFg);
        EmptyHintText.Foreground = new SolidColorBrush(hintFg);

        TitleTextBlock.Foreground = fgBrush;
        TitleTextBlock.HorizontalAlignment = _config.TitleAlignment == BoxTitleAlignment.Left
            ? System.Windows.HorizontalAlignment.Left
            : System.Windows.HorizontalAlignment.Center;
        TitleTextBlock.Margin = _config.TitleAlignment == BoxTitleAlignment.Left
            ? new Thickness(6, 0, 4, 0)
            : new Thickness(0);
        TitleTextBlock.FontSize = _config.FontSizeTier switch
        {
            BoxSizeTier.Small => 11.5,
            BoxSizeTier.Large => 13.5,
            _ => 12.5
        };

        CollapseButton.Foreground = btnBrush;
        if (!_config.IsLocked)
            LockButton.Foreground = btnBrush;
        ViewModeButton.Foreground = btnBrush;
        SortButton.Foreground = btnBrush;
        MenuButton.Foreground = btnBrush;

        HeaderGrid.Background = Brushes.Transparent;
        if (_config.HeaderBandVisible)
        {
            var bandColor = isLight
                ? Color.FromArgb(48, 0, 0, 0)
                : Color.FromArgb(72, 0, 0, 0);
            HeaderBackgroundBorder.Background = new SolidColorBrush(bandColor);
            var showSep = !_config.IsCollapsed || _isHoverExpanded;
            HeaderSeparator.Visibility = showSep ? Visibility.Visible : Visibility.Collapsed;
            HeaderSeparator.Background = new SolidColorBrush(isLight
                ? Color.FromArgb(28, 0, 0, 0)
                : Color.FromArgb(30, 255, 255, 255));
            HeaderBackgroundBorder.CornerRadius = showSep
                ? new CornerRadius(corner, corner, 0, 0)
                : radius;
        }
        else
        {
            HeaderBackgroundBorder.Background = Brushes.Transparent;
            HeaderSeparator.Visibility = Visibility.Collapsed;
        }

        var shadow = _config.EnableTextShadow
            ? (isLight ? LightTextShadowEffect : DarkTextShadowEffect)
            : null;
        TitleTextBlock.Effect = shadow;
        ItemTextShadowEffect = shadow;

        UpdateHeaderButtonsFadeState();
        UpdateBackdropCrop();
    }

    private void UpdateHeaderButtonsFadeState()
    {
        bool hideButtons = _config.AutoFadeHeaderButtons && !IsMouseOver && !_isPopupOpen && !_isRenaming;
        double targetOpacity = hideButtons ? 0.0 : 1.0;
        LeftHeaderButtonsPanel.Opacity = targetOpacity;
        RightHeaderButtonsPanel.Opacity = targetOpacity;
    }

    public void UpdateBackdropCrop()
    {
        var useFrosted = _config.UseFrostedGlass ?? _manager.Config.Tidy.EnableFrostedGlass;
        if (!useFrosted)
        {
            BlurBackdropBorder.Background = Brushes.Transparent;
            return;
        }

        _cachedMonitors ??= _manager.TopologyService.GetConnectedMonitors();
        if (_cachedMonitors.Count == 0)
        {
            BlurBackdropBorder.Background = Brushes.Transparent;
            return;
        }

        var (rect, _) = GetPhysicalGeometry();
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        var owner = MonitorTopologyService.FindOwnerMonitor(rect, _cachedMonitors);
        var blurred = _manager.WallpaperBlurService.GetBlurredWallpaperForMonitor(owner);
        if (blurred == null)
        {
            BlurBackdropBorder.Background = Brushes.Transparent;
            return;
        }

        var u = (rect.X - owner.BoundsX) / (double)owner.BoundsWidth;
        var v = (rect.Y - owner.BoundsY) / (double)owner.BoundsHeight;
        var uw = rect.Width / (double)owner.BoundsWidth;
        var vh = rect.Height / (double)owner.BoundsHeight;

        if (_backdropBrush == null || !ReferenceEquals(_lastBackdropBitmap, blurred))
        {
            _lastBackdropBitmap = blurred;
            _backdropBrush = new ImageBrush(blurred)
            {
                Stretch = Stretch.Fill,
                ViewboxUnits = BrushMappingMode.RelativeToBoundingBox
            };
        }

        _backdropBrush.Viewbox = new Rect(u, v, uw, vh);
        if (!ReferenceEquals(BlurBackdropBorder.Background, _backdropBrush))
        {
            BlurBackdropBorder.Background = _backdropBrush;
        }
    }

    public void ApplyPhysicalPlacement(
        MonitorTopologyService.PhysicalRect expandedRect,
        int collapsedHeaderHeight)
    {
        _expandedPhysicalHeight = expandedRect.Height;
        _collapsedPhysicalHeight = collapsedHeaderHeight;

        var targetHeight = (_config.IsCollapsed && !_isHoverExpanded)
            ? _collapsedPhysicalHeight
            : _expandedPhysicalHeight;

        _lastPhysicalRect = new MonitorTopologyService.PhysicalRect(expandedRect.X, expandedRect.Y, expandedRect.Width, targetHeight);
        DesktopHostService.MoveWindowPhysical(this, expandedRect.X, expandedRect.Y, expandedRect.Width, targetHeight);
        UpdateCollapseVisualOnly();
        UpdateBackdropCrop();
    }

    public (MonitorTopologyService.PhysicalRect currentRect, int expandedPhysicalHeight) GetPhysicalGeometry()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero &&
            IsVisible &&
            WindowState != WindowState.Minimized &&
            NativeMethods.GetWindowRect(hwnd, out var rect) &&
            rect.Width > 0 &&
            rect.Height > 0 &&
            rect.Left > -20000 &&
            rect.Top > -20000)
        {
            _lastPhysicalRect = new MonitorTopologyService.PhysicalRect(rect.Left, rect.Top, rect.Width, rect.Height);
            var expandedH = (_config.IsCollapsed && !_isHoverExpanded)
                ? _expandedPhysicalHeight
                : rect.Height;
            return (_lastPhysicalRect, expandedH);
        }

        return (_lastPhysicalRect, _expandedPhysicalHeight);
    }

    private void UpdateCollapseVisualOnly()
    {
        var showBody = !_config.IsCollapsed || _isHoverExpanded;
        ContentHostGrid.Visibility = showBody ? Visibility.Visible : Visibility.Collapsed;
        CollapsePath.Data = _config.IsCollapsed ? ChevronDownGeometry : ChevronUpGeometry;
        if (_config.HeaderBandVisible)
        {
            var radius = new CornerRadius(Math.Clamp(_config.CornerRadius, 0.0, 20.0));
            HeaderSeparator.Visibility = showBody ? Visibility.Visible : Visibility.Collapsed;
            HeaderBackgroundBorder.CornerRadius = showBody
                ? new CornerRadius(radius.TopLeft, radius.TopRight, 0, 0)
                : radius;
        }
        else
        {
            HeaderSeparator.Visibility = Visibility.Collapsed;
        }
        UpdateResizeThumbsVisibility();
    }

    private void ApplyCollapseVisualState()
    {
        UpdateCollapseVisualOnly();

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect))
            return;

        var showBody = !_config.IsCollapsed || _isHoverExpanded;
        var targetH = showBody ? _expandedPhysicalHeight : _collapsedPhysicalHeight;
        _lastPhysicalRect = new MonitorTopologyService.PhysicalRect(rect.Left, rect.Top, rect.Width, targetH);

        NativeMethods.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            0,
            0,
            rect.Width,
            targetH,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        UpdateBackdropCrop();
    }

    private void UpdateLockVisual()
    {
        LockShacklePath.Data = _config.IsLocked ? LockedShackleGeometry : UnlockedShackleGeometry;
        if (_config.IsLocked)
        {
            LockButton.Foreground = new SolidColorBrush(Color.FromRgb(246, 200, 95));
            LockButton.Opacity = 1.0;
        }
        else
        {
            var baseColor = ParseThemeColor();
            var isLight = (0.299 * baseColor.R + 0.587 * baseColor.G + 0.114 * baseColor.B) / 255.0 > 0.65;
            LockButton.Foreground = new SolidColorBrush(isLight ? Color.FromArgb(215, 35, 38, 42) : Color.FromArgb(220, 255, 255, 255));
            LockButton.Opacity = 0.72;
        }
        UpdateResizeThumbsVisibility();
    }

    private void UpdateResizeThumbsVisibility()
    {
        var canResize = !_config.IsLocked && (!_config.IsCollapsed || _isHoverExpanded);
        var vis = canResize ? Visibility.Visible : Visibility.Collapsed;
        ResizeLeft.Visibility = vis;
        ResizeRight.Visibility = vis;
        ResizeBottom.Visibility = vis;
        ResizeBottomRight.Visibility = vis;
    }

    public void UpdateViewModeVisual()
    {
        ViewModePath.Data = _config.ViewMode == BoxViewMode.List ? GridViewGeometry : ListViewGeometry;
        UpdateGridCellWidth();
        ItemsListBox.ItemsPanel = BuildItemsPanel(_config.ViewMode);
        ItemsListBox.ItemTemplate = BuildItemTemplate(_config.ViewMode);
    }

    public static double ComputeUniformGridCellWidth(double availableWidth, BoxSizeTier iconTier = BoxSizeTier.Medium)
    {
        // 预留垂直滚动条宽度（约 8 DIP），防止临界宽度导致多折行
        double usable = Math.Max(64.0, availableWidth - 8.0);
        double idealCellWidth = iconTier switch
        {
            BoxSizeTier.Small => 58.0,
            BoxSizeTier.Large => 84.0,
            _ => 68.0
        };
        int cols = Math.Max(1, (int)Math.Floor(usable / idealCellWidth));
        return Math.Floor(usable / cols);
    }

    private void UpdateGridCellWidth()
    {
        if (_config.ViewMode != BoxViewMode.Grid)
            return;

        double avail = ItemsListBox.ActualWidth;
        if (avail <= 0)
            avail = Math.Max(120.0, Width - 10.0);

        double cellW = ComputeUniformGridCellWidth(avail, _config.IconSizeTier);
        if (Math.Abs(GridCellWidth - cellW) > 0.5)
        {
            GridCellWidth = cellW;
        }
    }

    private static ItemsPanelTemplate BuildItemsPanel(BoxViewMode mode)
    {
        if (mode == BoxViewMode.Grid)
        {
            var factory = new FrameworkElementFactory(typeof(WrapPanel));
            factory.SetValue(WrapPanel.OrientationProperty, Orientation.Horizontal);
            return new ItemsPanelTemplate(factory);
        }
        else
        {
            var factory = new FrameworkElementFactory(typeof(VirtualizingStackPanel));
            return new ItemsPanelTemplate(factory);
        }
    }

    private DataTemplate BuildItemTemplate(BoxViewMode mode)
    {
        var shadowBinding = new System.Windows.Data.Binding(nameof(ItemTextShadowEffect))
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(DeskBoxWindow), 1)
        };

        if (mode == BoxViewMode.Grid)
        {
            var iconSize = _config.IconSizeTier switch
            {
                BoxSizeTier.Small => 26.0,
                BoxSizeTier.Large => 46.0,
                _ => 34.0
            };
            var stackWidth = _config.IconSizeTier switch
            {
                BoxSizeTier.Small => 54.0,
                BoxSizeTier.Large => 78.0,
                _ => 64.0
            };
            var fontSize = _config.FontSizeTier switch
            {
                BoxSizeTier.Small => 10.0,
                BoxSizeTier.Large => 12.5,
                _ => 11.0
            };
            var lineHeight = fontSize + 2.0;
            var textMaxHeight = lineHeight * 2.0 + 2.5;

            // 大图标容器：宽度动态均分整行，内容居中对称且紧凑
            var container = new FrameworkElementFactory(typeof(Grid));
            container.SetBinding(WidthProperty, new System.Windows.Data.Binding(nameof(GridCellWidth))
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(DeskBoxWindow), 1)
            });

            var stack = new FrameworkElementFactory(typeof(StackPanel));
            stack.SetValue(WidthProperty, stackWidth);
            stack.SetValue(HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
            stack.SetValue(MarginProperty, new Thickness(2, 3, 2, 3));

            var img = new FrameworkElementFactory(typeof(System.Windows.Controls.Image));
            img.SetValue(WidthProperty, iconSize);
            img.SetValue(HeightProperty, iconSize);
            img.SetValue(MarginProperty, new Thickness(0, 1, 0, 2));
            img.SetBinding(System.Windows.Controls.Image.SourceProperty, new System.Windows.Data.Binding(nameof(BoxViewItem.Icon)));

            var txt = new FrameworkElementFactory(typeof(TextBlock));
            txt.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(BoxViewItem.DisplayName)));
            txt.SetValue(TextBlock.FontSizeProperty, fontSize);
            txt.SetValue(TextBlock.LineHeightProperty, lineHeight);
            txt.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
            txt.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
            txt.SetValue(TextBlock.MaxHeightProperty, textMaxHeight);
            txt.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            txt.SetBinding(EffectProperty, shadowBinding);

            stack.AppendChild(img);
            stack.AppendChild(txt);
            container.AppendChild(stack);
            return new DataTemplate { VisualTree = container };
        }
        else
        {
            var iconSize = _config.IconSizeTier switch
            {
                BoxSizeTier.Small => 15.0,
                BoxSizeTier.Large => 24.0,
                _ => 18.0
            };
            var fontSize = _config.FontSizeTier switch
            {
                BoxSizeTier.Small => 11.0,
                BoxSizeTier.Large => 13.5,
                _ => 12.0
            };
            var rowHeight = Math.Max(iconSize + 3.0, fontSize + 9.0);

            var grid = new FrameworkElementFactory(typeof(DockPanel));
            grid.SetValue(HeightProperty, rowHeight);
            grid.SetValue(MarginProperty, new Thickness(5, 2, 9, 2));

            var img = new FrameworkElementFactory(typeof(System.Windows.Controls.Image));
            img.SetValue(DockPanel.DockProperty, Dock.Left);
            img.SetValue(WidthProperty, iconSize);
            img.SetValue(HeightProperty, iconSize);
            img.SetValue(MarginProperty, new Thickness(2, 0, 6, 0));
            img.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            img.SetBinding(System.Windows.Controls.Image.SourceProperty, new System.Windows.Data.Binding(nameof(BoxViewItem.Icon)));

            var txt = new FrameworkElementFactory(typeof(TextBlock));
            txt.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(BoxViewItem.DisplayName)));
            txt.SetValue(TextBlock.FontSizeProperty, fontSize);
            txt.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            txt.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            txt.SetBinding(EffectProperty, shadowBinding);

            grid.AppendChild(img);
            grid.AppendChild(txt);
            return new DataTemplate { VisualTree = grid };
        }
    }

    public void RefreshItems()
    {
        IEnumerable<BoxItemEntry> entries;
        if (!string.IsNullOrWhiteSpace(_config.MappedFolderPath) && Directory.Exists(_config.MappedFolderPath))
        {
            entries = Directory.EnumerateFileSystemEntries(_config.MappedFolderPath)
                .Select(p => new BoxItemEntry { Path = p });
        }
        else if (_config.IsUnorganizedBox)
        {
            entries = _manager.StorageService.EnumerateUnorganizedDesktopEntries(_manager.Config);
        }
        else
        {
            // 非破坏性引用模式：保留原路径，绝不移动用户物理文件
            entries = _config.Items.Where(i =>
                i.Path.StartsWith("shell:::", StringComparison.OrdinalIgnoreCase) ||
                File.Exists(i.Path) ||
                Directory.Exists(i.Path));
        }

        // 过滤 Windows 系统桌面配置文件（如 desktop.ini / thumbs.db）
        entries = entries.Where(e =>
        {
            var fileName = Path.GetFileName(e.Path);
            return !fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) &&
                   !fileName.Equals("thumbs.db", StringComparison.OrdinalIgnoreCase);
        });

        var sorted = ShellService.SortItems(entries, _config.SortField, _config.SortDescending, _config.CustomOrder).ToList();
        var viewItems = sorted.Select(e => new BoxViewItem(e, _shellService.GetIcon(e.Path))).ToList();
        ItemsListBox.ItemsSource = viewItems;
        EmptyHintText.Visibility = viewItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CollapseButton_Click(object sender, RoutedEventArgs e)
    {
        _config.IsCollapsed = !_config.IsCollapsed;
        _isHoverExpanded = false;
        ApplyCollapseVisualState();
        _manager.RecordCurrentPlacementsAndSave();
    }

    private void LockButton_Click(object sender, RoutedEventArgs e)
    {
        _config.IsLocked = !_config.IsLocked;
        UpdateLockVisual();
        _manager.SaveConfigDebounced();
    }

    private void ViewModeButton_Click(object sender, RoutedEventArgs e)
    {
        _config.ViewMode = _config.ViewMode == BoxViewMode.List ? BoxViewMode.Grid : BoxViewMode.List;
        UpdateViewModeVisual();
        _manager.SaveConfigDebounced();
    }

    private void SortButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = CreateDarkContextMenu();
        AddSortItem(menu, "自定义顺序", BoxSortField.Custom);
        AddSortItem(menu, "按名称", BoxSortField.Name);
        AddSortItem(menu, "按修改时间", BoxSortField.ModifiedTime);
        AddSortItem(menu, "按类型", BoxSortField.Type);
        AddSortItem(menu, "按大小", BoxSortField.Size);
        menu.Items.Add(new Separator());
        var descItem = new MenuItem
        {
            Header = "降序排列",
            IsChecked = _config.SortDescending
        };
        descItem.Click += (_, _) =>
        {
            _config.SortDescending = !_config.SortDescending;
            RefreshItems();
            _manager.SaveConfigDebounced();
        };
        menu.Items.Add(descItem);
        ShowPopupMenu(menu, SortButton);
    }

    private void AddSortItem(ContextMenu menu, string label, BoxSortField field)
    {
        var item = new MenuItem
        {
            Header = label,
            IsChecked = _config.SortField == field
        };
        item.Click += (_, _) =>
        {
            _config.SortField = field;
            RefreshItems();
            _manager.SaveConfigDebounced();
        };
        menu.Items.Add(item);
    }

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        ShowBoxContextMenu(MenuButton);
    }

    private void HeaderGrid_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ShowBoxContextMenu(HeaderGrid);
    }

    private void ShowBoxContextMenu(UIElement target)
    {
        var menu = CreateDarkContextMenu();

        var newBox = new MenuItem { Header = "新建格子" };
        newBox.Click += (_, _) => _manager.CreateNewBox();
        menu.Items.Add(newBox);

        var rename = new MenuItem { Header = "重命名格子" };
        rename.Click += (_, _) => BeginRename();
        menu.Items.Add(rename);

        var mapFolder = new MenuItem
        {
            Header = string.IsNullOrWhiteSpace(_config.MappedFolderPath)
                ? "关联本地文件夹..."
                : $"已关联目录: {Path.GetFileName(_config.MappedFolderPath)} (取消关联)"
        };
        mapFolder.Click += (_, _) => ToggleMappedFolder();
        menu.Items.Add(mapFolder);

        // 外观与样式子菜单
        var appearanceMenu = new MenuItem { Header = "外观与样式" };
        var openAppearance = new MenuItem { Header = "打开外观设置面板..." };
        openAppearance.Click += (_, _) => _manager.ShowBoxAppearanceWindow(this);
        appearanceMenu.Items.Add(openAppearance);
        appearanceMenu.Items.Add(new Separator());

        var colorMenu = new MenuItem { Header = "主题配色" };
        foreach (var preset in BoxAppearancePresets.Colors)
        {
            AddColorOption(colorMenu, preset.Name, preset.Hex);
        }
        appearanceMenu.Items.Add(colorMenu);

        var opacityMenu = new MenuItem { Header = "背景透明度" };
        foreach (var pct in new[] { 0, 20, 35, 55, 75, 90 })
        {
            var opacityItem = new MenuItem
            {
                Header = pct == 0 ? "0%（全透明）" : $"{pct}%",
                IsChecked = Math.Abs(_config.Opacity * 100 - pct) < 6
            };
            opacityItem.Click += (_, _) =>
            {
                _config.Opacity = pct / 100.0;
                UpdateAppearance();
                _manager.SaveConfigDebounced();
            };
            opacityMenu.Items.Add(opacityItem);
        }
        appearanceMenu.Items.Add(opacityMenu);

        var sizeMenu = new MenuItem { Header = "图标与字号" };
        AddSizeTierOption(sizeMenu, "小图标", BoxSizeTier.Small, isIcon: true);
        AddSizeTierOption(sizeMenu, "标准图标", BoxSizeTier.Medium, isIcon: true);
        AddSizeTierOption(sizeMenu, "大图标", BoxSizeTier.Large, isIcon: true);
        sizeMenu.Items.Add(new Separator());
        AddSizeTierOption(sizeMenu, "小字号", BoxSizeTier.Small, isIcon: false);
        AddSizeTierOption(sizeMenu, "标准字号", BoxSizeTier.Medium, isIcon: false);
        AddSizeTierOption(sizeMenu, "大字号", BoxSizeTier.Large, isIcon: false);
        sizeMenu.Items.Add(new Separator());
        var shadowItem = new MenuItem
        {
            Header = "开启文字阴影（防撞色）",
            IsChecked = _config.EnableTextShadow
        };
        shadowItem.Click += (_, _) =>
        {
            _config.EnableTextShadow = !_config.EnableTextShadow;
            UpdateAppearance();
            _manager.SaveConfigDebounced();
        };
        sizeMenu.Items.Add(shadowItem);
        appearanceMenu.Items.Add(sizeMenu);

        var borderMenu = new MenuItem { Header = "边框与圆角" };
        AddCornerOption(borderMenu, "直角（0px）", 0);
        AddCornerOption(borderMenu, "微圆角（6px）", 6);
        AddCornerOption(borderMenu, "大圆角（12px）", 12);
        borderMenu.Items.Add(new Separator());
        AddBorderStyleOption(borderMenu, "细高光边框", BoxBorderStyle.Subtle);
        AddBorderStyleOption(borderMenu, "无边框悬浮", BoxBorderStyle.None);
        AddBorderStyleOption(borderMenu, "主题强调边框", BoxBorderStyle.Accent);
        appearanceMenu.Items.Add(borderMenu);

        var headerStyleMenu = new MenuItem { Header = "标题栏样式" };
        var alignCenter = new MenuItem
        {
            Header = "标题居中",
            IsChecked = _config.TitleAlignment == BoxTitleAlignment.Center
        };
        alignCenter.Click += (_, _) =>
        {
            _config.TitleAlignment = BoxTitleAlignment.Center;
            UpdateAppearance();
            _manager.SaveConfigDebounced();
        };
        headerStyleMenu.Items.Add(alignCenter);

        var alignLeft = new MenuItem
        {
            Header = "标题居左",
            IsChecked = _config.TitleAlignment == BoxTitleAlignment.Left
        };
        alignLeft.Click += (_, _) =>
        {
            _config.TitleAlignment = BoxTitleAlignment.Left;
            UpdateAppearance();
            _manager.SaveConfigDebounced();
        };
        headerStyleMenu.Items.Add(alignLeft);
        headerStyleMenu.Items.Add(new Separator());

        var headerBand = new MenuItem
        {
            Header = "独立标题栏底色与分隔线",
            IsChecked = _config.HeaderBandVisible
        };
        headerBand.Click += (_, _) =>
        {
            _config.HeaderBandVisible = !_config.HeaderBandVisible;
            UpdateAppearance();
            _manager.SaveConfigDebounced();
        };
        headerStyleMenu.Items.Add(headerBand);

        var autoFade = new MenuItem
        {
            Header = "鼠标移开自动弱化顶栏按钮",
            IsChecked = _config.AutoFadeHeaderButtons
        };
        autoFade.Click += (_, _) =>
        {
            _config.AutoFadeHeaderButtons = !_config.AutoFadeHeaderButtons;
            UpdateAppearance();
            _manager.SaveConfigDebounced();
        };
        headerStyleMenu.Items.Add(autoFade);
        appearanceMenu.Items.Add(headerStyleMenu);

        appearanceMenu.Items.Add(new Separator());
        var syncAllItem = new MenuItem { Header = "将当前外观同步到所有格子" };
        syncAllItem.Click += (_, _) => _manager.ApplyBoxAppearanceToAllAndDefault(_config);
        appearanceMenu.Items.Add(syncAllItem);

        menu.Items.Add(appearanceMenu);

        var refresh = new MenuItem { Header = "刷新格子" };
        refresh.Click += (_, _) => RefreshItems();
        menu.Items.Add(refresh);

        menu.Items.Add(new Separator());

        var settings = new MenuItem { Header = "打开设置中心..." };
        settings.Click += (_, _) => _manager.ShowSettingsWindow();
        menu.Items.Add(settings);

        var hideAll = new MenuItem { Header = "隐藏全部图标（纯净桌面）" };
        hideAll.Click += (_, _) => _manager.ToggleAllBoxesVisibility();
        menu.Items.Add(hideAll);

        var pauseTidy = new MenuItem { Header = DesktopHostService.GetToggleTidyMenuText(_manager.Config.Tidy.IsTidyPaused) };
        pauseTidy.Click += (_, _) => _manager.ToggleTidyPaused();
        menu.Items.Add(pauseTidy);

        menu.Items.Add(new Separator());

        var deleteBox = new MenuItem { Header = "解散此格子（保留原文件）" };
        deleteBox.Click += (_, _) =>
        {
            if (NordConfirmDialog.Confirm(
                this,
                "解散桌面格子",
                $"确定要解散格子“{_config.Title}”吗？\n放心：你的任何物理文件都不会被删除。",
                confirmText: "解散格子",
                cancelText: "取消",
                isDanger: true))
            {
                _manager.RemoveBox(_config.Id);
            }
        };
        menu.Items.Add(deleteBox);

        var exitApp = new MenuItem { Header = "退出 DeskBox" };
        exitApp.Click += (_, _) =>
        {
            _manager.Dispose();
            System.Windows.Application.Current?.Shutdown();
        };
        menu.Items.Add(exitApp);

        ShowPopupMenu(menu, target);
    }

    private void AddColorOption(MenuItem parent, string name, string hex)
    {
        var item = new MenuItem
        {
            Header = name,
            IsChecked = string.Equals(_config.ThemeColor, hex, StringComparison.OrdinalIgnoreCase)
        };
        item.Click += (_, _) =>
        {
            _config.ThemeColor = hex;
            UpdateAppearance();
            _manager.SaveConfigDebounced();
        };
        parent.Items.Add(item);
    }

    private void AddSizeTierOption(MenuItem parent, string label, BoxSizeTier tier, bool isIcon)
    {
        var item = new MenuItem
        {
            Header = label,
            IsChecked = (isIcon ? _config.IconSizeTier : _config.FontSizeTier) == tier
        };
        item.Click += (_, _) =>
        {
            if (isIcon)
                _config.IconSizeTier = tier;
            else
                _config.FontSizeTier = tier;
            UpdateAppearance();
            UpdateViewModeVisual();
            _manager.SaveConfigDebounced();
        };
        parent.Items.Add(item);
    }

    private void AddCornerOption(MenuItem parent, string label, double radius)
    {
        var item = new MenuItem
        {
            Header = label,
            IsChecked = Math.Abs(_config.CornerRadius - radius) < 1.0
        };
        item.Click += (_, _) =>
        {
            _config.CornerRadius = radius;
            UpdateAppearance();
            _manager.SaveConfigDebounced();
        };
        parent.Items.Add(item);
    }

    private void AddBorderStyleOption(MenuItem parent, string label, BoxBorderStyle style)
    {
        var item = new MenuItem
        {
            Header = label,
            IsChecked = _config.BorderStyle == style
        };
        item.Click += (_, _) =>
        {
            _config.BorderStyle = style;
            UpdateAppearance();
            _manager.SaveConfigDebounced();
        };
        parent.Items.Add(item);
    }

    private void ToggleMappedFolder()
    {
        if (!string.IsNullOrWhiteSpace(_config.MappedFolderPath))
        {
            _config.MappedFolderPath = null;
            SetupStorageWatcher();
            RefreshItems();
            _manager.SaveConfigDebounced();
            return;
        }

        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择要映射到此格子的本地文件夹（格子内容将实时同步该文件夹）"
        };
        if (dlg.ShowDialog() == true)
        {
            _config.MappedFolderPath = dlg.FolderName;
            SetupStorageWatcher();
            RefreshItems();
            _manager.SaveConfigDebounced();
        }
    }

    private ContextMenu CreateDarkContextMenu()
    {
        var menu = new ContextMenu();
        menu.Closed += (_, _) =>
        {
            _isPopupOpen = false;
            UpdateHeaderButtonsFadeState();
        };
        return menu;
    }

    private void ShowPopupMenu(ContextMenu menu, UIElement placementTarget)
    {
        _isPopupOpen = true;
        UpdateHeaderButtonsFadeState();
        menu.PlacementTarget = placementTarget;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void HeaderGrid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            BeginRename();
            e.Handled = true;
            return;
        }

        if (_config.IsLocked || _isRenaming)
            return;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out _dragStartRect))
            return;

        NativeMethods.GetCursorPos(out _dragStartCursor);
        _isDraggingWindow = true;
        HeaderGrid.CaptureMouse();
        HeaderGrid.MouseMove += HeaderGrid_MouseMove;
        HeaderGrid.MouseLeftButtonUp += HeaderGrid_MouseLeftButtonUp;
    }

    private void HeaderGrid_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingWindow)
            return;

        NativeMethods.GetCursorPos(out var cur);
        var candidateX = _dragStartRect.Left + (cur.X - _dragStartCursor.X);
        var candidateY = _dragStartRect.Top + (cur.Y - _dragStartCursor.Y);

        var (newX, newY) = _manager.SnapMoveWithGuides(
            this,
            candidateX,
            candidateY,
            _dragStartRect.Width,
            _dragStartRect.Height,
            cur.X,
            cur.Y);

        _lastPhysicalRect = new MonitorTopologyService.PhysicalRect(newX, newY, _dragStartRect.Width, _dragStartRect.Height);
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_BOTTOM,
            newX,
            newY,
            0,
            0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER);
        UpdateBackdropCrop();
    }

    private void HeaderGrid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingWindow)
            return;

        _isDraggingWindow = false;
        HeaderGrid.ReleaseMouseCapture();
        HeaderGrid.MouseMove -= HeaderGrid_MouseMove;
        HeaderGrid.MouseLeftButtonUp -= HeaderGrid_MouseLeftButtonUp;
        _manager.HideAlignmentGuides();
        _manager.RecordCurrentPlacementsAndSave();
    }

    public void BeginRename()
    {
        _isRenaming = true;
        UpdateHeaderButtonsFadeState();
        TitleEditBox.Text = _config.Title;
        TitleTextBlock.Visibility = Visibility.Collapsed;
        TitleEditBox.Visibility = Visibility.Visible;
        TitleEditBox.Focus();
        TitleEditBox.SelectAll();
    }

    private void CommitRename(bool apply)
    {
        if (!_isRenaming)
            return;

        _isRenaming = false;
        UpdateHeaderButtonsFadeState();
        if (apply && !string.IsNullOrWhiteSpace(TitleEditBox.Text))
        {
            _config.Title = TitleEditBox.Text.Trim();
            Title = _config.Title;
            TitleTextBlock.Text = _config.Title;
            _manager.SaveConfigDebounced();
        }
        TitleEditBox.Visibility = Visibility.Collapsed;
        TitleTextBlock.Visibility = Visibility.Visible;
    }

    private void TitleEditBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitRename(true);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CommitRename(false);
            e.Handled = true;
        }
    }

    private void TitleEditBox_LostFocus(object sender, RoutedEventArgs e)
    {
        CommitRename(true);
    }

    private void Resize_DragStarted(object sender, DragStartedEventArgs e)
    {
        _isResizing = true;
        _hoverCollapseTimer.Stop();
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            NativeMethods.GetWindowRect(hwnd, out _resizeStartRect);
            NativeMethods.GetCursorPos(out _resizeStartCursor);
        }
    }

    private void ResizeLeft_DragDelta(object sender, DragDeltaEventArgs e) => ResizeWindowFromCursor(resizeLeft: true, resizeRight: false, resizeHeight: false);
    private void ResizeRight_DragDelta(object sender, DragDeltaEventArgs e) => ResizeWindowFromCursor(resizeLeft: false, resizeRight: true, resizeHeight: false);
    private void ResizeBottom_DragDelta(object sender, DragDeltaEventArgs e) => ResizeWindowFromCursor(resizeLeft: false, resizeRight: false, resizeHeight: true);
    private void ResizeBottomRight_DragDelta(object sender, DragDeltaEventArgs e) => ResizeWindowFromCursor(resizeLeft: false, resizeRight: true, resizeHeight: true);

    private void ResizeWindowFromCursor(bool resizeLeft, bool resizeRight, bool resizeHeight)
    {
        if (_config.IsLocked)
            return;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        NativeMethods.GetCursorPos(out var cur);
        int dx = cur.X - _resizeStartCursor.X;
        int dy = cur.Y - _resizeStartCursor.Y;

        int candX = _resizeStartRect.Left;
        int candY = _resizeStartRect.Top;
        int candW = _resizeStartRect.Width;
        int candH = _resizeStartRect.Height;

        if (resizeLeft)
        {
            int right = _resizeStartRect.Right;
            candW = Math.Max(200, _resizeStartRect.Width - dx);
            candX = right - candW;
        }
        else if (resizeRight)
        {
            candW = Math.Max(200, _resizeStartRect.Width + dx);
        }

        if (resizeHeight)
        {
            candH = Math.Max(120, _resizeStartRect.Height + dy);
        }

        var (x, y, w, h) = _manager.SnapResizeWithGuides(this, candX, candY, candW, candH, resizeLeft, resizeRight, resizeHeight);
        _expandedPhysicalHeight = h;
        _lastPhysicalRect = new MonitorTopologyService.PhysicalRect(x, y, w, h);

        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_BOTTOM,
            x,
            y,
            w,
            h,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER);
        UpdateBackdropCrop();
    }

    private void Resize_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _isResizing = false;
        _manager.HideAlignmentGuides();
        _manager.RecordCurrentPlacementsAndSave();
    }

    private void ItemsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsListBox.SelectedItem is BoxViewItem item)
        {
            ShellService.OpenItem(item.Path);
        }
    }

    private void ItemsListBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var selected = ItemsListBox.SelectedItems.OfType<BoxViewItem>().ToList();
        if (selected.Count == 0)
            return;

        if (e.Key == Key.Delete)
        {
            e.Handled = true;
            bool shiftHeld = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            DeleteOrRemoveSelectedItems(selected, shiftHeld);
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            foreach (var item in selected)
                ShellService.OpenItem(item.Path);
        }
    }

    private void DeleteOrRemoveSelectedItems(IReadOnlyList<BoxViewItem> selected, bool permanent)
    {
        var realDesktopOrMappedFiles = new List<string>();
        var referenceOnly = new List<string>();

        foreach (var item in selected)
        {
            if (item.Path.StartsWith("shell:::", StringComparison.OrdinalIgnoreCase))
            {
                referenceOnly.Add(item.Path);
                continue;
            }

            if ((File.Exists(item.Path) || Directory.Exists(item.Path)) &&
                (!string.IsNullOrWhiteSpace(_config.MappedFolderPath) || _manager.StorageService.IsInAnyDesktop(item.Path)))
            {
                realDesktopOrMappedFiles.Add(item.Path);
            }
            else
            {
                // 桌面外的项目按 Delete 仅移出格子引用，避免误删用户其它盘的重要目录
                referenceOnly.Add(item.Path);
            }
        }

        if (realDesktopOrMappedFiles.Count > 0)
        {
            _manager.IconFilterService.RestoreHiddenAttributeForPaths(_manager.Config, realDesktopOrMappedFiles);
            var hwnd = new WindowInteropHelper(this).Handle;
            ShellService.DeleteItems(realDesktopOrMappedFiles, permanent, hwnd);
            CleanupMissingItems();
        }

        bool removedAnyRef = false;
        foreach (var p in referenceOnly)
        {
            if (_config.Items.RemoveAll(i => string.Equals(i.Path, p, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                removedAnyRef = true;
                if (p.StartsWith("shell:::", StringComparison.OrdinalIgnoreCase))
                {
                    _manager.RestoreSystemDesktopIconIfOwnedByUs(p);
                }
            }
        }

        if (removedAnyRef)
            _manager.SaveConfigDebounced();

        ItemsListBox.SelectedItems.Clear();
        RefreshItems();
        _manager.RefreshUnorganizedBoxes();
        _manager.SyncDesktopHiddenIcons();
    }

    private void MoveSelectedItemsToDesktop(IReadOnlyList<BoxViewItem> selected)
    {
        bool configChanged = false;
        foreach (var item in selected)
        {
            if (_config.Items.RemoveAll(i => string.Equals(i.Path, item.Path, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                configChanged = true;
                if (item.Path.StartsWith("shell:::", StringComparison.OrdinalIgnoreCase))
                {
                    _manager.RestoreSystemDesktopIconIfOwnedByUs(item.Path);
                }
            }
        }

        if (configChanged)
            _manager.SaveConfigDebounced();

        ItemsListBox.SelectedItems.Clear();
        RefreshItems();
        _manager.RefreshUnorganizedBoxes();
        _manager.SyncDesktopHiddenIcons(forceShellRefreshFirst: true);
    }

    private static bool IsInsideScrollBar(DependencyObject? dep)
    {
        while (dep != null)
        {
            if (dep is ScrollBar)
                return true;
            dep = VisualTreeHelper.GetParent(dep);
        }
        return false;
    }

    private static (ListBoxItem? lbi, Border? itemBorder) FindHitItemContent(DependencyObject? dep)
    {
        Border? hitItemBorder = null;
        while (dep != null)
        {
            if (dep is Border b && b.Name == "ItemBorder")
                hitItemBorder = b;
            if (dep is ListBoxItem lbi)
                return (lbi, hitItemBorder);
            dep = VisualTreeHelper.GetParent(dep);
        }
        return (null, null);
    }

    private void ItemsListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideScrollBar(e.OriginalSource as DependencyObject))
            return;

        _itemDragStartPoint = e.GetPosition(ItemsListBox);
        var (lbi, hitItemBorder) = FindHitItemContent(e.OriginalSource as DependencyObject);

        if (lbi != null && hitItemBorder != null && lbi.DataContext is BoxViewItem viewItem)
        {
            // 点在图标或文件名内容区域内：准备项目拖拽或普通点击选择
            _itemDragCandidate = viewItem;
            _isMarqueeSelecting = false;
            return;
        }

        // 点在右侧或下方空白区域：准备进入框选模式
        _itemDragCandidate = null;
        _isMarqueeSelecting = true;
        _marqueeStartPoint = e.GetPosition(SelectionCanvas);

        bool ctrlHeld = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        _marqueeInitialSelection = ctrlHeld
            ? ItemsListBox.SelectedItems.OfType<BoxViewItem>().ToHashSet()
            : new HashSet<BoxViewItem>();

        if (!ctrlHeld)
        {
            ItemsListBox.SelectedItems.Clear();
        }

        ItemsListBox.CaptureMouse();
        e.Handled = true;
    }

    private void ItemsListBox_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
            return;

        if (_isMarqueeSelecting)
        {
            var cur = e.GetPosition(SelectionCanvas);
            double x = Math.Max(0, Math.Min(_marqueeStartPoint.X, cur.X));
            double y = Math.Max(0, Math.Min(_marqueeStartPoint.Y, cur.Y));
            double maxX = Math.Min(SelectionCanvas.ActualWidth, Math.Max(_marqueeStartPoint.X, cur.X));
            double maxY = Math.Min(SelectionCanvas.ActualHeight, Math.Max(_marqueeStartPoint.Y, cur.Y));
            double w = Math.Max(0, maxX - x);
            double h = Math.Max(0, maxY - y);

            if (w > 3 || h > 3)
            {
                MarqueeRect.Visibility = Visibility.Visible;
                Canvas.SetLeft(MarqueeRect, x);
                Canvas.SetTop(MarqueeRect, y);
                MarqueeRect.Width = w;
                MarqueeRect.Height = h;

                UpdateMarqueeSelection(new Rect(x, y, w, h));
            }
            return;
        }

        if (_itemDragCandidate == null)
            return;

        var pos = e.GetPosition(ItemsListBox);
        if (Math.Abs(pos.X - _itemDragStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(pos.Y - _itemDragStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            var candidate = _itemDragCandidate;
            _itemDragCandidate = null;

            var selected = ItemsListBox.SelectedItems.OfType<BoxViewItem>().ToList();
            if (!selected.Contains(candidate))
            {
                selected = [candidate];
            }

            var paths = selected.Select(s => s.Path).ToList();
            var data = new DataObject();
            data.SetData(InternalDragFormat, new ItemTransferPayload(_config.Id, paths));

            var realPaths = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
            if (realPaths.Length > 0)
            {
                var sc = new System.Collections.Specialized.StringCollection();
                sc.AddRange(realPaths);
                data.SetFileDropList(sc);
            }

            _dragPreviewWindow = new DragPreviewWindow(selected[0].Icon, selected[0].DisplayName, selected.Count);
            _dragPreviewWindow.Show();
            _dragPreviewWindow.UpdatePositionNearCursor();

            bool droppedOnDesktop = false;
            GiveFeedbackEventHandler feedbackHandler = (_, _) => _dragPreviewWindow?.UpdatePositionNearCursor();
            QueryContinueDragEventHandler queryHandler = (_, args) =>
            {
                _dragPreviewWindow?.UpdatePositionNearCursor();
                if (args.Action == DragAction.Drop && NativeMethods.GetCursorPos(out var dropPt))
                {
                    var hitHwnd = NativeMethods.WindowFromPoint(dropPt);
                    droppedOnDesktop = _hostService.ClassifyDesktopHitWindow(hitHwnd) != DesktopHostService.DesktopHitTarget.None;
                }
            };
            ItemsListBox.GiveFeedback += feedbackHandler;
            ItemsListBox.QueryContinueDrag += queryHandler;

            try
            {
                DragDrop.DoDragDrop(ItemsListBox, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
            }
            finally
            {
                ItemsListBox.GiveFeedback -= feedbackHandler;
                ItemsListBox.QueryContinueDrag -= queryHandler;
                _dragPreviewWindow?.Close();
                _dragPreviewWindow = null;
                HideInsertionIndicator();
            }

            if (droppedOnDesktop && !_config.IsUnorganizedBox && string.IsNullOrWhiteSpace(_config.MappedFolderPath))
            {
                // 物理文件本就在桌面，拖回桌面壁纸时只需解除格子收纳并恢复桌面视觉展示
                MoveSelectedItemsToDesktop(selected);
            }
            else
            {
                Dispatcher.BeginInvoke(async () =>
                {
                    await Task.Delay(150);
                    RefreshItems();
                });
            }
        }
    }

    private void ItemsListBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isMarqueeSelecting)
        {
            _isMarqueeSelecting = false;
            MarqueeRect.Visibility = Visibility.Collapsed;
            if (ItemsListBox.IsMouseCaptured)
                ItemsListBox.ReleaseMouseCapture();
            e.Handled = true;
        }
        _itemDragCandidate = null;
    }

    private void UpdateMarqueeSelection(Rect marquee)
    {
        bool ctrlHeld = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        for (int i = 0; i < ItemsListBox.Items.Count; i++)
        {
            if (ItemsListBox.Items[i] is not BoxViewItem viewItem)
                continue;

            if (ItemsListBox.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem lbi)
                continue;

            // 只检测项目真实内容区（ItemBorder）与框选矩形是否相交
            var targetVisual = FindVisualChildByName<Border>(lbi, "ItemBorder") ?? (FrameworkElement)lbi;
            var topLeft = targetVisual.TranslatePoint(new Point(0, 0), SelectionCanvas);
            var itemBounds = new Rect(topLeft.X, topLeft.Y, targetVisual.ActualWidth, targetVisual.ActualHeight);

            bool hits = marquee.IntersectsWith(itemBounds);
            bool shouldSelect = ctrlHeld ? (_marqueeInitialSelection.Contains(viewItem) ^ hits) : hits;

            if (shouldSelect && !lbi.IsSelected)
                lbi.IsSelected = true;
            else if (!shouldSelect && lbi.IsSelected)
                lbi.IsSelected = false;
        }
    }

    private static T? FindVisualChildByName<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T fe && fe.Name == name)
                return fe;
            var found = FindVisualChildByName<T>(child, name);
            if (found != null)
                return found;
        }
        return null;
    }

    private void ItemsListBox_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var (lbi, hitItemBorder) = FindHitItemContent(e.OriginalSource as DependencyObject);

        if (lbi != null && hitItemBorder != null && lbi.DataContext is BoxViewItem viewItem)
        {
            if (!lbi.IsSelected)
            {
                ItemsListBox.SelectedItems.Clear();
                lbi.IsSelected = true;
            }

            var selected = ItemsListBox.SelectedItems.OfType<BoxViewItem>().ToList();
            e.Handled = true;

            if (selected.Count > 1)
            {
                var menu = CreateDarkContextMenu();
                var openAll = new MenuItem { Header = $"批量打开（共 {selected.Count} 项）" };
                openAll.Click += (_, _) =>
                {
                    foreach (var s in selected) ShellService.OpenItem(s.Path);
                };
                menu.Items.Add(openAll);

                var moveOutAll = new MenuItem { Header = $"移出格子放回桌面（共 {selected.Count} 项）" };
                moveOutAll.Click += (_, _) => MoveSelectedItemsToDesktop(selected);
                menu.Items.Add(moveOutAll);

                var recycleAll = new MenuItem { Header = $"移入回收站（共 {selected.Count} 项）" };
                recycleAll.Click += (_, _) => DeleteOrRemoveSelectedItems(selected, permanent: false);
                menu.Items.Add(recycleAll);

                ShowPopupMenu(menu, ItemsListBox);
                return;
            }

            _isPopupOpen = true;
            try
            {
                NativeMethods.GetCursorPos(out var pt);
                ShellService.ShowNativeContextMenu(
                    viewItem.Path,
                    pt.X,
                    pt.Y,
                    onRemoveFromBox: () => MoveSelectedItemsToDesktop([viewItem]),
                    onRefreshAfterShellCommand: () =>
                    {
                        CleanupMissingItems();
                        RefreshItems();
                    });
            }
            finally
            {
                _isPopupOpen = false;
            }
        }
        else
        {
            e.Handled = true;
            ShowBoxContextMenu(ItemsListBox);
        }
    }

    private void OnWindowDragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(InternalDragFormat) ||
            e.Data.GetDataPresent(DataFormats.FileDrop) ||
            e.Data.GetDataPresent(ShellIdListFormat))
        {
            _hoverCollapseTimer.Stop();
            if (_config.IsCollapsed && !_isHoverExpanded)
            {
                _dragExpandTimer.Start();
            }
        }
    }

    private void OnWindowDragLeave(object sender, DragEventArgs e)
    {
        _dragExpandTimer.Stop();
        HideInsertionIndicator();
        if (_config.IsCollapsed && _isHoverExpanded && !_isPopupOpen && !_isRenaming)
        {
            _hoverCollapseTimer.Start();
        }
    }

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        _dragPreviewWindow?.UpdatePositionNearCursor();

        if ((e.AllowedEffects & (DragDropEffects.Move | DragDropEffects.Copy | DragDropEffects.Link)) != 0)
        {
            var draggedPaths = ExtractDraggedPaths(e.Data);
            if (draggedPaths.Count > 0 &&
                TryHitTestDropTargetItem(e.GetPosition(SelectionCanvas), draggedPaths, out var targetItemPath, out var targetKind, out var highlightRect))
            {
                var targetEffects = ResolveTargetDropEffects(e, targetKind);
                if (targetEffects != DragDropEffects.None)
                {
                    _pendingInsertIndex = -1;
                    InsertionIndicator.Visibility = Visibility.Collapsed;
                    _pendingTargetItemPath = targetItemPath;
                    _pendingTargetKind = targetKind;

                    Canvas.SetLeft(FolderDropIndicator, highlightRect.X);
                    Canvas.SetTop(FolderDropIndicator, highlightRect.Y);
                    FolderDropIndicator.Width = highlightRect.Width;
                    FolderDropIndicator.Height = highlightRect.Height;
                    FolderDropIndicator.Visibility = Visibility.Visible;

                    e.Effects = targetEffects;
                    e.Handled = true;
                    return;
                }
            }
        }

        _pendingTargetItemPath = null;
        _pendingTargetKind = ItemDropTargetKind.None;
        FolderDropIndicator.Visibility = Visibility.Collapsed;

        if (e.Data.GetDataPresent(InternalDragFormat))
        {
            e.Effects = DragDropEffects.Move;
            UpdateInsertionIndicator(e.GetPosition(SelectionCanvas));
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            // 使用 Link/Copy 提示，确保 Explorer 绝不会主动删/移用户在桌面或磁盘上的源文件
            e.Effects = (e.AllowedEffects & DragDropEffects.Link) != 0
                ? DragDropEffects.Link
                : (e.AllowedEffects & DragDropEffects.Copy) != 0
                    ? DragDropEffects.Copy
                    : DragDropEffects.Move;
            UpdateInsertionIndicator(e.GetPosition(SelectionCanvas));
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(ShellIdListFormat))
        {
            // 回收站/此电脑等系统虚拟图标从桌面拖出时通常允许 Link 或 Copy
            e.Effects = (e.AllowedEffects & DragDropEffects.Link) != 0
                ? DragDropEffects.Link
                : (e.AllowedEffects & DragDropEffects.Copy) != 0
                    ? DragDropEffects.Copy
                    : DragDropEffects.Move;
            UpdateInsertionIndicator(e.GetPosition(SelectionCanvas));
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.None;
        HideInsertionIndicator();
    }

    private static DragDropEffects ResolveTargetDropEffects(DragEventArgs e, ItemDropTargetKind kind)
    {
        return kind switch
        {
            ItemDropTargetKind.Folder => (e.KeyStates & DragDropKeyStates.ControlKey) != 0
                ? ((e.AllowedEffects & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None)
                : ((e.AllowedEffects & DragDropEffects.Move) != 0
                    ? DragDropEffects.Move
                    : (e.AllowedEffects & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None),
            ItemDropTargetKind.Application => (e.AllowedEffects & DragDropEffects.Copy) != 0
                ? DragDropEffects.Copy
                : (e.AllowedEffects & DragDropEffects.Link) != 0
                    ? DragDropEffects.Link
                    : (e.AllowedEffects & DragDropEffects.Move) != 0 ? DragDropEffects.Move : DragDropEffects.None,
            ItemDropTargetKind.RecycleBin => (e.AllowedEffects & DragDropEffects.Move) != 0
                ? DragDropEffects.Move
                : (e.AllowedEffects & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None,
            _ => DragDropEffects.None
        };
    }

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        _dragExpandTimer.Stop();
        int targetInsertIndex = _pendingInsertIndex;
        string? targetItemPath = _pendingTargetItemPath;
        var targetKind = _pendingTargetKind;
        HideInsertionIndicator();

        if (!string.IsNullOrWhiteSpace(targetItemPath) && targetKind != ItemDropTargetKind.None)
        {
            var draggedPaths = ExtractDraggedPaths(e.Data);
            bool isInternal = e.Data.GetDataPresent(InternalDragFormat);

            switch (targetKind)
            {
                case ItemDropTargetKind.Folder:
                {
                    bool copy = (e.KeyStates & DragDropKeyStates.ControlKey) != 0 ||
                                (e.AllowedEffects & DragDropEffects.Move) == 0;
                    DropItemsIntoFolder(draggedPaths, targetItemPath, copy);
                    break;
                }
                case ItemDropTargetKind.Application:
                    ShellService.OpenFilesWithApplication(targetItemPath, draggedPaths);
                    break;
                case ItemDropTargetKind.RecycleBin:
                    DropItemsIntoRecycleBin(draggedPaths);
                    break;
            }

            if (!isInternal)
            {
                // 已在本地完成打开/移动/复制/删除，返回 None 防止外部拖拽源重复移动或删除源文件
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
            if (_config.IsCollapsed && _isHoverExpanded)
            {
                _hoverCollapseTimer.Start();
            }
            return;
        }

        if (e.Data.GetDataPresent(InternalDragFormat) &&
            e.Data.GetData(InternalDragFormat) is ItemTransferPayload transferPayload)
        {
            if (transferPayload.SourceBoxId == _config.Id)
            {
                var currentKeys = ItemsListBox.Items.OfType<BoxViewItem>()
                    .Select(v => ShellService.GetItemOrderKey(v.Path))
                    .ToList();
                var movingKeys = transferPayload.ItemPaths
                    .Select(ShellService.GetItemOrderKey)
                    .ToList();

                int insertIdx = targetInsertIndex >= 0 ? targetInsertIndex : currentKeys.Count;
                _config.SortField = BoxSortField.Custom;
                _config.CustomOrder = ShellService.ReorderCustomOrder(currentKeys, movingKeys, insertIdx);
                RefreshItems();
                _manager.SaveConfigDebounced();
            }
            else
            {
                _manager.TransferItemsBetweenBoxes(transferPayload.SourceBoxId, _config.Id, transferPayload.ItemPaths);
                if (targetInsertIndex >= 0 && _config.SortField == BoxSortField.Custom)
                {
                    InsertKeysAtOrder(transferPayload.ItemPaths.Select(ShellService.GetItemOrderKey).ToList(), targetInsertIndex);
                }
            }
            e.Handled = true;
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            bool forceMove = (e.KeyStates & DragDropKeyStates.ShiftKey) != 0;
            var addedKeys = AddDroppedFiles(files, forceMove);
            if (targetInsertIndex >= 0 && addedKeys.Count > 0 && _config.SortField == BoxSortField.Custom)
            {
                InsertKeysAtOrder(addedKeys, targetInsertIndex);
            }
            e.Handled = true;
        }
        else if (e.Data.GetDataPresent(ShellIdListFormat) &&
            e.Data.GetData(ShellIdListFormat) is Stream cidaStream)
        {
            var parsedItems = ShellService.ParseShellIdListArray(cidaStream);
            var diskFiles = parsedItems.Where(p => !p.path.StartsWith("shell:::", StringComparison.OrdinalIgnoreCase)).Select(p => p.path).ToList();
            var virtualItems = parsedItems.Where(p => p.path.StartsWith("shell:::", StringComparison.OrdinalIgnoreCase)).ToList();

            var addedKeys = new List<string>();
            if (diskFiles.Count > 0)
            {
                bool forceMove = (e.KeyStates & DragDropKeyStates.ShiftKey) != 0;
                addedKeys.AddRange(AddDroppedFiles(diskFiles, forceMove));
            }

            if (virtualItems.Count > 0 && !_config.IsUnorganizedBox && string.IsNullOrWhiteSpace(_config.MappedFolderPath))
            {
                bool changed = false;
                foreach (var (vPath, vName) in virtualItems)
                {
                    _manager.RemovePathFromOtherBoxes(vPath, _config.Id, syncDesktopImmediately: false);
                    if (!_config.Items.Any(i => string.Equals(i.Path, vPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        _config.Items.Add(new BoxItemEntry { Path = vPath, CustomName = vName });
                        addedKeys.Add(vPath);
                        changed = true;
                    }
                    _manager.HideSystemDesktopIconIfOnDesktop(vPath);
                }
                if (changed)
                {
                    RefreshItems();
                    _manager.SaveConfigDebounced();
                }
            }

            if (targetInsertIndex >= 0 && addedKeys.Count > 0 && _config.SortField == BoxSortField.Custom)
            {
                InsertKeysAtOrder(addedKeys, targetInsertIndex);
            }
            e.Handled = true;
        }

        if (_config.IsCollapsed && _isHoverExpanded)
        {
            _hoverCollapseTimer.Start();
        }
    }

    private void InsertKeysAtOrder(IReadOnlyList<string> keysToInsert, int insertIndex)
    {
        var currentKeys = ItemsListBox.Items.OfType<BoxViewItem>()
            .Select(v => ShellService.GetItemOrderKey(v.Path))
            .ToList();
        _config.CustomOrder = ShellService.ReorderCustomOrder(currentKeys, keysToInsert, insertIndex);
        RefreshItems();
        _manager.SaveConfigDebounced();
    }

    private void UpdateInsertionIndicator(Point posInCanvas)
    {
        int count = ItemsListBox.Items.Count;
        if (count == 0)
        {
            _pendingInsertIndex = 0;
            InsertionIndicator.Visibility = Visibility.Collapsed;
            return;
        }

        int insertIndex = count;
        Rect indicatorRect = default;
        bool hasIndicator = false;

        if (_config.ViewMode == BoxViewMode.Grid)
        {
            ListBoxItem? lastRealized = null;
            for (int i = 0; i < count; i++)
            {
                if (ItemsListBox.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem lbi)
                    continue;
                lastRealized = lbi;
                var tl = lbi.TranslatePoint(new Point(0, 0), SelectionCanvas);
                var rect = new Rect(tl.X, tl.Y, lbi.ActualWidth, lbi.ActualHeight);

                if (posInCanvas.Y >= rect.Top - 6 && posInCanvas.Y <= rect.Bottom + 6)
                {
                    if (posInCanvas.X < rect.Left + rect.Width / 2)
                    {
                        insertIndex = i;
                        indicatorRect = new Rect(Math.Max(2, rect.Left - 2), rect.Top + 4, 2.5, Math.Max(16, rect.Height - 8));
                        hasIndicator = true;
                        break;
                    }
                    if (posInCanvas.X <= rect.Right + 4)
                    {
                        insertIndex = i + 1;
                        indicatorRect = new Rect(rect.Right - 1, rect.Top + 4, 2.5, Math.Max(16, rect.Height - 8));
                        hasIndicator = true;
                        break;
                    }
                }
            }

            if (!hasIndicator && lastRealized != null)
            {
                var tl = lastRealized.TranslatePoint(new Point(0, 0), SelectionCanvas);
                indicatorRect = new Rect(tl.X + lastRealized.ActualWidth, tl.Y + 4, 2.5, Math.Max(16, lastRealized.ActualHeight - 8));
                hasIndicator = true;
            }
        }
        else
        {
            ListBoxItem? lastRealized = null;
            for (int i = 0; i < count; i++)
            {
                if (ItemsListBox.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem lbi)
                    continue;
                lastRealized = lbi;
                var tl = lbi.TranslatePoint(new Point(0, 0), SelectionCanvas);
                var rect = new Rect(tl.X, tl.Y, lbi.ActualWidth, lbi.ActualHeight);

                if (posInCanvas.Y < rect.Top + rect.Height / 2)
                {
                    insertIndex = i;
                    indicatorRect = new Rect(8, Math.Max(1, rect.Top - 1), Math.Max(40, SelectionCanvas.ActualWidth - 16), 2.5);
                    hasIndicator = true;
                    break;
                }
            }

            if (!hasIndicator && lastRealized != null)
            {
                var tl = lastRealized.TranslatePoint(new Point(0, 0), SelectionCanvas);
                indicatorRect = new Rect(8, tl.Y + lastRealized.ActualHeight - 1, Math.Max(40, SelectionCanvas.ActualWidth - 16), 2.5);
                hasIndicator = true;
            }
        }

        _pendingInsertIndex = insertIndex;
        if (hasIndicator)
        {
            Canvas.SetLeft(InsertionIndicator, indicatorRect.X);
            Canvas.SetTop(InsertionIndicator, indicatorRect.Y);
            InsertionIndicator.Width = indicatorRect.Width;
            InsertionIndicator.Height = indicatorRect.Height;
            InsertionIndicator.Visibility = Visibility.Visible;
        }
        else
        {
            InsertionIndicator.Visibility = Visibility.Collapsed;
        }
    }

    private void HideInsertionIndicator()
    {
        _pendingInsertIndex = -1;
        _pendingTargetItemPath = null;
        _pendingTargetKind = ItemDropTargetKind.None;
        InsertionIndicator.Visibility = Visibility.Collapsed;
        FolderDropIndicator.Visibility = Visibility.Collapsed;
    }

    private static IReadOnlyList<string> ExtractDraggedPaths(System.Windows.IDataObject data)
    {
        if (data.GetDataPresent(InternalDragFormat) &&
            data.GetData(InternalDragFormat) is ItemTransferPayload payload)
        {
            return payload.ItemPaths;
        }

        if (data.GetDataPresent(DataFormats.FileDrop) &&
            data.GetData(DataFormats.FileDrop) is string[] files)
        {
            return files;
        }

        if (data.GetDataPresent(ShellIdListFormat) &&
            data.GetData(ShellIdListFormat) is Stream cidaStream)
        {
            return ShellService.ParseShellIdListArray(cidaStream)
                .Select(p => p.path)
                .ToList();
        }

        return Array.Empty<string>();
    }

    private bool TryHitTestDropTargetItem(
        Point posInCanvas,
        IReadOnlyList<string> draggedPaths,
        out string targetItemPath,
        out ItemDropTargetKind targetKind,
        out Rect highlightRect)
    {
        targetItemPath = string.Empty;
        targetKind = ItemDropTargetKind.None;
        highlightRect = default;

        if (draggedPaths.Count == 0 || SelectionCanvas.ActualWidth <= 0 || SelectionCanvas.ActualHeight <= 0)
            return false;

        if (posInCanvas.X < 0 || posInCanvas.Y < 0 ||
            posInCanvas.X > SelectionCanvas.ActualWidth || posInCanvas.Y > SelectionCanvas.ActualHeight)
            return false;

        int count = ItemsListBox.Items.Count;
        for (int i = 0; i < count; i++)
        {
            if (ItemsListBox.Items[i] is not BoxViewItem viewItem)
                continue;

            if (ItemsListBox.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem lbi || !lbi.IsVisible)
                continue;

            var lbiTl = lbi.TranslatePoint(new Point(0, 0), SelectionCanvas);
            var lbiRect = new Rect(lbiTl.X, lbiTl.Y, lbi.ActualWidth, lbi.ActualHeight);
            if (lbiRect.Width <= 0 || lbiRect.Height <= 0)
                continue;

            var itemBorder = FindVisualChildByName<Border>(lbi, "ItemBorder");
            Rect borderRect;
            if (itemBorder != null && itemBorder.ActualWidth > 0 && itemBorder.ActualHeight > 0)
            {
                var borderTl = itemBorder.TranslatePoint(new Point(0, 0), SelectionCanvas);
                borderRect = new Rect(borderTl.X, borderTl.Y, itemBorder.ActualWidth, itemBorder.ActualHeight);
            }
            else
            {
                borderRect = lbiRect;
            }

            Rect hitRect;
            if (_config.ViewMode == BoxViewMode.Grid)
            {
                double edgeReserveX = Math.Clamp(borderRect.Width * 0.16, 8.0, 13.0);
                hitRect = new Rect(
                    borderRect.Left + edgeReserveX,
                    borderRect.Top,
                    Math.Max(1.0, borderRect.Width - edgeReserveX * 2),
                    borderRect.Height);
            }
            else
            {
                double edgeReserveY = Math.Clamp(lbiRect.Height * 0.20, 4.0, 6.0);
                hitRect = new Rect(
                    borderRect.Left,
                    lbiRect.Top + edgeReserveY,
                    borderRect.Width,
                    Math.Max(1.0, lbiRect.Height - edgeReserveY * 2));
            }

            if (!hitRect.Contains(posInCanvas))
                continue;

            if (ShellService.CanDropIntoFolder(draggedPaths, viewItem.Path))
            {
                targetItemPath = viewItem.Path;
                targetKind = ItemDropTargetKind.Folder;
                highlightRect = borderRect;
                return true;
            }

            if (ShellService.CanDropOntoApplication(draggedPaths, viewItem.Path))
            {
                targetItemPath = viewItem.Path;
                targetKind = ItemDropTargetKind.Application;
                highlightRect = borderRect;
                return true;
            }

            if (string.Equals(ShellService.ResolveHideDesktopIconGuid(viewItem.Path), "{645FF040-5081-101B-9F08-00AA002F954E}", StringComparison.OrdinalIgnoreCase) &&
                draggedPaths.Any(p =>
                {
                    var n = ShellService.TryNormalizePhysicalPath(p);
                    return n != null && (File.Exists(n) || Directory.Exists(n));
                }))
            {
                targetItemPath = viewItem.Path;
                targetKind = ItemDropTargetKind.RecycleBin;
                highlightRect = borderRect;
                return true;
            }

            return false;
        }

        return false;
    }

    private void DropItemsIntoFolder(
        IReadOnlyList<string> draggedPaths,
        string targetFolderPath,
        bool copy)
    {
        var resolvedTargetFolder = ShellService.TryResolveTargetFolder(targetFolderPath);
        if (resolvedTargetFolder == null)
            return;

        var validPaths = draggedPaths
            .Where(p => ShellService.CanMoveOrCopyPathIntoFolder(p, resolvedTargetFolder))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (validPaths.Count == 0)
            return;

        if (!copy)
        {
            _manager.IconFilterService.RestoreHiddenAttributeForPaths(_manager.Config, validPaths);
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        bool anyMovedOrCopied = false;

        foreach (var rawSrc in validPaths)
        {
            var normalizedSrc = ShellService.TryNormalizePhysicalPath(rawSrc);
            if (normalizedSrc == null)
                continue;

            bool wasDeskBoxHidden = copy && _manager.Config.HiddenDesktopPaths.Contains(normalizedSrc);
            if (!ShellService.MoveOrCopyItemToFolder(normalizedSrc, resolvedTargetFolder, copy, hwnd))
                continue;

            anyMovedOrCopied = true;

            if (wasDeskBoxHidden)
            {
                // 若源文件由 DeskBox 标记为桌面隐藏，复制进文件夹后需确保副本在文件夹内正常可见
                var copiedDest = Path.Combine(resolvedTargetFolder, Path.GetFileName(normalizedSrc));
                try
                {
                    if (File.Exists(copiedDest) || Directory.Exists(copiedDest))
                    {
                        var attrs = File.GetAttributes(copiedDest);
                        if ((attrs & FileAttributes.Hidden) != 0)
                            File.SetAttributes(copiedDest, attrs & ~FileAttributes.Hidden);
                    }
                }
                catch
                {
                }
            }
        }

        if (!anyMovedOrCopied)
        {
            _manager.SyncDesktopHiddenIcons(forceShellRefreshFirst: false);
            return;
        }

        if (!copy)
        {
            RemoveGonePathsFromBoxesAndRefresh(validPaths);
        }
        else
        {
            RefreshItems();
            _manager.RefreshUnorganizedBoxes();
            _manager.SyncDesktopHiddenIcons(forceShellRefreshFirst: true);
        }
    }

    private void DropItemsIntoRecycleBin(IReadOnlyList<string> draggedPaths)
    {
        var validPaths = draggedPaths
            .Select(ShellService.TryNormalizePhysicalPath)
            .Where(p => p != null && (File.Exists(p) || Directory.Exists(p)))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (validPaths.Count == 0)
            return;

        _manager.IconFilterService.RestoreHiddenAttributeForPaths(_manager.Config, validPaths);
        var hwnd = new WindowInteropHelper(this).Handle;
        ShellService.DeleteItems(validPaths, permanent: false, hwnd);
        RemoveGonePathsFromBoxesAndRefresh(validPaths);
    }

    private void RemoveGonePathsFromBoxesAndRefresh(IEnumerable<string> candidatePaths)
    {
        bool configChanged = false;
        foreach (var rawPath in candidatePaths)
        {
            var normalized = ShellService.TryNormalizePhysicalPath(rawPath);
            if (normalized == null || File.Exists(normalized) || Directory.Exists(normalized))
                continue;

            _manager.RemovePathFromOtherBoxes(normalized, excludeBoxId: string.Empty, syncDesktopImmediately: false);

            var orderKey = ShellService.GetItemOrderKey(normalized);
            foreach (var box in _manager.Config.Boxes)
            {
                if (box.Items.All(i => !string.Equals(ShellService.GetItemOrderKey(i.Path), orderKey, StringComparison.OrdinalIgnoreCase)) &&
                    box.CustomOrder.RemoveAll(k => string.Equals(k, orderKey, StringComparison.OrdinalIgnoreCase)) > 0)
                {
                    configChanged = true;
                }
            }
        }

        RefreshItems();
        _manager.RefreshUnorganizedBoxes();
        if (configChanged)
        {
            _manager.SaveConfigDebounced();
        }
        _manager.SyncDesktopHiddenIcons(forceShellRefreshFirst: true);
    }

    public List<string> AddDroppedFiles(IEnumerable<string> files, bool forceMove = false)
    {
        var addedKeys = new List<string>();
        if (!string.IsNullOrWhiteSpace(_config.MappedFolderPath) && Directory.Exists(_config.MappedFolderPath))
        {
            foreach (var file in files)
            {
                TryCopyOrMoveToFolder(file, _config.MappedFolderPath, forceMove);
                addedKeys.Add(ShellService.GetItemOrderKey(file));
            }
            RefreshItems();
            return addedKeys;
        }

        if (_config.IsUnorganizedBox)
        {
            foreach (var file in files)
            {
                var normalized = file.StartsWith("shell:::", StringComparison.OrdinalIgnoreCase)
                    ? file
                    : Path.GetFullPath(file).TrimEnd('\\', '/');
                _manager.RemovePathFromOtherBoxes(normalized, _config.Id, syncDesktopImmediately: false);
                addedKeys.Add(ShellService.GetItemOrderKey(normalized));
            }
            RefreshItems();
            _manager.RefreshUnorganizedBoxes();
            _manager.SyncDesktopHiddenIcons();
            return addedKeys;
        }

        bool configChanged = false;
        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file))
                continue;

            if (file.StartsWith("shell:::", StringComparison.OrdinalIgnoreCase))
            {
                _manager.RemovePathFromOtherBoxes(file, _config.Id, syncDesktopImmediately: false);
                if (!_config.Items.Any(i => string.Equals(i.Path, file, StringComparison.OrdinalIgnoreCase)))
                {
                    _config.Items.Add(new BoxItemEntry { Path = file });
                    addedKeys.Add(file);
                    configChanged = true;
                }
                _manager.HideSystemDesktopIconIfOnDesktop(file);
                continue;
            }

            string normalized;
            try
            {
                normalized = Path.GetFullPath(file).TrimEnd('\\', '/');
            }
            catch
            {
                continue;
            }

            if (!File.Exists(normalized) && !Directory.Exists(normalized))
                continue;

            _manager.RemovePathFromOtherBoxes(normalized, _config.Id, syncDesktopImmediately: false);

            // 绝不移动、不复制、不修改文件属性，仅在格子内记录真实原路径引用
            if (!_config.Items.Any(i => string.Equals(i.Path, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                _config.Items.Add(new BoxItemEntry { Path = normalized });
                addedKeys.Add(ShellService.GetItemOrderKey(normalized));
                configChanged = true;
            }
        }

        RefreshItems();
        if (configChanged)
        {
            _manager.SaveConfigDebounced();
        }
        _manager.RefreshUnorganizedBoxes();
        _manager.SyncDesktopHiddenIcons();

        return addedKeys;
    }

    private void TryCopyOrMoveToFolder(string sourcePath, string targetDir, bool move)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        ShellService.MoveOrCopyItemToFolder(sourcePath, targetDir, copy: !move, hwnd);
    }

    public void RemoveItemPath(string path)
    {
        var removed = _config.Items.RemoveAll(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            RefreshItems();
            _manager.SaveConfigDebounced();
        }
    }

    private void CleanupMissingItems()
    {
        if (!string.IsNullOrWhiteSpace(_config.MappedFolderPath))
            return;

        var removed = _config.Items.RemoveAll(i =>
            !i.Path.StartsWith("shell:::", StringComparison.OrdinalIgnoreCase) &&
            !File.Exists(i.Path) &&
            !Directory.Exists(i.Path));

        if (removed > 0)
            _manager.SaveConfigDebounced();
    }

    private sealed record ItemTransferPayload(string SourceBoxId, IReadOnlyList<string> ItemPaths);
}
