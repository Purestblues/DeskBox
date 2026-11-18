namespace DeskBox.Models;

/// <summary>
/// 描述一台物理显示器的稳定标识与工作区信息（单位：物理像素）。
/// </summary>
public sealed class MonitorDescriptor
{
    public string MonitorId { get; init; } = string.Empty;
    public string DeviceName { get; init; } = string.Empty;
    public string FriendlyName { get; init; } = string.Empty;
    public bool IsPrimary { get; init; }
    public int BoundsX { get; init; }
    public int BoundsY { get; init; }
    public int BoundsWidth { get; init; }
    public int BoundsHeight { get; init; }
    public int WorkX { get; init; }
    public int WorkY { get; init; }
    public int WorkWidth { get; init; }
    public int WorkHeight { get; init; }
    public uint DpiX { get; init; } = 96;
    public uint DpiY { get; init; } = 96;

    public double ScaleX => DpiX / 96.0;
    public double ScaleY => DpiY / 96.0;

    public string Summary =>
        $"{FriendlyName} ({BoundsWidth}x{BoundsHeight} @ {(int)Math.Round(ScaleX * 100)}%{(IsPrimary ? ", 主屏" : "")})";
}
