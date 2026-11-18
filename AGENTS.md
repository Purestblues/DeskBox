# DeskBox 项目协作与维护规范

## 1. 项目定位
轻量级、零推广、多显示器友好的 Windows 桌面格子收纳工具（1:1 复刻腾讯桌面整理核心收纳体验，剔除云搜索/AI/壁纸推销等冗余模块）。

## 2. 技术栈与架构约束
- **技术栈**：C# (.NET 10) + WPF + Win32 P/Invoke（单进程，目标常驻内存 < 40MB）。
- **目录结构**：
  - `DeskBox.slnx` / `src/DeskBox/`：主程序工程
    - `Assets/`：应用图标（`deskbox.ico`）
    - `Models/`：格子数据模型、多显示器拓扑快照模型（`LayoutSnapshot`、`MonitorKey`、`BoxItem`）
    - `Services/`：
      - `DesktopHostService.cs`：桌面底层嵌入与原生桌面图标可见性管理
      - `MonitorTopologyService.cs`：多显示器硬件识别 + 分辨率 + DPI 监听与布局自动切换/还原
      - `WallpaperBlurService.cs`：低内存壁纸下采样高斯模糊缓存
      - `TrayIconService.cs`：纯 Win32 托盘图标管理
      - `ShellService.cs`：高分屏图标提取、原生 Shell 右键菜单、文件打开与拖放
      - `ConfigService.cs`：本地 JSON 配置持久化（`%APPDATA%\DeskBox\config.json`）
    - `Views/`：
      - `DeskBoxWindow.xaml`：半透明磨砂格子窗口（折叠/展开、悬停展开、锁定、列表/网格视图、排序、调色/透明度）
      - `SettingsWindow.xaml`：设置中心（基础设置、桌面整理、多屏快照、关于与性能）
    - `Interop/`：Win32 API 声明
  - `src/DeskBox.Setup/`：单文件安装与标准卸载向导（支持写入 Windows 卸载项、快捷方式、静默卸载）
  - `build-installer.ps1`：一键测试与打包安装程序脚本（输出至 `dist/`）
  - `tests/DeskBox.Tests/`：核心逻辑单元测试（多显示器拓扑匹配、布局防越界、配置序列化）

## 3. 工程纪律
- **核心收纳原则（非破坏性）**：绝不把用户桌面文件/文件夹搬离原桌面目录，保证资源管理器与第三方应用工作目录 100% 不受破坏；通过 `DesktopIconFilterService`（登记 `HiddenDesktopPaths` 受控隐藏并在移出/解散/退出/卸载时无损恢复）及可选“桌面未整理”动态格子解决重复展示问题。
- **隐私与安全**：禁止上传任何本地文件名或配置；不随意移动/删除用户物理文件（仅管理桌面索引与视觉视图）。
- **死约束**：未经用户明确许可，禁止删除任何文件。
- **验证规范**：每次 UI 或交互修改后，必须构建并通过 `/gui-verify`（`gui_controller`）实机验证窗口渲染、控件树、鼠标交互与内存占用。
