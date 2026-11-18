using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeskBox.Models;
using Microsoft.Win32;

namespace DeskBox.Services;

/// <summary>
/// 使用 WIC 原生下采样解码 + 1/6 尺度 3-Pass 高斯模糊生成桌面壁纸缓存（单张仅 ~0.6MB），
/// 完全不引入 System.Drawing / GDI+，也不在内存保留大图。
/// </summary>
public sealed class WallpaperBlurService : IDisposable
{
    private readonly Dictionary<string, BitmapSource> _blurredByMonitorSize = new();
    private readonly object _lock = new();
    private string? _cachedWallpaperSignature;

    public event Action? WallpaperChanged;

    public WallpaperBlurService()
    {
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.Desktop)
        {
            Invalidate();
            WallpaperChanged?.Invoke();
        }
    }

    public void Invalidate()
    {
        lock (_lock)
        {
            _blurredByMonitorSize.Clear();
            _cachedWallpaperSignature = null;
        }
    }

    public BitmapSource? GetBlurredWallpaperForMonitor(MonitorDescriptor monitor)
    {
        var (path, style, tile) = ReadDesktopWallpaperSetting();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        var lastWrite = File.GetLastWriteTimeUtc(path).Ticks;
        var sig = $"{path}|{lastWrite}|{style}|{tile}";
        var key = $"{monitor.BoundsWidth}x{monitor.BoundsHeight}";

        lock (_lock)
        {
            if (_cachedWallpaperSignature != sig)
            {
                _blurredByMonitorSize.Clear();
                _cachedWallpaperSignature = sig;
            }

            if (_blurredByMonitorSize.TryGetValue(key, out var existing))
                return existing;

            var created = BuildBlurredForMonitor(path, style, tile, monitor.BoundsWidth, monitor.BoundsHeight);
            if (created != null)
            {
                _blurredByMonitorSize[key] = created;
            }
            return created;
        }
    }

    private static (string? path, int style, int tile) ReadDesktopWallpaperSetting()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", writable: false);
            var wp = key?.GetValue("WallPaper")?.ToString();
            int.TryParse(key?.GetValue("WallpaperStyle")?.ToString(), out var style);
            int.TryParse(key?.GetValue("TileWallpaper")?.ToString(), out var tile);

            if (!string.IsNullOrWhiteSpace(wp) && File.Exists(wp))
                return (wp, style, tile);
        }
        catch { }

        var transcoded = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Themes\TranscodedWallpaper");
        return File.Exists(transcoded) ? (transcoded, 10, 0) : (null, 10, 0);
    }

    private static BitmapSource? BuildBlurredForMonitor(string path, int style, int tile, int monitorW, int monitorH)
    {
        try
        {
            // 按 1/6 尺度生成（例如 2880x1800 -> 480x300，仅 ~576KB 字节数组）
            int targetW = Math.Clamp(monitorW / 6, 200, 640);
            int targetH = Math.Clamp(monitorH / 6, 120, 400);

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = Math.Max(targetW, targetH);
            bmp.EndInit();
            bmp.Freeze();

            BitmapSource source = bmp;
            if (source.Format != PixelFormats.Bgr32 && source.Format != PixelFormats.Pbgra32)
            {
                var converted = new FormatConvertedBitmap(source, PixelFormats.Bgr32, null, 0);
                converted.Freeze();
                source = converted;
            }

            // 用 WPF DrawingVisual 按壁纸 Fill/Fit/Stretch 方式裁切绘制到 targetW x targetH
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(System.Windows.Media.Brushes.Black, null, new Rect(0, 0, targetW, targetH));
                var drawRect = ComputeWallpaperRect(source.PixelWidth, source.PixelHeight, targetW, targetH, style, tile);
                dc.DrawImage(source, drawRect);
            }

            var rtb = new RenderTargetBitmap(targetW, targetH, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);

            int stride = targetW * 4;
            var pixels = new byte[stride * targetH];
            rtb.CopyPixels(pixels, stride, 0);

            ApplyFastBoxBlur(pixels, targetW, targetH, radius: 5, passes: 3);

            var result = BitmapSource.Create(targetW, targetH, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
            result.Freeze();
            return result;
        }
        catch
        {
            return null;
        }
    }

    private static Rect ComputeWallpaperRect(double srcW, double srcH, double dstW, double dstH, int style, int tile)
    {
        if (style == 2) // Stretch
            return new Rect(0, 0, dstW, dstH);

        if (style == 6) // Fit
        {
            double s = Math.Min(dstW / srcW, dstH / srcH);
            double w = srcW * s, h = srcH * s;
            return new Rect((dstW - w) / 2, (dstH - h) / 2, w, h);
        }

        if (style == 0 && tile == 0) // Center
        {
            return new Rect((dstW - srcW) / 2, (dstH - srcH) / 2, srcW, srcH);
        }

        // Fill (10)
        double fillScale = Math.Max(dstW / srcW, dstH / srcH);
        double drawW = srcW * fillScale;
        double drawH = srcH * fillScale;
        return new Rect((dstW - drawW) / 2, (dstH - drawH) / 2, drawW, drawH);
    }

    private static void ApplyFastBoxBlur(byte[] pixels, int w, int h, int radius, int passes)
    {
        var temp = new byte[pixels.Length];
        int stride = w * 4;
        int window = radius * 2 + 1;

        for (int p = 0; p < passes; p++)
        {
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                int sumB = 0, sumG = 0, sumR = 0;

                for (int kx = -radius; kx <= radius; kx++)
                {
                    int cx = Math.Clamp(kx, 0, w - 1) * 4 + row;
                    sumB += pixels[cx];
                    sumG += pixels[cx + 1];
                    sumR += pixels[cx + 2];
                }

                for (int x = 0; x < w; x++)
                {
                    int dst = row + x * 4;
                    temp[dst] = (byte)(sumB / window);
                    temp[dst + 1] = (byte)(sumG / window);
                    temp[dst + 2] = (byte)(sumR / window);
                    temp[dst + 3] = 255;

                    int addX = Math.Min(x + radius + 1, w - 1) * 4 + row;
                    int remX = Math.Max(x - radius, 0) * 4 + row;
                    sumB += pixels[addX] - pixels[remX];
                    sumG += pixels[addX + 1] - pixels[remX + 1];
                    sumR += pixels[addX + 2] - pixels[remX + 2];
                }
            }

            for (int x = 0; x < w; x++)
            {
                int col = x * 4;
                int sumB = 0, sumG = 0, sumR = 0;

                for (int ky = -radius; ky <= radius; ky++)
                {
                    int cy = Math.Clamp(ky, 0, h - 1) * stride + col;
                    sumB += temp[cy];
                    sumG += temp[cy + 1];
                    sumR += temp[cy + 2];
                }

                for (int y = 0; y < h; y++)
                {
                    int dst = y * stride + col;
                    pixels[dst] = (byte)(sumB / window);
                    pixels[dst + 1] = (byte)(sumG / window);
                    pixels[dst + 2] = (byte)(sumR / window);
                    pixels[dst + 3] = 255;

                    int addY = Math.Min(y + radius + 1, h - 1) * stride + col;
                    int remY = Math.Max(y - radius, 0) * stride + col;
                    sumB += temp[addY] - temp[remY];
                    sumG += temp[addY + 1] - temp[remY + 1];
                    sumR += temp[addY + 2] - temp[remY + 2];
                }
            }
        }
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        Invalidate();
    }
}
