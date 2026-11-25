using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using DeskBox.Interop;
using DeskBox.Models;
using DeskBox.Services;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ContextMenu = System.Windows.Controls.ContextMenu;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace DeskBox.Views;

/// <summary>
/// 全屏透明自绘桌面画布（每块显示器一个实例）。
/// 接管未收纳桌面图标的渲染与交互，彻底替代污染文件系统的 Hidden 属性方案。
/// </summary>
public partial class DesktopCanvasWindow : Window
{
    internal const string CanvasDragFormat = "DeskBoxCanvasItemTransfer";
    internal sealed record CanvasTransferPayload(string SourceMonitorId, IReadOnlyList<string> ItemPaths);

    private static readonly Brush SelectedBgBrush = CreateFrozenBrush(Color.FromArgb(0x55, 0x5E, 0x81, 0xAC));
    private static readonly Brush SelectedBorderBrush = CreateFrozenBrush(Color.FromArgb(0xCC, 0x88, 0xC0, 0xD0));
    private static readonly Brush HoverBgBrush = CreateFrozenBrush(Color.FromArgb(0x28, 0xEC, 0xEF, 0xF4));
    private static readonly Brush HoverBorderBrush = CreateFrozenBrush(Color.FromArgb(0x55, 0xD8, 0xDE, 0xE9));
    private static readonly Brush ShadowTextBrush = CreateFrozenBrush(Color.FromArgb(0xD8, 0x00, 0x00, 0x00));
    private static readonly DropShadowEffect IconTextShadow = CreateFrozenShadow();

    private static Brush CreateFrozenBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static DropShadowEffect CreateFrozenShadow()
    {
        var eff = new DropShadowEffect
        {
            Color = Colors.Black,
            BlurRadius = 2.5,
            ShadowDepth = 0.5,
            Direction = 315,
            Opacity = 0.85
        };
        eff.Freeze();
        return eff;
    }

    private sealed class CanvasIconVisual
    {
        public required string Path { get; init; }
        public required string DisplayName { get; init; }
        public required ImageSource? Icon { get; init; }
        public required Border RootBorder { get; init; }
        public int Col { get; set; }
        public int Row { get; set; }
    }

    private readonly BoxManager _manager;
    private readonly ShellService _shellService;
    private readonly DesktopHostService _hostService;
    private readonly DispatcherTimer _toastTimer;
    private readonly Dictionary<string, CanvasIconVisual> _visuals = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectedPaths = new(StringComparer.OrdinalIgnoreCase);

    private MonitorDescriptor _monitor;
    private DesktopGridLayoutService.GridMetrics _metrics;
    private bool _isMarqueeSelecting;
    private Point _marqueeStart;
    private HashSet<string> _marqueeInitialSelection = new(StringComparer.OrdinalIgnoreCase);
    private string? _dragCandidatePath;
    private Point _dragStartPoint;
    private DragPreviewWindow? _dragPreview;
    private Point? _lastContextMenuPoint;

    public string MonitorId => _monitor.MonitorId;
    public MonitorDescriptor Monitor => _monitor;

