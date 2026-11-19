using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Models;

namespace DeskBox.Services;

public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _configPath;
    private readonly string _backupPath;
    private readonly object _saveLock = new();
    private CancellationTokenSource? _saveDebounceCts;

    public ConfigService(string? customDirectory = null)
    {
        var dir = customDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DeskBox");
        Directory.CreateDirectory(dir);
        _configPath = Path.Combine(dir, "config.json");
        _backupPath = Path.Combine(dir, "config.bak.json");
    }

    public string ConfigDirectory => Path.GetDirectoryName(_configPath)!;

    public AppConfig LoadOrCreateDefault()
    {
        var loaded = TryRead(_configPath) ?? TryRead(_backupPath);
        if (loaded != null && loaded.Boxes.Count > 0)
        {
            if (loaded.ConfigVersion < 1)
            {
                loaded.General.EnableHardwareAcceleration = false;
                loaded.ConfigVersion = 1;
                SaveImmediate(loaded);
            }
            return loaded;
        }

        var defaultConfig = CreateDefaultStarterConfig();
        SaveImmediate(defaultConfig);
        return defaultConfig;
    }

    private static AppConfig? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public void SaveDebounced(AppConfig config, int delayMs = 400)
    {
        CancellationTokenSource cts;
        lock (_saveLock)
        {
            _saveDebounceCts?.Cancel();
            _saveDebounceCts?.Dispose();
            cts = new CancellationTokenSource();
            _saveDebounceCts = cts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs, cts.Token);
                if (!cts.IsCancellationRequested)
                {
                    WriteConfigLocked(config);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    public void SaveImmediate(AppConfig config)
    {
        lock (_saveLock)
        {
            _saveDebounceCts?.Cancel();
            _saveDebounceCts?.Dispose();
            _saveDebounceCts = null;
            WriteConfigLocked(config);
        }
    }

    private void WriteConfigLocked(AppConfig config)
    {
        lock (_saveLock)
        {
            try
            {
                var json = JsonSerializer.Serialize(config, JsonOptions);
                var temp = _configPath + ".tmp";
                File.WriteAllText(temp, json);
                if (File.Exists(_configPath))
                {
                    File.Copy(_configPath, _backupPath, overwrite: true);
                }
                File.Move(temp, _configPath, overwrite: true);
            }
            catch
            {
                // Ignore transient IO lock
            }
        }
    }

    /// <summary>
    /// 创建干净无广告的默认起步布局（含示例常用入口，不移动任何物理文件）。
    /// </summary>
    public static AppConfig CreateDefaultStarterConfig()
    {
        var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var commonItems = new List<BoxItemEntry>
        {
            new() { Path = "shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", CustomName = "此电脑" },
            new() { Path = "shell:::{645FF040-5081-101B-9F08-00AA002F954E}", CustomName = "回收站" },
            new() { Path = "shell:::{26EE0668-A00A-44D7-9371-BEB064C986EE}", CustomName = "控制面板" }
        };

        var appItems = new List<BoxItemEntry>();
        var docItems = new List<BoxItemEntry>();

        if (Directory.Exists(desktopDir))
        {
            foreach (var file in Directory.EnumerateFileSystemEntries(desktopDir).Take(60))
            {
                var name = Path.GetFileName(file);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("thumbs.db", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var attrs = File.GetAttributes(file);
                    if ((attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                        continue;
                }
                catch { }

                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is ".lnk" or ".url" or ".exe")
                {
                    if (appItems.Count < 14)
                        appItems.Add(new BoxItemEntry { Path = file });
                }
                else
                {
                    if (docItems.Count < 14)
                        docItems.Add(new BoxItemEntry { Path = file });
                }
            }
        }

        return new AppConfig
        {
            Boxes =
            [
                new BoxConfig
                {
                    Id = "box_common",
                    Title = "常用工具",
                    ViewMode = BoxViewMode.List,
                    Items = commonItems.Concat(appItems.Take(8)).ToList(),
                    FallbackPlacement = new BoxPlacement
                    {
                        WasPrimaryMonitor = true,
                        RelX = 0.015,
                        RelY = 0.015,
                        RelWidth = 0.185,
                        RelExpandedHeight = 0.48
                    }
                },
                new BoxConfig
                {
                    Id = "box_docs",
                    Title = "桌面文档",
                    ViewMode = BoxViewMode.List,
                    Items = docItems,
                    FallbackPlacement = new BoxPlacement
                    {
                        WasPrimaryMonitor = true,
                        RelX = 0.215,
                        RelY = 0.015,
                        RelWidth = 0.185,
                        RelExpandedHeight = 0.48
                    }
                }
            ]
        };
    }
}
