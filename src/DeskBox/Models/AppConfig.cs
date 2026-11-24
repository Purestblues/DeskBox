using System.Text.Json.Serialization;

namespace DeskBox.Models;

public enum BoxViewMode
{
    List,
    Grid
}

public enum BoxSortField
{
    Custom,
    Name,
    ModifiedTime,
    Type,
    Size
}

public enum DropBehavior
{
    ReferenceOnly,
    MoveToBoxFolder,
    CopyToBoxFolder
}

public enum BoxBorderStyle
{
    Subtle,
    None,
    Accent
}

public enum BoxTitleAlignment
{
    Center,
    Left
}

public enum BoxSizeTier
{
    Small,
    Medium,
    Large
}

public sealed record ThemeColorPreset(string Name, string Hex);

public static class BoxAppearancePresets
{
    public static readonly IReadOnlyList<ThemeColorPreset> Colors =
    [
        new("经典深灰", "#202226"),
        new("曜石纯黑", "#121417"),
        new("深海蓝", "#1B2C3F"),
        new("森林绿", "#1B2E25"),
        new("暗夜紫", "#2A2138"),
        new("琥珀棕", "#33261E"),
        new("勃艮第红", "#361D24"),
        new("莫兰迪灰蓝", "#384654"),
        new("薄荷青", "#233B3A"),
        new("樱花粉", "#F5E6EA"),
        new("暖阳米白", "#F4EFE6"),
        new("清透浅白", "#EFF2F7")
    ];
}

public readonly record struct DesktopIconPoint(int X, int Y);

public sealed class DesktopIconGridPosition
{
    public string MonitorId { get; set; } = string.Empty;
    public int Col { get; set; }
    public int Row { get; set; }
}