    public DesktopCanvasWindow(
        MonitorDescriptor monitor,
        BoxManager manager,
        ShellService shellService,
        DesktopHostService hostService)
    {
        InitializeComponent();
        _monitor = monitor;
        _manager = manager;
        _shellService = shellService;
        _hostService = hostService;

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastBorder.Visibility = Visibility.Collapsed;
        };

        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) =>
        {
            _toastTimer.Stop();
            DesktopClipboardService.CutStateChanged -= OnCutStateChanged;
        };
        DesktopClipboardService.CutStateChanged += OnCutStateChanged;

        DragEnter += OnCanvasDragOver;
        DragOver += OnCanvasDragOver;
        DragLeave += (_, _) => HideDropCellHighlight();
        Drop += OnCanvasDrop;

        UpdateMonitorBounds(monitor);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hostService.AttachDesktopCanvasBehavior(this, allowZOrderChange: () => _manager.IsReorderingZOrder);
        ApplyPhysicalMonitorRect();
    }

    public void UpdateMonitorBounds(MonitorDescriptor monitor)
    {
        _monitor = monitor;
        double scaleX = Math.Max(0.5, monitor.ScaleX);
        double scaleY = Math.Max(0.5, monitor.ScaleY);
        double wDip = monitor.WorkWidth / scaleX;
        double hDip = monitor.WorkHeight / scaleY;
        _metrics = DesktopGridLayoutService.ComputeMetrics(wDip, hDip);
        ApplyPhysicalMonitorRect();
    }

    private void ApplyPhysicalMonitorRect()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            double scaleX = Math.Max(0.5, _monitor.ScaleX);
            double scaleY = Math.Max(0.5, _monitor.ScaleY);
            Left = _monitor.WorkX / scaleX;
            Top = _monitor.WorkY / scaleY;
            Width = _monitor.WorkWidth / scaleX;
            Height = _monitor.WorkHeight / scaleY;
            return;
        }

        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_BOTTOM,
            _monitor.WorkX,
            _monitor.WorkY,
            _monitor.WorkWidth,
            _monitor.WorkHeight,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER);
    }

    public DesktopGridLayoutService.GridMetrics GetGridMetrics() => _metrics;

    public void RenderItems(IReadOnlyList<(BoxItemEntry Entry, DesktopIconGridPosition Position)> items)
    {
        IconCanvas.Children.Clear();
        _visuals.Clear();

        var validPaths = new HashSet<string>(items.Select(i => i.Entry.Path), StringComparer.OrdinalIgnoreCase);
        _selectedPaths.RemoveWhere(p => !validPaths.Contains(p));

        double dpiScale = Math.Max(_monitor.ScaleX, _monitor.ScaleY);
        int iconPx = Math.Clamp((int)Math.Round(40.0 * dpiScale), 40, 128);

        foreach (var (entry, pos) in items)
        {
            var icon = _shellService.GetIcon(entry.Path, iconPx);
            var visual = CreateIconVisual(entry, icon, pos.Col, pos.Row);
            _visuals[entry.Path] = visual;
            var (left, top) = DesktopGridLayoutService.CellToCanvasPoint(pos.Col, pos.Row, _metrics);
            Canvas.SetLeft(visual.RootBorder, left);
            Canvas.SetTop(visual.RootBorder, top);
            IconCanvas.Children.Add(visual.RootBorder);
        }

        ApplySelectionAndCutVisuals();
    }

    public void ShowToast(string message)
    {
        ToastTextBlock.Text = message;
        ToastBorder.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private CanvasIconVisual CreateIconVisual(BoxItemEntry entry, ImageSource? icon, int col, int row)
    {
        var img = new Image
        {
            Source = icon,
            Width = 40,
            Height = 40,
            Margin = new Thickness(0, 4, 0, 5),
            HorizontalAlignment = HorizontalAlignment.Center,
            Stretch = Stretch.Uniform,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

        var shadowTxt = new TextBlock
        {
            Text = entry.DisplayName,
            Foreground = ShadowTextBrush,
            FontSize = 12,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 34,
            LineHeight = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 1, 0, 0),
            Effect = IconTextShadow,
            IsHitTestVisible = false
        };

        var txt = new TextBlock
        {
            Text = entry.DisplayName,
            Foreground = Brushes.White,
            FontSize = 12,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 34,
            LineHeight = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
        TextOptions.SetTextFormattingMode(txt, TextFormattingMode.Display);

        var labelGrid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center
        };
        labelGrid.Children.Add(shadowTxt);
        labelGrid.Children.Add(txt);

        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top
        };
        panel.Children.Add(img);
        panel.Children.Add(labelGrid);

        var border = new Border
        {
            Width = DesktopGridLayoutService.DefaultCellWidth - 4,
            Height = DesktopGridLayoutService.DefaultCellHeight - 4,
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(4, 5, 4, 4),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Tag = entry.Path,
            ToolTip = entry.DisplayName,
            Child = panel
        };

        border.MouseEnter += (_, _) =>
        {
            if (!_selectedPaths.Contains(entry.Path))
            {
                border.Background = HoverBgBrush;
                border.BorderBrush = HoverBorderBrush;
            }
        };
        border.MouseLeave += (_, _) =>
        {
            if (!_selectedPaths.Contains(entry.Path))
            {
                border.Background = Brushes.Transparent;
                border.BorderBrush = Brushes.Transparent;
            }
        };

        return new CanvasIconVisual
        {
            Path = entry.Path,
            DisplayName = entry.DisplayName,
            Icon = icon,
            RootBorder = border,
            Col = col,
            Row = row
        };
    }

    private void OnCutStateChanged()
    {
        Dispatcher.BeginInvoke(ApplySelectionAndCutVisuals);
    }

    private void ApplySelectionAndCutVisuals()
    {
        foreach (var kv in _visuals)
        {
            bool selected = _selectedPaths.Contains(kv.Key);
            bool cut = DesktopClipboardService.IsPathCut(kv.Key);
            kv.Value.RootBorder.Background = selected ? SelectedBgBrush : Brushes.Transparent;
            kv.Value.RootBorder.BorderBrush = selected ? SelectedBorderBrush : Brushes.Transparent;
            kv.Value.RootBorder.Opacity = cut ? 0.50 : 1.0;
        }
    }

    private string? FindHitIconPath(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is Border b && b.Tag is string path && _visuals.ContainsKey(path))
                return path;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private void RootGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        RootGrid.Focus();
        var hitPath = FindHitIconPath(e.OriginalSource as DependencyObject);

        if (hitPath != null)
        {
            if (e.ClickCount == 2)
            {
                ShellService.OpenItem(hitPath);
                e.Handled = true;
                return;
            }

            bool ctrlHeld = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (ctrlHeld)
            {
                if (!_selectedPaths.Add(hitPath))
                    _selectedPaths.Remove(hitPath);
            }
            else if (!_selectedPaths.Contains(hitPath))
            {
                _selectedPaths.Clear();
                _selectedPaths.Add(hitPath);
            }

            ApplySelectionAndCutVisuals();
            _dragCandidatePath = hitPath;
            _dragStartPoint = e.GetPosition(RootGrid);
            _isMarqueeSelecting = false;
            return;
        }

        // 点击在桌面空白区域
        if (e.ClickCount == 2 && _manager.Config.Tidy.DoubleClickBlankToHide)
        {
            _selectedPaths.Clear();
            ApplySelectionAndCutVisuals();
            _manager.ToggleAllBoxesVisibility();
            e.Handled = true;
            return;
        }

        _dragCandidatePath = null;
        _isMarqueeSelecting = true;
        _marqueeStart = e.GetPosition(RootGrid);

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        _marqueeInitialSelection = ctrl
            ? new HashSet<string>(_selectedPaths, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!ctrl)
        {
            _selectedPaths.Clear();
            ApplySelectionAndCutVisuals();
        }

        RootGrid.CaptureMouse();
    }

    private void RootGrid_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
            return;

        if (_isMarqueeSelecting)
        {
            var cur = e.GetPosition(RootGrid);
            double x = Math.Max(0, Math.Min(_marqueeStart.X, cur.X));
            double y = Math.Max(0, Math.Min(_marqueeStart.Y, cur.Y));
            double w = Math.Abs(cur.X - _marqueeStart.X);
            double h = Math.Abs(cur.Y - _marqueeStart.Y);

            if (w > 3 || h > 3)
            {
                MarqueeRect.Visibility = Visibility.Visible;
                Canvas.SetLeft(MarqueeRect, x);
                Canvas.SetTop(MarqueeRect, y);
                MarqueeRect.Width = w;
                MarqueeRect.Height = h;

                var marquee = new Rect(x, y, w, h);
                _selectedPaths.Clear();
                foreach (var init in _marqueeInitialSelection)
                    _selectedPaths.Add(init);

                foreach (var kv in _visuals)
                {
                    var left = Canvas.GetLeft(kv.Value.RootBorder);
                    var top = Canvas.GetTop(kv.Value.RootBorder);
                    var itemRect = new Rect(left, top, kv.Value.RootBorder.Width, kv.Value.RootBorder.Height);
                    if (marquee.IntersectsWith(itemRect))
                        _selectedPaths.Add(kv.Key);
                }
                ApplySelectionAndCutVisuals();
            }
            return;
        }

        if (_dragCandidatePath == null)
            return;

        var pos = e.GetPosition(RootGrid);
        if (Math.Abs(pos.X - _dragStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(pos.Y - _dragStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            var candidate = _dragCandidatePath;
            _dragCandidatePath = null;

            var paths = _selectedPaths.Contains(candidate)
                ? _selectedPaths.ToList()
                : new List<string> { candidate };

            var data = new DataObject();
            data.SetData(CanvasDragFormat, new CanvasTransferPayload(_monitor.MonitorId, paths));

            var realPaths = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
            if (realPaths.Length > 0)
            {
                var sc = new System.Collections.Specialized.StringCollection();
                sc.AddRange(realPaths);
                data.SetFileDropList(sc);
            }

            _visuals.TryGetValue(candidate, out var firstVisual);
            _dragPreview = new DragPreviewWindow(firstVisual?.Icon, firstVisual?.DisplayName ?? Path.GetFileName(candidate), paths.Count);
            _dragPreview.Show();
            _dragPreview.UpdatePositionNearCursor();

            GiveFeedbackEventHandler feedbackHandler = (_, _) => _dragPreview?.UpdatePositionNearCursor();
            QueryContinueDragEventHandler queryHandler = (_, _) => _dragPreview?.UpdatePositionNearCursor();
            RootGrid.GiveFeedback += feedbackHandler;
            RootGrid.QueryContinueDrag += queryHandler;

            try
            {
                DragDrop.DoDragDrop(RootGrid, data, DragDropEffects.Move | DragDropEffects.Copy | DragDropEffects.Link);
            }
            finally
            {
                RootGrid.GiveFeedback -= feedbackHandler;
                RootGrid.QueryContinueDrag -= queryHandler;
                _dragPreview?.Close();
                _dragPreview = null;
                HideDropCellHighlight();
            }
        }
    }

    private void RootGrid_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isMarqueeSelecting)
        {
            _isMarqueeSelecting = false;
            MarqueeRect.Visibility = Visibility.Collapsed;
            if (RootGrid.IsMouseCaptured)
                RootGrid.ReleaseMouseCapture();
        }
        _dragCandidatePath = null;
    }

    private void RootGrid_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        RootGrid.Focus();
        _lastContextMenuPoint = e.GetPosition(RootGrid);
        var hitPath = FindHitIconPath(e.OriginalSource as DependencyObject);

        if (hitPath != null)
        {
            if (!_selectedPaths.Contains(hitPath))
            {
                _selectedPaths.Clear();
                _selectedPaths.Add(hitPath);
                ApplySelectionAndCutVisuals();
            }

            if (_selectedPaths.Count == 1)
            {
                NativeMethods.GetCursorPos(out var pt);
                ShellService.ShowNativeContextMenu(
                    hitPath,
                    pt.X,
                    pt.Y,
                    onRemoveFromBox: () => _manager.CreateBoxWithItems([hitPath]),
                    onRefreshAfterShellCommand: () => _manager.RefreshAllDesktopCanvases(),
                    customTopActionLabel: "新建格子并收纳此项");
                _manager.EnsureCanvasesAtBottom();
            }
            else
            {
                ShowMultiSelectIconContextMenu(_selectedPaths.ToList());
            }

            e.Handled = true;
            return;
        }

        NativeMethods.GetCursorPos(out var cursorPt);
        if (!ShellService.ShowNativeDesktopBackgroundContextMenu(
            cursorPt.X,
            cursorPt.Y,
            _manager.StorageService.UserDesktopDirectory,
            onArrangeIcons: (field, desc) => _manager.ArrangeDesktopCanvasIcons(field, desc),
            onRefreshDesktop: () =>
            {
                _manager.RefreshAllDesktopCanvases();
                ShowToast("已刷新桌面");
            },
            onTogglePureDesktop: () => _manager.ToggleAllBoxesVisibility(),
            onAfterShellCommand: () => _manager.RefreshAllDesktopCanvases()))
        {
            ShowBlankDesktopContextMenu();
        }
        _manager.EnsureCanvasesAtBottom();
        e.Handled = true;
    }

    private void ShowMultiSelectIconContextMenu(IReadOnlyList<string> paths)
    {
        var menu = new ContextMenu();

        var newBoxItem = new MenuItem { Header = $"新建格子并收纳（共 {paths.Count} 项）" };
        newBoxItem.Click += (_, _) => _manager.CreateBoxWithItems(paths);
        menu.Items.Add(newBoxItem);

        var moveToSub = new MenuItem { Header = "移动到已有格子" };
        foreach (var box in _manager.Config.Boxes.Where(b => !b.IsUnorganizedBox && string.IsNullOrWhiteSpace(b.MappedFolderPath)))
        {
            var targetBoxId = box.Id;
            var mi = new MenuItem { Header = box.Title };
            mi.Click += (_, _) => _manager.AddItemsToBox(targetBoxId, paths);
            moveToSub.Items.Add(mi);
        }
        if (moveToSub.Items.Count > 0)
            menu.Items.Add(moveToSub);

        menu.Items.Add(new Separator());

        var copyItem = new MenuItem { Header = "复制 (Ctrl+C)" };
        copyItem.Click += (_, _) => PerformCopy(isCut: false);
        menu.Items.Add(copyItem);

        var cutItem = new MenuItem { Header = "剪切 (Ctrl+X)" };
        cutItem.Click += (_, _) => PerformCopy(isCut: true);
        menu.Items.Add(cutItem);

        menu.Items.Add(new Separator());

        var delItem = new MenuItem { Header = $"删除到回收站（共 {paths.Count} 项）" };
        delItem.Click += (_, _) => PerformDelete(permanent: false);
        menu.Items.Add(delItem);

        menu.IsOpen = true;
    }

    private void ShowBlankDesktopContextMenu()
    {
        var menu = new ContextMenu();

        var newBox = new MenuItem { Header = "新建桌面格子" };
        newBox.Click += (_, _) => _manager.CreateNewBox();
        menu.Items.Add(newBox);

        var createSub = new MenuItem { Header = "新建" };
        var newFolder = new MenuItem { Header = "文件夹" };
        newFolder.Click += (_, _) =>
        {
            var created = DesktopClipboardService.CreateNewFolder(_manager.StorageService.UserDesktopDirectory);
            if (created != null)
            {
                PlacePathAtCanvasPoint(created, _lastContextMenuPoint);
                _manager.RefreshAllDesktopCanvases();
                ShowToast($"已新建文件夹「{Path.GetFileName(created)}」");
            }
        };
        createSub.Items.Add(newFolder);

        var newTxt = new MenuItem { Header = "文本文档" };
        newTxt.Click += (_, _) =>
        {
            var created = DesktopClipboardService.CreateNewTextFile(_manager.StorageService.UserDesktopDirectory);
            if (created != null)
            {
                PlacePathAtCanvasPoint(created, _lastContextMenuPoint);
                _manager.RefreshAllDesktopCanvases();
                ShowToast($"已新建「{Path.GetFileName(created)}」");
            }
        };
        createSub.Items.Add(newTxt);
        menu.Items.Add(createSub);

        menu.Items.Add(new Separator());

        var pasteItem = new MenuItem
        {
            Header = "粘贴 (Ctrl+V)",
            IsEnabled = System.Windows.Clipboard.ContainsFileDropList()
        };
        pasteItem.Click += (_, _) => PerformPaste(_lastContextMenuPoint);
        menu.Items.Add(pasteItem);

        var sortSub = new MenuItem { Header = "排列图标" };
        void AddSort(string label, BoxSortField field, bool desc = false)
        {
            var mi = new MenuItem { Header = label };
            mi.Click += (_, _) => _manager.ArrangeDesktopCanvasIcons(field, desc);
            sortSub.Items.Add(mi);
        }
        AddSort("按名称排序", BoxSortField.Name);
        AddSort("按修改时间排序", BoxSortField.ModifiedTime, desc: true);
        AddSort("按类型排序", BoxSortField.Type);
        AddSort("按大小排序", BoxSortField.Size, desc: true);
        sortSub.Items.Add(new Separator());
        AddSort("自动对齐网格", BoxSortField.Custom);
        menu.Items.Add(sortSub);

        var refreshItem = new MenuItem { Header = "刷新桌面 (F5)" };
        refreshItem.Click += (_, _) =>
        {
            _manager.RefreshAllDesktopCanvases();
            ShowToast("已刷新桌面");
        };
        menu.Items.Add(refreshItem);

        menu.Items.Add(new Separator());

        var pureDesktopItem = new MenuItem { Header = "隐藏全部图标（纯净桌面）" };
        pureDesktopItem.Click += (_, _) => _manager.ToggleAllBoxesVisibility();
        menu.Items.Add(pureDesktopItem);

        var settingsItem = new MenuItem { Header = "打开设置中心..." };
        settingsItem.Click += (_, _) => _manager.ShowSettingsWindow();
        menu.Items.Add(settingsItem);

        var pauseItem = new MenuItem { Header = "临时退出桌面整理" };
        pauseItem.Click += (_, _) => _manager.SetTidyPaused(true);
        menu.Items.Add(pauseItem);

        menu.IsOpen = true;
    }

    private void RootGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (ctrl && e.Key == Key.A)
        {
            _selectedPaths.Clear();
            foreach (var p in _visuals.Keys)
                _selectedPaths.Add(p);
            ApplySelectionAndCutVisuals();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.C)
        {
            PerformCopy(isCut: false);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.X)
        {
            PerformCopy(isCut: true);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.V)
        {
            PerformPaste(null);
            e.Handled = true;
        }
        else if (e.Key == Key.F2 && _selectedPaths.Count == 1)
        {
            PerformRename(_selectedPaths.First());
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && _selectedPaths.Count > 0)
        {
            PerformDelete(permanent: shift);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _selectedPaths.Count > 0)
        {
            foreach (var p in _selectedPaths.ToList())
                ShellService.OpenItem(p);
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            _manager.RefreshAllDesktopCanvases();
            ShowToast("已刷新桌面");
            e.Handled = true;
        }
    }

    private void PerformCopy(bool isCut)
    {
        if (_selectedPaths.Count == 0)
            return;

        int count = DesktopClipboardService.CopyPathsToClipboard(_selectedPaths, isCut);
        if (count > 0)
        {
            ShowToast(isCut ? $"已剪切 {count} 个项目" : $"已复制 {count} 个项目");
        }
    }

    private void PerformPaste(Point? preferredPoint)
    {
        if (!DesktopClipboardService.TryReadClipboardFiles(out var sourcePaths, out var isMove))
            return;

        var hwnd = new WindowInteropHelper(this).Handle;
        var res = DesktopClipboardService.PasteFilesToDirectory(
            sourcePaths,
            _manager.StorageService.UserDesktopDirectory,
            isMove,
            hwnd);

        if (res.PastedPaths.Count == 0)
            return;

        // 若是剪切自某个格子，同步从所有格子中移出，让它们出现在桌面画布上
        if (isMove)
        {
            foreach (var moved in res.MovedSourcePaths)
            {
                _manager.RemovePathFromOtherBoxes(moved, excludeBoxId: string.Empty, syncDesktopImmediately: false);
            }
        }

        foreach (var p in res.PastedPaths)
        {
            PlacePathAtCanvasPoint(p, preferredPoint);
        }

        _manager.RefreshAllDesktopCanvases();
        _manager.RefreshAllBoxes();
        _manager.SaveConfigDebounced();
        ShowToast($"已粘贴 {res.PastedPaths.Count} 个项目到桌面");
    }

    private void PerformRename(string path)
    {
        var newName = RenamePromptDialog.Show(path, this);
        if (string.IsNullOrWhiteSpace(newName))
            return;

        var renamed = DesktopClipboardService.TryRenameFileSystemEntry(path, newName);
        if (renamed == null)
        {
            ShowToast("重命名失败（名称无效或目标已存在）");
            return;
        }

        if (_manager.Config.DesktopIconPositions.TryGetValue(path, out var oldPos))
        {
            _manager.Config.DesktopIconPositions.Remove(path);
            _manager.Config.DesktopIconPositions[renamed] = oldPos;
        }

        _selectedPaths.Remove(path);
        _selectedPaths.Add(renamed);
        _manager.SaveConfigDebounced();
        _manager.RefreshAllDesktopCanvases();
        ShowToast($"已重命名为「{Path.GetFileName(renamed)}」");
    }

    private void PerformDelete(bool permanent)
    {
        var targets = _selectedPaths.ToList();
        if (targets.Count == 0)
            return;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (ShellService.DeleteItems(targets, permanent, hwnd))
        {
            foreach (var t in targets)
            {
                _selectedPaths.Remove(t);
                _manager.Config.DesktopIconPositions.Remove(t);
            }
            _manager.SaveConfigDebounced();
            _manager.RefreshAllDesktopCanvases();
        }
    }

    private void PlacePathAtCanvasPoint(string path, Point? canvasPoint)
    {
        var occupied = _visuals.Values
            .Where(v => !string.Equals(v.Path, path, StringComparison.OrdinalIgnoreCase))
            .Select(v => (v.Col, v.Row))
            .ToHashSet();

        (int col, int row) cell;
        if (canvasPoint.HasValue)
        {
            var desired = DesktopGridLayoutService.CanvasPointToCell(canvasPoint.Value.X, canvasPoint.Value.Y, _metrics);
            cell = DesktopGridLayoutService.FindNearestFreeCell(desired.Col, desired.Row, _metrics, occupied);
        }
        else
        {
            cell = DesktopGridLayoutService.FindFirstFreeCellColumnMajor(_metrics, occupied);
        }

        _manager.Config.DesktopIconPositions[path] = new DesktopIconGridPosition
        {
            MonitorId = _monitor.MonitorId,
            Col = cell.col,
            Row = cell.row
        };
    }

    private void OnCanvasDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(CanvasDragFormat) ||
            e.Data.GetDataPresent(DeskBoxWindow.InternalDragFormat) ||
            e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = (e.KeyStates & DragDropKeyStates.ControlKey) != 0
                ? DragDropEffects.Copy
                : DragDropEffects.Move;

            var pt = e.GetPosition(RootGrid);
            var (col, row) = DesktopGridLayoutService.CanvasPointToCell(pt.X, pt.Y, _metrics);
            var (left, top) = DesktopGridLayoutService.CellToCanvasPoint(col, row, _metrics);
            DropCellHighlight.Width = DesktopGridLayoutService.DefaultCellWidth - 4;
            DropCellHighlight.Height = DesktopGridLayoutService.DefaultCellHeight - 4;
            Canvas.SetLeft(DropCellHighlight, left);
            Canvas.SetTop(DropCellHighlight, top);
            DropCellHighlight.Visibility = Visibility.Visible;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.None;
        HideDropCellHighlight();
        e.Handled = true;
    }

    private void HideDropCellHighlight()
    {
        DropCellHighlight.Visibility = Visibility.Collapsed;
    }

    private void OnCanvasDrop(object sender, DragEventArgs e)
    {
        HideDropCellHighlight();
        var dropPt = e.GetPosition(RootGrid);
        var hitIconPath = FindHitIconPath(e.OriginalSource as DependencyObject);

        // 1. 优先处理从格子拖出到桌面画布！
        if (e.Data.GetDataPresent(DeskBoxWindow.InternalDragFormat) &&
            e.Data.GetData(DeskBoxWindow.InternalDragFormat) is DeskBoxWindow.ItemTransferPayload boxPayload)
        {
            if (TryHandleDropOntoDesktopTargetIcon(boxPayload.ItemPaths, hitIconPath, e))
            {
                e.Handled = true;
                return;
            }

            var occupied = _visuals.Values.Select(v => (v.Col, v.Row)).ToHashSet();
            var desired = DesktopGridLayoutService.CanvasPointToCell(dropPt.X, dropPt.Y, _metrics);

            foreach (var p in boxPayload.ItemPaths)
            {
                var cell = DesktopGridLayoutService.FindNearestFreeCell(desired.Col, desired.Row, _metrics, occupied);
                occupied.Add(cell);
                _manager.Config.DesktopIconPositions[p] = new DesktopIconGridPosition
                {
                    MonitorId = _monitor.MonitorId,
                    Col = cell.Col,
                    Row = cell.Row
                };
            }

            _manager.MoveItemsOutOfBoxToDesktopCanvas(boxPayload.SourceBoxId, boxPayload.ItemPaths);
            ShowToast($"已移出 {boxPayload.ItemPaths.Count} 个项目到桌面");
            e.Handled = true;
            return;
        }

        // 2. 桌面画布内部（含跨显示器）自由拖拽重新定位
        if (e.Data.GetDataPresent(CanvasDragFormat) &&
            e.Data.GetData(CanvasDragFormat) is CanvasTransferPayload canvasPayload)
        {
            if (TryHandleDropOntoDesktopTargetIcon(canvasPayload.ItemPaths, hitIconPath, e))
            {
                e.Handled = true;
                return;
            }

            var movingSet = new HashSet<string>(canvasPayload.ItemPaths, StringComparer.OrdinalIgnoreCase);
            var occupied = _visuals.Values
                .Where(v => !movingSet.Contains(v.Path))
                .Select(v => (v.Col, v.Row))
                .ToHashSet();

            var desired = DesktopGridLayoutService.CanvasPointToCell(dropPt.X, dropPt.Y, _metrics);
            foreach (var p in canvasPayload.ItemPaths)
            {
                var cell = DesktopGridLayoutService.FindNearestFreeCell(desired.Col, desired.Row, _metrics, occupied);
                occupied.Add(cell);
                _manager.Config.DesktopIconPositions[p] = new DesktopIconGridPosition
                {
                    MonitorId = _monitor.MonitorId,
                    Col = cell.Col,
                    Row = cell.Row
                };
            }

            _manager.SaveConfigDebounced();
            _manager.RefreshAllDesktopCanvases();
            e.Handled = true;
            return;
        }

        // 3. 从外部窗口（如资源管理器）拖入桌面画布
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files &&
            files.Length > 0)
        {
            if (TryHandleDropOntoDesktopTargetIcon(files, hitIconPath, e))
            {
                e.Handled = true;
                return;
            }

            bool isCopy = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
            var hwnd = new WindowInteropHelper(this).Handle;
            var res = DesktopClipboardService.PasteFilesToDirectory(
                files,
                _manager.StorageService.UserDesktopDirectory,
                isMove: !isCopy,
                ownerHwnd: hwnd);

            if (res.PastedPaths.Count > 0)
            {
                var occupied = _visuals.Values.Select(v => (v.Col, v.Row)).ToHashSet();
                var desired = DesktopGridLayoutService.CanvasPointToCell(dropPt.X, dropPt.Y, _metrics);
                foreach (var p in res.PastedPaths)
                {
                    var cell = DesktopGridLayoutService.FindNearestFreeCell(desired.Col, desired.Row, _metrics, occupied);
                    occupied.Add(cell);
                    _manager.Config.DesktopIconPositions[p] = new DesktopIconGridPosition
                    {
                        MonitorId = _monitor.MonitorId,
                        Col = cell.Col,
                        Row = cell.Row
                    };
                }
                _manager.SaveConfigDebounced();
                _manager.RefreshAllDesktopCanvases();
                ShowToast(isCopy ? $"已复制 {res.PastedPaths.Count} 个项目到桌面" : $"已移动 {res.PastedPaths.Count} 个项目到桌面");
            }
            e.Handled = true;
        }
    }

    private bool TryHandleDropOntoDesktopTargetIcon(IReadOnlyList<string> draggedPaths, string? hitIconPath, DragEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(hitIconPath) || draggedPaths.Count == 0)
            return false;

        if (ShellService.CanDropIntoFolder(draggedPaths, hitIconPath))
        {
            bool copy = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
            var hwnd = new WindowInteropHelper(this).Handle;
            foreach (var p in draggedPaths)
                ShellService.MoveOrCopyItemToFolder(p, hitIconPath, copy, hwnd);
            _manager.RefreshAllDesktopCanvases();
            _manager.RefreshAllBoxes();
            return true;
        }

        if (ShellService.CanDropOntoApplication(draggedPaths, hitIconPath))
        {
            ShellService.OpenFilesWithApplication(hitIconPath, draggedPaths);
            return true;
        }

        return false;
    }
}
