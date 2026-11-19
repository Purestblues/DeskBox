using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using Microsoft.Win32;

namespace DeskBox.Setup.Services;

public sealed record InstallOptions(
    string InstallDirectory,
    bool CreateDesktopShortcut,
    bool CreateStartMenuShortcut,
    bool AutoStart,
    bool LaunchAfterInstall);

public sealed class InstallerEngine
{
    public const string AppVersion = "1.1.0";
    private const string UninstallRegPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DeskBox";
    private const string RunRegPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ManifestFileName = "install_manifest.txt";

    public static string DefaultInstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        "DeskBox");

    public static string StartMenuFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        "Programs",
        "DeskBox");

    public static string DesktopShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        "DeskBox.lnk");

    public static string AppDataConfigDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DeskBox");

    public static bool HasEmbeddedPayload()
    {
        var asm = Assembly.GetExecutingAssembly();
        return asm.GetManifestResourceNames().Any(n => n.EndsWith("payload.zip", StringComparison.OrdinalIgnoreCase));
    }

    public static async Task InstallAsync(InstallOptions options, IProgress<(int percent, string message)> progress)
    {
        await Task.Run(async () =>
        {
            progress.Report((5, "正在检查并停止运行中的 DeskBox..."));
            await StopRunningDeskBoxAsync();

            var installDir = Path.GetFullPath(options.InstallDirectory.Trim());
            Directory.CreateDirectory(installDir);

            progress.Report((20, "正在解压程序文件..."));
            var asm = Assembly.GetExecutingAssembly();
            var resName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("payload.zip", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("安装包未包含内嵌程序资源（payload.zip）。请使用 build-installer.ps1 构建安装包。");

            var extractedRelativeFiles = new List<string>();
            long totalBytes = 0;

            using (var resStream = asm.GetManifestResourceStream(resName)!)
            using (var zip = new ZipArchive(resStream, ZipArchiveMode.Read))
            {
                int count = Math.Max(1, zip.Entries.Count);
                int idx = 0;
                foreach (var entry in zip.Entries)
                {
                    idx++;
                    if (string.IsNullOrEmpty(entry.Name))
                        continue;

                    var fullDest = Path.GetFullPath(Path.Combine(installDir, entry.FullName));
                    if (!fullDest.StartsWith(installDir, StringComparison.OrdinalIgnoreCase))
                        continue;

                    Directory.CreateDirectory(Path.GetDirectoryName(fullDest)!);
                    entry.ExtractToFile(fullDest, overwrite: true);
                    extractedRelativeFiles.Add(entry.FullName);
                    totalBytes += entry.Length;

                    var pct = 20 + (int)(idx * 55.0 / count);
                    progress.Report((pct, $"正在安装: {entry.Name}"));
                }
            }

            progress.Report((80, "正在生成卸载程序与清单..."));
            var uninstallerPath = Path.Combine(installDir, "Uninstall.exe");
            var selfExe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(selfExe) && File.Exists(selfExe) &&
                !string.Equals(Path.GetFullPath(selfExe), Path.GetFullPath(uninstallerPath), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(selfExe, uninstallerPath, overwrite: true);
                extractedRelativeFiles.Add("Uninstall.exe");
            }

            extractedRelativeFiles.Add(ManifestFileName);
            File.WriteAllLines(Path.Combine(installDir, ManifestFileName), extractedRelativeFiles);

            var mainExePath = Path.Combine(installDir, "DeskBox.exe");

            progress.Report((88, "正在创建快捷方式与系统登记..."));
            if (options.CreateDesktopShortcut)
            {
                ShortcutHelper.CreateShortcut(
                    DesktopShortcutPath,
                    mainExePath,
                    installDir,
                    "DeskBox 轻量桌面格子");
            }

            if (options.CreateStartMenuShortcut)
            {
                Directory.CreateDirectory(StartMenuFolder);
                ShortcutHelper.CreateShortcut(
                    Path.Combine(StartMenuFolder, "DeskBox.lnk"),
                    mainExePath,
                    installDir,
                    "DeskBox 轻量桌面格子");
                ShortcutHelper.CreateShortcut(
                    Path.Combine(StartMenuFolder, "卸载 DeskBox.lnk"),
                    uninstallerPath,
                    installDir,
                    "卸载 DeskBox",
                    "--uninstall");
            }

            UpdateAutoStartRegistry(options.AutoStart, mainExePath);
            RegisterUninstallEntry(installDir, mainExePath, uninstallerPath, totalBytes);

            progress.Report((100, "安装完成！"));
        });
    }

    public static async Task UninstallAsync(string installDir, bool removeUserConfig, IProgress<(int percent, string message)> progress)
    {
        await Task.Run(async () =>
        {
            progress.Report((10, "正在关闭运行中的 DeskBox..."));
            await StopRunningDeskBoxAsync();

            progress.Report((25, "正在将收纳盒中的文件安全放回桌面..."));
            RestoreAllStoredFilesToDesktop();

            progress.Report((38, "正在清理开机自启与快捷方式..."));
            UpdateAutoStartRegistry(false, string.Empty);
            try
            {
                using var shellKey = Registry.CurrentUser.OpenSubKey(@"Software\Classes\DesktopBackground\Shell", writable: true);
                shellKey?.DeleteSubKeyTree("DeskBox_1_NewBox", throwOnMissingSubKey: false);
                shellKey?.DeleteSubKeyTree("DeskBox_2_ToggleTidy", throwOnMissingSubKey: false);
                shellKey?.DeleteSubKeyTree("DeskBox_3_Settings", throwOnMissingSubKey: false);
            }
            catch { }

            if (File.Exists(DesktopShortcutPath))
                TryDeleteFile(DesktopShortcutPath);

            if (Directory.Exists(StartMenuFolder))
                TryDeleteDirectory(StartMenuFolder, recursive: true);

            if (removeUserConfig && Directory.Exists(AppDataConfigDir))
            {
                progress.Report((50, "正在清理个人配置与布局快照..."));
                TryDeleteDirectory(AppDataConfigDir, recursive: true);
            }

            progress.Report((70, "正在移除已安装程序文件..."));
            var fullDir = Path.GetFullPath(installDir);
            var manifestPath = Path.Combine(fullDir, ManifestFileName);
            var selfExe = Path.GetFullPath(Environment.ProcessPath ?? string.Empty);

            if (File.Exists(manifestPath))
            {
                var files = File.ReadAllLines(manifestPath);
                foreach (var rel in files)
                {
                    if (string.IsNullOrWhiteSpace(rel))
                        continue;
                    var target = Path.GetFullPath(Path.Combine(fullDir, rel));
                    if (string.Equals(target, selfExe, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (File.Exists(target))
                        TryDeleteFile(target);
                }
            }

            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(UninstallRegPath, throwOnMissingSubKey: false);
            }
            catch { }

            progress.Report((95, "正在收尾清理..."));
            ScheduleSelfCleanup(selfExe, fullDir);
            progress.Report((100, "DeskBox 已从你的电脑中卸载。"));
        });
    }

    public static string? GetRegisteredInstallLocation()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallRegPath, writable: false);
            return key?.GetValue("InstallLocation")?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static void RestoreAllStoredFilesToDesktop()
    {
        try
        {
            // 若 DeskBox 曾隐藏了回收站等原生桌面系统图标，卸载时一并恢复
            var configPath = Path.Combine(AppDataConfigDir, "config.json");
            if (File.Exists(configPath))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(configPath));
                    if (doc.RootElement.TryGetProperty("HiddenSystemDesktopGuids", out var guidsEl) &&
                        guidsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel", writable: true);
                        foreach (var g in guidsEl.EnumerateArray())
                        {
                            var guidStr = g.GetString();
                            if (!string.IsNullOrWhiteSpace(guidStr))
                                key.SetValue(guidStr, 0, RegistryValueKind.DWord);
                        }
                    }

                    if (doc.RootElement.TryGetProperty("HiddenDesktopPaths", out var pathsEl) &&
                        pathsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var p in pathsEl.EnumerateArray())
                        {
                            var pathStr = p.GetString();
                            if (string.IsNullOrWhiteSpace(pathStr) || (!File.Exists(pathStr) && !Directory.Exists(pathStr)))
                                continue;

                            try
                            {
                                var attrs = File.GetAttributes(pathStr);
                                if ((attrs & FileAttributes.Hidden) != 0)
                                    File.SetAttributes(pathStr, attrs & ~FileAttributes.Hidden);
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }

            var storageRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeskBox",
                "Storage");
            if (!Directory.Exists(storageRoot))
                return;

            var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            Directory.CreateDirectory(desktopDir);

            foreach (var boxDir in Directory.EnumerateDirectories(storageRoot))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(boxDir))
                {
                    try
                    {
                        bool isDir = Directory.Exists(entry);
                        var name = Path.GetFileName(entry);
                        var dest = Path.Combine(desktopDir, name);
                        if (File.Exists(dest) || Directory.Exists(dest))
                        {
                            var stem = isDir ? name : Path.GetFileNameWithoutExtension(name);
                            var ext = isDir ? string.Empty : Path.GetExtension(name);
                            for (int i = 2; i < 1000; i++)
                            {
                                var candidate = Path.Combine(desktopDir, $"{stem} ({i}){ext}");
                                if (!File.Exists(candidate) && !Directory.Exists(candidate))
                                {
                                    dest = candidate;
                                    break;
                                }
                            }
                        }

                        if (isDir) Directory.Move(entry, dest);
                        else File.Move(entry, dest);
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    private static async Task StopRunningDeskBoxAsync()
    {
        var procs = Process.GetProcessesByName("DeskBox");
        foreach (var p in procs)
        {
            try
            {
                p.Kill(entireProcessTree: true);
                await p.WaitForExitAsync();
            }
            catch { }
            finally
            {
                p.Dispose();
            }
        }
        await Task.Delay(200);
    }

    private static void UpdateAutoStartRegistry(bool enabled, string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegPath, writable: true);
            if (key == null) return;
            if (enabled && !string.IsNullOrWhiteSpace(exePath))
                key.SetValue("DeskBox", $"\"{exePath}\"");
            else
                key.DeleteValue("DeskBox", throwOnMissingValue: false);
        }
        catch { }
    }

    private static void RegisterUninstallEntry(string installDir, string mainExe, string uninstallerExe, long totalBytes)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallRegPath, writable: true);
        key.SetValue("DisplayName", "DeskBox 桌面格子");
        key.SetValue("DisplayVersion", AppVersion);
        key.SetValue("Publisher", "DeskBox");
        key.SetValue("InstallLocation", installDir);
        key.SetValue("DisplayIcon", $"\"{mainExe}\",0");
        key.SetValue("UninstallString", $"\"{uninstallerExe}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{uninstallerExe}\" --uninstall --quiet");
        key.SetValue("EstimatedSize", (int)Math.Max(512, totalBytes / 1024), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private static void ScheduleSelfCleanup(string selfExe, string installDir)
    {
        if (string.IsNullOrWhiteSpace(selfExe) || !selfExe.StartsWith(installDir, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            var cmd = $"/c timeout /t 2 /nobreak >nul & del /f /q \"{selfExe}\" & rmdir \"{installDir}\"";
            Process.Start(new ProcessStartInfo("cmd.exe", cmd)
            {
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = false
            });
        }
        catch { }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path, bool recursive)
    {
        try { Directory.Delete(path, recursive); } catch { }
    }
}