public sealed class AppConfig
{
    public int ConfigVersion { get; set; } = 2;
    public GeneralSettings General { get; set; } = new();
    public TidySettings Tidy { get; set; } = new();
    public List<BoxConfig> Boxes { get; set; } = new();
    public Dictionary<string, TopologyLayoutSnapshot> LayoutSnapshots { get; set; } = new();
    public HashSet<string> HiddenSystemDesktopGuids { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // 仅用于迁移清理旧版本遗留的隐藏属性，新版本永不再写入 Hidden 属性
    public HashSet<string> HiddenDesktopPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DesktopIconPoint> SavedDesktopIconPositions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // 自绘桌面画布上未收纳图标的网格坐标（按规范路径索引）
    public Dictionary<string, DesktopIconGridPosition> DesktopIconPositions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public enum UiThemeMode
{
    System,
    Light,
    Dark
}

public sealed class GeneralSettings
{
    public bool AutoStart { get; set; }
    public bool EnableHardwareAcceleration { get; set; } = false;
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public bool HideTrayIcon { get; set; } = false;
    public string ToggleHotkey { get; set; } = "Alt+Space";
    public UiThemeMode UiTheme { get; set; } = UiThemeMode.System;
    public bool ReduceMotion { get; set; } = false;
}

public sealed class TidySettings
{
    public bool DoubleClickBlankToHide { get; set; } = true;
    public bool EnableFrostedGlass { get; set; } = true;
    public bool AutoExpandOnHover { get; set; } = true;
    public bool HideNativeDesktopIcons { get; set; }
    public bool BoxesHidden { get; set; }
    public bool IsTidyPaused { get; set; }
    public DropBehavior DropAction { get; set; } = DropBehavior.ReferenceOnly;
    public string DefaultThemeColor { get; set; } = "#202226";
    public double DefaultOpacity { get; set; } = 0.35;
    public double DefaultCornerRadius { get; set; } = 6.0;
    public BoxBorderStyle DefaultBorderStyle { get; set; } = BoxBorderStyle.Subtle;
    public BoxTitleAlignment DefaultTitleAlignment { get; set; } = BoxTitleAlignment.Center;
    public bool DefaultHeaderBandVisible { get; set; }
    public bool DefaultAutoFadeHeaderButtons { get; set; }
    public BoxSizeTier DefaultIconSizeTier { get; set; } = BoxSizeTier.Medium;
    public BoxSizeTier DefaultFontSizeTier { get; set; } = BoxSizeTier.Medium;
    public bool DefaultEnableTextShadow { get; set; }
    public BoxSortField DesktopIconSortField { get; set; } = BoxSortField.Custom;
    public bool DesktopIconSortDescending { get; set; }
}

public sealed class BoxConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Title { get; set; } = "新建格子";
    public string ThemeColor { get; set; } = "#202226";
    public double Opacity { get; set; } = 0.35;
    public bool? UseFrostedGlass { get; set; }
    public double CornerRadius { get; set; } = 6.0;
    public BoxBorderStyle BorderStyle { get; set; } = BoxBorderStyle.Subtle;
    public BoxTitleAlignment TitleAlignment { get; set; } = BoxTitleAlignment.Center;
    public bool HeaderBandVisible { get; set; }
    public bool AutoFadeHeaderButtons { get; set; }
    public BoxSizeTier IconSizeTier { get; set; } = BoxSizeTier.Medium;
    public BoxSizeTier FontSizeTier { get; set; } = BoxSizeTier.Medium;
    public bool EnableTextShadow { get; set; }
    public bool IsLocked { get; set; }
    public bool IsCollapsed { get; set; }
    public BoxViewMode ViewMode { get; set; } = BoxViewMode.List;
    public BoxSortField SortField { get; set; } = BoxSortField.Custom;
    public bool SortDescending { get; set; }
    public int IconSize { get; set; } = 32;
    public string? MappedFolderPath { get; set; }
    public bool IsUnorganizedBox { get; set; }
    public List<BoxItemEntry> Items { get; set; } = new();
    public List<string> CustomOrder { get; set; } = new();

    // 兜底的最近一次有效相对布局，供全新拓扑初始化使用
    public BoxPlacement FallbackPlacement { get; set; } = new();

    /// <summary>
    /// 从源格子复制全部外观样式属性。
    /// </summary>
    public void CopyAppearanceFrom(BoxConfig source)
    {
        ThemeColor = source.ThemeColor;
        Opacity = source.Opacity;
        UseFrostedGlass = source.UseFrostedGlass;
        CornerRadius = source.CornerRadius;
        BorderStyle = source.BorderStyle;
        TitleAlignment = source.TitleAlignment;
        HeaderBandVisible = source.HeaderBandVisible;
        AutoFadeHeaderButtons = source.AutoFadeHeaderButtons;
        IconSizeTier = source.IconSizeTier;
        FontSizeTier = source.FontSizeTier;
        EnableTextShadow = source.EnableTextShadow;
    }

    /// <summary>
    /// 使用全局默认外观初始化当前格子。
    /// </summary>
    public void ApplyDefaultAppearanceFrom(TidySettings tidy)
    {
        ThemeColor = tidy.DefaultThemeColor;
        Opacity = tidy.DefaultOpacity;
        UseFrostedGlass = null;
        CornerRadius = tidy.DefaultCornerRadius;
        BorderStyle = tidy.DefaultBorderStyle;
        TitleAlignment = tidy.DefaultTitleAlignment;
        HeaderBandVisible = tidy.DefaultHeaderBandVisible;
        AutoFadeHeaderButtons = tidy.DefaultAutoFadeHeaderButtons;
        IconSizeTier = tidy.DefaultIconSizeTier;
        FontSizeTier = tidy.DefaultFontSizeTier;
        EnableTextShadow = tidy.DefaultEnableTextShadow;
    }

    /// <summary>
    /// 将当前格子外观保存为新建格子的全局默认值。
    /// </summary>
    public void CopyAppearanceToDefaults(TidySettings tidy)
    {
        tidy.DefaultThemeColor = ThemeColor;
        tidy.DefaultOpacity = Opacity;
        if (UseFrostedGlass.HasValue)
            tidy.EnableFrostedGlass = UseFrostedGlass.Value;
        tidy.DefaultCornerRadius = CornerRadius;
        tidy.DefaultBorderStyle = BorderStyle;
        tidy.DefaultTitleAlignment = TitleAlignment;
        tidy.DefaultHeaderBandVisible = HeaderBandVisible;
        tidy.DefaultAutoFadeHeaderButtons = AutoFadeHeaderButtons;
        tidy.DefaultIconSizeTier = IconSizeTier;
        tidy.DefaultFontSizeTier = FontSizeTier;
        tidy.DefaultEnableTextShadow = EnableTextShadow;
    }

    /// <summary>
    /// 重置为内置出厂默认外观。
    /// </summary>
    public void ResetAppearanceToBuiltInDefaults()
    {
        ThemeColor = "#202226";
        Opacity = 0.35;
        UseFrostedGlass = null;
        CornerRadius = 6.0;
        BorderStyle = BoxBorderStyle.Subtle;
        TitleAlignment = BoxTitleAlignment.Center;
        HeaderBandVisible = false;
        AutoFadeHeaderButtons = false;
        IconSizeTier = BoxSizeTier.Medium;
        FontSizeTier = BoxSizeTier.Medium;
        EnableTextShadow = false;
    }
}

public sealed class BoxItemEntry
{
    public string Path { get; set; } = string.Empty;
    public string? CustomName { get; set; }

    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(CustomName))
                return CustomName;

            var name = System.IO.Path.GetFileName(Path.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(name))
                return Path;

            var ext = System.IO.Path.GetExtension(name);
            if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
            {
                return System.IO.Path.GetFileNameWithoutExtension(name);
            }

            return name;
        }
    }
}

/// <summary>
/// 某一套显示器拓扑下所有格子的布局快照，彻底解决热插拔错乱问题。
/// </summary>
public sealed class TopologyLayoutSnapshot
{
    public string Signature { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public Dictionary<string, BoxPlacement> Placements { get; set; } = new();
}

/// <summary>
/// 绑定到具体显示器的归一化相对坐标（与 DPI 和虚拟屏原点无关）。
/// </summary>
public sealed class BoxPlacement
{
    public string MonitorId { get; set; } = string.Empty;
    public bool WasPrimaryMonitor { get; set; } = true;
    public double RelX { get; set; } = 0.02;
    public double RelY { get; set; } = 0.02;
    public double RelWidth { get; set; } = 0.19;
    public double RelExpandedHeight { get; set; } = 0.42;
    public bool IsCollapsed { get; set; }

    public BoxPlacement Clone() => new()
    {
        MonitorId = MonitorId,
        WasPrimaryMonitor = WasPrimaryMonitor,
        RelX = RelX,
        RelY = RelY,
        RelWidth = RelWidth,
        RelExpandedHeight = RelExpandedHeight,
        IsCollapsed = IsCollapsed
    };
}
