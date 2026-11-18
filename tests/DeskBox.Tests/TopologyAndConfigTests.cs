using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class TopologyAndConfigTests
{
    private static MonitorDescriptor Primary2880 => new()
    {
        MonitorId = "DEL_2880_MAIN",
        DeviceName = @"\\.\DISPLAY1",
        FriendlyName = "Built-in Display",
        IsPrimary = true,
        BoundsX = 0,
        BoundsY = 0,
        BoundsWidth = 2880,
        BoundsHeight = 1800,
        WorkX = 0,
        WorkY = 0,
        WorkWidth = 2880,
        WorkHeight = 1720,
        DpiX = 192,
        DpiY = 192
    };

    private static MonitorDescriptor SecondaryTop1920 => new()
    {
        MonitorId = "AOC_1920_TOP",
        DeviceName = @"\\.\DISPLAY2",
        FriendlyName = "External Monitor",
        IsPrimary = false,
        BoundsX = 480,
        BoundsY = -1080,
        BoundsWidth = 1920,
        BoundsHeight = 1080,
        WorkX = 480,
        WorkY = -1080,
        WorkWidth = 1920,
        WorkHeight = 1040,
        DpiX = 96,
        DpiY = 96
    };

    [Fact]
    public void CaptureAndResolve_PreservesExactPlacementAcrossMixedDpiDualMonitors()
    {
        var monitors = new[] { Primary2880, SecondaryTop1920 };

        // 格子在主屏右上角
        var primaryRightBox = new MonitorTopologyService.PhysicalRect(2200, 40, 560, 720);
        var placement1 = MonitorTopologyService.CapturePlacement(primaryRightBox, 720, isCollapsed: false, monitors);
        Assert.Equal("DEL_2880_MAIN", placement1.MonitorId);

        var (resolved1, _, target1, matched1) = MonitorTopologyService.ResolvePhysicalPlacement(placement1, monitors);
        Assert.True(matched1);
        Assert.Equal("DEL_2880_MAIN", target1.MonitorId);
        Assert.InRange(Math.Abs(resolved1.X - primaryRightBox.X), 0, 2);
        Assert.InRange(Math.Abs(resolved1.Y - primaryRightBox.Y), 0, 2);
        Assert.InRange(Math.Abs(resolved1.Width - primaryRightBox.Width), 0, 2);
        Assert.InRange(Math.Abs(resolved1.Height - primaryRightBox.Height), 0, 2);

        // 格子在上方副屏（负 Y 坐标、不同 DPI）
        var topMonitorBox = new MonitorTopologyService.PhysicalRect(560, -1000, 380, 460);
        var placement2 = MonitorTopologyService.CapturePlacement(topMonitorBox, 460, isCollapsed: true, monitors);
        Assert.Equal("AOC_1920_TOP", placement2.MonitorId);
        Assert.True(placement2.IsCollapsed);

        var (resolved2, headerH2, target2, matched2) = MonitorTopologyService.ResolvePhysicalPlacement(placement2, monitors);
        Assert.True(matched2);
        Assert.Equal("AOC_1920_TOP", target2.MonitorId);
        Assert.Equal(34, headerH2); // 96 DPI -> 34px
        Assert.InRange(Math.Abs(resolved2.X - topMonitorBox.X), 0, 2);
        Assert.InRange(Math.Abs(resolved2.Y - topMonitorBox.Y), 0, 2);
    }

    [Fact]
    public void Resolve_WhenSecondaryUnplugged_ClampsSafelyToPrimaryWorkArea()
    {
        var dual = new[] { Primary2880, SecondaryTop1920 };
        var topMonitorBox = new MonitorTopologyService.PhysicalRect(560, -1000, 380, 460);
        var placement = MonitorTopologyService.CapturePlacement(topMonitorBox, 460, isCollapsed: false, dual);

        // 副屏拔出，只剩主屏
        var single = new[] { Primary2880 };
        var (resolved, _, target, matchedExact) = MonitorTopologyService.ResolvePhysicalPlacement(placement, single);

        Assert.False(matchedExact);
        Assert.Equal("DEL_2880_MAIN", target.MonitorId);
        Assert.True(resolved.X >= Primary2880.WorkX);
        Assert.True(resolved.Y >= Primary2880.WorkY);
        Assert.True(resolved.X + resolved.Width <= Primary2880.WorkX + Primary2880.WorkWidth);
    }

    [Fact]
    public void TopologySignature_DistinguishesSingleAndDualConfigurations()
    {
        var sigSingle = MonitorTopologyService.ComputeTopologySignature([Primary2880]);
        var sigDual = MonitorTopologyService.ComputeTopologySignature([Primary2880, SecondaryTop1920]);
        var sigDualReordered = MonitorTopologyService.ComputeTopologySignature([SecondaryTop1920, Primary2880]);

        Assert.NotEqual(sigSingle, sigDual);
        Assert.Equal(sigDual, sigDualReordered);
    }

    [Fact]
    public void ConfigService_RoundTripsSnapshotsAndBoxes()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DeskBoxTest_" + Guid.NewGuid().ToString("N"));
        var svc = new ConfigService(tempDir);

        var cfg = new AppConfig
        {
            General = new GeneralSettings { ToggleHotkey = "Alt+D" },
            Tidy = new TidySettings { AutoExpandOnHover = true, EnableFrostedGlass = true },
            Boxes =
            [
                new BoxConfig
                {
                    Id = "b1",
                    Title = "Common",
                    ViewMode = BoxViewMode.Grid,
                    Items = [new BoxItemEntry { Path = @"C:\test.txt" }]
                }
            ],
            LayoutSnapshots = new Dictionary<string, TopologyLayoutSnapshot>
            {
                ["SIG1"] = new TopologyLayoutSnapshot
                {
                    Signature = "SIG1",
                    Description = "Dual",
                    Placements = new Dictionary<string, BoxPlacement>
                    {
                        ["b1"] = new BoxPlacement { MonitorId = "M1", RelX = 0.5, RelY = 0.1 }
                    }
                }
            }
        };

        svc.SaveImmediate(cfg);
        var loaded = svc.LoadOrCreateDefault();

        Assert.Equal("Alt+D", loaded.General.ToggleHotkey);
        Assert.Single(loaded.Boxes);
        Assert.Equal("Common", loaded.Boxes[0].Title);
        Assert.Equal(BoxViewMode.Grid, loaded.Boxes[0].ViewMode);
        Assert.True(loaded.LayoutSnapshots.ContainsKey("SIG1"));
        Assert.Equal(0.5, loaded.LayoutSnapshots["SIG1"].Placements["b1"].RelX);
    }

    [Fact]
    public void SortItems_SortsByNameAscendingAndDescending()
    {
        var items = new[]
        {
            new BoxItemEntry { Path = @"C:\b.txt" },
            new BoxItemEntry { Path = @"C:\a.txt" },
            new BoxItemEntry { Path = @"C:\c.txt" }
        };

        var asc = ShellService.SortItems(items, BoxSortField.Name, false).Select(x => x.DisplayName).ToList();
        var desc = ShellService.SortItems(items, BoxSortField.Name, true).Select(x => x.DisplayName).ToList();

        Assert.Equal(["a.txt", "b.txt", "c.txt"], asc);
        Assert.Equal(["c.txt", "b.txt", "a.txt"], desc);
    }

    [Fact]
    public void ClampDragPositionToMonitors_PreventsDraggingOutsideOrIntoDeadZone()
    {
        var dual = new[] { Primary2880, SecondaryTop1920 };

        // 1. 尝试把主屏格子拖到主屏最右边外与最底部外
        var (x1, y1) = MonitorTopologyService.ClampDragPositionToMonitors(
            candidateX: 2700, candidateY: 1700, width: 400, visibleHeight: 600,
            cursorX: 2800, cursorY: 1650, monitors: dual);
        Assert.Equal(2880 - 400, x1);
        Assert.Equal(1720 - 600, y1);

        // 2. 光标在上方副屏 (WorkX=480..2400, WorkY=-1080..-40) 时，禁止拖入左右两侧黑洞 (x < 480)
        var (x2, y2) = MonitorTopologyService.ClampDragPositionToMonitors(
            candidateX: 100, candidateY: -1200, width: 380, visibleHeight: 400,
            cursorX: 520, cursorY: -500, monitors: dual);
        Assert.Equal(480, x2);
        Assert.Equal(-1080, y2);
    }

    [Fact]
    public void BoxStorageService_MigratesCollisionWithoutOverwritingDesktopFile()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DeskBoxStorageTest_" + Guid.NewGuid().ToString("N"));
        var fakeDesktop = Path.Combine(tempRoot, "Desktop");
        var fakeStorage = Path.Combine(tempRoot, "Storage");
        Directory.CreateDirectory(fakeDesktop);
        Directory.CreateDirectory(Path.Combine(fakeStorage, "box1"));

        var existingDesktopFile = Path.Combine(fakeDesktop, "teapot.txt");
        File.WriteAllText(existingDesktopFile, "new_tea");
        var legacyStoredFile = Path.Combine(fakeStorage, "box1", "teapot.txt");
        File.WriteAllText(legacyStoredFile, "old_tea");

        var cfg = new AppConfig
        {
            Boxes = [new BoxConfig { Id = "box1", Title = "Box 1" }]
        };

        var svc = new BoxStorageService(fakeStorage, fakeDesktop);
        Assert.True(svc.IsInUserDesktop(existingDesktopFile));
        Assert.True(svc.RestoreLegacyStorageToDesktopAndConfig(cfg));

        Assert.Equal("new_tea", File.ReadAllText(existingDesktopFile));
        var renamedRestore = Path.Combine(fakeDesktop, "teapot (1).txt");
        Assert.True(File.Exists(renamedRestore));
        Assert.Equal("old_tea", File.ReadAllText(renamedRestore));
        Assert.Contains(cfg.Boxes[0].Items, i => string.Equals(i.Path, renamedRestore, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReorderCustomOrder_SupportsMoveUpMoveDownAndBatchMove()
    {
        var list = new[] { "A", "B", "C", "D" };

        // 把 B 移到 C 下面（insertIndex = 3 -> A, C, B, D）
        var movedDown = ShellService.ReorderCustomOrder(list, ["B"], insertIndex: 3);
        Assert.Equal(["A", "C", "B", "D"], movedDown);

        // 把 C 移到最顶上（insertIndex = 0 -> C, A, B, D）
        var movedUp = ShellService.ReorderCustomOrder(list, ["C"], insertIndex: 0);
        Assert.Equal(["C", "A", "B", "D"], movedUp);

        // 多选 A 和 C 一起移到 D 下面（insertIndex = 4 -> B, D, A, C）
        var movedBatch = ShellService.ReorderCustomOrder(list, ["A", "C"], insertIndex: 4);
        Assert.Equal(["B", "D", "A", "C"], movedBatch);
    }

    [Theory]
    [InlineData(160)]
    [InlineData(230)]
    [InlineData(248)]
    [InlineData(310)]
    public void ComputeUniformGridCellWidth_FillsRowEvenlyWithoutOverflow(double availableWidth)
    {
        double cellW = DeskBox.Views.DeskBoxWindow.ComputeUniformGridCellWidth(availableWidth);
        double usable = availableWidth - 8.0;
        int cols = (int)Math.Floor(usable / cellW);

        Assert.True(cols >= 2);
        Assert.True(cols * cellW <= usable);
        // 剩余不可再均分的边缘像素不超过列数本身
        Assert.True(usable - cols * cellW < cols);
    }

    [Fact]
    public void BoxStorageService_RestoresLegacyStorageAndEnumeratesUnorganizedDesktop()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DeskBoxRefTest_" + Guid.NewGuid().ToString("N"));
        var fakeUserDesktop = Path.Combine(tempRoot, "UserDesktop");
        var fakePublicDesktop = Path.Combine(tempRoot, "PublicDesktop");
        var fakeStorage = Path.Combine(tempRoot, "Storage");
        Directory.CreateDirectory(fakeUserDesktop);
        Directory.CreateDirectory(fakePublicDesktop);
        Directory.CreateDirectory(Path.Combine(fakeStorage, "box1"));

        var trappedFolder = Path.Combine(fakeStorage, "box1", "WorkFolder");
        Directory.CreateDirectory(trappedFolder);
        File.WriteAllText(Path.Combine(trappedFolder, "data.txt"), "important");

        var pubShortcut = Path.Combine(fakePublicDesktop, "WeChat.lnk");
        File.WriteAllText(pubShortcut, "shortcut");

        var unorganizedDoc = Path.Combine(fakeUserDesktop, "Todo.txt");
        File.WriteAllText(unorganizedDoc, "todo");

        var cfg = new AppConfig
        {
            Boxes =
            [
                new BoxConfig { Id = "box1", Title = "Work" },
                new BoxConfig { Id = "unorg", Title = "Unorganized", IsUnorganizedBox = true }
            ]
        };

        var svc = new BoxStorageService(fakeStorage, fakeUserDesktop, fakePublicDesktop);
        Assert.False(svc.IsInUserDesktop(pubShortcut));
        Assert.True(svc.IsInPublicDesktop(pubShortcut));
        Assert.True(svc.IsInAnyDesktop(pubShortcut));

        // 1. 启动恢复把旧版 Storage 内文件夹无损移回桌面并写入引用
        Assert.True(svc.RestoreLegacyStorageToDesktopAndConfig(cfg));
        var restoredFolder = Path.Combine(fakeUserDesktop, "WorkFolder");
        Assert.True(Directory.Exists(restoredFolder));
        Assert.True(File.Exists(Path.Combine(restoredFolder, "data.txt")));
        Assert.Contains(cfg.Boxes[0].Items, i => string.Equals(i.Path, restoredFolder, StringComparison.OrdinalIgnoreCase));

        // 2. 未整理清单自动排除已归入 box1 的 WorkFolder，仅返回 Todo.txt 与 WeChat.lnk
        var unorganized = svc.EnumerateUnorganizedDesktopEntries(cfg);
        Assert.DoesNotContain(unorganized, i => string.Equals(i.Path, restoredFolder, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(unorganized, i => string.Equals(i.Path, unorganizedDoc, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(unorganized, i => string.Equals(i.Path, pubShortcut, StringComparison.OrdinalIgnoreCase));

        // 3. 桌面视觉过滤器仅收集正常格子路径，不包含动态未整理格子或映射目录格子
        cfg.Boxes.Add(new BoxConfig
        {
            Id = "mapped",
            MappedFolderPath = tempRoot,
            Items = [new BoxItemEntry { Path = unorganizedDoc }]
        });
        var hiddenSet = DesktopIconFilterService.CollectOrganizedPaths(cfg);
        Assert.Contains(restoredFolder, hiddenSet);
        Assert.DoesNotContain(unorganizedDoc, hiddenSet);
    }

    [Fact]
    public void ResolveHideDesktopIconGuid_MapsSystemIconsAccurately()
    {
        Assert.Equal("{645FF040-5081-101B-9F08-00AA002F954E}",
            ShellService.ResolveHideDesktopIconGuid("shell:::{645FF040-5081-101B-9F08-00AA002F954E}"));
        Assert.Equal("{20D04FE0-3AEA-1069-A2D8-08002B30309D}",
            ShellService.ResolveHideDesktopIconGuid("shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}"));
        Assert.Equal("{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}",
            ShellService.ResolveHideDesktopIconGuid("shell:::{26EE0668-A00A-44D7-9371-BEB064C986EE}"));
        Assert.Null(ShellService.ResolveHideDesktopIconGuid(@"C:\Users\test\file.txt"));
    }

    [Fact]
    public void ComputePureDesktopTransition_HidesAndRestoresBothBoxesAndNativeIcons()
    {
        // 1. 正常状态进入纯净桌面：隐藏格子且隐藏可见的原生桌面图标
        var enter = BoxManager.ComputePureDesktopTransition(
            targetHidden: true,
            alwaysHideNativeIcons: false,
            currentlyNativeIconsVisible: true,
            pureDesktopNativeIconsHiddenByUs: false);
        Assert.True(enter.TargetBoxesHidden);
        Assert.Equal(false, enter.SetNativeIconsVisible);
        Assert.True(enter.NextPureDesktopNativeIconsHiddenByUs);

        // 2. 退出纯净桌面：恢复格子与此前由纯净模式隐藏的原生桌面图标
        var exit = BoxManager.ComputePureDesktopTransition(
            targetHidden: false,
            alwaysHideNativeIcons: false,
            currentlyNativeIconsVisible: false,
            pureDesktopNativeIconsHiddenByUs: enter.NextPureDesktopNativeIconsHiddenByUs);
        Assert.False(exit.TargetBoxesHidden);
        Assert.Equal(true, exit.SetNativeIconsVisible);
        Assert.False(exit.NextPureDesktopNativeIconsHiddenByUs);
    }

    [Fact]
    public void ComputePureDesktopTransition_PreservesPreHiddenNativeIconsPreference()
    {
        // 用户原本就在 Explorer 手动关闭了桌面图标，或勾选了“始终隐藏原生图标”
        var enter = BoxManager.ComputePureDesktopTransition(
            targetHidden: true,
            alwaysHideNativeIcons: false,
            currentlyNativeIconsVisible: false,
            pureDesktopNativeIconsHiddenByUs: false);
        Assert.Null(enter.SetNativeIconsVisible);
        Assert.False(enter.NextPureDesktopNativeIconsHiddenByUs);

        var exit = BoxManager.ComputePureDesktopTransition(
            targetHidden: false,
            alwaysHideNativeIcons: false,
            currentlyNativeIconsVisible: false,
            pureDesktopNativeIconsHiddenByUs: false);
        Assert.Null(exit.SetNativeIconsVisible);

        var exitWithAlwaysHide = BoxManager.ComputePureDesktopTransition(
            targetHidden: false,
            alwaysHideNativeIcons: true,
            currentlyNativeIconsVisible: false,
            pureDesktopNativeIconsHiddenByUs: true);
        Assert.Null(exitWithAlwaysHide.SetNativeIconsVisible);
    }

    [Fact]
    public void DesktopIconFilterService_HidesAndRestoresOnlyDeskBoxTrackedDesktopItems()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DeskBoxFilterTest_" + Guid.NewGuid().ToString("N"));
        var fakeUserDesktop = Path.Combine(tempRoot, "UserDesktop");
        var fakePublicDesktop = Path.Combine(tempRoot, "PublicDesktop");
        var externalDir = Path.Combine(tempRoot, "External");
        Directory.CreateDirectory(fakeUserDesktop);
        Directory.CreateDirectory(fakePublicDesktop);
        Directory.CreateDirectory(externalDir);

        var chromeLnk = Path.Combine(fakeUserDesktop, "Google Chrome.lnk");
        var preHiddenFile = Path.Combine(fakeUserDesktop, "PreHidden.txt");
        var externalFile = Path.Combine(externalDir, "Notes.txt");
        var unorganizedFile = Path.Combine(fakePublicDesktop, "FreeApp.lnk");

        File.WriteAllText(chromeLnk, "chrome");
        File.WriteAllText(preHiddenFile, "secret");
        File.SetAttributes(preHiddenFile, File.GetAttributes(preHiddenFile) | FileAttributes.Hidden);
        File.WriteAllText(externalFile, "notes");
        File.WriteAllText(unorganizedFile, "free");

        var storage = new BoxStorageService(Path.Combine(tempRoot, "Storage"), fakeUserDesktop, fakePublicDesktop);
        var filter = new DesktopIconFilterService(new DesktopHostService(), storage, enableShellRefresh: false);

        var cfg = new AppConfig
        {
            Boxes =
            [
                new BoxConfig
                {
                    Id = "b1",
                    Title = "Apps",
                    Items =
                    [
                        new BoxItemEntry { Path = chromeLnk },
                        new BoxItemEntry { Path = preHiddenFile },
                        new BoxItemEntry { Path = externalFile },
                        new BoxItemEntry { Path = "shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}" }
                    ]
                }
            ]
        };

        try
        {
            Assert.True(filter.NeedsResync(cfg));
            Assert.True(filter.SyncHiddenDesktopIcons(cfg));
            Assert.False(filter.NeedsResync(cfg));

            // 1. 仅桌面可见项被设置 Hidden 并登记到 HiddenDesktopPaths
            Assert.True((File.GetAttributes(chromeLnk) & FileAttributes.Hidden) != 0);
            Assert.Contains(chromeLnk, cfg.HiddenDesktopPaths);

            // 2. 非桌面文件与用户原本就隐藏的文件不登记
            Assert.True((File.GetAttributes(externalFile) & FileAttributes.Hidden) == 0);
            Assert.DoesNotContain(externalFile, cfg.HiddenDesktopPaths);
            Assert.DoesNotContain(preHiddenFile, cfg.HiddenDesktopPaths);

            // 3. 桌面未整理列表不包含已隐藏/系统或已归档文件，仅含 FreeApp.lnk
            var unorganized = storage.EnumerateUnorganizedDesktopEntries(cfg);
            Assert.Single(unorganized);
            Assert.Equal(unorganizedFile, unorganized[0].Path);

            // 4. 从格子移出后同步：仅恢复 chromeLnk，不误动 preHiddenFile
            cfg.Boxes[0].Items.RemoveAll(i => i.Path == chromeLnk || i.Path == preHiddenFile);
            Assert.True(filter.NeedsResync(cfg));
            Assert.True(filter.SyncHiddenDesktopIcons(cfg));
            Assert.True((File.GetAttributes(chromeLnk) & FileAttributes.Hidden) == 0);
            Assert.Empty(cfg.HiddenDesktopPaths);
            Assert.True((File.GetAttributes(preHiddenFile) & FileAttributes.Hidden) != 0);

            // 5. RestoreAllHiddenDesktopIcons 也能完整清理
            cfg.Boxes[0].Items.Add(new BoxItemEntry { Path = chromeLnk });
            Assert.True(filter.SyncHiddenDesktopIcons(cfg));
            Assert.True(filter.RestoreAllHiddenDesktopIcons(cfg));
            Assert.True((File.GetAttributes(chromeLnk) & FileAttributes.Hidden) == 0);
            Assert.Empty(cfg.HiddenDesktopPaths);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ResolvePhysicalPlacement_DoesNotOverExpandNarrowBoxOnHighDpi()
    {
        var placement = new BoxPlacement
        {
            MonitorId = Primary2880.MonitorId,
            WasPrimaryMonitor = true,
            RelX = 0.01,
            RelY = 0.02,
            RelWidth = 0.10, // 288px on 2880px wide work area (< old 360px min, >= new 260px min)
            RelExpandedHeight = 0.40,
            IsCollapsed = true
        };

        var (rect, _, _, matched) = MonitorTopologyService.ResolvePhysicalPlacement(placement, [Primary2880]);
        Assert.True(matched);
        Assert.InRange(rect.Width, 286, 290);
    }

    [Fact]
    public void CanMoveOrCopyPathIntoFolder_AndCanDropIntoFolder_ValidateBoundaries()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DeskBoxDropValidateTest_" + Guid.NewGuid().ToString("N"));
        var targetDir = Path.Combine(tempRoot, "TargetFolder");
        var childDir = Path.Combine(targetDir, "SubFolder");
        var siblingDir = Path.Combine(tempRoot, "SiblingFolder");
        var outsideFile = Path.Combine(tempRoot, "Outside.txt");
        var alreadyInsideFile = Path.Combine(targetDir, "Inside.txt");
        var nestedInsideFile = Path.Combine(childDir, "Deep.txt");

        Directory.CreateDirectory(childDir);
        Directory.CreateDirectory(siblingDir);
        File.WriteAllText(outsideFile, "out");
        File.WriteAllText(alreadyInsideFile, "in");
        File.WriteAllText(nestedInsideFile, "deep");

        try
        {
            // 正常外部文件与同级目录允许移入目标目录
            Assert.True(ShellService.CanMoveOrCopyPathIntoFolder(outsideFile, targetDir));
            Assert.True(ShellService.CanMoveOrCopyPathIntoFolder(siblingDir, targetDir));
            // 孙子目录的文件允许移到外层目录
            Assert.True(ShellService.CanMoveOrCopyPathIntoFolder(nestedInsideFile, targetDir));

            // 禁止虚拟路径、不存在路径、拖入自身、直接子项重复移入、父目录拖入子目录
            Assert.False(ShellService.CanMoveOrCopyPathIntoFolder("shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", targetDir));
            Assert.False(ShellService.CanMoveOrCopyPathIntoFolder(outsideFile, Path.Combine(tempRoot, "MissingDir")));
            Assert.False(ShellService.CanMoveOrCopyPathIntoFolder(targetDir, targetDir));
            Assert.False(ShellService.CanMoveOrCopyPathIntoFolder(alreadyInsideFile, targetDir));
            Assert.False(ShellService.CanMoveOrCopyPathIntoFolder(targetDir, childDir));
            Assert.False(ShellService.CanMoveOrCopyPathIntoFolder(tempRoot, targetDir));

            // CanDropIntoFolder：若拖拽列表包含目标文件夹自身或其父目录，一律拒绝（避免排序时误高亮）
            Assert.True(ShellService.CanDropIntoFolder([outsideFile, siblingDir], targetDir));
            Assert.False(ShellService.CanDropIntoFolder([outsideFile, targetDir], targetDir));
            Assert.False(ShellService.CanDropIntoFolder([tempRoot], targetDir));
            Assert.False(ShellService.CanDropIntoFolder([alreadyInsideFile], targetDir));
            Assert.True(ShellService.CanDropIntoFolder([alreadyInsideFile, outsideFile], targetDir));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void MoveOrCopyItemToFolder_MovesHiddenDesktopFileAndRestoresVisibleAttribute()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DeskBoxDropMoveTest_" + Guid.NewGuid().ToString("N"));
        var fakeUserDesktop = Path.Combine(tempRoot, "UserDesktop");
        var fakePublicDesktop = Path.Combine(tempRoot, "PublicDesktop");
        var targetFolder = Path.Combine(fakeUserDesktop, "Projects");
        var desktopFile = Path.Combine(fakeUserDesktop, "Draft.txt");
        var sourceSubDir = Path.Combine(tempRoot, "SrcDir");

        Directory.CreateDirectory(targetFolder);
        Directory.CreateDirectory(fakePublicDesktop);
        Directory.CreateDirectory(sourceSubDir);
        File.WriteAllText(desktopFile, "draft-content");
        File.WriteAllText(Path.Combine(sourceSubDir, "nested.txt"), "nested-content");

        var storage = new BoxStorageService(Path.Combine(tempRoot, "Storage"), fakeUserDesktop, fakePublicDesktop);
        var filter = new DesktopIconFilterService(new DesktopHostService(), storage, enableShellRefresh: false);

        var cfg = new AppConfig
        {
            Boxes =
            [
                new BoxConfig
                {
                    Id = "b1",
                    Title = "Work",
                    Items =
                    [
                        new BoxItemEntry { Path = targetFolder },
                        new BoxItemEntry { Path = desktopFile }
                    ]
                }
            ]
        };

        try
        {
            // 先通过桌面隐藏服务标记 desktopFile（模拟已收纳桌面文件的真实状态）
            Assert.True(filter.SyncHiddenDesktopIcons(cfg));
            Assert.True((File.GetAttributes(desktopFile) & FileAttributes.Hidden) != 0);
            Assert.Contains(desktopFile, cfg.HiddenDesktopPaths);

            // 移入前清除隐藏属性，再移动到 targetFolder
            Assert.True(filter.RestoreHiddenAttributeForPaths(cfg, [desktopFile]));
            Assert.True(ShellService.MoveOrCopyItemToFolder(desktopFile, targetFolder, copy: false));

            var movedFile = Path.Combine(targetFolder, "Draft.txt");
            Assert.False(File.Exists(desktopFile));
            Assert.True(File.Exists(movedFile));
            Assert.Equal("draft-content", File.ReadAllText(movedFile));
            Assert.True((File.GetAttributes(movedFile) & FileAttributes.Hidden) == 0);
            Assert.DoesNotContain(desktopFile, cfg.HiddenDesktopPaths);

            // 验证复制文件夹到目标目录（走 Shell FO_COPY 路径，不嵌套也不删源目录）
            Assert.True(ShellService.MoveOrCopyItemToFolder(sourceSubDir, targetFolder, copy: true));
            var copiedDir = Path.Combine(targetFolder, "SrcDir");
            Assert.True(Directory.Exists(sourceSubDir));
            Assert.True(Directory.Exists(copiedDir));
            Assert.True(File.Exists(Path.Combine(copiedDir, "nested.txt")));
            Assert.False(Directory.Exists(Path.Combine(copiedDir, "SrcDir")));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TryResolveShortcut_AndCanDropOntoApplication_DistinguishAppAndFolderShortcuts()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DeskBoxShortcutDropTest_" + Guid.NewGuid().ToString("N"));
        var targetFolder = Path.Combine(tempRoot, "WorkDir");
        var appDir = Path.Combine(tempRoot, "App");
        var appExe = Path.Combine(appDir, "pdfcraft.exe");
        var pdfFile = Path.Combine(tempRoot, "sample.pdf");
        var appShortcut = Path.Combine(tempRoot, "PdfCraft.lnk");
        var folderShortcut = Path.Combine(tempRoot, "WorkDir.lnk");

        Directory.CreateDirectory(targetFolder);
        Directory.CreateDirectory(appDir);
        File.WriteAllText(appExe, "stub");
        File.WriteAllText(pdfFile, "pdf-stub");

        CreateTestShortcut(appShortcut, appExe, appDir, "--open");
        CreateTestShortcut(folderShortcut, targetFolder, tempRoot);

        try
        {
            // 1. 解析应用快捷方式的目标路径、参数与工作目录
            Assert.True(ShellService.TryResolveShortcut(appShortcut, out var resolvedExe, out var args, out var workDir));
            Assert.Equal(appExe, resolvedExe, StringComparer.OrdinalIgnoreCase);
            Assert.Equal("--open", args);
            Assert.Equal(appDir, workDir, StringComparer.OrdinalIgnoreCase);

            // 2. 文件夹快捷方式应识别为目录投放目标，而非应用图标
            Assert.Equal(targetFolder, ShellService.TryResolveTargetFolder(folderShortcut), StringComparer.OrdinalIgnoreCase);
            Assert.False(ShellService.IsApplicationDropTarget(folderShortcut));
            Assert.True(ShellService.CanDropIntoFolder([pdfFile], folderShortcut));
            Assert.False(ShellService.CanDropIntoFolder([folderShortcut], folderShortcut));

            // 3. 应用快捷方式与可执行文件支持文件拖入打开，禁止拖入自身或普通文档
            Assert.Null(ShellService.TryResolveTargetFolder(appShortcut));
            Assert.True(ShellService.IsApplicationDropTarget(appShortcut));
            Assert.True(ShellService.IsApplicationDropTarget(appExe));
            Assert.False(ShellService.IsApplicationDropTarget(pdfFile));

            Assert.True(ShellService.CanDropOntoApplication([pdfFile], appShortcut));
            Assert.True(ShellService.CanDropOntoApplication([pdfFile], appExe));
            Assert.False(ShellService.CanDropOntoApplication([appShortcut], appShortcut));
            Assert.False(ShellService.CanDropOntoApplication([appExe], appShortcut));
            Assert.False(ShellService.CanDropOntoApplication([pdfFile], pdfFile));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void BoxAppearance_ConfigRoundTripsAndSynchronizesDefaults()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DeskBoxAppearanceTest_" + Guid.NewGuid().ToString("N"));
        var svc = new ConfigService(tempDir);

        var sourceBox = new BoxConfig
        {
            Id = "b1",
            Title = "Styled Box",
            ThemeColor = "#1B2C3F",
            Opacity = 0.0,
            UseFrostedGlass = false,
            CornerRadius = 12.0,
            BorderStyle = BoxBorderStyle.Accent,
            TitleAlignment = BoxTitleAlignment.Left,
            HeaderBandVisible = true,
            AutoFadeHeaderButtons = true,
            IconSizeTier = BoxSizeTier.Large,
            FontSizeTier = BoxSizeTier.Large,
            EnableTextShadow = true
        };

        var targetBox = new BoxConfig { Id = "b2", Title = "Target" };
        targetBox.CopyAppearanceFrom(sourceBox);
        Assert.Equal("#1B2C3F", targetBox.ThemeColor);
        Assert.Equal(0.0, targetBox.Opacity);
        Assert.False(targetBox.UseFrostedGlass);
        Assert.Equal(12.0, targetBox.CornerRadius);
        Assert.Equal(BoxBorderStyle.Accent, targetBox.BorderStyle);
        Assert.Equal(BoxTitleAlignment.Left, targetBox.TitleAlignment);
        Assert.True(targetBox.HeaderBandVisible);
        Assert.True(targetBox.AutoFadeHeaderButtons);
        Assert.Equal(BoxSizeTier.Large, targetBox.IconSizeTier);
        Assert.Equal(BoxSizeTier.Large, targetBox.FontSizeTier);
        Assert.True(targetBox.EnableTextShadow);

        var tidy = new TidySettings();
        sourceBox.CopyAppearanceToDefaults(tidy);
        var newBox = new BoxConfig { Id = "b3", Title = "New" };
        newBox.ApplyDefaultAppearanceFrom(tidy);
        Assert.Equal("#1B2C3F", newBox.ThemeColor);
        Assert.Equal(12.0, newBox.CornerRadius);
        Assert.Equal(BoxBorderStyle.Accent, newBox.BorderStyle);
        Assert.Equal(BoxTitleAlignment.Left, newBox.TitleAlignment);
        Assert.True(newBox.HeaderBandVisible);
        Assert.True(newBox.AutoFadeHeaderButtons);
        Assert.Equal(BoxSizeTier.Large, newBox.IconSizeTier);
        Assert.Equal(BoxSizeTier.Large, newBox.FontSizeTier);
        Assert.True(newBox.EnableTextShadow);

        var cfg = new AppConfig
        {
            Tidy = tidy,
            Boxes = [sourceBox, newBox]
        };

        try
        {
            svc.SaveImmediate(cfg);
            var loaded = svc.LoadOrCreateDefault();
            Assert.Equal(2, loaded.Boxes.Count);
            Assert.Equal(BoxBorderStyle.Accent, loaded.Boxes[0].BorderStyle);
            Assert.Equal(BoxTitleAlignment.Left, loaded.Boxes[0].TitleAlignment);
            Assert.Equal(BoxSizeTier.Large, loaded.Boxes[0].IconSizeTier);
            Assert.Equal(12.0, loaded.Boxes[0].CornerRadius);
            Assert.True(loaded.Boxes[0].HeaderBandVisible);
            Assert.True(loaded.Boxes[0].EnableTextShadow);
            Assert.False(loaded.Boxes[0].UseFrostedGlass);
            Assert.Equal(BoxSizeTier.Large, loaded.Tidy.DefaultIconSizeTier);

            loaded.Boxes[0].ResetAppearanceToBuiltInDefaults();
            Assert.Equal("#202226", loaded.Boxes[0].ThemeColor);
            Assert.Equal(0.35, loaded.Boxes[0].Opacity);
            Assert.Null(loaded.Boxes[0].UseFrostedGlass);
            Assert.Equal(6.0, loaded.Boxes[0].CornerRadius);
            Assert.Equal(BoxBorderStyle.Subtle, loaded.Boxes[0].BorderStyle);
            Assert.Equal(BoxTitleAlignment.Center, loaded.Boxes[0].TitleAlignment);
            Assert.False(loaded.Boxes[0].HeaderBandVisible);
            Assert.False(loaded.Boxes[0].AutoFadeHeaderButtons);
            Assert.Equal(BoxSizeTier.Medium, loaded.Boxes[0].IconSizeTier);
            Assert.Equal(BoxSizeTier.Medium, loaded.Boxes[0].FontSizeTier);
            Assert.False(loaded.Boxes[0].EnableTextShadow);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(240, BoxSizeTier.Small)]
    [InlineData(240, BoxSizeTier.Medium)]
    [InlineData(240, BoxSizeTier.Large)]
    [InlineData(340, BoxSizeTier.Large)]
    public void ComputeUniformGridCellWidth_SupportsAllIconSizeTiers(double availableWidth, BoxSizeTier tier)
    {
        double cellW = DeskBox.Views.DeskBoxWindow.ComputeUniformGridCellWidth(availableWidth, tier);
        double usable = availableWidth - 8.0;
        int cols = (int)Math.Floor(usable / cellW);

        Assert.True(cols >= 1);
        Assert.True(cols * cellW <= usable);
        Assert.True(usable - cols * cellW < cols);
    }

    [Fact]
    public void DesktopHostService_GetToggleTidyMenuText_SwitchesByPauseState()
    {
        Assert.Equal("临时退出桌面整理", DesktopHostService.GetToggleTidyMenuText(isTidyPaused: false));
        Assert.Equal("开启桌面整理", DesktopHostService.GetToggleTidyMenuText(isTidyPaused: true));
    }

    [Fact]
    public void ConfigService_RoundTripsHideTrayIconAndIsTidyPaused()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DeskBoxTrayAndTidyTest_" + Guid.NewGuid().ToString("N"));
        var svc = new ConfigService(tempDir);
        var cfg = new AppConfig
        {
            General = new GeneralSettings { HideTrayIcon = true },
            Tidy = new TidySettings { IsTidyPaused = true },
            Boxes = [new BoxConfig { Id = "b1", Title = "Test" }]
        };

        try
        {
            svc.SaveImmediate(cfg);
            var loaded = svc.LoadOrCreateDefault();
            Assert.True(loaded.General.HideTrayIcon);
            Assert.True(loaded.Tidy.IsTidyPaused);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ComputeConflictFreeRestorePositions_RelocatesCollidingIconsAndKeepsUnorganizedExact()
    {
        var saved = new Dictionary<string, DesktopIconPoint>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\Users\me\Desktop\Report.pdf"] = new(1227, 1082),
            [@"C:\Users\me\Desktop\Notes.md"] = new(1377, 1082)
        };

        var currentDesktop = new List<(string Name, int CurrentX, int CurrentY)>
        {
            (@"C:\Users\me\Desktop\Report.pdf", 1227, 2306), // 被推挤错位了
            (@"C:\Users\me\Desktop\Notes.md", 1377, 1082),
            (@"C:\Users\me\Desktop\RestoredApp.lnk", 1227, 1082), // 新恢复出来的图标正占用 Report.pdf 原坑位
            (@"C:\Users\me\Desktop\Other.lnk", 36, 36)
        };

        var plan = DesktopIconFilterService.ComputeConflictFreeRestorePositions(saved, currentDesktop, 180, 200);

        Assert.Equal(new DesktopIconPoint(1227, 1082), plan[@"C:\Users\me\Desktop\Report.pdf"]);
        Assert.Equal(new DesktopIconPoint(1377, 1082), plan[@"C:\Users\me\Desktop\Notes.md"]);
        Assert.True(plan.ContainsKey(@"C:\Users\me\Desktop\RestoredApp.lnk"));
        var movedApp = plan[@"C:\Users\me\Desktop\RestoredApp.lnk"];
        Assert.False(Math.Abs(movedApp.X - 1227) < 60 && Math.Abs(movedApp.Y - 1082) < 60);
        Assert.False(plan.ContainsKey(@"C:\Users\me\Desktop\Other.lnk"));
    }

    private static void CreateTestShortcut(string shortcutPath, string targetPath, string workingDirectory, string? arguments = null)
    {
        var link = (ITestShellLinkW)new TestShellLink();
        try
        {
            link.SetPath(targetPath);
            link.SetWorkingDirectory(workingDirectory);
            if (!string.IsNullOrWhiteSpace(arguments))
                link.SetArguments(arguments);
            ((ITestPersistFile)link).Save(shortcutPath, false);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class TestShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface ITestShellLinkW
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
    private interface ITestPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
